using AURA.Analysis.Api.Upload;
using Npgsql;
using NpgsqlTypes;

namespace AURA.Analysis.Api.Alerts;

public sealed class PostgresAlertRepository(NpgsqlDataSource dataSource) : IAlertRepository, IAlertScopeRepository
{
    public async Task<IReadOnlyList<AlertCandidate>> ReadCandidatesAsync(
        Guid tenantId, Guid clinicId, IReadOnlyCollection<Guid>? patientIds, int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT i.tenant_id, b.clinic_id, b.patient_id, i.id, result.id, result.version,
                   CASE WHEN review.analysis_result_id = result.id THEN review.version ELSE NULL END,
                   i.processing_status,
                   COALESCE(result.risk_assessment->>'riskLevel', result.risk_assessment->>'risk_level'),
                   CASE WHEN review.analysis_result_id = result.id THEN review.corrected_risk_level ELSE NULL END,
                   (review.analysis_result_id = result.id) AS has_current_review,
                   CASE lower(COALESCE(result.metrics #>> '{research,avr,measurement_available}', ''))
                     WHEN 'true' THEN 'AVAILABLE' WHEN 'false' THEN 'UNAVAILABLE' ELSE 'UNKNOWN' END,
                   i.created_at
            FROM analysis.images i
            JOIN analysis.batches b ON b.tenant_id = i.tenant_id AND b.id = i.batch_id
            LEFT JOIN LATERAL (
                SELECT r.id, r.version, r.risk_assessment, r.metrics
                FROM analysis.analysis_results r
                WHERE r.tenant_id = i.tenant_id AND r.image_id = i.id
                ORDER BY r.version DESC LIMIT 1
            ) result ON true
            LEFT JOIN LATERAL (
                SELECT d.version, d.analysis_result_id, d.corrected_risk_level
                FROM analysis.doctor_reviews d
                WHERE d.tenant_id = i.tenant_id AND d.image_id = i.id
                ORDER BY d.version DESC LIMIT 1
            ) review ON true
            WHERE i.tenant_id = $1 AND b.clinic_id = $2
              AND ($3::uuid[] IS NULL OR b.patient_id = ANY($3))
              AND (i.processing_status IN ('COMPLETED','LOW_QUALITY')
                   OR lower(COALESCE(result.metrics #>> '{research,avr,measurement_available}', '')) = 'true')
              AND result.id IS NOT NULL
            ORDER BY i.created_at DESC, i.id DESC
            LIMIT $4
            """);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(clinicId);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = patientIds is null ? DBNull.Value : patientIds.ToArray() });
        command.Parameters.AddWithValue(limit);

        var candidates = new List<AlertCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(new AlertCandidate(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
                reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetBoolean(10), reader.GetString(11), reader.GetFieldValue<DateTimeOffset>(12)));
        }
        return candidates;
    }

    public async Task<IReadOnlyList<AlertStateRecord>> ReadStatesAsync(
        Guid tenantId, Guid clinicId, IReadOnlyCollection<Guid> analysisIds, CancellationToken ct)
    {
        if (analysisIds.Count == 0) return [];
        await using var command = dataSource.CreateCommand("""
            SELECT analysis_id, result_id, review_version, rule_version, kind, state, state_version, reason
            FROM analysis.alerts
            WHERE tenant_id = $1 AND clinic_id = $2 AND analysis_id = ANY($3)
            """);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(clinicId);
        command.Parameters.AddWithValue(analysisIds.ToArray());
        var states = new List<AlertStateRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var source = new AlertSource(tenantId, reader.GetGuid(0), reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetInt32(3),
                $"scrum385-v{reader.GetInt32(3)}", reader.GetString(4));
            states.Add(new(source, reader.GetString(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return states;
    }

    public async Task MaterializeSourcesAsync(Guid clinicId, Guid actorId,
        IReadOnlyCollection<AlertSource> sources, string correlationId, CancellationToken ct)
    {
        if (sources.Count == 0) return;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var source in sources)
        {
            Guid? insertedId;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO analysis.alerts
                    (id, tenant_id, clinic_id, analysis_id, result_id, review_version, rule_version, kind, state)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'OPEN')
                ON CONFLICT (tenant_id, analysis_id, result_id, (COALESCE(review_version,0)), rule_version, kind)
                DO NOTHING
                RETURNING id
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(AlertSourceRules.ToAlertId(source));
                insert.Parameters.AddWithValue(source.TenantId);
                insert.Parameters.AddWithValue(clinicId);
                insert.Parameters.AddWithValue(source.AnalysisId);
                insert.Parameters.AddWithValue(source.ResultId);
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer,
                    Value = source.ReviewVersion is null ? DBNull.Value : source.ReviewVersion.Value });
                insert.Parameters.AddWithValue(source.RuleVersionNumber);
                insert.Parameters.AddWithValue(source.Kind);
                var scalar = await insert.ExecuteScalarAsync(ct);
                insertedId = scalar is Guid id ? id : null;
            }

            if (insertedId is null) continue;
            await using var history = new NpgsqlCommand("""
                INSERT INTO analysis.alert_history
                    (id, tenant_id, alert_id, actor_id, from_state, to_state, state_version, reason, correlation_id)
                VALUES ($1,$2,$3,$4,NULL,'OPEN',1,NULL,$5)
                """, connection, transaction);
            history.Parameters.AddWithValue(Guid.NewGuid());
            history.Parameters.AddWithValue(source.TenantId);
            history.Parameters.AddWithValue(insertedId.Value);
            history.Parameters.AddWithValue(actorId);
            history.Parameters.AddWithValue(correlationId);
            await history.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<AlertScope?> FindScopeAsync(Guid alertId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT a.tenant_id, a.clinic_id, b.patient_id, a.analysis_id
            FROM analysis.alerts a
            JOIN analysis.images i ON i.tenant_id = a.tenant_id AND i.id = a.analysis_id
            JOIN analysis.batches b ON b.tenant_id = i.tenant_id AND b.id = i.batch_id
            WHERE a.id = $1
            """);
        command.Parameters.AddWithValue(alertId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AlertScope(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3))
            : null;
    }

    public async Task<AlertStateRecord> ChangeStateAsync(
        Guid clinicId, Guid actorId, AlertSource source, int expectedVersion, string targetStatus,
        string reason, string correlationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var create = new NpgsqlCommand("""
            INSERT INTO analysis.alerts
                (id, tenant_id, clinic_id, analysis_id, result_id, review_version, rule_version, kind, state)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,'OPEN')
            ON CONFLICT (tenant_id, analysis_id, result_id, (COALESCE(review_version,0)), rule_version, kind) DO NOTHING
            """, connection, transaction))
        {
            create.Parameters.AddWithValue(AlertSourceRules.ToAlertId(source));
            create.Parameters.AddWithValue(source.TenantId);
            create.Parameters.AddWithValue(clinicId);
            create.Parameters.AddWithValue(source.AnalysisId);
            create.Parameters.AddWithValue(source.ResultId);
            create.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer,
                Value = source.ReviewVersion is null ? DBNull.Value : source.ReviewVersion.Value });
            create.Parameters.AddWithValue(source.RuleVersionNumber);
            create.Parameters.AddWithValue(source.Kind);
            await create.ExecuteNonQueryAsync(ct);
        }

        string currentStatus;
        int currentVersion;
        await using (var read = new NpgsqlCommand("""
            SELECT state, state_version FROM analysis.alerts
            WHERE tenant_id=$1 AND analysis_id=$2 AND result_id=$3
              AND COALESCE(review_version,0)=COALESCE($4,0) AND rule_version=$5 AND kind=$6
            FOR UPDATE
            """, connection, transaction))
        {
            read.Parameters.AddWithValue(source.TenantId);
            read.Parameters.AddWithValue(source.AnalysisId);
            read.Parameters.AddWithValue(source.ResultId);
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer,
                Value = source.ReviewVersion is null ? DBNull.Value : source.ReviewVersion.Value });
            read.Parameters.AddWithValue(source.RuleVersionNumber);
            read.Parameters.AddWithValue(source.Kind);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy alert.");
            currentStatus = reader.GetString(0);
            currentVersion = reader.GetInt32(1);
        }

        var allowedTransition = (currentStatus, targetStatus) is ("OPEN", "ACKNOWLEDGED") or ("ACKNOWLEDGED", "CLOSED");
        if (!allowedTransition || currentVersion != expectedVersion)
            throw new UploadFailure(409, "ERR_CONFLICT", "Trạng thái hoặc phiên bản alert đã thay đổi.");

        var nextVersion = currentVersion + 1;
        await using (var update = new NpgsqlCommand("""
            UPDATE analysis.alerts SET state=$1, state_version=$2, reason=$3, updated_at=now()
            WHERE tenant_id=$4 AND analysis_id=$5 AND result_id=$6
              AND COALESCE(review_version,0)=COALESCE($7,0) AND rule_version=$8 AND kind=$9
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(targetStatus);
            update.Parameters.AddWithValue(nextVersion);
            update.Parameters.AddWithValue(reason);
            update.Parameters.AddWithValue(source.TenantId);
            update.Parameters.AddWithValue(source.AnalysisId);
            update.Parameters.AddWithValue(source.ResultId);
            update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer,
                Value = source.ReviewVersion is null ? DBNull.Value : source.ReviewVersion.Value });
            update.Parameters.AddWithValue(source.RuleVersionNumber);
            update.Parameters.AddWithValue(source.Kind);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using (var history = new NpgsqlCommand("""
            INSERT INTO analysis.alert_history
                (id, tenant_id, alert_id, actor_id, from_state, to_state, state_version, reason, correlation_id)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """, connection, transaction))
        {
            history.Parameters.AddWithValue(Guid.NewGuid());
            history.Parameters.AddWithValue(source.TenantId);
            history.Parameters.AddWithValue(AlertSourceRules.ToAlertId(source));
            history.Parameters.AddWithValue(actorId);
            history.Parameters.AddWithValue(currentStatus);
            history.Parameters.AddWithValue(targetStatus);
            history.Parameters.AddWithValue(nextVersion);
            history.Parameters.AddWithValue(reason);
            history.Parameters.AddWithValue(correlationId);
            await history.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new AlertStateRecord(source, targetStatus, nextVersion, reason);
    }
}
