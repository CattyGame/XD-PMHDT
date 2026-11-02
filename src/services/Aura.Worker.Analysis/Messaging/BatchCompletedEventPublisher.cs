using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Processing;
using AURA.Common.Cache;

namespace Aura.Worker.Analysis.Messaging;

public interface IBatchCompletedEventPublisher
{
    Task PublishAsync(Guid tenantId, Guid batchId, Guid patientId, string correlationId,
        BatchImageCompletionResult progress, CancellationToken cancellationToken);
}

public sealed class BatchCompletedEventPublisher(
    IOptions<RabbitMqOptions> options,
    RabbitMqConnectionProvider connections) : IBatchCompletedEventPublisher
{
    public Task PublishAsync(Guid tenantId, Guid batchId, Guid patientId, string correlationId,
        BatchImageCompletionResult progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!progress.IsBatchFinished || progress.BatchId != batchId || tenantId == Guid.Empty || patientId == Guid.Empty)
            throw new ArgumentException("Only a completed, correctly bound batch can be published.");

        var message = OutboxEventFactory.CreateBatchCompletedEvent(tenantId, batchId, patientId, correlationId,
            progress.BatchStatus, progress.Total, progress.Completed, progress.Failed);
        var settings = options.Value;
        using var channel = connections.GetConnection().CreateModel();
        channel.ConfirmSelect();
        var properties = channel.CreateBasicProperties();
        properties.MessageId = message.EventId.ToString("D");
        properties.Type = message.EventType;
        properties.CorrelationId = correlationId;
        properties.ContentType = "application/json";
        properties.DeliveryMode = 2;
        channel.BasicPublish(settings.EventsExchange, message.EventType, mandatory: true, properties,
            Encoding.UTF8.GetBytes(message.PayloadJson));
        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }
}
