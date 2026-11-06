using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Upload;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class CampaignMultiTenantHttpTests
{
    // Fixed identifiers for Multi-Tenant tests
    public static readonly Guid ClinicA_Id = Guid.Parse("11111111-aaaa-4111-8111-000000000001");
    public static readonly Guid ClinicB_Id = Guid.Parse("22222222-bbbb-4222-8222-000000000002");
    public static readonly Guid TenantA = Guid.Parse("aaaa1111-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbb2222-0000-0000-0000-000000000002");

    public static readonly Guid AccountAdminA = Guid.Parse("a1111111-0000-0000-0000-000000000001");
    public static readonly Guid AccountDoctorA = Guid.Parse("a2222222-0000-0000-0000-000000000002");
    public static readonly Guid AccountAdminB = Guid.Parse("b1111111-0000-0000-0000-000000000001");
    public static readonly Guid AccountDoctorB = Guid.Parse("b2222222-0000-0000-0000-000000000002");
    public static readonly Guid AccountPatient = Guid.Parse("c1111111-0000-0000-0000-000000000001");

    private sealed class FakeIdentityResolver : IAnalysisUserIdentityResolver
    {
        public Task<AnalysisUserContext> ResolveAsync(HttpRequest request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Yêu cầu cần xác thực danh tính hợp lệ.");
            }

            var token = auth["Bearer ".Length..].Trim();
            return token switch
            {
                "token_admin_a" => Task.FromResult(new AnalysisUserContext(AccountAdminA, ["CLINIC_ADMIN"])),
                "token_doctor_a" => Task.FromResult(new AnalysisUserContext(AccountDoctorA, ["DOCTOR"])),
                "token_admin_b" => Task.FromResult(new AnalysisUserContext(AccountAdminB, ["CLINIC_ADMIN"])),
                "token_doctor_b" => Task.FromResult(new AnalysisUserContext(AccountDoctorB, ["DOCTOR"])),
                "token_patient" => Task.FromResult(new AnalysisUserContext(AccountPatient, ["PATIENT"])),
                _ => throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Token không hợp lệ.")
            };
        }
    }

    private sealed class FakeClinicAccessResolver : IClinicAccessResolver
    {
        public Task<ClinicAccessDecision> ResolveAsync(ClinicAccessRequest request, CancellationToken ct)
        {
            // Clinic A resolution
            if (request.ClinicId == ClinicA_Id)
            {
                if (request.ActorAccountId == AccountAdminA)
                    return Task.FromResult(new ClinicAccessDecision(true, TenantA, "CLINIC_ADMIN"));

                if (request.ActorAccountId == AccountDoctorA)
                {
                    if (request.Action == "CAMPAIGN_READ")
                        return Task.FromResult(new ClinicAccessDecision(true, TenantA, "DOCTOR"));

                    return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, "DOCTOR", "ROLE_DENIED"));
                }

                // Any other account (e.g. Clinic B accounts or Patient) is not a member of Clinic A
                return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, null, "NOT_A_MEMBER"));
            }

            // Clinic B resolution
            if (request.ClinicId == ClinicB_Id)
            {
                if (request.ActorAccountId == AccountAdminB)
                    return Task.FromResult(new ClinicAccessDecision(true, TenantB, "CLINIC_ADMIN"));

                if (request.ActorAccountId == AccountDoctorB)
                {
                    if (request.Action == "CAMPAIGN_READ")
                        return Task.FromResult(new ClinicAccessDecision(true, TenantB, "DOCTOR"));

                    return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, "DOCTOR", "ROLE_DENIED"));
                }

                return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, null, "NOT_A_MEMBER"));
            }

            return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, null, "CLINIC_NOT_FOUND"));
        }
    }

    private sealed class InMemoryCampaignRepository : ICampaignRepository
    {
        private readonly List<CampaignResponse> _campaigns = [];
        private readonly List<(Guid TenantId, Guid BatchId, Guid? CampaignId, int TotalImages, int CompletedImages)> _batches = [];

        public void SeedBatch(Guid tenantId, Guid batchId, int totalImages, int completedImages)
        {
            _batches.Add((tenantId, batchId, null, totalImages, completedImages));
        }

        public Task<CampaignResponse> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                throw UploadFailure.Validation(new ErrorDetail("name", "Tên chiến dịch không được để trống."));
            if (string.IsNullOrWhiteSpace(request.Location))
                throw UploadFailure.Validation(new ErrorDetail("location", "Địa điểm khám không được để trống."));
            if (request.EndDate < request.StartDate)
                throw UploadFailure.Validation(new ErrorDetail("endDate", "Thời gian kết thúc phải sau hoặc bằng thời gian bắt đầu."));
            if (request.TargetPatientCount < 0)
                throw UploadFailure.Validation(new ErrorDetail("targetPatientCount", "Số lượng bệnh nhân mục tiêu không được âm."));

            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var camp = new CampaignResponse(
                id, tenantId, request.Name.Trim(), request.Description?.Trim(), request.Location.Trim(),
                request.DoctorInChargeId, request.TargetPatientCount, CampaignStatus.Planned,
                request.StartDate, request.EndDate, now, now);

            _campaigns.Add(camp);
            return Task.FromResult(camp);
        }

        public Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(Guid tenantId, string? status, CancellationToken ct)
        {
            var query = _campaigns.Where(c => c.TenantId == tenantId);
            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(c => string.Equals(c.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            return Task.FromResult<IReadOnlyList<CampaignResponse>>(query.OrderByDescending(c => c.CreatedAt).ToList());
        }

        public Task<CampaignResponse> GetCampaignByIdAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
        {
            var camp = _campaigns.FirstOrDefault(c => c.TenantId == tenantId && c.Id == campaignId);
            if (camp is null)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");

            return Task.FromResult(camp);
        }

        public Task<CampaignResponse> UpdateCampaignStatusAsync(Guid tenantId, Guid campaignId, string newStatus, CancellationToken ct)
        {
            var normalized = newStatus?.Trim().ToUpperInvariant() ?? "";
            if (!CampaignStatus.All.Contains(normalized))
                throw UploadFailure.Validation(new ErrorDetail("status", $"Trạng thái không hợp lệ. Phải là một trong: {string.Join(", ", CampaignStatus.All)}"));

            var idx = _campaigns.FindIndex(c => c.TenantId == tenantId && c.Id == campaignId);
            if (idx < 0)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát để cập nhật trạng thái.");

            var current = _campaigns[idx];
            var updated = current with { Status = normalized, UpdatedAt = DateTime.UtcNow };
            _campaigns[idx] = updated;
            return Task.FromResult(updated);
        }

        public Task LinkBatchAsync(Guid tenantId, Guid campaignId, Guid batchId, CancellationToken ct)
        {
            var camp = _campaigns.FirstOrDefault(c => c.TenantId == tenantId && c.Id == campaignId);
            if (camp is null)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");

            if (string.Equals(camp.Status, CampaignStatus.Closed, StringComparison.OrdinalIgnoreCase))
                throw new UploadFailure(409, "ERR_CAMPAIGN_CLOSED", "Chiến dịch đã kết thúc (CLOSED), không cho phép nạp thêm ảnh chụp mới.");

            var batchIdx = _batches.FindIndex(b => b.TenantId == tenantId && b.BatchId == batchId);
            if (batchIdx < 0)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy batch ảnh trong phòng khám này.");

            var b = _batches[batchIdx];
            _batches[batchIdx] = (b.TenantId, b.BatchId, campaignId, b.TotalImages, b.CompletedImages);
            return Task.CompletedTask;
        }

        public Task<CampaignSummaryResponse> GetCampaignSummaryAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
        {
            var camp = _campaigns.FirstOrDefault(c => c.TenantId == tenantId && c.Id == campaignId);
            if (camp is null)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");

            var linkedBatches = _batches.Where(b => b.TenantId == tenantId && b.CampaignId == campaignId).ToList();
            var totalBatches = linkedBatches.Count;
            var totalImages = linkedBatches.Sum(b => b.TotalImages);
            var completedImages = linkedBatches.Sum(b => b.CompletedImages);
            var rate = totalImages > 0 ? Math.Round((double)completedImages / totalImages * 100.0, 2) : 0.0;

            return Task.FromResult(new CampaignSummaryResponse(
                campaignId, tenantId, camp.Name, camp.Status, camp.TargetPatientCount,
                totalBatches, totalImages, completedImages, 0, 0, rate,
                42, 18, 4, 35));
        }

        public Task<string> ExportCampaignReportCsvAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
        {
            var camp = _campaigns.FirstOrDefault(c => c.TenantId == tenantId && c.Id == campaignId);
            if (camp is null)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");

            var csv = $"# BAO CAO TONG KET CHIEN DICH TAM SOAT\nTen chien dich,{camp.Name}\nTrang thai,{camp.Status}\n";
            return Task.FromResult(csv);
        }
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        public readonly InMemoryCampaignRepository Repository = new();
        public readonly FakeIdentityResolver Identity = new();
        public readonly FakeClinicAccessResolver AccessResolver = new();
        public WebApplication App = null!;
        public HttpClient Client = null!;

        public static async Task<TestHarness> CreateAsync()
        {
            var harness = new TestHarness();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            builder.Services.AddSingleton<IAnalysisUserIdentityResolver>(harness.Identity);
            builder.Services.AddSingleton<IClinicAccessResolver>(harness.AccessResolver);
            builder.Services.AddSingleton<ICampaignRepository>(harness.Repository);
            builder.Services.AddSingleton(harness.Repository);

            harness.App = builder.Build();
            harness.App.MapCampaignEndpoints();
            await harness.App.StartAsync();

            harness.Client = harness.App.GetTestClient();
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Client.Dispose();
        }
    }

    // =========================================================================
    // NHÓM 1: POSITIVE CONTROL (AC2 - Kiểm tra trường hợp hợp lệ trước)
    // =========================================================================

    [Fact]
    public async Task AC2_Positive_01_ClinicAdminA_Can_Create_Campaign_Successfully_201()
    {
        await using var h = await TestHarness.CreateAsync();
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_a");

        var body = new CreateCampaignRequest(
            Name: "Chiến dịch Tầm soát Đáy mắt Quận 5",
            Description: "Khám sàng lọc người cao tuổi",
            Location: "Trung tâm Y tế Quận 5",
            DoctorInChargeId: AccountDoctorA,
            TargetPatientCount: 100,
            StartDate: DateTime.UtcNow,
            EndDate: DateTime.UtcNow.AddDays(7));

        var res = await h.Client.PostAsJsonAsync($"/api/v1/campaigns?clinicContextId={ClinicA_Id}", body);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<CampaignResponse>();
        Assert.NotNull(created);
        Assert.Equal(TenantA, created.TenantId);
        Assert.Equal(CampaignStatus.Planned, created.Status);
    }

    [Fact]
    public async Task AC2_Positive_02_ClinicAdminA_Can_Read_Update_LinkBatch_And_Export_CSV()
    {
        await using var h = await TestHarness.CreateAsync();
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_a");

        // 1. Tạo campaign
        var createBody = new CreateCampaignRequest("Chiến dịch Mắt A", "Mô tả", "Trạm Y tế", AccountDoctorA, 50, DateTime.UtcNow, DateTime.UtcNow.AddDays(3));
        var createRes = await h.Client.PostAsJsonAsync($"/api/v1/campaigns?clinicContextId={ClinicA_Id}", createBody);
        var created = await createRes.Content.ReadFromJsonAsync<CampaignResponse>();
        Assert.NotNull(created);

        // 2. Đọc chi tiết
        var getRes = await h.Client.GetAsync($"/api/v1/campaigns/{created.Id}?clinicContextId={ClinicA_Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        // 3. Đổi trạng thái sang IN_PROGRESS
        var patchRes = await h.Client.PatchAsJsonAsync($"/api/v1/campaigns/{created.Id}/status?clinicContextId={ClinicA_Id}", new UpdateCampaignStatusRequest(CampaignStatus.InProgress));
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);

        // 4. Gắn Batch hợp lệ của Clinic A
        var batchA_Id = Guid.NewGuid();
        h.Repository.SeedBatch(TenantA, batchA_Id, 50, 50);
        var linkRes = await h.Client.PostAsJsonAsync($"/api/v1/campaigns/{created.Id}/batches?clinicContextId={ClinicA_Id}", new LinkBatchToCampaignRequest(batchA_Id));
        Assert.Equal(HttpStatusCode.OK, linkRes.StatusCode);

        // 5. Xuất CSV
        var exportRes = await h.Client.GetAsync($"/api/v1/campaigns/{created.Id}/export?clinicContextId={ClinicA_Id}");
        Assert.Equal(HttpStatusCode.OK, exportRes.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", exportRes.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task AC2_Positive_03_DoctorA_Can_Read_Campaign_And_Summary()
    {
        await using var h = await TestHarness.CreateAsync();

        // Admin tạo campaign trước
        var camp = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch A", null, "Địa điểm A", AccountDoctorA, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(5)), default);
        var batchId = Guid.NewGuid();
        h.Repository.SeedBatch(TenantA, batchId, 50, 45);
        await h.Repository.LinkBatchAsync(TenantA, camp.Id, batchId, default);

        // Bác sĩ Clinic A gọi đọc
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_doctor_a");

        var getRes = await h.Client.GetAsync($"/api/v1/campaigns/{camp.Id}?clinicContextId={ClinicA_Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        var sumRes = await h.Client.GetAsync($"/api/v1/campaigns/{camp.Id}/summary?clinicContextId={ClinicA_Id}");
        Assert.Equal(HttpStatusCode.OK, sumRes.StatusCode);
        var summary = await sumRes.Content.ReadFromJsonAsync<CampaignSummaryResponse>();
        Assert.NotNull(summary);
        Assert.Equal(50, summary.TotalImages);
    }

    // =========================================================================
    // NHÓM 2: NEGATIVE CONTROL (AC1 - 10+ Ca Chặn Chéo Tenant & Bảo Mật Nghiêm Ngặt)
    // =========================================================================

    [Fact]
    public async Task AC1_Neg_01_ClinicB_Admin_Using_ClinicA_Context_Gets_403_Forbidden()
    {
        await using var h = await TestHarness.CreateAsync();
        // Token của Admin Clinic B cố khai báo context của Clinic A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_b");

        var res = await h.Client.GetAsync($"/api/v1/campaigns?clinicContextId={ClinicA_Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_FORBIDDEN", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC1_Neg_02_ClinicB_Doctor_Using_ClinicA_Context_Gets_403_Forbidden()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch A", null, "Địa điểm", AccountDoctorA, 50, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), default);

        // Bác sĩ Clinic B cố đọc Campaign với context Clinic A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_doctor_b");
        var res = await h.Client.GetAsync($"/api/v1/campaigns/{campA.Id}?clinicContextId={ClinicA_Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_03_ClinicB_Admin_Probing_CampaignA_With_ClinicB_Context_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Admin B dùng context hợp lệ của Clinic B để dò quét UUID của Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_b");
        var res = await h.Client.GetAsync($"/api/v1/campaigns/{campA.Id}?clinicContextId={ClinicB_Id}");

        // Anti-probing: Phải trả về 404 Not Found, không để lộ sự tồn tại của Campaign
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC1_Neg_04_ClinicB_Doctor_Probing_CampaignA_With_ClinicB_Context_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Bác sĩ B dùng context Clinic B dò quét Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_doctor_b");
        var res = await h.Client.GetAsync($"/api/v1/campaigns/{campA.Id}?clinicContextId={ClinicB_Id}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_05_ClinicB_Admin_Mutating_CampaignA_Status_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Admin B cố cập nhật trạng thái của Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_b");
        var res = await h.Client.PatchAsJsonAsync($"/api/v1/campaigns/{campA.Id}/status?clinicContextId={ClinicB_Id}", new UpdateCampaignStatusRequest("CLOSED"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_06_ClinicB_Doctor_Reading_CampaignA_Summary_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Bác sĩ B cố xem summary của Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_doctor_b");
        var res = await h.Client.GetAsync($"/api/v1/campaigns/{campA.Id}/summary?clinicContextId={ClinicB_Id}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_07_ClinicB_Admin_Exporting_CampaignA_Csv_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Admin B cố xuất CSV của Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_b");
        var res = await h.Client.GetAsync($"/api/v1/campaigns/{campA.Id}/export?clinicContextId={ClinicB_Id}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_08_AdminA_Linking_Foreign_BatchB_Into_CampaignA_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Batch thuộc sở hữu của Clinic B (Tenant B)
        var batchB_Id = Guid.NewGuid();
        h.Repository.SeedBatch(TenantB, batchB_Id, 50, 50);

        // Admin Clinic A cố tình gắn Batch của Clinic B vào Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_a");
        var res = await h.Client.PostAsJsonAsync($"/api/v1/campaigns/{campA.Id}/batches?clinicContextId={ClinicA_Id}", new LinkBatchToCampaignRequest(batchB_Id));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC1_Neg_09_AdminB_Linking_BatchB_Into_Foreign_CampaignA_Gets_404_NotFound()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        var batchB_Id = Guid.NewGuid();
        h.Repository.SeedBatch(TenantB, batchB_Id, 50, 50);

        // Admin B dùng context Clinic B cố gắn batch vào Campaign A
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_b");
        var res = await h.Client.PostAsJsonAsync($"/api/v1/campaigns/{campA.Id}/batches?clinicContextId={ClinicB_Id}", new LinkBatchToCampaignRequest(batchB_Id));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_10_AdminA_Linking_Batch_Into_Closed_Campaign_Gets_409_Conflict()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);
        await h.Repository.UpdateCampaignStatusAsync(TenantA, campA.Id, CampaignStatus.Closed, default);

        var batchA_Id = Guid.NewGuid();
        h.Repository.SeedBatch(TenantA, batchA_Id, 50, 50);

        // Admin Clinic A cố gắn thêm batch vào campaign đã CLOSED
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_a");
        var res = await h.Client.PostAsJsonAsync($"/api/v1/campaigns/{campA.Id}/batches?clinicContextId={ClinicA_Id}", new LinkBatchToCampaignRequest(batchA_Id));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_CAMPAIGN_CLOSED", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC1_Neg_11_DoctorA_Attempting_To_Create_Campaign_Gets_403_RoleDenied()
    {
        await using var h = await TestHarness.CreateAsync();
        // Bác sĩ Clinic A cố tạo chiến dịch (chỉ CLINIC_ADMIN mới có quyền CAMPAIGN_MANAGE)
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_doctor_a");

        var body = new CreateCampaignRequest("Chiến dịch trái phép", null, "Địa điểm", null, 50, DateTime.UtcNow, DateTime.UtcNow.AddDays(1));
        var res = await h.Client.PostAsJsonAsync($"/api/v1/campaigns?clinicContextId={ClinicA_Id}", body);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_12_DoctorA_Attempting_To_Export_CSV_Gets_403_RoleDenied()
    {
        await using var h = await TestHarness.CreateAsync();
        var campA = await h.Repository.CreateCampaignAsync(TenantA, new("Chiến dịch Clinic A", null, "Quận 1", null, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(2)), default);

        // Bác sĩ cố xuất CSV báo cáo (cần CAMPAIGN_EXPORT)
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_doctor_a");
        var res = await h.Client.GetAsync($"/api/v1/campaigns/{campA.Id}/export?clinicContextId={ClinicA_Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_13_Patient_Attempting_To_Access_Campaign_Gets_403_Forbidden()
    {
        await using var h = await TestHarness.CreateAsync();
        // Bệnh nhân cá nhân không thuộc clinic cố gọi Campaign API
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_patient");

        var res = await h.Client.GetAsync($"/api/v1/campaigns?clinicContextId={ClinicA_Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_14_Anonymous_Request_Without_Token_Gets_401_Unauthorized()
    {
        await using var h = await TestHarness.CreateAsync();
        // Không gửi Authorization header
        var res = await h.Client.GetAsync($"/api/v1/campaigns?clinicContextId={ClinicA_Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AC1_Neg_15_Client_Sending_Header_Without_QueryParam_Gets_400_BadRequest()
    {
        await using var h = await TestHarness.CreateAsync();
        h.Client.DefaultRequestHeaders.Authorization = new("Bearer", "token_admin_a");
        // ADR-005 D2: Không được tin cậy header, bắt buộc query clinicContextId
        h.Client.DefaultRequestHeaders.Add("X-Clinic-Context-Id", ClinicA_Id.ToString());

        var res = await h.Client.GetAsync("/api/v1/campaigns");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_VALIDATION", json.GetProperty("error").GetProperty("code").GetString());
    }
}

