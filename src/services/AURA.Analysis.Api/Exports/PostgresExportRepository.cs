using Npgsql;
using NpgsqlTypes;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Exports;

public interface IExportRepository
{
    Task<ExportSnapshot?> FindByKeyAsync(Guid tenantId, Guid actorId, string kind, string key, CancellationToken ct);
    Task<ExportSnapshot?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ReadCampaignPatientScopeAsync(Guid tenantId, Guid campaignId, CancellationToken ct);
    Task<IReadOnlyList<ResearchSourceRow>> ReadResearchRowsAsync(Guid tenantId, Guid clinicId, Guid? campaignId,
        IReadOnlyCollection<Guid> patientIds, CancellationToken ct);
    Task InsertAsync(ExportSnapshot snapshot, CancellationToken ct);
}

public sealed class PostgresExportRepository(NpgsqlDataSource dataSource) : IExportRepository
{
    public async Task<ExportSnapshot?> FindByKeyAsync(Guid tenantId, Guid actorId, string kind, string key, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id,tenant_id,clinic_id,actor_id,kind,campaign_id,format,object_key,content_type,sha256,fingerprint,COALESCE(snapshot->>'purpose',''),snapshot::text,idempotency_key,patient_scope,expires_at FROM analysis.export_snapshots WHERE tenant_id=$1 AND actor_id=$2 AND kind=$3 AND idempotency_key=$4 AND expires_at>now()");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(actorId); cmd.Parameters.AddWithValue(kind); cmd.Parameters.AddWithValue(key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<ExportSnapshot?> FindByIdAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id,tenant_id,clinic_id,actor_id,kind,campaign_id,format,object_key,content_type,sha256,fingerprint,COALESCE(snapshot->>'purpose',''),snapshot::text,idempotency_key,patient_scope,expires_at FROM analysis.export_snapshots WHERE id=$1 AND expires_at>now()");
        cmd.Parameters.AddWithValue(id); await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<Guid>> ReadCampaignPatientScopeAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT DISTINCT b.patient_id FROM analysis.batches b WHERE b.tenant_id=$1 AND b.campaign_id=$2 ORDER BY b.patient_id");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(campaignId);
        var ids = new List<Guid>(); await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(r.GetGuid(0));
        return ids;
    }

    public async Task<IReadOnlyList<ResearchSourceRow>> ReadResearchRowsAsync(Guid tenantId, Guid clinicId, Guid? campaignId,
        IReadOnlyCollection<Guid> patientIds, CancellationToken ct)
    {
        if (patientIds.Count == 0) return [];
        await using var cmd = dataSource.CreateCommand("""
            SELECT r.patient_id, result.id, result.model_version, result.config_version, result.threshold_version,
                   COALESCE(review.version,0),result.risk_assessment->>'riskLevel',
                   COALESCE(lower(result.metrics #>> '{research,avr,measurement_available}')='true',false),
                   NULLIF(result.metrics #>> '{research,avr,avr}','')::numeric
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
            LEFT JOIN LATERAL (SELECT candidate.campaign_id FROM analysis.batches candidate
                WHERE candidate.tenant_id=i.tenant_id AND (candidate.id=i.batch_id OR candidate.id=i.analysis_request_id)
                ORDER BY candidate.id LIMIT 1) b ON true
            LEFT JOIN LATERAL (SELECT x.id,x.model_version,x.config_version,x.threshold_version,x.risk_assessment,x.metrics FROM analysis.analysis_results x
                WHERE x.tenant_id=i.tenant_id AND x.image_id=i.id ORDER BY x.version DESC LIMIT 1) result ON true
            LEFT JOIN LATERAL (SELECT d.version FROM analysis.doctor_reviews d
                WHERE d.tenant_id=i.tenant_id AND d.image_id=i.id ORDER BY d.version DESC LIMIT 1) review ON true
            WHERE i.tenant_id=$1 AND r.owner_id=$2 AND r.patient_id=ANY($3)
              AND ($4::uuid IS NULL OR b.campaign_id=$4) AND i.processing_status='COMPLETED'
            ORDER BY r.patient_id,i.created_at,i.id LIMIT 10001
            """);
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(clinicId);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Array|NpgsqlDbType.Uuid, Value=patientIds.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Uuid, Value=campaignId is { } c ? c : DBNull.Value });
        var rows=new List<ResearchSourceRow>(); await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) rows.Add(new(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetString(2),
            r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetInt32(5),r.IsDBNull(6)?null:r.GetString(6),r.GetBoolean(7),r.IsDBNull(8)?null:r.GetDecimal(8)));
        return rows;
    }

    public async Task InsertAsync(ExportSnapshot snapshot, CancellationToken ct)
    {
        await using var connection=await dataSource.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using var cmd=new NpgsqlCommand("INSERT INTO analysis.export_snapshots(id,tenant_id,clinic_id,actor_id,kind,campaign_id,format,object_key,content_type,sha256,fingerprint,idempotency_key,patient_scope,snapshot,expires_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14::jsonb,$15)",connection,tx);
        cmd.Parameters.AddWithValue(snapshot.Id);cmd.Parameters.AddWithValue(snapshot.TenantId);cmd.Parameters.AddWithValue(snapshot.ClinicId);cmd.Parameters.AddWithValue(snapshot.ActorId);
        cmd.Parameters.AddWithValue(snapshot.Kind);cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=snapshot.CampaignId is{} campaign?campaign:DBNull.Value});
        cmd.Parameters.AddWithValue(snapshot.Format);cmd.Parameters.AddWithValue(snapshot.ObjectKey);cmd.Parameters.AddWithValue(snapshot.ContentType);
        cmd.Parameters.AddWithValue(snapshot.Sha256);
        cmd.Parameters.AddWithValue(snapshot.Fingerprint);cmd.Parameters.AddWithValue(snapshot.IdempotencyKey);
        cmd.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Array|NpgsqlDbType.Uuid,Value=snapshot.PatientScope.ToArray()});
        cmd.Parameters.AddWithValue(snapshot.SnapshotJson);cmd.Parameters.AddWithValue(snapshot.ExpiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
        await using var audit=new NpgsqlCommand("INSERT INTO analysis.audit_logs(id,tenant_id,actor_id,actor_type,action,target_type,requested_target_id,outcome,correlation_id,metadata) VALUES($1,$2,$3,'USER','EXPORT_CREATED','analysis.export_snapshots',$4,'ALLOWED',$5,$6::jsonb)",connection,tx);
        audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(snapshot.TenantId);audit.Parameters.AddWithValue(snapshot.ActorId);
        audit.Parameters.AddWithValue(snapshot.Id.ToString("D"));audit.Parameters.AddWithValue(snapshot.CorrelationId);
        audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new{kind=snapshot.Kind,format=snapshot.Format,sha256=snapshot.Sha256}));
        await audit.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }

    private static ExportSnapshot Read(NpgsqlDataReader r)
    {
        var json=r.GetString(12);using var doc=System.Text.Json.JsonDocument.Parse(json);
        var purpose=doc.RootElement.TryGetProperty("purpose",out var p)?p.GetString()??"":"";
        var correlation=doc.RootElement.TryGetProperty("correlationId",out var corr)?corr.GetString()??"": "";
        return new(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetString(4),r.IsDBNull(5)?null:r.GetGuid(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),purpose,json,r.GetString(13),r.GetFieldValue<Guid[]>(14),r.GetFieldValue<DateTimeOffset>(15),correlation);
    }
}
