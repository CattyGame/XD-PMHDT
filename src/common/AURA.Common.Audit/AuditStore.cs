using System.Text.Json;
using Npgsql;

namespace AURA.Common.Audit;

public sealed record AuditClaim(AuditEvent Event, Guid LeaseId, int Attempts);

public interface IAuditWriter
{
    Task AppendAsync(AuditEvent e, CancellationToken ct);
}

public sealed class AuditStore(NpgsqlDataSource database, AuditSource source) : IAuditWriter
{
    private readonly string schema = Schema(source);

    public static string Schema(AuditSource source) => Enum.IsDefined(source)
        ? source.ToString().ToLowerInvariant() : throw new ArgumentException("Unknown audit owner.");

    public async Task AppendAsync(AuditEvent e, CancellationToken ct)
    {
        if (e.Source != source) throw new ArgumentException("Audit owner mismatch.");
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await AppendAsync(connection, tx, e, ct);
        await tx.CommitAsync(ct);
    }

    // The caller's business transaction owns both the mutation and the audit obligation.
    public static async Task AppendAsync(NpgsqlConnection connection, NpgsqlTransaction tx, AuditEvent e, CancellationToken ct)
    {
        AuditEvent.Validate(e);
        var schema = Schema(e.Source);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {schema}.audit_events(event_id,occurred_at,payload,payload_sha256)
            VALUES($1,$2,$3::jsonb,$4) ON CONFLICT(event_id) DO NOTHING;
            """, connection, tx);
        insert.Parameters.AddWithValue(e.EventId);
        insert.Parameters.AddWithValue(e.OccurredAt);
        insert.Parameters.AddWithValue(JsonSerializer.Serialize(e, AuditJson.Options));
        insert.Parameters.AddWithValue(AuditJson.Hash(e));
        if (await insert.ExecuteNonQueryAsync(ct) == 0)
        {
            await using var existing = new NpgsqlCommand($"SELECT payload_sha256 FROM {schema}.audit_events WHERE event_id=$1", connection, tx);
            existing.Parameters.AddWithValue(e.EventId);
            if ((string?)await existing.ExecuteScalarAsync(ct) != AuditJson.Hash(e)) throw new AuditConflictException();
        }
        await using var outbox = new NpgsqlCommand($"INSERT INTO {schema}.audit_outbox(event_id) VALUES($1) ON CONFLICT DO NOTHING", connection, tx);
        outbox.Parameters.AddWithValue(e.EventId);
        await outbox.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuditClaim?> ClaimAsync(CancellationToken ct)
    {
        await using var cmd = database.CreateCommand($"""
            WITH candidate AS (
              SELECT event_id FROM {schema}.audit_outbox WHERE delivered_at IS NULL AND next_attempt_at<=now()
                AND (lease_until IS NULL OR lease_until<=now()) ORDER BY next_attempt_at,event_id
              FOR UPDATE SKIP LOCKED LIMIT 1
            ), claimed AS (
              UPDATE {schema}.audit_outbox o SET lease_id=gen_random_uuid(),lease_until=now()+interval '45 seconds',attempts=attempts+1
              FROM candidate c WHERE o.event_id=c.event_id RETURNING o.event_id,o.lease_id,o.attempts
            ) SELECT e.payload::text,c.lease_id,c.attempts FROM claimed c JOIN {schema}.audit_events e USING(event_id)
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var e = JsonSerializer.Deserialize<AuditEvent>(reader.GetString(0), AuditJson.Options)
            ?? throw new InvalidOperationException("Invalid durable audit event.");
        AuditEvent.Validate(e);
        return new(e, reader.GetGuid(1), reader.GetInt32(2));
    }

    public async Task<bool> CompleteAsync(AuditClaim claim, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand($"""
            UPDATE {schema}.audit_outbox SET delivered_at=now(),lease_until=NULL,lease_id=NULL
            WHERE event_id=$1 AND lease_id=$2 AND lease_until>now() AND delivered_at IS NULL
            """);
        cmd.Parameters.AddWithValue(claim.Event.EventId); cmd.Parameters.AddWithValue(claim.LeaseId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task RetryAsync(AuditClaim claim, CancellationToken ct)
    {
        await using var cmd = database.CreateCommand($"""
            UPDATE {schema}.audit_outbox SET next_attempt_at=now()+$3,lease_until=NULL,lease_id=NULL
            WHERE event_id=$1 AND lease_id=$2 AND delivered_at IS NULL
            """);
        cmd.Parameters.AddWithValue(claim.Event.EventId); cmd.Parameters.AddWithValue(claim.LeaseId);
        cmd.Parameters.AddWithValue(TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(claim.Attempts, 12)))));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        int removed;
        do
        {
            await using var cmd = database.CreateCommand($"SELECT {schema}.purge_expired_audit()");
            removed = (int)(await cmd.ExecuteScalarAsync(ct))!;
        } while (removed > 0);
    }
}
