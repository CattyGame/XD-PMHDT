using System.Globalization;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Alerts;

public sealed class AlertService(
    IAlertRepository alerts,
    IAnalyticsAccessResolver access)
{
    private const int MaximumAuthorizedAssignments = 500;
    private const int MaximumCandidates = 2000;

    public async Task<AlertsPage> ListAsync(
        Guid actorId, Guid clinicId, int limit, string? cursor, string? correlationId, CancellationToken ct)
    {
        if (actorId == Guid.Empty || clinicId == Guid.Empty)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản hợp lệ.");
        if (limit is < 1 or > 100)
            throw UploadFailure.Validation(new ErrorDetail("limit", "must_be_between_1_and_100"));

        Guid? after = ParseCursor(cursor);
        var grant = await ResolveClinicOrDoctorAsync(actorId, clinicId, null, correlationId, ct);
        var candidates = await alerts.ReadCandidatesAsync(
            grant.TenantId!.Value, clinicId, grant.AuthorizedPatientIds, MaximumCandidates + 1, ct);
        if (candidates.Count > MaximumCandidates)
            throw UploadFailure.Dependency("bounded alert query");

        var sources = candidates.SelectMany(x => AlertSourceRules.Derive(x.Facts)).ToArray();
        await alerts.MaterializeSourcesAsync(clinicId, actorId, sources, correlationId ?? string.Empty, ct);

        var states = await alerts.ReadStatesAsync(grant.TenantId.Value, clinicId,
            sources.Select(x => x.AnalysisId).Distinct().ToArray(), ct);
        var stateBySource = states.ToDictionary(x => x.Source);
        var items = sources.Select(source =>
            
            
                ToResponse(source, stateBySource.TryGetValue(source, out var state) ? state : null))
            .Where(item => after is null || item.AlertId.CompareTo(after.Value) > 0)
            .OrderBy(item => item.AlertId)
            .Take(limit + 1)
            .ToArray();

        var hasMore = items.Length > limit;
        var page = items.Take(limit).ToArray();
        return new AlertsPage(page, hasMore && page.Length > 0 ? FormatCursor(page[^1].AlertId) : null);
    }

    public async Task<AlertResponse> ChangeStateAsync(
        Guid actorId, Guid clinicId, Guid alertId, AlertStateChangeRequest request,
        string? correlationId, CancellationToken ct)
    {
        AlertStateChangeValidator.Validate(request);
        if (actorId == Guid.Empty || clinicId == Guid.Empty || alertId == Guid.Empty)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy alert.");

        // This metadata-only lookup recovers the patient scope needed for the Profile decision.
        var scope = await FindAlertScopeAsync(alertId, ct)
            ?? throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy alert.");
        if (scope.ClinicId != clinicId)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy alert.");

        var clinicGrant = await TryClinicGrantAsync(actorId, clinicId, correlationId, ct);
        AnalyticsAccessGrant grant;
        if (clinicGrant is not null)
        {
            grant = clinicGrant;
        }
        else
        {
            grant = await ResolvePatientAsync(actorId, clinicId, scope.PatientId, correlationId, ct);
        }
        if (grant.TenantId != scope.TenantId)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy alert.");

        var patientIds = grant.AuthorizedPatientIds;
        if (patientIds is { Count: >= MaximumAuthorizedAssignments })
            throw UploadFailure.Dependency("complete alert assignment scope");
        var candidates = await alerts.ReadCandidatesAsync(grant.TenantId!.Value, clinicId, patientIds, 500, ct);
        var current = candidates.SelectMany(candidate => AlertSourceRules.Derive(candidate.Facts))
            .FirstOrDefault(source => AlertSourceRules.ToAlertId(source) == alertId);
        if (current is null)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy alert.");

        var state = await alerts.ChangeStateAsync(clinicId, actorId, current,
            request.ExpectedVersion, request.Status, request.Reason.Trim(), correlationId ?? string.Empty, ct);
        return ToResponse(current, state);
    }

    private async Task<AnalyticsAccessGrant> ResolveClinicOrDoctorAsync(
        Guid actorId, Guid clinicId, Guid? patientId, string? correlationId, CancellationToken ct)
    {
        var clinic = await TryClinicGrantAsync(actorId, clinicId, correlationId, ct);
        if (clinic is not null) return clinic;
        var doctorGrant = await access.ResolveAsync(new AnalyticsAccessRequest(
            actorId, "ANALYTICS_READ", "DOCTOR_ASSIGNMENT", clinicId, patientId, correlationId), ct);
        ValidateGrant(doctorGrant, clinicId);
        if (doctorGrant.AuthorizedPatientIds is null || doctorGrant.AuthorizedPatientIds.Count == 0
            || doctorGrant.AuthorizedPatientIds.Count >= MaximumAuthorizedAssignments)
            throw UploadFailure.Dependency("complete alert assignment scope");
        return doctorGrant;
    }

    private async Task<AnalyticsAccessGrant?> TryClinicGrantAsync(
        Guid actorId, Guid clinicId, string? correlationId, CancellationToken ct)
    {
        try
        {
            var grant = await access.ResolveAsync(new AnalyticsAccessRequest(
                actorId, "ANALYTICS_READ", "CLINIC", clinicId, null, correlationId), ct);
            ValidateGrant(grant, clinicId);
            return grant;
        }
        catch (UploadFailure failure) when (IsRoleDenial(failure))
        {
            return null;
        }
    }

    private async Task<AnalyticsAccessGrant> ResolvePatientAsync(
        Guid actorId, Guid clinicId, Guid patientId, string? correlationId, CancellationToken ct)
    {
        var grant = await access.ResolveAsync(new AnalyticsAccessRequest(
            actorId, "ANALYTICS_READ", "PATIENT", clinicId, patientId, correlationId), ct);
        ValidateGrant(grant, clinicId);
        return grant;
    }

    private static void ValidateGrant(AnalyticsAccessGrant grant, Guid clinicId)
    {
        if (grant.TenantId is null || grant.TenantId == Guid.Empty || grant.ClinicId != clinicId)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên trong phạm vi được phép.");
    }

    private static bool IsRoleDenial(UploadFailure failure) =>
        failure.Status == 403 && failure.Details.Any(x => x.Field == "authority" && x.Issue == "ROLE_DENIED");

    private static AlertResponse ToResponse(AlertSource source, AlertStateRecord? state) => new(
        AlertSourceRules.ToAlertId(source), source.AnalysisId, source.ResultId, source.ReviewVersion ?? 0,
        source.RuleVersion, source.Kind, state?.Status ?? "OPEN", state?.Version ?? 1);

    private static string? FormatCursor(Guid id) => Convert.ToBase64String(id.ToByteArray());

    private static Guid? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var bytes = Convert.FromBase64String(cursor);
            if (bytes.Length != 16) throw new FormatException();
            return new Guid(bytes);
        }
        catch (FormatException)
        {
            throw UploadFailure.Validation(new ErrorDetail("cursor", "invalid_cursor"));
        }
    }

    // Alert scope lookup is supplied by the repository and only returns non-content authorization metadata.
    private Task<AlertScope?> FindAlertScopeAsync(Guid alertId, CancellationToken ct) =>
        alerts is IAlertScopeRepository scoped ? scoped.FindScopeAsync(alertId, ct) : Task.FromResult<AlertScope?>(null);

}

public sealed record AlertScope(Guid TenantId, Guid ClinicId, Guid PatientId, Guid AnalysisId);

public interface IAlertScopeRepository
{
    Task<AlertScope?> FindScopeAsync(Guid alertId, CancellationToken ct);
}
