using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Processing;

namespace Aura.Worker.Analysis.Data.Repositories;

public sealed record DbBatchCompletionStatus(bool IsFinished,string BatchStatus,int Total,int Completed,int Failed);
public interface IAnalysisRepository
{
    Task<AnalysisRecord?> GetAnalysisAsync(Guid analysisId,CancellationToken ct);
    Task CommitSuccessAsync(Guid analysisId,JobLease lease,string resultJson,IReadOnlyList<AnnotationDescriptor> annotations,OutboxRecord evt,CancellationToken ct);
    Task CommitFailureAsync(Guid analysisId,JobLease lease,string status,string errorJson,OutboxRecord evt,CancellationToken ct);
    Task ScheduleRetryAsync(Guid analysisId,JobLease lease,JobRecord nextJob,TimeSpan delay,OutboxRecord command,CancellationToken ct);
    Task<DbBatchCompletionStatus?> CheckBatchCompletionAsync(Guid batchId,CancellationToken ct);
}

public sealed class AnalysisRepository(WorkerDbContext db) : IAnalysisRepository
{
    private sealed record Context(Guid TenantId,Guid RequestId,Guid BatchId,Guid PatientId,string CorrelationId);
    private NpgsqlConnection Connection()=>new(db.Database.GetConnectionString() ?? throw new InvalidOperationException("PostgreSQL is required."));
    private static NpgsqlCommand Cmd(NpgsqlConnection c,NpgsqlTransaction? tx,string sql,params object[] args)
    {
        var command=new NpgsqlCommand(sql,c,tx);
        foreach(var value in args)command.Parameters.AddWithValue(value);
        return command;
    }

    public async Task<AnalysisRecord?> GetAnalysisAsync(Guid analysisId,CancellationToken ct)
    {
        var image=await db.Analyses.AsNoTracking().FirstOrDefaultAsync(i=>i.AnalysisId==analysisId,ct);
        if(image is null)return null;
        var request=await db.AnalysisRequests.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==image.BatchId && r.TenantId==image.TenantId,ct)
            ?? throw new DataException("Image has no bound acceptance context.");
        image.PatientId=request.PatientId;image.RequesterId=request.RequesterId;image.ClinicContextId=request.ClinicContextId;
        return image;
    }

    private static async Task<Context> LockContext(NpgsqlConnection c,NpgsqlTransaction tx,Guid imageId,CancellationToken ct)
    {
        await using var command=Cmd(c,tx,"""
            SELECT r.tenant_id,r.id,COALESCE(i.batch_id,r.id),r.patient_id,r.correlation_id
            FROM analysis.images i JOIN analysis.analysis_requests r ON r.id=i.analysis_request_id AND r.tenant_id=i.tenant_id
            WHERE i.id=$1;
            """,imageId);
        Context context;
        await using(var reader=await command.ExecuteReaderAsync(ct)) {
            if(!await reader.ReadAsync(ct))throw new DataException("Unknown analysis.");
            context=new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetGuid(3),reader.GetString(4));
        }
        await using var parent=Cmd(c,tx,"SELECT id FROM analysis.batches WHERE tenant_id=$1 AND id=$2 FOR UPDATE;",context.TenantId,context.BatchId);
        if(await parent.ExecuteScalarAsync(ct) is not Guid)throw new DataException("Missing parent batch.");
        return context;
    }

    private static async Task Fence(NpgsqlConnection c,NpgsqlTransaction tx,Context context,Guid imageId,JobLease lease,string status,CancellationToken ct)
    {
        await using var command=Cmd(c,tx,"""
            UPDATE analysis.jobs SET status=$6,finished_at=clock_timestamp(),updated_at=clock_timestamp(),retryable=($6='RETRY_READY')
            WHERE id=$1 AND lease_owner=$2 AND fencing_token=$3 AND image_id=$4 AND tenant_id=$5
              AND lease_expires_at>clock_timestamp() AND status='RUNNING';
            """,lease.JobId,lease.Owner,lease.FencingToken,imageId,context.TenantId,status);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new JobLeaseLostException(lease.JobId);
    }

    public async Task CommitSuccessAsync(Guid analysisId,JobLease lease,string resultJson,IReadOnlyList<AnnotationDescriptor> annotations,OutboxRecord evt,CancellationToken ct)
    {
        using var parsed=JsonDocument.Parse(resultJson);
        var result=parsed.RootElement;
        string Required(string name)=>result.GetProperty(name).GetString() is {Length:>0} value ? value : throw new DataException($"Missing {name}.");
        if(result.GetProperty("isMock").GetBoolean())throw new DataException("Mock result cannot be committed in the real worker.");
        if(annotations.Count!=2 || annotations.Select(a=>a.Type).Order().SequenceEqual(new[]{"MASK","OVERLAY"})==false)
            throw new DataException("Expected mask and overlay metadata.");
        foreach(var a in annotations)
            if(a.FileId==Guid.Empty || a.SizeBytes<=0 || a.Width<=0 || a.Height<=0 || a.Sha256.Length!=64 || a.Sha256.Any(ch=>!Uri.IsHexDigit(ch)))
                throw new DataException("Invalid actual annotation metadata.");

        await using var c=Connection();await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var context=await LockContext(c,tx,analysisId,ct);
        await Fence(c,tx,context,analysisId,lease,"SUCCEEDED",ct);
        await using var versionCommand=Cmd(c,tx,"SELECT COALESCE(max(version),0)+1 FROM analysis.analysis_results WHERE image_id=$1;",analysisId);
        var version=Convert.ToInt32(await versionCommand.ExecuteScalarAsync(ct));
        var resultId=Guid.NewGuid();
        var risk=JsonSerializer.Serialize(new { riskAvailable=result.GetProperty("riskAvailable").GetBoolean(),riskLevel=result.GetProperty("riskLevel"),
            riskScore=result.GetProperty("riskScore"),confidenceScore=result.GetProperty("confidenceScore") });
        await using(var insert=Cmd(c,tx,"""
            INSERT INTO analysis.analysis_results(id,tenant_id,image_id,job_id,version,result_schema_version,
              risk_assessment,metrics,warnings,is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations)
            VALUES($1,$2,$3,$4,$5,'0.7',$6::jsonb,$7::jsonb,$8::jsonb,false,$9,$10,$11,$12,$13);
            """,resultId,context.TenantId,analysisId,lease.JobId,version,risk,result.GetProperty("metrics").GetRawText(),
            result.GetProperty("warnings").GetRawText(),Required("modelVersion"),Required("configVersion"),Required("thresholdVersion"),
            result.GetProperty("processingTimeMs").GetDouble(),Required("limitations"))) await insert.ExecuteNonQueryAsync(ct);
        foreach(var a in annotations)
        {
            await using var insert=Cmd(c,tx,"""
                INSERT INTO analysis.annotation_refs(id,tenant_id,image_id,analysis_result_id,file_id,type,object_key,mime_type,size_bytes,width,height,sha256)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12);
                """,Guid.NewGuid(),context.TenantId,analysisId,resultId,a.FileId,a.Type,a.ObjectKey,a.MimeType,a.SizeBytes,a.Width,a.Height,a.Sha256);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using(var update=Cmd(c,tx,"UPDATE analysis.images SET processing_status='COMPLETED',review_status='PENDING',updated_at=clock_timestamp() WHERE id=$1 AND tenant_id=$2;",analysisId,context.TenantId))await update.ExecuteNonQueryAsync(ct);
        var payload=JsonNode.Parse(evt.PayloadJson)!.AsObject();payload["data"]!["resultVersion"]=version;
        payload["data"]!["riskAvailable"]=result.GetProperty("riskAvailable").GetBoolean();
        payload["data"]!["riskLevel"]=JsonNode.Parse(result.GetProperty("riskLevel").GetRawText());
        evt.PayloadJson=payload.ToJsonString();
        await InsertOutbox(c,tx,context,analysisId,evt,ct);
        await CompleteBatch(c,tx,context,analysisId,ct);
        await tx.CommitAsync(ct);
    }

    public async Task CommitFailureAsync(Guid analysisId,JobLease lease,string status,string errorJson,OutboxRecord evt,CancellationToken ct)
    {
        if(status is not("FAILED" or "LOW_QUALITY" or "REJECTED"))throw new ArgumentException("Not a terminal failure.");
        await using var c=Connection();await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var context=await LockContext(c,tx,analysisId,ct);await Fence(c,tx,context,analysisId,lease,"FAILED",ct);
        await using(var update=Cmd(c,tx,"UPDATE analysis.images SET processing_status=$2,updated_at=clock_timestamp() WHERE id=$1;",analysisId,status))await update.ExecuteNonQueryAsync(ct);
        using var payload=JsonDocument.Parse(evt.PayloadJson);
        var code=payload.RootElement.GetProperty("data").GetProperty("errorCode").GetString()!;
        await using(var update=Cmd(c,tx,"UPDATE analysis.jobs SET error_code=$2,error_details=$3::jsonb WHERE id=$1;",lease.JobId,code,JsonSerializer.Serialize(new {code})))await update.ExecuteNonQueryAsync(ct);
        await InsertOutbox(c,tx,context,analysisId,evt,ct);await CompleteBatch(c,tx,context,analysisId,ct);await tx.CommitAsync(ct);
    }

    public async Task ScheduleRetryAsync(Guid analysisId,JobLease lease,JobRecord nextJob,TimeSpan delay,OutboxRecord command,CancellationToken ct)
    {
        if(delay<=TimeSpan.Zero || nextJob.MessageId!=command.OutboxId)throw new ArgumentException("Retry identity/delay mismatch.");
        await using var c=Connection();await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(ct);
        var context=await LockContext(c,tx,analysisId,ct);await Fence(c,tx,context,analysisId,lease,"RETRY_READY",ct);
        await using(var update=Cmd(c,tx,"UPDATE analysis.images SET processing_status='RETRY_READY',updated_at=clock_timestamp() WHERE id=$1;",analysisId))await update.ExecuteNonQueryAsync(ct);
        await using(var insert=Cmd(c,tx,"""
            INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,correlation_id,next_retry_at)
            VALUES($1,$2,$3,$4,$5,'QUEUED',$6,clock_timestamp()+make_interval(secs=>$7));
            """,nextJob.JobId,context.TenantId,analysisId,nextJob.MessageId,nextJob.AttemptNo,context.CorrelationId,delay.TotalSeconds))await insert.ExecuteNonQueryAsync(ct);
        command.NextPublishAt=DateTime.UtcNow.Add(delay);
        await InsertOutbox(c,tx,context,analysisId,command,ct);await tx.CommitAsync(ct);
    }

    private static async Task InsertOutbox(NpgsqlConnection c,NpgsqlTransaction tx,Context context,Guid imageId,OutboxRecord row,CancellationToken ct)
    {
        using var payload=JsonDocument.Parse(row.PayloadJson);var p=payload.RootElement;
        var job=p.TryGetProperty("messageId",out var id);if(!job)id=p.GetProperty("eventId");
        if(id.GetGuid()!=row.OutboxId || p.GetProperty("schemaVersion").GetString()!=row.SchemaVersion || p.GetProperty("tenantId").GetGuid()!=context.TenantId)
            throw new DataException("Outbox row/payload binding mismatch.");
        await using var command=Cmd(c,tx,"""
            INSERT INTO analysis.outbox_events(id,tenant_id,analysis_request_id,image_id,event_type,schema_version,payload,occurred_at,next_publish_at)
            VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9);
            """,row.OutboxId,context.TenantId,context.RequestId,imageId,row.EventType,row.SchemaVersion,row.PayloadJson,row.OccurredAt,
            row.NextPublishAt is {} next ? next : DBNull.Value);
        // Npgsql cannot infer the type of a null optional schedule.
        command.Parameters[8].NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.TimestampTz;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DbBatchCompletionStatus?> BatchStatus(NpgsqlConnection c,NpgsqlTransaction? tx,Guid batchId,CancellationToken ct)
    {
        await using var command=Cmd(c,tx,"""
            SELECT count(*),count(*) FILTER(WHERE processing_status IN ('COMPLETED','DUPLICATE')),
              count(*) FILTER(WHERE processing_status IN ('FAILED','LOW_QUALITY','REJECTED'))
            FROM analysis.images WHERE COALESCE(batch_id,analysis_request_id)=$1;
            """,batchId);
        await using var reader=await command.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
        var total=Convert.ToInt32(reader.GetInt64(0));var complete=Convert.ToInt32(reader.GetInt64(1));var failed=Convert.ToInt32(reader.GetInt64(2));
        if(total==0)return null;
        var finished=total==complete+failed;
        return new(finished,!finished?"IN_PROGRESS":failed==0?"COMPLETED":complete==0?"FAILED":"PARTIAL_FAILED",total,complete,failed);
    }

    private static async Task CompleteBatch(NpgsqlConnection c,NpgsqlTransaction tx,Context context,Guid imageId,CancellationToken ct)
    {
        await using var command=Cmd(c,tx,"SELECT analysis.ensure_batch_completion($1,$2);",context.TenantId,context.BatchId);
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<DbBatchCompletionStatus?> CheckBatchCompletionAsync(Guid batchId,CancellationToken ct)
    {
        if(batchId==Guid.Empty)return null;
        await using var c=Connection();await c.OpenAsync(ct);return await BatchStatus(c,null,batchId,ct);
    }
}
