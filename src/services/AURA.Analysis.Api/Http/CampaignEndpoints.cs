using System.Text;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Http;

public static class CampaignEndpoints
{
    public static void MapCampaignEndpoints(this WebApplication app)
    {
        // 1. Tạo chiến dịch mới (D2: Authoritative tenant isolation)
        app.MapPost("/api/v1/campaigns", (HttpContext c, CreateCampaignRequest request) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_MANAGE");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            var response = await repo.CreateCampaignAsync(tenantId, request, c.RequestAborted);
            return Results.Json(response, UploadJson.Options, statusCode: StatusCodes.Status201Created);
        }));

        // 2. Danh sách chiến dịch của phòng khám
        app.MapGet("/api/v1/campaigns", (HttpContext c, string? status) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_READ");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            var response = await repo.GetCampaignsAsync(tenantId, status, c.RequestAborted);
            return Results.Json(response, UploadJson.Options);
        }));

        // 3. Chi tiết chiến dịch
        app.MapGet("/api/v1/campaigns/{campaignId}", (HttpContext c, string campaignId) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_READ");
            var campId = ParseGuid(campaignId, "campaignId");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            var response = await repo.GetCampaignByIdAsync(tenantId, campId, c.RequestAborted);
            return Results.Json(response, UploadJson.Options);
        }));

        // 4. Cập nhật trạng thái chiến dịch
        app.MapPatch("/api/v1/campaigns/{campaignId}/status", (HttpContext c, string campaignId, UpdateCampaignStatusRequest request) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_MANAGE");
            var campId = ParseGuid(campaignId, "campaignId");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            var response = await repo.UpdateCampaignStatusAsync(tenantId, campId, request.Status, c.RequestAborted);
            return Results.Json(response, UploadJson.Options);
        }));

        // 5. Gắn batch ảnh vào chiến dịch (AC1 & AC3: chặn nếu CLOSED)
        app.MapPost("/api/v1/campaigns/{campaignId}/batches", (HttpContext c, string campaignId, LinkBatchToCampaignRequest request) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_MANAGE");
            var campId = ParseGuid(campaignId, "campaignId");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            await repo.LinkBatchAsync(tenantId, campId, request.BatchId, c.RequestAborted);
            return Results.Json(new { message = "Liên kết batch vào chiến dịch tầm soát thành công." }, UploadJson.Options);
        }));

        // 6. Tổng hợp thống kê tiến độ & lâm sàng (AC2)
        app.MapGet("/api/v1/campaigns/{campaignId}/summary", (HttpContext c, string campaignId) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_READ");
            var campId = ParseGuid(campaignId, "campaignId");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            var response = await repo.GetCampaignSummaryAsync(tenantId, campId, c.RequestAborted);
            return Results.Json(response, UploadJson.Options);
        }));

        // 7. Kết xuất dữ liệu báo cáo y tế CSV
        app.MapGet("/api/v1/campaigns/{campaignId}/export", (HttpContext c, string campaignId) => Handle(c, async () =>
        {
            var tenantId = await ResolveClinicTenantIdAsync(c, "CAMPAIGN_READ");
            var campId = ParseGuid(campaignId, "campaignId");
            var repo = c.RequestServices.GetRequiredService<CampaignRepository>();
            var csv = await repo.ExportCampaignReportCsvAsync(tenantId, campId, c.RequestAborted);

            c.Response.Headers.ContentDisposition = $"attachment; filename=screening_campaign_{campId:D}_summary.csv";
            return Results.Text(csv, "text/csv", Encoding.UTF8);
        }));
    }

    private static async Task<Guid> ResolveClinicTenantIdAsync(HttpContext c, string operation)
    {
        // 1. D2 ADR-005: Authenticate user authoritatively from Bearer token
        var resolver = c.RequestServices.GetService<IAnalysisUserIdentityResolver>();
        if (resolver == null)
        {
            throw new UploadFailure(503, "ERR_SERVICE_UNAVAILABLE", "Dịch vụ xác thực danh tính chưa sẵn sàng.");
        }

        var user = await resolver.ResolveAsync(c.Request, c.RequestAborted);
        if (user == null || user.AccountId == Guid.Empty)
        {
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Yêu cầu cần xác thực danh tính hợp lệ.");
        }

        // 2. D2 ADR-005: Chặn tin cậy header X-Clinic-Context-Id do client gửi trực tiếp
        // Chỉ chấp nhận clinic selector qua query param, sau đó server phải xác thực context
        Guid? clinicSelector = null;
        if (c.Request.Query.TryGetValue("clinicContextId", out var qVal)
            && Guid.TryParse(qVal.FirstOrDefault(), out var qId) && qId != Guid.Empty)
        {
            clinicSelector = qId;
        }
        else if (c.Request.Query.TryGetValue("clinicId", out var qVal2)
            && Guid.TryParse(qVal2.FirstOrDefault(), out var qId2) && qId2 != Guid.Empty)
        {
            clinicSelector = qId2;
        }

        if (clinicSelector is null || clinicSelector.Value == Guid.Empty)
        {
            throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "Ngữ cảnh phòng khám (clinicContextId) là bắt buộc."));
        }

        // 3. Fail-closed: Phân giải tenant authoritative qua Profile Client (R01/N04)
        // ADR-005 D2: Không bypass Profile cho SYSTEM_ADMIN; phân giải clinicId -> tenantId authoritatively.
        // Quyền quản trị clinic (CLINIC_ADMIN, DOCTOR, STAFF) do Profile membership giải quyết, không dựa vào JWT global roles.
        var profileClient = c.RequestServices.GetService<IPatientAccessResolver>();
        if (profileClient == null)
        {
            throw new UploadFailure(503, "ERR_SERVICE_UNAVAILABLE", "Dịch vụ xác thực phòng khám chưa sẵn sàng.");
        }

        var correlationId = c.Items["CorrelationId"] as string;
        bool isMutate = operation == "CAMPAIGN_MANAGE";
        string action = isMutate ? "CAMPAIGN_MANAGE" : "ANALYSIS_READ";

        AccessDecision decision;
        try
        {
            decision = await profileClient.ResolveAsync(new PatientAccessRequest(
                user.AccountId,
                clinicSelector.Value,
                user.AccountId,
                action)
            {
                CorrelationId = correlationId
            }, c.RequestAborted);
        }
        catch (UploadFailure) { throw; }
        catch (Exception)
        {
            // Fail-closed! Bất kỳ lỗi kết nối Profile đều trả 503, tuyệt đối KHÔNG fallback sang selector
            throw new UploadFailure(503, "ERR_SERVICE_UNAVAILABLE", "Không thể xác minh quyền phòng khám lúc này.");
        }

        if (decision == null || !decision.Allowed || decision.TenantId == Guid.Empty)
        {
            throw new UploadFailure(403, "ERR_FORBIDDEN", "Không có quyền truy cập vào phòng khám này.");
        }

        return decision.TenantId;
    }

    private static Guid ParseGuid(string raw, string field)
    {
        return Guid.TryParse(raw, out var id) && id != Guid.Empty
            ? id
            : throw UploadFailure.Validation(new ErrorDetail(field, "Định dạng UUID không hợp lệ."));
    }

    private static async Task<IResult> Handle(HttpContext c, Func<Task<IResult>> action)
    {
        var header = c.Request.Headers["X-Correlation-Id"];
        var candidate = header.Count == 1 ? header[0] : null;
        var id = candidate is { Length: > 0 and <= 64 } && candidate.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-')
            ? candidate : Guid.NewGuid().ToString("D");
        c.Items["CorrelationId"] = id;
        c.Response.Headers["X-Correlation-Id"] = id;
        c.Response.Headers.CacheControl = "no-store";

        try
        {
            return await action();
        }
        catch (UploadFailure e)
        {
            return Results.Json(new
            {
                error = new
                {
                    code = e.Code,
                    message = e.Message,
                    correlationId = id,
                    retryable = e.Retryable,
                    details = e.Details
                }
            }, UploadJson.Options, statusCode: e.Status);
        }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Json(new
            {
                error = new
                {
                    code = "ERR_NETWORK",
                    message = "Dịch vụ gặp sự cố kết nối cơ sở dữ liệu.",
                    correlationId = id,
                    retryable = true
                }
            }, UploadJson.Options, statusCode: 503);
        }
    }
}
