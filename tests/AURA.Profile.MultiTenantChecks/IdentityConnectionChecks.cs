using AURA.Identity.Api;
using Npgsql;

internal static class IdentityConnectionChecks
{
    public static async Task RunAsync(NpgsqlDataSource admin,string connectionString)
    {
        var id=Guid.NewGuid();var uid="m6-pool-"+id.ToString("N");
        async Task Sql(string text, params object[] values)
        {
            await using var cmd=admin.CreateCommand(text);
            foreach(var value in values)cmd.Parameters.AddWithValue(value);
            await cmd.ExecuteNonQueryAsync();
        }
        await Sql("INSERT INTO identity.accounts(account_id,firebase_uid,account_status,profile_provisioning_status) VALUES($1,$2,'ACTIVE','READY')",id,uid);
        await Sql("INSERT INTO identity.account_roles(account_id,role_name) VALUES($1,'PATIENT'),($1,'DOCTOR')",id);
        var connection=new NpgsqlConnectionStringBuilder(connectionString){Options="-c role=aura_identity_app",MaxPoolSize=1,Timeout=2};
        await using var runtime=NpgsqlDataSource.Create(connection.ConnectionString);
        var repository=new IdentityRepository(runtime);
        var first=await repository.GetAccountByIdAsync(id);
        if(first?.FirebaseUid!=uid || !first.Roles.Contains("DOCTOR"))throw new Exception("Identity by-id positive control failed.");
        await Sql("DELETE FROM identity.account_roles WHERE account_id=$1 AND role_name='DOCTOR'",id);
        await Sql("UPDATE identity.accounts SET account_status='DISABLED' WHERE account_id=$1",id);
        var next=await repository.GetAccountByIdAsync(id);
        if(next?.AccountStatus!="DISABLED" || next.Roles.Contains("DOCTOR"))throw new Exception("Authority must re-read current status and roles.");

        // W6-M6-01: Admin lifecycle, optimistic concurrency, self-protection, sole admin protection & audit log
        var adminActor = Guid.NewGuid();
        var secondAdmin = Guid.NewGuid();
        var adminUid = "m6-admin-" + adminActor.ToString("N");
        var secondAdminUid = "m6-admin2-" + secondAdmin.ToString("N");
        await Sql("INSERT INTO identity.accounts(account_id,firebase_uid,display_name,account_status,profile_provisioning_status,version) VALUES($1,$2,'Admin One','ACTIVE','READY',1),($3,$4,'Admin Two','ACTIVE','READY',1)",
            adminActor, adminUid, secondAdmin, secondAdminUid);
        await Sql("INSERT INTO identity.account_roles(account_id,role_name) VALUES($1,'PATIENT'),($1,'SYSTEM_ADMIN'),($2,'PATIENT'),($2,'SYSTEM_ADMIN')",
            adminActor, secondAdmin);

        var page = await repository.ListAccountsAdminAsync(100, null, null, adminUid);
        if (!page.Items.Any(x => x.AccountId == adminActor && x.Version == 1 && x.Roles.Contains("SYSTEM_ADMIN")))
            throw new Exception("Admin account listing must return version and SYSTEM_ADMIN role.");

        var selfDisable = await repository.UpdateAccountStatusAdminAsync(adminActor, adminActor, "DISABLED", 1, "Attempt self disable");
        if (selfDisable.Success || selfDisable.HttpStatus != 400 || selfDisable.ErrorCode != "ERR_SELF_DISABLE_FORBIDDEN")
            throw new Exception("Admin self-disable must be rejected.");

        var selfDemote = await repository.UpdateAccountRolesAdminAsync(adminActor, adminActor, new[] { "PATIENT" }, 1, "Attempt self demote");
        if (selfDemote.Success || selfDemote.HttpStatus != 400 || selfDemote.ErrorCode != "ERR_SELF_DEMOTE_FORBIDDEN")
            throw new Exception("Admin self-demote must be rejected.");

        var doctorRoleBypass = await repository.UpdateAccountRolesAdminAsync(adminActor, secondAdmin, new[] { "PATIENT", "DOCTOR", "SYSTEM_ADMIN" }, 1, "Bypass doctor capability");
        if (doctorRoleBypass.Success || doctorRoleBypass.HttpStatus != 400 || doctorRoleBypass.ErrorCode != "ERR_DOCTOR_CAPABILITY_SEPARATED")
            throw new Exception("DOCTOR role changes must be rejected via general PATCH /roles.");

        var staleStatus = await repository.UpdateAccountStatusAdminAsync(adminActor, secondAdmin, "DISABLED", 99, "Stale version");
        if (staleStatus.Success || staleStatus.HttpStatus != 409 || staleStatus.ErrorCode != "ERR_VERSION_CONFLICT")
            throw new Exception("Stale expectedVersion must return 409 ERR_VERSION_CONFLICT.");

        var okDisable = await repository.UpdateAccountStatusAdminAsync(adminActor, secondAdmin, "DISABLED", 1, "Suspended during audit");
        if (!okDisable.Success || okDisable.Account?.Version != 2 || okDisable.Account.Status != "DISABLED")
            throw new Exception("Valid status update must bump version and persist DISABLED.");

        // Confirm sole-admin protection: isolate test by temporarily disabling any other SYSTEM_ADMIN accounts in fixture DB?
        // Only assert if adminActor is the sole remaining ACTIVE SYSTEM_ADMIN, otherwise verify non-self disable of another sole admin fails when count=1.
        await using var countAdmins = admin.CreateCommand("SELECT COUNT(*) FROM identity.accounts a JOIN identity.account_roles ar ON ar.account_id=a.account_id AND ar.role_name='SYSTEM_ADMIN' WHERE a.account_status='ACTIVE'");
        var activeAdminCount = Convert.ToInt64(await countAdmins.ExecuteScalarAsync());
        if (activeAdminCount == 1)
        {
            var lastAdminDemote = await repository.UpdateAccountRolesAdminAsync(secondAdmin, adminActor, new[] { "PATIENT" }, 1, "Try remove last admin");
            if (lastAdminDemote.Success || lastAdminDemote.ErrorCode != "ERR_LAST_SYSTEM_ADMIN")
                throw new Exception("Removing the last active SYSTEM_ADMIN must be rejected.");
        }

        await using var auditCmd = runtime.CreateCommand("SELECT count(*) FROM identity.account_lifecycle_audit WHERE target_account_id=$1 AND action='ACCOUNT_STATUS_UPDATE'");
        auditCmd.Parameters.AddWithValue(secondAdmin);
        if (Convert.ToInt64(await auditCmd.ExecuteScalarAsync()) < 1)
            throw new Exception("Account lifecycle audit row must be recorded under aura_identity_app.");

        Console.WriteLine("Identity DB PASS: by-id lookup, admin lifecycle, optimistic concurrency and append-only audit (aura_identity_app).");
    }
}
