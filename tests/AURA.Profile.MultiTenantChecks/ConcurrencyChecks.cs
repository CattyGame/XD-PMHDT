using AURA.Profile.Api;
using Npgsql;

internal static class ConcurrencyChecks
{
    public static async Task RunAsync(NpgsqlDataSource admin, string connectionString, Guid clinic, Guid owner, Guid secondAdmin)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var application = "profile-revoke-" + Guid.NewGuid().ToString("N");
        await using var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
            { Options = "-c role=aura_profile_app", ApplicationName = application }.ConnectionString);
        var repository = new ProfileRepository(runtime);
        await using (var seed = admin.CreateCommand("INSERT INTO profile.memberships(clinic_id,account_id,role,status) VALUES($1,$2,'CLINIC_ADMIN','ACTIVE')"))
        {
            seed.Parameters.AddWithValue(clinic); seed.Parameters.AddWithValue(secondAdmin);
            await seed.ExecuteNonQueryAsync(ct);
        }
        await using var hold = await admin.OpenConnectionAsync(ct);
        await using var tx = await hold.BeginTransactionAsync(ct);
        await new NpgsqlCommand("LOCK TABLE profile.memberships IN SHARE MODE", hold, tx).ExecuteNonQueryAsync(ct);
        var first = repository.RevokeClinicMemberAsync(clinic, owner, ct);
        var second = repository.RevokeClinicMemberAsync(clinic, secondAdmin, ct);
        await using var blocked = admin.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock'");
        blocked.Parameters.AddWithValue(application);
        while (Convert.ToInt32(await blocked.ExecuteScalarAsync(ct)) < 2) await Task.Delay(20, ct);
        await tx.CommitAsync(ct);
        var results = await Task.WhenAll(first, second);
        if (results.Count(x => x.Success) != 1 || results.Count(x => x.ErrorCode == "ERR_CANNOT_REVOKE_SOLE_ADMIN") != 1)
            throw new InvalidOperationException("Concurrent revocation must preserve the last active clinic admin.");
        await using var remaining = admin.CreateCommand("SELECT count(*) FROM profile.memberships WHERE clinic_id=$1 AND role='CLINIC_ADMIN' AND status='ACTIVE'");
        remaining.Parameters.AddWithValue(clinic);
        if (Convert.ToInt32(await remaining.ExecuteScalarAsync(ct)) != 1) throw new InvalidOperationException("Last admin lost.");
        Console.WriteLine("DB PASS: Concurrent admin revocations preserve exactly one active admin (aura_profile_app).");
    }
}
