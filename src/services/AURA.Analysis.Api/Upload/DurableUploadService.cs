using AURA.Analysis.Api.Http;
using AURA.Common.Cache;
namespace AURA.Analysis.Api.Upload;

/// <summary>Resumes durable intents. Unknown outcomes never authorize compensation.</summary>
public sealed class DurableUploadService(UploadPlanningService planning, IUploadRecoveryJournal journal,
    ICreditReservations credits, IPrivateFileStorage storage, IBatchRedisProgressService batchProgress) : IAnalysisUploadHandler
{
    public async Task<UploadResult> CreateAsync(UploadCommand command, CancellationToken ct)
    {
        try { return await Execute(command, ct); }
        catch (UploadFailure) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw UploadFailure.Dependency("upload dependencies"); }
    }

    private async Task<UploadResult> Execute(UploadCommand command, CancellationToken ct)
    {
        await using var prepared = await planning.PrepareAsync(command, ct);
        if (prepared.StoredResponse is { } replay) return new(replay, true);
        var candidate = prepared.Plan!;
        var r = await journal.PrepareAsync(candidate, prepared.Decision.BillingOwner, "aura-originals", ct);
        if (r.Phase == UploadRecoveryPhase.Accepted) throw UploadFailure.Dependency("accepted upload state");
        if (r.Phase == UploadRecoveryPhase.Aborting)
        {
            await FinishKnownCreditAbort(r, ct);
            throw Insufficient();
        }
        if (r.Phase != UploadRecoveryPhase.Pending) throw UploadRecovery.InProgress();
        // Another accepted upload may have become the canonical image while this process was down.
        // Hold for reconciliation rather than accept duplicate work or release an unknown reservation.
        if (!r.Plan.Images.Select(i => (i.AnalysisId,i.Sha256,i.ProcessingStatus,i.ExistingAnalysisId))
            .SequenceEqual(candidate.Images.Select(i => (i.AnalysisId,i.Sha256,i.ProcessingStatus,i.ExistingAnalysisId))))
            throw UploadRecovery.InProgress();

        for (var index = 0; index < r.Effects.Count; index++)
        {
            var e = r.Effects[index];
            if (e.ReserveKey == Guid.Empty || e.ReservationId is not null) continue;
            if (e.CreditDenied)
            {
                r = await Step(r, UploadRecoveryEvent.BeginAbort, default, ct, reason:"ANALYSIS_ABORT");
                await FinishKnownCreditAbort(r, ct); throw Insufficient();
            }
            var wasUnknown = e.ReserveStarted;
            if (!wasUnknown) r = await Step(r, UploadRecoveryEvent.ReserveIntent, e.AnalysisId, ct);
            var result = await credits.ReserveAsync(new(r.Plan.TenantId,r.BillingOwner,e.AnalysisId,e.ReserveKey)
            {
                ActorAccountId=r.Plan.RequesterId,PatientId=r.Plan.PatientId,Generation=e.Generation,CorrelationId=r.Plan.CorrelationId
            }, ct);
            if (result.Status == ReservationStatus.InsufficientCredit)
            {
                if (result.ReservationId is not null) throw UploadFailure.Dependency("Billing response");
                // A replayed 402 cannot prove an earlier timed-out request will never create a hold.
                if (wasUnknown) throw UploadFailure.Dependency("Billing outcome reconciliation");
                r = await Step(r, UploadRecoveryEvent.CreditDenied, e.AnalysisId, ct);
                r = await Step(r, UploadRecoveryEvent.BeginAbort, default, ct, reason:"ANALYSIS_ABORT");
                await FinishKnownCreditAbort(r, ct); throw Insufficient();
            }
            if (result.Status != ReservationStatus.Reserved || result.ReservationId is null || result.ReservationId == Guid.Empty)
                throw UploadFailure.Dependency("Billing response");
            r = await Step(r, UploadRecoveryEvent.Reserved, e.AnalysisId, ct, result.ReservationId);
        }
        for (var index = 0; index < r.Effects.Count; index++)
        {
            var e = r.Effects[index]; if (e.BlobConfirmed) continue;
            if (!e.BlobStarted) r = await Step(r, UploadRecoveryEvent.BlobIntent, e.AnalysisId, ct);
            var image = r.Plan.Images[index];
            await using var bytes = command.Files[index].OpenReadStream();
            await storage.PutAsync(r.Container,image.ObjectKey,bytes,image.MimeType,ct);
            r = await Step(r,UploadRecoveryEvent.BlobStored,e.AnalysisId,ct);
        }
        // The repository verifies/fences this exact frozen plan inside the acceptance transaction.
        // An exception, including a lost COMMIT acknowledgement, never releases/deletes anything here.
        await prepared.Session.CommitAsync(r.Plan,ct);
        // PostgreSQL commits the accepted batch and all jobs before touching Redis. The cache may be
        // rebuilt from the committed image states, so an outage must not block durable acceptance.
        try
        {
            await batchProgress.InitializeBatchAsync(r.Plan.BatchId, r.Plan.Images.Count);
            foreach (var image in r.Plan.Images.Where(i => i.Job is null))
                await batchProgress.RecordImageOutcomeAsync(r.Plan.BatchId, image.AnalysisId, isSuccess: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // The persisted batch and image rows are the recovery source of truth.
        }
        return new(r.Plan.Response,false);
    }

    private async Task FinishKnownCreditAbort(UploadRecoveryRecord r, CancellationToken ct)
    {
        // Storage/unknown-outcome abort requires the future reconciler; do not pretend it is complete.
        if (r.Phase != UploadRecoveryPhase.Aborting || r.AbortReason != "ANALYSIS_ABORT"
            || r.Effects.Any(e => e.BlobStarted || (e.ReserveStarted && e.ReservationId is null && !e.CreditDenied)))
            throw UploadRecovery.InProgress();
        foreach (var e in r.Effects)
        {
            if (e.ReservationId is null || e.ReleaseConfirmed) continue;
            await credits.ReleaseAsync(new(r.Plan.TenantId,e.ReservationId.Value,e.ReleaseKey)
                {Reason="ANALYSIS_ABORT",CorrelationId=r.Plan.CorrelationId},ct);
            r = await Step(r,UploadRecoveryEvent.Released,e.AnalysisId,ct);
        }
        await Step(r,UploadRecoveryEvent.FinishAbort,default,ct);
    }

    private Task<UploadRecoveryRecord> Step(UploadRecoveryRecord r,UploadRecoveryEvent action,Guid image,
        CancellationToken ct,Guid? reservation=null,string? reason=null) => journal.RecordAsync(
            UploadRecovery.Scope(r.Plan),r.AttemptId,r.Revision,action,image,reservation,reason,ct);
    private static UploadFailure Insufficient() => new(402,"ERR_INSUFFICIENT_CREDIT","Không đủ credit.");
}
