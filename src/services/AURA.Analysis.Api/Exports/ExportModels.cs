using System.Text.Json.Serialization;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Exports;

public sealed record ResearchExportRequest(
    [property: JsonPropertyName("clinicContextId")] Guid ClinicContextId,
    [property: JsonPropertyName("campaignId")] Guid? CampaignId,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonRequired, JsonPropertyName("includeMasks")] bool IncludeMasks);

public sealed record CampaignReportRequest(
    [property: JsonPropertyName("format")] string Format);

public sealed record ExportReceipt(
    [property: JsonPropertyName("reportId")] Guid ReportId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("statusUrl")] string StatusUrl,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);

public sealed record ResearchExportRow(
    string CaseCode,
    string? RiskLevel,
    bool MeasurementAvailable,
    decimal? Avr);

public sealed record ExportSnapshot(
    Guid Id, Guid TenantId, Guid ClinicId, Guid ActorId, string Kind, Guid? CampaignId,
    string Format, string ObjectKey, string ContentType, string Sha256, string Fingerprint, string Purpose,
    string SnapshotJson, string IdempotencyKey, IReadOnlyList<Guid> PatientScope, DateTimeOffset ExpiresAt, string CorrelationId);

public sealed record ResearchSourceRow(Guid PatientId, Guid? ResultId, string? ModelVersion, string? ConfigVersion,
    string? ThresholdVersion, int ReviewVersion, string? RiskLevel, bool MeasurementAvailable, decimal? Avr);

public static class ResearchExportRequestValidator
{
    public static void Validate(ResearchExportRequest request)
    {
        if (request.ClinicContextId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "required_uuid"));
        if (request.CampaignId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("campaignId", "invalid_uuid"));
        if (string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Trim().Length > 500)
            throw UploadFailure.Validation(new ErrorDetail("purpose", "required_max_length_500"));
        if (!string.Equals(request.Format, "CSV", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Format, "PDF", StringComparison.OrdinalIgnoreCase))
            throw UploadFailure.Validation(new ErrorDetail("format", "must_be_csv_or_pdf"));
    }
}
