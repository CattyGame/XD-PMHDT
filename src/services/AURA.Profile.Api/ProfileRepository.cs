using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Profile.Api.Models;
using Npgsql;

namespace AURA.Profile.Api;

public partial class ProfileRepository
{
    private readonly NpgsqlDataSource _dataSource;

    private readonly IAccountAuthority? _accountAuthority;
    private readonly IAnalysisBindingAuthority? _analysisBindingAuthority;

    public ProfileRepository(NpgsqlDataSource dataSource, IAccountAuthority? accountAuthority = null, IAnalysisBindingAuthority? analysisBindingAuthority = null)
    {
        _dataSource = dataSource;
        _accountAuthority = accountAuthority;
        _analysisBindingAuthority = analysisBindingAuthority;
    }

    public async Task<UserProfileResponse?> GetProfileByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT p.account_id, p.display_name, p.phone, p.personal_tenant_id, p.profile_version,
                   pt.patient_id, p.email
            FROM profile.profiles p
            LEFT JOIN profile.patients pt ON pt.linked_account_id = p.account_id
            WHERE p.account_id = $1
            LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(accountId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var accId = reader.GetGuid(0);
            var displayName = reader.GetString(1);
            var phone = reader.IsDBNull(2) ? null : reader.GetString(2);
            var personalTenantId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3);
            var profileVersion = reader.GetInt32(4);
            var patientId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
            var email = reader.IsDBNull(6) ? null : reader.GetString(6);

            return new UserProfileResponse(accId, displayName, phone, patientId, personalTenantId, profileVersion, email);
        }

        return null;
    }

    public async Task<UserProfileResponse> ProvisionProfileAsync(
        Guid accountId, string displayName, string? email = null, CancellationToken cancellationToken = default)
    {
        var existing = await GetProfileByAccountIdAsync(accountId, cancellationToken);
        if (existing != null && existing.PersonalTenantId.HasValue && existing.PatientId.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                await AutoActivatePendingMembershipsAsync(accountId, email, cancellationToken);
            }
            return existing;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            Guid tenantId;
            const string findTenantSql = @"
                SELECT tenant_id FROM profile.tenants
                WHERE owner_principal_id = $1 AND tenant_kind = 'PERSONAL'
                LIMIT 1;";

            await using (var findTenantCmd = new NpgsqlCommand(findTenantSql, connection, transaction))
            {
                findTenantCmd.Parameters.AddWithValue(accountId);
                var obj = await findTenantCmd.ExecuteScalarAsync(cancellationToken);
                if (obj is Guid existingTenantId)
                {
                    tenantId = existingTenantId;
                }
                else
                {
                    tenantId = Guid.NewGuid();
                    const string insertTenantSql = @"
                        INSERT INTO profile.tenants (tenant_id, tenant_kind, owner_principal_id)
                        VALUES ($1, 'PERSONAL', $2)
                        ON CONFLICT DO NOTHING;";

                    await using var tenantCmd = new NpgsqlCommand(insertTenantSql, connection, transaction);
                    tenantCmd.Parameters.AddWithValue(tenantId);
                    tenantCmd.Parameters.AddWithValue(accountId);
                    await tenantCmd.ExecuteNonQueryAsync(cancellationToken);

                    await using var reFindCmd = new NpgsqlCommand(findTenantSql, connection, transaction);
                    reFindCmd.Parameters.AddWithValue(accountId);
                    var reObj = await reFindCmd.ExecuteScalarAsync(cancellationToken);
                    if (reObj is Guid reId)
                    {
                        tenantId = reId;
                    }
                }
            }

            const string insertProfileSql = @"
                INSERT INTO profile.profiles (account_id, display_name, personal_tenant_id, email, profile_version)
                VALUES ($1, $2, $3, $4, 1)
                ON CONFLICT (account_id) DO UPDATE
                    SET display_name = EXCLUDED.display_name,
                        email = COALESCE(profile.profiles.email, EXCLUDED.email),
                        personal_tenant_id = COALESCE(profile.profiles.personal_tenant_id, EXCLUDED.personal_tenant_id);";

            await using (var profileCmd = new NpgsqlCommand(insertProfileSql, connection, transaction))
            {
                profileCmd.Parameters.AddWithValue(accountId);
                profileCmd.Parameters.AddWithValue(displayName);
                profileCmd.Parameters.AddWithValue(tenantId);
                profileCmd.Parameters.AddWithValue((object?)email ?? DBNull.Value);
                await profileCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string findPatientSql = @"
                SELECT patient_id FROM profile.patients
                WHERE linked_account_id = $1 AND tenant_id = $2
                LIMIT 1;";

            await using (var findPatientCmd = new NpgsqlCommand(findPatientSql, connection, transaction))
            {
                findPatientCmd.Parameters.AddWithValue(accountId);
                findPatientCmd.Parameters.AddWithValue(tenantId);
                var pObj = await findPatientCmd.ExecuteScalarAsync(cancellationToken);
                if (pObj is not Guid)
                {
                    var patientId = Guid.NewGuid();
                    const string insertPatientSql = @"
                        INSERT INTO profile.patients (patient_id, tenant_id, linked_account_id, owner_principal_id, status)
                        VALUES ($1, $2, $3, $4, 'ACTIVE')
                        ON CONFLICT DO NOTHING;";

                    await using var patientCmd = new NpgsqlCommand(insertPatientSql, connection, transaction);
                    patientCmd.Parameters.AddWithValue(patientId);
                    patientCmd.Parameters.AddWithValue(tenantId);
                    patientCmd.Parameters.AddWithValue(accountId);
                    patientCmd.Parameters.AddWithValue(accountId);
                    await patientCmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(email))
            {
                await AutoActivatePendingMembershipsAsync(accountId, email, cancellationToken);
            }

            return (await GetProfileByAccountIdAsync(accountId, cancellationToken))!;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<(bool Success, UserProfileResponse? UpdatedProfile, bool Conflict)> UpdateProfileAsync(
        Guid accountId, string displayName, string? phone, int expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string updateSql = @"
            UPDATE profile.profiles
            SET display_name = $1,
                phone = $2,
                profile_version = profile_version + 1,
                updated_at = NOW()
            WHERE account_id = $3 AND profile_version = $4
            RETURNING profile_version;";

        await using var cmd = new NpgsqlCommand(updateSql, connection);
        cmd.Parameters.AddWithValue(displayName);
        cmd.Parameters.AddWithValue((object?)phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue(accountId);
        cmd.Parameters.AddWithValue(expectedVersion);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is int)
        {
            var updated = await GetProfileByAccountIdAsync(accountId, cancellationToken);
            return (true, updated, false);
        }

        const string checkSql = "SELECT profile_version FROM profile.profiles WHERE account_id = $1;";
        await using var checkCmd = new NpgsqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue(accountId);
        var currentVersionObj = await checkCmd.ExecuteScalarAsync(cancellationToken);

        if (currentVersionObj is int)
        {
            return (false, null, true);
        }

        return (false, null, false);
    }

    public Task<AccessResolveResponse> ResolveAccessAsync(
        Guid actorAccountId, Guid patientId, Guid? clinicContextId, string action, CancellationToken cancellationToken = default)
        => ResolveAccessAsync(actorAccountId, patientId, clinicContextId, action, null, cancellationToken);

    public async Task<AccessResolveResponse> ResolveAccessAsync(
        Guid actorAccountId, Guid patientId, Guid? clinicContextId, string action, Guid? analysisId, CancellationToken cancellationToken = default)
    {
        return await ResolvePatientAccessCoreAsync(actorAccountId, patientId, clinicContextId, action, analysisId, cancellationToken);
    }

    public async Task<CameraDelegationResolveResponse> ResolveCameraDelegationAsync(
        CameraDelegationResolveRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Action, "ANALYSIS_CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return new CameraDelegationResolveResponse(false, null, null, null, null, null, null, "INVALID_ACTION");
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.ClientId)
            || request.PatientId == Guid.Empty || request.ExpectedTenantId == Guid.Empty
            || request.ActorAccountId == Guid.Empty || request.ServicePrincipalId == Guid.Empty)
        {
            return new CameraDelegationResolveResponse(false, null, null, null, null, null, null, "INVALID_REQUEST_PARAMETERS");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 1. Kiểm tra bệnh nhân và tenant
        const string patientSql = @"
            SELECT p.patient_id, p.tenant_id, p.linked_account_id, p.owner_principal_id, p.status, t.tenant_kind
            FROM profile.patients p
            JOIN profile.tenants t ON t.tenant_id = p.tenant_id
            WHERE p.patient_id = $1;";

        Guid pTenantId;
        Guid? pLinkedAccountId;
        Guid pOwnerPrincipalId;
        string pStatus;
        string tenantKind;

        await using (var pCmd = new NpgsqlCommand(patientSql, connection))
        {
            pCmd.Parameters.AddWithValue(request.PatientId);
            await using var reader = await pCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new CameraDelegationResolveResponse(false, null, null, null, null, null, null, "PATIENT_NOT_FOUND");
            }

            pTenantId = reader.GetGuid(1);
            pLinkedAccountId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
            pOwnerPrincipalId = reader.GetGuid(3);
            pStatus = reader.GetString(4);
            tenantKind = reader.GetString(5);
        }

        // Camera Sprint 3 chỉ áp dụng cho tenant PERSONAL
        if (!string.Equals(tenantKind, "PERSONAL", StringComparison.OrdinalIgnoreCase))
        {
            return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, null, "CLINIC_CAMERA_UNSUPPORTED");
        }

        if (!string.Equals(pStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, null, "PATIENT_DISABLED");
        }

        // Kiểm tra actorAccountId phải là chủ sở hữu hoặc tài khoản liên kết của bệnh nhân
        var isOwn = (pLinkedAccountId.HasValue && pLinkedAccountId.Value == request.ActorAccountId)
                    || pOwnerPrincipalId == request.ActorAccountId;
        if (!isOwn)
        {
            return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, null, "ACTOR_PATIENT_MISMATCH");
        }

        // Kiểm tra expectedTenantId phải khớp với tenantId của bệnh nhân
        if (request.ExpectedTenantId != pTenantId)
        {
            return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, null, "TENANT_MISMATCH");
        }

        // 2. Kiểm tra bản ghi ủy quyền Camera (profile.camera_grants)
        const string grantSql = @"
            SELECT grant_id, status, expires_at
            FROM profile.camera_grants
            WHERE device_id = $1 AND client_id = $2 AND service_principal_id = $3
              AND patient_id = $4 AND tenant_id = $5
            LIMIT 1;";

        Guid grantId;
        string grantStatus;
        DateTimeOffset expiresAt;

        await using (var gCmd = new NpgsqlCommand(grantSql, connection))
        {
            gCmd.Parameters.AddWithValue(request.DeviceId);
            gCmd.Parameters.AddWithValue(request.ClientId);
            gCmd.Parameters.AddWithValue(request.ServicePrincipalId);
            gCmd.Parameters.AddWithValue(request.PatientId);
            gCmd.Parameters.AddWithValue(pTenantId);

            await using var reader = await gCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, null, "GRANT_NOT_FOUND");
            }

            grantId = reader.GetGuid(0);
            grantStatus = reader.GetString(1);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(2);
        }

        if (!string.Equals(grantStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, grantId, "GRANT_REVOKED");
        }

        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, grantId, "GRANT_EXPIRED");
        }

        // 3. Kiểm tra trạng thái tài khoản Actor trong identity.accounts (Fail-Closed)
        const string accSql = "SELECT account_status FROM identity.accounts WHERE account_id = $1 LIMIT 1;";
        await using (var aCmd = new NpgsqlCommand(accSql, connection))
        {
            aCmd.Parameters.AddWithValue(request.ActorAccountId);
            var aObj = await aCmd.ExecuteScalarAsync(cancellationToken);
            if (aObj is not string accStatus || !string.Equals(accStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return new CameraDelegationResolveResponse(false, request.PatientId, pTenantId, tenantKind, null, null, grantId, "ACCOUNT_DISABLED");
            }
        }

        // 4. Hợp lệ hoàn toàn
        return new CameraDelegationResolveResponse(
            Allowed: true,
            PatientId: request.PatientId,
            TenantId: pTenantId,
            TenantKind: "PERSONAL",
            OwnerPrincipalId: request.ActorAccountId,
            BillingOwner: new BillingOwnerDto("ACCOUNT", request.ActorAccountId),
            GrantId: grantId,
            Reason: null
        );
    }

    // ====================================================================
    // CLINIC LIFECYCLE MANAGEMENT (Task W4-M6-01 / SCRUM-358)
    // ====================================================================

    public async Task<ClinicDto> RegisterClinicAsync(
        Guid creatorAccountId,
        RegisterClinicRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var tenantId = Guid.NewGuid();
            const string insertTenantSql = @"
                INSERT INTO profile.tenants (tenant_id, tenant_kind, owner_principal_id)
                VALUES ($1, 'CLINIC', $2);";

            await using (var tenantCmd = new NpgsqlCommand(insertTenantSql, connection, transaction))
            {
                tenantCmd.Parameters.AddWithValue(tenantId);
                tenantCmd.Parameters.AddWithValue(creatorAccountId);
                await tenantCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var clinicId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            const string insertClinicSql = @"
                INSERT INTO profile.clinics (
                    clinic_id, tenant_id, name, tax_id, address, representative_name, phone, status, created_at, updated_at
                )
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9);";

            await using (var clinicCmd = new NpgsqlCommand(insertClinicSql, connection, transaction))
            {
                clinicCmd.Parameters.AddWithValue(clinicId);
                clinicCmd.Parameters.AddWithValue(tenantId);
                clinicCmd.Parameters.AddWithValue(request.Name.Trim());
                clinicCmd.Parameters.AddWithValue((object?)request.TaxId ?? DBNull.Value);
                clinicCmd.Parameters.AddWithValue((object?)request.Address ?? DBNull.Value);
                clinicCmd.Parameters.AddWithValue((object?)request.RepresentativeName ?? DBNull.Value);
                clinicCmd.Parameters.AddWithValue((object?)request.Phone ?? DBNull.Value);
                clinicCmd.Parameters.AddWithValue(ClinicStatuses.Pending);
                clinicCmd.Parameters.AddWithValue(now);
                await clinicCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Gán creator làm CLINIC_ADMIN ban đầu
            var membershipId = Guid.NewGuid();
            const string insertMemberSql = @"
                INSERT INTO profile.memberships (
                    membership_id, clinic_id, account_id, role, status, created_at, updated_at
                )
                VALUES ($1, $2, $3, $4, $5, $6, $6);";

            await using (var memberCmd = new NpgsqlCommand(insertMemberSql, connection, transaction))
            {
                memberCmd.Parameters.AddWithValue(membershipId);
                memberCmd.Parameters.AddWithValue(clinicId);
                memberCmd.Parameters.AddWithValue(creatorAccountId);
                memberCmd.Parameters.AddWithValue(ClinicMemberRoles.ClinicAdmin);
                memberCmd.Parameters.AddWithValue(MembershipStatuses.Active);
                memberCmd.Parameters.AddWithValue(now);
                await memberCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new ClinicDto(
                clinicId,
                tenantId,
                request.Name.Trim(),
                request.TaxId,
                request.Address,
                request.RepresentativeName,
                request.Phone,
                ClinicStatuses.Pending,
                null,
                now,
                now
            );
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ClinicDto?> GetClinicByIdAsync(Guid clinicId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = @"
            SELECT clinic_id, tenant_id, name, tax_id, address, representative_name, phone, status, audit_reason, created_at, updated_at
            FROM profile.clinics
            WHERE clinic_id = $1
            LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(clinicId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClinicDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(10)
        );
    }

    public async Task<ClinicDto?> UpdateClinicStatusAsync(
        Guid adminAccountId,
        Guid clinicId,
        string newStatus,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            const string selectSql = @"
                SELECT status FROM profile.clinics
                WHERE clinic_id = $1
                FOR UPDATE;";

            string? previousStatus = null;
            await using (var selectCmd = new NpgsqlCommand(selectSql, connection, transaction))
            {
                selectCmd.Parameters.AddWithValue(clinicId);
                var prevObj = await selectCmd.ExecuteScalarAsync(cancellationToken);
                if (prevObj is string prev)
                {
                    previousStatus = prev;
                }
            }

            if (previousStatus == null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            const string updateSql = @"
                UPDATE profile.clinics
                SET status = $1, audit_reason = $2, updated_at = $3
                WHERE clinic_id = $4;";

            await using (var updateCmd = new NpgsqlCommand(updateSql, connection, transaction))
            {
                updateCmd.Parameters.AddWithValue(newStatus);
                updateCmd.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue(now);
                updateCmd.Parameters.AddWithValue(clinicId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            const string logSql = @"
                INSERT INTO profile.clinic_audit_logs (
                    clinic_id, previous_status, new_status, reason, changed_by, changed_at
                )
                VALUES ($1, $2, $3, $4, $5, $6);";

            await using (var logCmd = new NpgsqlCommand(logSql, connection, transaction))
            {
                logCmd.Parameters.AddWithValue(clinicId);
                logCmd.Parameters.AddWithValue(previousStatus);
                logCmd.Parameters.AddWithValue(newStatus);
                logCmd.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
                logCmd.Parameters.AddWithValue(adminAccountId);
                logCmd.Parameters.AddWithValue(now);
                await logCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return await GetClinicByIdAsync(clinicId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<(ClinicMembershipDto? Result, string? ErrorCode)> InviteClinicMemberAsync(
        Guid clinicId,
        string email,
        string role,
        CancellationToken cancellationToken = default)
    {
        var normalizedRole = ClinicMemberRoles.NormalizeRole(role);
        if (normalizedRole == null)
        {
            return (null, "ERR_INVALID_ROLE");
        }

        var cleanEmail = email.Trim().ToLowerInvariant();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Kiểm tra tài khoản đã tồn tại trong profile.profiles chưa
            const string findProfileSql = @"
                SELECT p.account_id, p.display_name
                FROM profile.profiles p
                WHERE LOWER(p.email) = $1
                LIMIT 1;";

            Guid? existingAccountId = null;
            string? displayName = null;

            await using (var findCmd = new NpgsqlCommand(findProfileSql, connection, transaction))
            {
                findCmd.Parameters.AddWithValue(cleanEmail);
                await using var reader = await findCmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    existingAccountId = reader.GetGuid(0);
                    displayName = reader.GetString(1);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var invitationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var invitationHash = SHA256.HashData(Encoding.UTF8.GetBytes(invitationToken));
            // ADR-005 D2 & R15: Require explicit invitation acceptance for all accounts (no auto-active on invite)
            var initialStatus = MembershipStatuses.Invited;

            // 2. Kiểm tra xem đã có membership với account_id hoặc email này trong clinic chưa
            const string checkMembershipSql = @"
                SELECT membership_id, status, role
                FROM profile.memberships
                WHERE clinic_id = $1 AND (
                    (account_id IS NOT NULL AND account_id = $2) OR
                    (invited_email IS NOT NULL AND LOWER(invited_email) = $3)
                )
                LIMIT 1;";

            Guid? existingMembershipId = null;
            string? currentStatus = null;

            await using (var checkCmd = new NpgsqlCommand(checkMembershipSql, connection, transaction))
            {
                checkCmd.Parameters.AddWithValue(clinicId);
                checkCmd.Parameters.AddWithValue((object?)existingAccountId ?? DBNull.Value);
                checkCmd.Parameters.AddWithValue(cleanEmail);
                await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    existingMembershipId = reader.GetGuid(0);
                    currentStatus = reader.GetString(1);
                }
            }

            if (existingMembershipId.HasValue && string.Equals(currentStatus, MembershipStatuses.Active, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (null, "ERR_MEMBERSHIP_ALREADY_EXISTS");
            }

            Guid membershipId;
            if (existingMembershipId.HasValue)
            {
                membershipId = existingMembershipId.Value;
                const string updateSql = @"
                    UPDATE profile.memberships
                    SET role = $1, status = $2, account_id = $3,
                        invited_email = $4, created_at = $5, updated_at = $5
                    WHERE membership_id = $6;";

                await using var updateCmd = new NpgsqlCommand(updateSql, connection, transaction);
                updateCmd.Parameters.AddWithValue(normalizedRole);
                updateCmd.Parameters.AddWithValue(initialStatus);
                updateCmd.Parameters.AddWithValue((object?)existingAccountId ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue(cleanEmail);
                updateCmd.Parameters.AddWithValue(now);
                updateCmd.Parameters.AddWithValue(membershipId);
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                membershipId = Guid.NewGuid();
                const string insertSql = @"
                    INSERT INTO profile.memberships (
                        membership_id, clinic_id, account_id, role, status, invited_email, created_at, updated_at
                    )
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $7);";

                await using var insertCmd = new NpgsqlCommand(insertSql, connection, transaction);
                insertCmd.Parameters.AddWithValue(membershipId);
                insertCmd.Parameters.AddWithValue(clinicId);
                insertCmd.Parameters.AddWithValue((object?)existingAccountId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue(normalizedRole);
                insertCmd.Parameters.AddWithValue(initialStatus);
                insertCmd.Parameters.AddWithValue(cleanEmail);
                insertCmd.Parameters.AddWithValue(now);
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var invite = new NpgsqlCommand("""
                INSERT INTO profile.clinic_invitations(membership_id,token_hash,issued_at,expires_at,consumed_at)
                VALUES($1,$2,$3,$4,NULL) ON CONFLICT(membership_id) DO UPDATE
                SET token_hash=EXCLUDED.token_hash,issued_at=EXCLUDED.issued_at,expires_at=EXCLUDED.expires_at,consumed_at=NULL
                """,connection,transaction))
            {
                invite.Parameters.AddWithValue(membershipId);invite.Parameters.AddWithValue(invitationHash);
                invite.Parameters.AddWithValue(now);invite.Parameters.AddWithValue(now.AddHours(72));
                await invite.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);

            return (new ClinicMembershipDto(
                membershipId,
                clinicId,
                existingAccountId,
                displayName ?? (cleanEmail.Split('@')[0]),
                normalizedRole,
                initialStatus,
                now,
                now,
                cleanEmail, invitationToken
            ), null);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> AutoActivatePendingMembershipsAsync(
        Guid accountId,
        string? email,
        CancellationToken cancellationToken = default)
    {
        // ADR-005 D2 & R15: Deprecated legacy auto-activation.
        // Explicit acceptance via /api/v1/clinics/invitations/{membershipId}/accept is mandatory.
        return 0;
    }

    // ====================================================================
    // CLINIC MEMBERSHIP MANAGEMENT (Task W3-M6-01)
    // ====================================================================

    public async Task<(bool Allowed, string? FailureReason)> CheckClinicManagementAuthorityAsync(
        Guid actorAccountId, Guid clinicId, IReadOnlyList<string> actorRoles, CancellationToken cancellationToken = default)
    {
        if (actorRoles.Contains("SYSTEM_ADMIN", StringComparer.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string clinicSql = "SELECT tenant_id FROM profile.clinics WHERE clinic_id = $1 AND status = 'ACTIVE' LIMIT 1;";
        await using (var clinicCmd = new NpgsqlCommand(clinicSql, connection))
        {
            clinicCmd.Parameters.AddWithValue(clinicId);
            var tenantObj = await clinicCmd.ExecuteScalarAsync(cancellationToken);
            if (tenantObj is not Guid)
            {
                return (false, "CLINIC_NOT_FOUND");
            }
        }

        const string memberSql = @"
            SELECT role FROM profile.memberships
            WHERE clinic_id = $1 AND account_id = $2 AND status = 'ACTIVE'
            LIMIT 1;";

        await using (var memberCmd = new NpgsqlCommand(memberSql, connection))
        {
            memberCmd.Parameters.AddWithValue(clinicId);
            memberCmd.Parameters.AddWithValue(actorAccountId);
            var roleObj = await memberCmd.ExecuteScalarAsync(cancellationToken);
            if (roleObj is not string role)
            {
                return (false, "NOT_A_MEMBER");
            }

            if (string.Equals(role, ClinicMemberRoles.ClinicAdmin, StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }

            return (false, "FORBIDDEN");
        }
    }

    public async Task<(bool Allowed, string? FailureReason)> CheckClinicAccessAuthorityAsync(
        Guid actorAccountId, Guid clinicId, IReadOnlyList<string> actorRoles, CancellationToken cancellationToken = default)
    {
        var decision=await ResolveClinicAccessAsync(actorAccountId,clinicId,"REGISTRY_READ",cancellationToken);
        return(decision.Allowed,decision.Reason);
    }

    public async Task<IReadOnlyList<ClinicMembershipDto>> GetClinicMembersAsync(
        Guid clinicId, string? statusFilter = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var queryFilter = string.IsNullOrWhiteSpace(statusFilter) || statusFilter.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? null
            : statusFilter.Trim().ToUpperInvariant();

        const string sql = @"
            SELECT m.membership_id, m.clinic_id, m.account_id,
                   COALESCE(p.display_name, m.invited_email, 'Thành viên phòng khám'),
                   m.role, m.status, m.created_at, m.updated_at, m.invited_email
            FROM profile.memberships m
            LEFT JOIN profile.profiles p ON p.account_id = m.account_id
            WHERE m.clinic_id = $1
              AND ($2::text IS NULL OR m.status = $2)
            ORDER BY m.created_at ASC;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.AddWithValue((object?)queryFilter ?? DBNull.Value);

        var items = new List<ClinicMembershipDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var membershipId = reader.GetGuid(0);
            var cId = reader.GetGuid(1);
            var accId = reader.IsDBNull(2) ? (Guid?)null : reader.GetGuid(2);
            var displayName = reader.IsDBNull(3) ? null : reader.GetString(3);
            var role = reader.GetString(4);
            var status = reader.GetString(5);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(6);
            var updatedAt = reader.GetFieldValue<DateTimeOffset>(7);
            var invitedEmail = reader.IsDBNull(8) ? null : reader.GetString(8);

            items.Add(new ClinicMembershipDto(
                membershipId, cId, accId, displayName, role, status, createdAt, updatedAt, invitedEmail));
        }

        return items;
    }

    public async Task<(bool Success, ClinicMembershipDto? Membership, string? ErrorCode, string? ErrorMessage)> AddOrReactivateClinicMemberAsync(
        Guid clinicId, Guid targetAccountId, string role, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string checkProfileSql = "SELECT display_name FROM profile.profiles WHERE account_id = $1 LIMIT 1;";
        string displayName;
        await using (var profileCmd = new NpgsqlCommand(checkProfileSql, connection))
        {
            profileCmd.Parameters.AddWithValue(targetAccountId);
            var nameObj = await profileCmd.ExecuteScalarAsync(cancellationToken);
            if (nameObj is not string name)
            {
                return (false, null, "ERR_ACCOUNT_NOT_FOUND", "Không tìm thấy hồ sơ người dùng tương ứng.");
            }
            displayName = name;
        }

        const string checkMembershipSql = @"
            SELECT membership_id, role, status
            FROM profile.memberships
            WHERE clinic_id = $1 AND account_id = $2
            LIMIT 1;";

        Guid? existingMembershipId = null;
        string? existingStatus = null;

        await using (var checkCmd = new NpgsqlCommand(checkMembershipSql, connection))
        {
            checkCmd.Parameters.AddWithValue(clinicId);
            checkCmd.Parameters.AddWithValue(targetAccountId);
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingMembershipId = reader.GetGuid(0);
                existingStatus = reader.GetString(2);
            }
        }

        if (existingMembershipId.HasValue && string.Equals(existingStatus, MembershipStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "ERR_MEMBERSHIP_ALREADY_EXISTS", "Thành viên đã tồn tại và đang hoạt động trong phòng khám.");
        }

        if (existingMembershipId.HasValue)
        {
            const string reactivateSql = @"
                UPDATE profile.memberships
                SET role = $1, status = 'ACTIVE', updated_at = NOW()
                WHERE clinic_id = $2 AND account_id = $3
                RETURNING membership_id, created_at, updated_at;";

            await using var reactivateCmd = new NpgsqlCommand(reactivateSql, connection);
            reactivateCmd.Parameters.AddWithValue(role);
            reactivateCmd.Parameters.AddWithValue(clinicId);
            reactivateCmd.Parameters.AddWithValue(targetAccountId);

            await using var reader = await reactivateCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var membershipId = reader.GetGuid(0);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(2);

                return (true, new ClinicMembershipDto(
                    membershipId, clinicId, targetAccountId, displayName, role, MembershipStatuses.Active, createdAt, updatedAt), null, null);
            }
        }
        else
        {
            var newMembershipId = Guid.NewGuid();
            const string insertSql = @"
                INSERT INTO profile.memberships (membership_id, clinic_id, account_id, role, status, created_at, updated_at)
                VALUES ($1, $2, $3, $4, 'ACTIVE', NOW(), NOW())
                RETURNING created_at, updated_at;";

            await using var insertCmd = new NpgsqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue(newMembershipId);
            insertCmd.Parameters.AddWithValue(clinicId);
            insertCmd.Parameters.AddWithValue(targetAccountId);
            insertCmd.Parameters.AddWithValue(role);

            await using var reader = await insertCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var createdAt = reader.GetFieldValue<DateTimeOffset>(0);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(1);

                return (true, new ClinicMembershipDto(
                    newMembershipId, clinicId, targetAccountId, displayName, role, MembershipStatuses.Active, createdAt, updatedAt), null, null);
            }
        }

        return (false, null, "ERR_INTERNAL_DB", "Không thể ghi nhận thành viên phòng khám.");
    }

    public async Task<(bool Success, string? ErrorCode, string? ErrorMessage)> RevokeClinicMemberAsync(
        Guid clinicId, Guid targetAccountId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string checkSql = @"
            SELECT role FROM profile.memberships
            WHERE clinic_id = $1 AND account_id = $2 AND status = 'ACTIVE'
            LIMIT 1;";

        string role;
        await using (var checkCmd = new NpgsqlCommand(checkSql, connection))
        {
            checkCmd.Parameters.AddWithValue(clinicId);
            checkCmd.Parameters.AddWithValue(targetAccountId);
            var roleObj = await checkCmd.ExecuteScalarAsync(cancellationToken);
            if (roleObj is not string r)
            {
                return (false, "ERR_MEMBERSHIP_NOT_FOUND", "Không tìm thấy thành viên hoạt động trong phòng khám.");
            }
            role = r;
        }

        if (string.Equals(role, ClinicMemberRoles.ClinicAdmin, StringComparison.OrdinalIgnoreCase))
        {
            const string countAdminSql = @"
                SELECT COUNT(*) FROM profile.memberships
                WHERE clinic_id = $1 AND role = 'CLINIC_ADMIN' AND status = 'ACTIVE';";

            await using var countCmd = new NpgsqlCommand(countAdminSql, connection);
            countCmd.Parameters.AddWithValue(clinicId);
            var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
            if (count <= 1)
            {
                return (false, "ERR_CANNOT_REVOKE_SOLE_ADMIN", "Không thể gỡ bỏ quản trị viên duy nhất của phòng khám.");
            }
        }

        const string revokeSql = @"
            UPDATE profile.memberships
            SET status = 'REVOKED', updated_at = NOW()
            WHERE clinic_id = $1 AND account_id = $2 AND status = 'ACTIVE';";

        await using (var revokeCmd = new NpgsqlCommand(revokeSql, connection))
        {
            revokeCmd.Parameters.AddWithValue(clinicId);
            revokeCmd.Parameters.AddWithValue(targetAccountId);
            var rowsAffected = await revokeCmd.ExecuteNonQueryAsync(cancellationToken);
            if (rowsAffected > 0)
            {
                return (true, null, null);
            }
        }

        return (false, "ERR_MEMBERSHIP_NOT_FOUND", "Không tìm thấy thành viên hoạt động trong phòng khám.");
    }

    // ====================================================================
    // DOCTOR ASSIGNMENT MANAGEMENT (Task W3-M6-02 / AC2)
    // ====================================================================

    public Task<(bool Success, DoctorAssignmentDto? Assignment, string? ErrorCode, string? ErrorMessage)> CreateOrReactivateDoctorAssignmentAsync(
        Guid clinicId, Guid doctorId, Guid patientId, CancellationToken cancellationToken = default)
        => CreateOrReactivateDoctorAssignmentAsync(clinicId, doctorId, patientId, null, null, null, cancellationToken);

    public async Task<(bool Success, DoctorAssignmentDto? Assignment, string? ErrorCode, string? ErrorMessage)> CreateOrReactivateDoctorAssignmentAsync(
        Guid clinicId, Guid doctorId, Guid patientId, Guid? analysisId, string? notes = null, Guid? transferredFromAssignmentId = null, CancellationToken cancellationToken = default, NpgsqlConnection? existingConnection = null, NpgsqlTransaction? existingTransaction = null)
    {
        await using var ownedConnection = existingConnection is null ? await _dataSource.OpenConnectionAsync(cancellationToken) : null;
        var connection = existingConnection ?? ownedConnection!;
        var account = await AccountAsync(doctorId, cancellationToken);
        if (account?.AccountStatus != "ACTIVE" || !account.Roles.Contains("DOCTOR", StringComparer.Ordinal))
            return (false, null, "ERR_INVALID_DOCTOR", "Global Doctor capability is required.");

        // 1. Kiểm tra Clinic tồn tại và ACTIVE
        const string clinicSql = "SELECT tenant_id FROM profile.clinics WHERE clinic_id = $1 AND status = 'ACTIVE' LIMIT 1;";
        Guid clinicTenantId;
        await using (var clinicCmd = new NpgsqlCommand(clinicSql, connection, existingTransaction))
        {
            clinicCmd.Parameters.AddWithValue(clinicId);
            var ctObj = await clinicCmd.ExecuteScalarAsync(cancellationToken);
            if (ctObj is not Guid cTId)
            {
                return (false, null, "ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.");
            }
            clinicTenantId = cTId;
        }

        // 2. Kiểm tra Bác sĩ có membership DOCTOR và ACTIVE tại clinic
        const string checkDoctorSql = @"
            SELECT m.account_id, COALESCE(p.display_name, 'Bác sĩ')
            FROM profile.memberships m
            LEFT JOIN profile.profiles p ON p.account_id = m.account_id
            WHERE m.clinic_id = $1 AND m.account_id = $2 AND m.role = 'DOCTOR' AND m.status = 'ACTIVE'
            LIMIT 1;";

        string doctorName;
        await using (var docCmd = new NpgsqlCommand(checkDoctorSql, connection, existingTransaction))
        {
            docCmd.Parameters.AddWithValue(clinicId);
            docCmd.Parameters.AddWithValue(doctorId);
            await using var reader = await docCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return (false, null, "ERR_INVALID_DOCTOR", "Bác sĩ không hợp lệ hoặc không có tư cách hành nghề tại phòng khám.");
            }
            doctorName = reader.GetString(1);
        }

        // 3. Kiểm tra Bệnh nhân phải thuộc phạm vi phòng khám
        const string checkPatientSql = @"
            SELECT patient_id FROM profile.patients
            WHERE patient_id = $1 AND (tenant_id = $2 OR owner_principal_id = $3) AND status = 'ACTIVE'
            LIMIT 1;";

        await using (var patientCmd = new NpgsqlCommand(checkPatientSql, connection, existingTransaction))
        {
            patientCmd.Parameters.AddWithValue(patientId);
            patientCmd.Parameters.AddWithValue(clinicTenantId);
            patientCmd.Parameters.AddWithValue(clinicId);
            var pObj = await patientCmd.ExecuteScalarAsync(cancellationToken);
            if (pObj is not Guid)
            {
                return (false, null, "ERR_PATIENT_NOT_IN_CLINIC", "Bệnh nhân không thuộc phạm vi quản lý của phòng khám.");
            }
        }

        if (analysisId is {} caseId)
        {
            var binding = await (_analysisBindingAuthority ?? throw new InvalidOperationException("Analysis binding authority not configured.")).GetAsync(caseId, cancellationToken);
            if (binding is null || binding.PatientId != patientId || binding.TenantId != clinicTenantId)
                return (false, null, "ERR_INVALID_ANALYSIS_BINDING", "Analysis does not belong to this clinic patient.");
        }

        // 4. Kiểm tra phân công đã tồn tại chưa
        string checkAssignSql;
        if (analysisId.HasValue)
        {
            checkAssignSql = @"
                SELECT assignment_id, status
                FROM profile.doctor_assignments
                WHERE clinic_id = $1 AND doctor_account_id = $2 AND analysis_id = $3
                LIMIT 1;";
        }
        else
        {
            checkAssignSql = @"
                SELECT assignment_id, status
                FROM profile.doctor_assignments
                WHERE clinic_id = $1 AND doctor_account_id = $2 AND patient_id = $3 AND analysis_id IS NULL
                LIMIT 1;";
        }

        Guid? existingAssignId = null;
        string? existingStatus = null;

        await using (var checkCmd = new NpgsqlCommand(checkAssignSql, connection, existingTransaction))
        {
            checkCmd.Parameters.AddWithValue(clinicId);
            checkCmd.Parameters.AddWithValue(doctorId);
            checkCmd.Parameters.AddWithValue(analysisId.HasValue ? analysisId.Value : patientId);
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingAssignId = reader.GetGuid(0);
                existingStatus = reader.GetString(1);
            }
        }

        if (existingAssignId.HasValue && string.Equals(existingStatus, DoctorAssignmentStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "ERR_ASSIGNMENT_ALREADY_EXISTS", "Phân công bác sĩ cho ca chụp/bệnh nhân này đã tồn tại và đang hoạt động.");
        }

        if (existingAssignId.HasValue)
        {
            // Tái kích hoạt phân công đã bị thu hồi trước đó
            const string reactivateSql = @"
                UPDATE profile.doctor_assignments
                SET status = 'ACTIVE', revoked_at = NULL, assigned_at = NOW(), updated_at = NOW(),
                    notes = COALESCE($2, notes), transferred_from_assignment_id = $3
                WHERE assignment_id = $1
                RETURNING assigned_at, created_at, updated_at;";

            await using var reactivateCmd = new NpgsqlCommand(reactivateSql, connection, existingTransaction);
            reactivateCmd.Parameters.AddWithValue(existingAssignId.Value);
            reactivateCmd.Parameters.AddWithValue((object?)notes ?? DBNull.Value);
            reactivateCmd.Parameters.AddWithValue((object?)transferredFromAssignmentId ?? DBNull.Value);

            await using var reader = await reactivateCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var assignedAt = reader.GetFieldValue<DateTimeOffset>(0);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(2);

                return (true, new DoctorAssignmentDto(
                    existingAssignId.Value, clinicId, doctorId, doctorName, patientId, DoctorAssignmentStatuses.Active,
                    assignedAt, null, createdAt, updatedAt, analysisId, transferredFromAssignmentId, notes), null, null);
            }
        }
        else
        {
            // Tạo mới phân công
            var newAssignId = Guid.NewGuid();
            const string insertSql = @"
                INSERT INTO profile.doctor_assignments (
                    assignment_id, clinic_id, doctor_account_id, patient_id, analysis_id,
                    transferred_from_assignment_id, notes, status, assigned_at, created_at, updated_at
                ) VALUES ($1, $2, $3, $4, $5, $6, $7, 'ACTIVE', NOW(), NOW(), NOW())
                RETURNING assigned_at, created_at, updated_at;";

            await using var insertCmd = new NpgsqlCommand(insertSql, connection, existingTransaction);
            insertCmd.Parameters.AddWithValue(newAssignId);
            insertCmd.Parameters.AddWithValue(clinicId);
            insertCmd.Parameters.AddWithValue(doctorId);
            insertCmd.Parameters.AddWithValue(patientId);
            insertCmd.Parameters.AddWithValue((object?)analysisId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue((object?)transferredFromAssignmentId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue((object?)notes ?? DBNull.Value);

            await using var reader = await insertCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var assignedAt = reader.GetFieldValue<DateTimeOffset>(0);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
                var updatedAt = reader.GetFieldValue<DateTimeOffset>(2);

                return (true, new DoctorAssignmentDto(
                    newAssignId, clinicId, doctorId, doctorName, patientId, DoctorAssignmentStatuses.Active,
                    assignedAt, null, createdAt, updatedAt, analysisId, transferredFromAssignmentId, notes), null, null);
            }
        }

        return (false, null, "ERR_INTERNAL_DB", "Không thể ghi nhận phân công bác sĩ.");
    }

    public async Task<(bool Success, IReadOnlyList<DoctorAssignmentDto>? Items, string? ErrorCode, string? ErrorMessage)> GetDoctorAssignmentsAsync(
        Guid clinicId, Guid actorAccountId, IReadOnlyList<string> actorRoles, Guid? doctorIdFilter, Guid? patientIdFilter, string? statusFilter, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 1. Kiểm tra Clinic tồn tại
        const string clinicSql = "SELECT tenant_id FROM profile.clinics WHERE clinic_id = $1 AND status = 'ACTIVE' LIMIT 1;";
        await using (var clinicCmd = new NpgsqlCommand(clinicSql, connection))
        {
            clinicCmd.Parameters.AddWithValue(clinicId);
            var ctObj = await clinicCmd.ExecuteScalarAsync(cancellationToken);
            if (ctObj is not Guid)
            {
                return (false, null, "ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.");
            }
        }

        var policy=await ResolveClinicAccessAsync(actorAccountId,clinicId,"ASSIGNMENT_READ",cancellationToken);
        if(!policy.Allowed)return(false,null,"ERR_FORBIDDEN","Assignment access denied.");
        Guid? effectiveDoctorId=doctorIdFilter;
        if(policy.Role=="DOCTOR")
        {
            if(doctorIdFilter.HasValue && doctorIdFilter!=actorAccountId)return(false,null,"ERR_FORBIDDEN_DOCTOR_ACCESS","Doctor can read only own assignments.");
            effectiveDoctorId=actorAccountId;
        }

        var queryStatus = string.IsNullOrWhiteSpace(statusFilter) || statusFilter.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? null
            : statusFilter.Trim().ToUpperInvariant();

        const string sql = @"
            SELECT a.assignment_id, a.clinic_id, a.doctor_account_id, COALESCE(p.display_name, 'Bác sĩ'),
                   a.patient_id, a.status, a.assigned_at, a.revoked_at, a.created_at, a.updated_at,
                   a.analysis_id, a.transferred_from_assignment_id, a.notes
            FROM profile.doctor_assignments a
            LEFT JOIN profile.profiles p ON p.account_id = a.doctor_account_id
            WHERE a.clinic_id = $1
              AND ($2::uuid IS NULL OR a.doctor_account_id = $2)
              AND ($3::uuid IS NULL OR a.patient_id = $3)
              AND ($4::text IS NULL OR a.status = $4)
            ORDER BY a.assigned_at DESC;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.AddWithValue((object?)effectiveDoctorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)patientIdFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)queryStatus ?? DBNull.Value);

        var list = new List<DoctorAssignmentDto>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var assignmentId = reader.GetGuid(0);
            var cId = reader.GetGuid(1);
            var docId = reader.GetGuid(2);
            var doctorName = reader.GetString(3);
            var pId = reader.GetGuid(4);
            var status = reader.GetString(5);
            var assignedAt = reader.GetFieldValue<DateTimeOffset>(6);
            var revokedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(8);
            var updatedAt = reader.GetFieldValue<DateTimeOffset>(9);

            list.Add(new DoctorAssignmentDto(
                assignmentId, cId, docId, doctorName, pId, status, assignedAt, revokedAt, createdAt, updatedAt,
                reader.IsDBNull(10) ? null : reader.GetGuid(10), reader.IsDBNull(11) ? null : reader.GetGuid(11), reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return (true, list, null, null);
    }

    public async Task<(bool Success, string? ErrorCode, string? ErrorMessage)> RevokeDoctorAssignmentAsync(
        Guid clinicId, Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string checkSql = @"
            SELECT status FROM profile.doctor_assignments
            WHERE assignment_id = $1 AND clinic_id = $2
            LIMIT 1;";

        string status;
        await using (var checkCmd = new NpgsqlCommand(checkSql, connection))
        {
            checkCmd.Parameters.AddWithValue(assignmentId);
            checkCmd.Parameters.AddWithValue(clinicId);
            var statusObj = await checkCmd.ExecuteScalarAsync(cancellationToken);
            if (statusObj is not string s)
            {
                return (false, "ERR_ASSIGNMENT_NOT_FOUND", "Không tìm thấy phân công trong phòng khám.");
            }
            status = s;
        }

        if (!string.Equals(status, DoctorAssignmentStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ERR_ASSIGNMENT_NOT_ACTIVE", "Phân công đã ở trạng thái thu hồi hoặc không còn hoạt động.");
        }

        const string revokeSql = @"
            UPDATE profile.doctor_assignments
            SET status = 'REVOKED', revoked_at = NOW(), updated_at = NOW()
            WHERE assignment_id = $1 AND clinic_id = $2 AND status = 'ACTIVE';";

        await using var revokeCmd = new NpgsqlCommand(revokeSql, connection);
        revokeCmd.Parameters.AddWithValue(assignmentId);
        revokeCmd.Parameters.AddWithValue(clinicId);
        var rows = await revokeCmd.ExecuteNonQueryAsync(cancellationToken);
        if (rows > 0)
        {
            return (true, null, null);
        }

        return (false, "ERR_ASSIGNMENT_NOT_FOUND", "Không tìm thấy phân công trong phòng khám.");
    }

    public async Task<(bool Success, IReadOnlyList<DoctorAssignmentDto>? Assignments, string? ErrorCode, string? ErrorMessage)> BatchAssignDoctorAsync(
        Guid clinicId, Guid doctorId, IReadOnlyList<Guid> patientIds, string? notes = null, CancellationToken cancellationToken = default)
    {
        if (patientIds == null || patientIds.Count == 0)
        {
            return (false, null, "ERR_VALIDATION", "Danh sách bệnh nhân không được để trống.");
        }

        var results = new List<DoctorAssignmentDto>();
        foreach (var pId in patientIds)
        {
            var (success, dto, errCode, errMsg) = await CreateOrReactivateDoctorAssignmentAsync(
                clinicId, doctorId, pId, null, notes, null, cancellationToken);
            if (!success)
            {
                // Bỏ qua lỗi trùng phân công, tiếp tục phân công các bệnh nhân khác
                if (errCode == "ERR_ASSIGNMENT_ALREADY_EXISTS") continue;
                return (false, null, errCode, errMsg);
            }
            if (dto != null) results.Add(dto);
        }

        return (true, results, null, null);
    }

    public async Task<(bool Success, DoctorAssignmentDto? NewAssignment, string? ErrorCode, string? ErrorMessage)> TransferDoctorAssignmentAsync(
        Guid clinicId, Guid assignmentId, Guid newDoctorId, string? notes = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Tìm thông tin phân công hiện tại
        const string findSql = @"
            SELECT patient_id, analysis_id, status, doctor_account_id
            FROM profile.doctor_assignments
            WHERE clinic_id = $1 AND assignment_id = $2
            LIMIT 1 FOR UPDATE;";

        Guid patientId;
        Guid? analysisId = null;
        string status;
        Guid currentDoctorId;

        await using (var cmd = new NpgsqlCommand(findSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue(clinicId);
            cmd.Parameters.AddWithValue(assignmentId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return (false, null, "ERR_ASSIGNMENT_NOT_FOUND", "Không tìm thấy phân công chỉ định.");
            }
            patientId = reader.GetGuid(0);
            analysisId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
            status = reader.GetString(2);
            currentDoctorId = reader.GetGuid(3);
        }

        if (!string.Equals(status, DoctorAssignmentStatuses.Active, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "ERR_ASSIGNMENT_NOT_ACTIVE", "Phân công không ở trạng thái ACTIVE nên không thể chuyển giao.");
        }

        if (currentDoctorId == newDoctorId)
        {
            return (false, null, "ERR_SAME_DOCTOR", "Bác sĩ mới phải khác bác sĩ hiện tại được phân công.");
        }

        // 2. Thu hồi phân công cũ (status = REVOKED)
        const string revokeSql = @"
            UPDATE profile.doctor_assignments
            SET status = 'REVOKED', revoked_at = NOW(), updated_at = NOW()
            WHERE assignment_id = $1;";
        await using (var rCmd = new NpgsqlCommand(revokeSql, connection, transaction))
        {
            rCmd.Parameters.AddWithValue(assignmentId);
            await rCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3. Tạo phân công mới cho Bác sĩ mới (ghi nhận transferred_from_assignment_id)
        var result = await CreateOrReactivateDoctorAssignmentAsync(
            clinicId, newDoctorId, patientId, analysisId, notes, assignmentId, cancellationToken, connection, transaction);
        if (result.Success) await transaction.CommitAsync(cancellationToken);
        else await transaction.RollbackAsync(cancellationToken);
        return result;
    }

    // ====================================================================
    // CLINIC PATIENT REGISTRY (Task W4-M6-02 / AC1)
    // ====================================================================

    public async Task<(bool Success, ClinicPatientDto? Patient, string? ErrorCode, string? ErrorMessage)> RegisterClinicPatientAsync(
        Guid clinicId, RegisterClinicPatientRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return (false, null, "ERR_VALIDATION", "Họ và tên bệnh nhân không được để trống.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 1. Xác định tenant_id của phòng khám
        const string clinicSql = "SELECT tenant_id FROM profile.clinics WHERE clinic_id = $1 AND status = 'ACTIVE' LIMIT 1;";
        Guid tenantId;
        await using (var cCmd = new NpgsqlCommand(clinicSql, connection))
        {
            cCmd.Parameters.AddWithValue(clinicId);
            var tObj = await cCmd.ExecuteScalarAsync(cancellationToken);
            if (tObj is not Guid tId)
            {
                return (false, null, "ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám hoạt động.");
            }
            tenantId = tId;
        }

        var patientId = Guid.NewGuid();
        var medicalHistoryJson = request.MedicalHistory != null
            ? JsonSerializer.Serialize(request.MedicalHistory)
            : null;

        const string insertSql = @"
            INSERT INTO profile.patients (
                patient_id, tenant_id, linked_account_id, owner_principal_id,
                patient_type, full_name, date_of_birth, gender, phone,
                health_insurance_number, medical_history, status, created_at, updated_at
            ) VALUES (
                $1, $2, NULL, $3,
                'CLINIC_MANAGED', $4, $5, $6, $7,
                $8, $9::jsonb, 'ACTIVE', NOW(), NOW()
            )
            RETURNING created_at, updated_at;";

        await using var insertCmd = new NpgsqlCommand(insertSql, connection);
        insertCmd.Parameters.AddWithValue(patientId);
        insertCmd.Parameters.AddWithValue(tenantId);
        insertCmd.Parameters.AddWithValue(clinicId);
        insertCmd.Parameters.AddWithValue(request.FullName.Trim());
        insertCmd.Parameters.AddWithValue((object?)request.DateOfBirth ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue((object?)request.Gender?.Trim() ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue((object?)request.Phone?.Trim() ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue((object?)request.HealthInsuranceNumber?.Trim() ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue((object?)medicalHistoryJson ?? DBNull.Value);

        await using var reader = await insertCmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var createdAt = reader.GetFieldValue<DateTimeOffset>(0);
            var updatedAt = reader.GetFieldValue<DateTimeOffset>(1);

            return (true, new ClinicPatientDto(
                patientId, clinicId, tenantId, PatientTypes.ClinicManaged, request.FullName.Trim(),
                request.DateOfBirth, request.Gender?.Trim(), request.Phone?.Trim(),
                request.HealthInsuranceNumber?.Trim(), request.MedicalHistory, "ACTIVE",
                createdAt, updatedAt), null, null);
        }

        return (false, null, "ERR_INTERNAL_DB", "Không thể lưu hồ sơ bệnh nhân.");
    }

    public async Task<(bool Success, ClinicPatientListResponse? Response, string? ErrorCode, string? ErrorMessage)> GetClinicPatientsAsync(
        Guid clinicId, string? search, string? status, int limit, int offset, CancellationToken cancellationToken = default, Guid? doctorFilter = null)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string checkSql = "SELECT tenant_id FROM profile.clinics WHERE clinic_id = $1 LIMIT 1;";
        await using (var cCmd = new NpgsqlCommand(checkSql, connection))
        {
            cCmd.Parameters.AddWithValue(clinicId);
            var obj = await cCmd.ExecuteScalarAsync(cancellationToken);
            if (obj is not Guid)
            {
                return (false, null, "ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.");
            }
        }

        var filterStatus = string.IsNullOrWhiteSpace(status) || status.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? null
            : status.Trim().ToUpperInvariant();

        var searchTrim = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        // Đếm tổng số bản ghi
        const string countSql = @"
            SELECT COUNT(*) FROM profile.patients
            WHERE owner_principal_id = $1
              AND ($4::uuid IS NULL OR EXISTS(SELECT 1 FROM profile.doctor_assignments da WHERE da.clinic_id=$1 AND da.doctor_account_id=$4 AND da.patient_id=profile.patients.patient_id AND da.analysis_id IS NULL AND da.status='ACTIVE'))
              AND ($2::text IS NULL OR status = $2)
              AND ($3::text IS NULL OR full_name ILIKE ('%' || $3 || '%') OR health_insurance_number ILIKE ('%' || $3 || '%'));";

        long totalCount = 0;
        await using (var countCmd = new NpgsqlCommand(countSql, connection))
        {
            countCmd.Parameters.AddWithValue(clinicId);
            countCmd.Parameters.AddWithValue((object?)filterStatus ?? DBNull.Value);
            countCmd.Parameters.AddWithValue((object?)searchTrim ?? DBNull.Value);
            countCmd.Parameters.AddWithValue((object?)doctorFilter ?? DBNull.Value);
            totalCount = (long)(await countCmd.ExecuteScalarAsync(cancellationToken))!;
        }

        const string listSql = @"
            SELECT patient_id, clinic_id_val, tenant_id, patient_type, full_name,
                   date_of_birth, gender, phone, health_insurance_number, medical_history,
                   status, created_at, updated_at
            FROM (
                SELECT patient_id, owner_principal_id as clinic_id_val, tenant_id, patient_type, full_name,
                       date_of_birth, gender, phone, health_insurance_number, medical_history,
                       status, created_at, updated_at
                FROM profile.patients
                WHERE owner_principal_id = $1
                  AND ($6::uuid IS NULL OR EXISTS(SELECT 1 FROM profile.doctor_assignments da WHERE da.clinic_id=$1 AND da.doctor_account_id=$6 AND da.patient_id=profile.patients.patient_id AND da.analysis_id IS NULL AND da.status='ACTIVE'))
                  AND ($2::text IS NULL OR status = $2)
                  AND ($3::text IS NULL OR full_name ILIKE ('%' || $3 || '%') OR health_insurance_number ILIKE ('%' || $3 || '%'))
                ORDER BY created_at DESC
                LIMIT $4 OFFSET $5
            ) sub;";

        var list = new List<ClinicPatientDto>();
        await using (var listCmd = new NpgsqlCommand(listSql, connection))
        {
            listCmd.Parameters.AddWithValue(clinicId);
            listCmd.Parameters.AddWithValue((object?)filterStatus ?? DBNull.Value);
            listCmd.Parameters.AddWithValue((object?)searchTrim ?? DBNull.Value);
            listCmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 100));
            listCmd.Parameters.AddWithValue(Math.Max(0, offset));
            listCmd.Parameters.AddWithValue((object?)doctorFilter ?? DBNull.Value);

            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pId = reader.GetGuid(0);
                var cId = reader.GetGuid(1);
                var tId = reader.GetGuid(2);
                var pType = reader.GetString(3);
                var fn = reader.IsDBNull(4) ? null : reader.GetString(4);
                var dob = reader.IsDBNull(5) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(5);
                var gen = reader.IsDBNull(6) ? null : reader.GetString(6);
                var ph = reader.IsDBNull(7) ? null : reader.GetString(7);
                var ins = reader.IsDBNull(8) ? null : reader.GetString(8);
                var medStr = reader.IsDBNull(9) ? null : reader.GetString(9);
                var st = reader.GetString(10);
                var ca = reader.GetFieldValue<DateTimeOffset>(11);
                var ua = reader.GetFieldValue<DateTimeOffset>(12);

                ClinicPatientMedicalHistoryDto? med = null;
                if (!string.IsNullOrEmpty(medStr))
                {
                    try { med = JsonSerializer.Deserialize<ClinicPatientMedicalHistoryDto>(medStr); } catch { }
                }

                list.Add(new ClinicPatientDto(
                    pId, cId, tId, pType, fn, dob, gen, ph, ins, med, st, ca, ua));
            }
        }

        return (true, new ClinicPatientListResponse(clinicId, list, (int)totalCount), null, null);
    }

    public async Task<(bool Success, ClinicPatientDto? Patient, string? ErrorCode, string? ErrorMessage)> GetClinicPatientByIdAsync(
        Guid clinicId, Guid patientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = @"
            SELECT patient_id, owner_principal_id, tenant_id, patient_type, full_name,
                   date_of_birth, gender, phone, health_insurance_number, medical_history,
                   status, created_at, updated_at
            FROM profile.patients
            WHERE owner_principal_id = $1 AND patient_id = $2
            LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.AddWithValue(patientId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var pId = reader.GetGuid(0);
            var cId = reader.GetGuid(1);
            var tId = reader.GetGuid(2);
            var pType = reader.GetString(3);
            var fn = reader.IsDBNull(4) ? null : reader.GetString(4);
            var dob = reader.IsDBNull(5) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(5);
            var gen = reader.IsDBNull(6) ? null : reader.GetString(6);
            var ph = reader.IsDBNull(7) ? null : reader.GetString(7);
            var ins = reader.IsDBNull(8) ? null : reader.GetString(8);
            var medStr = reader.IsDBNull(9) ? null : reader.GetString(9);
            var st = reader.GetString(10);
            var ca = reader.GetFieldValue<DateTimeOffset>(11);
            var ua = reader.GetFieldValue<DateTimeOffset>(12);

            ClinicPatientMedicalHistoryDto? med = null;
            if (!string.IsNullOrEmpty(medStr))
            {
                try { med = JsonSerializer.Deserialize<ClinicPatientMedicalHistoryDto>(medStr); } catch { }
            }

            return (true, new ClinicPatientDto(
                pId, cId, tId, pType, fn, dob, gen, ph, ins, med, st, ca, ua), null, null);
        }

        return (false, null, "ERR_PATIENT_NOT_FOUND", "Không tìm thấy hồ sơ bệnh nhân trong phòng khám.");
    }

    public async Task<(bool Success, ClinicPatientDto? Patient, string? ErrorCode, string? ErrorMessage)> UpdateClinicPatientAsync(
        Guid clinicId, Guid patientId, UpdateClinicPatientRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        var (found, existing, errCode, errMsg) = await GetClinicPatientByIdAsync(clinicId, patientId, cancellationToken);
        if (!found || existing == null)
        {
            return (false, null, errCode ?? "ERR_PATIENT_NOT_FOUND", errMsg ?? "Không tìm thấy bệnh nhân.");
        }

        var newFullName = request.FullName?.Trim() ?? existing.FullName;
        var newDob = request.DateOfBirth ?? existing.DateOfBirth;
        var newGender = request.Gender?.Trim() ?? existing.Gender;
        var newPhone = request.Phone?.Trim() ?? existing.Phone;
        var newInsurance = request.HealthInsuranceNumber?.Trim() ?? existing.HealthInsuranceNumber;
        var newMedHistory = request.MedicalHistory ?? existing.MedicalHistory;
        var medJson = newMedHistory != null ? JsonSerializer.Serialize(newMedHistory) : null;

        const string updateSql = @"
            UPDATE profile.patients
            SET full_name = $3, date_of_birth = $4, gender = $5, phone = $6,
                health_insurance_number = $7, medical_history = $8::jsonb, updated_at = NOW()
            WHERE owner_principal_id = $1 AND patient_id = $2
            RETURNING updated_at;";

        await using var cmd = new NpgsqlCommand(updateSql, connection);
        cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue((object?)newFullName ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)newDob ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)newGender ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)newPhone ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)newInsurance ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)medJson ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var ua = reader.GetFieldValue<DateTimeOffset>(0);
            return (true, new ClinicPatientDto(
                patientId, clinicId, existing.TenantId, existing.PatientType, newFullName,
                newDob, newGender, newPhone, newInsurance, newMedHistory, existing.Status,
                existing.CreatedAt, ua), null, null);
        }

        return (false, null, "ERR_INTERNAL_DB", "Không thể cập nhật hồ sơ bệnh nhân.");
    }


    /// <summary>
    /// ADR-005 D2: Chấp nhận lời mời tham gia phòng khám rõ ràng (Explicit Acceptance).
    /// Kiểm tra token/lời mời trong hạn 72 giờ và email trùng khớp với tài khoản được xác nhận.
    /// </summary>
    public async Task<(bool Succeeded, string? ErrorCode, ClinicMembershipDto? Result)> AcceptClinicInvitationExplicitAsync(
        Guid accountId, string? email, Guid membershipId, string token, CancellationToken cancellationToken = default)
    {
        if(string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token) || token.Length>256)return(false,"ERR_INVALID_INVITATION",null);
        await using var connection=await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction=await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd=new NpgsqlCommand("""
            SELECT m.clinic_id,m.account_id,m.role,m.status,m.invited_email,m.created_at,c.status,
                   i.token_hash,i.expires_at,i.consumed_at
            FROM profile.memberships m JOIN profile.clinics c ON c.clinic_id=m.clinic_id
            JOIN profile.clinic_invitations i ON i.membership_id=m.membership_id
            WHERE m.membership_id=$1 FOR UPDATE OF m,i
            """,connection,transaction);
        cmd.Parameters.AddWithValue(membershipId);
        Guid clinicId;string role;DateTimeOffset createdAt;
        await using(var reader=await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if(!await reader.ReadAsync(cancellationToken))return(false,"ERR_INVITATION_NOT_FOUND",null);
            clinicId=reader.GetGuid(0);var linked=reader.IsDBNull(1)?(Guid?)null:reader.GetGuid(1);
            role=reader.GetString(2);createdAt=reader.GetFieldValue<DateTimeOffset>(5);
            if(reader.GetString(6)!="ACTIVE")return(false,"ERR_CLINIC_SUSPENDED",null);
            if(reader.GetString(3)!="INVITED" || !reader.IsDBNull(9))return(false,"ERR_INVITATION_REVOKED",null);
            if(reader.GetFieldValue<DateTimeOffset>(8)<=DateTimeOffset.UtcNow)return(false,"ERR_INVITATION_EXPIRED",null);
            if(linked.HasValue && linked!=accountId || !string.Equals(reader.GetString(4),email.Trim(),StringComparison.OrdinalIgnoreCase))return(false,"ERR_EMAIL_MISMATCH",null);
            if(!CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(7),SHA256.HashData(Encoding.UTF8.GetBytes(token))))return(false,"ERR_INVALID_INVITATION",null);
        }
        await using var update=new NpgsqlCommand("""
            UPDATE profile.memberships SET account_id=$1,status='ACTIVE',updated_at=now() WHERE membership_id=$2;
            """,connection,transaction);
        update.Parameters.AddWithValue(accountId);update.Parameters.AddWithValue(membershipId);await update.ExecuteNonQueryAsync(cancellationToken);
        await using var consume=new NpgsqlCommand("UPDATE profile.clinic_invitations SET consumed_at=now() WHERE membership_id=$1",connection,transaction);
        consume.Parameters.AddWithValue(membershipId);await consume.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return(true,null,new ClinicMembershipDto(membershipId,clinicId,accountId,null,role,"ACTIVE",createdAt,DateTimeOffset.UtcNow,email));
    }
}
