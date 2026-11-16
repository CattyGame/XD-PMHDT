using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Upload;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class CampaignConcurrencyTests
{
    [PostgresFact]
    public async Task Close_waits_for_inflight_link_then_rejects_new_links()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var connectionString = Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!;
        var application = "campaign-race-" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(connectionString);
        await using var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
            { ApplicationName = application, Options = "-c role=aura_analysis_app" }.ConnectionString);
        var tenant = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var repository = new CampaignRepository(runtime);
        var campaign = await repository.CreateCampaignAsync(tenant,
            new("Concurrency fixture", null, "test", null, 50, DateTime.UtcNow, DateTime.UtcNow.AddDays(1)), ct);
        await using (var seed = admin.CreateCommand("""
            INSERT INTO analysis.batches(id,tenant_id,owner_id,requester_id,patient_id,total_images,status)
            VALUES($1,$2,$3,$3,$4,50,'IN_PROGRESS');
            """))
        {
            foreach (var value in new[] { batch, tenant, Guid.NewGuid(), Guid.NewGuid() }) seed.Parameters.AddWithValue(value);
            await seed.ExecuteNonQueryAsync(ct);
        }

        await using var hold = await admin.OpenConnectionAsync(ct);
        await using var tx = await hold.BeginTransactionAsync(ct);
        await new NpgsqlCommand("LOCK TABLE analysis.batches IN ACCESS EXCLUSIVE MODE", hold, tx).ExecuteNonQueryAsync(ct);
        var linking = repository.LinkBatchAsync(tenant, campaign.Id, batch, ct);
        await using var blocked = admin.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock'");
        blocked.Parameters.AddWithValue(application);
        while (Convert.ToInt32(await blocked.ExecuteScalarAsync(ct)) == 0) await Task.Delay(20, ct);
        var closing = repository.UpdateCampaignStatusAsync(tenant, campaign.Id, CampaignStatus.Closed, ct);
        // The campaign row must remain locked until the admitted batch is linked.
        while (!closing.IsCompleted && Convert.ToInt32(await blocked.ExecuteScalarAsync(ct)) < 2) await Task.Delay(20, ct);
        var earlyClose = closing.IsCompleted;
        await tx.CommitAsync(ct);
        await linking;
        await closing;
        Assert.False(earlyClose, "CLOSED overtook a link which had already read the old status.");
        var failure = await Assert.ThrowsAsync<UploadFailure>(() => repository.LinkBatchAsync(tenant, campaign.Id, batch, ct));
        Assert.Equal("ERR_CAMPAIGN_CLOSED", failure.Code);
    }
}
