using Npgsql;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AURA.M1.RegressionTests;

public sealed class M1PostgresFactAttribute : FactAttribute
{
    public M1PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES")))
            Skip = "Set AURA_TEST_POSTGRES to an isolated migrated aura_dev instance.";
    }
}

sealed class Database : IAsyncDisposable
{
    public NpgsqlDataSource Admin { get; }
    public NpgsqlDataSource Notification { get; }
    public NpgsqlDataSource Billing { get; }
    private readonly NpgsqlDataSource _cluster;
    private readonly string _name;

    private Database(NpgsqlDataSource cluster, string name, NpgsqlConnectionStringBuilder connection)
    {
        _cluster=cluster; _name=name; connection.Database=name;
        Admin=NpgsqlDataSource.Create(connection.ConnectionString);
        connection.Options="-c role=aura_notification_app"; Notification=NpgsqlDataSource.Create(connection.ConnectionString);
        connection.Options="-c role=aura_billing_app"; Billing=NpgsqlDataSource.Create(connection.ConnectionString);
    }
    public static async Task<Database> CreateAsync()
    {
        var cs=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES")!);
        if (cs.Host is not ("127.0.0.1" or "localhost") || cs.Database != "aura_dev")
            throw new InvalidOperationException("M1 tests require an isolated migrated aura_dev on loopback.");
        cs.Database="postgres"; cs.Options=null;
        var cluster=NpgsqlDataSource.Create(cs.ConnectionString);
        var name="aura_m1_test_"+Guid.NewGuid().ToString("N");
        await using var cmd=cluster.CreateCommand($"CREATE DATABASE {name} TEMPLATE aura_dev;");
        await cmd.ExecuteNonQueryAsync();
        var database=new Database(cluster,name,cs);
        try
        {
            // Earlier suites leave fixtures in the template; reset only this uniquely owned clone.
            await database.SqlAsync("TRUNCATE billing.orders, billing.credit_wallets, notification.inbox_items CASCADE;");
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
    public async Task SqlAsync(string sql, params object[] args)
    {
        await using var cmd=Admin.CreateCommand(sql);
        foreach(var arg in args) cmd.Parameters.AddWithValue(arg);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd=Admin.CreateCommand(sql);
        foreach(var arg in args)cmd.Parameters.AddWithValue(arg);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
    public async ValueTask DisposeAsync()
    {
        await Notification.DisposeAsync();await Billing.DisposeAsync();await Admin.DisposeAsync();
        await using var cmd=_cluster.CreateCommand($"DROP DATABASE {_name} WITH (FORCE);");
        await cmd.ExecuteNonQueryAsync();await _cluster.DisposeAsync();
    }
}
