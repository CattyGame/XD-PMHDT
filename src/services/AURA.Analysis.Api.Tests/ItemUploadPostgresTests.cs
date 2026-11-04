using System.Security.Cryptography;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Upload;
using AURA.Common.Cache;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

/// <summary>Real admission/journal/acceptance PostgreSQL with explicit external Billing/Blob test doubles.</summary>
public sealed class ItemUploadPostgresTests
{
    [PostgresFact]
    public async Task Background_reconciliation_recovers_unknown_acceptance_without_client_or_second_reserve()
    {
        await using var f=new Fixture(loseResponse:true);var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"recover.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"background-reconcile",default));
        Assert.Equal(1,await f.Service.ReconcilePendingAsync(default,parent.BatchId));
        Assert.Equal("FINALIZED",(await f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default)).Status);
        Assert.Equal(1,f.Billing.ReserveCalls);Assert.Empty(f.Billing.Released);
        var resolver=new PostgresInternalResolversRepository(f.Db);
        var state=await resolver.GetStateAsync(Assert.Single((await f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default)).Items).AnalysisId!.Value,f.Tenant,default);
        Assert.NotNull(state);Assert.Equal(f.Billing.ByKey.Values.Single(),state.ReservationId);Assert.Equal(1,state.Generation);
    }
    private sealed class Access(Guid tenant,Guid actor,Guid clinic) : IPatientAccessResolver
    {
        public bool Allowed=true;
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request,CancellationToken ct) =>
            Task.FromResult(new AccessDecision(Allowed && request.RequesterId==actor && request.ClinicId==clinic,
                tenant,"CLINIC",clinic,$"CLINIC:{clinic:D}",1));
    }
    private sealed class Progress : IBatchRedisProgressService
    {
        public Task InitializeBatchAsync(Guid batchId,int count,TimeSpan? expiry=null)=>Task.CompletedTask;
        public Task<BatchProgressSnapshot?> GetProgressAsync(Guid id)=>Task.FromResult<BatchProgressSnapshot?>(null);
        public Task RebuildBatchAsync(Guid id,int total,IReadOnlyList<BatchImageOutcome> outcomes,string status,TimeSpan? expiry=null)=>Task.CompletedTask;
        public Task<BatchImageCompletionResult> RecordImageOutcomeAsync(Guid batch,Guid image,bool success)=>
            Task.FromResult(new BatchImageCompletionResult(batch,1,success?1:0,success?0:1,0,true,success?"COMPLETED":"FAILED"));
    }
    private sealed class CommitThenTimeout(IAnalysisUploadHandler inner) : IAnalysisUploadHandler
    {
        public bool LoseNextResponse=true;
        public async Task<UploadResult> CreateAsync(UploadCommand command,CancellationToken ct)
        {
            var result=await inner.CreateAsync(command,ct);
            if(LoseNextResponse){LoseNextResponse=false;throw new IOException("Fixture: response lost after DB acceptance committed.");}
            return result;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Guid Actor=Guid.NewGuid(),Clinic=Guid.NewGuid(),Tenant=Guid.NewGuid();
        public readonly NpgsqlDataSource Db;
        public readonly FakeBilling Billing=new();
        public readonly FakeStorage Storage=new();
        public readonly Access Policy;
        public readonly ItemUploadService Service;
        public readonly FixtureClock Clock;
        public Fixture(bool loseResponse=false,DateTimeOffset? initialTime=null)
        {
            var raw=Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!;
            var cs=new NpgsqlConnectionStringBuilder(raw){Options="-c role=aura_analysis_app"};
            Db=NpgsqlDataSource.Create(cs.ConnectionString);Policy=new(Tenant,Actor,Clinic);Clock=new(initialTime??DateTimeOffset.UtcNow);
            var repository=new PostgresUploadRepository(Db);
            var planning=new UploadPlanningService(new(),Policy,repository,Clock);
            IAnalysisUploadHandler uploads=new DurableUploadService(planning,new PostgresUploadRecoveryRepository(Db),Billing,Storage,new Progress());
            if(loseResponse)uploads=new CommitThenTimeout(uploads);
            Service=new(Db,Policy,Storage,new(),uploads,new PostgresBatchStatusRepository(Db),Clock);
        }
        public async Task<long> Count(string sql,params object[] args)
        {
            await using var command=Db.CreateCommand(sql);foreach(var arg in args)command.Parameters.AddWithValue(arg);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        public ValueTask DisposeAsync()=>Db.DisposeAsync();
    }
    private sealed class FixtureClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current=initial;
        public override DateTimeOffset GetUtcNow()=>current;
        public void Advance(TimeSpan duration)=>current=current.Add(duration);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static UploadManifestItem Item(Guid patient,string name,byte[] bytes)=>new(Guid.NewGuid(),patient,name,Hash(bytes),"FUNDUS","left");

    [PostgresFact]
    public async Task Manifest_exact_replay_is_stable_and_changed_metadata_conflicts_without_credit()
    {
        await using var f=new Fixture();var bytes=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"manifest.png",bytes);
        var key=Guid.NewGuid();var manifest=new UploadManifest([item]);
        var first=await f.Service.CreateAsync(f.Actor,f.Clinic,key,manifest,default);
        var replay=await f.Service.CreateAsync(f.Actor,f.Clinic,key,manifest,default);
        Assert.Equal(first.BatchId,replay.BatchId);
        Assert.Equal(1,await f.Count("SELECT count(*) FROM analysis.upload_batches WHERE requester_id=$1;",f.Actor));
        var conflict=await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.CreateAsync(f.Actor,f.Clinic,key,
            new([item with{FileName="changed.png"}]),default));
        Assert.Equal(409,conflict.Status);Assert.Equal(0,f.Billing.ReserveCalls);
    }

    [PostgresFact]
    public async Task Multi_patient_partial_admission_persists_one_parent_and_accepted_denominator()
    {
        await using var f=new Fixture();var png=TestImages.Png(100,100);var bad="not an image"u8.ToArray();
        var a=Item(Guid.NewGuid(),"left.png",png);var b=Item(Guid.NewGuid(),"right.png",png);var rejected=Item(Guid.NewGuid(),"invalid.png",bad);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([a,b,rejected]),default);
        Assert.Equal("STAGED",(await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,a.ItemId,new MemFile(a.FileName,png,"image/png"),default)).UploadStatus);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,b.ItemId,new MemFile(b.FileName,png,"image/png"),default);
        Assert.Equal("REJECTED",(await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,rejected.ItemId,new MemFile(rejected.FileName,bad,"image/png"),default)).UploadStatus);
        Assert.Equal(0,f.Billing.ReserveCalls);
        var result=await f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"item-regression",default);
        Assert.Equal(parent.BatchId,result.BatchId);Assert.Equal(3,result.Items.Count);
        Assert.Equal(2,result.Items.Count(i=>i.ProcessingStatus=="QUEUED"));Assert.Equal(2,f.Billing.ByKey.Count);
        Assert.Equal(2,await f.Count("SELECT count(*) FROM analysis.images WHERE batch_id=$1 AND tenant_id=$2;",parent.BatchId,f.Tenant));
        Assert.Equal(1,await f.Count("SELECT count(*) FROM analysis.batches WHERE tenant_id=$1",f.Tenant));
        var query=new PostgresAnalysisQueryRepository(f.Db);
        var acceptedId=result.Items.First(i=>i.ProcessingStatus=="QUEUED").AnalysisId;
        Assert.Equal(parent.BatchId,(await query.FindIdentityAsync(acceptedId,default))!.BatchId);
        Assert.Equal(2,await f.Count("SELECT count(DISTINCT r.patient_id) FROM analysis.images i JOIN analysis.analysis_requests r ON r.id=i.analysis_request_id WHERE i.batch_id=$1;",parent.BatchId));
        var inventory=await f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default);
        Assert.Equal("FINALIZED",inventory.Status);
        var progress=await f.Service.TryReadAsync(f.Actor,f.Clinic,parent.BatchId,"item-regression",default);
        Assert.NotNull(progress);Assert.Equal(2,progress.Summary.Total);Assert.Equal(3,progress.TotalDeclared);Assert.Equal(1,progress.AdmissionRejected);
        await f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"item-regression",default);
        Assert.Equal(2,f.Billing.ReserveCalls);
    }

    [PostgresFact]
    public async Task Lost_acceptance_response_resumes_same_child_and_reservation_after_recreating_service()
    {
        await using var f=new Fixture(loseResponse:true);var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"unknown.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        var uncertain=await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"unknown-response",default));
        Assert.True(uncertain.Retryable);
        Assert.Equal("UNKNOWN",Assert.Single((await f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default)).Items).UploadStatus);
        Assert.Equal(1,await f.Count("SELECT count(*) FROM analysis.images WHERE batch_id=$1;",parent.BatchId));
        // A newly constructed handler must discover accepted child response from durable PostgreSQL, not its own memory.
        var durable=new DurableUploadService(new UploadPlanningService(new(),f.Policy,new PostgresUploadRepository(f.Db),TimeProvider.System),
            new PostgresUploadRecoveryRepository(f.Db),f.Billing,f.Storage,new Progress());
        var resumed=new ItemUploadService(f.Db,f.Policy,f.Storage,new(),durable,new PostgresBatchStatusRepository(f.Db),TimeProvider.System);
        var result=await resumed.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"unknown-response",default);
        Assert.Single(result.Items);Assert.Single(f.Billing.ByKey);Assert.Equal(1,f.Billing.ReserveCalls);
        Assert.Equal(1,await f.Count("SELECT count(*) FROM analysis.jobs j JOIN analysis.images i ON i.id=j.image_id WHERE i.batch_id=$1;",parent.BatchId));
    }

    [PostgresFact]
    public async Task Revoked_authorization_blocks_finalize_and_inventory_without_creating_credit()
    {
        await using var f=new Fixture();var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"revoke.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        f.Policy.Allowed=false;
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"revoked",default))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default))).Status);
        Assert.Equal(0,f.Billing.ReserveCalls);Assert.Equal(0,await f.Count("SELECT count(*) FROM analysis.images WHERE batch_id=$1;",parent.BatchId));
    }

    [PostgresFact]
    public async Task Replaying_staged_item_with_different_bytes_is_a_conflict()
    {
        await using var f=new Fixture();var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"replay.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        var different=TestImages.Png(120,100);
        Assert.Equal(409,(await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,
            new MemFile(item.FileName,different,"image/png"),default))).Status);
        Assert.Equal(0,f.Billing.ReserveCalls);
    }

    [PostgresFact]
    public async Task Corrupted_staging_is_rejected_before_credit_and_parent_has_zero_accepted_items()
    {
        await using var f=new Fixture();var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"integrity.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        f.Storage.Files[$"aura-originals/staging/{parent.BatchId:D}/{item.ItemId:D}"]=TestImages.Png(120,100);
        var result=await f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"stage-integrity",default);
        Assert.Equal("REJECTED",Assert.Single(result.Items).ProcessingStatus);Assert.Equal(0,f.Billing.ReserveCalls);
        Assert.Equal(0,await f.Count("SELECT total_images FROM analysis.batches WHERE id=$1 AND status='FAILED';",parent.BatchId));
    }

    [PostgresFact]
    public async Task Expired_unaccepted_staging_is_deleted_with_durable_audit_and_cannot_finalize()
    {
        await using var f=new Fixture(initialTime:DateTimeOffset.UtcNow.AddHours(-25));
        var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"expire.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        f.Clock.Advance(TimeSpan.FromHours(25));await f.Service.CleanupExpiredAsync(default);
        Assert.False(f.Storage.Files.ContainsKey($"aura-originals/staging/{parent.BatchId:D}/{item.ItemId:D}"));
        Assert.Equal("EXPIRED",(await f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default)).Status);
        Assert.Equal(2,await f.Count("SELECT count(*) FROM analysis.audit_logs WHERE tenant_id=$1 AND requested_target_id=$2 AND metadata->>'phase' IN ('INTENT','DONE');",f.Tenant,item.ItemId.ToString("D")));
        Assert.Equal(0,f.Billing.ReserveCalls);Assert.Empty(f.Billing.Released);
        Assert.Equal(409,(await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"expired",default))).Status);
        Assert.Equal(0,await f.Service.CleanupExpiredAsync(default));
    }

    [PostgresFact]
    public async Task Unknown_acceptance_is_retained_after_expiry_and_recovers_even_if_staging_is_missing()
    {
        await using var f=new Fixture(loseResponse:true,initialTime:DateTimeOffset.UtcNow.AddHours(-25));
        var png=TestImages.Png(100,100);var item=Item(Guid.NewGuid(),"hold.png",png);
        var parent=await f.Service.CreateAsync(f.Actor,f.Clinic,Guid.NewGuid(),new([item]),default);
        await f.Service.PutAsync(f.Actor,f.Clinic,parent.BatchId,item.ItemId,new MemFile(item.FileName,png,"image/png"),default);
        await Assert.ThrowsAsync<UploadFailure>(()=>f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"held",default));
        f.Clock.Advance(TimeSpan.FromHours(25));await f.Service.CleanupExpiredAsync(default);
        var key=$"aura-originals/staging/{parent.BatchId:D}/{item.ItemId:D}";
        Assert.True(f.Storage.Files.ContainsKey(key));Assert.Empty(f.Billing.Released);
        Assert.Equal("UNKNOWN",Assert.Single((await f.Service.GetAsync(f.Actor,f.Clinic,parent.BatchId,default)).Items).UploadStatus);
        f.Storage.Files.TryRemove(key,out _);
        var recovered=await f.Service.FinalizeAsync(f.Actor,f.Clinic,parent.BatchId,"held",default);
        Assert.Equal("QUEUED",Assert.Single(recovered.Items).ProcessingStatus);Assert.Single(f.Billing.ByKey);
        Assert.Equal(1,f.Billing.ReserveCalls);
    }

    [PostgresFact]
    public async Task Concurrent_create_cannot_exceed_the_three_open_batches_per_actor_quota()
    {
        await using var f=new Fixture();var png=TestImages.Png(100,100);var patient=Guid.NewGuid();
        async Task<bool> Create()
        {
            var key=Guid.NewGuid();var manifest=new UploadManifest([Item(patient,$"quota-{key:N}.png",png)]);
            for(var retry=0;retry<100;retry++)
            {
                try{await f.Service.CreateAsync(f.Actor,f.Clinic,key,manifest,default);return true;}
                catch(UploadFailure e)when(e.Status==429){return false;}
                catch(UploadFailure e)when(e.Status==409){await Task.Delay(10);}
            }
            throw new TimeoutException("Fixture lock contention did not resolve.");
        }
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Create()));
        Assert.Equal(3,results.Count(x=>x));Assert.Equal(3,await f.Count("SELECT count(*) FROM analysis.upload_batches WHERE requester_id=$1;",f.Actor));
    }
}
