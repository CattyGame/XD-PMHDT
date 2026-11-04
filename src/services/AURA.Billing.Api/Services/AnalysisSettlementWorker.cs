using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AURA.Billing.Api.Services;

public sealed class BillingSettlementOptions
{
    public const string Section = "BillingSettlement";
    public bool Enabled { get; set; }
    public string ConsumerName { get; set; } = "s3-billing";
    public string Host { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/aura-s3";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Queue { get; set; } = "aura.s3.billing.events";
}

/// <summary>Consumes terminal Analysis events into a durable inbox and applies resolved settlements in Billing.</summary>
public sealed class AnalysisSettlementWorker(
    IOptions<BillingSettlementOptions> options,
    SettlementInboxRepository inbox,
    CreditReservationRepository reservations,
    AnalysisSettlementResolverClient resolver,
    NpgsqlDataSource dataSource,
    ILogger<AnalysisSettlementWorker> logger) : BackgroundService
{
    private readonly BillingSettlementOptions _options = options.Value;
    private const string ConsumerTag = "aura-s3-billing";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Billing settlement event processing is disabled by configuration.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.User) || string.IsNullOrWhiteSpace(_options.Password)
            || string.IsNullOrWhiteSpace(_options.Queue) || string.IsNullOrWhiteSpace(_options.ConsumerName))
            throw new InvalidOperationException("BillingSettlement RabbitMQ credentials, queue and consumer name are required when enabled.");

        await inbox.VerifyDatabaseAsync(stoppingToken);
        var processor = ProcessInboxLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ConsumeLoopAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Billing settlement RabbitMQ connection failed; retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        finally { await processor; }
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host, Port = _options.Port, VirtualHost = _options.VirtualHost,
            UserName = _options.User, Password = _options.Password,
            AutomaticRecoveryEnabled = true, RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            ClientProvidedName = "aura-billing-settlement-consumer"
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(0, 10, false, ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var incoming = ParseEvent(ea.Body.ToArray(), _options.ConsumerName);
                var result = await inbox.IngestAsync(incoming, ct);
                if (result.Status == SettlementInboxIngestStatus.PayloadConflict)
                    logger.LogError("Billing inbox held conflicting payload for event {EventId}.", incoming.EventId);
                await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
            }
            catch (JsonException)
            {
                logger.LogWarning("Rejected malformed Analysis settlement event from Billing queue.");
                await channel.BasicRejectAsync(ea.DeliveryTag, false, ct);
            }
            catch (ArgumentException exception)
            {
                logger.LogWarning("Rejected invalid Analysis settlement event: {Code}.", exception.Message);
                await channel.BasicRejectAsync(ea.DeliveryTag, false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not persist Analysis settlement event; requeueing.");
                await channel.BasicNackAsync(ea.DeliveryTag, false, true, ct);
            }
        };
        await channel.BasicConsumeAsync(_options.Queue, false, ConsumerTag, false, false, null, consumer, ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static SettlementInboxEvent ParseEvent(byte[] body, string consumerName)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        string RequiredString(string name) => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()! : throw new ArgumentException("INVALID_EVENT_ENVELOPE");
        Guid RequiredGuid(string name) => Guid.TryParseExact(RequiredString(name), "D", out var value) && value != Guid.Empty
            ? value : throw new ArgumentException("INVALID_EVENT_ENVELOPE");
        if (!root.TryGetProperty("eventType", out var eventTypeNode)
            || eventTypeNode.ValueKind != JsonValueKind.String
            || eventTypeNode.GetString() is not ("analysis.completed" or "analysis.failed"))
            throw new ArgumentException("UNSUPPORTED_EVENT_TYPE");
        if (!root.TryGetProperty("schemaVersion", out var versionNode)
            || versionNode.ValueKind != JsonValueKind.String || versionNode.GetString() is not ("0.7" or "0.8"))
            throw new ArgumentException("UNSUPPORTED_SCHEMA_VERSION");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("INVALID_EVENT_DATA");
        var occurredAtText = RequiredString("occurredAt");
        if (!occurredAtText.EndsWith('Z') || !DateTimeOffset.TryParse(occurredAtText, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var occurredAt))
            throw new ArgumentException("INVALID_EVENT_TIME");
        var rawHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return new SettlementInboxEvent(consumerName, RequiredGuid("eventId"), eventTypeNode.GetString()!,
            versionNode.GetString()!, occurredAt, RequiredGuid("tenantId"), RequiredGuid("analysisId"),
            RequiredString("correlationId"), rawHash);
    }

    private async Task ProcessInboxLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claim = await inbox.ClaimNextDueAsync(_options.ConsumerName, TimeSpan.FromSeconds(60), ct);
                if (claim is null) { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
                await ProcessClaimAsync(claim, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Billing settlement inbox processing failed; the durable lease will recover the claim.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async Task ProcessClaimAsync(SettlementInboxClaim claim, CancellationToken ct)
    {
        SettlementResolutionResponse resolution;
        try { resolution = await resolver.ResolveAsync(claim.EventId, claim.AnalysisId, claim.TenantId, claim.CorrelationId, ct); }
        catch (SettlementResolverCallException exception)
        {
            if (exception.Retryable) await inbox.ScheduleResolverRetryAsync(claim, exception.Code.Replace("ERR_", "", StringComparison.Ordinal), ct);
            else await inbox.MarkHoldAsync(claim, "RESOLVER_REJECTED", ct);
            return;
        }

        if (!resolution.Applicable)
        {
            if (resolution.Reason == "NO_RESERVATION") await FinishIgnoredAsync(claim, "NO_RESERVATION", ct);
            else if (resolution.Reason == "NOT_TERMINAL") await inbox.ScheduleResolverRetryAsync(claim, "NOT_TERMINAL", ct);
            else await inbox.MarkHoldAsync(claim, "RECONCILE_REQUIRED", ct);
            return;
        }

        if (resolution.ReservationId is not Guid reservationId || resolution.Generation is not int generation
            || resolution.IsMock != false)
        { await inbox.MarkHoldAsync(claim, "INVALID_SETTLEMENT_DECISION", ct); return; }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await inbox.HasValidLeaseForUpdateAsync(connection, transaction, claim, ct))
        { await transaction.RollbackAsync(ct); return; }
        var fingerprint = resolution.SettlementAction == "CAPTURE"
            ? ReservationFingerprint.ForCapture(reservationId)
            : ReservationFingerprint.ForRelease(reservationId, resolution.Reason);
        var operation = resolution.SettlementAction == "CAPTURE"
            ? await reservations.CaptureInTransactionAsync(connection, transaction, reservationId, claim.TenantId,
                claim.AnalysisId, generation, claim.EventId, fingerprint, ct)
            : await reservations.ReleaseInTransactionAsync(connection, transaction, reservationId, claim.TenantId,
                claim.AnalysisId, generation, claim.EventId, fingerprint, resolution.Reason, ct);
        if (operation.Status is not (ReservationOperationStatus.Changed or ReservationOperationStatus.Replayed))
        { await transaction.RollbackAsync(ct); await inbox.MarkHoldAsync(claim, "RESERVATION_STATE_CONFLICT", ct); return; }
        var decision = new SettlementInboxDecision(reservationId, generation, resolution.ResultVersion,
            resolution.IsMock!.Value, resolution.ProcessingStatus, resolution.SettlementAction, resolution.Reason);
        if (!await inbox.MarkCompletedInTransactionAsync(connection, transaction, claim, decision, ct))
        { await transaction.RollbackAsync(ct); return; }
        await transaction.CommitAsync(ct);
    }

    private async Task FinishIgnoredAsync(SettlementInboxClaim claim, string reason, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await inbox.HasValidLeaseForUpdateAsync(connection, transaction, claim, ct))
        { await transaction.RollbackAsync(ct); return; }
        if (!await inbox.MarkIgnoredInTransactionAsync(connection, transaction, claim, reason, ct))
        { await transaction.RollbackAsync(ct); return; }
        await transaction.CommitAsync(ct);
    }
}
