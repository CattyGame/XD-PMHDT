using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AURA.Analysis.Api.Upload;

public sealed record DoctorReviewRequest(
    bool IsApproved,
    int BaseVersion,
    string? CorrectedRiskLevel = null,
    string? Reason = null,
    string? DoctorNotes = null);

public sealed record DoctorReviewDto(
    Guid ReviewId,
    Guid AnalysisId,
    int Version,
    bool IsApproved,
    string? CorrectedRiskLevel,
    string? DoctorNotes,
    Guid DoctorId,
    DateTimeOffset CreatedAt,
    string? Reason,
    int AiResultVersion);

public sealed record DoctorReviewCommand(
    Guid AnalysisId,
    Guid TenantId,
    Guid PatientId,
    Guid DoctorId,
    int BaseVersion,
    bool IsApproved,
    string? CorrectedRiskLevel,
    string? Reason,
    string? DoctorNotes,
    string CorrelationId);

public interface IDoctorReviewRepository
{
    Task<DoctorReviewDto> SubmitAsync(DoctorReviewCommand command, CancellationToken ct);
}

public sealed class DoctorReviewService(
    IAnalysisQueryRepository analyses,
    IPatientAccessResolver access,
    IDoctorReviewRepository reviews)
{
    public async Task<DoctorReviewDto> SubmitAsync(Guid doctorId, Guid? clinicId, Guid analysisId,
        DoctorReviewRequest request, string correlationId, CancellationToken ct)
    {
        Validate(request);
        var identity = await analyses.FindIdentityAsync(analysisId, ct)
            ?? throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

        var decision = await access.ResolveAsync(new PatientAccessRequest(
            doctorId, clinicId, identity.PatientId, "DOCTOR_REVIEW", analysisId)
        {
            CorrelationId = correlationId
        }, ct);
        if (!decision.Allowed || decision.TenantId != identity.TenantId || decision.OwnerPrincipalId != identity.OwnerId)
            throw new UploadFailure(403, "ERR_FORBIDDEN", "Bác sĩ chưa được phân công cho ca bệnh này.");

        return await reviews.SubmitAsync(new DoctorReviewCommand(
            analysisId, identity.TenantId, identity.PatientId, doctorId, request.BaseVersion,
            request.IsApproved, request.CorrectedRiskLevel?.Trim().ToUpperInvariant(),
            request.Reason?.Trim(), request.DoctorNotes?.Trim(), correlationId), ct);
    }

    private static void Validate(DoctorReviewRequest request)
    {
        if (request.BaseVersion < 0)
            throw UploadFailure.Validation(new ErrorDetail("baseVersion", "must_be_non_negative"));
        if (request.DoctorNotes?.Length > 2000)
            throw UploadFailure.Validation(new ErrorDetail("doctorNotes", "max_length_2000"));
        if (request.IsApproved)
        {
            if (request.CorrectedRiskLevel is not null || request.Reason is not null)
                throw UploadFailure.Validation(new ErrorDetail("review", "approval_cannot_include_correction"));
            return;
        }
        var level = request.CorrectedRiskLevel?.Trim().ToUpperInvariant();
        if (level is not ("LOW" or "MODERATE" or "HIGH"))
            throw UploadFailure.Validation(new ErrorDetail("correctedRiskLevel", "required_valid_risk_level"));
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000)
            throw UploadFailure.Validation(new ErrorDetail("reason", "required_max_length_1000"));
    }
}

public sealed class ProfileDoctorAssignmentClient(IHttpClientFactory clients) : IDoctorAssignmentClient
{
    public async Task<IReadOnlyList<Guid>> GetActivePatientIdsAsync(HttpRequest request, Guid clinicId,
        Guid doctorId, string? correlationId, CancellationToken ct)
    {
        if (clinicId == Guid.Empty || doctorId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "invalid_uuid"));
        var auth = request.Headers.Authorization;
        if (auth.Count != 1 || !AuthenticationHeaderValue.TryParse(auth[0], out var bearer)
            || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(bearer.Parameter))
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần token người dùng hợp lệ.");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/clinics/{clinicId:D}/assignments?status=ACTIVE");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Parameter);
            ServiceHttp.AddCorrelation(message, correlationId);
            using var response = await clients.CreateClient("ProfileAssignments")
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần token người dùng hợp lệ.");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new UploadFailure(403, "ERR_FORBIDDEN", "Không có quyền xem danh sách phân công.");
            if (response.StatusCode != HttpStatusCode.OK)
                throw UploadFailure.Dependency("Profile assignments");
            if (response.Content.Headers.ContentLength is > 262144)
                throw UploadFailure.Dependency("Profile assignments");
            var body = await response.Content.ReadFromJsonAsync<AssignmentResponse>(cancellationToken: ct);
            if (body?.Items is null || body.Items.Count > 500)
                throw UploadFailure.Dependency("Profile assignments");
            return body.Items
                .Where(x => x.Status == "ACTIVE" && x.DoctorId == doctorId && x.PatientId != Guid.Empty)
                .Select(x => x.PatientId).Distinct().ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UploadFailure) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            throw UploadFailure.Dependency("Profile assignments");
        }
    }

    private sealed record AssignmentResponse(List<AssignmentItem>? Items);
    private sealed record AssignmentItem(Guid DoctorId, Guid PatientId, string? Status);
}

public interface IDoctorAssignmentClient
{
    Task<IReadOnlyList<Guid>> GetActivePatientIdsAsync(HttpRequest request, Guid clinicId,
        Guid doctorId, string? correlationId, CancellationToken ct);
}

public sealed record DoctorWorklistResponse(IReadOnlyList<AnalysisDetailResponse> Items);

public sealed class DoctorWorklistService(
    IDoctorAssignmentClient assignments,
    IDoctorWorklistRepository worklist,
    IPatientAccessResolver access,
    AnalysisQueryService analyses)
{
    public async Task<DoctorWorklistResponse> GetAsync(HttpRequest request, Guid doctorId, Guid clinicId,
        CancellationToken ct, string? correlationId)
    {
        var patientIds = await assignments.GetActivePatientIdsAsync(request, clinicId, doctorId, correlationId, ct);
        var items = new List<AnalysisDetailResponse>();
        foreach (var patientId in patientIds.Take(200))
        {
            if (items.Count >= 200) break;
            var decision = await access.ResolveAsync(new PatientAccessRequest(doctorId, clinicId, patientId, "DOCTOR_REVIEW")
            {
                CorrelationId = correlationId
            }, ct);
            if (!decision.Allowed || decision.OwnerPrincipalId != clinicId) continue;
            var identities = await worklist.ListDoctorWorklistAsync(decision.TenantId, patientId, ct);
            foreach (var identity in identities.Take(200 - items.Count))
                items.Add(await analyses.GetAsync(doctorId, clinicId, identity.AnalysisId, ct,
                    correlationId, "DOCTOR_REVIEW"));
        }
        return new(items.OrderBy(x => x.CreatedAt).ToArray());
    }
}
