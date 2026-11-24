using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class PublisherRoleTests
{
    [M1PostgresFact]
    public async Task Fresh_install_provisions_publisher_without_granting_business_or_payload_writes()
    {
        await using var db=await Database.CreateAsync();
        var cs=new NpgsqlConnectionStringBuilder(db.Admin.ConnectionString){Options="-c role=aura_analysis_publisher"};
        await using var publisher=NpgsqlDataSource.Create(cs.ConnectionString);
        await using(var query=publisher.CreateCommand("SELECT id,claim_generation FROM analysis.outbox_events LIMIT 0"))
            await query.ExecuteNonQueryAsync();
        await using(var claim=publisher.CreateCommand("UPDATE analysis.outbox_events SET claim_generation=claim_generation+1,locked_until=now() WHERE false"))
            Assert.Equal(0,await claim.ExecuteNonQueryAsync());
        foreach(var sql in new[]{"UPDATE analysis.outbox_events SET payload='{}' WHERE false",
            "DELETE FROM analysis.outbox_events WHERE false","SELECT * FROM identity.accounts LIMIT 0",
            "UPDATE analysis.images SET processing_status='FAILED' WHERE false"})
        {
            await using var command=publisher.CreateCommand(sql);
            var error=await Assert.ThrowsAsync<PostgresException>(async()=>await command.ExecuteNonQueryAsync());
            Assert.Equal("42501",error.SqlState);
        }
    }
}
