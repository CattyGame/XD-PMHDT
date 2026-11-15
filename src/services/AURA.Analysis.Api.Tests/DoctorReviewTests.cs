using AURA.Analysis.Api.Upload;
using Npgsql;
using Xunit;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;

namespace AURA.Analysis.Api.Tests;

public sealed class DoctorReviewTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING");

    [PostgresFact]
    public async Task Twenty_HTTP_reviews_use_real_endpoint_and_database_with_one_winner_and_immutable_AI()
    {
        await using var db=NpgsqlDataSource.Create(ConnectionString!);
        var tenant=Guid.NewGuid();var clinic=Guid.NewGuid();var patient=Guid.NewGuid();var doctor=Guid.NewGuid();
        var requestId=Guid.NewGuid();var image=Guid.NewGuid();var resultId=Guid.NewGuid();
        await SeedCompletedAnalysis(db,tenant,patient,clinic,requestId,image,Guid.NewGuid(),resultId);
        async Task<string> ResultJson()
        {
            await using var sql=db.CreateCommand("SELECT row_to_json(r)::text FROM analysis.analysis_results r WHERE id=$1");
            sql.Parameters.AddWithValue(resultId);return (string)(await sql.ExecuteScalarAsync())!;
        }
        var before=await ResultJson();
        var policy=new ReviewAccess(tenant,clinic,patient,doctor);
        var query=new PostgresAnalysisQueryRepository(db);
        var builder=WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAnalysisUserIdentityResolver>(new ReviewIdentity(doctor));
        builder.Services.AddSingleton(new DoctorReviewService(query,policy,new PostgresDoctorReviewRepository(db)));
        await using var app=builder.Build();app.MapAnalysisEndpoints();await app.StartAsync();
        using var client=app.GetTestClient();client.DefaultRequestHeaders.Authorization=new("Bearer","review-fixture");
        var url=$"/api/v1/doctor/analyses/{image:D}/reviews?clinicContextId={clinic:D}";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var responses=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>client.PostAsJsonAsync(url,
            new DoctorReviewRequest(true,0,DoctorNotes:"HTTP concurrency fixture"),deadline.Token)));
        try
        {
            Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Created);
            Assert.Equal(19,responses.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
            foreach(var response in responses.Where(r=>r.StatusCode==HttpStatusCode.Conflict))
                Assert.Contains("ERR_CONCURRENT_REVIEW",await response.Content.ReadAsStringAsync());
        }
        finally {foreach(var response in responses)response.Dispose();}
        Assert.Equal(before,await ResultJson());
        await using var count=db.CreateCommand("SELECT count(*) FROM analysis.doctor_reviews WHERE image_id=$1");
        count.Parameters.AddWithValue(image);Assert.Equal(1L,await count.ExecuteScalarAsync());
        policy.Allowed=false;
        using var denied=await client.PostAsJsonAsync(url,new DoctorReviewRequest(true,1),deadline.Token);
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        Assert.Equal(1L,await count.ExecuteScalarAsync());
    }

    // Only external identity/Profile authority are fixtures; routing, review service and PostgreSQL are real.
    private sealed class ReviewIdentity(Guid doctor):IAnalysisUserIdentityResolver
    {
        public Task<AnalysisUserContext> ResolveAsync(HttpRequest request,CancellationToken ct)
            =>request.Headers.Authorization=="Bearer review-fixture"
                ?Task.FromResult(new AnalysisUserContext(doctor,["DOCTOR"]))
                :throw new UploadFailure(401,"ERR_UNAUTHORIZED","Fixture bearer required.");
    }
    private sealed class ReviewAccess(Guid tenant,Guid clinic,Guid patient,Guid doctor):IPatientAccessResolver
    {
        public bool Allowed=true;
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request,CancellationToken ct)
            =>Task.FromResult(new AccessDecision(Allowed && request.RequesterId==doctor && request.ClinicId==clinic && request.PatientId==patient,
                tenant,"CLINIC",clinic,$"CLINIC:{clinic:D}",1));
    }

    [PostgresFact]
    public async Task Concurrent_reviews_commit_once_and_reject_stale_versions_without_deadlock()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var tenant = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var resultId = Guid.NewGuid();
        await SeedCompletedAnalysis(dataSource, tenant, patient, owner, requestId, analysisId, jobId, resultId);

        var repository = new PostgresDoctorReviewRepository(dataSource);
        var worklist = new PostgresAnalysisQueryRepository(dataSource);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var worklists = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            worklist.ListDoctorWorklistAsync(tenant, patient, timeout.Token)));
        Assert.All(worklists, rows => Assert.Contains(rows, row => row.AnalysisId == analysisId));
        var attempts = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            try
            {
                var response = await repository.SubmitAsync(new DoctorReviewCommand(analysisId, tenant, patient,
                    Guid.NewGuid(), 0, true, null, null, $"concurrent review {i}", null, $"scrum345-{requestId:N}"), timeout.Token);
                return (Success: true, Error: (string?)null, Version: response.Version);
            }
            catch (UploadFailure error) when (error.Status == 409)
            {
                return (Success: false, Error: error.Code, Version: -1);
            }
        }, timeout.Token)).ToArray();

        var outcomes = await Task.WhenAll(attempts);
        Assert.Single(outcomes, x => x.Success);
        Assert.Equal(19, outcomes.Count(x => x.Error == "ERR_CONCURRENT_REVIEW"));
        Assert.All(outcomes.Where(x => x.Success), x => Assert.Equal(1, x.Version));

        await using var check = dataSource.CreateCommand("""
            SELECT i.row_version,
                   (SELECT count(*) FROM analysis.doctor_reviews r WHERE r.image_id=i.id),
                   (SELECT count(*) FROM analysis.audit_logs a WHERE a.image_id=i.id)
            FROM analysis.images i WHERE i.id=$1;
            """);
        check.Parameters.AddWithValue(analysisId);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }

    [PostgresFact]
    public async Task Approved_review_persists_only_the_doctor_supplied_icdr_grade()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        var tenant = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var resultId = Guid.NewGuid();
        await SeedCompletedAnalysis(dataSource, tenant, patient, owner, requestId, analysisId, jobId, resultId);

        var review = await new PostgresDoctorReviewRepository(dataSource).SubmitAsync(new DoctorReviewCommand(
            analysisId, tenant, patient, doctor, 0, true, null, null, "approved note", "MILD_NPDR", "scrum368-test"), default);

        Assert.Equal("MILD_NPDR", review.IcdrLevel);
        await using var command = dataSource.CreateCommand("SELECT icdr_level FROM analysis.doctor_reviews WHERE id=$1;");
        command.Parameters.AddWithValue(review.ReviewId);
        Assert.Equal("MILD_NPDR", await command.ExecuteScalarAsync());
    }

    private static async Task SeedCompletedAnalysis(NpgsqlDataSource dataSource, Guid tenant, Guid patient,
        Guid owner, Guid requestId, Guid analysisId, Guid jobId, Guid resultId)
    {
        var accepted = System.Text.Json.JsonSerializer.Serialize(new
        {
            batchId = requestId,
            items = new[] { new { analysisId, fileName = "scrum345.png", processingStatus = "COMPLETED" } },
            createdAt = DateTime.UtcNow,
            statusUrl = $"/api/v1/batches/{requestId:D}"
        });
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.analysis_requests
                (id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,request_fingerprint,
                 request_schema_version,accepted_response,correlation_id)
            VALUES ($1,$2,$3,$3,$4,$5,repeat('a',64),'0.8.0',$6::jsonb,$7);
            """, requestId, tenant, owner, patient, Guid.NewGuid(), accepted, $"scrum345-{requestId:N}");
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.images
                (id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,mime_type,size_bytes,
                 width,height,sha256,processing_status,review_status)
            VALUES ($1,$2,$3,$4,'FUNDUS','left',$5,'image/png',64,8,8,repeat('b',64),'COMPLETED','PENDING');
            """, analysisId, tenant, requestId, Guid.NewGuid(), $"scrum345/{requestId:N}/image.png");
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.jobs (id,tenant_id,image_id,message_id,attempt_no,status,correlation_id)
            VALUES ($1,$2,$3,$4,1,'SUCCEEDED',$5);
            """, jobId, tenant, analysisId, Guid.NewGuid(), $"scrum345-{requestId:N}");
        await ExecuteAsync(dataSource, """
            INSERT INTO analysis.analysis_results
                (id,tenant_id,image_id,job_id,version,result_schema_version,risk_assessment,metrics,warnings,
                 is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations)
            VALUES ($1,$2,$3,$4,1,'0.8.0','{"riskLevel":"LOW"}'::jsonb,'{}'::jsonb,'[]'::jsonb,
                    true,'test','test','test',1,'synthetic integration fixture');
            """, resultId, tenant, analysisId, jobId);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
}
