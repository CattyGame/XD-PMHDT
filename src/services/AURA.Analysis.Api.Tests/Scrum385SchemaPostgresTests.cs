using Npgsql;
using AURA.Analysis.Api.Exports;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class Scrum385SchemaPostgresTests
{
    [PostgresFact]
    public async Task Migration_creates_the_three_analysis_owned_scrum385_tables()
    {
        await using var db = NpgsqlDataSource.Create(
            Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!);
        await using var command = db.CreateCommand("""
            SELECT to_regclass('analysis.alerts') IS NOT NULL,
                   to_regclass('analysis.alert_history') IS NOT NULL,
                   to_regclass('analysis.feedback') IS NOT NULL,
                   to_regclass('analysis.export_snapshots') IS NOT NULL;
            """);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((true, true, true, true),
            (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
    }

    [PostgresFact]
    public async Task Export_snapshot_round_trips_scope_and_idempotency_metadata()
    {
        await using var db=NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!);
        var repository=new PostgresExportRepository(db);var tenant=Guid.NewGuid();var clinic=Guid.NewGuid();var actor=Guid.NewGuid();
        var scope=new[]{Guid.NewGuid(),Guid.NewGuid()};var id=Guid.NewGuid();var key="scrum385-"+Guid.NewGuid().ToString("N");
        var item=new ExportSnapshot(id,tenant,clinic,actor,"RESEARCH_EXPORT",null,"CSV","private/test.csv","text/csv",
            new string('a',64),new string('b',64),"retrospective study","{\"purpose\":\"retrospective study\"}",key,scope,DateTimeOffset.UtcNow.AddHours(1),"integration-test");

        await repository.InsertAsync(item,CancellationToken.None);
        var found=await repository.FindByKeyAsync(tenant,actor,"RESEARCH_EXPORT",key,CancellationToken.None);

        Assert.NotNull(found);Assert.Equal(id,found.Id);Assert.Equal(scope,found.PatientScope);Assert.Equal(item.Purpose,found.Purpose);
        Assert.Equal(item.Sha256,found.Sha256);Assert.Equal(item.Fingerprint,found.Fingerprint);
    }
}
