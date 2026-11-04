using AURA.Analysis.Api.Upload;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public class FileAccessTests
{
    private static readonly Guid Requester = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Patient = Guid.Parse("9b2e5c10-1111-4a2b-8c3d-000000000001");
    private static readonly Guid AnalysisId = Guid.Parse("3f6c1d52-8a41-4c5e-9b0a-6d2f7a1e0001");
    private static readonly Guid FileId = Guid.Parse("f0000000-0000-4000-8000-000000000001");
    private static readonly Guid AnnoId = Guid.Parse("f0000000-0000-4000-8000-000000000002");

    private sealed class Rig
    {
        public readonly FakeAccess Access = new();
        public readonly FakeStorage Storage = new();
        public readonly FakeFileRepo Files = new();
        public readonly StorageOptions Options = new();
        public readonly FileAccessService Service;
        public Rig()
        {
            Files.Records.Add(new StoredFileRecord(AnalysisId, FileId, FakeAccess.Tenant, Patient, Requester, StoredFileKind.Original, "t/b/a"));
            Files.Records.Add(new StoredFileRecord(AnalysisId, AnnoId, FakeAccess.Tenant, Patient, Requester, StoredFileKind.Annotation, "t/r/mask"));
            Service = new FileAccessService(Options, Files, Access, Storage, NullLogger<FileAccessService>.Instance);
        }
        public Task<FileAccessResult> Get(Guid? analysis = null, Guid? file = null) =>
            Service.GetAsync(Requester, null, analysis ?? AnalysisId, file ?? FileId, default);
    }

    private static async Task<UploadFailure> Fails(Func<Task> act) => await Assert.ThrowsAsync<UploadFailure>(act);

    [Fact]
    public async Task Authorized_access_returns_authenticated_proxy_without_SAS()
    {
        var rig = new Rig();
        var r = await rig.Get();
        Assert.Equal($"/api/v1/analyses/{AnalysisId:D}/files/{FileId:D}/content",r.Url);
        Assert.Null(rig.Storage.LastReadAccess);
        Assert.Equal("FILE_READ", rig.Access.LastAction);
        Assert.Equal(AnalysisId, rig.Access.LastRequest!.AnalysisId);
        var secondsLeft = (r.ExpiresAt - DateTime.UtcNow).TotalSeconds;
        Assert.True(secondsLeft > 100 && secondsLeft <= 121);
    }

    [Fact]
    public async Task File_annotation_dung_container_annotation()
    {
        var rig = new Rig();
        rig.Storage.Files["aura-annotations/t/r/mask"]=[7,8,9];
        await using var stream=await rig.Service.ReadAsync(Requester,null,AnalysisId,AnnoId,default);
        Assert.Equal(7,stream.ReadByte());
    }

    [Fact]
    public async Task TTL_lay_tu_cau_hinh_Storage()
    {
        var rig = new Rig();
        rig.Options.ReadUrlTtlSeconds = 60;
        var result=await rig.Get();
        Assert.InRange((result.ExpiresAt-DateTime.UtcNow).TotalSeconds,58,61);
    }

    [Fact]
    public async Task Khong_ton_tai_hoac_sai_cap_analysis_file_tra_404_va_chua_hoi_Profile()
    {
        var rig = new Rig();
        var f1 = await Fails(() => rig.Get(file: Guid.NewGuid()));
        var f2 = await Fails(() => rig.Get(analysis: Guid.NewGuid()));
        Assert.Equal(404, f1.Status);
        Assert.Equal(404, f2.Status);
        Assert.Equal(0, rig.Access.Calls);
        Assert.Null(rig.Storage.LastReadAccess);
    }

    [Fact]
    public async Task Khong_co_quyen_tra_dung_cung_loi_404_nhu_khong_ton_tai_va_khong_xin_URL()
    {
        var rig = new Rig { Access = { Allowed = false } };
        var denied = await Fails(() => rig.Get());
        var missing = await Fails(() => rig.Get(file: Guid.NewGuid()));
        Assert.Equal(missing.Status, denied.Status);
        Assert.Equal(missing.Code, denied.Code);
        Assert.Equal(missing.Message, denied.Message);          // không lộ file có tồn tại hay không
        Assert.Null(rig.Storage.LastReadAccess);
    }

    [Fact]
    public async Task Tenant_khac_tra_404_du_Profile_cho_phep()
    {
        var rig = new Rig { Access = { TenantOverride = Guid.NewGuid() } };
        var f = await Fails(() => rig.Get());
        Assert.Equal(404, f.Status);
        Assert.Null(rig.Storage.LastReadAccess);
    }

    [Fact]
    public async Task Profile_DB_hoac_storage_hong_tra_503_ERR_NETWORK()
    {
        var a = new Rig { Access = { Down = true } };
        Assert.Equal(503, (await Fails(() => a.Get())).Status);
        var b = new Rig { Files = { Down = true } };
        Assert.Equal(503, (await Fails(() => b.Get())).Status);
        var c = new Rig(); // Missing private object is a dependency failure at authenticated read time.
        var f = await Fails(() => c.Service.ReadAsync(Requester,null,AnalysisId,FileId,default));
        Assert.Equal("ERR_NETWORK", f.Code);
        Assert.True(f.Retryable);
    }
    [Fact]
    public async Task Access_link_cannot_bypass_revocation_before_bytes_are_read()
    {
        var rig=new Rig();rig.Storage.Files["aura-originals/t/b/a"]=[1,2,3];
        await rig.Get();rig.Access.Allowed=false;
        Assert.Equal(404,(await Fails(()=>rig.Service.ReadAsync(Requester,null,AnalysisId,FileId,default))).Status);
        Assert.Null(rig.Storage.LastReadAccess);
    }

    [Fact]
    public async Task Ket_qua_chi_co_url_va_expiresAt_khong_lo_object_key()
    {
        var rig = new Rig();
        var json = System.Text.Json.JsonSerializer.Serialize(await rig.Get(), UploadJson.Options);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal("expiresAt,url", string.Join(",", names));
    }
}
