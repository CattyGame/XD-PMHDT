using System.Reflection;
using System.Text;
using System.Text.Json;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Messaging;
using Aura.Worker.Analysis.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;

namespace Aura.Worker.Analysis.Tests;

public class TransportRecoveryTests
{
    [Theory]
    [InlineData("retry")]
    [InlineData("dead")]
    [InlineData("returned")]
    [InlineData("timeout")]
    [InlineData("lease")]
    public async Task Transport_handoff_uses_bound_route_and_never_acks_before_confirm(string outcome)
    {
        var confirmed = false;
        var publisher = new Mock<IModel>();
        var consumer = new Mock<IModel>();
        var connection = new Mock<IConnection>();
        connection.Setup(x => x.CreateModel()).Returns(publisher.Object);
        publisher.Setup(x => x.CreateBasicProperties()).Returns(new Mock<IBasicProperties>().SetupAllProperties().Object);
        publisher.Setup(x => x.WaitForConfirmsOrDie(It.IsAny<TimeSpan>())).Callback(() =>
        {
            if (outcome == "timeout") throw new TimeoutException();
            confirmed = true;
        });
        var options = new RabbitMqOptions();
        var exchange = outcome == "dead" ? "aura.s3.dead.jobs" : "aura.s3.retry.jobs";
        publisher.Setup(x => x.BasicPublish(exchange, "jobs", true, It.IsAny<IBasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>()))
            .Callback(() =>
            {
                if (outcome == "returned") publisher.Raise(x => x.BasicReturn += null, new BasicReturnEventArgs());
            });
        consumer.Setup(x => x.BasicAck(1, false)).Callback(() => Assert.True(confirmed));
        var processor = new Mock<IJobProcessor>();
        processor.Setup(x => x.ProcessAsync(It.IsAny<JobRunMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome == "lease" ? JobProcessResult.WaitForLease : JobProcessResult.Requeue);
        using var services = new ServiceCollection().AddScoped(_ => processor.Object).BuildServiceProvider();
        var worker = new JobRunConsumer(Options.Create(options), Options.Create(new WorkerOptions()), services,
            NullLogger<JobRunConsumer>.Instance);
        typeof(JobRunConsumer).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, connection.Object);
        typeof(JobRunConsumer).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(worker, consumer.Object);
        var properties = new Mock<IBasicProperties>().SetupAllProperties().Object;
        properties.Headers = new Dictionary<string, object>();
        if (outcome is "dead" or "lease") properties.Headers["x-aura-transport-retry-count"] = Encoding.UTF8.GetBytes("3");
        var delivery = new BasicDeliverEventArgs("test", 1, false, "aura.s3.jobs", "analysis.job.run", properties,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new JobRunMessage { MessageId = Guid.NewGuid() })));
        var invoke = typeof(JobRunConsumer).GetMethod("HandleMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)invoke.Invoke(worker, [delivery, CancellationToken.None])!;
        publisher.Verify(x => x.BasicPublish(exchange, "jobs", true, It.IsAny<IBasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>()), Times.Once);
        publisher.Verify(x => x.ConfirmSelect(), Times.Once);
        consumer.Verify(x => x.BasicAck(1, false), outcome is "retry" or "dead" or "lease" ? Times.Once() : Times.Never());
        if (outcome == "lease") publisher.Verify(x => x.BasicPublish(exchange, "jobs", true,
            It.Is<IBasicProperties>(p => Encoding.UTF8.GetString((byte[])p.Headers["x-aura-transport-retry-count"]) == "3"),
            It.IsAny<ReadOnlyMemory<byte>>()), Times.Once);
        consumer.Verify(x => x.BasicNack(1, false, true), outcome is "returned" or "timeout" ? Times.Once() : Times.Never());
    }
}
