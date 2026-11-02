using AURA.Analysis.Api.Upload;
using Xunit;
using AURA.Common.Cache;

namespace AURA.Analysis.Api.Tests;

public sealed class DurableUploadTests
{

        private sealed class BatchProgressStub : IBatchRedisProgressService
    {
        private sealed class BatchState
        {
            public BatchState(int total)
            {
                Total = total;
                CreatedAt = DateTime.UtcNow;
            }

            public int Total { get; }
            public int Completed { get; set; }
            public int Failed { get; set; }
            public DateTime CreatedAt { get; }
            public HashSet<Guid> RecordedImages { get; } = new();
        }

        private readonly Dictionary<Guid, BatchState> _states = new();


        public Task InitializeBatchAsync(
            Guid batchId,
            int totalImages,
            TimeSpan? expiry = null)
        {
            if (batchId == Guid.Empty || totalImages is < 1 or > 100)
            {
                throw new ArgumentException("Invalid batch initialization.");
            }

            // Idempotent initialization for upload retries.
            _states.TryAdd(batchId, new BatchState(totalImages));
            return Task.CompletedTask;
        }

        public Task<BatchProgressSnapshot?> GetProgressAsync(Guid batchId)
        {
            if (!_states.TryGetValue(batchId, out var state))
            {
                return Task.FromResult<BatchProgressSnapshot?>(null);
            }

            var processing = state.Total - state.Completed - state.Failed;
            var status = GetStatus(state, processing);

            return Task.FromResult<BatchProgressSnapshot?>(
                new BatchProgressSnapshot(
                    batchId,
                    state.Total,
                    processing,
                    state.Completed,
                    state.Failed,
                    status,
                    state.CreatedAt,
                    DateTime.UtcNow));
        }

        public Task<BatchImageCompletionResult> RecordImageOutcomeAsync(
            Guid batchId,
            Guid imageId,
            bool isSuccess)
        {
            if (!_states.TryGetValue(batchId, out var state))
            {
                throw new InvalidOperationException(
                    "Batch progress has not been initialized.");
            }

            if (imageId == Guid.Empty)
            {
                throw new ArgumentException("Image ID is required.");
            }

            // Avoid counting the same image outcome twice.
            if (state.RecordedImages.Add(imageId))
            {
                if (isSuccess)
                {
                    state.Completed++;
                }
                else
                {
                    state.Failed++;
                }
            }

            var processing = state.Total - state.Completed - state.Failed;
            var status = GetStatus(state, processing);

            return Task.FromResult(
                new BatchImageCompletionResult(
                    batchId,
                    state.Total,
                    state.Completed,
                    state.Failed,
                    processing,
                    processing == 0,
                    status));
        }

        public async Task RebuildBatchAsync(Guid batchId, int totalImages, IReadOnlyList<BatchImageOutcome> outcomes,
            string status, TimeSpan? expiry = null)
        {
            _states[batchId] = new BatchState(totalImages);
            foreach (var outcome in outcomes)
                await RecordImageOutcomeAsync(batchId, outcome.ImageId, outcome.IsSuccess);
        }

        private static string GetStatus(BatchState state, int processing)
        {
            if (processing > 0)
                return "IN_PROGRESS";

            if (state.Failed == 0)
                return "COMPLETED";

            if (state.Completed == 0)
                return "FAILED";

            return "PARTIAL_COMPLETED";
        }
    }

    private sealed class Journal : IUploadRecoveryJournal
    {
        public UploadRecoveryRecord? Current;
        public bool FailPrepare;
        public UploadRecoveryEvent? FailEvent;
        public Task<UploadRecoveryRecord?> ReadAsync(IdempotencyScope s,CancellationToken ct)=>Task.FromResult(Current);
        public Task<UploadRecoveryRecord> PrepareAsync(BatchPlan p,string owner,string container,CancellationToken ct)
        {
            if(FailPrepare)throw new IOException();
            if(Current is not null && Current.Plan.Fingerprint!=p.Fingerprint)throw new UploadFailure(409,"ERR_IDEMPOTENCY_KEY_REUSED","conflict");
            if(Current is null || Current.Phase==UploadRecoveryPhase.Aborted)Current=UploadRecovery.Prepare(p,owner,container,Current);
            else if(Current.Phase is UploadRecoveryPhase.Pending or UploadRecoveryPhase.Aborting)Current=Current with{Revision=Current.Revision+1};
            return Task.FromResult(Current);
        }
        public Task<UploadRecoveryRecord> RecordAsync(IdempotencyScope s,Guid id,long revision,UploadRecoveryEvent action,
            Guid analysisId=default,Guid? reservationId=null,string? reason=null,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            if(FailEvent==action){FailEvent=null;throw new IOException();}
            if(Current is null || Current.AttemptId!=id || Current.Revision!=revision)throw UploadRecovery.InProgress();
            Current=UploadRecovery.Apply(Current,action,analysisId,reservationId,reason);return Task.FromResult(Current);
        }
        public Task<IReadOnlyList<UploadRecoveryCandidate>> FindDueAsync(int limit,CancellationToken ct)=>throw new NotSupportedException();
    }
    private sealed class Billing : ICreditReservations
    {
        public readonly FakeBilling Inner=new();
        public bool LoseReserve, LoseRelease, UnknownReturns402;
        public async Task<ReservationOutcome> ReserveAsync(ReserveCredit r,CancellationToken ct)
        {
            if(UnknownReturns402)return new(ReservationStatus.InsufficientCredit,null);
            var result=await Inner.ReserveAsync(r,ct);
            if(LoseReserve){LoseReserve=false;throw new IOException();}return result;
        }
        public async Task ReleaseAsync(ReleaseCredit r,CancellationToken ct)
        {await Inner.ReleaseAsync(r,ct);if(LoseRelease){LoseRelease=false;throw new IOException();}}
    }
    private sealed class Storage : IPrivateFileStorage
    {
        public readonly FakeStorage Inner=new(); public bool LosePut;
        public async Task PutAsync(string c,string k,Stream b,string t,CancellationToken ct)
        {await Inner.PutAsync(c,k,b,t,ct);if(LosePut){LosePut=false;throw new IOException();}}
        public Task DeleteAsync(string c,string k,CancellationToken ct)=>Inner.DeleteAsync(c,k,ct);
        public Task<Stream> OpenReadAsync(string c,string k,CancellationToken ct)=>Inner.OpenReadAsync(c,k,ct);
        public Task<FileReadAccess> CreateReadAccessAsync(string c,string k,TimeSpan ttl,CancellationToken ct)=>Inner.CreateReadAccessAsync(c,k,ttl,ct);
    }
    private sealed class Repository(Journal journal) : IUploadRepository
    {
        public Journal Journal { get; }=journal;
        public readonly FakeRepo Inner=new();public bool LoseCommit;
        public async Task<IUploadSession> BeginAsync(IdempotencyScope s,string fp,CancellationToken ct)=>new Session(this,await Inner.BeginAsync(s,fp,ct));
        private sealed class Session(Repository owner,IUploadSession inner):IUploadSession
        {
            public IdempotencyState State=>inner.State;public AcceptedResponse? StoredResponse=>inner.StoredResponse;
            public Task<Guid?> FindExistingAnalysisAsync(Guid p,string m,string e,string sha,CancellationToken ct)=>inner.FindExistingAnalysisAsync(p,m,e,sha,ct);
            public async Task CommitAsync(BatchPlan plan,CancellationToken ct)
            {
                var r=owner.Journal.Current!;
                if(r.Phase!=UploadRecoveryPhase.Pending || !UploadRecovery.ReadyToAccept(r) || plan.RecoveryAttemptId!=r.AttemptId)throw UploadRecovery.InProgress();
                await inner.CommitAsync(plan,ct);
                owner.Journal.Current=r with{Phase=UploadRecoveryPhase.Accepted,Revision=r.Revision+1};
                if(owner.LoseCommit){owner.LoseCommit=false;throw new IOException();}
            }
            public ValueTask DisposeAsync()=>inner.DisposeAsync();
        }
    }
    private sealed class Rig
    {
        public readonly BatchProgressStub BatchProgress = new();
        public readonly FakeAccess Access=new();public readonly Journal Journal=new();public readonly Billing Billing=new();public readonly Storage Storage=new();
        public readonly Repository Repo;
        public Rig()=>Repo=new(Journal);
        public DurableUploadService Service() =>
            new(
                new(new UploadOptions(), Access, Repo, TimeProvider.System),
                Journal,
                Billing,
                Storage,
                BatchProgress);
        public readonly Guid Requester=Guid.NewGuid(),Patient=Guid.NewGuid(),Key=Guid.NewGuid();
        public UploadCommand Command(int count=1,int seed=1)=>new(Requester,null,Key,"durable-check",Patient.ToString(),"FUNDUS","left",
            Enumerable.Range(0,count).Select(i=>(IUploadedFile)new MemFile("synthetic.jpg",TestImages.Jpeg(32,32,seed:seed+i),"image/jpeg")).ToArray());
    }
    private static async Task Error(Rig r,int status,UploadCommand? c=null)=>Assert.Equal(status,(await Assert.ThrowsAsync<UploadFailure>(()=>r.Service().CreateAsync(c??r.Command(),default))).Status);

    [Fact] public async Task Success_accepts_frozen_plan_and_replays_without_more_effects()
    {var r=new Rig();var first=await r.Service().CreateAsync(r.Command(),default);var second=await r.Service().CreateAsync(r.Command(),default);Assert.False(first.Replayed);Assert.True(second.Replayed);Assert.Equal(first.Response,second.Response);Assert.Single(r.Billing.Inner.ByKey);Assert.Equal(1,r.Storage.Inner.PutCalls);Assert.Equal(UploadRecoveryPhase.Accepted,r.Journal.Current!.Phase);}
    [Fact] public async Task Prepare_failure_sends_no_external_request()
    {var r=new Rig();r.Journal.FailPrepare=true;await Error(r,503);Assert.Equal(0,r.Billing.Inner.ReserveCalls);Assert.Equal(0,r.Storage.Inner.PutCalls);}
    [Fact] public async Task Reserve_intent_write_failure_sends_no_reserve()
    {var r=new Rig();r.Journal.FailEvent=UploadRecoveryEvent.ReserveIntent;await Error(r,503);Assert.Equal(0,r.Billing.Inner.ReserveCalls);}
    [Fact] public async Task New_owner_claim_invalidates_previous_checkpoint_revision()
    {var r=new Rig();r.Billing.LoseReserve=true;await Error(r,503);var old=r.Journal.Current!;var claimed=await r.Journal.PrepareAsync(old.Plan,old.BillingOwner,old.Container,default);Assert.True(claimed.Revision>old.Revision);await Assert.ThrowsAsync<UploadFailure>(()=>r.Journal.RecordAsync(UploadRecovery.Scope(old.Plan),old.AttemptId,old.Revision,UploadRecoveryEvent.CreditDenied,old.Effects[0].AnalysisId));}
    [Fact] public async Task Lost_reserve_ack_resumes_same_key_without_release()
    {var r=new Rig();r.Billing.LoseReserve=true;await Error(r,503);var before=r.Journal.Current!;Assert.Empty(r.Billing.Inner.Released);await r.Service().CreateAsync(r.Command(),default);Assert.Single(r.Billing.Inner.ByKey);Assert.Equal(before.AttemptId,r.Journal.Current!.AttemptId);Assert.Equal(before.Plan.CreatedAt,r.Journal.Current.Plan.CreatedAt);}
    [Fact] public async Task Reserve_checkpoint_failure_recovers_original_reservation()
    {var r=new Rig();r.Journal.FailEvent=UploadRecoveryEvent.Reserved;await Error(r,503);await r.Service().CreateAsync(r.Command(),default);Assert.Single(r.Billing.Inner.ByKey);Assert.Empty(r.Billing.Inner.Released);}
    [Fact] public async Task Unknown_reserve_followed_by_402_stays_pending()
    {var r=new Rig();r.Billing.LoseReserve=true;await Error(r,503);r.Billing.UnknownReturns402=true;await Error(r,503);Assert.Equal(UploadRecoveryPhase.Pending,r.Journal.Current!.Phase);Assert.False(r.Journal.Current.Effects[0].CreditDenied);Assert.Empty(r.Billing.Inner.Released);}
    [Fact] public async Task Blob_intent_failure_sends_no_put_or_compensation()
    {var r=new Rig();r.Journal.FailEvent=UploadRecoveryEvent.BlobIntent;await Error(r,503);Assert.Equal(0,r.Storage.Inner.PutCalls);Assert.Empty(r.Billing.Inner.Released);}
    [Fact] public async Task Lost_put_ack_reuses_same_blob_and_reservation()
    {var r=new Rig();r.Storage.LosePut=true;await Error(r,503);var key=r.Journal.Current!.Plan.Images[0].ObjectKey;await r.Service().CreateAsync(r.Command(),default);Assert.Single(r.Storage.Inner.Files);Assert.Equal(key,r.Journal.Current!.Plan.Images[0].ObjectKey);Assert.Single(r.Billing.Inner.ByKey);Assert.Empty(r.Storage.Inner.Deleted);}
    [Fact] public async Task Blob_checkpoint_failure_replays_put_without_changing_attempt()
    {var r=new Rig();r.Journal.FailEvent=UploadRecoveryEvent.BlobStored;await Error(r,503);var id=r.Journal.Current!.AttemptId;await r.Service().CreateAsync(r.Command(),default);Assert.Equal(id,r.Journal.Current!.AttemptId);Assert.Equal(2,r.Storage.Inner.PutCalls);}
    [Fact] public async Task Failed_commit_keeps_resources_and_retry_skips_completed_effects()
    {var r=new Rig();r.Repo.Inner.FailCommit=true;await Error(r,503);r.Repo.Inner.FailCommit=false;await r.Service().CreateAsync(r.Command(),default);Assert.Equal(1,r.Billing.Inner.ReserveCalls);Assert.Equal(1,r.Storage.Inner.PutCalls);Assert.Empty(r.Billing.Inner.Released);}
    [Fact] public async Task Lost_commit_ack_replays_original_response_without_compensation()
    {var r=new Rig();r.Repo.LoseCommit=true;await Error(r,503);var result=await r.Service().CreateAsync(r.Command(),default);Assert.True(result.Replayed);Assert.Single(r.Repo.Inner.Commits);Assert.Empty(r.Billing.Inner.Released);Assert.Empty(r.Storage.Inner.Deleted);}
    [Fact] public async Task Known_402_aborts_and_releases_prior_holds()
    {var r=new Rig();r.Billing.Inner.InsufficientAtCall=2;await Error(r,402,r.Command(2));Assert.Equal(UploadRecoveryPhase.Aborted,r.Journal.Current!.Phase);Assert.Single(r.Billing.Inner.Released);Assert.Equal(0,r.Storage.Inner.PutCalls);}
    [Fact] public async Task Retry_after_known_abort_uses_per_image_generations()
    {var r=new Rig();r.Billing.Inner.InsufficientAtCall=2;await Error(r,402,r.Command(2));var old=r.Journal.Current!;await r.Service().CreateAsync(r.Command(2),default);Assert.Equal(2,r.Journal.Current!.Effects[0].Generation);Assert.Equal(1,r.Journal.Current.Effects[1].Generation);Assert.NotEqual(old.Plan.Images[0].ObjectKey,r.Journal.Current.Plan.Images[0].ObjectKey);}
    [Fact] public async Task Release_lost_ack_is_replayed_before_abort_completes()
    {var r=new Rig();r.Billing.Inner.InsufficientAtCall=2;r.Billing.LoseRelease=true;await Error(r,503,r.Command(2));Assert.Equal(UploadRecoveryPhase.Aborting,r.Journal.Current!.Phase);await Error(r,402,r.Command(2));Assert.Equal(UploadRecoveryPhase.Aborted,r.Journal.Current!.Phase);Assert.Equal(r.Billing.Inner.ReleaseRequests[0].IdempotencyKey,r.Billing.Inner.ReleaseRequests[1].IdempotencyKey);}
    [Fact] public async Task Changed_payload_after_unknown_reserve_is_rejected()
    {var r=new Rig();r.Billing.LoseReserve=true;await Error(r,503);await Error(r,409,r.Command(seed:9));Assert.Single(r.Billing.Inner.ReserveRequests);}
    [Fact] public async Task Permission_is_checked_again_before_resume()
    {var r=new Rig();r.Storage.LosePut=true;await Error(r,503);r.Access.Allowed=false;await Error(r,404);Assert.Equal(1,r.Storage.Inner.PutCalls);}
    [Fact] public async Task Dedupe_changed_during_outage_holds_for_reconciliation()
    {var r=new Rig();r.Storage.LosePut=true;await Error(r,503);r.Repo.Inner.Existing[r.Journal.Current!.Plan.Images[0].Sha256]=Guid.NewGuid();await Error(r,409);Assert.Empty(r.Billing.Inner.Released);}
    [Fact] public async Task Duplicate_images_are_not_reserved_twice()
    {var r=new Rig();var c=r.Command();c=c with{Files=new[]{c.Files[0],c.Files[0]}};var result=await r.Service().CreateAsync(c,default);Assert.Equal("DUPLICATE",result.Response.Items[1].ProcessingStatus);Assert.Equal(result.Response.Items[0].AnalysisId,result.Response.Items[1].ExistingAnalysisId);Assert.Null(result.Response.Items[0].ExistingAnalysisId);Assert.Single(r.Billing.Inner.ByKey);Assert.Equal(2,r.Storage.Inner.PutCalls);}
    [Fact] public async Task Existing_duplicate_reference_survives_accepted_response_and_replay()
    {
        var r=new Rig();var c=r.Command();var canonical=Guid.NewGuid();
        await using(var stream=c.Files[0].OpenReadStream())
        {var inspected=await ImageInspector.InspectAsync(stream,"image/jpeg",new UploadOptions());r.Repo.Inner.Existing[inspected.Image!.Sha256]=canonical;}
        var first=await r.Service().CreateAsync(c,default);var second=await r.Service().CreateAsync(c,default);
        Assert.Equal(canonical,Assert.Single(first.Response.Items).ExistingAnalysisId);
        Assert.Equal(canonical,Assert.Single(second.Response.Items).ExistingAnalysisId);
        Assert.True(second.Replayed);Assert.Equal(first.Response,second.Response);
        Assert.Empty(r.Billing.Inner.ByKey);Assert.Equal(1,r.Storage.Inner.PutCalls);
    }
    [Fact] public async Task Caller_cancellation_keeps_intents_and_propagates()
    {var r=new Rig();using var cts=new CancellationTokenSource();r.Storage.Inner.BeforePut=()=>{cts.Cancel();return Task.CompletedTask;};await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>r.Service().CreateAsync(r.Command(),cts.Token));Assert.Empty(r.Billing.Inner.Released);Assert.Equal(UploadRecoveryPhase.Pending,r.Journal.Current!.Phase);}
    [Fact] public async Task Concurrent_same_key_gets_in_progress_without_extra_hold()
    {var r=new Rig();var entered=new TaskCompletionSource();var release=new TaskCompletionSource();r.Storage.Inner.BeforePut=async()=>{entered.SetResult();await release.Task;};var running=r.Service().CreateAsync(r.Command(),default);await entered.Task;try{await Error(r,409);}finally{release.SetResult();}await running;Assert.Single(r.Billing.Inner.ByKey);}
}
