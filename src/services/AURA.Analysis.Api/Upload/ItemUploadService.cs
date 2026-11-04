using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Http;
using Npgsql;
using NpgsqlTypes;

namespace AURA.Analysis.Api.Upload;

public sealed record UploadManifestItem(Guid ItemId, Guid PatientId, string FileName, string Sha256, string Modality, string EyeSide);
public sealed record UploadManifest(IReadOnlyList<UploadManifestItem> Items);
public sealed record UploadInventoryItem(Guid ItemId, Guid PatientId, string FileName, string Sha256, string Modality,
    string EyeSide, string UploadStatus, ItemError? Error = null, Guid? AnalysisId = null)
{
    public string? ContentType { get; init; }
    public long Length { get; init; }
    public Guid? ChildBatchId { get; init; }
    public AcceptedItem? Receipt { get; init; }
}
public sealed record UploadInventory(Guid BatchId, DateTime CreatedAt, DateTime ExpiresAt, string Status, string StatusUrl,
    IReadOnlyList<UploadInventoryItem> Items, AcceptedResponse? FinalizedResponse = null)
{
    public Guid RequesterId { get; init; }
    public Guid TenantId { get; init; }
    public Guid OwnerId { get; init; }
    public Guid? ClinicId { get; init; }
}
public interface IGroupedBatchStatusReader
{
    Task<BatchStatusResponse?> TryReadAsync(Guid requesterId, Guid? clinicId, Guid batchId, string? correlationId, CancellationToken ct);
}

/// <summary>Persisted item admission. The per-image durable saga remains the only credit/acceptance writer.</summary>
public sealed class ItemUploadService(NpgsqlDataSource db, IPatientAccessResolver access, IPrivateFileStorage storage,
    UploadOptions options, IAnalysisUploadHandler uploads, IBatchStatusRepository progress, TimeProvider clock) : IGroupedBatchStatusReader
{
    private const string Container = "aura-originals";
    private static UploadFailure Missing() => new(404,"ERR_NOT_FOUND","Không tìm thấy tài nguyên.");
    private static string StageKey(Guid batch, Guid item) => $"staging/{batch:D}/{item:D}";
    public static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0,16));
    private static void Validate(UploadManifest manifest)
    {
        if (manifest.Items is null || manifest.Items.Count is <1 or >100
            || manifest.Items.Select(i=>i.ItemId).Distinct().Count()!=manifest.Items.Count
            || manifest.Items.Any(i=>i.ItemId==Guid.Empty || i.PatientId==Guid.Empty || string.IsNullOrWhiteSpace(i.FileName)
                || i.FileName.Length>255 || i.FileName.Any(char.IsControl) || i.Sha256 is not {Length:64}
                || i.Sha256.Any(c=>!char.IsAsciiHexDigit(c)) || i.Modality!="FUNDUS" || i.EyeSide is not ("left" or "right" or "unknown")))
            throw UploadFailure.Validation(new ErrorDetail("items","invalid_manifest"));
    }
    public async Task<UploadInventory> CreateAsync(Guid actor, Guid? clinic, Guid key, UploadManifest manifest, CancellationToken ct)
    {
        Validate(manifest);
        if(actor==Guid.Empty || key==Guid.Empty || clinic==Guid.Empty || clinic is null && manifest.Items.Select(i=>i.PatientId).Distinct().Count()>1)
            throw UploadFailure.Validation(new ErrorDetail("context","invalid_actor_key_or_personal_batch"));
        manifest=manifest with{Items=manifest.Items.Select(i=>i with{Sha256=i.Sha256.ToLowerInvariant()}).ToArray()};
        var id=StableId($"aura-item-batch:{actor:D}:{key:D}");
        await using var actorLock=await LockAsync(StableId($"aura-staging-quota:{actor:D}"),ct);
        await using var held=await LockAsync(id,ct);
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{clinic,manifest},UploadJson.Options))).ToLowerInvariant();
        var previous=await ReadAsync(id,ct);
        if(previous is not null)
        {
            await AuthorizeAsync(previous,actor,clinic,"ANALYSIS_CREATE",null,ct);
            await using var check=db.CreateCommand("SELECT fingerprint FROM analysis.upload_batches WHERE id=$1");check.Parameters.AddWithValue(id);
            if((string?)await check.ExecuteScalarAsync(ct)!=fingerprint)throw new UploadFailure(409,"ERR_IDEMPOTENCY_KEY_REUSED","Manifest đã thay đổi.");
            return previous;
        }
        AccessDecision? scope=null;
        var denied=new HashSet<Guid>();
        foreach(var patient in manifest.Items.Select(i=>i.PatientId).Distinct())
        {
            var decision=await access.ResolveAsync(new(actor,clinic,patient,"ANALYSIS_CREATE"),ct);
            if(!decision.Allowed || scope is not null && (scope.TenantId!=decision.TenantId || scope.OwnerPrincipalId!=decision.OwnerPrincipalId))denied.Add(patient);
            else scope=decision;
        }
        if(scope is null)throw Missing();
        // Bound outstanding staged storage per authenticated actor, in addition to each batch/image limit.
        await using(var quota=db.CreateCommand("SELECT count(*) FROM analysis.upload_batches WHERE requester_id=$1 AND expires_at>now() AND state->>'status'<>'FINALIZED'"))
        {quota.Parameters.AddWithValue(actor);if(Convert.ToInt64(await quota.ExecuteScalarAsync(ct))>=3)throw new UploadFailure(429,"ERR_UPLOAD_QUOTA","Có quá nhiều đợt upload chưa hoàn tất.",true);}
        var now=clock.GetUtcNow().UtcDateTime;
        var inventory=new UploadInventory(id,now,now.AddHours(24),"UPLOADING",$"/api/v1/batches/{id:D}",
            manifest.Items.Select(i=>new UploadInventoryItem(i.ItemId,i.PatientId,i.FileName,i.Sha256.ToLowerInvariant(),i.Modality,i.EyeSide,
                denied.Contains(i.PatientId)?"REJECTED":"DECLARED",denied.Contains(i.PatientId)?new("ERR_UNAUTHORIZED","Không có quyền tiếp nhận ảnh.",false):null)).ToArray())
            {RequesterId=actor,ClinicId=clinic,TenantId=scope!.TenantId,OwnerId=scope.OwnerPrincipalId};
        await using var conn=await db.OpenConnectionAsync(ct);await using var tx=await conn.BeginTransactionAsync(ct);
        await using(var parent=new NpgsqlCommand("""
            INSERT INTO analysis.batches(id,tenant_id,owner_id,requester_id,patient_id,clinic_id,total_images,admission_complete,created_at,updated_at)
            VALUES($1,$2,$3,$4,$5,$6,0,false,$7,$7)
            """,conn,tx))
        {
            foreach(var g in new[]{id,scope.TenantId,scope.OwnerPrincipalId,actor,manifest.Items.First(i=>!denied.Contains(i.PatientId)).PatientId})parent.Parameters.AddWithValue(g);
            parent.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)clinic??DBNull.Value);parent.Parameters.AddWithValue(now);await parent.ExecuteNonQueryAsync(ct);
        }
        await using(var insert=new NpgsqlCommand("INSERT INTO analysis.upload_batches(id,requester_id,tenant_id,clinic_id,fingerprint,state,created_at,expires_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",conn,tx))
        {
            insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(actor);insert.Parameters.AddWithValue(scope.TenantId);
            insert.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)clinic??DBNull.Value);insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(inventory,UploadJson.Options));insert.Parameters.AddWithValue(now);insert.Parameters.AddWithValue(inventory.ExpiresAt);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);return inventory;
    }
    public async Task<UploadInventory> GetAsync(Guid actor, Guid? clinic, Guid id, CancellationToken ct)
    {
        var state=await ReadAsync(id,ct)??throw Missing();await AuthorizeAsync(state,actor,clinic,"ANALYSIS_CREATE",null,ct);return state;
    }
    public async Task<UploadInventoryItem> PutAsync(Guid actor,Guid? clinic,Guid batch,Guid itemId,IUploadedFile file,CancellationToken ct)
    {
        await using var held=await LockAsync(batch,ct);
        var state=await GetAsync(actor,clinic,batch,ct);var item=state.Items.FirstOrDefault(i=>i.ItemId==itemId)??throw Missing();
        if(file.Length>options.MaxImageBytes)throw new UploadFailure(413,"ERR_PAYLOAD_TOO_LARGE","Ảnh vượt giới hạn kích thước.");
        await using var bytes=file.OpenReadStream();
        var inspection=await ImageInspector.InspectAsync(bytes,file.ContentType,options,ct);
        if(item.UploadStatus!="DECLARED")
        {
            if(inspection.Image?.Sha256!=item.Sha256)throw new UploadFailure(409,"ERR_IDEMPOTENCY_KEY_REUSED","Nội dung ảnh khác manifest.");
            if(item.UploadStatus=="UNKNOWN" && state.Status=="FINALIZING")
            {
                bytes.Position=0;await storage.PutAsync(Container,StageKey(batch,itemId),bytes,inspection.Image.MimeType,ct);
            }
            return item;
        }
        if(state.Status!="UPLOADING" || state.ExpiresAt<=clock.GetUtcNow().UtcDateTime)throw new UploadFailure(409,"ERR_INVALID_STATE","Đợt upload đã đóng hoặc hết hạn.");
        if(inspection.Problem is {} p)item=item with{UploadStatus="REJECTED",Error=new(p.Code,"Ảnh không hợp lệ: "+p.Issue,false)};
        else if(inspection.Image!.Sha256!=item.Sha256)item=item with{UploadStatus="REJECTED",Error=new("ERR_VALIDATION","Checksum không khớp manifest.",false)};
        else
        {
            bytes.Position=0;await storage.PutAsync(Container,StageKey(batch,itemId),bytes,inspection.Image.MimeType,ct);
            item=item with{UploadStatus="STAGED",ContentType=inspection.Image.MimeType,Length=inspection.Image.SizeBytes};
        }
        await SaveAsync(state with{Items=state.Items.Select(i=>i.ItemId==itemId?item:i).ToArray()},ct);return item;
    }
    public async Task<AcceptedResponse> FinalizeAsync(Guid actor,Guid? clinic,Guid batch,string? correlation,CancellationToken ct)
    {
        await using var held=await LockAsync(batch,ct);var state=await GetAsync(actor,clinic,batch,ct);
        if(state.FinalizedResponse is {} replay)return replay;
        if(state.Status=="EXPIRED")throw new UploadFailure(409,"ERR_INVALID_STATE","Đợt upload đã hết hạn.");
        if(state.Items.Any(i=>i.UploadStatus=="DECLARED"))throw new UploadFailure(409,"ERR_UPLOAD_INCOMPLETE","Còn ảnh chưa upload.",true);
        if(state.ExpiresAt<=clock.GetUtcNow().UtcDateTime && state.Status=="UPLOADING")throw new UploadFailure(409,"ERR_INVALID_STATE","Đợt upload đã hết hạn.");
        state=state with{Status="FINALIZING"};await SaveAsync(state,ct);
        foreach(var candidate in state.Items.Where(i=>i.UploadStatus is "STAGED" or "UNKNOWN").ToArray())
        {
            var item=candidate;var accepting=false;
            try
            {
                // A lost response can be recovered directly from the committed acceptance, even if staging was lost.
                var accepted=await ReadAcceptedAsync(state,item,ct);
                if(accepted is not null)
                {
                    item=item with{UploadStatus="ACCEPTED",AnalysisId=accepted.Items.Single().AnalysisId,
                        Receipt=accepted.Items.Single(),ChildBatchId=accepted.BatchId,Error=null};
                    state=state with{Items=state.Items.Select(i=>i.ItemId==item.ItemId?item:i).ToArray()};
                    await SaveAsync(state,ct);continue;
                }
                await using var source=await storage.OpenReadAsync(Container,StageKey(batch,item.ItemId),ct);
                var content=await ReadBoundedAsync(source,ct);
                if(content.LongLength!=item.Length || Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()!=item.Sha256)
                {
                    if(item.UploadStatus=="UNKNOWN")throw UploadFailure.Dependency("Admission integrity reconciliation");
                    throw UploadFailure.Validation(new ErrorDetail("item","staged_integrity_mismatch"));
                }
                // Durable intent precedes any downstream operation: an unacknowledged acceptance is never cleanup-safe.
                item=item with{UploadStatus="UNKNOWN"};
                state=state with{Items=state.Items.Select(i=>i.ItemId==item.ItemId?item:i).ToArray()};await SaveAsync(state,ct);
                var command=new UploadCommand(actor,clinic,StableId($"aura-item-admission:{batch:D}:{item.ItemId:D}"),correlation,
                    item.PatientId.ToString("D"),item.Modality,item.EyeSide,[new StagedFile(item.FileName,item.ContentType,content)]){ParentBatchId=batch};
                accepting=true;
                var result=await uploads.CreateAsync(command,ct);var receipt=result.Response.Items.Single();
                item=item with{UploadStatus="ACCEPTED",AnalysisId=receipt.AnalysisId,Receipt=receipt,ChildBatchId=result.Response.BatchId,Error=null};
            }
            catch(UploadFailure e)when(!e.Retryable && e.Status is 400 or 402 or 413 or 415 && (accepting || candidate.UploadStatus!="UNKNOWN"))
            {item=item with{UploadStatus="REJECTED",Error=new(e.Code,e.Message,false)};}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception){item=item with{UploadStatus="UNKNOWN",Error=new("ERR_NETWORK","Kết quả tiếp nhận đang được đối soát.",true)};}
            state=state with{Items=state.Items.Select(i=>i.ItemId==item.ItemId?item:i).ToArray()};await SaveAsync(state,ct);
        }
        if(state.Items.Any(i=>i.UploadStatus=="UNKNOWN"))throw UploadFailure.Dependency("Admission reconciliation");
        var response=new AcceptedResponse(batch,state.Items.Select(i=>i.Receipt??new(i.ItemId,i.FileName,"REJECTED",i.Error)).ToArray(),state.CreatedAt,state.StatusUrl);
        state=state with{Status="FINALIZED",FinalizedResponse=response};
        await using var conn=await db.OpenConnectionAsync(ct);await using var tx=await conn.BeginTransactionAsync(ct);
        await using(var close=new NpgsqlCommand("UPDATE analysis.batches SET admission_complete=true,total_images=$2,status=CASE WHEN $2=0 THEN 'FAILED' ELSE status END,updated_at=now() WHERE id=$1",conn,tx))
        {close.Parameters.AddWithValue(batch);close.Parameters.AddWithValue(state.Items.Count(i=>i.UploadStatus=="ACCEPTED"));await close.ExecuteNonQueryAsync(ct);}
        await using(var complete=new NpgsqlCommand("SELECT analysis.ensure_batch_completion($1,$2)",conn,tx))
        {complete.Parameters.AddWithValue(state.TenantId);complete.Parameters.AddWithValue(batch);await complete.ExecuteNonQueryAsync(ct);}
        await WriteStateAsync(state,conn,tx,ct);await tx.CommitAsync(ct);return response;
    }
    public async Task<BatchStatusResponse?> TryReadAsync(Guid actor,Guid? clinic,Guid batch,string? correlation,CancellationToken ct)
    {
        var state=await ReadAsync(batch,ct);if(state is null)return null;
        await AuthorizeAsync(state,actor,clinic,"ANALYSIS_READ",correlation,ct);
        var rows=new List<BatchStatusRow>();
        foreach(var item in state.Items)
        {
            if(item.ChildBatchId is {} child)
            {
                var identity=await progress.FindIdentityAsync(child,ct)??throw UploadFailure.Dependency("Batch identity");
                if(identity.TenantId!=state.TenantId || identity.PatientId!=item.PatientId)throw UploadFailure.Dependency("Batch identity");
                var snapshot=await progress.ReadAsync(identity,ct)??throw UploadFailure.Dependency("Batch progress");rows.AddRange(snapshot.Items);
            }
        }
        var rejected=state.Items.Count(i=>i.UploadStatus=="REJECTED");
        var response=rows.Count>0?BatchStatusProjection.Create(new(batch,state.CreatedAt,rows))
            :new BatchStatusResponse(batch,[],state.CreatedAt,state.Status=="FINALIZED"?"FAILED":"IN_PROGRESS",
                new(0,0,0,0,0,BatchStatusProjection.ProcessingStatuses.ToDictionary(s=>s,_=>0)));
        return response with{TotalDeclared=state.Items.Count,AdmissionRejected=rejected,UploadPending=state.Items.Count-rejected-rows.Count,
            BatchStatus=state.Status!="FINALIZED"?"IN_PROGRESS":response.BatchStatus};
    }
    private async Task AuthorizeAsync(UploadInventory state,Guid actor,Guid? clinic,string action,string? correlation,CancellationToken ct)
    {
        if(action=="ANALYSIS_CREATE" && state.RequesterId!=actor || state.ClinicId!=clinic)throw Missing();
        if(state.Items.All(i=>i.UploadStatus=="REJECTED") && state.RequesterId!=actor)throw Missing();
        foreach(var item in state.Items.Where(i=>i.UploadStatus!="REJECTED"))
        {
            var d=await access.ResolveAsync(new(actor,clinic,item.PatientId,action,item.AnalysisId){CorrelationId=correlation},ct);
            if(!d.Allowed || d.TenantId!=state.TenantId || d.OwnerPrincipalId!=state.OwnerId)throw Missing();
        }
    }
    private async Task<byte[]> ReadBoundedAsync(Stream source,CancellationToken ct)
    {
        using var result=new MemoryStream();var chunk=new byte[64*1024];
        while(true)
        {
            var count=await source.ReadAsync(chunk,ct);if(count==0)break;
            if(result.Length+count>options.MaxImageBytes)throw UploadFailure.Validation(new ErrorDetail("item","staged_image_too_large"));
            await result.WriteAsync(chunk.AsMemory(0,count),ct);
        }
        return result.ToArray();
    }
    private async Task<AcceptedResponse?> ReadAcceptedAsync(UploadInventory state,UploadInventoryItem item,CancellationToken ct)
    {
        var key=StableId($"aura-item-admission:{state.BatchId:D}:{item.ItemId:D}");
        await using var command=db.CreateCommand("""
            SELECT r.accepted_response::text FROM analysis.analysis_requests r
            WHERE r.tenant_id=$1 AND r.requester_id=$2 AND r.idempotency_key=$3 AND r.patient_id=$4
              AND r.request_fingerprint=$5 AND r.clinic_context_id IS NOT DISTINCT FROM $6
              AND EXISTS(SELECT 1 FROM analysis.images i WHERE i.analysis_request_id=r.id AND i.tenant_id=r.tenant_id AND i.batch_id=$7);
            """);
        command.Parameters.AddWithValue(state.TenantId);command.Parameters.AddWithValue(state.RequesterId);
        command.Parameters.AddWithValue(key);command.Parameters.AddWithValue(item.PatientId);
        command.Parameters.AddWithValue(RequestFingerprint.Compute(item.PatientId,item.Modality,item.EyeSide,[item.Sha256]));
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)state.ClinicId??DBNull.Value);command.Parameters.AddWithValue(state.BatchId);
        if(await command.ExecuteScalarAsync(ct) is not string json)return null;
        var accepted=JsonSerializer.Deserialize<AcceptedResponse>(json,UploadJson.Options);
        if(accepted is null || accepted.Items.Count!=1 || accepted.Items[0].AnalysisId==Guid.Empty)
            throw UploadFailure.Dependency("Accepted item journal");
        return accepted;
    }
    public async Task<int> ReconcilePendingAsync(CancellationToken ct,Guid? targetBatchId=null)
    {
        var ids=new List<Guid>();
        await using(var cmd=db.CreateCommand("SELECT id FROM analysis.upload_batches WHERE state->>'status'='FINALIZING' AND reconciliation_next_at<=now() AND ($1::uuid IS NULL OR id=$1) ORDER BY reconciliation_next_at,id LIMIT 20"))
        {cmd.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)targetBatchId??DBNull.Value);await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))ids.Add(reader.GetGuid(0));}
        var completed=0;
        foreach(var id in ids)
        {
            var state=await ReadAsync(id,ct);if(state is null)continue;
            // Durable scheduling provides fairness/backoff. Concurrent reconcilers still share the per-batch admission fence.
            await using(var schedule=db.CreateCommand("UPDATE analysis.upload_batches SET reconciliation_attempts=reconciliation_attempts+1,reconciliation_next_at=now()+make_interval(secs=>least(1800,30*power(2,least(reconciliation_attempts,6)))) WHERE id=$1"))
            {schedule.Parameters.AddWithValue(id);await schedule.ExecuteNonQueryAsync(ct);}
            try{await FinalizeAsync(state.RequesterId,state.ClinicId,id,$"reconcile-{id:N}",ct);completed++;}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception)
            {
                await using var audit=db.CreateCommand("""
                    INSERT INTO analysis.audit_logs(id,tenant_id,actor_type,action,target_type,requested_target_id,outcome,correlation_id,metadata)
                    VALUES($1,$2,'SERVICE','UPLOAD_ADMISSION_RECONCILIATION','UPLOAD_BATCH',$3,'ERROR',$4,'{"decision":"HOLD","automaticRelease":false}')
                    """);
                audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(state.TenantId);audit.Parameters.AddWithValue(id.ToString("D"));
                audit.Parameters.AddWithValue($"reconcile-{id:N}");await audit.ExecuteNonQueryAsync(ct);
            }
        }
        return completed;
    }

    /// <summary>Expires unaccepted stages; UNKNOWN/FINALIZING is retained for reconciliation, never timer-refunded.</summary>
    public async Task<int> CleanupExpiredAsync(CancellationToken ct)
    {
        var ids=new List<Guid>();
        await using(var command=db.CreateCommand("""
            SELECT b.id FROM analysis.upload_batches b WHERE b.expires_at<=$1
            AND NOT EXISTS(SELECT 1 FROM analysis.audit_logs a WHERE a.action='UPLOAD_STAGE_CLEANUP'
              AND a.target_type='UPLOAD_BATCH' AND a.requested_target_id=b.id::text AND a.metadata->>'phase'='DONE')
            ORDER BY CASE WHEN b.state->>'status'='FINALIZING' THEN 1 ELSE 0 END,b.expires_at,b.id LIMIT 20
            """))
        {
            command.Parameters.AddWithValue(clock.GetUtcNow().UtcDateTime);
            await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))ids.Add(reader.GetGuid(0));
        }
        var deleted=0;
        foreach(var id in ids)
        {
            AdvisoryLease held;
            try{held=await LockAsync(id,ct);}catch(UploadFailure e)when(e.Status==409){continue;}
            await using(held)
            {
                var state=await ReadAsync(id,ct);if(state is null)continue;
                if(state.Status=="FINALIZING" || state.Items.Any(i=>i.UploadStatus=="UNKNOWN"))
                {await CleanupAuditAsync(state,null,"HOLD",ct);continue;}
                if(state.Status=="UPLOADING")
                {
                    state=state with{Status="EXPIRED"};
                    await using var conn=await db.OpenConnectionAsync(ct);await using var tx=await conn.BeginTransactionAsync(ct);
                    await using(var close=new NpgsqlCommand("UPDATE analysis.batches SET admission_complete=true,status='FAILED',updated_at=now() WHERE id=$1",conn,tx))
                    {close.Parameters.AddWithValue(id);await close.ExecuteNonQueryAsync(ct);}
                    await WriteStateAsync(state,conn,tx,ct);await tx.CommitAsync(ct);
                }
                var complete=true;
                foreach(var item in state.Items)
                {
                    var done=StableId($"aura-stage-cleanup:{id:D}:{item.ItemId:D}:DONE");
                    await using var check=db.CreateCommand("SELECT EXISTS(SELECT 1 FROM analysis.audit_logs WHERE id=$1)");check.Parameters.AddWithValue(done);
                    if(await check.ExecuteScalarAsync(ct) is true)continue;
                    await CleanupAuditAsync(state,item.ItemId,"INTENT",ct);
                    try
                    {
                        await storage.DeleteAsync(Container,StageKey(id,item.ItemId),ct);
                        await CleanupAuditAsync(state,item.ItemId,"DONE",ct);deleted++;
                    }
                    catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch{complete=false;await CleanupAuditAsync(state,item.ItemId,"ERROR",ct);}
                }
                if(complete)await CleanupAuditAsync(state,null,"DONE",ct);
            }
        }
        return deleted;
    }
    private async Task CleanupAuditAsync(UploadInventory state,Guid? item,string phase,CancellationToken ct)
    {
        var id=StableId($"aura-stage-cleanup:{state.BatchId:D}:{item?.ToString("D")??"batch"}:{phase}");
        await using var command=db.CreateCommand("""
            INSERT INTO analysis.audit_logs(id,tenant_id,actor_type,action,target_type,requested_target_id,outcome,correlation_id,metadata)
            VALUES($1,$2,'SERVICE','UPLOAD_STAGE_CLEANUP',$7,$3,$4,$5,$6)
            ON CONFLICT(id) DO NOTHING;
            """);
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(state.TenantId);
        command.Parameters.AddWithValue(item?.ToString("D")??state.BatchId.ToString("D"));
        command.Parameters.AddWithValue(phase=="ERROR"?"ERROR":"ALLOWED");command.Parameters.AddWithValue($"cleanup-{state.BatchId:N}");
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(new{uploadBatchId=state.BatchId,itemId=item,phase}));
        command.Parameters.AddWithValue(item.HasValue?"UPLOAD_ITEM":"UPLOAD_BATCH");
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<UploadInventory?> ReadAsync(Guid id,CancellationToken ct)
    {
        await using var cmd=db.CreateCommand("SELECT state::text FROM analysis.upload_batches WHERE id=$1");cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) is string json?JsonSerializer.Deserialize<UploadInventory>(json,UploadJson.Options):null;
    }
    private async Task SaveAsync(UploadInventory state,CancellationToken ct)
    {await using var conn=await db.OpenConnectionAsync(ct);await WriteStateAsync(state,conn,null,ct);}
    private static async Task WriteStateAsync(UploadInventory state,NpgsqlConnection conn,NpgsqlTransaction? tx,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("UPDATE analysis.upload_batches SET state=$2,updated_at=now() WHERE id=$1",conn,tx);
        cmd.Parameters.AddWithValue(state.BatchId);cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(state,UploadJson.Options));await cmd.ExecuteNonQueryAsync(ct);
    }
    private async Task<AdvisoryLease> LockAsync(Guid id,CancellationToken ct)
    {
        var conn=await db.OpenConnectionAsync(ct);
        try
        {
            await using var cmd=new NpgsqlCommand("SELECT pg_try_advisory_lock($1)",conn);cmd.Parameters.AddWithValue(BitConverter.ToInt64(id.ToByteArray()));
            if(await cmd.ExecuteScalarAsync(ct) is not true)throw UploadRecovery.InProgress();return new(conn,BitConverter.ToInt64(id.ToByteArray()));
        }
        catch{await conn.DisposeAsync();throw;}
    }
    private sealed class AdvisoryLease(NpgsqlConnection connection,long key):IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd=new NpgsqlCommand("SELECT pg_advisory_unlock($1)",connection){CommandTimeout=5};cmd.Parameters.AddWithValue(key);
                await cmd.ExecuteScalarAsync(CancellationToken.None);
            }
            catch{NpgsqlConnection.ClearPool(connection);}
            finally{await connection.DisposeAsync();}
        }
    }
    private sealed class StagedFile(string name,string? type,byte[] bytes):IUploadedFile
    {public string FileName=>name;public string? ContentType=>type;public long Length=>bytes.LongLength;public Stream OpenReadStream()=>new MemoryStream(bytes,false);}
}

/// <summary>Resolves a fresh scoped service each minute; cleanup never deletes accepted originals/results.</summary>
public sealed class ItemUploadCleanupWorker(IServiceScopeFactory scopes,ILogger<ItemUploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope=scopes.CreateScope();
                var uploads=scope.ServiceProvider.GetRequiredService<ItemUploadService>();
                await uploads.ReconcilePendingAsync(stoppingToken);
                await uploads.CleanupExpiredAsync(stoppingToken);
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception ex){logger.LogWarning(ex,"Staged upload cleanup requires retry.");}
        }while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
