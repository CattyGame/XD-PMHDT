using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AURA.Common.Audit;

public interface IAuditTransport
{
    Task SendAsync(AuditEvent e, CancellationToken ct);
}

public sealed class LocalAuditTransport(AuditProjection projection) : IAuditTransport
{
    public Task SendAsync(AuditEvent e, CancellationToken ct) => projection.AcceptAsync(e, ct);
}

public sealed class AuditPublisher(AuditStore store, IAuditTransport transport, ILogger<AuditPublisher> logger)
    : BackgroundService
{
    public async Task<bool> DispatchOnceAsync(CancellationToken ct)
    {
        var claim = await store.ClaimAsync(ct);
        if (claim is null) return false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await transport.SendAsync(claim.Event, deadline.Token);
            if (!await store.CompleteAsync(claim, ct))
                logger.LogWarning("Audit delivery lease expired for {EventId}; replay remains safe.", claim.Event.EventId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            // Exception messages can contain URLs, provider responses or credentials.
            logger.LogWarning("Audit delivery deferred for {EventId}: {ErrorType}.", claim.Event.EventId, e.GetType().Name);
            await store.RetryAsync(claim, ct);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRetention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextRetention)
                {
                    await store.PurgeExpiredAsync(stoppingToken);
                    nextRetention = DateTimeOffset.UtcNow.AddHours(1);
                }
                if (!await DispatchOnceAsync(stoppingToken)) await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogWarning("Audit worker dependency unavailable: {ErrorType}.", e.GetType().Name);
                await Task.Delay(3000, stoppingToken);
            }
        }
    }
}
