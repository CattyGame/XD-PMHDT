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

    public Task<BootstrapResponse?> GetAccountByFirebaseUidAsync(string firebaseUid, CancellationToken cancellationToken = default)
        => GetAccountAsync("a.firebase_uid", firebaseUid, cancellationToken);

    private async Task<BootstrapResponse?> GetAccountAsync(string keyColumn, object key, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var sql = $@"
            SELECT a.account_id, a.firebase_uid, a.account_status, a.profile_provisioning_status,
                   COALESCE(array_agg(ar.role_name) FILTER (WHERE ar.role_name IS NOT NULL), ARRAY[]::VARCHAR[]) as roles
            FROM identity.accounts a
            LEFT JOIN identity.account_roles ar ON a.account_id = ar.account_id
            WHERE {keyColumn} = $1
            GROUP BY a.account_id, a.firebase_uid, a.account_status, a.profile_provisioning_status;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(key);

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

    public async Task<BootstrapResponse> CreateAccountAsync(
        string firebaseUid,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var accountId = Guid.NewGuid();
            var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? firebaseUid
                : displayName.Trim();
            if (resolvedDisplayName.Length > 100)
            {
                resolvedDisplayName = resolvedDisplayName[..100];
            }

            const string insertAccountSql = @"
                INSERT INTO identity.accounts (account_id, firebase_uid, display_name, account_status, profile_provisioning_status, version)
                VALUES ($1, $2, $3, 'ACTIVE', 'PENDING', 1)
                ON CONFLICT (firebase_uid) DO NOTHING
                RETURNING account_id, firebase_uid, account_status, profile_provisioning_status;";

            await using var cmd = new NpgsqlCommand(insertAccountSql, connection, transaction);
            cmd.Parameters.AddWithValue(accountId);
            cmd.Parameters.AddWithValue(firebaseUid);
            cmd.Parameters.AddWithValue(resolvedDisplayName);

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

    public async Task UpdateDisplayNameIfEmptyAsync(Guid accountId, string? displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        var trimmed = displayName.Trim();
        if (trimmed.Length > 100)
        {
            trimmed = trimmed[..100];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            UPDATE identity.accounts
            SET display_name = $1, updated_at = NOW()
            WHERE account_id = $2 AND (display_name IS NULL OR btrim(display_name) = '');";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(trimmed);
        cmd.Parameters.AddWithValue(accountId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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

    public Task<BootstrapResponse?> GetAccountByIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        => GetAccountAsync("a.account_id", accountId, cancellationToken);

    public async Task<bool> SetDoctorCapabilityAsync(
        Guid actor,
        Guid target,
        bool approved,
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        string currentStatus;
        int currentVersion;
        await using (var account = new NpgsqlCommand(
            "SELECT account_status, version FROM identity.accounts WHERE account_id = $1 AND account_status = 'ACTIVE' FOR UPDATE",
            connection, tx))
        {
            account.Parameters.AddWithValue(target);
            await using var reader = await account.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }
            currentStatus = reader.GetString(0);
            currentVersion = reader.GetInt32(1);
        }

        string[] previousRoles;
        await using (var prevRolesCmd = new NpgsqlCommand(
            "SELECT COALESCE(array_agg(role_name ORDER BY role_name), ARRAY[]::VARCHAR[]) FROM identity.account_roles WHERE account_id = $1",
            connection, tx))
        {
            prevRolesCmd.Parameters.AddWithValue(target);
            previousRoles = (string[])(await prevRolesCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var role = new NpgsqlCommand(
            approved
                ? "INSERT INTO identity.account_roles(account_id,role_name) VALUES($1,'DOCTOR') ON CONFLICT DO NOTHING"
                : "DELETE FROM identity.account_roles WHERE account_id=$1 AND role_name='DOCTOR'",
            connection, tx))
        {
            role.Parameters.AddWithValue(target);
            await role.ExecuteNonQueryAsync(cancellationToken);
        }

        string[] newRoles;
        await using (var newRolesCmd = new NpgsqlCommand(
            "SELECT COALESCE(array_agg(role_name ORDER BY role_name), ARRAY[]::VARCHAR[]) FROM identity.account_roles WHERE account_id = $1",
            connection, tx))
        {
            newRolesCmd.Parameters.AddWithValue(target);
            newRoles = (string[])(await newRolesCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        var nextVersion = currentVersion + 1;
        await using (var bump = new NpgsqlCommand(
            "UPDATE identity.accounts SET version = $1, updated_at = NOW() WHERE account_id = $2",
            connection, tx))
        {
            bump.Parameters.AddWithValue(nextVersion);
            bump.Parameters.AddWithValue(target);
            await bump.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var audit = new NpgsqlCommand(
            "INSERT INTO identity.doctor_capability_audit(actor_account_id,target_account_id,approved,reason) VALUES($1,$2,$3,$4)",
            connection, tx))
        {
            audit.Parameters.AddWithValue(actor);
            audit.Parameters.AddWithValue(target);
            audit.Parameters.AddWithValue(approved);
            audit.Parameters.AddWithValue(reason);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var lifecycleAudit = new NpgsqlCommand(@"
            INSERT INTO identity.account_lifecycle_audit (
                actor_account_id, target_account_id, action, previous_status, new_status,
                previous_roles, new_roles, version, reason, correlation_id
            ) VALUES ($1, $2, $3, $4, $4, $5, $6, $7, $8, $9);",
            connection, tx))
        {
            lifecycleAudit.Parameters.AddWithValue(actor);
            lifecycleAudit.Parameters.AddWithValue(target);
            lifecycleAudit.Parameters.AddWithValue(approved ? "DOCTOR_CAPABILITY_APPROVED" : "DOCTOR_CAPABILITY_REVOKED");
            lifecycleAudit.Parameters.AddWithValue(currentStatus);
            lifecycleAudit.Parameters.AddWithValue(previousRoles);
            lifecycleAudit.Parameters.AddWithValue(newRoles);
            lifecycleAudit.Parameters.AddWithValue(nextVersion);
            lifecycleAudit.Parameters.AddWithValue(reason);
            lifecycleAudit.Parameters.AddWithValue((object?)correlationId ?? DBNull.Value);
            await lifecycleAudit.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AdminAccountPageResponse> ListAccountsAdminAsync(
        int limit,
        Guid? cursorAccountId = null,
        string? statusFilter = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var cleanStatus = string.IsNullOrWhiteSpace(statusFilter) || statusFilter.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? null
            : statusFilter.Trim().ToUpperInvariant();
        var cleanSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            SELECT a.account_id, a.firebase_uid,
                   CASE WHEN btrim(COALESCE(a.display_name, '')) = '' THEN a.firebase_uid ELSE a.display_name END,
                   a.account_status,
                   COALESCE(array_agg(ar.role_name ORDER BY ar.role_name) FILTER (WHERE ar.role_name IN ('PATIENT', 'DOCTOR', 'SYSTEM_ADMIN')), ARRAY[]::VARCHAR[]),
                   a.version, a.profile_provisioning_status, a.created_at, a.updated_at
            FROM identity.accounts a
            LEFT JOIN identity.account_roles ar ON a.account_id = ar.account_id
            WHERE ($1::uuid IS NULL OR a.account_id > $1)
              AND ($2::text IS NULL OR a.account_status = $2)
              AND ($3::text IS NULL OR a.firebase_uid ILIKE ('%' || $3 || '%') OR a.display_name ILIKE ('%' || $3 || '%'))
            GROUP BY a.account_id, a.firebase_uid, a.display_name, a.account_status, a.version, a.profile_provisioning_status, a.created_at, a.updated_at
            ORDER BY a.account_id ASC
            LIMIT $4;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue((object?)cursorAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)cleanStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)cleanSearch ?? DBNull.Value);
        cmd.Parameters.AddWithValue(clampedLimit + 1);

        var items = new List<AdminAccountDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var accountStatus = reader.GetString(3);
            items.Add(new AdminAccountDto(
                AccountId: reader.GetGuid(0),
                FirebaseUid: reader.GetString(1),
                DisplayName: reader.GetString(2),
                Status: accountStatus,
                AccountStatus: accountStatus,
                Roles: (string[])reader.GetValue(4),
                Version: reader.GetInt32(5),
                ProfileProvisioningStatus: reader.GetString(6),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(8)
            ));
        }

        string? nextCursor = null;
        if (items.Count > clampedLimit)
        {
            items.RemoveAt(clampedLimit);
            nextCursor = items[^1].AccountId.ToString("D");
        }

        return new AdminAccountPageResponse(items, nextCursor);
    }

    private static async Task<bool> AcquireAdminGuardAndCheckActorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid actorAccountId,
        CancellationToken cancellationToken)
    {
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('identity.system_admin_guard'));",
            connection, tx))
        {
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var actorCmd = new NpgsqlCommand(@"
            SELECT EXISTS(
                SELECT 1
                FROM identity.accounts a
                JOIN identity.account_roles ar ON ar.account_id = a.account_id AND ar.role_name = 'SYSTEM_ADMIN'
                WHERE a.account_id = $1 AND a.account_status = 'ACTIVE'
            );",
            connection, tx);
        actorCmd.Parameters.AddWithValue(actorAccountId);
        return Convert.ToBoolean(await actorCmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<(bool Success, AdminAccountDto? Account, int HttpStatus, string? ErrorCode, string? ErrorMessage)> UpdateAccountStatusAdminAsync(
        Guid actorAccountId,
        Guid targetAccountId,
        string requestedStatus,
        int expectedVersion,
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedStatus = requestedStatus?.Trim().ToUpperInvariant() ?? "";
        var cleanReason = reason?.Trim() ?? "";

        if (!AllowedAccountStatuses.All.Contains(normalizedStatus) || expectedVersion < 1 || cleanReason.Length is < 1 or > 1000)
        {
            return (false, null, 400, "ERR_VALIDATION", "Status (ACTIVE/DISABLED), expectedVersion (>= 1) và reason (1..1000 ký tự) là bắt buộc.");
        }

        if (actorAccountId == targetAccountId && normalizedStatus == AllowedAccountStatuses.Disabled)
        {
            return (false, null, 400, "ERR_SELF_DISABLE_FORBIDDEN", "Quản trị viên không thể tự vô hiệu hóa tài khoản của chính mình.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        if (!await AcquireAdminGuardAndCheckActorAsync(connection, tx, actorAccountId, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return (false, null, 403, "ERR_FORBIDDEN", "Chỉ Quản trị viên hệ thống (SYSTEM_ADMIN) đang hoạt động mới có quyền thực hiện.");
        }

        string currentStatus;
        int currentVersion;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT account_status, version FROM identity.accounts WHERE account_id = $1 FOR UPDATE;",
            connection, tx))
        {
            lockCmd.Parameters.AddWithValue(targetAccountId);
            await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, null, 404, "ERR_NOT_FOUND", "Không tìm thấy tài khoản.");
            }
            currentStatus = reader.GetString(0);
            currentVersion = reader.GetInt32(1);
        }

        if (currentVersion != expectedVersion)
        {
            await tx.RollbackAsync(cancellationToken);
            return (false, null, 409, "ERR_VERSION_CONFLICT", "Phiên bản tài khoản đã thay đổi. Vui lòng tải lại trước khi cập nhật.");
        }

        string[] currentRoles;
        await using (var rolesCmd = new NpgsqlCommand(
            "SELECT COALESCE(array_agg(role_name ORDER BY role_name), ARRAY[]::VARCHAR[]) FROM identity.account_roles WHERE account_id = $1;",
            connection, tx))
        {
            rolesCmd.Parameters.AddWithValue(targetAccountId);
            currentRoles = (string[])(await rolesCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        if (normalizedStatus == AllowedAccountStatuses.Disabled
            && currentRoles.Contains(AllowedGlobalRoles.SystemAdmin, StringComparer.Ordinal))
        {
            await using var adminCountCmd = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM identity.accounts a
                JOIN identity.account_roles ar ON ar.account_id = a.account_id AND ar.role_name = 'SYSTEM_ADMIN'
                WHERE a.account_status = 'ACTIVE' AND a.account_id <> $1;",
                connection, tx);
            adminCountCmd.Parameters.AddWithValue(targetAccountId);
            var remainingAdmins = Convert.ToInt64(await adminCountCmd.ExecuteScalarAsync(cancellationToken));
            if (remainingAdmins <= 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, null, 400, "ERR_LAST_SYSTEM_ADMIN", "Không thể vô hiệu hóa tài khoản SYSTEM_ADMIN hoạt động cuối cùng.");
            }
        }

        var nextVersion = currentVersion + 1;
        await using (var updateCmd = new NpgsqlCommand(@"
            UPDATE identity.accounts
            SET account_status = $1, version = $2, updated_at = NOW()
            WHERE account_id = $3;",
            connection, tx))
        {
            updateCmd.Parameters.AddWithValue(normalizedStatus);
            updateCmd.Parameters.AddWithValue(nextVersion);
            updateCmd.Parameters.AddWithValue(targetAccountId);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var auditCmd = new NpgsqlCommand(@"
            INSERT INTO identity.account_lifecycle_audit (
                actor_account_id, target_account_id, action, previous_status, new_status,
                previous_roles, new_roles, version, reason, correlation_id
            ) VALUES ($1, $2, 'ACCOUNT_STATUS_UPDATE', $3, $4, $5, $5, $6, $7, $8);",
            connection, tx))
        {
            auditCmd.Parameters.AddWithValue(actorAccountId);
            auditCmd.Parameters.AddWithValue(targetAccountId);
            auditCmd.Parameters.AddWithValue(currentStatus);
            auditCmd.Parameters.AddWithValue(normalizedStatus);
            auditCmd.Parameters.AddWithValue(currentRoles);
            auditCmd.Parameters.AddWithValue(nextVersion);
            auditCmd.Parameters.AddWithValue(cleanReason);
            auditCmd.Parameters.AddWithValue((object?)correlationId ?? DBNull.Value);
            await auditCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var updated = await GetAdminAccountByIdAsync(targetAccountId, cancellationToken, connection);
        return (true, updated, 200, null, null);
    }

    public async Task<(bool Success, AdminAccountDto? Account, int HttpStatus, string? ErrorCode, string? ErrorMessage)> UpdateAccountRolesAdminAsync(
        Guid actorAccountId,
        Guid targetAccountId,
        IReadOnlyList<string>? requestedRoles,
        int expectedVersion,
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var cleanReason = reason?.Trim() ?? "";
        if (requestedRoles is null || requestedRoles.Count == 0 || expectedVersion < 1 || cleanReason.Length is < 1 or > 1000)
        {
            return (false, null, 400, "ERR_VALIDATION", "Danh sách roles, expectedVersion (>= 1) và reason (1..1000 ký tự) là bắt buộc.");
        }

        var normalizedRoles = new List<string>();
        foreach (var raw in requestedRoles)
        {
            var role = raw?.Trim().ToUpperInvariant() ?? "";
            if (!AllowedGlobalRoles.All.Contains(role))
            {
                return (false, null, 400, "ERR_INVALID_ROLE", $"Vai trò '{raw}' không hợp lệ.");
            }
            if (!normalizedRoles.Contains(role, StringComparer.Ordinal))
            {
                normalizedRoles.Add(role);
            }
        }
        normalizedRoles.Sort(StringComparer.Ordinal);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        if (!await AcquireAdminGuardAndCheckActorAsync(connection, tx, actorAccountId, cancellationToken))
        {
            await tx.RollbackAsync(cancellationToken);
            return (false, null, 403, "ERR_FORBIDDEN", "Chỉ Quản trị viên hệ thống (SYSTEM_ADMIN) đang hoạt động mới có quyền thực hiện.");
        }

        string currentStatus;
        int currentVersion;
        await using (var lockCmd = new NpgsqlCommand(
            "SELECT account_status, version FROM identity.accounts WHERE account_id = $1 FOR UPDATE;",
            connection, tx))
        {
            lockCmd.Parameters.AddWithValue(targetAccountId);
            await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, null, 404, "ERR_NOT_FOUND", "Không tìm thấy tài khoản.");
            }
            currentStatus = reader.GetString(0);
            currentVersion = reader.GetInt32(1);
        }

        if (currentVersion != expectedVersion)
        {
            await tx.RollbackAsync(cancellationToken);
            return (false, null, 409, "ERR_VERSION_CONFLICT", "Phiên bản tài khoản đã thay đổi. Vui lòng tải lại trước khi cập nhật.");
        }

        string[] currentRoles;
        await using (var rolesCmd = new NpgsqlCommand(
            "SELECT COALESCE(array_agg(role_name ORDER BY role_name), ARRAY[]::VARCHAR[]) FROM identity.account_roles WHERE account_id = $1;",
            connection, tx))
        {
            rolesCmd.Parameters.AddWithValue(targetAccountId);
            currentRoles = (string[])(await rolesCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        var hadDoctor = currentRoles.Contains(AllowedGlobalRoles.Doctor, StringComparer.Ordinal);
        var wantsDoctor = normalizedRoles.Contains(AllowedGlobalRoles.Doctor, StringComparer.Ordinal);
        if (hadDoctor != wantsDoctor)
        {
            await tx.RollbackAsync(cancellationToken);
            return (false, null, 400, "ERR_DOCTOR_CAPABILITY_SEPARATED", "Quyền DOCTOR phải được phê duyệt hoặc thu hồi qua endpoint doctor-capability.");
        }

        var hadAdmin = currentRoles.Contains(AllowedGlobalRoles.SystemAdmin, StringComparer.Ordinal);
        var wantsAdmin = normalizedRoles.Contains(AllowedGlobalRoles.SystemAdmin, StringComparer.Ordinal);
        if (hadAdmin && !wantsAdmin)
        {
            if (actorAccountId == targetAccountId)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, null, 400, "ERR_SELF_DEMOTE_FORBIDDEN", "Quản trị viên không thể tự gỡ quyền SYSTEM_ADMIN của chính mình.");
            }

            await using var adminCountCmd = new NpgsqlCommand(@"
                SELECT COUNT(*)
                FROM identity.accounts a
                JOIN identity.account_roles ar ON ar.account_id = a.account_id AND ar.role_name = 'SYSTEM_ADMIN'
                WHERE a.account_status = 'ACTIVE' AND a.account_id <> $1;",
                connection, tx);
            adminCountCmd.Parameters.AddWithValue(targetAccountId);
            var remainingAdmins = Convert.ToInt64(await adminCountCmd.ExecuteScalarAsync(cancellationToken));
            if (remainingAdmins <= 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return (false, null, 400, "ERR_LAST_SYSTEM_ADMIN", "Không thể gỡ quyền SYSTEM_ADMIN của quản trị viên hoạt động cuối cùng.");
            }
        }

        await using (var deleteCmd = new NpgsqlCommand(
            "DELETE FROM identity.account_roles WHERE account_id = $1 AND NOT (role_name = ANY($2));",
            connection, tx))
        {
            deleteCmd.Parameters.AddWithValue(targetAccountId);
            deleteCmd.Parameters.AddWithValue(normalizedRoles.ToArray());
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var role in normalizedRoles)
        {
            await using var insertRoleCmd = new NpgsqlCommand(
                "INSERT INTO identity.account_roles (account_id, role_name) VALUES ($1, $2) ON CONFLICT DO NOTHING;",
                connection, tx);
            insertRoleCmd.Parameters.AddWithValue(targetAccountId);
            insertRoleCmd.Parameters.AddWithValue(role);
            await insertRoleCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var nextVersion = currentVersion + 1;
        await using (var bumpCmd = new NpgsqlCommand(
            "UPDATE identity.accounts SET version = $1, updated_at = NOW() WHERE account_id = $2;",
            connection, tx))
        {
            bumpCmd.Parameters.AddWithValue(nextVersion);
            bumpCmd.Parameters.AddWithValue(targetAccountId);
            await bumpCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var auditCmd = new NpgsqlCommand(@"
            INSERT INTO identity.account_lifecycle_audit (
                actor_account_id, target_account_id, action, previous_status, new_status,
                previous_roles, new_roles, version, reason, correlation_id
            ) VALUES ($1, $2, 'ACCOUNT_ROLES_UPDATE', $3, $3, $4, $5, $6, $7, $8);",
            connection, tx))
        {
            auditCmd.Parameters.AddWithValue(actorAccountId);
            auditCmd.Parameters.AddWithValue(targetAccountId);
            auditCmd.Parameters.AddWithValue(currentStatus);
            auditCmd.Parameters.AddWithValue(currentRoles);
            auditCmd.Parameters.AddWithValue(normalizedRoles.ToArray());
            auditCmd.Parameters.AddWithValue(nextVersion);
            auditCmd.Parameters.AddWithValue(cleanReason);
            auditCmd.Parameters.AddWithValue((object?)correlationId ?? DBNull.Value);
            await auditCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var updated = await GetAdminAccountByIdAsync(targetAccountId, cancellationToken, connection);
        return (true, updated, 200, null, null);
    }

    public async Task<AdminAccountDto?> GetAdminAccountByIdAsync(
        Guid accountId,
        CancellationToken cancellationToken = default,
        NpgsqlConnection? existingConnection = null)
    {
        await using var ownedConnection = existingConnection is null
            ? await _dataSource.OpenConnectionAsync(cancellationToken)
            : null;
        var connection = existingConnection ?? ownedConnection!;
        const string sql = @"
            SELECT a.account_id, a.firebase_uid,
                   CASE WHEN btrim(COALESCE(a.display_name, '')) = '' THEN a.firebase_uid ELSE a.display_name END,
                   a.account_status,
                   COALESCE(array_agg(ar.role_name ORDER BY ar.role_name) FILTER (WHERE ar.role_name IN ('PATIENT', 'DOCTOR', 'SYSTEM_ADMIN')), ARRAY[]::VARCHAR[]),
                   a.version, a.profile_provisioning_status, a.created_at, a.updated_at
            FROM identity.accounts a
            LEFT JOIN identity.account_roles ar ON a.account_id = ar.account_id
            WHERE a.account_id = $1
            GROUP BY a.account_id, a.firebase_uid, a.display_name, a.account_status, a.version, a.profile_provisioning_status, a.created_at, a.updated_at;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(accountId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = reader.GetString(3);
        return new AdminAccountDto(
            AccountId: reader.GetGuid(0),
            FirebaseUid: reader.GetString(1),
            DisplayName: reader.GetString(2),
            Status: status,
            AccountStatus: status,
            Roles: (string[])reader.GetValue(4),
            Version: reader.GetInt32(5),
            ProfileProvisioningStatus: reader.GetString(6),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(8)
        );
    }
}
