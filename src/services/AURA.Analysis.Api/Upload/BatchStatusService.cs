using System.Collections.ObjectModel;
using AURA.Common.Cache;

namespace AURA.Analysis.Api.Upload;

// Internal binding is never an HTTP response. Profile must authorize the patient in this tenant on every read.
public sealed record BatchIdentity(Guid BatchId, Guid TenantId, Guid PatientId, Guid OwnerId);
public sealed record BatchJobStatus(Guid JobId, string Status, int RetryCount, int AttemptNo);
public sealed record BatchStatusRow(Guid AnalysisId, string? FileName, string ProcessingStatus, string ReviewStatus,
    BatchJobStatus? Job, bool JobRetryable, string? ErrorCode, string? RiskLevel, Guid? ExistingAnalysisId = null);
public sealed record BatchStatusSnapshot(Guid BatchId, DateTime CreatedAt, IReadOnlyList<BatchStatusRow> Items);

public interface IBatchStatusRepository
{
    Task<BatchIdentity?> FindIdentityAsync(Guid batchId, CancellationToken ct);
    Task<BatchStatusSnapshot?> ReadAsync(BatchIdentity identity, CancellationToken ct);
}

public sealed record BatchPublicError(string Code, string Message, bool Retryable, int? RetryCount);
public sealed record BatchPublicItem(Guid AnalysisId, string? FileName, string ProcessingStatus, string ReviewStatus,
    string? RiskLevel, BatchPublicError? Error, BatchJobStatus? Job, Guid? ExistingAnalysisId = null);
public sealed record BatchPublicSummary(int Total, int Active, int Succeeded, int Failed, int Duplicate,
    IReadOnlyDictionary<string, int> ByStatus);
public sealed record BatchStatusResponse(Guid BatchId, IReadOnlyList<BatchPublicItem> Items, DateTime CreatedAt,
    string BatchStatus, BatchPublicSummary Summary)
{
    public int? TotalDeclared { get; init; }
    public int? AdmissionRejected { get; init; }
    public int? UploadPending { get; init; }
}

/// <summary>Current Batch/BatchSummary rules in Analysis REST 0.6. A FAILED retry waiting for another attempt is active.</summary>
public static class BatchStatusProjection
{
    public static readonly IReadOnlyList<string> ProcessingStatuses = Array.AsReadOnly(new[]
        { "PENDING", "QUEUED", "PROCESSING", "COMPLETED", "FAILED", "RETRY_READY", "LOW_QUALITY", "REJECTED", "DUPLICATE" });
    private static readonly string[] ReviewStatuses = ["NOT_REQUIRED", "PENDING", "IN_REVIEW", "REVIEWED", "CORRECTION_REQUIRED"];
    private static readonly string[] JobStatuses = ["QUEUED", "RUNNING", "SUCCEEDED", "FAILED", "RETRY_READY", "DEAD_LETTER"];
    private static readonly HashSet<string> ErrorCodes = new(StringComparer.Ordinal)
    {
        "ERR_AI_FAILED", "ERR_TIMEOUT", "ERR_NETWORK", "ERR_IMAGE_LOW_QUALITY", "ERR_IMAGE_MISSING",
        "ERR_INSUFFICIENT_CREDIT", "ERR_UNAUTHORIZED", "ERR_DUPLICATE_REQUEST", "ERR_VALIDATION",
        "ERR_UNSUPPORTED_MODALITY", "ERR_CONCURRENT_REVIEW", "ERR_IDEMPOTENCY_KEY_REUSED"
    };

    public static BatchStatusResponse Create(BatchStatusSnapshot snapshot)
    {
        if (snapshot.BatchId == Guid.Empty || snapshot.CreatedAt.Kind != DateTimeKind.Utc
            || snapshot.Items.Count is < 1 or > 100 || snapshot.Items.Select(i => i.AnalysisId).Distinct().Count() != snapshot.Items.Count)
            throw UploadFailure.Dependency("Analysis database");
        var counts = ProcessingStatuses.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        var output = new List<BatchPublicItem>(snapshot.Items.Count);
        int active = 0, succeeded = 0, failed = 0, duplicate = 0;
        foreach (var row in snapshot.Items)
        {
            if (row.AnalysisId == Guid.Empty || !counts.ContainsKey(row.ProcessingStatus)
                || !ReviewStatuses.Contains(row.ReviewStatus)
                || (row.RiskLevel is not null && row.RiskLevel is not ("LOW" or "MODERATE" or "HIGH"))
                || (row.ProcessingStatus == "DUPLICATE") != (row.ExistingAnalysisId is not null)
                || row.ExistingAnalysisId == Guid.Empty || row.ExistingAnalysisId == row.AnalysisId)
                throw UploadFailure.Dependency("Analysis database");
            if (row.Job is { } job && (job.JobId == Guid.Empty || job.AttemptNo < 1
                || job.RetryCount != job.AttemptNo - 1 || !JobStatuses.Contains(job.Status)))
                throw UploadFailure.Dependency("Analysis database");
            bool canRetry = row.ProcessingStatus == "FAILED" && row.JobRetryable
                && row.Job?.Status is "FAILED" or "RETRY_READY";
            switch (row.ProcessingStatus)
            {
                case "PENDING": case "QUEUED": case "PROCESSING": case "RETRY_READY": active++; break;
                case "FAILED" when canRetry: active++; break;
                case "COMPLETED": succeeded++; break;
                case "DUPLICATE": duplicate++; break;
                default: failed++; break;
            }
            counts[row.ProcessingStatus]++;
            BatchPublicError? error = null;
            if (row.ProcessingStatus is "FAILED" or "LOW_QUALITY" or "REJECTED" or "DUPLICATE")
            {
                var code = row.ProcessingStatus switch
                {
                    "DUPLICATE" => "ERR_DUPLICATE_REQUEST",
                    "LOW_QUALITY" => "ERR_IMAGE_LOW_QUALITY",
                    "REJECTED" => "ERR_VALIDATION",
                    _ => row.ErrorCode is { } c && ErrorCodes.Contains(c) ? c : "ERR_AI_FAILED"
                };
                // Never forward raw job error_details, exception messages or dependency URLs.
                var message = code switch
                {
                    "ERR_DUPLICATE_REQUEST" => "Ảnh trùng đã được bỏ qua.",
                    "ERR_IMAGE_LOW_QUALITY" => "Chất lượng ảnh chưa đạt yêu cầu.",
                    "ERR_TIMEOUT" => "Quá thời gian xử lý.",
                    "ERR_NETWORK" => "Dịch vụ xử lý tạm thời không khả dụng.",
                    _ => "Không thể hoàn tất phân tích ảnh."
                };
                error = new(code, message, canRetry, row.Job?.RetryCount);
            }
            output.Add(new(row.AnalysisId, row.FileName, row.ProcessingStatus, row.ReviewStatus,
                row.ProcessingStatus == "COMPLETED" ? row.RiskLevel : null, error, row.Job, row.ExistingAnalysisId));
        }
        if (active + succeeded + failed + duplicate != output.Count || counts.Values.Sum() != output.Count)
            throw UploadFailure.Dependency("Analysis database");
        var status = active > 0 ? "IN_PROGRESS" : failed == 0 ? "COMPLETED"
            : succeeded + duplicate > 0 ? "PARTIAL_FAILED" : "FAILED";
        return new(snapshot.BatchId, output.AsReadOnly(), snapshot.CreatedAt, status,
            new(output.Count, active, succeeded, failed, duplicate, new ReadOnlyDictionary<string, int>(counts)));
    }
}

public sealed class BatchStatusService(IBatchStatusRepository repository, IPatientAccessResolver access,
    IBatchRedisProgressService? batchProgress = null, IGroupedBatchStatusReader? groups = null)
{
    private static UploadFailure Missing() => new(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

    public async Task<BatchStatusResponse> GetAsync(Guid requesterId, Guid? clinicId, Guid batchId,
        CancellationToken ct, string? correlationId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (requesterId == Guid.Empty) throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
        if (clinicId == Guid.Empty) throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "invalid_uuid"));
        if (batchId == Guid.Empty) throw Missing();
        try
        {
            if(groups is not null && await groups.TryReadAsync(requesterId,clinicId,batchId,correlationId,ct) is {} grouped)return grouped;
            var identity = await repository.FindIdentityAsync(batchId, ct);
            if (identity is null) throw Missing();
            if (identity.BatchId != batchId || identity.TenantId == Guid.Empty || identity.PatientId == Guid.Empty || identity.OwnerId == Guid.Empty)
                throw UploadFailure.Dependency("Analysis database");
            var decision = await access.ResolveAsync(new PatientAccessRequest(requesterId, clinicId, identity.PatientId, "ANALYSIS_READ")
                { CorrelationId = correlationId }, ct);
            if (!decision.Allowed || decision.TenantId != identity.TenantId || decision.OwnerPrincipalId != identity.OwnerId)
                throw Missing();
            // Only fetch items/counts after current Profile authorization. The SQL binds all four identity fields again.
            var snapshot = await repository.ReadAsync(identity, ct);
            if (snapshot is null) throw Missing();
            if (snapshot.BatchId != batchId) throw UploadFailure.Dependency("Analysis database");
            var response = BatchStatusProjection.Create(snapshot);
            if (batchProgress is not null)
            {
                try
                {
                    var progress = await batchProgress.GetProgressAsync(batchId);
                    if (!MatchesDatabase(progress, response))
                    {
                        var outcomes = response.Items
                            .Where(i => i.ProcessingStatus is "COMPLETED" or "DUPLICATE"
                                || (i.ProcessingStatus is "FAILED" or "LOW_QUALITY" or "REJECTED")
                                    && i.Error?.Retryable != true)
                            .Select(i => new BatchImageOutcome(i.AnalysisId,
                                i.ProcessingStatus is "COMPLETED" or "DUPLICATE"))
                            .ToArray();
                        await batchProgress.RebuildBatchAsync(batchId, response.Summary.Total, outcomes,
                            response.BatchStatus switch
                            {
                                "PARTIAL_FAILED" => "PARTIAL_COMPLETED",
                                "PARTIAL_COMPLETED" => "PARTIAL_COMPLETED",
                                var status => status
                            });
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Redis is only a cache. A cache outage must not hide PostgreSQL's durable batch state.
                }
            }
            return response;
        }
        catch (UploadFailure) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw UploadFailure.Dependency("batch read dependencies"); }
    }

    private static bool MatchesDatabase(BatchProgressSnapshot? progress, BatchStatusResponse response)
    {
        if (progress is null) return false;
        var completed = response.Summary.Succeeded + response.Summary.Duplicate;
        var expectedStatus = response.BatchStatus == "PARTIAL_FAILED" ? "PARTIAL_COMPLETED" : response.BatchStatus;
        return progress.BatchId == response.BatchId
            && progress.Total == response.Summary.Total
            && progress.Completed == completed
            && progress.Failed == response.Summary.Failed
            && progress.Processing == response.Summary.Active
            && progress.Status == expectedStatus;
    }
}
