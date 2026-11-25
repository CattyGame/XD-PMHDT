using AURA.Common.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditDeliveryTests
{
    [M1PostgresFact]
    public async Task Collector_outage_and_restart_keep_obligation_and_replay_after_lost_ack_is_deduplicated()
    {
        await using var db = await Database.CreateAsync();
        var store = new AuditStore(db.Billing, AuditSource.Billing);
        var projection = new AuditProjection(db.Admin);
        var e = AuditEvent.Create(AuditSource.Billing, Guid.NewGuid(), AuditAction.PriceChanged, AuditResource.Package, "1", "correlation");
        await store.AppendAsync(e, default);
        var transport = new TestTransport(projection) { Down = true };
        var publisher = new AuditPublisher(store, transport, NullLogger<AuditPublisher>.Instance);
        Assert.True(await publisher.DispatchOnceAsync(default));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_outbox WHERE delivered_at IS NULL"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        await db.SqlAsync("UPDATE billing.audit_outbox SET next_attempt_at=now()");
        transport.Down = false; transport.LoseAcknowledgement = true;
        await publisher.DispatchOnceAsync(default);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_outbox WHERE delivered_at IS NULL"));
        await db.SqlAsync("UPDATE billing.audit_outbox SET next_attempt_at=now()");
        var restarted = new AuditPublisher(store, new TestTransport(projection), NullLogger<AuditPublisher>.Instance);
        await restarted.DispatchOnceAsync(default);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_outbox WHERE delivered_at IS NULL"));
        Assert.False(await restarted.DispatchOnceAsync(default));
    }

    private sealed class TestTransport(AuditProjection projection) : IAuditTransport
    {
        public bool Down { get; set; }
        public bool LoseAcknowledgement { get; set; }
        public async Task SendAsync(AuditEvent e, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("test-only outage; Bearer secret must never be logged");
            await projection.AcceptAsync(e, ct);
            if (LoseAcknowledgement) throw new IOException("test-only lost ACK");
        }
    }
}
