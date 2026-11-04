using Npgsql;
using NpgsqlTypes;

namespace AURA.Analysis.Api.Outbox.Adapters;

public sealed class OutboxDataSource
{
    public OutboxDataSource(NpgsqlDataSource ds) { DataSource = ds; }
    public NpgsqlDataSource DataSource { get; }
}

public sealed class NpgsqlOutboxStore : IOutboxStore
{
    private readonly NpgsqlDataSource _ds;
    public NpgsqlOutboxStore(OutboxDataSource source) { _ds = source.DataSource; }

    private static void Scope(NpgsqlCommand cmd, OutboxClaim c)
    {
        cmd.Parameters.AddWithValue("now", c.Now.UtcDateTime);
        cmd.Parameters.AddWithValue("min_occurred_at", c.MinOccurredAt.UtcDateTime);
        cmd.Parameters.Add(new NpgsqlParameter("excluded_tenants", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = c.ExcludedTenants.ToArray() });
    }
    private static void Owner(NpgsqlCommand cmd, OutboxRecord r, DateTimeOffset at)
    {
        if (r.ClaimGeneration <= 0) throw new InvalidOperationException("Missing claim generation");
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("generation", r.ClaimGeneration);
        cmd.Parameters.AddWithValue("at", at.UtcDateTime);
    }
    public async Task<IReadOnlyList<OutboxRecord>> ClaimAsync(OutboxClaim c, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.Claim); Scope(cmd, c);
        cmd.Parameters.AddWithValue("batch_size", c.BatchSize);
        cmd.Parameters.AddWithValue("lease_seconds", c.Lease.TotalSeconds);
        cmd.Parameters.AddWithValue("max_attempts", c.MaxAttempts);
        var list = new List<OutboxRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new OutboxRecord(rd.GetGuid(0), rd.GetGuid(1), rd.GetString(2), rd.GetString(3), rd.GetString(4),
                new DateTimeOffset(DateTime.SpecifyKind(rd.GetDateTime(5), DateTimeKind.Utc)), rd.GetInt32(6), rd.GetInt64(7)));
        return list.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToList();
    }
    public async Task<bool> RenewLeaseAsync(OutboxRecord r, DateTimeOffset at, TimeSpan lease, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.RenewLease); Owner(cmd, r, at);
        cmd.Parameters.AddWithValue("lease_seconds", lease.TotalSeconds);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> MarkPublishedAsync(OutboxRecord r, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.MarkPublished); Owner(cmd, r, at);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> MarkRetryAsync(OutboxRecord r, string code, DateTimeOffset at, DateTimeOffset next, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.MarkRetry); Owner(cmd, r, at);
        cmd.Parameters.AddWithValue("code", code); cmd.Parameters.AddWithValue("next", next.UtcDateTime);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> MarkNeedsAttentionAsync(OutboxRecord r, string code, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.MarkNeedsAttention); Owner(cmd, r, at);
        cmd.Parameters.AddWithValue("code", code);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<int> ReleaseUnattemptedAsync(IReadOnlyCollection<OutboxRecord> records, DateTimeOffset at, CancellationToken ct)
    {
        int count = 0;
        foreach (var r in records)
        {
            await using var cmd = _ds.CreateCommand(OutboxSql.ReleaseUnattempted); Owner(cmd, r, at);
            count += await cmd.ExecuteNonQueryAsync(ct);
        }
        return count;
    }
    public async Task<int> SweepExhaustedAsync(OutboxClaim c, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.SweepExhausted); Scope(cmd, c);
        cmd.Parameters.AddWithValue("max_attempts", c.MaxAttempts);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<OutboxBacklog> GetBacklogAsync(OutboxClaim c, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand(OutboxSql.Backlog); Scope(cmd, c);
        await using var rd = await cmd.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
        DateTimeOffset? oldest = rd.IsDBNull(4) ? null : new DateTimeOffset(DateTime.SpecifyKind(rd.GetDateTime(4), DateTimeKind.Utc));
        return new OutboxBacklog(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2), rd.GetInt64(3), oldest);
    }
}
