using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AURA.Analysis.Api.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class Scrum402PostgresFactAttribute : FactAttribute
{
    public Scrum402PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_SCRUM402_TEST_CONNECTION_STRING")))
            Skip = "Requires a disposable PostgreSQL database in AURA_SCRUM402_TEST_CONNECTION_STRING.";
    }
}

public sealed class Scrum402AnalyticsPerformanceTests(ITestOutputHelper output)
{
    private const int AnalysisCount = 10_000;
    private const int AccountCount = 2_000;
    private const int ReaderCount = 20;
    private const int Rounds = 5;
    private static readonly DateTimeOffset WindowStart = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    [Scrum402PostgresFact]
    [Trait("TestKind", "Performance")]
    public async Task Analytics_query_and_service_p95_remain_below_three_seconds_at_scrum402_profile()
    {
        var connectionString = Environment.GetEnvironmentVariable("AURA_SCRUM402_TEST_CONNECTION_STRING")!;
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        var tenantId = Guid.NewGuid();
        var clinicId = FixtureId("clinic-0");
        var patientId = FixtureId("account-0");
        var actorId = FixtureId("actor-doctor");
        var database = NpgsqlDataSource.Create(adminBuilder.ConnectionString);
        var appBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Options = "-c role=aura_analysis_app" };
        var appDatabase = NpgsqlDataSource.Create(appBuilder.ConnectionString);

        try
        {
            await SeedAsync(database, tenantId);
            await AnalyzeAsync(database);
            await AssertFixtureAsync(database, tenantId);

            var repository = new PostgresAnalyticsRepository(appDatabase);
            var historyRepository = new PostgresAnalysisQueryRepository(appDatabase);
            var clinicScope = new AnalyticsDataScope(tenantId, clinicId, null);
            var window = AnalyticsWindow.Create(WindowStart, WindowEnd);
            var access = new ProfileFixture(tenantId, clinicId, patientId);
            var service = new AnalyticsService(access, repository, TimeProvider.System);

            var summaryPlan = await ExplainSummaryAsync(appDatabase, tenantId, clinicId, window);
            var trendsPlan = await ExplainTrendsAsync(appDatabase, tenantId, clinicId, patientId, window);
            var historyFirstPage = await historyRepository.ListAsync(tenantId, clinicId, patientId, 3, null, CancellationToken.None);
            Assert.Equal(3, historyFirstPage.Count);
            var historySecondPage = await historyRepository.ListAsync(tenantId, clinicId, patientId, 3,
                historyFirstPage[^1].AnalysisId, CancellationToken.None);
            Assert.Equal(2, historySecondPage.Count);
            Assert.Empty(historyFirstPage.Select(x => x.AnalysisId).Intersect(historySecondPage.Select(x => x.AnalysisId)));
            Assert.True(historyFirstPage[^1].CreatedAt > historySecondPage[0].CreatedAt ||
                (historyFirstPage[^1].CreatedAt == historySecondPage[0].CreatedAt && historyFirstPage[^1].AnalysisId.CompareTo(historySecondPage[0].AnalysisId) > 0));
            var historyPlan = await ExplainHistoryAsync(appDatabase, tenantId, clinicId, patientId,
                historyFirstPage[^1].AnalysisId, 4);

            var querySummaryP95 = await MeasureP95Async(async () =>
            {
                var row = await repository.ReadSummaryAsync(clinicScope, window, CancellationToken.None);
                Assert.Equal(500, row.Total);
            });
            var queryTrendsP95 = await MeasureP95Async(async () =>
            {
                var rows = await repository.ReadPatientTrendsAsync(tenantId, clinicId, patientId, window, 100, CancellationToken.None);
                Assert.InRange(rows.Count, 1, 100);
            });
            var queryHistoryP95 = await MeasureP95Async(async () =>
            {
                var rows = await historyRepository.ListAsync(tenantId, clinicId, patientId, 3,
                    historyFirstPage[^1].AnalysisId, CancellationToken.None);
                Assert.Equal(2, rows.Count);
            });
            var serviceSummaryP95 = await MeasureP95Async(async () =>
            {
                var result = await service.GetClinicSummaryAsync(actorId, clinicId, window, "scrum402-perf", CancellationToken.None);
                Assert.Equal(500, result.Total);
            });
            var serviceTrendsP95 = await MeasureP95Async(async () =>
            {
                var result = await service.GetPatientTrendsAsync(actorId, clinicId, patientId, window, 100, "scrum402-perf", CancellationToken.None);
                Assert.InRange(result.Items.Count, 1, 100);
            });

            output.WriteLine("SCRUM-402 profile: PostgreSQL {0}; analyses={1}; distinct accounts={2}; concurrent readers={3}; rounds={4}.",
                await ServerVersionAsync(database), AnalysisCount, AccountCount, ReaderCount, Rounds);
            output.WriteLine("EXPLAIN ANALYZE clinic-summary: {0:N2} ms; plan={1}", summaryPlan.ExecutionMs, summaryPlan.Nodes);
            output.WriteLine("EXPLAIN ANALYZE patient-trends: {0:N2} ms; plan={1}", trendsPlan.ExecutionMs, trendsPlan.Nodes);
            output.WriteLine("EXPLAIN ANALYZE patient-history page (stable cursor): {0:N2} ms; plan={1}", historyPlan.ExecutionMs, historyPlan.Nodes);
            output.WriteLine("Query p95: clinic-summary={0:N2} ms; patient-trends={1:N2} ms.", querySummaryP95, queryTrendsP95);
            output.WriteLine("Query p95: patient-history-cursor={0:N2} ms (page size 3).", queryHistoryP95);
            output.WriteLine("Service p95 (local Profile authority fixture): clinic-summary={0:N2} ms; patient-trends={1:N2} ms.", serviceSummaryP95, serviceTrendsP95);
            output.WriteLine("UI timing: not applicable; no frontend caller for these Analytics routes is present in this checkout.");

            Assert.InRange(querySummaryP95, 0, 3_000);
            Assert.InRange(queryTrendsP95, 0, 3_000);
            Assert.InRange(queryHistoryP95, 0, 3_000);
            Assert.InRange(serviceSummaryP95, 0, 3_000);
            Assert.InRange(serviceTrendsP95, 0, 3_000);
        }
        finally
        {
            await CleanupAsync(database, tenantId);
            await appDatabase.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private static async Task SeedAsync(NpgsqlDataSource db, Guid tenantId)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var function = new NpgsqlCommand("""
            CREATE FUNCTION pg_temp.scrum402_uuid(input text) RETURNS uuid
            LANGUAGE sql IMMUTABLE AS $$
                SELECT (substr(md5(input),1,8)||'-'||substr(md5(input),9,4)||'-'||substr(md5(input),13,4)||'-'||substr(md5(input),17,4)||'-'||substr(md5(input),21,12))::uuid
            $$;
            """, connection, transaction))
            await function.ExecuteNonQueryAsync();

        await using (var fixture = new NpgsqlCommand("""
            CREATE TEMP TABLE scrum402_seed ON COMMIT PRESERVE ROWS AS
            SELECT n,
                   pg_temp.scrum402_uuid('request-'||n::text) AS request_id,
                   pg_temp.scrum402_uuid('image-'||n::text) AS image_id,
                   pg_temp.scrum402_uuid('job-'||n::text) AS job_id,
                   pg_temp.scrum402_uuid('result-'||n::text) AS result_id,
                   pg_temp.scrum402_uuid('clinic-'||((n-1)%20)::text) AS clinic_id,
                   pg_temp.scrum402_uuid('account-'||((n-1)%2000)::text) AS account_id,
                   @tenant_id::uuid AS tenant_id,
                   timestamptz '2026-11-01 00:00:00+00'
                       + (((n-1)%21) * interval '1 day') + ((n%1440) * interval '1 minute') AS created_at
            FROM generate_series(1,10000) AS n;
            """, connection, transaction))
        {
            fixture.Parameters.AddWithValue("tenant_id", tenantId);
            await fixture.ExecuteNonQueryAsync();
        }

        var inserts = new[]
        {
            """
            INSERT INTO analysis.analysis_requests
                (id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,request_fingerprint,
                 request_schema_version,accepted_response,correlation_id,created_at)
            SELECT request_id,tenant_id,clinic_id,account_id,account_id,
                   pg_temp.scrum402_uuid('key-'||n::text),repeat('a',64),'0.9','{}','scrum402-perf',created_at
            FROM scrum402_seed;
            """,
            """
            INSERT INTO analysis.images
                (id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,mime_type,
                 size_bytes,width,height,sha256,processing_status,review_status,created_at)
            SELECT image_id,tenant_id,request_id,pg_temp.scrum402_uuid('file-'||n::text),'FUNDUS','right',
                   'scrum402-perf/'||n::text,'image/jpeg',100,1000,800,repeat('b',64),
                   CASE WHEN n%10<7 THEN 'COMPLETED' WHEN n%10=7 THEN 'FAILED'
                        WHEN n%10=8 THEN 'LOW_QUALITY' ELSE 'PENDING' END,
                   CASE WHEN n%10=0 THEN 'REVIEWED' ELSE 'PENDING' END,created_at
            FROM scrum402_seed;
            """,
            """
            INSERT INTO analysis.jobs
                (id,tenant_id,image_id,message_id,attempt_no,status,retryable,correlation_id)
            SELECT job_id,tenant_id,image_id,pg_temp.scrum402_uuid('message-'||n::text),1,'SUCCEEDED',false,'scrum402-perf'
            FROM scrum402_seed WHERE n%10<7;
            """,
            """
            INSERT INTO analysis.analysis_results
                (id,tenant_id,image_id,job_id,version,result_schema_version,risk_assessment,metrics,warnings,
                 is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations)
            SELECT result_id,tenant_id,image_id,job_id,1,'0.9','{"riskLevel":"LOW"}'::jsonb,
                   '{"research":{"avr":{"measurement_available":true,"avr":0.67,"model_version":"avr-v1"}}}'::jsonb,
                   '[]'::jsonb,false,'model-v1','config-v1','threshold-v1',10,'synthetic performance fixture'
            FROM scrum402_seed WHERE n%10<7;
            """,
            """
            INSERT INTO analysis.doctor_reviews
                (id,tenant_id,image_id,analysis_result_id,doctor_id,version,base_version,is_approved,corrected_risk_level,reason)
            SELECT pg_temp.scrum402_uuid('review-'||n::text),tenant_id,image_id,result_id,
                   pg_temp.scrum402_uuid('doctor-'||((n-1)%20)::text),1,0,false,'LOW','synthetic performance fixture'
            FROM scrum402_seed WHERE n%10=0;
            """
        };

        foreach (var sql in inserts)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task AnalyzeAsync(NpgsqlDataSource db)
    {
        foreach (var table in new[] { "analysis.analysis_requests", "analysis.images", "analysis.analysis_results", "analysis.doctor_reviews" })
        {
            await using var command = db.CreateCommand($"ANALYZE {table};");
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertFixtureAsync(NpgsqlDataSource db, Guid tenantId)
    {
        await using var command = db.CreateCommand("""
            SELECT count(*),count(DISTINCT requester_id)
            FROM analysis.analysis_requests WHERE tenant_id=@tenant_id;
            """);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(AnalysisCount, reader.GetInt64(0));
        Assert.Equal(AccountCount, reader.GetInt64(1));
    }

    private static async Task<(double ExecutionMs, string Nodes)> ExplainSummaryAsync(
        NpgsqlDataSource db, Guid tenantId, Guid clinicId, AnalyticsWindow window)
    {
        const string sql = """
            WITH scoped AS (
                SELECT i.processing_status,(review.version IS NOT NULL) AS has_review,
                       COALESCE(review.corrected_risk_level,reviewed_result.risk_assessment->>'riskLevel',
                           reviewed_result.risk_assessment->>'risk_level') AS reviewed_risk,
                       CASE lower(COALESCE(result.metrics #>> '{research,avr,measurement_available}',''))
                           WHEN 'true' THEN true WHEN 'false' THEN false ELSE NULL END AS avr_available
                FROM analysis.images i
                JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
                LEFT JOIN LATERAL (
                    SELECT x.risk_assessment,x.metrics FROM analysis.analysis_results x
                    WHERE x.tenant_id=i.tenant_id AND x.image_id=i.id ORDER BY x.version DESC LIMIT 1
                ) result ON true
                LEFT JOIN LATERAL (
                    SELECT d.corrected_risk_level,d.analysis_result_id,d.version,d.doctor_id
                    FROM analysis.doctor_reviews d WHERE d.tenant_id=i.tenant_id AND d.image_id=i.id
                    ORDER BY d.version DESC LIMIT 1
                ) review ON true
                LEFT JOIN analysis.analysis_results reviewed_result
                  ON reviewed_result.tenant_id=i.tenant_id AND reviewed_result.id=review.analysis_result_id
                WHERE ($1::uuid IS NULL OR i.tenant_id=$1) AND ($2::uuid IS NULL OR r.owner_id=$2)
                  AND ($3::uuid IS NULL OR r.patient_id=$3) AND ($4::uuid[] IS NULL OR r.patient_id=ANY($4))
                  AND ($5::uuid IS NULL OR review.doctor_id=$5) AND i.created_at >= $6 AND i.created_at < $7
            )
            SELECT count(*) FILTER (WHERE processing_status<>'DUPLICATE') AS total,
                   count(*) FILTER (WHERE processing_status IN ('PENDING','QUEUED','PROCESSING','RETRY_READY')) AS active,
                   count(*) FILTER (WHERE processing_status='COMPLETED') AS completed,
                   count(*) FILTER (WHERE processing_status='FAILED') AS failed,
                   count(*) FILTER (WHERE processing_status='LOW_QUALITY') AS low_quality,
                   count(*) FILTER (WHERE processing_status='REJECTED') AS rejected,
                   count(*) FILTER (WHERE processing_status='DUPLICATE') AS duplicate,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND has_review AND reviewed_risk='LOW') AS risk_low,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND has_review AND reviewed_risk='MODERATE') AS risk_moderate,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND has_review AND reviewed_risk='HIGH') AS risk_high,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND has_review AND reviewed_risk IS NULL) AS risk_unavailable,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND NOT has_review) AS risk_unreviewed,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND avr_available IS TRUE) AS measurement_available,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND avr_available IS FALSE) AS measurement_unavailable,
                   count(*) FILTER (WHERE processing_status<>'DUPLICATE' AND avr_available IS NULL) AS measurement_unknown
            FROM scoped;
            """;
        await using var command = db.CreateCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = clinicId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Value = DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = DBNull.Value });
        command.Parameters.AddWithValue(window.From);
        command.Parameters.AddWithValue(window.To);
        return await ReadPlanAsync(command);
    }

    private static async Task<(double ExecutionMs, string Nodes)> ExplainTrendsAsync(
        NpgsqlDataSource db, Guid tenantId, Guid clinicId, Guid patientId, AnalyticsWindow window)
    {
        const string sql = """
            SELECT i.id,i.created_at,
                   COALESCE(result.metrics #>> '{research,avr,model_version}',result.model_version),
                   result.config_version,result.threshold_version,
                   CASE WHEN result.metrics #>> '{research,avr,avr}' IS NOT NULL THEN 'ratio'
                        WHEN result.metrics #>> '{research,avr,crae_px}' IS NOT NULL THEN 'native_px' ELSE NULL END,
                   COALESCE(review.version,0),NULLIF(result.metrics #>> '{research,avr,avr}','')::numeric,
                   NULLIF(result.metrics #>> '{research,avr,unavailable_reason}',''),
                   COALESCE(lower(result.metrics #>> '{research,avr,measurement_available}')='true',false),
                   NULLIF(result.metrics->>'vesselDensity','')::numeric,
                   NULLIF(result.metrics->>'tortuosityIndex','')::numeric
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
            LEFT JOIN LATERAL (
                SELECT x.model_version,x.config_version,x.threshold_version,x.metrics FROM analysis.analysis_results x
                WHERE x.tenant_id=i.tenant_id AND x.image_id=i.id ORDER BY x.version DESC LIMIT 1
            ) result ON true
            LEFT JOIN LATERAL (
                SELECT d.version FROM analysis.doctor_reviews d WHERE d.tenant_id=i.tenant_id AND d.image_id=i.id
                ORDER BY d.version DESC LIMIT 1
            ) review ON true
            WHERE i.tenant_id=$1 AND r.owner_id=$2 AND r.patient_id=$3
              AND i.processing_status='COMPLETED' AND result.metrics IS NOT NULL
              AND i.processing_status<>'DUPLICATE' AND i.created_at >= $4 AND i.created_at < $5
            ORDER BY i.created_at,i.id LIMIT $6;
            """;
        await using var command = db.CreateCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(clinicId);
        command.Parameters.AddWithValue(patientId);
        command.Parameters.AddWithValue(window.From);
        command.Parameters.AddWithValue(window.To);
        command.Parameters.AddWithValue(100);
        return await ReadPlanAsync(command);
    }

    private static async Task<(double ExecutionMs, string Nodes)> ExplainHistoryAsync(
        NpgsqlDataSource db, Guid tenantId, Guid clinicId, Guid patientId, Guid beforeId, int limit)
    {
        const string sql = """
            SELECT i.id,COALESCE(i.batch_id,i.analysis_request_id),i.processing_status,i.review_status,i.created_at
            FROM analysis.images i JOIN analysis.analysis_requests r
              ON r.id=i.analysis_request_id AND r.tenant_id=i.tenant_id
            WHERE i.tenant_id=$1 AND r.owner_id=$2 AND r.patient_id=$3
              AND ($4::uuid IS NULL OR (i.created_at,i.id) < (
                SELECT ci.created_at,ci.id FROM analysis.images ci
                JOIN analysis.analysis_requests cr ON cr.id=ci.analysis_request_id AND cr.tenant_id=ci.tenant_id
                WHERE ci.id=$4 AND ci.tenant_id=$1 AND cr.owner_id=$2 AND cr.patient_id=$3))
            ORDER BY i.created_at DESC,i.id DESC LIMIT $5;
            """;
        await using var command = db.CreateCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(clinicId);
        command.Parameters.AddWithValue(patientId);
        command.Parameters.AddWithValue(beforeId);
        command.Parameters.AddWithValue(limit);
        return await ReadPlanAsync(command);
    }

    private static async Task<(double ExecutionMs, string Nodes)> ReadPlanAsync(NpgsqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using var json = JsonDocument.Parse(reader.GetString(0));
        var plan = json.RootElement[0];
        var nodes = new List<string>();
        AddNode(plan.GetProperty("Plan"), nodes);
        return (plan.GetProperty("Execution Time").GetDouble(), string.Join(" > ", nodes));
    }

    private static void AddNode(JsonElement node, List<string> nodes)
    {
        var label = node.GetProperty("Node Type").GetString()!;
        if (node.TryGetProperty("Index Name", out var index)) label += "[" + index.GetString() + "]";
        nodes.Add(label);
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) AddNode(child, nodes);
    }

    private static async Task<double> MeasureP95Async(Func<Task> operation)
    {
        var observations = new ConcurrentBag<double>();
        for (var round = 0; round < Rounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, ReaderCount).Select(async _ =>
            {
                var timer = Stopwatch.StartNew();
                await operation();
                observations.Add(timer.Elapsed.TotalMilliseconds);
            }));
        }
        var ordered = observations.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];
    }

    private static async Task<string> ServerVersionAsync(NpgsqlDataSource db)
    {
        await using var command = db.CreateCommand("SHOW server_version;");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CleanupAsync(NpgsqlDataSource db, Guid tenantId)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var table in new[] { "analysis.doctor_reviews", "analysis.analysis_results", "analysis.jobs", "analysis.images", "analysis.analysis_requests" })
        {
            await using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE tenant_id=@tenant_id;", connection, transaction);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static Guid FixtureId(string value)
    {
        var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
        return Guid.Parse($"{hash[..8]}-{hash[8..12]}-{hash[12..16]}-{hash[16..20]}-{hash[20..32]}");
    }

    private sealed class ProfileFixture(Guid tenantId, Guid clinicId, Guid patientId) : IAnalyticsAccessResolver
    {
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(request.ResourceScope == "PATIENT"
                ? new AnalyticsAccessGrant(tenantId, clinicId, patientId, null, 1)
                : new AnalyticsAccessGrant(tenantId, clinicId, null, null, 1));
        }
    }
}
