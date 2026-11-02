using System.Text.Json;
using Npgsql;

namespace AURA.Analysis.Api.Upload;

public sealed class PostgresDoctorReviewRepository(NpgsqlDataSource dataSource) : IDoctorReviewRepository
{
    public async Task<DoctorReviewDto> SubmitAsync(DoctorReviewCommand request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        int baseVersion;
        int aiResultVersion;
        Guid requestId;
        string processingStatus;
        await using (var current = new NpgsqlCommand("""
            SELECT i.analysis_request_id, i.processing_status, i.row_version,
                   ar.id, ar.version
            FROM analysis.images i
            LEFT JOIN LATERAL (
                SELECT r.id, r.version
                FROM analysis.analysis_results r
                WHERE r.tenant_id = i.tenant_id AND r.image_id = i.id
                ORDER BY r.version DESC LIMIT 1
            ) ar ON true
            WHERE i.id = $1 AND i.tenant_id = $2 AND i.analysis_request_id IN (
                SELECT id FROM analysis.analysis_requests WHERE id = i.analysis_request_id AND patient_id = $3
            )
            FOR UPDATE OF i;
            """, connection, transaction))
        {
            current.Parameters.AddWithValue(request.AnalysisId);
            current.Parameters.AddWithValue(request.TenantId);
            current.Parameters.AddWithValue(request.PatientId);
            await using var reader = await current.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");
            requestId = reader.GetGuid(0);
            processingStatus = reader.GetString(1);
            baseVersion = reader.GetInt32(2);
            if (reader.IsDBNull(3) || reader.IsDBNull(4))
                throw new UploadFailure(409, "ERR_INVALID_STATE", "Ca phân tích chưa có kết quả AI để review.");
            aiResultVersion = reader.GetInt32(4);
        }
        if (processingStatus != "COMPLETED")
            throw new UploadFailure(409, "ERR_INVALID_STATE", "Chỉ ca phân tích đã hoàn tất mới được review.");
        if (baseVersion != request.BaseVersion)
            throw new UploadFailure(409, "ERR_CONCURRENT_REVIEW", "Ca bệnh đã được bác sĩ khác cập nhật; hãy tải lại phiên làm việc.");

        var version = checked(baseVersion + 1);
        var reviewId = Guid.NewGuid();
        DateTimeOffset createdAt;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO analysis.doctor_reviews
                (id, tenant_id, image_id, analysis_result_id, doctor_id, version, base_version,
                 is_approved, corrected_risk_level, reason, doctor_notes, icdr_level)
            SELECT $1, $2, $3, r.id, $4, $5, $6, $7, $8, $9, $10, $11
            FROM analysis.analysis_results r
            WHERE r.tenant_id = $2 AND r.image_id = $3 AND r.version = $12
            RETURNING created_at;
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(reviewId);
            insert.Parameters.AddWithValue(request.TenantId);
            insert.Parameters.AddWithValue(request.AnalysisId);
            insert.Parameters.AddWithValue(request.DoctorId);
            insert.Parameters.AddWithValue(version);
            insert.Parameters.AddWithValue(request.BaseVersion);
            insert.Parameters.AddWithValue(request.IsApproved);
            insert.Parameters.AddWithValue((object?)request.CorrectedRiskLevel ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)request.Reason ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)request.DoctorNotes ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)request.IcdrLevel ?? DBNull.Value);
            insert.Parameters.AddWithValue(aiResultVersion);
            var value = await insert.ExecuteScalarAsync(ct);
            if (value is not DateTime created)
                throw new UploadFailure(409, "ERR_CONCURRENT_REVIEW", "Kết quả AI đã thay đổi; hãy tải lại phiên làm việc.");
            createdAt = new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc));
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE analysis.images
            SET review_status = 'REVIEWED', row_version = row_version + 1, updated_at = clock_timestamp()
            WHERE id = $1 AND tenant_id = $2 AND row_version = $3;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue(request.AnalysisId);
            update.Parameters.AddWithValue(request.TenantId);
            update.Parameters.AddWithValue(request.BaseVersion);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new UploadFailure(409, "ERR_CONCURRENT_REVIEW", "Ca bệnh đã được bác sĩ khác cập nhật; hãy tải lại phiên làm việc.");
        }

        var metadata = JsonSerializer.Serialize(new
        {
            reviewId,
            version,
            aiResultVersion,
            request.IsApproved,
            request.CorrectedRiskLevel,
            request.Reason,
            request.DoctorNotes,
            request.IcdrLevel
        });
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO analysis.audit_logs
                (id, tenant_id, analysis_request_id, image_id, actor_id, actor_type, action,
                 target_type, requested_target_id, outcome, correlation_id, metadata)
            VALUES ($1, $2, $3, $4, $5, 'DOCTOR', 'DOCTOR_REVIEW', 'analysis.doctor_reviews',
                    $6, 'ALLOWED', $7, $8::jsonb);
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue(Guid.NewGuid());
            audit.Parameters.AddWithValue(request.TenantId);
            audit.Parameters.AddWithValue(requestId);
            audit.Parameters.AddWithValue(request.AnalysisId);
            audit.Parameters.AddWithValue(request.DoctorId);
            audit.Parameters.AddWithValue(reviewId.ToString("D"));
            audit.Parameters.AddWithValue(request.CorrelationId);
            audit.Parameters.AddWithValue(metadata);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(reviewId, request.AnalysisId, version, request.IsApproved, request.CorrectedRiskLevel,
            request.DoctorNotes, request.DoctorId, createdAt, request.Reason, aiResultVersion, request.IcdrLevel);
    }
}
