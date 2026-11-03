using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Domain;

namespace Aura.Worker.Analysis.Data.Repositories;

public interface IJobRepository
{
    Task<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);
    Task<bool> TryAcquireLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan extension, CancellationToken cancellationToken);
    Task RecordCallIntentAsync(Guid jobId, CancellationToken cancellationToken);
    Task UpdateJobStatusAsync(Guid jobId, string status, CancellationToken cancellationToken);
    Task<JobRecord> CreateNextAttemptJobAsync(JobRecord previousJob, int nextAttempt, CancellationToken cancellationToken);
}

public class JobRepository : IJobRepository
{
    private readonly WorkerDbContext _db;
    private readonly ILogger<JobRepository> _logger;

    public JobRepository(WorkerDbContext db, ILogger<JobRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
    }

    public async Task<bool> TryAcquireLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expiry = now.Add(leaseDuration);

        // N05 / R05: Atomic conditional database update with monotonic fencing token increment
        if (_db.Database.IsRelational())
        {
            var affected = await _db.Database.ExecuteSqlRawAsync("""
                UPDATE analysis.jobs
                SET lease_owner = {0},
                    lease_expires_at = {1},
                    fencing_token = fencing_token + 1,
                    status = 'RUNNING',
                    started_at = COALESCE(started_at, {2}),
                    updated_at = {2}
                WHERE id = {3}
                  AND (lease_expires_at IS NULL OR lease_expires_at < {2} OR lease_owner = {0})
                  AND status IN ('QUEUED', 'RUNNING', 'RETRY_READY');
                """, workerId, expiry, now, jobId);

            if (affected > 0)
            {
                _logger.LogInformation("Job {JobId} successfully leased by worker {WorkerId} until {Expiry}", jobId, workerId, expiry);
                return true;
            }

            _logger.LogWarning("Job {JobId} could not be leased by worker {WorkerId} (already leased or terminal)", jobId, workerId);
            return false;
        }

        // In-memory test fallback only for non-relational database providers
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        if (job == null) return false;

        if (job.LeaseExpiresAt == null || job.LeaseExpiresAt < now || job.LeaseOwner == workerId)
        {
            job.LeaseOwner = workerId;
            job.LeaseExpiresAt = expiry;
            job.Status = JobStatus.Running;
            job.FencingToken++;
            job.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        return false;
    }

    public async Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan extension, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var expiry = now.Add(extension);

        if (_db.Database.IsRelational())
        {
            var affected = await _db.Database.ExecuteSqlRawAsync("""
                UPDATE analysis.jobs
                SET lease_expires_at = {0},
                    updated_at = {1}
                WHERE id = {2}
                  AND lease_owner = {3}
                  AND lease_expires_at >= {1};
                """, expiry, now, jobId, workerId);

            return affected > 0;
        }

        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId && j.LeaseOwner == workerId, cancellationToken);
        if (job == null) return false;

        if (job.LeaseExpiresAt != null && job.LeaseExpiresAt >= now)
        {
            job.LeaseExpiresAt = expiry;
            job.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        return false;
    }

    public async Task RecordCallIntentAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_db.Database.IsRelational())
        {
            await _db.Database.ExecuteSqlRawAsync("""
                UPDATE analysis.jobs
                SET call_intent_recorded = true,
                    updated_at = {0}
                WHERE id = {1};
                """, now, jobId);
        }
        else
        {
            var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
            if (job != null)
            {
                job.IntentRecorded = true;
                job.UpdatedAt = now;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public async Task UpdateJobStatusAsync(Guid jobId, string status, CancellationToken cancellationToken)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken);
        if (job != null)
        {
            job.Status = status;
            job.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<JobRecord> CreateNextAttemptJobAsync(JobRecord previousJob, int nextAttempt, CancellationToken cancellationToken)
    {
        var correlationId = string.IsNullOrWhiteSpace(previousJob.CorrelationId)
            ? Guid.NewGuid().ToString("D")
            : previousJob.CorrelationId;
        if (correlationId.Length > 64) correlationId = correlationId[..64];

        var nextJob = new JobRecord
        {
            JobId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AnalysisId = previousJob.AnalysisId,
            BatchId = previousJob.BatchId,
            TenantId = previousJob.TenantId,
            ImageFileId = previousJob.ImageFileId,
            AttemptNo = nextAttempt,
            Status = JobStatus.Queued,
            CorrelationId = correlationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Jobs.Add(nextJob);
        await _db.SaveChangesAsync(cancellationToken);
        return nextJob;
    }
}
