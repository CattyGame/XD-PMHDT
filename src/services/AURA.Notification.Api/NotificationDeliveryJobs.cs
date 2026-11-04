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
        var leaseToken = Guid.NewGuid();
        const string claimSql = """
            WITH candidate_jobs AS (
                SELECT id
                FROM notification.delivery_jobs
                WHERE (status = 'PENDING' AND next_attempt_at <= now())
                   OR (status = 'IN_FLIGHT' AND lease_expires_at < now())
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
                      ii.type, ii.title, ii.target_path, ii.metadata;
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
            WHERE id = @job_id AND lease_token = @lease_token;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("job_id", jobId);
        cmd.Parameters.AddWithValue("lease_token", leaseToken);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAllForInboxItemAsync(Guid inboxItemId, CancellationToken ct)
    {
        const string sql = """
            UPDATE notification.delivery_jobs
            SET status = 'COMPLETED',
                completed_at = now(),
                lease_token = NULL,
                lease_expires_at = NULL
            WHERE inbox_item_id = @inbox_item_id AND status != 'COMPLETED';
            """;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("inbox_item_id", inboxItemId);
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
                WHERE id = @job_id AND lease_token = @lease_token;
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
                WHERE id = @job_id AND lease_token = @lease_token;
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

public sealed class NotificationDeliveryWorker(
    NotificationDeliveryJobRepository jobRepository,
    DeviceRegistrationRepository deviceRepository,
    IAccountResolver accountResolver,
    IFcmClient fcmClient,
    IConfiguration configuration,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    private readonly string _projectId = configuration["Firebase:ProjectId"] ?? "aura-dev";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NotificationDeliveryWorker starting durable FCM delivery loop.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobs = await jobRepository.ClaimPendingJobsAsync(
                    batchSize: 20,
                    leaseDuration: TimeSpan.FromSeconds(30),
                    stoppingToken);

                if (jobs.Count == 0)
                {
                    await Task.Delay(1500, stoppingToken);
                    continue;
                }

                logger.LogInformation("Claimed {Count} pending delivery jobs for durable FCM dispatch.", jobs.Count);

                foreach (var job in jobs)
                {
                    await ProcessJobAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in NotificationDeliveryWorker loop.");
                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(ClaimedDeliveryJob job, CancellationToken ct)
    {
        try
        {
            // 1. Verify current account active status
            var firebaseUid = await accountResolver.ResolveFirebaseUidAsync(job.AccountId, ct);
            if (string.IsNullOrWhiteSpace(firebaseUid))
            {
                logger.LogWarning("Account {AccountId} is inactive or suspended. Terminating delivery job {JobId}.",
                    job.AccountId, job.Id);
                await jobRepository.FailJobAsync(job.Id, job.LeaseToken, "Account inactive or suspended", isTransient: false, job.Attempts, job.MaxAttempts, ct);
                return;
            }

            // 2. Retrieve decrypted device token
            var token = await deviceRepository.GetTokenAsync(_projectId, firebaseUid, job.DeviceId, ct);
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogWarning("Device token for device {DeviceId} not found. Terminating delivery job {JobId}.",
                    job.DeviceId, job.Id);
                await jobRepository.FailJobAsync(job.Id, job.LeaseToken, "Device token not found", isTransient: false, job.Attempts, job.MaxAttempts, ct);
                return;
            }

            // 3. Dispatch Web Push via FCM client
            var bodyText = job.Type switch
            {
                "ANALYSIS_COMPLETED" => "Kết quả phân tích mắt của bạn đã có! Bấm để xem chi tiết.",
                "PAYMENT_SUCCESS" => "Giao dịch thanh toán đã được xác nhận. Bấm để xem gói cước.",
                _ => "Thông báo từ hệ thống AURA."
            };

            var activeDevice = new ActiveDeviceToken(job.DeviceId, token);
            var multicastMessage = new FirebaseAdmin.Messaging.MulticastMessage
            {
                Tokens = [token],
                Notification = new FirebaseAdmin.Messaging.Notification
                {
                    Title = job.Title,
                    Body = bodyText
                },
                Data = new Dictionary<string, string>
                {
                    ["notificationId"] = job.InboxItemId.ToString("D"),
                    ["targetPath"] = job.TargetPath,
                    ["url"] = job.TargetPath
                },
                Webpush = new FirebaseAdmin.Messaging.WebpushConfig
                {
                    Notification = new FirebaseAdmin.Messaging.WebpushNotification
                    {
                        Title = job.Title,
                        Body = bodyText,
                        Icon = "/favicon.ico",
                        Tag = $"notification-{job.InboxItemId:D}"
                    },
                    FcmOptions = new FirebaseAdmin.Messaging.WebpushFcmOptions
                    {
                        Link = job.TargetPath
                    }
                }
            };

            var result = await fcmClient.SendMulticastAsync([activeDevice], multicastMessage, ct);

            if (result.SuccessCount > 0)
            {
                logger.LogInformation("Delivery job {JobId} delivered successfully to device {DeviceId}.", job.Id, job.DeviceId);
                await jobRepository.CompleteJobAsync(job.Id, job.LeaseToken, ct);
            }
            else
            {
                var unregistered = result.Responses.FirstOrDefault(r => !r.IsSuccess && (r.ErrorCode == "Unregistered" || r.ErrorCode == "InvalidArgument"));
                if (unregistered is not null)
                {
                    logger.LogInformation("Device token for device {DeviceId} is unregistered. Cleaning up and marking job failed.", job.DeviceId);
                    await deviceRepository.DeleteIfTokenMatchesAsync(_projectId, firebaseUid, job.DeviceId, token, ct);
                    await jobRepository.FailJobAsync(job.Id, job.LeaseToken, $"FCM Unregistered Token ({unregistered.ErrorCode})", isTransient: false, job.Attempts, job.MaxAttempts, ct);
                }
                else
                {
                    var firstErr = result.Responses.FirstOrDefault(r => !r.IsSuccess);
                    var errMessage = firstErr?.ErrorMessage ?? firstErr?.ErrorCode ?? "FCM Dispatch Failure";
                    logger.LogWarning("Delivery job {JobId} failed transiently: {Error}", job.Id, errMessage);
                    await jobRepository.FailJobAsync(job.Id, job.LeaseToken, errMessage, isTransient: true, job.Attempts, job.MaxAttempts, ct);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Transient HTTP failure during delivery job {JobId}.", job.Id);
            await jobRepository.FailJobAsync(job.Id, job.LeaseToken, ex.Message, isTransient: true, job.Attempts, job.MaxAttempts, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error processing delivery job {JobId}.", job.Id);
            await jobRepository.FailJobAsync(job.Id, job.LeaseToken, ex.Message, isTransient: true, job.Attempts, job.MaxAttempts, ct);
        }
    }
}
