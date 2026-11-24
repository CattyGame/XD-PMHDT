using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace AURA.Analysis.Api.Outbox.Adapters;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";
    public string Host { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public bool UseTls { get; set; }
    /// <summary>config/common.yaml messaging.connectionTimeoutSeconds = 5.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 5;
    public string VirtualHost { get; set; } = "/aura-s3";
    /// <summary>Tai khoan publisher rieng; mat khau lay tu bien moi truong/secret file, KHONG dat trong appsettings repo.</summary>
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

/// <summary>Publish persistent + mandatory + publisher confirm (RabbitMQ.Client 7.x).
/// Chi tra Confirmed khi broker ack va khong bi return. Moi loi ket noi -> BrokerUnavailable va dong channel de tao lai.
/// Khong tu declare exchange/queue (topology do M1 tao, tai khoan app khong co quyen configure).
/// LUU Y: can package RabbitMQ.Client nen chua bien dich trong moi truong soan thao; kiem tra ten API (PublishException.IsReturn,
/// CreateChannelOptions) theo phien ban 7.x ban cai, va chay test tich hop voi broker that (S3-03/S3-08).</summary>
public sealed class RabbitMqBrokerPublisher : IBrokerPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _opt;
    private readonly ILogger<RabbitMqBrokerPublisher> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _conn;
    private IChannel? _ch;

    public RabbitMqBrokerPublisher(Microsoft.Extensions.Options.IOptions<RabbitMqOptions> opt, ILogger<RabbitMqBrokerPublisher> log)
    { _opt = opt.Value; _log = log; }

    public async Task<PublishOutcome> PublishAsync(BrokerMessage m, TimeSpan confirmTimeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ch = await EnsureChannelAsync(ct);
            var props = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = m.MessageId,
                Type = m.Type,
                CorrelationId = m.CorrelationId
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(confirmTimeout);
            try
            {
                // Voi publisherConfirmationTrackingEnabled, BasicPublishAsync cho den khi co ack; nack/return -> PublishException.
                await ch.BasicPublishAsync(m.Exchange, m.RoutingKey, mandatory: true, basicProperties: props, body: m.Body, cancellationToken: timeout.Token);
                return PublishOutcome.Confirmed;
            }
            catch (PublishException pe)
            {
                return pe.IsReturn ? PublishOutcome.Returned : PublishOutcome.Nacked;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Het thoi gian cho confirm: ket qua CHUA RO (broker co the da nhan). Channel co the lech trang thai -> tao lai.
                await ResetAsync();
                return PublishOutcome.ConfirmTimeout;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is AlreadyClosedException or BrokerUnreachableException or IOException
                                    or System.Net.Sockets.SocketException or TimeoutException or OperationInterruptedException)
        {
            _log.LogWarning(ex, "RabbitMQ khong san sang");
            await ResetAsync();
            return PublishOutcome.BrokerUnavailable;
        }
        finally { _gate.Release(); }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_ch is { IsOpen: true } && _conn is { IsOpen: true }) return _ch;
        await ResetAsync();
        var factory = new ConnectionFactory
        {
            HostName = _opt.Host, Port = _opt.Port, VirtualHost = _opt.VirtualHost,
            Ssl = new() { Enabled = _opt.UseTls, ServerName = _opt.Host,
                AcceptablePolicyErrors = System.Net.Security.SslPolicyErrors.None,
                Version = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
            UserName = _opt.User, Password = _opt.Password, AutomaticRecoveryEnabled = true,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(_opt.ConnectionTimeoutSeconds),
            ClientProvidedName = "aura-analysis-outbox-publisher"
        };
        _conn = await factory.CreateConnectionAsync(ct);
        _ch = await _conn.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        return _ch;
    }

    private async Task ResetAsync()
    {
        try { if (_ch is not null) await _ch.DisposeAsync(); } catch { }
        try { if (_conn is not null) await _conn.DisposeAsync(); } catch { }
        _ch = null; _conn = null;
    }

    public async ValueTask DisposeAsync() { await ResetAsync(); _gate.Dispose(); }
}
