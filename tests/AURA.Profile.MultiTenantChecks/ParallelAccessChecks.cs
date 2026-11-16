using AURA.Profile.Api;
using Npgsql;

internal static class ParallelAccessChecks
{
    public static async Task RunAsync(NpgsqlDataSource admin, string connectionString, Guid actor, Guid clinic, Guid patient)
    {
        var name = "m6-parallel-" + Guid.NewGuid().ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
            { Options = "-c role=aura_profile_app", ApplicationName = name, MaxPoolSize = 1 };
        await using var runtime = NpgsqlDataSource.Create(builder.ConnectionString);
        var gate = new Gate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var pending = new ProfileRepository(runtime, gate).ResolveAccessAsync(actor, patient, clinic, "ANALYSIS_READ", timeout.Token);
        try
        {
            var observed = false;
            for (var i = 0; i < 100; i++)
            {
                await using var query = admin.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND state='idle' AND query LIKE '%FROM profile.patients p%')");
                query.Parameters.AddWithValue(name);
                if ((bool)(await query.ExecuteScalarAsync(timeout.Token))!) { observed = true; break; }
                await Task.Delay(20, timeout.Token);
            }
            if (!observed) throw new Exception("Patient scope must be read while independent Identity authority is pending.");
            if (pending.IsCompleted) throw new Exception("Patient scope alone must never grant access before Identity confirms.");
        }
        finally { gate.Complete(new(actor, "ACTIVE", new[] { "PATIENT" })); }
        if (!(await pending).Allowed) throw new Exception("Both current authorities should permit the clinic owner.");
        var disabled = new Gate();
        disabled.Complete(new(actor, "DISABLED", new[] { "PATIENT" }));
        if ((await new ProfileRepository(runtime, disabled).ResolveAccessAsync(actor, patient, clinic, "ANALYSIS_READ")).Allowed)
            throw new Exception("Parallel scope read cannot bypass disabled account.");
        Console.WriteLine("DB PASS: independent Identity/Profile reads overlap, both required; disabled account still denied.");
    }
    private sealed class Gate : IAccountAuthority
    {
        private readonly TaskCompletionSource<AccountAuthoritySnapshot?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AccountAuthoritySnapshot?> GetAsync(Guid id, CancellationToken ct) => result.Task.WaitAsync(ct);
        public void Complete(AccountAuthoritySnapshot value) => result.TrySetResult(value);
    }
}
