using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AURA.Notification.Api;

public sealed record DeliveryJob(
    Guid Id,
    Guid InboxItemId,
    Guid AccountId,
    Guid DeviceId,
    byte[] FcmTokenHash,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset NextAttemptAt,
    Guid? LeaseToken,
    DateTimeOffset? LeaseExpiresAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ClaimedDeliveryJob(
    Guid Id,
    Guid InboxItemId,
    Guid AccountId,
    Guid DeviceId,
    int Attempts,
    int MaxAttempts,
    Guid LeaseToken,
    string Type,
    string Title,
    string TargetPath,
    string Metadata);

public sealed class NotificationDeliveryJobRepository(
    NpgsqlDataSource dataSource,
    ILogger<NotificationDeliveryJobRepository> logger)
{
    public async Task<int> EnqueueDeliveryJobsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid inboxItemId,
        Guid accountId,
        IReadOnlyList<(Guid DeviceId, byte[] TokenHash)> devices,
        CancellationToken ct)
    {
        if (devices.Count == 0) return 0;

        const string sql = """
            INSERT INTO notification.delivery_jobs
                (inbox_item_id, account_id, device_id, fcm_token_hash, status, attempts, next_attempt_at)
            VALUES
                (@inbox_item_id, @account_id, @device_id, @fcm_token_hash, 'PENDING', 0, now())
            ON CONFLICT (inbox_item_id, device_id) DO NOTHING;
            """;

        int enqueued = 0;
        foreach (var (deviceId, tokenHash) in devices)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("inbox_item_id", inboxItemId);
            cmd.Parameters.AddWithValue("account_id", accountId);
            cmd.Parameters.AddWithValue("device_id", deviceId);
            cmd.Parameters.AddWithValue("fcm_token_hash", tokenHash);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows > 0) enqueued++;
        }

        logger.LogInformation("Enqueued {Count} durable delivery jobs for inbox item {InboxItemId}, account {AccountId}.",
            enqueued, inboxItemId, accountId);
        return enqueued;
    }

    public async Task<List<ClaimedDeliveryJob>> ClaimPendingJobsAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        if (batchSize is < 1 or > 100 || leaseDuration < TimeSpan.FromSeconds(5) || leaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using (var exhausted = dataSource.CreateCommand("""
            UPDATE notification.delivery_jobs SET status='DEAD_LETTER', completed_at=now(),
                lease_token=NULL, lease_expires_at=NULL, last_error='ATTEMPTS_EXHAUSTED_AFTER_CRASH'
            WHERE attempts>=max_attempts AND
                ((status='PENDING' AND next_attempt_at<=now()) OR (status='IN_FLIGHT' AND lease_expires_at<now()));
            """)) await exhausted.ExecuteNonQueryAsync(ct);
        var leaseToken = Guid.NewGuid();
        const string claimSql = """
            WITH candidate_jobs AS (
                SELECT id
                FROM notification.delivery_jobs
                WHERE attempts < max_attempts AND
                    ((status = 'PENDING' AND next_attempt_at <= now())
                     OR (status = 'IN_FLIGHT' AND lease_expires_at < now()))
                ORDER BY next_attempt_at ASC
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED
            )
            UPDATE notification.delivery_jobs dj
            SET status = 'IN_FLIGHT',
                lease_token = @lease_token,
                lease_expires_at = now() + @lease_duration,
                attempts = dj.attempts + 1
            FROM candidate_jobs cj
            JOIN notification.inbox_items ii ON ii.id = (SELECT inbox_item_id FROM notification.delivery_jobs WHERE id = cj.id)
            WHERE dj.id = cj.id
            RETURNING dj.id, dj.inbox_item_id, dj.account_id, dj.device_id, dj.attempts, dj.max_attempts, dj.lease_token,
                      ii.type, ii.title,
                      CASE WHEN ii.type='PAYMENT_SUCCESS' AND ii.order_id IS NOT NULL
                           THEN '/billing/orders/' || ii.order_id::text ELSE ii.target_path END,
                      ii.metadata;
            """;

        var jobs = new List<ClaimedDeliveryJob>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(claimSql, connection);
        cmd.Parameters.AddWithValue("batch_size", batchSize);
        cmd.Parameters.AddWithValue("lease_token", leaseToken);
        cmd.Parameters.AddWithValue("lease_duration", leaseDuration);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(new ClaimedDeliveryJob(
                Id: reader.GetGuid(0),
                InboxItemId: reader.GetGuid(1),
                AccountId: reader.GetGuid(2),
                DeviceId: reader.GetGuid(3),
                Attempts: reader.GetInt32(4),
                MaxAttempts: reader.GetInt32(5),
                LeaseToken: reader.GetGuid(6),
                Type: reader.GetString(7),
                Title: reader.GetString(8),
                TargetPath: reader.GetString(9),
                Metadata: reader.GetString(10)
            ));
        }

        return jobs;
    }

    public async Task CompleteJobAsync(Guid jobId, Guid leaseToken, CancellationToken ct)
    {
        const string sql = """
            UPDATE notification.delivery_jobs
            SET status = 'COMPLETED',
                completed_at = now(),
                lease_token = NULL,
                lease_expires_at = NULL
            WHERE id = @job_id AND lease_token = @lease_token
                AND status = 'IN_FLIGHT' AND lease_expires_at > now();
            """;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("job_id", jobId);
        cmd.Parameters.AddWithValue("lease_token", leaseToken);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FailJobAsync(
        Guid jobId,
        Guid leaseToken,
        string error,
        bool isTransient,
        int currentAttempts,
        int maxAttempts,
        CancellationToken ct)
    {
        if (isTransient && currentAttempts < maxAttempts)
        {
            // Bounded exponential backoff + jitter: Math.Min(300, 2^attempts * 2) seconds
            var backoffSec = Math.Min(300, Math.Pow(2, Math.Min(currentAttempts, 8)) * 2);
            var jitter = Random.Shared.Next(1, 5);
            var delay = TimeSpan.FromSeconds(backoffSec + jitter);

            const string retrySql = """
                UPDATE notification.delivery_jobs
                SET status = 'PENDING',
                    next_attempt_at = now() + @delay,
                    last_error = @last_error,
                    lease_token = NULL,
                    lease_expires_at = NULL
                WHERE id = @job_id AND lease_token = @lease_token
                    AND status = 'IN_FLIGHT' AND lease_expires_at > now();
                """;

            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(retrySql, connection);
            cmd.Parameters.AddWithValue("delay", delay);
            cmd.Parameters.AddWithValue("last_error", error);
            cmd.Parameters.AddWithValue("job_id", jobId);
            cmd.Parameters.AddWithValue("lease_token", leaseToken);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        else
        {
            // Dead letter (terminal failure or max attempts exhausted)
            const string dlqSql = """
                UPDATE notification.delivery_jobs
                SET status = 'DEAD_LETTER',
                    last_error = @last_error,
                    completed_at = now(),
                    lease_token = NULL,
                    lease_expires_at = NULL
                WHERE id = @job_id AND lease_token = @lease_token
                    AND status = 'IN_FLIGHT' AND lease_expires_at > now();
                """;

            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(dlqSql, connection);
            cmd.Parameters.AddWithValue("last_error", error);
            cmd.Parameters.AddWithValue("job_id", jobId);
            cmd.Parameters.AddWithValue("lease_token", leaseToken);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
