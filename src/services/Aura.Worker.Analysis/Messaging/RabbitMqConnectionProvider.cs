using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Aura.Worker.Analysis.Configuration;

namespace Aura.Worker.Analysis.Messaging;

/// <summary>Shares one recoverable TCP connection while callers create a channel per operation.</summary>
public sealed class RabbitMqConnectionProvider(IOptions<RabbitMqOptions> options) : IDisposable
{
    private readonly object _sync = new();
    private IConnection? _connection;

    public IConnection GetConnection()
    {
        lock (_sync)
        {
            if (_connection is { IsOpen: true }) return _connection;
            _connection?.Dispose();
            var settings = options.Value;
            var factory = new ConnectionFactory
            {
                HostName = settings.HostName,
                Port = settings.Port,
                Ssl = new() { Enabled = settings.UseTls, ServerName = settings.HostName,
                    AcceptablePolicyErrors = System.Net.Security.SslPolicyErrors.None,
                    Version = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
                VirtualHost = settings.VirtualHost,
                UserName = settings.UserName,
                Password = settings.Password,
                AutomaticRecoveryEnabled = true
            };
            _connection = factory.CreateConnection();
            return _connection;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
