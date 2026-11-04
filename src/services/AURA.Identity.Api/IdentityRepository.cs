using AURA.Identity.Api.Models;
using Npgsql;

namespace AURA.Identity.Api;

public class IdentityRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public IdentityRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<BootstrapResponse?> GetAccountByFirebaseUidAsync(string firebaseUid, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT a.account_id, a.firebase_uid, a.account_status, a.profile_provisioning_status,
                   COALESCE(array_agg(ar.role_name) FILTER (WHERE ar.role_name IS NOT NULL), ARRAY[]::VARCHAR[]) as roles
            FROM identity.accounts a
            LEFT JOIN identity.account_roles ar ON a.account_id = ar.account_id
            WHERE a.firebase_uid = $1
            GROUP BY a.account_id, a.firebase_uid, a.account_status, a.profile_provisioning_status;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(firebaseUid);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var accountId = reader.GetGuid(0);
            var uid = reader.GetString(1);
            var status = reader.GetString(2);
            var provStatus = reader.GetString(3);
            var roles = (string[])reader.GetValue(4);

            return new BootstrapResponse(accountId, uid, status, roles, provStatus);
        }

        return null;
    }

    public async Task<BootstrapResponse> CreateAccountAsync(string firebaseUid, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var accountId = Guid.NewGuid();
            const string insertAccountSql = @"
                INSERT INTO identity.accounts (account_id, firebase_uid, account_status, profile_provisioning_status)
                VALUES ($1, $2, 'ACTIVE', 'PENDING')
                ON CONFLICT (firebase_uid) DO NOTHING
                RETURNING account_id, firebase_uid, account_status, profile_provisioning_status;";

            await using var cmd = new NpgsqlCommand(insertAccountSql, connection, transaction);
            cmd.Parameters.AddWithValue(accountId);
            cmd.Parameters.AddWithValue(firebaseUid);

            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    // Concurrency conflict: account already inserted by another request
                    await reader.CloseAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return (await GetAccountByFirebaseUidAsync(firebaseUid, cancellationToken))!;
                }
            }

            // Assign default role 'PATIENT'
            const string insertRoleSql = @"
                INSERT INTO identity.account_roles (account_id, role_name)
                VALUES ($1, 'PATIENT')
                ON CONFLICT DO NOTHING;";

            await using var roleCmd = new NpgsqlCommand(insertRoleSql, connection, transaction);
            roleCmd.Parameters.AddWithValue(accountId);
            await roleCmd.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new BootstrapResponse(accountId, firebaseUid, "ACTIVE", new[] { "PATIENT" }, "PENDING");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateProvisioningStatusAsync(Guid accountId, string status, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            UPDATE identity.accounts
            SET profile_provisioning_status = $1, updated_at = NOW()
            WHERE account_id = $2;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(accountId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BootstrapResponse?> GetAccountByIdAsync(Guid accountId)
    {
        await using var connection=await _dataSource.OpenConnectionAsync();
        await using var cmd=new NpgsqlCommand("SELECT firebase_uid FROM identity.accounts WHERE account_id=$1",connection);
        cmd.Parameters.AddWithValue(accountId);
        var uid=await cmd.ExecuteScalarAsync() as string;
        return uid is null ? null : await GetAccountByFirebaseUidAsync(uid);
    }
    public async Task<bool> SetDoctorCapabilityAsync(Guid actor, Guid target, bool approved, string reason)
    {
        await using var connection=await _dataSource.OpenConnectionAsync();
        await using var tx=await connection.BeginTransactionAsync();
        await using var account=new NpgsqlCommand("SELECT account_id FROM identity.accounts WHERE account_id=$1 AND account_status='ACTIVE' FOR UPDATE",connection,tx);
        account.Parameters.AddWithValue(target);
        if(await account.ExecuteScalarAsync() is null)return false;
        await using var role=new NpgsqlCommand(approved ? "INSERT INTO identity.account_roles(account_id,role_name) VALUES($1,'DOCTOR') ON CONFLICT DO NOTHING" : "DELETE FROM identity.account_roles WHERE account_id=$1 AND role_name='DOCTOR'",connection,tx);
        role.Parameters.AddWithValue(target);await role.ExecuteNonQueryAsync();
        await using var audit=new NpgsqlCommand("INSERT INTO identity.doctor_capability_audit(actor_account_id,target_account_id,approved,reason) VALUES($1,$2,$3,$4)",connection,tx);
        audit.Parameters.AddWithValue(actor);audit.Parameters.AddWithValue(target);audit.Parameters.AddWithValue(approved);audit.Parameters.AddWithValue(reason);await audit.ExecuteNonQueryAsync();
        await tx.CommitAsync();return true;
    }
}
