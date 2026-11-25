using AURA.Common.Audit;
using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditPersistenceTests
{
    [M1PostgresFact]
    public async Task All_five_runtime_owners_are_append_only_and_cannot_write_another_owner_audit()
    {
        await using var db=await Database.CreateAsync();
        foreach(var (source,owner) in new[]{(AuditSource.Identity,db.Identity),(AuditSource.Profile,db.Profile),
            (AuditSource.Analysis,db.Analysis),(AuditSource.Billing,db.Billing),(AuditSource.Notification,db.Notification)})
        {
            var store=new AuditStore(owner,source);var e=Event() with { Source=source };
            await store.AppendAsync(e,default);
            await using(var update=owner.CreateCommand($"UPDATE {AuditStore.Schema(source)}.audit_events SET payload='{{}}'::jsonb"))
                Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>update.ExecuteNonQueryAsync())).SqlState);
            await using(var changeIdentity=owner.CreateCommand($"UPDATE {AuditStore.Schema(source)}.audit_outbox SET event_id=gen_random_uuid()"))
                Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>changeIdentity.ExecuteNonQueryAsync())).SqlState);
            var foreign=source==AuditSource.Profile ? AuditSource.Billing : AuditSource.Profile;
            await using var connection=await owner.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync();
            Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>AuditStore.AppendAsync(connection,tx,e with {Source=foreign,EventId=Guid.NewGuid()},default))).SqlState);
            await tx.RollbackAsync();
        }
    }
    [M1PostgresFact]
    public async Task Keyset_pagination_uses_database_precision_and_binds_cursor_to_filters()
    {
        await using var db = await Database.CreateAsync();
        var projection = new AuditProjection(db.Admin);
        var at = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10 + 7, TimeSpan.Zero);
        for (int i=0;i<5;i++) await projection.AcceptAsync(Event() with { OccurredAt=at },default);
        var seen=new HashSet<Guid>(); string? cursor=null;
        do
        {
            var page=await projection.QueryAsync(2,cursor,null,null,null,null,default);
            Assert.NotEmpty(page.Items);
            foreach(var row in page.Items) Assert.True(seen.Add(row.EventId),"A cursor must not repeat its final row");
            cursor=page.NextCursor;
            if(cursor is not null)await Assert.ThrowsAsync<ArgumentException>(()=>projection.QueryAsync(2,cursor,null,null,AuditSource.Analysis,null,default));
        }while(cursor is not null);
        Assert.Equal(5,seen.Count);
    }

    [M1PostgresFact]
    public async Task Source_is_append_only_atomic_idempotent_and_outbox_is_fenced()
    {
        await using var db = await Database.CreateAsync();
        var store = new AuditStore(db.Billing, AuditSource.Billing);
        var e = Event();
        await store.AppendAsync(e, default);
        await store.AppendAsync(e, default);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_outbox"));
        await Assert.ThrowsAsync<AuditConflictException>(() => store.AppendAsync(e with { ResourceId = "2" }, default));
        await using (var update = db.Billing.CreateCommand("DELETE FROM billing.audit_events"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        var first = (await store.ClaimAsync(default))!;
        Assert.Null(await store.ClaimAsync(default));
        await db.SqlAsync("UPDATE billing.audit_outbox SET lease_until=now()-interval '1 second'");
        var next = (await store.ClaimAsync(default))!;
        Assert.NotEqual(first.LeaseId, next.LeaseId);
        Assert.False(await store.CompleteAsync(first, default));
        await store.RetryAsync(first, default);
        Assert.True(await store.CompleteAsync(next, default));
        Assert.Null(await store.ClaimAsync(default));
        await using var connection = await db.Billing.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await AuditStore.AppendAsync(connection, tx, Event(), default);
        await tx.RollbackAsync();
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events"));
    }

    [M1PostgresFact]
    public async Task Projection_deduplicates_replay_rejects_conflicts_and_retention_keeps_pending_obligations()
    {
        await using var db = await Database.CreateAsync();
        var store = new AuditStore(db.Billing, AuditSource.Billing);
        var projection = new AuditProjection(db.Admin);
        var e = Event();
        await store.AppendAsync(e, default);
        await projection.AcceptAsync(e, default);
        await projection.AcceptAsync(e, default);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        await Assert.ThrowsAsync<AuditConflictException>(() => projection.AcceptAsync(e with { ResourceId = "99" }, default));
        var old = Event() with { OccurredAt = DateTimeOffset.UtcNow.AddDays(-181) };
        await store.AppendAsync(old, default);
        await projection.AcceptAsync(old, default);
        await store.PurgeExpiredAsync(default);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events"));
        await db.SqlAsync("UPDATE billing.audit_outbox SET delivered_at=now() WHERE event_id=$1", old.EventId);
        await store.PurgeExpiredAsync(default);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM billing.audit_events"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_inbox"));
        await projection.AcceptAsync(old, default);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
    }

    private static AuditEvent Event() => AuditEvent.Create(AuditSource.Billing, Guid.NewGuid(),
        AuditAction.PriceChanged, AuditResource.Package, "1", "test-correlation");
}
