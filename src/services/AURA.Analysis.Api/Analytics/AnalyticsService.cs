using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Analytics;

public sealed class AnalyticsService(
    IAnalyticsAccessResolver access,
    IAnalyticsRepository repository,
    TimeProvider clock)
{
    public Task<AnalyticsSummary> GetClinicSummaryAsync(Guid actorId, Guid clinicId,
        AnalyticsWindow window, string? correlationId, CancellationToken ct)
    {
        RequireActor(actorId);
        RequireId(clinicId, "clinicId");
        return GetSummaryAsync(new(actorId, "ANALYTICS_READ", "CLINIC", clinicId, null, correlationId),
            (grant) =>
            {
                RequireMatchingClinic(grant, clinicId);
                return new AnalyticsDataScope(grant.TenantId, clinicId, null);
            }, window, ct);
    }

    public Task<AnalyticsSummary> GetAdminSummaryAsync(Guid actorId, AnalyticsWindow window,
        string? correlationId, CancellationToken ct)
    {
        RequireActor(actorId);
        return GetSummaryAsync(new(actorId, "ANALYTICS_READ", "PLATFORM", null, null, correlationId),
            grant =>
            {
                ValidateGrant(grant);
                if (grant.ClinicId is not null || grant.TenantId is not null || grant.PatientId is not null
                    || grant.AuthorizedPatientIds is not null)
                    throw NotFound();
                return new AnalyticsDataScope(null, null, null);
            }, window, ct);
    }

    public Task<AnalyticsSummary> GetDoctorSummaryAsync(Guid actorId, Guid clinicId,
        AnalyticsWindow window, string? correlationId, CancellationToken ct)
    {
        RequireActor(actorId);
        RequireId(clinicId, "clinicContextId");
        return GetSummaryAsync(new(actorId, "ANALYTICS_READ", "DOCTOR_ASSIGNMENT", clinicId, null, correlationId),
            grant =>
            {
                RequireMatchingClinic(grant, clinicId);
                if (grant.PatientId is not null || grant.AuthorizedPatientIds is null
                    || grant.AuthorizedPatientIds.Count > 500 || grant.AuthorizedPatientIds.Any(x => x == Guid.Empty))
                    throw UploadFailure.Dependency("Profile analytics scope");
                return new AnalyticsDataScope(grant.TenantId, clinicId, null,
                    grant.AuthorizedPatientIds.Distinct().ToArray(), actorId);
            }, window, ct);
    }

    public async Task<PatientAnalyticsTrends> GetPatientTrendsAsync(Guid actorId, Guid clinicId, Guid patientId,
        AnalyticsWindow window, int limit, string? correlationId, CancellationToken ct)
    {
        RequireActor(actorId);
        RequireId(clinicId, "clinicContextId");
        RequireId(patientId, "patientId");
        if (limit is < 1 or > 100) throw UploadFailure.Validation(new ErrorDetail("limit", "must_be_between_1_and_100"));
        var grant = await access.ResolveAsync(
            new(actorId, "ANALYTICS_READ", "PATIENT", clinicId, patientId, correlationId), ct);
        RequireMatchingClinic(grant, clinicId, allowPatientScope: true);
        if (grant.PatientId != patientId) throw NotFound();
        var rows = await repository.ReadPatientTrendsAsync(grant.TenantId!.Value, clinicId, patientId, window, limit, ct);
        var items = new List<AnalyticsTrendPoint>(rows.Count);
        AnalyticsTrendRow? previous = null;
        foreach (var row in rows)
        {
            var canConnect = previous is not null && Compatible(previous, row);
            items.Add(new(row.AnalysisId, row.CreatedAt, row.ModelVersion, row.ConfigVersion,
                row.ThresholdVersion, row.MeasurementUnits, row.ReviewVersion, row.Avr,
                row.UnavailableReason, row.AvrAvailable, canConnect, row.VesselDensity, row.TortuosityIndex));
            previous = row;
        }
        return new(patientId, items, null);
    }

    private async Task<AnalyticsSummary> GetSummaryAsync(AnalyticsAccessRequest request,
        Func<AnalyticsAccessGrant, AnalyticsDataScope> toScope, AnalyticsWindow window, CancellationToken ct)
    {
        var grant = await access.ResolveAsync(request, ct);
        var scope = toScope(grant);
        var row = await repository.ReadSummaryAsync(scope, window, ct);
        return new("UNIQUE_ANALYSIS", window.From, window.To, row.Total,
            new(row.Active, row.Completed, row.Failed, row.LowQuality, row.Rejected, row.Duplicate),
            "LATEST_CLINICIAN_REVIEW",
            new(row.RiskLow, row.RiskModerate, row.RiskHigh, row.RiskUnavailable, row.RiskUnreviewed),
            row.MeasurementAvailable, row.MeasurementUnavailable, row.MeasurementUnknown, clock.GetUtcNow(),
            new AnalyticsRevenueSummary("UNAVAILABLE", [], "BILLING_API_UNAVAILABLE"));
    }

    private static bool Compatible(AnalyticsTrendRow previous, AnalyticsTrendRow current) =>
        previous.AvrAvailable && current.AvrAvailable
        && HasVersion(previous.ModelVersion) && HasVersion(current.ModelVersion)
        && HasVersion(previous.ConfigVersion) && HasVersion(current.ConfigVersion)
        && HasVersion(previous.ThresholdVersion) && HasVersion(current.ThresholdVersion)
        && HasVersion(previous.MeasurementUnits) && HasVersion(current.MeasurementUnits)
        && string.Equals(previous.ModelVersion, current.ModelVersion, StringComparison.Ordinal)
        && string.Equals(previous.ConfigVersion, current.ConfigVersion, StringComparison.Ordinal)
        && string.Equals(previous.ThresholdVersion, current.ThresholdVersion, StringComparison.Ordinal)
        && string.Equals(previous.MeasurementUnits, current.MeasurementUnits, StringComparison.Ordinal);

    private static bool HasVersion(string? value) => !string.IsNullOrWhiteSpace(value);

    private static void RequireMatchingClinic(AnalyticsAccessGrant grant, Guid clinicId, bool allowPatientScope = false)
    {
        ValidateGrant(grant);
        if (grant.TenantId is null || grant.TenantId == Guid.Empty || grant.ClinicId != clinicId
            || (!allowPatientScope && grant.PatientId is not null)) throw NotFound();
    }

    private static void ValidateGrant(AnalyticsAccessGrant grant)
    {
        if (grant.PermissionVersion < 1) throw UploadFailure.Dependency("Profile analytics authority");
    }

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty) throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
    }

    private static void RequireId(Guid id, string field)
    {
        if (id == Guid.Empty) throw UploadFailure.Validation(new ErrorDetail(field, "invalid_uuid"));
    }

    private static UploadFailure NotFound() => new(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");
}
