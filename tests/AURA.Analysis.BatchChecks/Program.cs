using System.Text.Json;
using System.Text.RegularExpressions;
using AURA.Analysis.Api.Upload;
using Npgsql;

namespace AURA.Analysis.BatchChecks;

internal static class Program
{
    private static string _current = "preflight";
    private static int _passed;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Check(string name, Func<Task> action)
    {
        _current = name;
        await action();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static IdempotencyScope Scope(Guid? tenant = null, Guid? requester = null, Guid? key = null) =>
        new(tenant ?? Guid.NewGuid(), requester ?? Guid.NewGuid(), key ?? Guid.NewGuid());

    private static BatchPlan Plan(IdempotencyScope scope, string fingerprint, Guid? patient = null)
    {
        var batch = Guid.NewGuid(); var image = Guid.NewGuid(); var file = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow; var job = new JobPlan(Guid.NewGuid(), Guid.NewGuid(), "repo-check");
        var response = new AcceptedResponse(batch, new[] { new AcceptedItem(image, "synthetic.png", "QUEUED", null) },
            now.UtcDateTime, $"/api/v1/batches/{batch:D}");
        var images = new[] { new ImagePlan(image, file, $"{scope.TenantId:D}/{batch:D}/{image:D}", "synthetic.png",
            "image/png", 100, 10, 10, new string('b',64), "FUNDUS", "left", "QUEUED", null, job) };
        var payload = JsonSerializer.Serialize(new { analysisId = image });
        var outbox = new[] { new OutboxPlan(Guid.NewGuid(), image, "analysis.requested", "0.6", payload, now),
            new OutboxPlan(job.MessageId, image, "analysis.job.run", "0.6", payload, now) };
        return new(batch, scope.TenantId, scope.RequesterId, scope.RequesterId, patient ?? Guid.NewGuid(), scope.Key,
            fingerprint, RequestFingerprint.Version, "repo-check", now, response, images, outbox);
    }

    private static async Task<long> Scalar(NpgsqlDataSource source, string sql, params object[] parameters)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in parameters) command.Parameters.AddWithValue(value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task<int> Main()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("AURA_ANALYSIS_BATCH_TEST_CONNECTION");
            Require(!string.IsNullOrWhiteSpace(raw), "Use the guarded Python runner to create an isolated test database first.");
            var options = new NpgsqlConnectionStringBuilder(raw);
            Require(options.Host is "127.0.0.1" or "localhost", "Test host must be loopback.");
            Require(options.Username == "aura_analysis_app", "Runtime credentials are required.");
            Require(Regex.IsMatch(options.Database ?? "", "^aura_m2_batch_[0-9a-f]{32}$"), "Refusing a non-test database.");
            options.IncludeErrorDetail = false;
            options.Timeout = 5; options.CommandTimeout = 10; options.MaxPoolSize = 16;
            await using var source = NpgsqlDataSource.Create(options.ConnectionString);
            await using (var command = source.CreateCommand("""
                SELECT current_user, shobj_description(d.oid,'pg_database'),
                       current_setting('server_version_num')::int, r.rolsuper, r.rolcreatedb, r.rolcreaterole
                FROM pg_database d JOIN pg_roles r ON r.rolname=current_user WHERE d.datname=current_database();
                """))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Require(await reader.ReadAsync(), "Missing database metadata.");
                Require(reader.GetString(0) == "aura_analysis_app", "Wrong database principal.");
                Require(!reader.IsDBNull(1) && reader.GetString(1) == "aura-m2-batch-check:" + options.Database![14..], "Missing test database marker.");
                Require(reader.GetInt32(2) / 10000 == 17, "PostgreSQL 17 is required.");
                Require(!reader.GetBoolean(3) && !reader.GetBoolean(4) && !reader.GetBoolean(5), "Runtime role has admin privileges.");
            }
            const string allRows = """
                SELECT (SELECT count(*) FROM analysis.analysis_requests)+(SELECT count(*) FROM analysis.images)
                 +(SELECT count(*) FROM analysis.jobs)+(SELECT count(*) FROM analysis.outbox_events)
                 +(SELECT count(*) FROM analysis.analysis_results)+(SELECT count(*) FROM analysis.annotation_refs)
                 +(SELECT count(*) FROM analysis.doctor_reviews)+(SELECT count(*) FROM analysis.audit_logs);
                """;
            Require(await Scalar(source, allRows) == 0, "Test schema must start empty.");
            Console.WriteLine("PASS: isolated PostgreSQL 17 database and actual runtime login verified.");
            var writer = new PostgresUploadRepository(source);
            var repository = new PostgresBatchStatusRepository(source);
            var scope = Scope(); var plan = Plan(scope, new string('a', 64)) with { ClinicId = Guid.NewGuid() };
            await Commit(writer, scope, plan);
            var identity = new BatchIdentity(plan.BatchId, plan.TenantId, plan.PatientId, plan.OwnerId);

            await Check("Queued batch preserves upload order, filename, UTC timestamp and job counts", async () =>
            {
                Require(await repository.FindIdentityAsync(plan.BatchId, default) == identity, "Wrong resource binding.");
                var snapshot = await repository.ReadAsync(identity, default);
                Require(snapshot is not null && snapshot.CreatedAt.Kind == DateTimeKind.Utc, "Missing UTC snapshot.");
                var view = BatchStatusProjection.Create(snapshot!);
                Require(view.BatchId == plan.BatchId && view.Items[0].AnalysisId == plan.Images[0].AnalysisId
                    && view.Items[0].FileName == "synthetic.png" && view.BatchStatus == "IN_PROGRESS", "Wrong queued view.");
                Require(view.Summary.Total == 1 && view.Summary.Active == 1 && view.Summary.ByStatus["QUEUED"] == 1
                    && view.Items[0].Job?.AttemptNo == 1 && view.Items[0].Job?.RetryCount == 0, "Wrong queue summary.");
            });
            await Check("Unknown and mismatched tenant, patient or owner bindings return no batch", async () =>
            {
                Require(await repository.FindIdentityAsync(Guid.NewGuid(), default) is null, "Unknown batch found.");
                Require(await repository.FindIdentityAsync(Guid.Empty, default) is null, "Empty ID found.");
                foreach (var changed in new[] { identity with { TenantId=Guid.NewGuid() }, identity with { PatientId=Guid.NewGuid() },
                    identity with { OwnerId=Guid.NewGuid() }, identity with { BatchId=Guid.NewGuid() } })
                    Require(await repository.ReadAsync(changed, default) is null, "Cross-scope batch data leaked.");
            });
            var secondJob = Guid.NewGuid();
            await Check("Highest attempt wins even with an older created_at; retryable FAILED counts as active", async () =>
            {
                await Execute(source, """
                    INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,retryable,error_code,error_details,correlation_id,created_at)
                    VALUES($1,$2,$3,$4,2,'FAILED',true,'ERR_TIMEOUT','{"message":"PRIVATE_DIAGNOSTIC","objectKey":"PRIVATE_PATH"}'::jsonb,'batch-check',now()-interval '1 day');
                    """, secondJob, plan.TenantId, plan.Images[0].AnalysisId, Guid.NewGuid());
                await Execute(source, "UPDATE analysis.images SET processing_status='FAILED' WHERE tenant_id=$1 AND id=$2;", plan.TenantId, plan.Images[0].AnalysisId);
                var view = BatchStatusProjection.Create((await repository.ReadAsync(identity, default))!);
                Require(view.Items[0].Job?.JobId == secondJob && view.Items[0].Job?.AttemptNo == 2
                    && view.Items[0].Job?.RetryCount == 1 && view.Items[0].Error?.RetryCount == 1
                    && view.Items[0].Error?.Retryable == true && view.Items[0].Error?.Code == "ERR_TIMEOUT", "Wrong latest attempt.");
                Require(view.BatchStatus == "IN_PROGRESS" && view.Summary.Active == 1 && view.Summary.Failed == 0
                    && view.Summary.ByStatus["FAILED"] == 1, "Retryable failure was counted as terminal.");
            });
            await Check("Dead-letter attempt is terminal despite a stale retryable flag", async () =>
            {
                await Execute(source, "UPDATE analysis.jobs SET status='DEAD_LETTER' WHERE id=$1;", secondJob);
                var view = BatchStatusProjection.Create((await repository.ReadAsync(identity, default))!);
                Require(view.BatchStatus == "FAILED" && view.Summary.Active == 0 && view.Summary.Failed == 1
                    && view.Items[0].Error?.Retryable == false, "Dead-letter attempt remained active.");
            });
            var mixedScope = new IdempotencyScope(plan.TenantId, plan.RequesterId, Guid.NewGuid());
            var mixed = MixedPlan(mixedScope, plan);
            await Commit(writer, mixedScope, mixed);
            var mixedIdentity = new BatchIdentity(mixed.BatchId, mixed.TenantId, mixed.PatientId, mixed.OwnerId);
            var completed = mixed.Images.Single(i => i.ProcessingStatus == "COMPLETED");
            await Check("All nine statuses, original order and latest result version produce a consistent summary", async () =>
            {
                foreach (var image in mixed.Images.Where(i => i.Job is not null))
                {
                    var jobStatus = image.ProcessingStatus switch
                    {
                        "PROCESSING" => "RUNNING", "COMPLETED" => "SUCCEEDED", "RETRY_READY" => "RETRY_READY",
                        "FAILED" or "LOW_QUALITY" or "REJECTED" => "FAILED", _ => "QUEUED"
                    };
                    await Execute(source, "UPDATE analysis.jobs SET status=$1,retryable=$2,error_code='ERR_TIMEOUT' WHERE id=$3;",
                        jobStatus, image.ProcessingStatus == "FAILED", image.Job!.JobId);
                }
                await Result(source, mixed, completed, completed.Job!.JobId, 1, "LOW");
                var newerJob = Guid.NewGuid();
                await Execute(source, """
                    INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,correlation_id,created_at)
                    VALUES($1,$2,$3,$4,2,'SUCCEEDED','batch-check',now()-interval '1 day');
                    """, newerJob, mixed.TenantId, completed.AnalysisId, Guid.NewGuid());
                await Result(source, mixed, completed, newerJob, 2, "HIGH");
                var view = BatchStatusProjection.Create((await repository.ReadAsync(mixedIdentity, default))!);
                Require(view.Items.Select(i=>i.AnalysisId).SequenceEqual(mixed.Images.Select(i=>i.AnalysisId)), "Upload item order changed.");
                Require(view.Items.Select(i=>i.FileName).SequenceEqual(mixed.Response.Items.Select(i=>i.FileName)), "Filenames changed.");
                Require(view.BatchStatus == "IN_PROGRESS" && view.Summary.Total == 9 && view.Summary.Active == 5
                    && view.Summary.Succeeded == 1 && view.Summary.Failed == 2 && view.Summary.Duplicate == 1, "Wrong mixed counters.");
                Require(view.Summary.ByStatus.Count == 9 && view.Summary.ByStatus.Values.All(v=>v==1), "Raw status counts changed.");
                var current = view.Items.Single(i=>i.AnalysisId==completed.AnalysisId);
                Require(current.RiskLevel == "HIGH" && current.Job?.JobId == newerJob && current.Job?.RetryCount == 1, "Latest result/attempt not selected.");
                Require(view.Items.Where(i=>i.ProcessingStatus!="COMPLETED").All(i=>i.RiskLevel is null), "Risk leaked to another status.");
                Require(view.Items.Single(i=>i.ProcessingStatus=="DUPLICATE").ExistingAnalysisId==plan.Images[0].AnalysisId
                    && view.Items.Where(i=>i.ProcessingStatus!="DUPLICATE").All(i=>i.ExistingAnalysisId is null),
                    "Duplicate canonical reference missing or leaked to another status.");
                await using var counters = source.CreateCommand("""
                    SELECT total_items,completed_items,failed_images,status,clinic_id
                    FROM analysis.batches WHERE tenant_id=$1 AND id=$2;
                    """);
                counters.Parameters.AddWithValue(mixed.TenantId);
                counters.Parameters.AddWithValue(mixed.BatchId);
                await using var reader = await counters.ExecuteReaderAsync();
                Require(await reader.ReadAsync() && reader.GetInt32(0)==9 && reader.GetInt32(1)==2
                    && reader.GetInt32(2)==2 && reader.GetString(3)=="IN_PROGRESS"
                    && !reader.IsDBNull(4) && reader.GetGuid(4)==mixed.ClinicId,
                    "Durable batch counters did not match the image/job states.");
            });
            await Check("Public JSON has contract count keys and no internal bindings, object keys or job diagnostics", async () =>
            {
                var view = BatchStatusProjection.Create((await repository.ReadAsync(identity, default))!);
                var serialized = JsonSerializer.Serialize(view, UploadJson.Options);
                foreach (var secret in new[] { "PRIVATE_DIAGNOSTIC", "PRIVATE_PATH", "tenantId", "ownerId", "patientId", "objectKey", "errorDetails", "jobRetryable" })
                    Require(!serialized.Contains(secret, StringComparison.Ordinal), "Internal metadata reached public JSON.");
                using var json = JsonDocument.Parse(serialized);
                Require(json.RootElement.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[] {"batchId","batchStatus","createdAt","items","summary"}), "Wrong public root.");
                var summary = json.RootElement.GetProperty("summary");
                Require(summary.GetProperty("byStatus").EnumerateObject().Count()==9 && summary.GetProperty("byStatus").TryGetProperty("FAILED",out _), "Count keys changed case.");
            });
            await Check("Uncommitted state is invisible; a committed image/job transition is read consistently", async () =>
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                foreach (var sql in new[] {
                    "UPDATE analysis.images SET processing_status='FAILED' WHERE tenant_id=$1 AND id=$2;",
                    "UPDATE analysis.jobs SET status='DEAD_LETTER',retryable=false,error_code='ERR_AI_FAILED' WHERE tenant_id=$1 AND image_id=$2 AND attempt_no=2;" })
                {
                    await using var command = new NpgsqlCommand(sql, connection, transaction);
                    command.Parameters.AddWithValue(mixed.TenantId); command.Parameters.AddWithValue(completed.AnalysisId);
                    await command.ExecuteNonQueryAsync();
                }
                var before = (await repository.ReadAsync(mixedIdentity, default))!.Items.Single(i=>i.AnalysisId==completed.AnalysisId);
                Require(before.ProcessingStatus=="COMPLETED" && before.Job?.Status=="SUCCEEDED", "Uncommitted write became visible.");
                await transaction.CommitAsync();
                var after = BatchStatusProjection.Create((await repository.ReadAsync(mixedIdentity, default))!).Items.Single(i=>i.AnalysisId==completed.AnalysisId);
                Require(after.ProcessingStatus=="FAILED" && after.Job?.Status=="DEAD_LETTER" && after.RiskLevel is null, "Committed transition was inconsistent.");
            });
            await Check("Fresh data source reads identical data; canceled reads propagate and do not change any Analysis row", async () =>
            {
                var before = await DatabaseDigest(source);
                var old = JsonSerializer.Serialize(BatchStatusProjection.Create((await repository.ReadAsync(mixedIdentity, default))!), UploadJson.Options);
                await using var freshSource = NpgsqlDataSource.Create(options.ConnectionString);
                var fresh = new PostgresBatchStatusRepository(freshSource);
                var reopened = JsonSerializer.Serialize(BatchStatusProjection.Create((await fresh.ReadAsync(mixedIdentity, default))!), UploadJson.Options);
                Require(old==reopened, "Fresh data source changed the public response.");
                using var cts = new CancellationTokenSource(); cts.Cancel();
                try { await repository.FindIdentityAsync(plan.BatchId, cts.Token); throw new InvalidOperationException("Identity cancellation ignored."); }
                catch (OperationCanceledException) { }
                try { await repository.ReadAsync(identity, cts.Token); throw new InvalidOperationException("Read cancellation ignored."); }
                catch (OperationCanceledException) { }
                Require(before==await DatabaseDigest(source), "A read operation changed database rows.");
            });
            Require(_passed==8,"Missing batch status checks.");
            Console.WriteLine("RESULT: 8/8 PostgreSQL batch-status groups passed with aura_analysis_app.");
            return 0;
        }
        catch(Exception ex)
        {
            var code=ex is PostgresException pg ? "; SQLSTATE="+pg.SqlState : ex is UploadFailure u ? "; code="+u.Code : "";
            Console.Error.WriteLine($"STOP: {_current}; {ex.GetType().Name}{code}. Test database retained by runner on failure.");
            return 1;
        }
    }
    private static async Task Execute(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command=source.CreateCommand(sql);
        foreach(var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task Commit(PostgresUploadRepository repository, IdempotencyScope scope, BatchPlan plan)
    {
        await using var session=await repository.BeginAsync(scope,plan.Fingerprint,default);
        Require(session.State==IdempotencyState.New,"Fixture is not new.");
        await session.CommitAsync(plan,default);
    }
    private static BatchPlan MixedPlan(IdempotencyScope scope, BatchPlan original)
    {
        var plan=Plan(scope,new string('c',64),original.PatientId);
        var images=new List<ImagePlan>();var accepted=new List<AcceptedItem>();var events=new List<OutboxPlan>();
        foreach(var state in BatchStatusProjection.ProcessingStatuses)
        {
            var id=Guid.NewGuid();var file=Guid.NewGuid();
            JobPlan? job=state=="DUPLICATE" ? null : new(Guid.NewGuid(),Guid.NewGuid(),"batch-check");
            var image=plan.Images[0] with
            {
                AnalysisId=id,FileId=file,ObjectKey=$"{plan.TenantId:D}/{plan.BatchId:D}/{id:D}",FileName=state+".png",
                Sha256=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(id.ToByteArray())),
                ProcessingStatus=state,ExistingAnalysisId=state=="DUPLICATE" ? original.Images[0].AnalysisId : null,Job=job
            };
            images.Add(image);
            accepted.Add(new(id,image.FileName,state,state=="DUPLICATE" ? new("ERR_DUPLICATE_REQUEST","synthetic duplicate",false) : null,image.ExistingAnalysisId));
            if(job is not null)
            {
                var payload=JsonSerializer.Serialize(new {analysisId=id});
                events.Add(new(Guid.NewGuid(),id,"analysis.requested","0.6",payload,plan.CreatedAt));
                events.Add(new(job.MessageId,id,"analysis.job.run","0.6",payload,plan.CreatedAt));
            }
        }
        return plan with {Images=images,Outbox=events,Response=plan.Response with {Items=accepted}};
    }
    private static Task Result(NpgsqlDataSource source, BatchPlan plan, ImagePlan image, Guid job, int version, string level) =>
        Execute(source,"""
            INSERT INTO analysis.analysis_results(id,tenant_id,image_id,job_id,version,result_schema_version,risk_assessment,metrics,
                is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations,created_at)
            VALUES($1,$2,$3,$4,$5,'0.6',jsonb_build_object('riskLevel',$6::text),'{}'::jsonb,true,'test','test','test',1,'synthetic',now()-($5::int*interval '1 hour'));
            """,Guid.NewGuid(),plan.TenantId,image.AnalysisId,job,version,level);
    private static async Task<string> DatabaseDigest(NpgsqlDataSource source)
    {
        var tables=new[] {"analysis_requests","images","jobs","outbox_events","analysis_results","annotation_refs","doctor_reviews","audit_logs"};
        var expressions=tables.Select(t=>$"coalesce((SELECT jsonb_agg(to_jsonb(t) ORDER BY id)::text FROM analysis.{t} t),'[]')");
        await using var command=source.CreateCommand("SELECT md5("+string.Join(" || ",expressions)+");");
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
