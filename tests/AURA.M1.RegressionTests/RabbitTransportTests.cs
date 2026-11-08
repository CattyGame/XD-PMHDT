using System.Text.Json;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Services;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Xunit;

namespace AURA.M1.RegressionTests;

public sealed class M1RabbitFactAttribute : FactAttribute
{
    public M1RabbitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_TEST_RABBITMQ_PASSWORD")))
            Skip="Provision the isolated M1 topology and explicit broker credentials.";
    }
}

public class RabbitTransportTests
{
    [M1RabbitFact]
    public async Task Confirmed_payment_reaches_notification_queue_with_stable_identity_and_limited_permissions()
    {
        var password=Environment.GetEnvironmentVariable("AURA_TEST_RABBITMQ_PASSWORD")!;
        var port=int.Parse(Environment.GetEnvironmentVariable("AURA_TEST_RABBITMQ_PORT")??"56739");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var factory=new ConnectionFactory {HostName="127.0.0.1",Port=port,VirtualHost="/aura-s3",UserName="guest",Password=password};
        await using var connection=await factory.CreateConnectionAsync(timeout.Token);
        await using var channel=await connection.CreateChannelAsync(cancellationToken:timeout.Token);
        var item=new PaymentOutboxEvent(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"1.0",DateTimeOffset.UtcNow,Guid.NewGuid(),1);
        var transport=new RabbitPaymentEventTransport(Options.Create(new BillingSettlementOptions {
            Host="127.0.0.1",Port=port,VirtualHost="/aura-s3",User="s3-billing",Password=password
        }));
        await transport.PublishAsync(item,timeout.Token);
        var result=await channel.BasicGetAsync("aura.s3.notification.events",autoAck:false,timeout.Token);
        Assert.NotNull(result);
        using var payload=JsonDocument.Parse(result.Body);
        Assert.Equal(item.EventId,payload.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(item.OrderId,payload.RootElement.GetProperty("orderId").GetGuid());
        Assert.Equal(item.TenantId,payload.RootElement.GetProperty("tenantId").GetGuid());
        Assert.False(payload.RootElement.TryGetProperty("recipientAccountId",out _));
        Assert.Equal(item.EventId.ToString("D"),result.BasicProperties.MessageId);
        await channel.BasicAckAsync(result.DeliveryTag,false,timeout.Token);

        // Mandatory return must fail rather than silently lose a paid order event.
        await channel.QueueUnbindAsync("aura.s3.notification.events","aura.s3.events","billing.payment.success",cancellationToken:timeout.Token);
        try { await Assert.ThrowsAnyAsync<Exception>(()=>transport.PublishAsync(item,timeout.Token)); }
        finally { await channel.QueueBindAsync("aura.s3.notification.events","aura.s3.events","billing.payment.success",cancellationToken:timeout.Token); }

        factory.UserName="s3-billing";
        await using var restricted=await factory.CreateConnectionAsync(timeout.Token);
        await using var restrictedChannel=await restricted.CreateChannelAsync(cancellationToken:timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async()=>await restrictedChannel.BasicGetAsync("aura.s3.notification.events",true,timeout.Token));
    }
}
