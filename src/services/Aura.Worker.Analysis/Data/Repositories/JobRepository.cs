using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Domain;

namespace Aura.Worker.Analysis.Data.Repositories;

public interface IJobRepository
{
    Task<JobRecord?> GetJobAsync(Guid jobId, CancellationToken ct);
    Task<JobLease?> TryAcquireLeaseAsync(Guid jobId, string workerId, TimeSpan duration, CancellationToken ct);
    Task<JobLease?> RenewLeaseAsync(JobLease lease, TimeSpan extension, CancellationToken ct);
    Task RecordCallIntentAsync(JobLease lease, CancellationToken ct);
    Task<JobRecord> CreateNextAttemptJobAsync(JobRecord previousJob, int nextAttempt, CancellationToken ct);
}

public sealed class JobRepository(WorkerDbContext db, ILogger<JobRepository> logger) : IJobRepository
{
    public Task<JobRecord?> GetJobAsync(Guid jobId, CancellationToken ct) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == jobId, ct);

    private NpgsqlConnection Connection() => new(db.Database.GetConnectionString()
        ?? throw new InvalidOperationException("Worker requires its configured PostgreSQL database."));

    public async Task<JobLease?> TryAcquireLeaseAsync(Guid jobId, string workerId, TimeSpan duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerId) || duration <= TimeSpan.Zero) throw new ArgumentException("Invalid lease.");
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE analysis.jobs SET lease_owner=$2,
                lease_expires_at=clock_timestamp()+make_interval(secs=>$3),
                fencing_token=fencing_token+1, status='RUNNING',
                started_at=COALESCE(started_at,clock_timestamp()),updated_at=clock_timestamp()
            WHERE id=$1 AND (lease_expires_at IS NULL OR lease_expires_at<clock_timestamp())
              AND status IN ('QUEUED','RUNNING','RETRY_READY')
            RETURNING fencing_token,lease_expires_at;
            """, connection);
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(duration.TotalSeconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        logger.LogInformation("Acquired job {JobId} with fence {Fence}", jobId,reader.GetInt64(0));
        return new(jobId,workerId,reader.GetInt64(0),reader.GetDateTime(1));
    }

    public async Task<JobLease?> RenewLeaseAsync(JobLease lease, TimeSpan extension, CancellationToken ct)
    {
        if (extension <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(extension));
        await using var connection = Connection();
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE analysis.jobs SET lease_expires_at=clock_timestamp()+make_interval(secs=>$4),updated_at=clock_timestamp()
            WHERE id=$1 AND lease_owner=$2 AND fencing_token=$3
              AND lease_expires_at>clock_timestamp() AND status='RUNNING'
            RETURNING lease_expires_at;
            """,connection);
        command.Parameters.AddWithValue(lease.JobId); command.Parameters.AddWithValue(lease.Owner);
        command.Parameters.AddWithValue(lease.FencingToken); command.Parameters.AddWithValue(extension.TotalSeconds);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DateTime expiry ? lease with { ExpiresAt=expiry } : null;
    }

    public async Task RecordCallIntentAsync(JobLease lease, CancellationToken ct)
    {
        await using var connection=Connection(); await connection.OpenAsync(ct);
        await using var command=new NpgsqlCommand("""
            UPDATE analysis.jobs SET call_intent_recorded=true,updated_at=clock_timestamp()
            WHERE id=$1 AND lease_owner=$2 AND fencing_token=$3 AND lease_expires_at>clock_timestamp() AND status='RUNNING';
            """,connection);
        command.Parameters.AddWithValue(lease.JobId);command.Parameters.AddWithValue(lease.Owner);command.Parameters.AddWithValue(lease.FencingToken);
        if(await command.ExecuteNonQueryAsync(ct)!=1) throw new JobLeaseLostException(lease.JobId);
    }

    // Construct only: the next job and command are inserted atomically by ScheduleRetryAsync.
    public Task<JobRecord> CreateNextAttemptJobAsync(JobRecord previousJob,int nextAttempt,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(nextAttempt!=previousJob.AttemptNo+1) throw new ArgumentException("Retry attempt must increase by one.");
        return Task.FromResult(new JobRecord {
            JobId=Guid.NewGuid(),MessageId=Guid.NewGuid(),AnalysisId=previousJob.AnalysisId,
            TenantId=previousJob.TenantId,AttemptNo=nextAttempt,Status=JobStatus.Queued,
            CorrelationId=previousJob.CorrelationId
        });
    }
}
