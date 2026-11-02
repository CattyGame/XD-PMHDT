using AURA.Analysis.Api.Upload;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class BatchIntegrationTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING");

    [PostgresFact]
    public async Task Twenty_concurrent_status_polls_return_consistent_batch_snapshot()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var tenant = Guid.NewGuid(); var patient = Guid.NewGuid(); var owner = Guid.NewGuid();
        var batchId = Guid.NewGuid(); var analysisId = Guid.NewGuid(); var jobId = Guid.NewGuid(); var resultId = Guid.NewGuid();
        var accepted = System.Text.Json.JsonSerializer.Serialize(new
        {
            batchId,
            items = new[] { new { analysisId, fileName = "scrum345-poll.png", processingStatus = "COMPLETED" } },
            createdAt = DateTime.UtcNow,
            statusUrl = $"/api/v1/batches/{batchId:D}"
        });
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.analysis_requests
                (id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,request_fingerprint,
                 request_schema_version,accepted_response,correlation_id)
            VALUES ($1,$2,$3,$3,$4,$5,repeat('c',64),'0.8.0',$6::jsonb,$7);
            """, batchId, tenant, owner, patient, Guid.NewGuid(), accepted, $"scrum345-{batchId:N}");
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.images
                (id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,mime_type,size_bytes,
                 width,height,sha256,processing_status,review_status)
            VALUES ($1,$2,$3,$4,'FUNDUS','right',$5,'image/png',64,8,8,repeat('d',64),'COMPLETED','NOT_REQUIRED');
            """, analysisId, tenant, batchId, Guid.NewGuid(), $"scrum345/{batchId:N}/image.png");
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.jobs (id,tenant_id,image_id,message_id,attempt_no,status,correlation_id)
            VALUES ($1,$2,$3,$4,1,'SUCCEEDED',$5);
            """, jobId, tenant, analysisId, Guid.NewGuid(), $"scrum345-{batchId:N}");
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.analysis_results
                (id,tenant_id,image_id,job_id,version,result_schema_version,risk_assessment,metrics,warnings,
                 is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations)
            VALUES ($1,$2,$3,$4,1,'0.8.0','{"riskLevel":"LOW"}'::jsonb,'{}'::jsonb,'[]'::jsonb,
                    true,'test','test','test',1,'synthetic integration fixture');
            """, resultId, tenant, analysisId, jobId);

        var repository = new PostgresBatchStatusRepository(dataSource);
        var identity = await repository.FindIdentityAsync(batchId, CancellationToken.None);
        Assert.NotNull(identity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var polls = Enumerable.Range(0, 20).Select(_ => repository.ReadAsync(identity!, timeout.Token));
        var snapshots = await Task.WhenAll(polls);
        Assert.All(snapshots, snapshot =>
        {
            Assert.NotNull(snapshot);
            Assert.Single(snapshot!.Items);
            Assert.Equal(analysisId, snapshot.Items[0].AnalysisId);
            Assert.Equal("COMPLETED", snapshot.Items[0].ProcessingStatus);
            Assert.Equal("LOW", snapshot.Items[0].RiskLevel);
        });
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
}
