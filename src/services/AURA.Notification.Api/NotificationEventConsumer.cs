using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AURA.Notification.Api;

public sealed class RabbitMqConsumerOptions
{
    public const string SectionName = "RabbitMQ";

    public string HostName { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/aura-s3";
    public string UserName { get; set; } = "s3-notification";
    public string Password { get; set; } = "aura_s3_notification_2026";
    public string QueueName { get; set; } = "aura.s3.notification.events";
    public ushort PrefetchCount { get; set; } = 10;
    public bool AutoStart { get; set; } = true;
}

public sealed record NotificationDispatchMetrics(
    Guid EventId,
    Guid AnalysisId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset FcmDispatchedAt,
    long FcmLatencyMs,
    long TotalPipelineMs,
    int DevicesTargeted,
    int SuccessCount,
    int FailureCount,
    int CleanedTokensCount);

public sealed class NotificationEventConsumer(
    IOptions<RabbitMqConsumerOptions> options,
    NotificationInboxRepository inboxRepository,
    DeviceRegistrationRepository deviceRepository,
    IAccountResolver accountResolver,
    FirebaseApp firebaseApp,
    IConfiguration configuration,
    NpgsqlDataSource dataSource,
    ILogger<NotificationEventConsumer> logger,
    IFcmClient? fcmClient = null) : BackgroundService
{
    private readonly RabbitMqConsumerOptions _options = options.Value;
    private readonly IFcmClient _fcmClient = fcmClient ?? new FirebaseAdminFcmClient(
        firebaseApp,
        NullLogger<FirebaseAdminFcmClient>.Instance);
    private IConnection? _connection;
    private IModel? _channel;

    public NotificationDispatchMetrics? LastDispatchMetrics { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoStart)
        {
            logger.LogInformation("NotificationEventConsumer AutoStart is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                InitializeRabbitMq();
                var consumer = new AsyncEventingBasicConsumer(_channel);

                consumer.Received += async (_, ea) =>
                {
                    await HandleMessageAsync(ea, stoppingToken);
                };

                _channel.BasicConsume(
                    queue: _options.QueueName,
                    autoAck: false,
                    consumer: consumer);

                logger.LogInformation("NotificationEventConsumer listening on {Queue} in vhost {VHost}",
                    _options.QueueName, _options.VirtualHost);

                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "NotificationEventConsumer connection failed. Retrying in 5s...");
                Cleanup();
                await Task.Delay(5000, stoppingToken);
            }
        }

        Cleanup();
    }

    private void InitializeRabbitMq()
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.BasicQos(0, _options.PrefetchCount, false);
    }

    public async Task HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        ulong deliveryTag = ea.DeliveryTag;
        try
        {
            var body = ea.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            var success = await ProcessNotificationEventAsync(json, cancellationToken);
            if (success)
            {
                _channel?.BasicAck(deliveryTag, false);
            }
            else
            {
                _channel?.BasicNack(deliveryTag, false, false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process notification message. Nacking without requeue to DLQ.");
            _channel?.BasicNack(deliveryTag, false, false);
        }
    }

    public async Task<bool> ProcessNotificationEventAsync(string json, CancellationToken cancellationToken)
    {
        var receiveTimestamp = DateTimeOffset.UtcNow;
        var pipelineStopwatch = Stopwatch.StartNew();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("eventId", out var eventIdProp) || !eventIdProp.TryGetGuid(out var eventId) ||
            !root.TryGetProperty("analysisId", out var analysisIdProp) || !analysisIdProp.TryGetGuid(out var analysisId) ||
            !root.TryGetProperty("eventType", out var eventTypeProp) || !root.TryGetProperty("tenantId", out var tenantIdProp) ||
            !tenantIdProp.TryGetGuid(out var tenantId))
        {
            logger.LogWarning("Discarding malformed notification event envelope: {Json}", json);
            return true;
        }

        var eventType = eventTypeProp.GetString();
        if (eventType is not ("analysis.completed" or "analysis.failed" or "billing.payment.success"))
        {
            logger.LogInformation("Ignoring non-terminal event type {EventType}", eventType);
            return true;
        }

        var recipients = await ResolveRecipientsAsync(root, analysisId, cancellationToken);
        if (recipients.Count == 0)
        {
            logger.LogWarning("No recipient accounts found for analysis {AnalysisId}. Acknowledging.", analysisId);
            return true;
        }

        var inboxType = eventType switch
        {
            "analysis.completed" => "ANALYSIS_COMPLETED",
            "billing.payment.success" => "PAYMENT_SUCCESS",
            _ => "ANALYSIS_FAILED"
        };

        // SRS §7.5: Title and body must be neutral, strictly free from patient personal data and clinical diagnosis
        var title = eventType switch
        {
            "analysis.completed" => "Phân tích ảnh đáy mắt hoàn tất",
            "billing.payment.success" => "Thanh toán thành công",
            _ => "Phân tích ảnh đáy mắt không thành công"
        };
        var bodyText = eventType switch
        {
            "analysis.completed" => "Kết quả phân tích mắt của bạn đã có! Bấm để xem chi tiết.",
            "billing.payment.success" => "Giao dịch thanh toán đã được xác nhận. Bấm để xem gói cước.",
            _ => "Không thể phân tích ảnh đáy mắt do chất lượng ảnh không đạt tiêu chuẩn."
        };
        var targetPath = eventType == "billing.payment.success"
            ? "/billing/packages"
            : $"/analyses/{analysisId:D}";

        int totalDevicesTargeted = 0;
        int totalSuccess = 0;
        int totalFailed = 0;
        int totalCleaned = 0;
        long maxFcmLatencyMs = 0;

        foreach (var recipientId in recipients)
        {
            // 1. Persist Before ACK: Save to durable PostgreSQL inbox
            var command = new SaveInboxItemCommand(
                AccountId: recipientId,
                TenantId: tenantId,
                EventId: eventId,
                AnalysisId: analysisId,
                Type: inboxType,
                Title: title,
                TargetPath: targetPath,
                Metadata: json
            );

            var saveResult = await inboxRepository.SaveAsync(command, cancellationToken);
            logger.LogInformation(
                "Notification inbox outcome for Account {AccountId}, Event {EventId}: {Outcome}",
                recipientId, eventId, saveResult.Outcome);

            // 2. Dispatch FCM Web Push via Multicast (< 200ms) with auto-cleanup
            var fcmStopwatch = Stopwatch.StartNew();
            var dispatchResult = await SendWebPushNotificationAsync(
                recipientId,
                analysisId,
                title,
                bodyText,
                targetPath,
                cancellationToken);
            fcmStopwatch.Stop();

            if (fcmStopwatch.ElapsedMilliseconds > maxFcmLatencyMs)
            {
                maxFcmLatencyMs = fcmStopwatch.ElapsedMilliseconds;
            }

            totalDevicesTargeted += dispatchResult.DevicesTargeted;
            totalSuccess += dispatchResult.SuccessCount;
            totalFailed += dispatchResult.FailureCount;
            totalCleaned += dispatchResult.CleanedTokensCount;
        }

        pipelineStopwatch.Stop();

        // 3. Record Master Audit 3 Timestamps & Metrics
        LastDispatchMetrics = new NotificationDispatchMetrics(
            EventId: eventId,
            AnalysisId: analysisId,
            ReceivedAt: receiveTimestamp,
            FcmDispatchedAt: DateTimeOffset.UtcNow,
            FcmLatencyMs: maxFcmLatencyMs,
            TotalPipelineMs: pipelineStopwatch.ElapsedMilliseconds,
            DevicesTargeted: totalDevicesTargeted,
            SuccessCount: totalSuccess,
            FailureCount: totalFailed,
            CleanedTokensCount: totalCleaned
        );

        logger.LogInformation(
            "NOTIFICATION_METRICS [SCRUM-364] EventId={EventId} AnalysisId={AnalysisId} Recipients={Recipients} DevicesTargeted={DevicesTargeted} FcmLatencyMs={FcmLatencyMs} TotalPipelineMs={TotalPipelineMs} CleanedTokens={Cleaned} Status=SUCCESS",
            eventId, analysisId, recipients.Count, totalDevicesTargeted, maxFcmLatencyMs, pipelineStopwatch.ElapsedMilliseconds, totalCleaned);

        return true;
    }

    private sealed record FcmDispatchInternalResult(
        int DevicesTargeted,
        int SuccessCount,
        int FailureCount,
        int CleanedTokensCount);

    private async Task<FcmDispatchInternalResult> SendWebPushNotificationAsync(
        Guid recipientId,
        Guid analysisId,
        string title,
        string bodyText,
        string targetPath,
        CancellationToken ct)
    {
        int targeted = 0;
        int success = 0;
        int failed = 0;
        int cleaned = 0;

        try
        {
            var projectId = configuration["Firebase:ProjectId"] ?? "aura-dev-97716";
            var firebaseUid = await accountResolver.ResolveFirebaseUidAsync(recipientId, ct);
            if (string.IsNullOrWhiteSpace(firebaseUid))
            {
                logger.LogDebug("No Firebase UID found for recipient Account {AccountId}", recipientId);
                return new FcmDispatchInternalResult(0, 0, 0, 0);
            }

            var devices = await deviceRepository.GetTokensForUserAsync(projectId, firebaseUid, ct);
            if (devices.Count == 0)
            {
                logger.LogDebug("No active device registrations found for Firebase UID {Uid}", firebaseUid);
                return new FcmDispatchInternalResult(0, 0, 0, 0);
            }

            targeted = devices.Count;

            var multicastMessage = new MulticastMessage
            {
                Tokens = devices.Select(d => d.Token).ToList(),
                Notification = new FirebaseAdmin.Messaging.Notification
                {
                    Title = title,
                    Body = bodyText
                },
                Data = new Dictionary<string, string>
                {
                    ["kind"] = "analysis-completed",
                    ["analysisId"] = analysisId.ToString("D"),
                    ["targetPath"] = targetPath,
                    ["url"] = targetPath
                },
                Webpush = new WebpushConfig
                {
                    Notification = new WebpushNotification
                    {
                        Title = title,
                        Body = bodyText,
                        Icon = "/favicon.ico",
                        Tag = $"analysis-{analysisId:D}"
                    },
                    FcmOptions = new WebpushFcmOptions
                    {
                        Link = targetPath
                    }
                }
            };

            FcmMulticastResult multicastResult;
            try
            {
                multicastResult = await _fcmClient.SendMulticastAsync(devices, multicastMessage, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FCM Multicast delivery attempt failed for User {Uid}. Retrying with bounded backoff...", firebaseUid);
                await Task.Delay(50, ct);
                multicastResult = await _fcmClient.SendMulticastAsync(devices, multicastMessage, ct);
            }

            success = multicastResult.SuccessCount;
            failed = multicastResult.FailureCount;

            // AC2: Inspect per-device responses and auto-cleanup unregistered or invalid tokens
            foreach (var outcome in multicastResult.Responses)
            {
                if (outcome.IsSuccess)
                {
                    logger.LogInformation(
                        "FCM Push delivered successfully to Device {DeviceId} (User {Uid}): MessageId={MessageId}",
                        outcome.DeviceId, firebaseUid, outcome.MessageId);
                }
                else
                {
                    logger.LogWarning(
                        "FCM Push failed for Device {DeviceId} (User {Uid}). Error: {Code}, Message: {Msg}",
                        outcome.DeviceId, firebaseUid, outcome.ErrorCode, outcome.ErrorMessage);

                    if (outcome.ErrorCode is "Unregistered" or "InvalidArgument" or "UNREGISTERED" or "INVALID_ARGUMENT")
                    {
                        logger.LogInformation("Auto-cleaning expired FCM token for Device {DeviceId} (Code: {Code})", outcome.DeviceId, outcome.ErrorCode);
                        try
                        {
                            await deviceRepository.DeleteIfTokenMatchesAsync(
                                projectId,
                                firebaseUid,
                                outcome.DeviceId,
                                outcome.DeviceToken,
                                ct);
                            cleaned++;
                        }
                        catch (Exception cleanupEx)
                        {
                            logger.LogWarning(cleanupEx, "Failed to delete expired token for Device {DeviceId}", outcome.DeviceId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispatch FCM push for recipient Account {AccountId}", recipientId);
        }

        return new FcmDispatchInternalResult(targeted, success, failed, cleaned);
    }

    private async Task<List<Guid>> ResolveRecipientsAsync(
        JsonElement root,
        Guid analysisId,
        CancellationToken ct)
    {
        var list = new List<Guid>();

        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("recipientAccountIds", out var recipientArray) && recipientArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in recipientArray.EnumerateArray())
                {
                    if (elem.TryGetGuid(out var accId) && accId != Guid.Empty && !list.Contains(accId))
                    {
                        list.Add(accId);
                    }
                }
            }

            if (data.TryGetProperty("actorAccountId", out var actorProp) && actorProp.TryGetGuid(out var actorId) && actorId != Guid.Empty)
            {
                if (!list.Contains(actorId))
                {
                    list.Add(actorId);
                }
            }
        }

        if (list.Count > 0)
        {
            return list;
        }

        // Query database analysis_requests to find the requester who created the analysis
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            const string sql = "SELECT requester_id, owner_id FROM analysis.analysis_requests WHERE id = @id LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", analysisId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var requesterId = reader.IsDBNull(0) ? Guid.Empty : reader.GetGuid(0);
                var ownerId = reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1);
                var targetId = requesterId != Guid.Empty ? requesterId : ownerId;
                if (targetId != Guid.Empty && !list.Contains(targetId))
                {
                    list.Add(targetId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query requester_id for analysis {AnalysisId}", analysisId);
        }

        return list;
    }

    private void Cleanup()
    {
        try { _channel?.Close(); _channel?.Dispose(); } catch { }
        try { _connection?.Close(); _connection?.Dispose(); } catch { }
        _channel = null;
        _connection = null;
    }
}
