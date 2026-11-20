using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Alerts;

public sealed record AlertSourceFacts(
    Guid TenantId,
    Guid AnalysisId,
    Guid ResultId,
    int? ReviewVersion,
    string ProcessingStatus,
    string? ResultRiskLevel,
    string? CorrectedRiskLevel,
    bool HasCurrentReview,
    string? MeasurementStatus);

public sealed record AlertSource(
    Guid TenantId,
    Guid AnalysisId,
    Guid ResultId,
    int? ReviewVersion,
    int RuleVersionNumber,
    string RuleVersion,
    string Kind);

public sealed record AlertCandidate(
    Guid TenantId,
    Guid ClinicId,
    Guid PatientId,
    Guid AnalysisId,
    Guid ResultId,
    int ResultVersion,
    int? ReviewVersion,
    string ProcessingStatus,
    string? ResultRiskLevel,
    string? CorrectedRiskLevel,
    bool HasCurrentReview,
    string? MeasurementStatus,
    DateTimeOffset CreatedAt)
{
    public AlertSourceFacts Facts => new(TenantId, AnalysisId, ResultId, ReviewVersion,
        ProcessingStatus, ResultRiskLevel, CorrectedRiskLevel, HasCurrentReview, MeasurementStatus);
}

public sealed record AlertStateRecord(AlertSource Source, string Status, int Version, string? Reason);

public interface IAlertRepository
{
    Task<IReadOnlyList<AlertCandidate>> ReadCandidatesAsync(
        Guid tenantId, Guid clinicId, IReadOnlyCollection<Guid>? patientIds, int limit, CancellationToken ct);
    Task MaterializeSourcesAsync(Guid clinicId, Guid actorId, IReadOnlyCollection<AlertSource> sources,
        string correlationId, CancellationToken ct);
    Task<IReadOnlyList<AlertStateRecord>> ReadStatesAsync(
        Guid tenantId, Guid clinicId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct);
    Task<AlertStateRecord> ChangeStateAsync(
        Guid clinicId, Guid actorId, AlertSource source, int expectedVersion, string targetStatus,
        string reason, string correlationId, CancellationToken ct);
}

public static class AlertSourceRules
{
    public const int CurrentRuleVersionNumber = 1;
    public const string CurrentRuleVersion = "scrum385-v1";
    public const string ClinicianReviewHigh = "CLINICIAN_REVIEW_HIGH";
    public const string ResearchNotice = "RESEARCH_NOTICE";

    public static IReadOnlyList<AlertSource> Derive(AlertSourceFacts facts)
    {
        if (facts.TenantId == Guid.Empty || facts.AnalysisId == Guid.Empty || facts.ResultId == Guid.Empty)
            return [];

        var sources = new List<AlertSource>(capacity: 2);
        var completed = string.Equals(facts.ProcessingStatus, "COMPLETED", StringComparison.OrdinalIgnoreCase);
        var lowQuality = string.Equals(facts.ProcessingStatus, "LOW_QUALITY", StringComparison.OrdinalIgnoreCase);

        var effectiveRisk = string.IsNullOrWhiteSpace(facts.CorrectedRiskLevel)
            ? facts.ResultRiskLevel
            : facts.CorrectedRiskLevel;

        if (completed && !lowQuality && facts.HasCurrentReview &&
            string.Equals(effectiveRisk?.Trim(), "HIGH", StringComparison.OrdinalIgnoreCase))
        {
            sources.Add(Create(facts, ClinicianReviewHigh));
        }

        if (string.Equals(facts.MeasurementStatus?.Trim(), "AVAILABLE", StringComparison.OrdinalIgnoreCase))
        {
            sources.Add(Create(facts, ResearchNotice));
        }

        return sources;
    }

    public static Guid ToAlertId(AlertSource source)
    {
        var canonical = string.Join('|',
            source.TenantId.ToString("N"),
            source.AnalysisId.ToString("N"),
            source.ResultId.ToString("N"),
            source.ReviewVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            source.RuleVersionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.Kind);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static AlertSource Create(AlertSourceFacts facts, string kind) => new(
        facts.TenantId,
        facts.AnalysisId,
        facts.ResultId,
        facts.ReviewVersion,
        CurrentRuleVersionNumber,
        CurrentRuleVersion,
        kind);
}

public sealed record AlertResponse(
    [property: JsonPropertyName("alertId")] Guid AlertId,
    [property: JsonPropertyName("analysisId")] Guid AnalysisId,
    [property: JsonPropertyName("resultId")] Guid ResultId,
    [property: JsonPropertyName("reviewVersion")] int ReviewVersion,
    [property: JsonPropertyName("ruleVersion")] string RuleVersion,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] int Version);

public sealed record AlertsPage(
    [property: JsonPropertyName("items")] IReadOnlyList<AlertResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed record AlertStateChangeRequest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expectedVersion")] int ExpectedVersion,
    [property: JsonPropertyName("reason")] string Reason);

public static class AlertStateChangeValidator
{
    public static void Validate(AlertStateChangeRequest request)
    {
        if (request.Status is not ("ACKNOWLEDGED" or "CLOSED"))
            throw UploadFailure.Validation(new ErrorDetail("status", "must_be_acknowledged_or_closed"));
        if (request.ExpectedVersion < 1)
            throw UploadFailure.Validation(new ErrorDetail("expectedVersion", "must_be_positive"));
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1000)
            throw UploadFailure.Validation(new ErrorDetail("reason", "required_max_length_1000"));
    }
}
