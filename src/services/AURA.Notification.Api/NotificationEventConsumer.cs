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
    public string Password { get; set; } = "";
    public string QueueName { get; set; } = "aura.s3.notification.events";
    public ushort PrefetchCount { get; set; } = 10;
    public bool AutoStart { get; set; } = false;
}

public sealed class NotificationEventConsumer(
    IOptions<RabbitMqConsumerOptions> options,
    NotificationInboxRepository inboxRepository,
    DeviceRegistrationRepository deviceRepository,
    IAccountResolver accountResolver,
    IConfiguration configuration,
    ILogger<NotificationEventConsumer> logger,
    INotificationContextResolver? contextResolver = null,
    NotificationDeliveryJobRepository? deliveryJobRepository = null) : BackgroundService
{
    private readonly RabbitMqConsumerOptions _options = options.Value;
    private IConnection? _connection;
    private IModel? _channel;


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
        if(string.IsNullOrWhiteSpace(_options.Password))throw new InvalidOperationException("RabbitMQ password must be explicitly configured.");
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
                _channel?.BasicNack(deliveryTag, false, true);
            }
        }
        catch (NotificationContextRejectedException)
        {
            logger.LogWarning("Rejected notification authority context; moving event to dead letter.");
            _channel?.BasicNack(deliveryTag, false, false);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,"Malformed notification JSON rejected to dead-letter queue.");
            _channel?.BasicNack(deliveryTag,false,false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process notification message. Requeuing to preserve the delivery obligation.");
            _channel?.BasicNack(deliveryTag, false, true);
        }
    }

    public async Task<bool> ProcessNotificationEventAsync(string json, CancellationToken cancellationToken)
    {

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("eventId", out var eventIdProp) || eventIdProp.ValueKind != JsonValueKind.String || !eventIdProp.TryGetGuid(out var eventId) || eventId == Guid.Empty ||
            !root.TryGetProperty("eventType", out var eventTypeProp) || !root.TryGetProperty("tenantId", out var tenantIdProp) ||
            eventTypeProp.ValueKind != JsonValueKind.String || tenantIdProp.ValueKind != JsonValueKind.String ||
            !tenantIdProp.TryGetGuid(out var tenantId) || tenantId == Guid.Empty)
        {
            throw new NotificationContextRejectedException();
        }

        var eventType = eventTypeProp.GetString();
        if (eventType is not ("analysis.completed" or "analysis.failed" or "batch.completed" or "billing.payment.success"))
        {
            logger.LogInformation("Ignoring non-terminal event type {EventType}", eventType);
            return true;
        }

        Guid? analysisId = null;
        Guid? orderId = null;
        List<Guid> recipients;
        string inboxType;
        string title;
        string targetPath;

        if (eventType == "billing.payment.success")
        {
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != "1.0" ||
                !root.TryGetProperty("orderId", out var orderIdProp) || orderIdProp.ValueKind != JsonValueKind.String ||
                !orderIdProp.TryGetGuid(out var parsedOrderId) || parsedOrderId == Guid.Empty)
            {
                throw new NotificationContextRejectedException();
            }
            orderId = parsedOrderId;

            if (contextResolver is null) throw new InvalidOperationException("Notification authority is required.");
            var paymentAuth = await contextResolver.ResolvePaymentAsync(eventId, parsedOrderId, tenantId, cancellationToken);
            if (paymentAuth.EventId != eventId || paymentAuth.OrderId != parsedOrderId || paymentAuth.TenantId != tenantId ||
                paymentAuth.RecipientAccountId == Guid.Empty || paymentAuth.Status != "COMPLETED")
                throw new NotificationContextRejectedException();

            recipients = [paymentAuth.RecipientAccountId];
            inboxType = "PAYMENT_SUCCESS";
            title = "Thanh toán thành công";
            targetPath = $"/billing/orders/{parsedOrderId:D}";
        }
        else
        {
            if (!root.TryGetProperty("analysisId", out var analysisIdProp) || !analysisIdProp.TryGetGuid(out var parsedAnalysisId))
            {
                logger.LogWarning("Discarding analysis/batch event without valid analysisId.");
                return true;
            }
            analysisId = parsedAnalysisId;

            if (contextResolver is null) throw new InvalidOperationException("Notification authority is required.");
            var authority = await contextResolver.ResolveAsync(eventId, parsedAnalysisId, tenantId, cancellationToken);
            if (authority.EventId != eventId || authority.AnalysisId != parsedAnalysisId || authority.TenantId != tenantId || !authority.Applicable ||
                (eventType == "batch.completed" ? authority.ProcessingStatus is not ("COMPLETED" or "PARTIAL_FAILED" or "FAILED")
                    : eventType == "analysis.failed" ? authority.ProcessingStatus is not ("FAILED" or "LOW_QUALITY" or "REJECTED")
                    : authority.ProcessingStatus != "COMPLETED"))
                throw new HttpRequestException("Terminal notification authority unavailable or state not applicable.");

            recipients = authority.RecipientAccountIds.Where(x => x != Guid.Empty).Distinct().ToList();
            if (recipients.Count == 0) throw new HttpRequestException("No authoritative recipients; retry required.");

            inboxType = eventType switch
            {
                "analysis.completed" or "batch.completed" => "ANALYSIS_COMPLETED",
                _ => "ANALYSIS_FAILED"
            };

            // SRS §7.5: Title and body must be neutral, strictly free from patient personal data and clinical diagnosis
            title = eventType switch
            {
                "analysis.completed" => "Phân tích ảnh đáy mắt hoàn tất",
                "batch.completed" => "Đợt xử lý ảnh đã kết thúc",
                _ => "Phân tích ảnh đáy mắt không thành công"
            };
            targetPath = eventType == "batch.completed" ? $"/batches/{parsedAnalysisId:D}" : $"/analyses/{parsedAnalysisId:D}";
        }

        int totalDevicesTargeted = 0;
        var projectId = configuration["Firebase:ProjectId"] ?? "aura-dev-97716";

        // Resolve current Identity authority before any writes: dependency failures must requeue.
        var activeRecipients = new List<Guid>();
        foreach (var recipient in recipients)
        {
            if (!string.IsNullOrWhiteSpace(await accountResolver.ResolveFirebaseUidAsync(recipient, cancellationToken)))
                activeRecipients.Add(recipient);
        }

        foreach (var recipientId in activeRecipients)
        {
            // 1. Persist Before ACK: Save to durable PostgreSQL inbox & delivery jobs (SCRUM-364)
            var command = new SaveInboxItemCommand(
                AccountId: recipientId,
                TenantId: tenantId,
                EventId: eventId,
                AnalysisId: analysisId,
                Type: inboxType,
                Title: title,
                TargetPath: targetPath,
                Metadata: json,
                OrderId: orderId
            );

            var firebaseUid = await accountResolver.ResolveFirebaseUidAsync(recipientId, cancellationToken);
            var devices = !string.IsNullOrWhiteSpace(firebaseUid)
                ? await deviceRepository.GetTokensForUserAsync(projectId, firebaseUid, cancellationToken)
                : new List<ActiveDeviceToken>();

            if (deliveryJobRepository is null)
                throw new InvalidOperationException("Durable notification delivery repository is required.");
            var deviceHashes = devices.Select(d => (d.DeviceId,
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(d.Token)))).ToList();
            var saved = await inboxRepository.SaveWithDeliveryJobsAsync(command, deviceHashes, deliveryJobRepository, cancellationToken);
            if (saved.Outcome == SaveInboxOutcome.Created) totalDevicesTargeted += devices.Count;
        }

        logger.LogInformation("Notification event {EventId} persisted before ACK; {Devices} device jobs admitted.", eventId, totalDevicesTargeted);
        return true;
    }

    private void Cleanup()
    {
        try { _channel?.Close(); _channel?.Dispose(); } catch { }
        try { _connection?.Close(); _connection?.Dispose(); } catch { }
        _channel = null;
        _connection = null;
    }
}
