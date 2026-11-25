using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace AURA.Common.Audit;

public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, string? NextCursor);

public sealed class AuditProjection(NpgsqlDataSource database)
{
    public async Task AcceptAsync(AuditEvent e, CancellationToken ct)
    {
        AuditEvent.Validate(e);
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO profile.audit_inbox(source,event_id,payload_sha256) VALUES($1,$2,$3)
            ON CONFLICT(source,event_id) DO NOTHING
            """, connection, tx);
        insert.Parameters.AddWithValue(e.Source.ToString()); insert.Parameters.AddWithValue(e.EventId);
        insert.Parameters.AddWithValue(AuditJson.Hash(e));
        if (await insert.ExecuteNonQueryAsync(ct) == 0)
        {
            await using var prior = new NpgsqlCommand("SELECT payload_sha256 FROM profile.audit_inbox WHERE source=$1 AND event_id=$2", connection, tx);
            prior.Parameters.AddWithValue(e.Source.ToString()); prior.Parameters.AddWithValue(e.EventId);
            if ((string?)await prior.ExecuteScalarAsync(ct) != AuditJson.Hash(e)) throw new AuditConflictException();
        }
        else if (e.OccurredAt >= DateTimeOffset.UtcNow.AddDays(-180))
        {
            await using var project = new NpgsqlCommand("""
                INSERT INTO profile.audit_projection(source,event_id,occurred_at,payload) VALUES($1,$2,$3,$4::jsonb)
                """, connection, tx);
            project.Parameters.AddWithValue(e.Source.ToString()); project.Parameters.AddWithValue(e.EventId);
            project.Parameters.AddWithValue(e.OccurredAt); project.Parameters.AddWithValue(JsonSerializer.Serialize(e, AuditJson.Options));
            await project.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<AuditPage> QueryAsync(int limit, string? cursor, DateTimeOffset? from, DateTimeOffset? to,
        AuditSource? source, AuditAction? action, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || from > to || from?.Offset != TimeSpan.Zero && from.HasValue
            || to?.Offset != TimeSpan.Zero && to.HasValue || source.HasValue && !Enum.IsDefined(source.Value)
            || action.HasValue && !Enum.IsDefined(action.Value)) throw new ArgumentException("Invalid audit query.");
        var fingerprint = AuditEvent.Correlation(JsonSerializer.Serialize(new { from, to, source, action }, AuditJson.Options));
        Cursor? after = null;
        if (cursor is not null)
        {
            if (cursor.Length > 1024) throw new ArgumentException("Invalid audit cursor.");
            try { after = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor), AuditJson.Options); }
            catch (Exception e) when (e is FormatException or JsonException) { throw new ArgumentException("Invalid audit cursor."); }
            if (after is null || after.Filter != fingerprint || after.EventId == Guid.Empty
                || !Enum.TryParse<AuditSource>(after.Source, out var s) || !Enum.IsDefined(s))
                throw new ArgumentException("Invalid audit cursor.");
        }
        await using var cmd = database.CreateCommand("""
            SELECT payload::text,occurred_at,event_id,source FROM profile.audit_projection
            WHERE occurred_at>=now()-interval '180 days' AND ($1::timestamptz IS NULL OR occurred_at>=$1)
              AND ($2::timestamptz IS NULL OR occurred_at<$2) AND ($3::text IS NULL OR source=$3)
              AND ($4::text IS NULL OR payload->>'action'=$4)
              AND ($5::timestamptz IS NULL OR (occurred_at,event_id,source)<($5,$6,$7))
            ORDER BY occurred_at DESC,event_id DESC,source DESC LIMIT $8
            """);
        cmd.Parameters.AddWithValue((object?)from ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)source?.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)action?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)after?.OccurredAt ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)after?.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)after?.Source ?? DBNull.Value); cmd.Parameters.AddWithValue(limit + 1);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AuditEvent>();
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<AuditEvent>(reader.GetString(0), AuditJson.Options)!);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        var last = rows.LastOrDefault();
        var next = more && last is not null ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(last.OccurredAt, last.EventId, last.Source.ToString(), fingerprint), AuditJson.Options)) : null;
        return new(rows, next);
    }

    public async Task PurgeExpiredAsync(CancellationToken ct)
    {
        int removed;
        do
        {
            await using var cmd = database.CreateCommand("SELECT profile.purge_expired_audit_projection()");
            removed = (int)(await cmd.ExecuteScalarAsync(ct))!;
        } while (removed > 0);
    }

    private sealed record Cursor(DateTimeOffset OccurredAt, Guid EventId, string Source, string Filter);
}
