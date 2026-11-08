using System.Text;
using System.Text.Json;
using AURA.Billing.Api.Data;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AURA.Billing.Api.Services;

public interface IPaymentEventTransport
{
    Task PublishAsync(PaymentOutboxEvent item, CancellationToken ct);
}

public sealed class RabbitPaymentEventTransport(IOptions<BillingSettlementOptions> options) : IPaymentEventTransport
{
    public async Task PublishAsync(PaymentOutboxEvent item, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.User) || string.IsNullOrWhiteSpace(o.Password))
            throw new InvalidOperationException("Explicit payment broker credentials are required.");
        var factory = new ConnectionFactory
        {
            HostName=o.Host,Port=o.Port,VirtualHost=o.VirtualHost,UserName=o.User,Password=o.Password,
            RequestedConnectionTimeout=TimeSpan.FromSeconds(5),ClientProvidedName="aura-billing-payment-outbox"
        };
        await using var connection=await factory.CreateConnectionAsync(ct);
        await using var channel=await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled:true, publisherConfirmationTrackingEnabled:true), ct);
        var payload=JsonSerializer.Serialize(new {
            eventId=item.EventId,eventType="billing.payment.success",schemaVersion=item.SchemaVersion,
            orderId=item.OrderId,tenantId=item.TenantId,occurredAt=item.OccurredAt
        });
        // Confirm tracking makes returns/nacks fail; unroutable events stay in the durable outbox.
        await channel.BasicPublishAsync("aura.s3.events","billing.payment.success",mandatory:true,
            basicProperties:new BasicProperties { MessageId=item.EventId.ToString("D"),Type="billing.payment.success",
                ContentType="application/json",DeliveryMode=DeliveryModes.Persistent },
            body:Encoding.UTF8.GetBytes(payload),cancellationToken:ct);
    }
}

public sealed class BillingPaymentEventPublisher(
    PaymentNotificationRepository outbox, IPaymentEventTransport transport, IConfiguration configuration,
    ILogger<BillingPaymentEventPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("BillingPaymentEvents:Enabled")) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DispatchOnceAsync(stoppingToken)) await Task.Delay(1500,stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Payment outbox unavailable: {ErrorType}",ex.GetType().Name);
                await Task.Delay(3000,stoppingToken);
            }
        }
    }

    public async Task<bool> DispatchOnceAsync(CancellationToken ct)
    {
        var item=await outbox.ClaimAsync(ct);
        if (item is null) return false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await transport.PublishAsync(item,timeout.Token);
            await outbox.CompleteAsync(item,ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await outbox.DeferAsync(item,ex.GetType().Name,ct);
            logger.LogWarning("Payment event {EventId} retained for retry: {ErrorType}",item.EventId,ex.GetType().Name);
        }
        return true;
    }
}
