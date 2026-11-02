using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Upload;
using AURA.Common.Cache;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class BatchProcessingTests
{
    private sealed class ClinicAccessResolver(Guid tenantId) : IPatientAccessResolver
    {
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct) =>
            Task.FromResult(new AccessDecision(true, tenantId, "CLINIC", request.RequesterId,
                $"CLINIC:{tenantId:D}", 1));
    }

    private sealed class BatchProgressStub : IBatchRedisProgressService
    {
        public Guid InitializedBatchId { get; private set; }
        public int InitializedTotal { get; private set; }

        public Task InitializeBatchAsync(Guid batchId, int totalImages, TimeSpan? expiry = null)
        {
            InitializedBatchId = batchId;
            InitializedTotal = totalImages;
            return Task.CompletedTask;
        }

        public Task<BatchProgressSnapshot?> GetProgressAsync(Guid batchId) =>
            Task.FromResult<BatchProgressSnapshot?>(null);

        public Task<BatchImageCompletionResult> RecordImageOutcomeAsync(Guid batchId, Guid imageId, bool isSuccess) =>
            Task.FromResult(new BatchImageCompletionResult(batchId, InitializedTotal, 0, 0, InitializedTotal,
                false, "IN_PROGRESS"));

        public Task RebuildBatchAsync(Guid batchId, int totalImages, IReadOnlyList<BatchImageOutcome> outcomes,
            string status, TimeSpan? expiry = null) => Task.CompletedTask;
    }

    [PostgresFact]
    public async Task Uploading_100_images_persists_one_batch_and_links_it_to_the_clinic_campaign()
    {
        var connectionString = Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var requesterId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.campaigns
                (id, tenant_id, name, location, target_patient_count, status, start_date, end_date)
            VALUES ($1, $2, 'SCRUM-369 integration campaign', 'CI fixture', 100, 'IN_PROGRESS', now(), now() + interval '1 day');
            """, ct, campaignId, tenantId);

        var access = new ClinicAccessResolver(tenantId);
        var repository = new PostgresUploadRepository(dataSource);
        var planning = new UploadPlanningService(new UploadOptions(), access, repository, TimeProvider.System);
        var journal = new PostgresUploadRecoveryRepository(dataSource);
        var billing = new FakeBilling();
        var storage = new FakeStorage();
        var progress = new BatchProgressStub();
        var handler = new DurableUploadService(planning, journal, billing, storage, progress);
        var files = Enumerable.Range(0, 100)
            .Select(index => (IUploadedFile)new MemFile($"fundus-{index + 1:D3}.jpg",
                TestImages.Jpeg(1000, 800, seed: index + 1), "image/jpeg"))
            .ToArray();
        var command = new UploadCommand(requesterId, clinicId, Guid.NewGuid(), "scrum369-batch100",
            patientId.ToString("D"), "FUNDUS", "right", files);

        var result = await handler.CreateAsync(command, ct);

        Assert.Equal(100, result.Response.Items.Count);
        Assert.All(result.Response.Items, item => Assert.Equal("QUEUED", item.ProcessingStatus));
        Assert.Equal(100, billing.ReserveCalls);
        Assert.Equal(100, storage.PutCalls);
        Assert.Equal(100, storage.Files.Count);
        Assert.Equal(result.Response.BatchId, progress.InitializedBatchId);
        Assert.Equal(100, progress.InitializedTotal);

        await using (var batch = dataSource.CreateCommand("""
            SELECT total_images, clinic_id FROM analysis.batches WHERE tenant_id=$1 AND id=$2;
            """))
        {
            batch.Parameters.AddWithValue(tenantId);
            batch.Parameters.AddWithValue(result.Response.BatchId);
            await using var reader = await batch.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(100, reader.GetInt32(0));
            Assert.Equal(clinicId, reader.GetGuid(1));
        }

        Assert.Equal(100L, await CountAsync(dataSource,
            "SELECT count(*) FROM analysis.images WHERE tenant_id=$1 AND batch_id=$2;", ct, tenantId, result.Response.BatchId));
        Assert.Equal(100L, await CountAsync(dataSource, """
            SELECT count(*) FROM analysis.jobs j JOIN analysis.images i ON i.tenant_id=j.tenant_id AND i.id=j.image_id
            WHERE i.tenant_id=$1 AND i.batch_id=$2;
            """, ct, tenantId, result.Response.BatchId));
        Assert.Equal(200L, await CountAsync(dataSource,
            "SELECT count(*) FROM analysis.outbox_events WHERE tenant_id=$1 AND analysis_request_id=$2;",
            ct, tenantId, result.Response.BatchId));

        await new CampaignRepository(dataSource).LinkBatchAsync(tenantId, campaignId, result.Response.BatchId, ct);
        await using var linked = dataSource.CreateCommand("""
            SELECT count(*) FROM analysis.batches b
            JOIN analysis.campaigns c ON c.tenant_id=b.tenant_id AND c.id=b.campaign_id
            WHERE b.tenant_id=$1 AND b.id=$2 AND b.clinic_id=$3 AND c.id=$4;
            """);
        linked.Parameters.AddWithValue(tenantId);
        linked.Parameters.AddWithValue(result.Response.BatchId);
        linked.Parameters.AddWithValue(clinicId);
        linked.Parameters.AddWithValue(campaignId);
        Assert.Equal(1L, await linked.ExecuteScalarAsync(ct));
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct,
        params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct,
        params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
