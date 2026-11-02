using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AURA.Analysis.Api.Upload;

public sealed record AnalysisIdentity(Guid AnalysisId, Guid BatchId, Guid TenantId, Guid PatientId, Guid OwnerId);

public sealed record AnalysisJobDetail(Guid JobId, string Status, int RetryCount, int AttemptNo);
public sealed record AnalysisImageRef(Guid FileId, string Kind = "original");
public sealed record AnalysisAnnotationRef(Guid FileId, string Kind);
public sealed record AnalysisWarningDetail(string Code, string Message);
public sealed record AnalysisMetricsDetail(double? VesselDensity, double? TortuosityIndex, double? AvRatio, double? FractalDimension, int? BranchingPoints);

public sealed record AnalysisResultDetail(
    bool RiskAvailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RiskLevel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? RiskScore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? ConfidenceScore,
    bool IsMock,
    string ModelVersion,
    string ConfigVersion,
    string ThresholdVersion,
    string Disclaimer,
    int Version,
    AnalysisMetricsDetail Metrics,
    IReadOnlyList<string> Indicators,
    IReadOnlyList<AnalysisAnnotationRef> Annotations,
    IReadOnlyList<AnalysisWarningDetail> Warnings,
    double ProcessingTimeMs,
    string Limitations);

public sealed record AnalysisErrorDetail(string Code, string Message, string? CorrelationId, bool Retryable);

public sealed record AnalysisDetailResponse(
    Guid AnalysisId,
    Guid BatchId,
    Guid PatientId,
    string ProcessingStatus,
    string ReviewStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid OwnerId,
    Guid RequesterId,
    AnalysisImageRef Image,
    AnalysisJobDetail? Job,
    AnalysisResultDetail? Result,
    AnalysisErrorDetail? Error,
    Guid? ExistingAnalysisId = null,
    int ReviewVersion = 0);

public interface IAnalysisQueryRepository
{
    Task<AnalysisIdentity?> FindIdentityAsync(Guid analysisId, CancellationToken ct);
    Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity identity, string? correlationId, CancellationToken ct);
}

public interface IDoctorWorklistRepository
{
    Task<IReadOnlyList<AnalysisIdentity>> ListDoctorWorklistAsync(Guid tenantId, Guid patientId, CancellationToken ct);
}

public sealed class AnalysisQueryService(IAnalysisQueryRepository repository, IPatientAccessResolver access)
{
    private static UploadFailure NotFound() => new(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

    public async Task<AnalysisDetailResponse> GetAsync(Guid requesterId, Guid? clinicId, Guid analysisId,
        CancellationToken ct, string? correlationId = null, string action = "ANALYSIS_READ")
    {
        ct.ThrowIfCancellationRequested();
        if (requesterId == Guid.Empty) throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
        if (clinicId == Guid.Empty) throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "invalid_uuid"));
        if (analysisId == Guid.Empty) throw NotFound();

        try
        {
            var identity = await repository.FindIdentityAsync(analysisId, ct);
            if (identity is null) throw NotFound();

            var decision = await access.ResolveAsync(new PatientAccessRequest(
                requesterId, clinicId, identity.PatientId, action, analysisId)
            {
                CorrelationId = correlationId
            }, ct);

            if (!decision.Allowed || decision.TenantId != identity.TenantId || decision.OwnerPrincipalId != identity.OwnerId)
            {
                if (action == "DOCTOR_REVIEW")
                    throw new UploadFailure(403, "ERR_FORBIDDEN", "Bác sĩ chưa được phân công cho ca bệnh này.");
                throw NotFound();
            }

            var result = await repository.ReadAsync(identity, correlationId, ct);
            if (result is null) throw NotFound();
            return result;
        }
        catch (UploadFailure) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw UploadFailure.Dependency("analysis read dependencies"); }
    }
}
