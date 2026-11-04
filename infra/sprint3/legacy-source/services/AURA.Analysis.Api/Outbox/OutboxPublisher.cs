using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AURA.Analysis.Api.Outbox;

/// <summary>Mot vong publish: claim -&gt; publish (confirm) -&gt; cap nhat DB.
/// Bat bien: published_at chi duoc ghi SAU khi broker confirm va khong bi return. Crash giua publish va cap nhat DB
/// => lease het han => phat lai CUNG eventId/messageId (at-least-once; consumer phai idempotent). Khong exactly-once.</summary>
public sealed class OutboxPublisher
{
    private readonly IOutboxStore _store;
    private readonly IBrokerPublisher _broker;
    private readonly OutboxOptions _opt;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxPublisher> _log;

    public OutboxPublisher(IOutboxStore store, IBrokerPublisher broker, IOptions<OutboxOptions> options,
        TimeProvider clock, ILogger<OutboxPublisher> log)
    {
        _store = store; _broker = broker; _opt = options.Value; _clock = clock; _log = log;
        _opt.Validate();
    }

    public async Task<OutboxRunResult> RunOnceAsync(CancellationToken ct)
    {
        if (!_opt.Enabled) return OutboxRunResult.Empty;
        var now = _clock.GetUtcNow();
        var claim = new OutboxClaim(now, _opt.BatchSize, TimeSpan.FromSeconds(_opt.LeaseSeconds),
            _opt.MaxPublishAttempts, _opt.MinOccurredAtUtc ?? DateTimeOffset.MaxValue, _opt.ExcludedTenantIds.ToArray());
        int swept = await _store.SweepExhaustedAsync(claim, ct);
        if (swept > 0) _log.LogWarning("Outbox exhausted rows in current scope: {Count}", swept);

        var batch = await _store.ClaimAsync(claim, ct);
        int published = 0, retried = 0, attention = swept, released = 0;

        for (int i = 0; i < batch.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = batch[i];
            // Renew only while this generation still owns an unexpired lease.
            // A waiting/stalled row that lost ownership must never reach the broker.
            if (!await SafeAsync(() => _store.RenewLeaseAsync(r, _clock.GetUtcNow(), claim.Lease, ct), r)) continue;
            var map = OutboxMessageMapper.Map(r, _opt);
            if (!map.Ok)
            {
                if (await SafeAsync(() => _store.MarkNeedsAttentionAsync(r, map.ErrorCode!, _clock.GetUtcNow(), ct), r))
                { attention++; _log.LogError("Outbox {EventId} invalid ({Code}); NEEDS_ATTENTION", r.Id, map.ErrorCode); }
                continue;
            }

            PublishOutcome outcome;
            try { outcome = await _broker.PublishAsync(map.Message!, TimeSpan.FromSeconds(_opt.ConfirmTimeoutSeconds), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Outbox {EventId}: loi broker khong phan loai", r.Id);
                outcome = PublishOutcome.BrokerUnavailable;
            }

            if (outcome == PublishOutcome.Confirmed)
            {
                // Neu buoc nay loi (DB), record van giu lease; het lease se phat lai cung ID. Khong nuot loi am tham.
                if (await SafeAsync(() => _store.MarkPublishedAsync(r, _clock.GetUtcNow(), ct), r))
                {
                published++;
                _log.LogInformation("Outbox published eventId={EventId} type={Type} correlationId={Correlation} attempt={Attempt}",
                    r.Id, r.EventType, map.Message!.CorrelationId, r.DeliveryAttempts);
                }
                continue;
            }

            var code = outcome switch
            {
                PublishOutcome.Nacked => OutboxErrors.BrokerNack,
                PublishOutcome.Returned => OutboxErrors.BrokerReturned,
                PublishOutcome.ConfirmTimeout => OutboxErrors.ConfirmTimeout,
                _ => OutboxErrors.BrokerUnavailable
            };
            if (r.DeliveryAttempts >= _opt.MaxPublishAttempts)
            {
                if (await SafeAsync(() => _store.MarkNeedsAttentionAsync(r, OutboxErrors.AttemptsExhausted + ":" + code, _clock.GetUtcNow(), ct), r))
                { attention++; _log.LogError("Outbox {EventId} exhausted ({Code}); NEEDS_ATTENTION", r.Id, code); }
            }
            else
            {
                var delay = Backoff(r.DeliveryAttempts);
                if (await SafeAsync(() => _store.MarkRetryAsync(r, code, _clock.GetUtcNow(), _clock.GetUtcNow() + delay, ct), r))
                { retried++; _log.LogWarning("Outbox {EventId} retry ({Code}) in {Delay}s", r.Id, code, (int)delay.TotalSeconds); }
            }

            // Broker mat ket noi: dung batch, tra lai phan chua thu de khong ton luot thu.
            if (outcome == PublishOutcome.BrokerUnavailable && i + 1 < batch.Count)
            {
                var rest = batch.Skip(i + 1).ToArray();
                released = await _store.ReleaseUnattemptedAsync(rest, _clock.GetUtcNow(), ct);
                break;
            }
        }
        return new OutboxRunResult(batch.Count, published, retried, attention, released);
    }

    public TimeSpan Backoff(int attempts)
    {
        var i = Math.Clamp(attempts - 1, 0, _opt.BackoffSeconds.Length - 1);
        return TimeSpan.FromSeconds(_opt.BackoffSeconds[i]);
    }

    /// <summary>Loi DB khi cap nhat trang thai khong duoc lam sap ca vong; record se duoc claim lai sau khi lease het.</summary>
    private async Task<bool> SafeAsync(Func<Task<bool>> action, OutboxRecord r)
    {
        try
        {
            bool written = await action();
            if (!written) _log.LogWarning("Outbox {EventId}: lease ownership lost; state not updated", r.Id);
            return written;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Outbox {EventId}: state update failed; lease recovery required", r.Id);
            return false;
        }
    }
}
