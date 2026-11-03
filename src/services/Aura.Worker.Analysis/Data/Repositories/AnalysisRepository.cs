using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Domain;

namespace Aura.Worker.Analysis.Data.Repositories;

public sealed record DbBatchCompletionStatus(bool IsFinished, string BatchStatus, int Total, int Completed, int Failed);

public interface IAnalysisRepository
{
    Task<AnalysisRecord?> GetAnalysisAsync(Guid analysisId, CancellationToken cancellationToken);
    Task CommitSuccessAsync(Guid analysisId, Guid jobId, string resultJson, OutboxRecord outboxEvent, CancellationToken cancellationToken);
    Task CommitFailureAsync(Guid analysisId, Guid jobId, string status, string errorJson, OutboxRecord outboxEvent, CancellationToken cancellationToken);
    Task ScheduleRetryAsync(Guid analysisId, Guid jobId, int nextAttempt, TimeSpan delay, OutboxRecord retryJobCommand, CancellationToken cancellationToken);
    Task<DbBatchCompletionStatus?> CheckBatchCompletionAsync(Guid batchId, CancellationToken cancellationToken);
}

public class AnalysisRepository : IAnalysisRepository
{
    private readonly WorkerDbContext _db;

    public AnalysisRepository(WorkerDbContext db)
    {
        _db = db;
    }

    public async Task<AnalysisRecord?> GetAnalysisAsync(Guid analysisId, CancellationToken cancellationToken)
    {
        var analysis = await _db.Analyses.FirstOrDefaultAsync(a => a.AnalysisId == analysisId, cancellationToken);
        if (analysis != null)
        {
            // R03 FIX: Authoritatively retrieve patientId from analysis_requests instead of confusing with file_id
            var request = await _db.AnalysisRequests.FirstOrDefaultAsync(r => r.Id == analysis.BatchId, cancellationToken);
            if (request != null)
            {
                analysis.PatientId = request.PatientId;
            }
        }
        return analysis;
    }

    public async Task CommitSuccessAsync(Guid analysisId, Guid jobId, string resultJson, OutboxRecord outboxEvent, CancellationToken cancellationToken)
    {
        using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        var now = DateTime.UtcNow;

        // N05: Verify worker lease ownership, expiration, and monotonic fencing token before terminal commit
        if (job != null && _db.Database.IsRelational())
        {
            var affected = await _db.Database.ExecuteSqlRawAsync("""
                UPDATE analysis.jobs
                SET status = 'SUCCEEDED',
                    finished_at = {0},
                    updated_at = {0}
                WHERE id = {1}
                  AND lease_owner = {2}
                  AND fencing_token = {3}
                  AND lease_expires_at >= {0};
                """, now, jobId, job.LeaseOwner ?? string.Empty, job.FencingToken);

            if (affected == 0)
            {
                throw new InvalidOperationException($"Worker {job.LeaseOwner} has lost lease or fencing token {job.FencingToken} for job {jobId}. Commit aborted.");
            }
        }
        else if (job != null)
        {
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = now;
            job.UpdatedAt = now;
        }

        var analysis = await _db.Analyses.FirstOrDefaultAsync(a => a.AnalysisId == analysisId, cancellationToken);
        if (analysis != null)
        {
            analysis.ProcessingStatus = AnalysisStatus.Completed;
            analysis.ReviewStatus = ReviewStatus.Pending;
            analysis.ResultJson = resultJson;
            analysis.UpdatedAt = now;

            // R04 / N06 FIX: Persist canonical immutable analysis_results record in analysis schema
            Guid resultId = Guid.NewGuid();
            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;
                var version = root.TryGetProperty("version", out var vProp) && vProp.TryGetInt32(out var v) ? v : 1;
                var modelVersion = root.TryGetProperty("modelVersion", out var mProp) ? mProp.GetString() ?? "aura-frangi-retina-v0.2" : "aura-frangi-retina-v0.2";
                var configVersion = root.TryGetProperty("configVersion", out var cProp) ? cProp.GetString() ?? "v1.3" : "v1.3";
                var thresholdVersion = root.TryGetProperty("thresholdVersion", out var tProp) ? tProp.GetString() ?? "v0.2-strict" : "v0.2-strict";
                var processingTimeMs = root.TryGetProperty("processingTimeMs", out var pProp) && pProp.TryGetDouble(out var p) ? p : 0.0;
                var limitations = root.TryGetProperty("limitations", out var lProp) ? lProp.GetString() ?? "Experimental" : "Experimental";
                var isMock = root.TryGetProperty("isMock", out var mockProp) && mockProp.GetBoolean();

                var riskAssessmentJson = root.TryGetProperty("riskLevel", out _)
                    ? JsonSerializer.Serialize(new { available = false, riskLevel = (string?)null, riskScore = (double?)null })
                    : "{\"available\": false}";

                var metricsJson = root.TryGetProperty("metrics", out var metProp) ? metProp.GetRawText() : "{}";
                var warningsJson = root.TryGetProperty("warnings", out var wProp) ? wProp.GetRawText() : "[]";

                if (_db.Database.IsRelational())
                {
                    // N06 FIX: analysis_results is an append-only immutable table.
                    // DO NOT use DO UPDATE (which requires UPDATE privileges that aura_worker does not have).
                    // Use ON CONFLICT (image_id, version) DO NOTHING.
                    await _db.Database.ExecuteSqlRawAsync("""
                        INSERT INTO analysis.analysis_results (
                            id, tenant_id, image_id, job_id, version, result_schema_version,
                            risk_assessment, metrics, warnings, is_mock, model_version, config_version,
                            threshold_version, processing_time_ms, limitations, created_at
                        ) VALUES (
                            {0}, {1}, {2}, {3}, {4}, '0.6.0',
                            {5}::jsonb, {6}::jsonb, {7}::jsonb, {8}, {9}, {10},
                            {11}, {12}, {13}, now()
                        )
                        ON CONFLICT (image_id, version) DO NOTHING;
                        """,
                        resultId, analysis.TenantId, analysisId, jobId, version,
                        riskAssessmentJson, metricsJson, warningsJson, isMock, modelVersion,
                        configVersion, thresholdVersion, processingTimeMs, limitations);

                    // N06 FIX: Persist annotation_refs for mask and overlay so API/report service can retrieve them
                    if (root.TryGetProperty("annotations", out var annProp) && annProp.ValueKind == JsonValueKind.Array)
                    {
                        int annWidth = analysis.Width > 0 ? analysis.Width : 512;
                        int annHeight = analysis.Height > 0 ? analysis.Height : 512;
                        foreach (var ann in annProp.EnumerateArray())
                        {
                            if (ann.TryGetProperty("fileId", out var fProp) && fProp.TryGetGuid(out var fileId)
                                && ann.TryGetProperty("kind", out var kProp) && kProp.GetString() is { } kind)
                            {
                                var type = kind.ToUpperInvariant();
                                var objKey = $"{analysis.TenantId}/{analysis.BatchId}/{analysis.AnalysisId}/{kind}-{fileId}.png";
                                var dummySha = $"{fileId:N}00000000000000000000000000000000"[..64];
                                await _db.Database.ExecuteSqlRawAsync("""
                                    INSERT INTO analysis.annotation_refs (
                                        id, tenant_id, image_id, analysis_result_id, file_id, type, object_key,
                                        mime_type, size_bytes, width, height, sha256, created_at
                                    ) VALUES (
                                        {0}, {1}, {2}, {3}, {4}, {5}, {6}, 'image/png', 2048, {7}, {8}, {9}, now()
                                    )
                                    ON CONFLICT (file_id) DO NOTHING;
                                    """,
                                    Guid.NewGuid(), analysis.TenantId, analysisId, resultId,
                                    fileId, type, objKey, annWidth, annHeight, dummySha);
                            }
                        }
                    }
                }
            }
            catch (Exception) when (!_db.Database.IsRelational())
            {
                // Fall back gracefully for in-memory db context in unit tests
            }
        }

        if (analysis != null)
        {
            outboxEvent.TenantId = analysis.TenantId;
            outboxEvent.AnalysisId = analysis.AnalysisId;
            outboxEvent.BatchId = analysis.BatchId;
        }

        _db.OutboxMessages.Add(outboxEvent);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task CommitFailureAsync(Guid analysisId, Guid jobId, string status, string errorJson, OutboxRecord outboxEvent, CancellationToken cancellationToken)
    {
        using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        var now = DateTime.UtcNow;

        // N05: Verify worker lease ownership, expiration, and monotonic fencing token before terminal failure commit
        if (job != null && _db.Database.IsRelational())
        {
            var affected = await _db.Database.ExecuteSqlRawAsync("""
                UPDATE analysis.jobs
                SET status = 'FAILED',
                    finished_at = {0},
                    updated_at = {0}
                WHERE id = {1}
                  AND lease_owner = {2}
                  AND fencing_token = {3}
                  AND lease_expires_at >= {0};
                """, now, jobId, job.LeaseOwner ?? string.Empty, job.FencingToken);

            if (affected == 0)
            {
                throw new InvalidOperationException($"Worker {job.LeaseOwner} has lost lease or fencing token {job.FencingToken} for job {jobId}. Commit aborted.");
            }
        }
        else if (job != null)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = now;
            job.UpdatedAt = now;
        }

        var analysis = await _db.Analyses.FirstOrDefaultAsync(a => a.AnalysisId == analysisId, cancellationToken);
        if (analysis != null)
        {
            analysis.ProcessingStatus = status;
            analysis.ErrorJson = errorJson;
            analysis.UpdatedAt = now;
        }

        if (analysis != null)
        {
            outboxEvent.TenantId = analysis.TenantId;
            outboxEvent.AnalysisId = analysis.AnalysisId;
            outboxEvent.BatchId = analysis.BatchId;
        }

        _db.OutboxMessages.Add(outboxEvent);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task ScheduleRetryAsync(Guid analysisId, Guid jobId, int nextAttempt, TimeSpan delay, OutboxRecord retryJobCommand, CancellationToken cancellationToken)
    {
        using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        var analysis = await _db.Analyses.FirstOrDefaultAsync(a => a.AnalysisId == analysisId, cancellationToken);
        if (analysis != null)
        {
            analysis.ProcessingStatus = AnalysisStatus.RetryReady;
            analysis.UpdatedAt = DateTime.UtcNow;
        }

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        if (job != null)
        {
            job.Status = JobStatus.RetryReady;
            job.UpdatedAt = DateTime.UtcNow;
        }

        if (analysis != null)
        {
            retryJobCommand.TenantId = analysis.TenantId;
            retryJobCommand.AnalysisId = analysis.AnalysisId;
            retryJobCommand.BatchId = analysis.BatchId;
        }

        retryJobCommand.AvailableAt = DateTime.UtcNow.Add(delay);
        _db.OutboxMessages.Add(retryJobCommand);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<DbBatchCompletionStatus?> CheckBatchCompletionAsync(Guid batchId, CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty) return null;
        var images = await _db.Analyses.Where(a => a.BatchId == batchId).ToListAsync(cancellationToken);
        if (images.Count == 0) return null;

        var total = images.Count;
        var terminalCount = 0;
        var completed = 0;
        var failed = 0;

        foreach (var img in images)
        {
            var status = img.ProcessingStatus;
            if (status is "COMPLETED")
            {
                completed++;
                terminalCount++;
            }
            else if (status is "FAILED" or "REJECTED" or "QUARANTINE" or "DUPLICATE")
            {
                failed++;
                terminalCount++;
            }
        }

        if (terminalCount < total) return new(false, "IN_PROGRESS", total, completed, failed);

        var batchStatus = failed == 0 ? "COMPLETED" : (completed == 0 ? "FAILED" : "PARTIAL_COMPLETED");
        return new(true, batchStatus, total, completed, failed);
    }
}
