using Npgsql;

namespace AURA.Billing.Api.Data;

public sealed record PaymentNotificationRequest(Guid EventId, Guid OrderId, Guid TenantId);
public sealed record PaymentNotificationContext(Guid EventId, Guid OrderId, Guid TenantId, Guid RecipientAccountId, string Status);
public sealed record PaymentOutboxEvent(Guid EventId, Guid OrderId, Guid TenantId, string SchemaVersion,
    DateTimeOffset OccurredAt, Guid LeaseToken, int Attempts);

public sealed class PaymentNotificationRepository(NpgsqlDataSource dataSource)
{
    public async Task<PaymentNotificationContext?> ResolveAsync(PaymentNotificationRequest request, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT e.event_id,e.order_id,e.tenant_id,e.recipient_account_id,o.status
            FROM billing.payment_event_outbox e JOIN billing.orders o ON o.id=e.order_id
            WHERE e.event_id=$1 AND e.order_id=$2 AND e.tenant_id=$3 AND o.tenant_id=e.tenant_id
              AND o.status='COMPLETED' AND e.recipient_account_id IS NOT NULL AND e.state<>'HOLD';
            """);
        cmd.Parameters.AddWithValue(request.EventId); cmd.Parameters.AddWithValue(request.OrderId); cmd.Parameters.AddWithValue(request.TenantId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetString(4)) : null;
    }

    public async Task<PaymentOutboxEvent?> ClaimAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH candidate AS (
                SELECT event_id FROM billing.payment_event_outbox
                WHERE (state='PENDING' AND available_at<=now()) OR (state='IN_FLIGHT' AND lease_expires_at<now())
                ORDER BY available_at,event_id LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE billing.payment_event_outbox e SET state='IN_FLIGHT',lease_token=gen_random_uuid(),
                lease_expires_at=now()+interval '30 seconds',attempts=e.attempts+1
            FROM candidate c WHERE e.event_id=c.event_id
            RETURNING e.event_id,e.order_id,e.tenant_id,e.schema_version,e.created_at,e.lease_token,e.attempts;
            """);
        await using var r=await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetString(3),r.GetFieldValue<DateTimeOffset>(4),r.GetGuid(5),r.GetInt32(6)) : null;
    }

    public async Task CompleteAsync(PaymentOutboxEvent item, CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
            UPDATE billing.payment_event_outbox SET state='SENT',dispatched_at=now(),lease_token=NULL,lease_expires_at=NULL,last_error=NULL
            WHERE event_id=$1 AND lease_token=$2 AND state='IN_FLIGHT' AND lease_expires_at>now();
            """);
        cmd.Parameters.AddWithValue(item.EventId); cmd.Parameters.AddWithValue(item.LeaseToken);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeferAsync(PaymentOutboxEvent item, string error, CancellationToken ct)
    {
        await using var cmd=dataSource.CreateCommand("""
            UPDATE billing.payment_event_outbox SET state='PENDING',last_error=$3,
                available_at=now()+make_interval(secs=>least(300,power(2,least(attempts,8))))+random()*interval '3 seconds',
                lease_token=NULL,lease_expires_at=NULL
            WHERE event_id=$1 AND lease_token=$2 AND state='IN_FLIGHT' AND lease_expires_at>now();
            """);
        cmd.Parameters.AddWithValue(item.EventId); cmd.Parameters.AddWithValue(item.LeaseToken); cmd.Parameters.AddWithValue(error);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
