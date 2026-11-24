using System.Net;
using System.Net.Sockets;
using AURA.Analysis.Api.Outbox;
using AURA.Analysis.Api.Outbox.Adapters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public class RabbitTlsTests
{
    [Theory]
    [InlineData(true, 22)]
    [InlineData(false, 65)]
    public async Task Configured_tls_starts_a_handshake_before_sending_amqp(bool tls, byte expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var values = new Dictionary<string, string?>
            {
                ["Host"] = "127.0.0.1", ["Port"] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
                ["UseTls"] = tls.ToString(), ["ConnectionTimeoutSeconds"] = "1",
                ["User"] = "fixture-only", ["Password"] = "fixture-only"
            };
            var options = new ConfigurationBuilder().AddInMemoryCollection(values).Build().Get<RabbitMqOptions>()!;
            await using var publisher = new RabbitMqBrokerPublisher(Options.Create(options), NullLogger<RabbitMqBrokerPublisher>.Instance);
            var publishing = publisher.PublishAsync(new("unused", "unused", "fixture", "fixture", "fixture", ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromSeconds(1), deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var prefix = new byte[1];
            await peer.GetStream().ReadExactlyAsync(prefix, deadline.Token);
            peer.Close();
            Assert.Equal(PublishOutcome.BrokerUnavailable, await publishing);
            Assert.Equal(expected, prefix[0]);
        }
        finally { listener.Stop(); }
    }
}
