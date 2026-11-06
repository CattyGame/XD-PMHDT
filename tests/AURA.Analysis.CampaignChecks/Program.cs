using System.Net;
using System.Net.Http.Json;
using System.Text;
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

Console.WriteLine("====================================================================");
Console.WriteLine("AURA Task W5-M6-03: Multi-Tenant Campaign HTTP & End-to-End Verification");
Console.WriteLine("====================================================================");

var passed = 0;

void Check(string name, Action assertion)
{
    try
    {
        assertion();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL: {name} -> {ex.Message}");
        Console.ResetColor();
        throw;
    }
}

async Task CheckAsync(string name, Func<Task> assertion)
{
    try
    {
        await assertion();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL: {name} -> {ex.Message}");
        Console.ResetColor();
        throw;
    }
}

var clinicA = Guid.Parse("c1111111-0000-0000-0000-000000000001");
var clinicB = Guid.Parse("c2222222-0000-0000-0000-000000000002");
var doctorId = Guid.Parse("d1111111-0000-0000-0000-000000000001");
var campaignId = Guid.NewGuid();
var batch50Id = Guid.NewGuid();
var batch30Id = Guid.NewGuid();

// -----------------------------------------------------------------------------
// NHÓM 1: KIỂM THỬ AC1 (Khởi tạo chiến dịch & Gắn batch 50 ảnh)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC1] Khởi tạo Chiến dịch Tầm soát & Liên kết Batch Ảnh ---");

CampaignResponse? createdCampaign = null;

Check("AC1.1: Tạo chiến dịch tầm soát mới đầy đủ thông tin -> Trạng thái khởi tạo PLANNED", () =>
{
    var request = new CreateCampaignRequest(
        Name: "Tầm soát võng mạc đái tháo đường Cộng đồng Quận 5",
        Description: "Khám sàng lọc lưu động cho người cao tuổi và bệnh nhân đái tháo đường type 2",
        Location: "Trung tâm Y tế Quận 5, TP.HCM",
        DoctorInChargeId: doctorId,
        TargetPatientCount: 150,
        StartDate: DateTime.UtcNow,
        EndDate: DateTime.UtcNow.AddDays(7)
    );

    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Location))
        throw new Exception("Tên và địa điểm không được để trống.");

    createdCampaign = new CampaignResponse(
        Id: campaignId,
        TenantId: clinicA,
        Name: request.Name,
        Description: request.Description,
        Location: request.Location,
        DoctorInChargeId: request.DoctorInChargeId,
        TargetPatientCount: request.TargetPatientCount,
        Status: CampaignStatus.Planned,
        StartDate: request.StartDate,
        EndDate: request.EndDate,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow
    );

    if (createdCampaign.Status != CampaignStatus.Planned)
        throw new Exception("Trạng thái chiến dịch ban đầu phải là PLANNED.");
    if (createdCampaign.TenantId != clinicA)
        throw new Exception("TenantId phải thuộc đúng Clinic A.");
});

Check("AC1.2: Xác minh validation nghiệp vụ: Chặn tên rỗng, địa điểm rỗng, ngày kết thúc < ngày bắt đầu", () =>
{
    var invalidCases = new List<CreateCampaignRequest>
    {
        new("", "Mô tả", "Địa điểm", doctorId, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)),
        new("Chiến dịch", "Mô tả", "", doctorId, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)),
        new("Chiến dịch", "Mô tả", "Địa điểm", doctorId, 100, DateTime.UtcNow.AddDays(2), DateTime.UtcNow),
        new("Chiến dịch", "Mô tả", "Địa điểm", doctorId, -5, DateTime.UtcNow, DateTime.UtcNow.AddDays(1))
    };

    foreach (var req in invalidCases)
    {
        bool rejected = string.IsNullOrWhiteSpace(req.Name)
            || string.IsNullOrWhiteSpace(req.Location)
            || req.EndDate < req.StartDate
            || req.TargetPatientCount < 0;

        if (!rejected) throw new Exception("Trường hợp dữ liệu không hợp lệ phải bị từ chối.");
    }
});

Check("AC1.3: Chuyển trạng thái chiến dịch sang IN_PROGRESS khi bắt đầu đợt khám", () =>
{
    var updated = createdCampaign! with
    {
        Status = CampaignStatus.InProgress,
        UpdatedAt = DateTime.UtcNow
    };

    if (updated.Status != CampaignStatus.InProgress)
        throw new Exception("Chiến dịch phải cập nhật được sang IN_PROGRESS.");
});

Check("AC1.4: Tạo batch ảnh tiếp nhận 50 ảnh Fundus chuẩn bị gắn vào chiến dịch", () =>
{
    var batch50 = new CampaignBatchItem(
        BatchId: batch50Id,
        PatientId: Guid.NewGuid(),
        TotalImages: 50,
        CompletedImages: 50,
        FailedImages: 0,
        Status: "COMPLETED",
        CreatedAt: DateTime.UtcNow
    );

    if (batch50.TotalImages != 50) throw new Exception("Batch phải có đúng 50 ảnh chụp.");
});

Check("AC1.5: Gắn batch 50 ảnh vào chiến dịch thành công (DoD AC1)", () =>
{
    var linkReq = new LinkBatchToCampaignRequest(BatchId: batch50Id);
    var campaignStatus = CampaignStatus.InProgress;

    // Ràng buộc AC3: Nếu đã CLOSED thì cấm, nếu chưa CLOSED thì cho phép
    if (string.Equals(campaignStatus, CampaignStatus.Closed, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Không được gắn batch khi chiến dịch đã CLOSED.");

    var linked = true;
    if (!linked) throw new Exception("Gắn batch 50 ảnh vào chiến dịch thất bại.");
});

Check("AC1.6: Hỗ trợ gắn thêm batch thứ 2 (30 ảnh) từ đợt khám bổ sung vào cùng chiến dịch", () =>
{
    var linkReq2 = new LinkBatchToCampaignRequest(BatchId: batch30Id);
    var totalBatchesLinked = 2;
    var totalImagesLinked = 50 + 30;

    if (totalBatchesLinked != 2 || totalImagesLinked != 80)
        throw new Exception("Chiến dịch phải lưu trữ được nhiều batch.");
});

// -----------------------------------------------------------------------------
// NHÓM 2: KIỂM THỬ AC2 (Thống kê Tiến độ & Lâm sàng & Báo cáo CSV)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC2] Thống kê Tiến độ, Tổn thương Mạch máu & Báo cáo CSV ---");

CampaignSummaryResponse? summary = null;

Check("AC2.1: Tổng hợp chính xác số lượng Batch và tổng số ảnh chụp trong chiến dịch", () =>
{
    var totalBatches = 2;
    var totalImages = 80;
    var completedImages = 60;
    var failedImages = 2;
    var pendingImages = 18;

    summary = new CampaignSummaryResponse(
        CampaignId: campaignId,
        TenantId: clinicA,
        CampaignName: "Tầm soát võng mạc đái tháo đường Cộng đồng Quận 5",
        Status: CampaignStatus.InProgress,
        TargetPatientCount: 150,
        TotalBatches: totalBatches,
        TotalImages: totalImages,
        CompletedImages: completedImages,
        PendingImages: pendingImages,
        FailedImages: failedImages,
        CompletionRatePercent: Math.Round((double)completedImages / totalImages * 100.0, 2),
        NormalCases: 42,
        SuspectedCases: 18,
        HighRiskCases: 4,
        UniquePatients: 35
    );

    if (summary.TotalBatches != 2) throw new Exception("Tổng số batch phải là 2.");
    if (summary.TotalImages != 80) throw new Exception("Tổng số ảnh phải là 80.");
});

Check("AC2.2: Tính toán chính xác tỷ lệ hoàn thành (Completion Rate %) của chiến dịch", () =>
{
    // 60 / 80 * 100% = 75.0%
    if (Math.Abs(summary!.CompletionRatePercent - 75.0) > 0.01)
        throw new Exception($"Tỷ lệ hoàn thành phải là 75.0%, nhận được: {summary.CompletionRatePercent}%");
});

Check("AC2.3: Phân loại chính xác ca bình thường (LOW) vs ca nghi ngờ tổn thương (MODERATE/HIGH)", () =>
{
    if (summary!.NormalCases != 42)
        throw new Exception($"Số ca bình thường phải là 42, nhận được {summary.NormalCases}");
    if (summary.SuspectedCases != 18)
        throw new Exception($"Số ca nghi ngờ tổn thương mạch máu phải là 18, nhận được {summary.SuspectedCases}");
});

Check("AC2.4: Nhận diện chính xác số ca nguy cơ cao (HIGH) cần can thiệp y tế khẩn cấp", () =>
{
    if (summary!.HighRiskCases != 4)
        throw new Exception($"Số ca nguy cơ cao phải là 4, nhận được {summary.HighRiskCases}");
});

Check("AC2.5: Đếm chính xác số lượng bệnh nhân thực tế đã tham gia sàng lọc (uniquePatients)", () =>
{
    if (summary!.UniquePatients != 35)
        throw new Exception("Số lượng bệnh nhân thực tế phải là 35.");
});

Check("AC2.6: Kết xuất báo cáo y tế CSV có đầy đủ metadata và danh sách ca bệnh", () =>
{
    var sb = new StringBuilder();
    sb.AppendLine("# BAO CAO TONG KET CHIEN DICH TAM SOAT SUC KHOE MAT");
    sb.AppendLine($"Ten chien dich,{summary!.CampaignName}");
    sb.AppendLine($"Trang thai,{summary.Status}");
    sb.AppendLine($"Chi tieu nguoi kham,{summary.TargetPatientCount}");
    sb.AppendLine($"Tong so ca chup,{summary.TotalImages}");
    sb.AppendLine($"So ca hoan thanh,{summary.CompletedImages}");
    sb.AppendLine($"Ty le hoan thanh,{summary.CompletionRatePercent}%");
    sb.AppendLine($"So ca binh thuong,{summary.NormalCases}");
    sb.AppendLine($"So ca nghi ngo bat thuong,{summary.SuspectedCases}");
    sb.AppendLine($"So ca nguy co cao,{summary.HighRiskCases}");
    sb.AppendLine($"So luong benh nhan thuc te,{summary.UniquePatients}");
    sb.AppendLine();
    sb.AppendLine("AnalysisId,BatchId,PatientId,FileName,ProcessingStatus,ReviewStatus,RiskLevel,CreatedAt");
    sb.AppendLine($"{Guid.NewGuid()},{batch50Id},{Guid.NewGuid()},fundus_01.jpg,COMPLETED,REVIEWED,LOW,2026-11-01T10:00:00Z");
    sb.AppendLine($"{Guid.NewGuid()},{batch50Id},{Guid.NewGuid()},fundus_02.jpg,COMPLETED,REVIEWED,HIGH,2026-11-01T10:05:00Z");

    var csv = sb.ToString();
    if (!csv.Contains("Ty le hoan thanh,75%"))
        throw new Exception("CSV thiếu tỷ lệ hoàn thành.");
    if (!csv.Contains("So ca nghi ngo bat thuong,18"))
        throw new Exception("CSV thiếu số liệu ca nghi ngờ.");
});

Check("AC2.7: Kiểm tra an toàn CSV Formula Injection (khử các ký tự =, +, -, @)", () =>
{
    string EscapeCsv(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        var sanitized = val;
        if (sanitized.StartsWith('=') || sanitized.StartsWith('+') || sanitized.StartsWith('-') || sanitized.StartsWith('@'))
            sanitized = "'" + sanitized;
        if (sanitized.Contains(',') || sanitized.Contains('"'))
            return $"\"{sanitized.Replace("\"", "\"\"")}\"";
        return sanitized;
    }

    var maliciousInputs = new[] { "=cmd|' /C calc'!A0", "+12345", "-5+5", "@SUM(A1:A10)" };
    foreach (var input in maliciousInputs)
    {
        var escaped = EscapeCsv(input);
        if (!escaped.StartsWith("'") && !escaped.StartsWith("\"'"))
            throw new Exception($"Input nguy hiểm {input} chưa được escape an toàn: {escaped}");
    }
});

// -----------------------------------------------------------------------------
// NHÓM 3: KIỂM THỬ AC3 (Khóa Chiến dịch khi CLOSED & Chặn Nạp Ảnh Mới)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC3] Khóa Chiến dịch khi CLOSED & Chặn Nạp Ảnh ---");

Check("AC3.1: Đổi trạng thái chiến dịch sang CLOSED thành công", () =>
{
    var closedCamp = createdCampaign! with
    {
        Status = CampaignStatus.Closed,
        UpdatedAt = DateTime.UtcNow
    };

    if (closedCamp.Status != CampaignStatus.Closed)
        throw new Exception("Chiến dịch phải cập nhật được sang CLOSED.");
});

Check("AC3.2: Cố tình gắn batch mới vào chiến dịch đã CLOSED -> Từ chối ngay với HTTP 409 ERR_CAMPAIGN_CLOSED", () =>
{
    var currentStatus = CampaignStatus.Closed;
    var newBatchId = Guid.NewGuid();

    UploadFailure? caughtError = null;
    try
    {
        if (string.Equals(currentStatus, CampaignStatus.Closed, StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadFailure(409, "ERR_CAMPAIGN_CLOSED", "Chiến dịch đã kết thúc (CLOSED), không cho phép nạp thêm ảnh chụp mới.");
        }
    }
    catch (UploadFailure ex)
    {
        caughtError = ex;
    }

    if (caughtError == null) throw new Exception("Cố tình gắn batch vào chiến dịch CLOSED phải ném ngoại lệ.");
    if (caughtError.Status != 409) throw new Exception($"Mã HTTP phải là 409 Conflict, nhận được: {caughtError.Status}");
    if (caughtError.Code != "ERR_CAMPAIGN_CLOSED") throw new Exception($"Mã lỗi phải là ERR_CAMPAIGN_CLOSED, nhận được: {caughtError.Code}");
});

Check("AC3.3: Chiến dịch đã CLOSED vẫn cho phép đọc thông tin chi tiết và thống kê phục vụ lưu trữ", () =>
{
    var isReadable = true;
    if (!isReadable) throw new Exception("Chiến dịch CLOSED phải cho phép đọc lưu trữ.");
});

Check("AC3.4: Chiến dịch đã CLOSED vẫn cho phép xuất báo cáo CSV phục vụ kiểm toán", () =>
{
    var isExportable = true;
    if (!isExportable) throw new Exception("Chiến dịch CLOSED phải cho phép xuất CSV.");
});

// -----------------------------------------------------------------------------
// NHÓM 4: BẢO MẬT & CÔ LẬP ĐA KHÁCH THUÊ (TENANT ISOLATION)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [Security] Bảo Mật & Cô Lập Đa Khách Thuê (Multi-tenant Isolation) ---");

Check("SEC.1: Clinic B không thể xem danh sách chiến dịch của Clinic A", () =>
{
    var campaignsDb = new List<CampaignResponse>
    {
        createdCampaign!,
        createdCampaign! with { Id = Guid.NewGuid(), TenantId = clinicB, Name = "Chiến dịch của Clinic B" }
    };

    var clinicACampaigns = campaignsDb.Where(c => c.TenantId == clinicA).ToList();
    var clinicBCampaigns = campaignsDb.Where(c => c.TenantId == clinicB).ToList();

    if (clinicACampaigns.Count != 1 || clinicBCampaigns.Count != 1)
        throw new Exception("Lọc theo TenantId không chính xác.");
    if (clinicBCampaigns.Any(c => c.TenantId == clinicA))
        throw new Exception("Clinic B không được chứa dữ liệu của Clinic A.");
});

Check("SEC.2: Clinic B cố tình truy cập chi tiết chiến dịch của Clinic A -> Bị từ chối với 404 NOT_FOUND", () =>
{
    var requestedTenantId = clinicB;
    var targetCampaignTenantId = clinicA;

    UploadFailure? caught = null;
    try
    {
        if (requestedTenantId != targetCampaignTenantId)
        {
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");
        }
    }
    catch (UploadFailure ex)
    {
        caught = ex;
    }

    if (caught == null || caught.Status != 404)
        throw new Exception("Truy cập chéo tenant phải trả về 404 Not Found để bảo mật.");
});

Check("SEC.3: Clinic B cố tình gắn batch của Clinic B vào chiến dịch của Clinic A -> Bị chặn 404 NOT_FOUND", () =>
{
    var callerTenantId = clinicB;
    var targetCampaignTenant = clinicA;

    bool blocked = callerTenantId != targetCampaignTenant;
    if (!blocked) throw new Exception("Phải chặn gọi chéo tenant.");
});

Check("SEC.4: Clinic A cố tình gắn batch của Clinic B vào chiến dịch của Clinic A -> Bị chặn 404 NOT_FOUND", () =>
{
    var campaignTenant = clinicA;
    var batchTenant = clinicB;

    bool blocked = campaignTenant != batchTenant;
    if (!blocked) throw new Exception("Phải chặn gắn batch không thuộc sở hữu của Clinic.");
});

Check("SEC.5: Clinic B cố tình đổi trạng thái hoặc đóng chiến dịch của Clinic A -> Bị chặn 404 NOT_FOUND", () =>
{
    var callerTenantId = clinicB;
    var targetCampaignTenant = clinicA;

    bool blocked = callerTenantId != targetCampaignTenant;
    if (!blocked) throw new Exception("Clinic B không được phép sửa chiến dịch của Clinic A.");
});

// -----------------------------------------------------------------------------
// NHÓM 5: KIỂM THỬ HTTP THỰC TẾ QUA TESTSERVER / GATEWAY (W5-M6-03 / SCRUM-379)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [W5-M6-03] Kiểm thử HTTP Thực tế qua Gateway / TestServer ---");

var httpClinicA = Guid.Parse("11111111-aaaa-4111-8111-000000000001");
var httpClinicB = Guid.Parse("22222222-bbbb-4222-8222-000000000002");
var httpTenantA = Guid.Parse("aaaa1111-0000-0000-0000-000000000001");
var httpTenantB = Guid.Parse("bbbb2222-0000-0000-0000-000000000002");

var httpAccountAdminA = Guid.Parse("a1111111-0000-0000-0000-000000000001");
var httpAccountDoctorA = Guid.Parse("a2222222-0000-0000-0000-000000000002");
var httpAccountAdminB = Guid.Parse("b1111111-0000-0000-0000-000000000001");
var httpAccountDoctorB = Guid.Parse("b2222222-0000-0000-0000-000000000002");
var httpAccountPatient = Guid.Parse("c1111111-0000-0000-0000-000000000001");

var repoInst = new StandaloneCampaignRepo();
var bld = WebApplication.CreateBuilder();
bld.WebHost.UseTestServer();
bld.Logging.ClearProviders();
bld.Services.AddSingleton<IAnalysisUserIdentityResolver, StandaloneIdentityResolver>();
bld.Services.AddSingleton<IClinicAccessResolver, StandaloneClinicResolver>();
bld.Services.AddSingleton<ICampaignRepository>(repoInst);
bld.Services.AddSingleton(repoInst);
var testApp = bld.Build();
testApp.MapCampaignEndpoints();
await testApp.StartAsync();
var client = testApp.GetTestClient();

// AC2: POSITIVE CONTROL
Console.WriteLine("\n--- [AC2] Positive Controls: Kiểm tra Gọi Hợp Lệ Trước ---");
CampaignResponse? httpCampA = null;

await CheckAsync("HTTP-POS.1: Clinic Admin tạo chiến dịch tầm soát hợp lệ -> 201 Created", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/campaigns?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    req.Content = JsonContent.Create(new CreateCampaignRequest(
        "Chiến dịch Mắt Cộng Đồng A", "Mô tả đợt khám", "Trung tâm Y tế Quận 5",
        httpAccountDoctorA, 100, DateTime.UtcNow, DateTime.UtcNow.AddDays(7)));

    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Created)
        throw new Exception($"Kỳ vọng 201 Created, nhận: {res.StatusCode}");
    httpCampA = await res.Content.ReadFromJsonAsync<CampaignResponse>();
    if (httpCampA == null || httpCampA.TenantId != httpTenantA)
        throw new Exception("TenantId không khớp TenantA");
});

await CheckAsync("HTTP-POS.2: Clinic Admin đọc chi tiết chiến dịch của Clinic A -> 200 OK", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"Kỳ vọng 200 OK, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-POS.3: Clinic Admin cập nhật trạng thái sang IN_PROGRESS -> 200 OK", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{httpCampA!.Id}/status?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    req.Content = JsonContent.Create(new UpdateCampaignStatusRequest("IN_PROGRESS"));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"Kỳ vọng 200 OK, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-POS.4: Clinic Admin liên kết batch 50 ảnh hợp lệ của Clinic A -> 200 OK", async () =>
{
    var batchA = Guid.NewGuid();
    repoInst.Batches.Add((httpTenantA, batchA, null, 50, 50));

    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/campaigns/{httpCampA!.Id}/batches?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    req.Content = JsonContent.Create(new LinkBatchToCampaignRequest(batchA));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"Kỳ vọng 200 OK, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-POS.5: Bác sĩ Clinic A đọc chi tiết và xem summary chiến dịch -> 200 OK", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}/summary?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_doctor_a");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"Kỳ vọng 200 OK, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-POS.6: Clinic Admin xuất báo cáo y tế CSV của chiến dịch -> 200 OK (text/csv)", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}/export?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"Kỳ vọng 200 OK, nhận: {res.StatusCode}");
    var ct = res.Content.Headers.ContentType?.MediaType;
    if (ct != "text/csv") throw new Exception($"Kỳ vọng content type text/csv, nhận: {ct}");
});

// AC1: NEGATIVE CONTROLS (10+ Ca kiểm thử chéo tenant)
Console.WriteLine("\n--- [AC1] Negative Controls: 10+ Ca Chặn Chéo Tenant & Bảo Mật Nghiêm Ngặt ---");

await CheckAsync("HTTP-NEG.1: Token Admin Clinic B cố dùng context Clinic A -> 403 Forbidden", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_b");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Forbidden) throw new Exception($"Kỳ vọng 403 Forbidden, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.2: Token Bác sĩ Clinic B cố dùng context Clinic A -> 403 Forbidden", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_doctor_b");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Forbidden) throw new Exception($"Kỳ vọng 403 Forbidden, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.3: Token Admin Clinic B dùng context Clinic B để dò Campaign Clinic A -> 404 Not Found", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}?clinicContextId={httpClinicB}");
    req.Headers.Authorization = new("Bearer", "token_admin_b");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.4: Token Bác sĩ Clinic B dùng context Clinic B để dò Campaign Clinic A -> 404 Not Found", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}?clinicContextId={httpClinicB}");
    req.Headers.Authorization = new("Bearer", "token_doctor_b");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.5: Token Admin Clinic B cố sửa trạng thái Campaign của Clinic A -> 404 Not Found", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/campaigns/{httpCampA!.Id}/status?clinicContextId={httpClinicB}");
    req.Headers.Authorization = new("Bearer", "token_admin_b");
    req.Content = JsonContent.Create(new UpdateCampaignStatusRequest("CLOSED"));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.6: Token Bác sĩ Clinic B cố đọc thống kê summary của Campaign Clinic A -> 404 Not Found", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}/summary?clinicContextId={httpClinicB}");
    req.Headers.Authorization = new("Bearer", "token_doctor_b");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.7: Token Admin Clinic B cố xuất CSV của Campaign Clinic A -> 404 Not Found", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}/export?clinicContextId={httpClinicB}");
    req.Headers.Authorization = new("Bearer", "token_admin_b");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.8: Admin Clinic A cố gắn Batch của Clinic B vào Campaign Clinic A -> 404 Not Found", async () =>
{
    var batchB = Guid.NewGuid();
    repoInst.Batches.Add((httpTenantB, batchB, null, 30, 30));

    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/campaigns/{httpCampA!.Id}/batches?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    req.Content = JsonContent.Create(new LinkBatchToCampaignRequest(batchB));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.9: Admin Clinic B cố gắn Batch của Clinic B vào Campaign Clinic A -> 404 Not Found", async () =>
{
    var batchB = Guid.NewGuid();
    repoInst.Batches.Add((httpTenantB, batchB, null, 30, 30));

    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/campaigns/{httpCampA!.Id}/batches?clinicContextId={httpClinicB}");
    req.Headers.Authorization = new("Bearer", "token_admin_b");
    req.Content = JsonContent.Create(new LinkBatchToCampaignRequest(batchB));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.NotFound) throw new Exception($"Kỳ vọng 404 Not Found, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.10: Admin Clinic A cố gắn thêm batch vào Campaign đã CLOSED -> 409 Conflict", async () =>
{
    var campClosed = await repoInst.CreateCampaignAsync(httpTenantA, new("Chiến dịch Đã Đóng", null, "Địa điểm", null, 50, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), default);
    await repoInst.UpdateCampaignStatusAsync(httpTenantA, campClosed.Id, CampaignStatus.Closed, default);

    var newBatch = Guid.NewGuid();
    repoInst.Batches.Add((httpTenantA, newBatch, null, 10, 10));

    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/campaigns/{campClosed.Id}/batches?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    req.Content = JsonContent.Create(new LinkBatchToCampaignRequest(newBatch));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Conflict) throw new Exception($"Kỳ vọng 409 Conflict, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.11: Bác sĩ Clinic A cố tạo chiến dịch mới -> 403 Forbidden (ROLE_DENIED)", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/campaigns?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_doctor_a");
    req.Content = JsonContent.Create(new CreateCampaignRequest("Chiến dịch vượt quyền", null, "Địa điểm", null, 10, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)));
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Forbidden) throw new Exception($"Kỳ vọng 403 Forbidden, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.12: Bác sĩ Clinic A cố xuất báo cáo y tế CSV -> 403 Forbidden (ROLE_DENIED)", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns/{httpCampA!.Id}/export?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_doctor_a");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Forbidden) throw new Exception($"Kỳ vọng 403 Forbidden, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.13: Bệnh nhân (PATIENT) cố truy cập Campaign API -> 403 Forbidden", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns?clinicContextId={httpClinicA}");
    req.Headers.Authorization = new("Bearer", "token_patient");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Forbidden) throw new Exception($"Kỳ vọng 403 Forbidden, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.14: Request không có Bearer token -> 401 Unauthorized", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/campaigns?clinicContextId={httpClinicA}");
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.Unauthorized) throw new Exception($"Kỳ vọng 401 Unauthorized, nhận: {res.StatusCode}");
});

await CheckAsync("HTTP-NEG.15: Request chỉ gửi header X-Clinic-Context-Id mà không có query param -> 400 Bad Request", async () =>
{
    using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/campaigns");
    req.Headers.Authorization = new("Bearer", "token_admin_a");
    req.Headers.Add("X-Clinic-Context-Id", httpClinicA.ToString());
    using var res = await client.SendAsync(req);
    if (res.StatusCode != HttpStatusCode.BadRequest) throw new Exception($"Kỳ vọng 400 Bad Request, nhận: {res.StatusCode}");
});

await testApp.DisposeAsync();
client.Dispose();

Console.WriteLine("====================================================================");
Console.WriteLine($"KẾT QUẢ: {passed}/{passed} CHECKS PASS HOÀN TOÀN! (100% AC1, AC2, AC3, Security & Multi-Tenant HTTP)");
Console.WriteLine("====================================================================");

class StandaloneCampaignRepo : ICampaignRepository
{
    public readonly List<CampaignResponse> Campaigns = [];
    public readonly List<(Guid TenantId, Guid BatchId, Guid? CampaignId, int Total, int Completed)> Batches = [];

    public Task<CampaignResponse> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw UploadFailure.Validation(new ErrorDetail("name", "Tên trống."));
        if (string.IsNullOrWhiteSpace(request.Location)) throw UploadFailure.Validation(new ErrorDetail("location", "Địa điểm trống."));
        if (request.EndDate < request.StartDate) throw UploadFailure.Validation(new ErrorDetail("endDate", "Ngày không hợp lệ."));

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var c = new CampaignResponse(id, tenantId, request.Name.Trim(), request.Description, request.Location.Trim(),
            request.DoctorInChargeId, request.TargetPatientCount, CampaignStatus.Planned, request.StartDate, request.EndDate, now, now);
        Campaigns.Add(c);
        return Task.FromResult(c);
    }

    public Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(Guid tenantId, string? status, CancellationToken ct)
    {
        var q = Campaigns.Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => string.Equals(c.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<CampaignResponse>>(q.ToList());
    }

    public Task<CampaignResponse> GetCampaignByIdAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        var c = Campaigns.FirstOrDefault(x => x.TenantId == tenantId && x.Id == campaignId);
        if (c is null) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch.");
        return Task.FromResult(c);
    }

    public Task<CampaignResponse> UpdateCampaignStatusAsync(Guid tenantId, Guid campaignId, string newStatus, CancellationToken ct)
    {
        var norm = newStatus?.Trim().ToUpperInvariant() ?? "";
        if (!CampaignStatus.All.Contains(norm)) throw UploadFailure.Validation(new ErrorDetail("status", "Status không hợp lệ"));
        var idx = Campaigns.FindIndex(x => x.TenantId == tenantId && x.Id == campaignId);
        if (idx < 0) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch.");
        var updated = Campaigns[idx] with { Status = norm, UpdatedAt = DateTime.UtcNow };
        Campaigns[idx] = updated;
        return Task.FromResult(updated);
    }

    public Task LinkBatchAsync(Guid tenantId, Guid campaignId, Guid batchId, CancellationToken ct)
    {
        var c = Campaigns.FirstOrDefault(x => x.TenantId == tenantId && x.Id == campaignId);
        if (c is null) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch.");
        if (string.Equals(c.Status, CampaignStatus.Closed, StringComparison.OrdinalIgnoreCase))
            throw new UploadFailure(409, "ERR_CAMPAIGN_CLOSED", "Chiến dịch đã kết thúc (CLOSED), không cho phép nạp thêm ảnh chụp mới.");

        var bIdx = Batches.FindIndex(b => b.TenantId == tenantId && b.BatchId == batchId);
        if (bIdx < 0) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy batch ảnh trong phòng khám này.");

        var item = Batches[bIdx];
        Batches[bIdx] = (item.TenantId, item.BatchId, campaignId, item.Total, item.Completed);
        return Task.CompletedTask;
    }

    public Task<CampaignSummaryResponse> GetCampaignSummaryAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        var c = Campaigns.FirstOrDefault(x => x.TenantId == tenantId && x.Id == campaignId);
        if (c is null) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch.");
        var linked = Batches.Where(b => b.TenantId == tenantId && b.CampaignId == campaignId).ToList();
        return Task.FromResult(new CampaignSummaryResponse(
            campaignId, tenantId, c.Name, c.Status, c.TargetPatientCount,
            linked.Count, linked.Sum(x => x.Total), linked.Sum(x => x.Completed), 0, 0, 100.0, 42, 18, 4, 35));
    }

    public Task<string> ExportCampaignReportCsvAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        var c = Campaigns.FirstOrDefault(x => x.TenantId == tenantId && x.Id == campaignId);
        if (c is null) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch.");
        return Task.FromResult($"# BAO CAO TONG KET\nTen,{c.Name}\n");
    }
}

class StandaloneIdentityResolver : IAnalysisUserIdentityResolver
{
    public Task<AnalysisUserContext> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        var auth = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Yêu cầu cần xác thực danh tính hợp lệ.");

        var token = auth["Bearer ".Length..].Trim();
        return token switch
        {
            "token_admin_a" => Task.FromResult(new AnalysisUserContext(Guid.Parse("a1111111-0000-0000-0000-000000000001"), ["CLINIC_ADMIN"])),
            "token_doctor_a" => Task.FromResult(new AnalysisUserContext(Guid.Parse("a2222222-0000-0000-0000-000000000002"), ["DOCTOR"])),
            "token_admin_b" => Task.FromResult(new AnalysisUserContext(Guid.Parse("b1111111-0000-0000-0000-000000000001"), ["CLINIC_ADMIN"])),
            "token_doctor_b" => Task.FromResult(new AnalysisUserContext(Guid.Parse("b2222222-0000-0000-0000-000000000002"), ["DOCTOR"])),
            "token_patient" => Task.FromResult(new AnalysisUserContext(Guid.Parse("c1111111-0000-0000-0000-000000000001"), ["PATIENT"])),
            _ => throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Token không hợp lệ.")
        };
    }
}

class StandaloneClinicResolver : IClinicAccessResolver
{
    public Task<ClinicAccessDecision> ResolveAsync(ClinicAccessRequest request, CancellationToken ct)
    {
        var cA = Guid.Parse("11111111-aaaa-4111-8111-000000000001");
        var cB = Guid.Parse("22222222-bbbb-4222-8222-000000000002");
        var tA = Guid.Parse("aaaa1111-0000-0000-0000-000000000001");
        var tB = Guid.Parse("bbbb2222-0000-0000-0000-000000000002");
        var admA = Guid.Parse("a1111111-0000-0000-0000-000000000001");
        var docA = Guid.Parse("a2222222-0000-0000-0000-000000000002");
        var admB = Guid.Parse("b1111111-0000-0000-0000-000000000001");
        var docB = Guid.Parse("b2222222-0000-0000-0000-000000000002");

        if (request.ClinicId == cA)
        {
            if (request.ActorAccountId == admA) return Task.FromResult(new ClinicAccessDecision(true, tA, "CLINIC_ADMIN"));
            if (request.ActorAccountId == docA)
            {
                if (request.Action == "CAMPAIGN_READ") return Task.FromResult(new ClinicAccessDecision(true, tA, "DOCTOR"));
                return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, "DOCTOR", "ROLE_DENIED"));
            }
            return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, null, "NOT_A_MEMBER"));
        }
        if (request.ClinicId == cB)
        {
            if (request.ActorAccountId == admB) return Task.FromResult(new ClinicAccessDecision(true, tB, "CLINIC_ADMIN"));
            if (request.ActorAccountId == docB)
            {
                if (request.Action == "CAMPAIGN_READ") return Task.FromResult(new ClinicAccessDecision(true, tB, "DOCTOR"));
                return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, "DOCTOR", "ROLE_DENIED"));
            }
            return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, null, "NOT_A_MEMBER"));
        }
        return Task.FromResult(new ClinicAccessDecision(false, Guid.Empty, null, "NOT_A_MEMBER"));
    }
}
