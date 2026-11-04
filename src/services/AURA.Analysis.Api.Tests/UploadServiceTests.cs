using System.Text.Json;
using AURA.Analysis.Api.Upload;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public class UploadServiceTests
{
    private static readonly Guid Requester = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Patient = Guid.Parse("9b2e5c10-1111-4a2b-8c3d-000000000001");
    private static readonly Guid Key = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private sealed class Rig
    {
        public readonly FakeAccess Access = new();
        public readonly FakeBilling Billing = new();
        public readonly FakeStorage Storage = new();
        public readonly FakeRepo Repo = new();
        public readonly UploadService Service;
        public Rig(UploadOptions? o = null) =>
            Service = new UploadService(o ?? new UploadOptions(), Access, Billing, Storage, Repo, TimeProvider.System, NullLogger<UploadService>.Instance);
    }

    private static MemFile Img(string name, int seed = 0) => new(name, TestImages.Jpeg(1000, 800, seed: seed), "image/jpeg");

    private static UploadCommand Cmd(IReadOnlyList<IUploadedFile> files, Guid? key = null, string? patient = null,
        string? modality = null, string? eye = null, string? correlation = null) =>
        new(Requester, null, key ?? Key, correlation, patient ?? Patient.ToString(), modality, eye, files);

    private static async Task<UploadFailure> Fails(Func<Task> act) => await Assert.ThrowsAsync<UploadFailure>(act);

    [Fact]
    public async Task Hai_anh_hop_le_tra_202_QUEUED_co_job_outbox_reserve_va_file_private()
    {
        var rig = new Rig();
        var r = await rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg", 1), Img("b.jpg", 2) }), default);

        Assert.False(r.Replayed);
        Assert.Equal(2, r.Response.Items.Count);
        Assert.True(r.Response.Items.All(i => i.ProcessingStatus == "QUEUED" && i.Error is null));
        Assert.Equal($"/api/v1/batches/{r.Response.BatchId:D}", r.Response.StatusUrl);

        var plan = Assert.Single(rig.Repo.Commits);
        Assert.Equal(2, plan.Images.Count);
        Assert.True(plan.Images.All(i => i.Job is not null && i.ProcessingStatus == "QUEUED"));
        Assert.Equal(4, plan.Outbox.Count);                       // requested + job.run cho mỗi ảnh
        Assert.Equal(2, rig.Billing.ReserveCalls);
        Assert.Equal(2, rig.Storage.Files.Count);
        Assert.Equal(FakeAccess.Tenant, plan.TenantId);
        Assert.Equal("1", plan.FingerprintVersion);
    }

    [Fact]
    public async Task Object_key_chi_chua_uuid_khong_co_ten_file()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(Cmd(new[] { Img("nguyen-van-a.jpg") }), default);
        var key = rig.Repo.Commits[0].Images[0].ObjectKey;
        Assert.True(key.Split('/').All(p => Guid.TryParse(p, out _)));
        Assert.True(!key.Contains("nguyen"));
    }

       [Fact]
    public async Task Payload_outbox_dung_dinh_dang_event_0_7_va_job_message_0_6()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }, correlation: "corr-123"), default);
        var plan = rig.Repo.Commits[0];

        var requested = plan.Outbox.Single(o => o.EventType == "analysis.requested");
        Assert.Equal("0.8", requested.SchemaVersion);                  // ADR005 domain event; legacy0.7 remains readable
        using var ev = JsonDocument.Parse(requested.PayloadJson);
        Assert.Equal("0.8", ev.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("corr-123", ev.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(Patient.ToString(), ev.RootElement.GetProperty("data").GetProperty("patientId").GetString());

        var jobItem = plan.Outbox.Single(o => o.EventType == "analysis.job.run");
        Assert.Equal("0.6", jobItem.SchemaVersion);                    // cột schema_version của job
        using var job = JsonDocument.Parse(jobItem.PayloadJson);
        Assert.Equal("0.6", job.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, job.RootElement.GetProperty("attempt").GetInt32());
        Assert.Equal(plan.Images[0].FileId.ToString(), job.RootElement.GetProperty("imageFileId").GetString());
        Assert.False(job.RootElement.TryGetProperty("object_key", out _) || job.RootElement.TryGetProperty("objectKey", out _));
    }

    [Fact]
    public async Task Gui_lai_cung_key_cung_noi_dung_tra_phan_hoi_cu_khong_reserve_them()
    {
        var rig = new Rig();
        var files = new[] { Img("a.jpg", 1) };
        var first = await rig.Service.CreateAsync(Cmd(files), default);
        var second = await rig.Service.CreateAsync(Cmd(files), default);

        Assert.True(second.Replayed);
        Assert.Equal(first.Response.BatchId, second.Response.BatchId);
        Assert.Equal(1, rig.Billing.ReserveCalls);
        Assert.Equal(1, rig.Repo.Commits.Count);
        Assert.Equal(1, rig.Storage.PutCalls);
    }

    [Fact]
    public async Task Cung_key_noi_dung_khac_bi_409_ERR_IDEMPOTENCY_KEY_REUSED()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg", 1) }), default);
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg", 2) }), default));
        Assert.Equal(409, f.Status);
        Assert.Equal("ERR_IDEMPOTENCY_KEY_REUSED", f.Code);
        Assert.Equal(1, rig.Billing.ReserveCalls);
    }

    [Fact]
    public async Task Hai_request_dong_thoi_cung_key_chi_mot_batch_va_mot_lan_reserve()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        rig.Storage.BeforePut = async () => { entered.TrySetResult(); await gate.Task; };

        var files = new[] { Img("a.jpg", 1) };
        var first = rig.Service.CreateAsync(Cmd(files), default);
        await entered.Task;                                       // request 1 đang giữ khóa ở bước lưu file
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(files), default));
        Assert.Equal(409, f.Status);
        Assert.Equal("ERR_REQUEST_IN_PROGRESS", f.Code);
        Assert.True(f.Retryable);

        gate.SetResult();
        await first;
        Assert.Equal(1, rig.Repo.Commits.Count);
        Assert.Equal(1, rig.Billing.ReserveCalls);
    }

    [Fact]
    public async Task Khong_co_quyen_tren_benh_nhan_tra_404_khong_reserve_khong_luu()
    {
        var rig = new Rig { Access = { Allowed = false } };
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }), default));
        Assert.Equal(404, f.Status);
        Assert.Equal("ERR_NOT_FOUND", f.Code);
        Assert.Equal(0, rig.Billing.ReserveCalls);
        Assert.Equal(0, rig.Storage.PutCalls);
    }

    [Fact]
    public async Task OCT_bi_400_truoc_khi_hoi_Profile_va_Billing()
    {
        var rig = new Rig();
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }, modality: "OCT"), default));
        Assert.Equal(400, f.Status);
        Assert.Equal("ERR_VALIDATION", f.Code);
        Assert.Equal(0, rig.Access.Calls);
        Assert.Equal(0, rig.Billing.ReserveCalls);
    }

    [Fact]
    public async Task Thieu_patientId_khong_co_anh_eyeSide_sai_deu_400()
    {
        var rig = new Rig();
        var f1 = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }, patient: ""), default));
        Assert.Equal("patientId", f1.Details[0].Field);
        var f2 = await Fails(() => rig.Service.CreateAsync(Cmd(Array.Empty<IUploadedFile>()), default));
        Assert.Equal("images", f2.Details[0].Field);
        var f3 = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }, eye: "middle"), default));
        Assert.Equal("eyeSide", f3.Details[0].Field);
        Assert.Equal(0, rig.Billing.ReserveCalls);
    }

    [Fact]
    public async Task Vuot_so_anh_toi_da_bi_400()
    {
        var rig = new Rig(new UploadOptions { MaxImagesPerRequest = 2 });
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a", 1), Img("b", 2), Img("c", 3) }), default));
        Assert.Equal("too_many", f.Details[0].Issue);
    }

    [Fact]
    public async Task Mot_anh_loi_thi_tu_choi_ca_request_truoc_reserve_va_luu()
    {
        var rig = new Rig();
        var gif = new MemFile("x.gif", System.Text.Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[30]).ToArray());
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new IUploadedFile[] { Img("a.jpg", 1), gif, Img("c.jpg", 3) }), default));
        Assert.Equal(415, f.Status);
        Assert.Equal("images[1]", f.Details[0].Field);
        Assert.Equal(0, rig.Billing.ReserveCalls);
        Assert.Equal(0, rig.Storage.PutCalls);
        Assert.Equal(0, rig.Repo.Commits.Count);
    }

    [Fact]
    public async Task Anh_trung_da_co_la_DUPLICATE_va_khong_reserve_cho_anh_do()
    {
        var rig = new Rig();
        var dup = Img("dup.jpg", 7);
        var sha = (await ImageInspector.InspectAsync(dup.OpenReadStream(), null, new UploadOptions())).Image!.Sha256;
        var existingId = Guid.NewGuid();
        rig.Repo.Existing[sha] = existingId;

        var r = await rig.Service.CreateAsync(Cmd(new[] { dup, Img("new.jpg", 8) }), default);
        Assert.Equal("DUPLICATE", r.Response.Items[0].ProcessingStatus);
        Assert.Equal("ERR_DUPLICATE_REQUEST", r.Response.Items[0].Error!.Code);
        Assert.Equal("QUEUED", r.Response.Items[1].ProcessingStatus);
        Assert.Equal(1, rig.Billing.ReserveCalls);

        var plan = rig.Repo.Commits[0];
        Assert.Equal(existingId, plan.Images[0].ExistingAnalysisId);
        Assert.True(plan.Images[0].Job is null);
        Assert.Equal(2, plan.Outbox.Count);                        // chỉ ảnh mới có outbox
    }

    [Fact]
    public async Task Hai_anh_giong_nhau_trong_cung_request_anh_thu_hai_la_DUPLICATE_cua_anh_thu_nhat()
    {
        var rig = new Rig();
        var r = await rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg", 5), Img("b.jpg", 5) }), default);
        Assert.Equal("QUEUED", r.Response.Items[0].ProcessingStatus);
        Assert.Equal("DUPLICATE", r.Response.Items[1].ProcessingStatus);
        Assert.Equal(r.Response.Items[0].AnalysisId, rig.Repo.Commits[0].Images[1].ExistingAnalysisId);
        Assert.Equal(1, rig.Billing.ReserveCalls);
    }

    [Fact]
    public async Task Thieu_credit_o_anh_thu_hai_tra_402_hoan_anh_thu_nhat_khong_luu_khong_commit()
    {
        var rig = new Rig { Billing = { InsufficientAtCall = 2 } };
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a", 1), Img("b", 2), Img("c", 3) }), default));
        Assert.Equal(402, f.Status);
        Assert.Equal("ERR_INSUFFICIENT_CREDIT", f.Code);
        Assert.Equal(1, rig.Billing.Released.Count);
        Assert.Equal(0, rig.Storage.PutCalls);
        Assert.Equal(0, rig.Repo.Commits.Count);
    }

    [Fact]
    public async Task Loi_luu_file_giua_chung_hoan_toan_bo_credit_xoa_file_da_luu_va_tra_503_retryable()
    {
        var rig = new Rig { Storage = { FailOnPut = 2 } };
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a", 1), Img("b", 2) }), default));
        Assert.Equal(503, f.Status);
        Assert.True(f.Retryable);
        Assert.Equal(2, rig.Billing.Released.Count);
        Assert.Equal(1, rig.Storage.Deleted.Count);
        Assert.Equal(0, rig.Storage.Files.Count);
        Assert.Equal(0, rig.Repo.Commits.Count);
    }

    [Fact]
    public async Task Loi_commit_giu_reservation_va_file_cho_reconciliation_retry_cung_key_dung_lai_ID_cu()
    {
        var rig = new Rig { Repo = { FailCommit = true } };
        var files = new[] { Img("a", 1), Img("b", 2) };
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(files), default));
        Assert.Equal(503, f.Status);
        Assert.Equal(0, rig.Billing.Released.Count);               // không biết đã commit chưa: không hoàn
        Assert.Equal(0, rig.Storage.Deleted.Count);
        Assert.Equal(2, rig.Storage.Files.Count);

        rig.Repo.FailCommit = false;
        var r = await rig.Service.CreateAsync(Cmd(files), default);
        Assert.False(r.Replayed);
        Assert.Equal(2, rig.Billing.ByKey.Count);                  // key reserve ổn định: Billing chỉ có 2 reservation
        Assert.Equal(2, rig.Storage.Files.Count);                  // ghi đè cùng key, không có file mồ côi
    }

    [Fact]
    public async Task Billing_hoac_Profile_hoac_DB_hong_tra_503_ERR_NETWORK_va_khong_luu_gi()
    {
        var rig = new Rig { Billing = { Down = true } };
        var f = await Fails(() => rig.Service.CreateAsync(Cmd(new[] { Img("a") }), default));
        Assert.Equal("ERR_NETWORK", f.Code);
        Assert.Equal(0, rig.Storage.PutCalls);

        var rig2 = new Rig { Access = { Down = true } };
        var f2 = await Fails(() => rig2.Service.CreateAsync(Cmd(new[] { Img("a") }), default));
        Assert.Equal(503, f2.Status);

        var rig3 = new Rig { Repo = { DbDown = true } };
        var f3 = await Fails(() => rig3.Service.CreateAsync(Cmd(new[] { Img("a") }), default));
        Assert.Equal(503, f3.Status);
        Assert.Equal(0, rig3.Billing.ReserveCalls);
    }

    [Fact]
    public async Task Billing_hong_giua_chung_hoan_cac_reservation_da_giu()
    {
        var rig = new Rig();
        // lần reserve thứ 2 ném lỗi mạng
        var billing = new FlakyBilling(rig.Billing);
        var svc = new UploadService(new UploadOptions(), rig.Access, billing, rig.Storage, rig.Repo, TimeProvider.System, NullLogger<UploadService>.Instance);
        var f = await Fails(() => svc.CreateAsync(Cmd(new[] { Img("a", 1), Img("b", 2) }), default));
        Assert.Equal(503, f.Status);
        Assert.Equal(1, rig.Billing.Released.Count);
        Assert.Equal(0, rig.Storage.PutCalls);
    }

    private sealed class FlakyBilling : ICreditReservations
    {
        private readonly FakeBilling _inner; private int _n;
        public FlakyBilling(FakeBilling inner) => _inner = inner;
        public Task<ReservationOutcome> ReserveAsync(ReserveCredit r, CancellationToken ct) =>
            ++_n == 2 ? throw new HttpRequestException("timeout") : _inner.ReserveAsync(r, ct);
        public Task ReleaseAsync(ReleaseCredit r, CancellationToken ct) => _inner.ReleaseAsync(r, ct);
    }

    [Fact]
    public async Task Correlation_id_sai_dinh_dang_duoc_thay_bang_uuid_moi_con_hop_le_thi_giu()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(Cmd(new[] { Img("a", 1) }, correlation: "bad id!"), default);
        Assert.True(Guid.TryParse(rig.Repo.Commits[0].CorrelationId, out _));

        var rig2 = new Rig();
        await rig2.Service.CreateAsync(Cmd(new[] { Img("a", 1) }, correlation: "req.abc-1_2"), default);
        Assert.Equal("req.abc-1_2", rig2.Repo.Commits[0].CorrelationId);
    }

    [Fact]
    public async Task Tenant_va_owner_lay_tu_Profile_khong_tu_client()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(Cmd(new[] { Img("a", 1) }), default);
        var plan = rig.Repo.Commits[0];
        Assert.Equal(FakeAccess.Tenant, plan.TenantId);
        Assert.Equal(Requester, plan.RequesterId);
        Assert.Equal(Requester, plan.OwnerId);
    }

    [Fact]
    public async Task Reserve_receives_authoritative_account_patient_and_correlation()
    {
        var rig = new Rig();
        await rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }, correlation: "corr-context"), default);
        var request = Assert.Single(rig.Billing.ReserveRequests);
        Assert.Equal(Requester, request.ActorAccountId);
        Assert.Equal(Patient, request.PatientId);
        Assert.Equal(FakeAccess.Tenant, request.TenantId);
        Assert.Equal(1, request.Generation);
        Assert.Equal("corr-context", request.CorrelationId);
    }

    [Fact]
    public async Task Storage_failure_uses_storage_abort_release_reason()
    {
        var rig = new Rig { Storage = { FailOnPut = 1 } };
        await Assert.ThrowsAsync<UploadFailure>(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }), default));
        Assert.Equal("STORAGE_ABORT", Assert.Single(rig.Billing.ReleaseRequests).Reason);
    }

    [Fact]
    public async Task Caller_disconnect_does_not_pre_cancel_compensation()
    {
        using var cts = new CancellationTokenSource();
        var rig = new Rig();
        rig.Storage.BeforePut = () => { cts.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.CreateAsync(Cmd(new[] { Img("a.jpg") }), cts.Token));
        Assert.Single(rig.Billing.ReleaseRequests);
        Assert.False(Assert.Single(rig.Billing.ReleaseWasCancelled));
        Assert.Empty(rig.Repo.Commits);
    }

}
