using System.Text.Json;
using Aura.Worker.Analysis.Data;
using Aura.Worker.Analysis.Data.Repositories;
using Aura.Worker.Analysis.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Aura.Worker.Analysis.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADR005_TEST_POSTGRES")))
            Skip="Set ADR005_TEST_POSTGRES to the migrated isolated PostgreSQL database.";
    }
}

public sealed class PostgresWorkerTests
{
    private static string Admin => Environment.GetEnvironmentVariable("ADR005_TEST_POSTGRES")!;
    private static WorkerDbContext Database()
    {
        // Exercise runtime privileges, not postgres superuser privileges, for all production repositories.
        var cs=new NpgsqlConnectionStringBuilder(Admin) { Options="-c role=aura_analysis_app" };
        return new(new DbContextOptionsBuilder<WorkerDbContext>().UseNpgsql(cs.ConnectionString).Options);
    }
    private sealed record Fixture(Guid Tenant,Guid Request,Guid Parent,Guid Image,Guid Job,Guid Patient,Guid Actor);
    private static async Task Execute(string sql,params object[] args)
    {
        await using var c=new NpgsqlConnection(Admin);await c.OpenAsync();
        await using var command=new NpgsqlCommand(sql,c);
        foreach(var arg in args)command.Parameters.AddWithValue(arg);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<Fixture> Seed()
    {
        var f=new Fixture(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        await Execute("""
            INSERT INTO analysis.batches(id,tenant_id,owner_id,requester_id,patient_id,total_images)
            VALUES($1,$2,$3,$3,$4,1);
            """,f.Parent,f.Tenant,f.Actor,f.Patient);
        await Execute("""
            INSERT INTO analysis.analysis_requests(id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,
              request_fingerprint,request_schema_version,accepted_response,correlation_id)
            VALUES($1,$2,$3,$3,$4,$5,$6,'0.6','{}','worker-regression');
            """,f.Request,f.Tenant,f.Actor,f.Patient,Guid.NewGuid(),new string('a',64));
        await Execute("""
            INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,mime_type,
              size_bytes,width,height,sha256,processing_status,batch_id)
            VALUES($1,$2,$3,$4,'FUNDUS','left',$5,'image/png',100,1,1,$6,'QUEUED',$7);
            """,f.Image,f.Tenant,f.Request,Guid.NewGuid(),$"regression/{f.Image:D}",new string('b',64),f.Parent);
        await Execute("""
            INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,correlation_id)
            VALUES($1,$2,$3,$4,1,'QUEUED','worker-regression');
            """,f.Job,f.Tenant,f.Image,Guid.NewGuid());
        return f;
    }
    private static IReadOnlyList<AnnotationDescriptor> Annotations(Fixture f)
    {
        var bytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jkioAAAAASUVORK5CYII=");
        return new[]{"MASK","OVERLAY"}.Select(type=>AnnotationMetadata.FromPng(Guid.NewGuid(),type,
            $"{f.Tenant:D}/{f.Request:D}/{f.Image:D}/{type.ToLowerInvariant()}-{Guid.NewGuid():D}.png",bytes)).ToArray();
    }
    private static string Result => JsonSerializer.Serialize(new {version=999,riskAvailable=false,riskLevel=(string?)null,
        riskScore=(double?)null,confidenceScore=(double?)null,isMock=false,modelVersion="fixture-model",configVersion="fixture-config",
        thresholdVersion="fixture-threshold",processingTimeMs=123.5,limitations="Research fixture",metrics=new{vesselDensity=.1},
        warnings=new[]{new{code="RISK_UNAVAILABLE",message="Research fixture"}}});

    [PostgresFact]
    public async Task Twenty_workers_contend_for_one_job_and_stale_fence_protects_terminal_result()
    {
        var f=await Seed();
        // Each contender has an independent DbContext, matching separate worker processes.
        var claims=await Task.WhenAll(Enumerable.Range(0,20).Select(async i =>
        {
            await using var contender=Database();
            var repository=new JobRepository(contender,NullLogger<JobRepository>.Instance);
            await repository.GetJobAsync(f.Job,default); // Preserve the tracked-entity regression check.
            return await repository.TryAcquireLeaseAsync(f.Job,$"worker-{i:D2}",TimeSpan.FromMinutes(1),default);
        }));
        var old=Assert.Single(claims,l=>l is not null)!;
        await using var first=Database();
        await using var second=Database();
        var repoA=new JobRepository(first,NullLogger<JobRepository>.Instance);
        var repoB=new JobRepository(second,NullLogger<JobRepository>.Instance);
        await Execute("UPDATE analysis.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=$1;",f.Job);
        var current=await repoB.TryAcquireLeaseAsync(f.Job,"worker-new",TimeSpan.FromMinutes(1),default);
        Assert.NotNull(current);Assert.True(current.FencingToken>old.FencingToken);
        var annotation=Annotations(f);
        var ar=new AnalysisRepository(first);
        await Assert.ThrowsAsync<JobLeaseLostException>(()=>ar.CommitSuccessAsync(f.Image,old,Result,annotation,
            OutboxEventFactory.CreateCompletedEvent(f.Tenant,f.Image,f.Patient,"worker-regression","fixture-model","fixture-config","fixture-threshold"),default));
        Assert.Null(await repoA.RenewLeaseAsync(old,TimeSpan.FromMinutes(1),default));
        await ar.CommitSuccessAsync(f.Image,current,Result,annotation,
            OutboxEventFactory.CreateCompletedEvent(f.Tenant,f.Image,f.Patient,"worker-regression","fixture-model","fixture-config","fixture-threshold"),default);
        await using var c=new NpgsqlConnection(Admin);await c.OpenAsync();
        await using var cmd=new NpgsqlCommand("""
            SELECT r.version,a.size_bytes,a.sha256,a.width,a.height
            FROM analysis.analysis_results r JOIN analysis.annotation_refs a ON a.analysis_result_id=r.id
            WHERE r.image_id=$1 ORDER BY a.type;
            """,c);cmd.Parameters.AddWithValue(f.Image);
        await using(var reader=await cmd.ExecuteReaderAsync())
        {
            var rows=0;while(await reader.ReadAsync()) {
                Assert.Equal(1,reader.GetInt32(0));Assert.Equal(annotation[0].SizeBytes,reader.GetInt64(1));
                Assert.Equal(annotation[0].Sha256,reader.GetString(2));Assert.Equal(1,reader.GetInt32(3));Assert.Equal(1,reader.GetInt32(4));rows++;
            }Assert.Equal(2,rows);
        }
        await AssertOutbox(c,f,2);
    }

    private static async Task AssertOutbox(NpgsqlConnection c,Fixture f,int expected)
    {
        await using var cmd=new NpgsqlCommand("SELECT id,tenant_id,event_type,schema_version,payload::text,occurred_at FROM analysis.outbox_events WHERE tenant_id=$1;",c);
        cmd.Parameters.AddWithValue(f.Tenant);await using var reader=await cmd.ExecuteReaderAsync();var count=0;
        while(await reader.ReadAsync()) {
            var row=new AURA.Analysis.Api.Outbox.OutboxRecord(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),new DateTimeOffset(reader.GetDateTime(5)),0);
            var mapped=AURA.Analysis.Api.Outbox.OutboxMessageMapper.Map(row,new());Assert.True(mapped.Ok,mapped.ErrorCode);count++;
        }Assert.Equal(expected,count);
    }

    [PostgresFact]
    public async Task Retry_is_atomic_fenced_and_persists_actual_message_identity_and_not_before()
    {
        var f=await Seed();await using var db=Database();var jobs=new JobRepository(db,NullLogger<JobRepository>.Instance);
        var original=(await jobs.GetJobAsync(f.Job,default))!;
        var lease=(await jobs.TryAcquireLeaseAsync(f.Job,"retry-worker",TimeSpan.FromMinutes(1),default))!;
        var next=await jobs.CreateNextAttemptJobAsync(original,2,default);
        var message=OutboxEventFactory.CreateRetryJobMessage(f.Tenant,f.Parent,f.Image,next.JobId,Guid.NewGuid(),2,"worker-regression",next.MessageId);
        await new AnalysisRepository(db).ScheduleRetryAsync(f.Image,lease,next,TimeSpan.FromSeconds(30),message,default);
        await using var c=new NpgsqlConnection(Admin);await c.OpenAsync();
        await using var command=new NpgsqlCommand("SELECT j.message_id,j.correlation_id,o.next_publish_at FROM analysis.jobs j JOIN analysis.outbox_events o ON o.id=j.message_id WHERE j.id=$1;",c);
        command.Parameters.AddWithValue(next.JobId);await using(var reader=await command.ExecuteReaderAsync()) {
            Assert.True(await reader.ReadAsync());Assert.Equal(next.MessageId,reader.GetGuid(0));
            Assert.Equal("worker-regression",reader.GetString(1));Assert.True(reader.GetDateTime(2)>DateTime.UtcNow.AddSeconds(15));
        }
        await AssertOutbox(c,f,1);
    }

    [PostgresFact]
    public async Task Closed_admission_and_low_quality_complete_durably_once_without_Redis()
    {
        var f=await Seed();await Execute("UPDATE analysis.batches SET admission_complete=false WHERE id=$1;",f.Parent);
        await using var db=Database();var jobs=new JobRepository(db,NullLogger<JobRepository>.Instance);
        var lease=(await jobs.TryAcquireLeaseAsync(f.Job,"quality-worker",TimeSpan.FromMinutes(1),default))!;
        await new AnalysisRepository(db).CommitFailureAsync(f.Image,lease,"LOW_QUALITY","{}",
            OutboxEventFactory.CreateFailedEvent(f.Tenant,f.Image,f.Patient,"worker-regression","ERR_IMAGE_LOW_QUALITY","LOW_QUALITY"),default);
        await using var c=new NpgsqlConnection(Admin);await c.OpenAsync();
        await using(var before=new NpgsqlCommand("SELECT count(*) FROM analysis.batch_completion_events WHERE batch_id=$1;",c)) {
            before.Parameters.AddWithValue(f.Parent);Assert.Equal(0L,await before.ExecuteScalarAsync());
        }
        await using(var beforeStatus=new NpgsqlCommand("SELECT status FROM analysis.batches WHERE id=$1;",c)) {
            beforeStatus.Parameters.AddWithValue(f.Parent);Assert.Equal("IN_PROGRESS",await beforeStatus.ExecuteScalarAsync());
        }
        await Execute("UPDATE analysis.batches SET admission_complete=true WHERE id=$1;",f.Parent);
        await Execute("SELECT analysis.ensure_batch_completion($1,$2);",f.Tenant,f.Parent);
        await AssertOutbox(c,f,2);
        await using(var afterStatus=new NpgsqlCommand("SELECT status FROM analysis.batches WHERE id=$1;",c)) {
            afterStatus.Parameters.AddWithValue(f.Parent);Assert.Equal("FAILED",await afterStatus.ExecuteScalarAsync());
        }
        var state=await new AnalysisRepository(db).CheckBatchCompletionAsync(f.Parent,default);
        Assert.NotNull(state);Assert.True(state.IsFinished);Assert.Equal("FAILED",state.BatchStatus);Assert.Equal(1,state.Failed);
    }

    [PostgresFact]
    public async Task Mixed_terminal_batch_counts_duplicate_as_success_and_low_quality_as_failure()
    {
        var first=await Seed();
        // A duplicate reuses the first case, and LOW_QUALITY is terminal even without Redis.
        await Execute("UPDATE analysis.images SET processing_status='COMPLETED' WHERE id=$1;",first.Image);
        // For FK tenant isolation use additional images in the same first request instead of crossing tenants.
        var dupId=Guid.NewGuid();var badId=Guid.NewGuid();
        await Execute("""
            INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,mime_type,
              size_bytes,width,height,sha256,processing_status,batch_id,existing_analysis_id)
            VALUES($1,$2,$3,$4,'FUNDUS','left',$5,'image/png',100,1,1,$6,'DUPLICATE',$7,$8);
            """,dupId,first.Tenant,first.Request,Guid.NewGuid(),$"regression/{dupId}",new string('c',64),first.Parent,first.Image);
        await Execute("""
            INSERT INTO analysis.images(id,tenant_id,analysis_request_id,file_id,modality,eye_side,object_key,mime_type,
              size_bytes,width,height,sha256,processing_status,batch_id)
            VALUES($1,$2,$3,$4,'FUNDUS','left',$5,'image/png',100,1,1,$6,'LOW_QUALITY',$7);
            """,badId,first.Tenant,first.Request,Guid.NewGuid(),$"regression/{badId}",new string('d',64),first.Parent);
        await Execute("SELECT analysis.ensure_batch_completion($1,$2);",first.Tenant,first.Parent);
        await using var db=Database();var state=await new AnalysisRepository(db).CheckBatchCompletionAsync(first.Parent,default);
        Assert.NotNull(state);Assert.Equal("PARTIAL_FAILED",state.BatchStatus);Assert.Equal(2,state.Completed);Assert.Equal(1,state.Failed);
        await using var c=new NpgsqlConnection(Admin);await c.OpenAsync();await AssertOutbox(c,first,1);
    }
}
