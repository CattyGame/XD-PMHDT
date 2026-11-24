using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Messaging;
using Aura.Worker.Analysis.Processing;

namespace Aura.Worker.Analysis.Messaging;

public class JobRunConsumer : BackgroundService
{
    private readonly RabbitMqOptions _rabbitOptions;
    private readonly WorkerOptions _workerOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JobRunConsumer> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public JobRunConsumer(
        IOptions<RabbitMqOptions> rabbitOptions,
        IOptions<WorkerOptions> workerOptions,
        IServiceProvider serviceProvider,
        ILogger<JobRunConsumer> logger)
    {
        _rabbitOptions = rabbitOptions.Value;
        _workerOptions = workerOptions.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    queue: _rabbitOptions.QueueName,
                    autoAck: false,
                    consumer: consumer);

                _logger.LogInformation("JobRunConsumer started listening on {Queue} with prefetch {Prefetch}",
                    _rabbitOptions.QueueName, _workerOptions.Prefetch);

                // Wait until cancellation
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RabbitMQ connection error. Retrying in 5 seconds...");
                await Task.Delay(5000, stoppingToken);
            }
        }

        Cleanup();
    }

    private void InitializeRabbitMq()
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitOptions.HostName,
            Port = _rabbitOptions.Port,
            Ssl = new() { Enabled = _rabbitOptions.UseTls, ServerName = _rabbitOptions.HostName,
                AcceptablePolicyErrors = System.Net.Security.SslPolicyErrors.None,
                Version = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
            VirtualHost = _rabbitOptions.VirtualHost,
            UserName = _rabbitOptions.UserName,
            Password = _rabbitOptions.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.BasicQos(prefetchSize: 0, prefetchCount: _workerOptions.Prefetch, global: false);
    }

    private async Task HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        ulong deliveryTag = ea.DeliveryTag;
        int transportRetryCount = GetTransportRetryCount(ea.BasicProperties);

        try
        {
            var body = ea.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            var message = JsonSerializer.Deserialize<JobRunMessage>(json);

            if (message == null)
            {
                _logger.LogError("Received invalid JSON payload on queue. Poison message moved out.");
                _channel?.BasicAck(deliveryTag, multiple: false);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IJobProcessor>();

            var result = await processor.ProcessAsync(message, cancellationToken);

            switch (result)
            {
                case JobProcessResult.AckSuccess:
                case JobProcessResult.AckFailure:
                case JobProcessResult.AckDuplicate:
                    // ACK ONLY after transaction committed or recognized duplicate
                    _channel?.BasicAck(deliveryTag, multiple: false);
                    _logger.LogInformation("Message {MessageId} ACKed ({Result})", message.MessageId, result);
                    break;

                case JobProcessResult.WaitForLease:
                    // A live lease is not a failed attempt; retry TTL backs off without consuming the error budget.
                    PublishToRetryQueue(ea, transportRetryCount);
                    _channel?.BasicAck(deliveryTag, false);
                    break;

                case JobProcessResult.Requeue:
                    // Transport backoff before requeuing
                    if (transportRetryCount >= 3)
                    {
                        _logger.LogError("Transport retry limit reached ({Count}) for message {MessageId}. Moving to dead letter.",
                            transportRetryCount, message.MessageId);
                        PublishToDeadLetter(ea, message);
                        _channel?.BasicAck(deliveryTag, false);
                    }
                    else
                    {
                        PublishToRetryQueue(ea, transportRetryCount + 1);
                        _channel?.BasicAck(deliveryTag, false);
                    }
                    break;

                case JobProcessResult.HoldInvestigate:
                    _logger.LogCritical("Job {JobId} held for operator investigation. Message will NOT be ACKed automatically.", message.JobId);
                    // Leave unacknowledged so it stays visible for inspection
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing message delivery tag {DeliveryTag}", deliveryTag);
            if (transportRetryCount >= 3)
            {
                _channel?.BasicReject(deliveryTag, requeue: false);
            }
            else
            {
                _channel?.BasicNack(deliveryTag, multiple: false, requeue: true);
            }
        }
    }

    private static int GetTransportRetryCount(IBasicProperties properties)
    {
        if (properties.Headers != null && properties.Headers.TryGetValue("x-aura-transport-retry-count", out var value))
        {
            if (value is byte[] bytes && int.TryParse(Encoding.UTF8.GetString(bytes), out var count))
            {
                return count;
            }
        }
        return 0;
    }

    private void PublishToRetryQueue(BasicDeliverEventArgs ea, int nextCount)
    {
        PublishTransport(ea, _rabbitOptions.RetryExchange, nextCount);
        _logger.LogInformation("Message published to retry exchange with count {Count}", nextCount);
    }

    private void PublishToDeadLetter(BasicDeliverEventArgs ea, JobRunMessage msg)
    {
        PublishTransport(ea, _rabbitOptions.DeadExchange, null);
        _logger.LogWarning("Message {MessageId} published to dead exchange", msg.MessageId);
    }

    private void PublishTransport(BasicDeliverEventArgs ea, string exchange, int? retryCount)
    {
        // Separate publisher channel: a publish failure must not close the channel owning the delivery.
        using var publisher = _connection?.CreateModel() ?? throw new InvalidOperationException("Broker unavailable.");
        publisher.ConfirmSelect();
        var returned = 0;
        publisher.BasicReturn += (_, _) => Interlocked.Exchange(ref returned, 1);
        var props = publisher.CreateBasicProperties();
        props.DeliveryMode = 2;
        props.MessageId = ea.BasicProperties.MessageId;
        props.ContentType = ea.BasicProperties.ContentType;
        props.CorrelationId = ea.BasicProperties.CorrelationId;
        props.Headers = new Dictionary<string, object>(ea.BasicProperties.Headers ?? new Dictionary<string, object>());
        if (retryCount is { } count) props.Headers["x-aura-transport-retry-count"] = Encoding.UTF8.GetBytes(count.ToString());
        // The canonical retry/dead bindings use "jobs", not the incoming "analysis.job.run" key.
        publisher.BasicPublish(exchange, "jobs", mandatory: true, props, ea.Body);
        publisher.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
        if (Volatile.Read(ref returned) != 0) throw new InvalidOperationException("Transport handoff was unroutable.");
    }

    private void Cleanup()
    {
        try
        {
            _channel?.Close();
            _connection?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing RabbitMQ connection");
        }
    }
}
