using Npgsql;

namespace AURA.Billing.Api.Data;

public sealed record BillingOrderEntry(Guid Id, Guid PriceVersionId, string PackageName, decimal Amount,
    string Currency, int Credits, string Status, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record BillingUsageEntry(Guid Id, string EntryType, int BalanceDelta, int BalanceAfter,
    int? ReservationUnits, Guid? OrderId, Guid? ReservationId, DateTime CreatedAt);
public sealed record BillingMoneyTotal(string Environment, string Currency, long PaidOrders, decimal PaidAmount);

public sealed class BillingHistoryRepository(NpgsqlDataSource source)
{
    public Task<List<BillingOrderEntry>> OrdersAsync(string ownerType, Guid ownerId, Guid tenant, string environment,
        DateTime before, Guid beforeId, int take, CancellationToken ct) =>
        ReadAsync("""
            SELECT id,package_price_version_id,package_name_snapshot,price_amount_snapshot,currency_code_snapshot,
                   credits_snapshot,status,created_at,updated_at
            FROM billing.orders
            WHERE tenant_id=$1 AND billing_owner_type=$2 AND billing_owner_id=$3 AND environment=$4
              AND (created_at,id) < ($5,$6)
            ORDER BY created_at DESC,id DESC LIMIT $7
            """, r => new BillingOrderEntry(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetDecimal(3), r.GetString(4),
                r.GetInt32(5), r.GetString(6), r.GetDateTime(7), r.GetDateTime(8)), ct,
            tenant, ownerType, ownerId, environment, before, beforeId, take);

    public Task<List<BillingUsageEntry>> UsageAsync(string ownerType, Guid ownerId, Guid tenant, string environment,
        DateTime from, DateTime to, DateTime before, Guid beforeId, int take, CancellationToken ct) =>
        ReadAsync("""
            SELECT e.id,e.entry_type,e.amount,e.balance_after,r.units,e.order_id,e.reservation_id,e.created_at
            FROM billing.credit_wallets w
            JOIN billing.credit_ledger_entries e ON e.wallet_id=w.id
            LEFT JOIN billing.credit_reservations r ON r.id=e.reservation_id AND r.wallet_id=w.id AND r.tenant_id=e.tenant_id
            WHERE e.tenant_id=$1 AND w.owner_type=$2 AND w.owner_id=$3 AND w.environment=$4
              AND e.created_at >= $8 AND e.created_at < $9 AND (e.created_at,e.id) < ($5,$6)
            ORDER BY e.created_at DESC,e.id DESC LIMIT $7
            """, r => new BillingUsageEntry(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt32(4), OptionalId(r,5), OptionalId(r,6), r.GetDateTime(7)), ct,
            tenant, ownerType, ownerId, environment, before, beforeId, take, from, to);

    public Task<List<BillingMoneyTotal>> SummaryAsync(DateTime from, DateTime to, CancellationToken ct) =>
        ReadAsync("""
            SELECT o.environment,o.currency_code_snapshot,count(*),sum(o.price_amount_snapshot)
            FROM billing.orders o
            WHERE o.status='COMPLETED' AND EXISTS (
                SELECT 1 FROM billing.payments p WHERE p.order_id=o.id AND p.status='SUCCESS'
                    AND p.amount=o.price_amount_snapshot AND p.currency=o.currency_code_snapshot
                    AND p.updated_at >= $1 AND p.updated_at < $2)
            GROUP BY o.environment,o.currency_code_snapshot ORDER BY o.environment,o.currency_code_snapshot
            """, r => new BillingMoneyTotal(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetDecimal(3)), ct, from, to);

    public Task<List<BillingMoneyTotal>> OwnerSummaryAsync(string ownerType, Guid ownerId, Guid tenantId,
        DateTime from, DateTime to, CancellationToken ct) =>
        ReadAsync("""
            SELECT o.environment,o.currency_code_snapshot,count(*),sum(o.price_amount_snapshot)
            FROM billing.orders o
            WHERE o.tenant_id=$1 AND o.billing_owner_type=$2 AND o.billing_owner_id=$3
              AND o.status='COMPLETED' AND EXISTS (
                  SELECT 1 FROM billing.payments p WHERE p.order_id=o.id AND p.status='SUCCESS'
                      AND p.amount=o.price_amount_snapshot AND p.currency=o.currency_code_snapshot
                      AND p.updated_at >= $4 AND p.updated_at < $5)
            GROUP BY o.environment,o.currency_code_snapshot ORDER BY o.environment,o.currency_code_snapshot
            """, r => new BillingMoneyTotal(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetDecimal(3)), ct,
            tenantId, ownerType, ownerId, from, to);

    private async Task<List<T>> ReadAsync<T>(string sql, Func<NpgsqlDataReader, T> read, CancellationToken ct, params object[] args)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new() { Value = arg });
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(read(reader));
        return rows;
    }

    private static Guid? OptionalId(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
}
