using System.Text.Json.Serialization;

namespace AURA.Analysis.Api.Upload;

public sealed record PatientHistoryItem(Guid AnalysisId, Guid BatchId, string ProcessingStatus,
    string ReviewStatus, DateTime CreatedAt);
public sealed record PatientHistoryResponse(Guid PatientId, IReadOnlyList<PatientHistoryItem> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? NextBeforeId);

public interface IPatientHistoryRepository
{
    Task<IReadOnlyList<PatientHistoryItem>> ListAsync(Guid tenantId, Guid clinicId, Guid patientId,
        int limit, Guid? beforeId, CancellationToken ct);
}

public sealed class PatientHistoryService(IPatientHistoryRepository repository, IPatientAccessResolver access)
{
    public async Task<PatientHistoryResponse> GetAsync(Guid actor, Guid clinic, Guid patient, int limit,
        Guid? beforeId, string? correlationId, CancellationToken ct)
    {
        if (actor == Guid.Empty) throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
        if (patient == Guid.Empty) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");
        if (clinic == Guid.Empty || limit is < 1 or > 100 || beforeId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("history", "invalid_selector_or_page"));
        // No analysisId: a case-only assignment must not grant longitudinal history.
        var decision = await access.ResolveAsync(new(actor, clinic, patient, "ANALYSIS_READ")
            { CorrelationId = correlationId }, ct);
        if (!decision.Allowed || decision.TenantId == Guid.Empty || decision.TenantKind != "CLINIC" || decision.OwnerPrincipalId != clinic)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");
        var rows = await repository.ListAsync(decision.TenantId, clinic, patient, limit + 1, beforeId, ct);
        return new(patient, rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].AnalysisId : null);
    }
}
