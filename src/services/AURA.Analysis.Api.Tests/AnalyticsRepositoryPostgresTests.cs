using AURA.Analysis.Api.Analytics;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsRepositoryPostgresTests
{
    [PostgresFact]
    [Trait("TestKind", "PostgreSQL")]
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
        var repository = new PostgresAnalyticsRepository(db);
        var revisionBefore = await repository.ReadDataRevisionAsync(default);

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

        var revisionAfter = await repository.ReadDataRevisionAsync(default);
        Assert.NotEqual(revisionBefore, revisionAfter);

        var summary = await repository.ReadSummaryAsync(
            new(tenant, clinic, patient), AnalyticsWindow.Create(start, end), default);

        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "scrum386-golden.json")));
        var golden = fixture.RootElement.GetProperty("analytics");
        var expected = golden.GetProperty("expected");
        Assert.Equal(expected.GetProperty("total").GetInt64(), summary.Total);
        Assert.Equal(expected.GetProperty("active").GetInt64(), summary.Active);
        Assert.Equal(expected.GetProperty("completed").GetInt64(), summary.Completed);
        Assert.Equal(expected.GetProperty("failed").GetInt64(), summary.Failed);
        Assert.Equal(expected.GetProperty("lowQuality").GetInt64(), summary.LowQuality);
        Assert.Equal(expected.GetProperty("rejected").GetInt64(), summary.Rejected);
        Assert.Equal(expected.GetProperty("duplicate").GetInt64(), summary.Duplicate);
        Assert.Equal(expected.GetProperty("riskLow").GetInt64(), summary.RiskLow);
        Assert.Equal(expected.GetProperty("riskModerate").GetInt64(), summary.RiskModerate);
        Assert.Equal(expected.GetProperty("riskHigh").GetInt64(), summary.RiskHigh);
        Assert.Equal(expected.GetProperty("riskUnavailable").GetInt64(), summary.RiskUnavailable);
        Assert.Equal(expected.GetProperty("riskUnreviewed").GetInt64(), summary.RiskUnreviewed);
        Assert.Equal(expected.GetProperty("measurementAvailable").GetInt64(), summary.MeasurementAvailable);
        Assert.Equal(expected.GetProperty("measurementUnavailable").GetInt64(), summary.MeasurementUnavailable);
        Assert.Equal(expected.GetProperty("measurementUnknown").GetInt64(), summary.MeasurementUnknown);

        var ratios = golden.GetProperty("ratios");
        AssertRatio(ratios.GetProperty("completedOfUnique"), summary.Completed, summary.Total);
        AssertRatio(ratios.GetProperty("lowQualityOfUnique"), summary.LowQuality, summary.Total);
        AssertRatio(ratios.GetProperty("moderateLatestReviewOfUnique"), summary.RiskModerate, summary.Total);
        AssertRatio(ratios.GetProperty("unreviewedOfUnique"), summary.RiskUnreviewed, summary.Total);
        AssertRatio(ratios.GetProperty("measurementAvailableOfUnique"), summary.MeasurementAvailable, summary.Total);
        AssertRatio(ratios.GetProperty("measurementUnavailableOfUnique"), summary.MeasurementUnavailable, summary.Total);
        AssertRatio(ratios.GetProperty("measurementUnknownOfUnique"), summary.MeasurementUnknown, summary.Total);

        var empty = await new PostgresAnalyticsRepository(db).ReadSummaryAsync(
            new(tenant, clinic, patient), AnalyticsWindow.Create(end.AddDays(1), end.AddDays(2)), default);
        Assert.Equal(0, empty.Total);
        AssertRatio(ratios.GetProperty("emptyDenominator"), 0, empty.Total);
    }

    private static void AssertRatio(JsonElement expected, long numerator, long denominator)
    {
        if (denominator == 0)
        {
            Assert.Equal(JsonValueKind.Null, expected.ValueKind);
            return;
        }

        Assert.Equal(expected.GetDouble(), numerator / (double)denominator, precision: 12);
    }
}
