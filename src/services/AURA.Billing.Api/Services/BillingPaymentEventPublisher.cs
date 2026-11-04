using System.Text;
using System.Text.Json;
using AURA.Billing.Api.Data;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace AURA.Billing.Api.Services;

public interface IBillingPaymentEventPublisher
{
    Task PublishPaymentSuccessAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class BillingPaymentEventPublisher(
    IOptions<BillingSettlementOptions> options,
    OrderRepository orderRepository,
    ILogger<BillingPaymentEventPublisher> logger) : IBillingPaymentEventPublisher
{
    private readonly BillingSettlementOptions _options = options.Value;

    public async Task PublishPaymentSuccessAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orderRepository.GetOrderByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("Cannot publish billing.payment.success: order {OrderId} not found.", orderId);
            return;
        }

        var eventId = Guid.NewGuid();
        var payload = new
        {
            eventId,
            eventType = "billing.payment.success",
            orderId = order.Id,
            tenantId = order.TenantId,
            billingOwnerType = order.BillingOwnerType,
            billingOwnerId = order.BillingOwnerId,
            creditsAdded = order.CreditsSnapshot,
            amount = order.PriceAmountSnapshot,
            currency = order.CurrencyCodeSnapshot,
            environment = order.Environment,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(payload);

        if (string.IsNullOrWhiteSpace(_options.Host) || string.IsNullOrWhiteSpace(_options.Password))
        {
            logger.LogInformation("RabbitMQ not configured for payment events; skipping broker publish. EventId={EventId}, OrderId={OrderId}",
                eventId, orderId);
            return;
        }

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.User,
                Password = _options.Password,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                ClientProvidedName = "aura-billing-payment-publisher"
            };

            await using var connection = await factory.CreateConnectionAsync(cancellationToken);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            var props = new BasicProperties
            {
                MessageId = eventId.ToString("D"),
                Type = "billing.payment.success",
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent
            };

            var body = Encoding.UTF8.GetBytes(json);
            await channel.BasicPublishAsync(
                exchange: "aura.s3.events",
                routingKey: "billing.payment.success",
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: cancellationToken);

            logger.LogInformation(
                "Published billing.payment.success event {EventId} for OrderId {OrderId} to RabbitMQ.",
                eventId, orderId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish billing.payment.success event to RabbitMQ for OrderId {OrderId}.", orderId);
        }
    }
}
