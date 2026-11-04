using Xunit;
using System.Text.Json;
using AURA.Analysis.Api.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AURA.Analysis.Api.Tests;

public class OutboxPublisherTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 2, 0, 0, TimeSpan.Zero);
    static readonly Guid Tenant = Guid.Parse("e8e2d32b-6fbb-504e-9f08-91b0d3efa6a3");

    static OutboxOptions Opt() => new() { Enabled = true, MinOccurredAtUtc = T0.AddDays(-1) };

    static string Event(Guid id, Guid tenant, string type = "analysis.requested", string corr = "s3-demo-001") => JsonSerializer.Serialize(new
    {
        schemaVersion = "0.7", occurredAt = "2026-10-28T02:00:00Z", tenantId = tenant, correlationId = corr,
        analysisId = Guid.Parse("58685092-7782-5bb6-b147-1b73c6047edd"), eventId = id, eventType = type,
        data = new { patientId = Guid.Parse("b04af0cc-f6fa-5731-a609-d76368fee17b"), batchId = Guid.Parse("e919bf5b-3625-591d-8a23-8f61ef0c0a44") }
    });

    static string Job(Guid id, Guid tenant) => JsonSerializer.Serialize(new
    {
        messageId = id, messageType = "analysis.job.run", schemaVersion = "0.6", occurredAt = "2026-10-28T02:00:00Z",
        tenantId = tenant, correlationId = "s3-demo-001", jobId = Guid.NewGuid(),
        analysisId = Guid.NewGuid(), batchId = Guid.NewGuid(), imageFileId = Guid.NewGuid(), attempt = 1
    });

    static OutboxRow EventRow(Guid? id = null, string type = "analysis.requested", Guid? tenant = null, DateTimeOffset? at = null)
    {
        var i = id ?? Guid.NewGuid(); var t = tenant ?? Tenant;
        return new OutboxRow { Id = i, TenantId = t, EventType = type, SchemaVersion = "0.7", Payload = Event(i, t, type), OccurredAt = at ?? T0 };
    }
    static OutboxRow JobRow(Guid? id = null)
    {
        var i = id ?? Guid.NewGuid();
        return new OutboxRow { Id = i, TenantId = Tenant, EventType = "analysis.job.run", SchemaVersion = "0.6", Payload = Job(i, Tenant), OccurredAt = T0 };
    }

    static OutboxPublisher Make(InMemoryOutboxStore s, FakeBroker b, FakeClock c, OutboxOptions? o = null) =>
        new(s, b, Options.Create(o ?? Opt()), c, NullLogger<OutboxPublisher>.Instance);

    [Fact]
    public async Task Upload_event_and_job_are_published_with_stable_ids_and_correct_routing()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
        var e = EventRow(); var j = JobRow(); s.Rows.Add(e); s.Rows.Add(j);
        var r = await Make(s, b, c).RunOnceAsync(default);
        Assert.Equal(2, r.Published);
        var job = b.Delivered.Single(m => m.Type == "analysis.job.run");
        Assert.Equal("aura.s3.jobs", job.Exchange); Assert.Equal("analysis.job.run", job.RoutingKey); Assert.Equal(j.Id.ToString(), job.MessageId);
        var ev = b.Delivered.Single(m => m.Type == "analysis.requested");
        Assert.Equal("aura.s3.events", ev.Exchange); Assert.Equal("analysis.requested", ev.RoutingKey); Assert.Equal(e.Id.ToString(), ev.MessageId);
        Assert.Equal("s3-demo-001", ev.CorrelationId);
        Assert.NotNull(s.Rows[0].PublishedAt); Assert.NotNull(s.Rows[1].PublishedAt);
    }

    [Fact]
    public async Task Published_is_not_marked_before_confirm_for_nack_return_timeout_unavailable()
    {
        foreach (var outcome in new[] { PublishOutcome.Nacked, PublishOutcome.Returned, PublishOutcome.ConfirmTimeout, PublishOutcome.BrokerUnavailable })
        {
            var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
            s.Rows.Add(EventRow()); b.Script.Enqueue(outcome);
            var r = await Make(s, b, c).RunOnceAsync(default);
            Assert.Equal(0, r.Published); Assert.Equal(1, r.Retried);
            Assert.Null(s.Rows[0].PublishedAt);           // khong danh dau published truoc/ngoai confirm
            Assert.NotNull(s.Rows[0].NextPublishAt);      // record con trong outbox, co lich thu lai
            Assert.Equal(1, s.Rows.Count);                // khong mat record
        }
    }

    [Fact]
    public async Task Broker_outage_keeps_record_then_recovers_with_same_event_id()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
        var row = EventRow(); s.Rows.Add(row);
        b.Script.Enqueue(PublishOutcome.BrokerUnavailable);
        var p = Make(s, b, c);
        await p.RunOnceAsync(default);
        Assert.Null(row.PublishedAt);
        var early = await p.RunOnceAsync(default);       // chua den han backoff: khong thu lai
        Assert.Equal(0, early.Claimed);
        c.Advance(TimeSpan.FromSeconds(6));
        var ok = await p.RunOnceAsync(default);
        Assert.Equal(1, ok.Published);
        Assert.Equal(2, b.Attempts.Count);
        Assert.Equal(b.Attempts[0].MessageId, b.Attempts[1].MessageId);
    }

    [Fact]
    public async Task Crash_after_publish_before_db_update_replays_same_event_id_and_consumer_applies_once()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0); var consumer = new IdempotentConsumer();
        var row = EventRow(); s.Rows.Add(row);
        s.FailMarkPublished = _ => true;                 // DB loi ngay sau khi broker confirm
        await Make(s, b, c).RunOnceAsync(default);
        foreach (var m in b.Delivered) consumer.Handle(m);
        Assert.Null(row.PublishedAt);                    // DB chua biet -> se phat lai
        s.FailMarkPublished = null;
        c.Advance(TimeSpan.FromSeconds(61));             // het lease
        var r = await Make(s, b, c).RunOnceAsync(default); // "restart": publisher moi tren cung store
        foreach (var m in b.Delivered) consumer.Handle(m);
        Assert.Equal(1, r.Published);
        Assert.Equal(2, b.Delivered.Count);              // broker nhan 2 lan (at-least-once)
        Assert.Equal(b.Delivered[0].MessageId, b.Delivered[1].MessageId);
        Assert.Equal(1, consumer.SideEffects);           // nhung chi 1 tac dung phu
    }

    [Fact]
    public async Task Confirm_timeout_after_broker_received_resends_same_id_without_double_effect()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0); var consumer = new IdempotentConsumer();
        s.Rows.Add(EventRow()); b.Script.Enqueue(PublishOutcome.ConfirmTimeout); // broker da nhan nhung confirm mat
        var p = Make(s, b, c);
        await p.RunOnceAsync(default); c.Advance(TimeSpan.FromSeconds(6)); await p.RunOnceAsync(default);
        foreach (var m in b.Delivered) consumer.Handle(m);
        Assert.Equal(2, b.Delivered.Count); Assert.Equal(1, consumer.SideEffects);
        Assert.NotNull(s.Rows[0].PublishedAt);
    }

    [Fact]
    public async Task Duplicate_publish_calls_do_not_publish_a_published_record_again()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
        s.Rows.Add(EventRow()); var p = Make(s, b, c);
        await p.RunOnceAsync(default); c.Advance(TimeSpan.FromMinutes(5));
        var again = await p.RunOnceAsync(default);
        Assert.Equal(0, again.Claimed); Assert.Equal(1, b.Attempts.Count);
    }

    [Fact]
    public async Task Two_publishers_do_not_claim_the_same_record_while_lease_is_active()
    {
        var s = new InMemoryOutboxStore(); var b1 = new FakeBroker(); var b2 = new FakeBroker(); var c = new FakeClock(T0);
        s.Rows.Add(EventRow());
        s.FailMarkPublished = _ => true;                 // publisher 1 treo/crash sau publish, lease con han
        await Make(s, b1, c).RunOnceAsync(default);
        var r2 = await Make(s, b2, c).RunOnceAsync(default);
        Assert.Equal(0, r2.Claimed);
    }

    [Fact]
    public async Task Ten_failed_attempts_move_to_needs_attention_and_keep_the_record()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker { Default = PublishOutcome.Nacked }; var c = new FakeClock(T0);
        var row = EventRow(); s.Rows.Add(row); var p = Make(s, b, c);
        for (int i = 0; i < 12; i++) { await p.RunOnceAsync(default); c.Advance(TimeSpan.FromMinutes(5)); }
        Assert.Equal(10, b.Attempts.Count);              // dung 10 lan, khong hon
        Assert.NotNull(row.NeedsAttentionAt);
        Assert.Null(row.PublishedAt);
        Assert.Equal(1, s.Rows.Count);
        Assert.True(row.LastError!.StartsWith("PUBLISH_ATTEMPTS_EXHAUSTED"));
        var backlog = await s.GetBacklogAsync(new OutboxClaim(c.GetUtcNow(),10,TimeSpan.FromSeconds(60),10,T0.AddDays(-1),Array.Empty<Guid>()), default);
        Assert.Equal(1, backlog.NeedsAttention); Assert.Equal(0, backlog.Pending);
    }

    [Fact]
    public async Task Crash_on_last_attempt_is_swept_to_needs_attention_after_lease_expiry()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
        var row = EventRow(); row.Attempts = 10; row.LockedUntil = T0.AddSeconds(30); s.Rows.Add(row);
        var p = Make(s, b, c);
        await p.RunOnceAsync(default); Assert.Null(row.NeedsAttentionAt);  // lease con han
        c.Advance(TimeSpan.FromSeconds(31));
        var r = await p.RunOnceAsync(default);
        Assert.Equal(1, r.NeedsAttention); Assert.NotNull(row.NeedsAttentionAt); Assert.Equal(0, b.Attempts.Count);
    }

    [Fact]
    public async Task Backoff_follows_the_configured_schedule_and_caps()
    {
        var p = Make(new InMemoryOutboxStore(), new FakeBroker(), new FakeClock(T0));
        Assert.Equal(5, (int)p.Backoff(1).TotalSeconds); Assert.Equal(15, (int)p.Backoff(2).TotalSeconds);
        Assert.Equal(120, (int)p.Backoff(5).TotalSeconds); Assert.Equal(120, (int)p.Backoff(9).TotalSeconds);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Broker_outage_stops_the_batch_and_does_not_burn_attempts_of_untried_records()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
        for (int i = 0; i < 3; i++) s.Rows.Add(EventRow(at: T0.AddSeconds(i)));
        b.Script.Enqueue(PublishOutcome.BrokerUnavailable);
        var r = await Make(s, b, c).RunOnceAsync(default);
        Assert.Equal(1, b.Attempts.Count); Assert.Equal(2, r.Released);
        Assert.Equal(1, s.Rows[0].Attempts); Assert.Equal(0, s.Rows[1].Attempts); Assert.Equal(0, s.Rows[2].Attempts);
        Assert.Null(s.Rows[1].LockedUntil);
    }

    [Fact]
    public async Task Broker_exception_is_treated_as_unavailable_not_crash()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker { Throw = _ => new IOException("conn reset") }; var c = new FakeClock(T0);
        s.Rows.Add(EventRow());
        var r = await Make(s, b, c).RunOnceAsync(default);
        Assert.Equal(1, r.Retried); Assert.Equal(OutboxErrors.BrokerUnavailable, s.Rows[0].LastError);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker { Throw = _ => new OperationCanceledException() }; var c = new FakeClock(T0);
        s.Rows.Add(EventRow());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Make(s, b, c).RunOnceAsync(cts.Token));
    }

    [Fact]
    public async Task Fixture_guard_ignores_old_rows_and_excluded_tenants()
    {
        var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
        var old = EventRow(at: T0.AddDays(-30));          // truoc moc Sprint 3 (vd fixture 08)
        var other = Guid.NewGuid(); var excl = EventRow(tenant: other);
        var good = EventRow();
        s.Rows.AddRange(new[] { old, excl, good });
        var o = Opt(); o.ExcludedTenantIds = new[] { other };
        var r = await Make(s, b, c, o).RunOnceAsync(default);
        Assert.Equal(1, r.Published);
        Assert.Null(old.PublishedAt); Assert.Null(excl.PublishedAt); Assert.NotNull(good.PublishedAt);
        Assert.Equal(0, old.Attempts);
    }

    [Fact]
    public async Task Enabled_without_cutoff_is_rejected_at_startup()
    {
        var o = new OutboxOptions { Enabled = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => { o.Validate(); return Task.CompletedTask; });
        var o2 = new OutboxOptions { Enabled = true, MinOccurredAtUtc = T0, BatchSize = 50 }; // lease 60 < 50*5+5
        await Assert.ThrowsAsync<InvalidOperationException>(() => { o2.Validate(); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Invalid_payloads_go_to_needs_attention_without_calling_the_broker()
    {
        var cases = new List<(string name, Action<OutboxRow> mutate, string code)>();
        cases.Add(("id mismatch", r => r.Payload = Event(Guid.NewGuid(), r.TenantId), OutboxErrors.IdMismatch));
        cases.Add(("tenant mismatch", r => r.Payload = Event(r.Id, Guid.NewGuid()), OutboxErrors.TenantMismatch));
        cases.Add(("type mismatch", r => r.EventType = "analysis.completed", OutboxErrors.TypeMismatch));
        cases.Add(("reviewed not published", r => { r.EventType = "analysis.reviewed"; r.Payload = Event(r.Id, r.TenantId, "analysis.reviewed"); }, OutboxErrors.UnroutableType));
        cases.Add(("old schema", r => r.SchemaVersion = "0.6", OutboxErrors.SchemaVersionMismatch));
        cases.Add(("bad correlation", r => r.Payload = Event(r.Id, r.TenantId, corr: "bad corr!"), OutboxErrors.InvalidMessage));
        cases.Add(("not json", r => r.Payload = "{oops", OutboxErrors.InvalidMessage));
        cases.Add(("base64 in payload", r => r.Payload = r.Payload.Replace("\"data\":{", "\"data\":{\"imageBase64\":\"AAAA\","), OutboxErrors.ForbiddenField));
        cases.Add(("sas token", r => r.Payload = r.Payload.Replace("\"data\":{", "\"data\":{\"sasToken\":\"x\","), OutboxErrors.ForbiddenField));
        cases.Add(("too large", r => r.Payload = r.Payload.Replace("\"data\":{", "\"data\":{\"note\":\"" + new string('a', 70000) + "\","), OutboxErrors.MessageTooLarge));
        cases.Add(("event with job envelope", r => r.Payload = r.Payload.Replace("\"eventId\"", "\"messageId\"").Replace("\"eventType\"", "\"messageType\""), OutboxErrors.UnroutableType));
        foreach (var (name, mutate, code) in cases)
        {
            var s = new InMemoryOutboxStore(); var b = new FakeBroker(); var c = new FakeClock(T0);
            var row = EventRow(); mutate(row); s.Rows.Add(row);
            await Make(s, b, c).RunOnceAsync(default);
            if (row.LastError != code) throw new InvalidOperationException($"{name}: expected {code} got {row.LastError}");
            Assert.NotNull(row.NeedsAttentionAt); Assert.Equal(0, b.Attempts.Count);
        }
    }

    [Fact]
    public async Task Pack_examples_are_accepted_by_the_mapper()
    {
        var evId = Guid.Parse("9daa3093-9750-5d40-8276-be502b5f9e77"); var tn = Guid.Parse("e8e2d32b-6fbb-504e-9f08-91b0d3efa6a3");
        var ev = new OutboxRecord(evId, tn, "analysis.requested", "0.7", Event(evId, tn), T0, 1);
        Assert.True(OutboxMessageMapper.Map(ev, Opt()).Ok);
        var jid = Guid.Parse("866e6f38-0a26-5d9e-a118-f2e9ba60ecbd");
        var job = new OutboxRecord(jid, tn, "analysis.job.run", "0.6", Job(jid, tn), T0, 1);
        Assert.True(OutboxMessageMapper.Map(job, Opt()).Ok);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Backlog_reports_pending_due_inflight_and_oldest()
    {
        var s = new InMemoryOutboxStore(); var c = new FakeClock(T0);
        var a = EventRow(at: T0.AddMinutes(-3)); var b2 = EventRow(at: T0); b2.LockedUntil = T0.AddSeconds(30);
        var d = EventRow(at: T0); d.NextPublishAt = T0.AddMinutes(1); s.Rows.AddRange(new[] { a, b2, d });
        var bl = await s.GetBacklogAsync(new OutboxClaim(T0,10,TimeSpan.FromSeconds(60),10,T0.AddDays(-1),Array.Empty<Guid>()), default);
        Assert.Equal(3, bl.Pending); Assert.Equal(1, bl.Due); Assert.Equal(1, bl.InFlight); Assert.Equal(T0.AddMinutes(-3), bl.OldestPendingOccurredAt);
    }
}
