using AURA.Analysis.Api.Analytics;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsRepositoryPostgresTests
{
    [PostgresFact]
    public async Task Summary_counts_unique_analyses_latest_review_and_explicit_measurement_state_in_utc_window()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!)
        { Options = "-c role=aura_analysis_app" }.ConnectionString;
        await using var db = NpgsqlDataSource.Create(connectionString);
        var tenant = Guid.NewGuid();
        var clinic = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        async Task Sql(string statement, params object[] values)
        {
            await using var command = db.CreateCommand(statement);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }

        async Task<Guid> SeedImage(string status, DateTimeOffset createdAt, Guid? duplicateOf = null)
        {
            var batch = Guid.NewGuid();
            var image = Guid.NewGuid();
            await Sql("""
                INSERT INTO analysis.analysis_requests(id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,
                    request_fingerprint,request_schema_version,accepted_response,correlation_id)
                VALUES($1,$2,$3,$4,$5,$6,repeat('a',64),'0.9','{}','analytics-fixture')
                """, batch, tenant, clinic, Guid.NewGuid(), patient, Guid.NewGuid());
            await Sql("""
                INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,
                    mime_type,size_bytes,width,height,sha256,processing_status,review_status,existing_analysis_id,created_at)
                VALUES($1,$2,$3,$4,'FUNDUS','right','analytics-fixture/'||$1::text,'image/jpeg',100,1000,800,
                    repeat('b',64),$5,'PENDING',$6,$7)
                """, image, tenant, batch, Guid.NewGuid(), status,
                duplicateOf is { } existing ? existing : DBNull.Value, createdAt.UtcDateTime);
            return image;
        }

        var completed = await SeedImage("COMPLETED", start);
        var unavailable = await SeedImage("COMPLETED", start.AddHours(1));
        await SeedImage("LOW_QUALITY", start.AddHours(2));
        await SeedImage("DUPLICATE", start.AddHours(3), completed);
        await SeedImage("COMPLETED", end);

        var retryJob = Guid.NewGuid();
        var job = Guid.NewGuid();
        var result = Guid.NewGuid();
        await Sql("""
            INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,retryable,correlation_id)
            VALUES($1,$2,$3,$4,1,'FAILED',true,'analytics-fixture')
            """, retryJob, tenant, completed, Guid.NewGuid());
        await Sql("""
            INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,retryable,correlation_id)
            VALUES($1,$2,$3,$4,2,'SUCCEEDED',false,'analytics-fixture')
            """, job, tenant, completed, Guid.NewGuid());
        await Sql("""
            INSERT INTO analysis.analysis_results(id,tenant_id,image_id,job_id,version,result_schema_version,
                risk_assessment,metrics,warnings,is_mock,model_version,config_version,threshold_version,
                processing_time_ms,limitations)
            VALUES($1,$2,$3,$4,1,'0.9','{"riskLevel":"HIGH"}',
                '{"research":{"avr":{"measurement_available":true,"avr":0.67,"model_version":"avr-v1"}}}',
                '[]',false,'model-v1','config-v1','threshold-v1',10,'fixture')
            """, result, tenant, completed, job);
        await Sql("""
            INSERT INTO analysis.doctor_reviews(id,tenant_id,image_id,analysis_result_id,doctor_id,version,
                base_version,is_approved,corrected_risk_level,reason)
            VALUES($1,$2,$3,$4,$5,1,0,false,'LOW','earlier correction')
            """, Guid.NewGuid(), tenant, completed, result, Guid.NewGuid());

        var unavailableJob = Guid.NewGuid();
        var unavailableResult = Guid.NewGuid();
        await Sql("""
            INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,retryable,correlation_id)
            VALUES($1,$2,$3,$4,1,'SUCCEEDED',false,'analytics-fixture')
            """, unavailableJob, tenant, unavailable, Guid.NewGuid());
        await Sql("""
            INSERT INTO analysis.analysis_results(id,tenant_id,image_id,job_id,version,result_schema_version,
                risk_assessment,metrics,warnings,is_mock,model_version,config_version,threshold_version,
                processing_time_ms,limitations)
            VALUES($1,$2,$3,$4,1,'0.9','{"riskLevel":"LOW"}',
                '{"research":{"avr":{"measurement_available":false,"avr":null,"unavailable_reason":"INCOMPLETE_ZONE_B"}}}',
                '[]',false,'model-v1','config-v1','threshold-v1',10,'fixture')
            """, unavailableResult, tenant, unavailable, unavailableJob);
        await Sql("""
            INSERT INTO analysis.doctor_reviews(id,tenant_id,image_id,analysis_result_id,doctor_id,version,
                base_version,is_approved,corrected_risk_level,reason)
            VALUES($1,$2,$3,$4,$5,2,1,false,'MODERATE','latest correction')
            """, Guid.NewGuid(), tenant, completed, result, Guid.NewGuid());

        var summary = await new PostgresAnalyticsRepository(db).ReadSummaryAsync(
            new(tenant, clinic, patient), AnalyticsWindow.Create(start, end), default);

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.Completed);
        Assert.Equal(1, summary.LowQuality);
        Assert.Equal(1, summary.Duplicate);
        Assert.Equal(1, summary.RiskModerate);
        Assert.Equal(2, summary.RiskUnreviewed);
        Assert.Equal(1, summary.MeasurementAvailable);
        Assert.Equal(1, summary.MeasurementUnavailable);
        Assert.Equal(1, summary.MeasurementUnknown);
        Assert.Equal(0, summary.RiskLow);
    }
}
