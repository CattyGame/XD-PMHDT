using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace AURA.Notification.Api;

public sealed record ConsultationOutboxItem(
    Guid OutboxId,
    Guid ConversationId,
    Guid MessageId,
    Guid RecipientAccountId,
    Guid TenantId,
    int Attempts,
    Guid LeaseToken);

public sealed class ConsultationOutboxRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<ConsultationOutboxItem>> ClaimAsync(CancellationToken ct)
    {
        const string exhaustedSql = """
            UPDATE notification.consultation_notification_outbox
            SET status='DEAD_LETTER', lease_token=NULL, lease_expires_at=NULL,
                last_error_code='ATTEMPTS_EXHAUSTED', completed_at=now()
            WHERE attempts>=10 AND
                  ((status='PENDING' AND next_attempt_at<=now()) OR (status='IN_FLIGHT' AND lease_expires_at<now()));
            """;
        await using (var exhausted = dataSource.CreateCommand(exhaustedSql))
            await exhausted.ExecuteNonQueryAsync(ct);

        const string sql = """
            WITH candidates AS (
                SELECT outbox_id FROM notification.consultation_notification_outbox
                WHERE attempts<10 AND
                      ((status='PENDING' AND next_attempt_at<=now()) OR (status='IN_FLIGHT' AND lease_expires_at<now()))
                ORDER BY next_attempt_at, created_at
                LIMIT 50 FOR UPDATE SKIP LOCKED
            )
            UPDATE notification.consultation_notification_outbox o
            SET status='IN_FLIGHT', attempts=o.attempts+1,
                lease_token=@lease_token, lease_expires_at=now()+interval '30 seconds'
            FROM candidates c WHERE o.outbox_id=c.outbox_id
            RETURNING o.outbox_id,o.conversation_id,o.message_id,o.recipient_account_id,o.tenant_id,o.attempts,o.lease_token;
            """;
        var items = new List<ConsultationOutboxItem>();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        var leaseToken = Guid.NewGuid();
        command.Parameters.AddWithValue("lease_token", leaseToken);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ConsultationOutboxItem(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetGuid(3), reader.GetGuid(4), reader.GetInt32(5), reader.GetGuid(6)));
        }
        return items;
    }

    public Task CompleteAsync(ConsultationOutboxItem item, bool canceled, CancellationToken ct) =>
        UpdateAsync(item, canceled ? "CANCELED" : "COMPLETED", null, null, ct);

    public Task RetryAsync(ConsultationOutboxItem item, string errorCode, CancellationToken ct)
    {
        var delaySeconds = Math.Min(300, (int)Math.Pow(2, Math.Min(item.Attempts, 8)) * 2 + Random.Shared.Next(1, 5));
        var terminal = item.Attempts >= 10;
        return UpdateAsync(item, terminal ? "DEAD_LETTER" : "PENDING", errorCode,
            terminal ? null : TimeSpan.FromSeconds(delaySeconds), ct);
    }

    private async Task UpdateAsync(ConsultationOutboxItem item, string status, string? errorCode, TimeSpan? delay, CancellationToken ct)
    {
        const string sql = """
            UPDATE notification.consultation_notification_outbox
            SET status=@status, last_error_code=@error_code,
                next_attempt_at=CASE WHEN @delay IS NULL THEN next_attempt_at ELSE now()+@delay END,
                lease_token=NULL, lease_expires_at=NULL,
                completed_at=CASE WHEN @status IN ('COMPLETED','CANCELED','DEAD_LETTER') THEN now() ELSE NULL END
            WHERE outbox_id=@outbox_id AND lease_token=@lease_token AND status='IN_FLIGHT';
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.Add("error_code", NpgsqlDbType.Varchar).Value = (object?)errorCode ?? DBNull.Value;
        command.Parameters.Add("delay", NpgsqlDbType.Interval).Value = (object?)delay ?? DBNull.Value;
        command.Parameters.AddWithValue("outbox_id", item.OutboxId);
        command.Parameters.AddWithValue("lease_token", item.LeaseToken);
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class ConsultationNotificationOutboxWorker(
    ConsultationOutboxRepository outbox,
    ConsultationRepository conversations,
    ConsultationService service,
    IAccountResolver accounts,
    DeviceRegistrationRepository devices,
    NotificationInboxRepository inbox,
    NotificationDeliveryJobRepository deliveryJobs,
    IConfiguration configuration,
    ILogger<ConsultationNotificationOutboxWorker> logger) : BackgroundService
{
    private readonly string _projectId = configuration["Firebase:ProjectId"] ?? "aura-dev-97716";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await outbox.ClaimAsync(stoppingToken);
                if (items.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                await Task.WhenAll(items.Select(item => ProcessAsync(item, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Consultation notification outbox polling failed. Type: {ErrorType}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(ConsultationOutboxItem item, CancellationToken ct)
    {
        try
        {
            var conversation = await conversations.GetConversationAsync(item.ConversationId, ct);
            if (conversation is null || conversation.TenantId != item.TenantId)
            {
                await outbox.CompleteAsync(item, canceled: true, ct);
                return;
            }
            if (!await service.HasCurrentAccessAsync(conversation, item.RecipientAccountId, ct))
            {
                await outbox.CompleteAsync(item, canceled: true, ct);
                return;
            }

            var firebaseUid = await accounts.ResolveFirebaseUidAsync(item.RecipientAccountId, ct);
            if (string.IsNullOrWhiteSpace(firebaseUid))
            {
                await outbox.CompleteAsync(item, canceled: true, ct);
                return;
            }

            var activeDevices = await devices.GetTokensForUserAsync(_projectId, firebaseUid, ct);
            var command = new SaveInboxItemCommand(
                item.RecipientAccountId, item.TenantId, item.OutboxId, null, "CONSULTATION_MESSAGE",
                "Có tin nhắn tư vấn mới", $"/consultations/{item.ConversationId:D}",
                JsonSerializer.Serialize(new { conversationId = item.ConversationId, messageId = item.MessageId }));
            var tokenHashes = activeDevices.Select(device => (device.DeviceId,
                SHA256.HashData(Encoding.UTF8.GetBytes(device.Token)))).ToList();
            await inbox.SaveWithDeliveryJobsAsync(command, tokenHashes, deliveryJobs, ct);
            await outbox.CompleteAsync(item, canceled: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Consultation notification outbox item {OutboxId} will retry. Type: {ErrorType}",
                item.OutboxId, ex.GetType().Name);
            await outbox.RetryAsync(item, "DEPENDENCY_UNAVAILABLE", ct);
        }
    }
}
