using System.Collections.Concurrent;
using Npgsql;

namespace AURA.Notification.Api;

public interface IAccountResolver
{
    Task<Guid?> ResolveAccountIdAsync(string firebaseUid, CancellationToken ct);
    Task<string?> ResolveFirebaseUidAsync(Guid accountId, CancellationToken ct);
}

public sealed class AccountResolver(
    NpgsqlDataSource dataSource,
    ILogger<AccountResolver> logger) : IAccountResolver
{
    private readonly ConcurrentDictionary<string, Guid> _cache = new();

    public async Task<Guid?> ResolveAccountIdAsync(string firebaseUid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(firebaseUid))
        {
            return null;
        }

        if (_cache.TryGetValue(firebaseUid, out var cached))
        {
            return cached;
        }

        // Direct UUID parse fallback if firebaseUid is already a GUID
        if (Guid.TryParse(firebaseUid, out var directGuid) && directGuid != Guid.Empty)
        {
            _cache[firebaseUid] = directGuid;
            return directGuid;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            const string sql = "SELECT account_id FROM identity.accounts WHERE firebase_uid = @firebase_uid LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("firebase_uid", firebaseUid);

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is Guid accountId && accountId != Guid.Empty)
            {
                _cache[firebaseUid] = accountId;
                return accountId;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve account_id for firebase_uid {Uid} from database.", firebaseUid);
        }

        return null;
    }

    private readonly ConcurrentDictionary<Guid, string> _reverseCache = new();

    public async Task<string?> ResolveFirebaseUidAsync(Guid accountId, CancellationToken ct)
    {
        if (accountId == Guid.Empty)
        {
            return null;
        }

        if (_reverseCache.TryGetValue(accountId, out var cached))
        {
            return cached;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            const string sql = "SELECT firebase_uid FROM identity.accounts WHERE account_id = @account_id LIMIT 1;";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("account_id", accountId);

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is string uid && !string.IsNullOrWhiteSpace(uid))
            {
                _reverseCache[accountId] = uid;
                return uid;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve firebase_uid for account_id {AccountId} from database.", accountId);
        }

        return null;
    }
}
