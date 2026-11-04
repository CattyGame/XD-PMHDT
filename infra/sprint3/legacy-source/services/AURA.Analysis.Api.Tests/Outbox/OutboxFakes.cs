using AURA.Analysis.Api.Outbox;

namespace AURA.Analysis.Api.Tests;

public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan t) => _now += t;
}

public sealed class OutboxRow
{
    public Guid Id; public Guid TenantId; public string EventType = ""; public string SchemaVersion = "";
    public string Payload = ""; public DateTimeOffset OccurredAt;
    public DateTimeOffset? PublishedAt, NextPublishAt, LockedUntil, NeedsAttentionAt;
    public int Attempts; public long Generation; public string? LastError;
}

/// <summary>Mo phong dung ngu nghia claim/lease cua SQL trong OutboxSql (SQL that duoc test rieng bang psql).</summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    public readonly List<OutboxRow> Rows = new();
    public Func<Guid, bool>? FailMarkPublished;
    public Func<OutboxRecord, bool>? RejectRenew;
    public int MarkPublishedCalls;
    private static bool Scoped(OutboxRow r, OutboxClaim c) => r.OccurredAt >= c.MinOccurredAt && !c.ExcludedTenants.Contains(r.TenantId);
    private OutboxRow? Owned(OutboxRecord claim, DateTimeOffset at) => Rows.SingleOrDefault(r => r.Id==claim.Id && claim.ClaimGeneration>0 && r.Generation==claim.ClaimGeneration && r.PublishedAt is null && r.NeedsAttentionAt is null && r.LockedUntil>at);
    public Task<IReadOnlyList<OutboxRecord>> ClaimAsync(OutboxClaim c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var due = Rows.Where(r => r.PublishedAt is null && r.NeedsAttentionAt is null && r.Attempts < c.MaxAttempts
                && (r.NextPublishAt is null || r.NextPublishAt <= c.Now)
                && (r.LockedUntil is null || r.LockedUntil <= c.Now) && Scoped(r,c))
            .OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).Take(c.BatchSize).ToList();
        foreach (var r in due) { r.LockedUntil = c.Now + c.Lease; r.Attempts++; r.Generation=checked(r.Generation+1); }
        return Task.FromResult<IReadOnlyList<OutboxRecord>>(due.Select(r =>
            new OutboxRecord(r.Id,r.TenantId,r.EventType,r.SchemaVersion,r.Payload,r.OccurredAt,r.Attempts,r.Generation)).ToList());
    }
    public Task<bool> RenewLeaseAsync(OutboxRecord claim, DateTimeOffset at, TimeSpan lease, CancellationToken ct)
    {
        var r=Owned(claim,at); if(r is null || RejectRenew?.Invoke(claim)==true) return Task.FromResult(false);
        var until=at+lease;if(r.LockedUntil<until)r.LockedUntil=until;return Task.FromResult(true);
    }
    public Task<bool> MarkPublishedAsync(OutboxRecord claim, DateTimeOffset at, CancellationToken ct)
    {
        MarkPublishedCalls++;
        if (FailMarkPublished?.Invoke(claim.Id)==true) throw new InvalidOperationException("db down");
        var r=Owned(claim,at);if(r is null)return Task.FromResult(false);
        r.PublishedAt=at;r.LockedUntil=null;r.NextPublishAt=null;r.LastError=null;return Task.FromResult(true);
    }
    public Task<bool> MarkRetryAsync(OutboxRecord claim, string code, DateTimeOffset at, DateTimeOffset next, CancellationToken ct)
    {
        var r=Owned(claim,at);if(r is null)return Task.FromResult(false);
        r.LastError=code;r.NextPublishAt=next;r.LockedUntil=null;return Task.FromResult(true);
    }
    public Task<bool> MarkNeedsAttentionAsync(OutboxRecord claim, string code, DateTimeOffset at, CancellationToken ct)
    {
        var r=Owned(claim,at);if(r is null)return Task.FromResult(false);
        r.NeedsAttentionAt=at;r.LastError=code;r.LockedUntil=null;return Task.FromResult(true);
    }
    public Task<int> ReleaseUnattemptedAsync(IReadOnlyCollection<OutboxRecord> records, DateTimeOffset at, CancellationToken ct)
    {
        int changed=0;foreach(var claim in records){var r=Owned(claim,at);if(r is null)continue;r.Attempts=Math.Max(r.Attempts-1,0);r.LockedUntil=null;changed++;}
        return Task.FromResult(changed);
    }
    public Task<int> SweepExhaustedAsync(OutboxClaim c, CancellationToken ct)
    {
        var rows=Rows.Where(r=>Scoped(r,c) && r.PublishedAt is null && r.NeedsAttentionAt is null && r.Attempts>=c.MaxAttempts && (r.LockedUntil is null || r.LockedUntil<=c.Now)).ToList();
        foreach(var r in rows){r.NeedsAttentionAt=c.Now;r.LastError=OutboxErrors.AttemptsExhausted;}return Task.FromResult(rows.Count);
    }
    public Task<OutboxBacklog> GetBacklogAsync(OutboxClaim c, CancellationToken ct)
    {
        var rows=Rows.Where(r=>Scoped(r,c)).ToList();var p=rows.Where(r=>r.PublishedAt is null && r.NeedsAttentionAt is null).ToList();
        return Task.FromResult(new OutboxBacklog(p.Count,
            p.Count(r=>(r.NextPublishAt is null || r.NextPublishAt<=c.Now) && (r.LockedUntil is null || r.LockedUntil<=c.Now)),
            p.Count(r=>r.LockedUntil>c.Now),rows.Count(r=>r.PublishedAt is null && r.NeedsAttentionAt is not null),
            p.Count==0?null:p.Min(r=>r.OccurredAt)));
    }
}

/// <summary>Broker gia lap: kich ban ket qua theo thu tu; "DeliveredBeforeOutcome" gia lap confirm bi mat sau khi broker da nhan.</summary>
public sealed class FakeBroker : IBrokerPublisher
{
    public readonly Queue<PublishOutcome> Script = new();
    public PublishOutcome Default = PublishOutcome.Confirmed;
    public readonly List<BrokerMessage> Delivered = new();   // message ma consumer that su nhan
    public readonly List<BrokerMessage> Attempts = new();
    public bool DeliverOnTimeout = true;
    public Func<BrokerMessage, Exception?>? Throw;

    public Task<PublishOutcome> PublishAsync(BrokerMessage m, TimeSpan t, CancellationToken ct)
    {
        Attempts.Add(m);
        var ex = Throw?.Invoke(m); if (ex is not null) throw ex;
        var o = Script.Count > 0 ? Script.Dequeue() : Default;
        if (o == PublishOutcome.Confirmed || (o == PublishOutcome.ConfirmTimeout && DeliverOnTimeout)) Delivered.Add(m);
        return Task.FromResult(o);
    }
}

/// <summary>Consumer idempotent mau: dedupe theo messageId, dem tac dung phu that su.</summary>
public sealed class IdempotentConsumer
{
    private readonly HashSet<string> _seen = new();
    public int SideEffects;
    public void Handle(BrokerMessage m) { if (_seen.Add(m.MessageId)) SideEffects++; }
}
