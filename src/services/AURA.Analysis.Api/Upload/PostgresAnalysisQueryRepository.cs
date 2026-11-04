using Npgsql;

namespace AURA.Analysis.Api.Upload;

public sealed class PostgresAnalysisQueryRepository(NpgsqlDataSource dataSource) : IAnalysisQueryRepository, IDoctorWorklistRepository
{
    public async Task<IReadOnlyList<AnalysisIdentity>> ListDoctorWorklistAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || patientId == Guid.Empty) return Array.Empty<AnalysisIdentity>();
        await using var command = dataSource.CreateCommand("""
            SELECT i.id, COALESCE(i.batch_id,i.analysis_request_id), i.tenant_id, r.patient_id, r.owner_id
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id = i.tenant_id AND r.id = i.analysis_request_id
            WHERE i.tenant_id = $1 AND r.patient_id = $2
              AND i.processing_status = 'COMPLETED' AND i.review_status = 'PENDING'
            ORDER BY i.created_at
            LIMIT 100;
            """);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(patientId);
        var identities = new List<AnalysisIdentity>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            identities.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4)));
        return identities;
    }

    public async Task<AnalysisIdentity?> FindIdentityAsync(Guid analysisId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (analysisId == Guid.Empty) return null;

        await using var command = dataSource.CreateCommand("""
            SELECT i.id, COALESCE(i.batch_id,i.analysis_request_id), i.tenant_id, r.patient_id, r.owner_id
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id = i.tenant_id AND r.id = i.analysis_request_id
            WHERE i.id = $1;
            """);
        command.Parameters.AddWithValue(analysisId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4))
            : null;
    }

    public async Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity identity, string? correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (identity.AnalysisId == Guid.Empty) return null;

        await using var command = dataSource.CreateCommand("""
            SELECT i.id, COALESCE(i.batch_id,i.analysis_request_id), r.patient_id, i.processing_status, i.review_status,
                   i.created_at, i.updated_at, r.owner_id, r.requester_id, i.file_id,
                   i.existing_analysis_id,
                   j.id AS job_id, j.status AS job_status, j.attempt_no, j.retryable, j.error_code,
                   ar.id AS result_id, ar.version, ar.risk_assessment::text, ar.metrics::text, ar.warnings::text,
                   ar.is_mock, ar.model_version, ar.config_version, ar.threshold_version,
                   ar.processing_time_ms, ar.limitations, i.row_version
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id = i.tenant_id AND r.id = i.analysis_request_id
            LEFT JOIN LATERAL (
                SELECT x.id, x.status, x.attempt_no, x.retryable, x.error_code
                FROM analysis.jobs x
                WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
                ORDER BY x.attempt_no DESC LIMIT 1
            ) j ON true
            LEFT JOIN LATERAL (
                SELECT x.id, x.version, x.risk_assessment, x.metrics, x.warnings, x.is_mock,
                       x.model_version, x.config_version, x.threshold_version,
                       x.processing_time_ms, x.limitations
                FROM analysis.analysis_results x
                WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
                ORDER BY x.version DESC LIMIT 1
            ) ar ON true
            WHERE i.id = $1 AND i.tenant_id = $2;
            """);
        command.Parameters.AddWithValue(identity.AnalysisId);
        command.Parameters.AddWithValue(identity.TenantId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var analysisId = reader.GetGuid(0);
        var batchId = reader.GetGuid(1);
        var patientId = reader.GetGuid(2);
        var processingStatus = reader.GetString(3);
        var reviewStatus = reader.GetString(4);
        var createdAt = reader.GetDateTime(5);
        var updatedAt = reader.GetDateTime(6);
        var ownerId = reader.GetGuid(7);
        var requesterId = reader.GetGuid(8);
        var fileId = reader.GetGuid(9);
        var existingAnalysisId = reader.IsDBNull(10) ? (Guid?)null : reader.GetGuid(10);
        var reviewVersion = reader.GetInt32(27);

        AnalysisJobDetail? job = null;
        bool jobRetryable = false;
        string? jobErrorCode = null;

        if (!reader.IsDBNull(11))
        {
            var jobId = reader.GetGuid(11);
            var status = reader.GetString(12);
            var attempt = reader.GetInt32(13);
            jobRetryable = !reader.IsDBNull(14) && reader.GetBoolean(14);
            jobErrorCode = reader.IsDBNull(15) ? null : reader.GetString(15);
            job = new AnalysisJobDetail(jobId, status, attempt - 1, attempt);
        }

        // Đọc xong dòng chính rồi mới truy vấn annotation (reader hiện tại phải đóng trước).
        Guid? resultId = reader.IsDBNull(16) ? null : reader.GetGuid(16);
        AnalysisResultDetail? result = null;
        if (processingStatus == "COMPLETED" && resultId is not null)
        {
            var version = reader.GetInt32(17);
            var risk = reader.GetString(18);
            var metrics = reader.GetString(19);
            var warnings = reader.GetString(20);
            var isMock = reader.GetBoolean(21);
            var modelVersion = reader.GetString(22);
            var configVersion = reader.GetString(23);
            var thresholdVersion = reader.GetString(24);
            var processingMs = reader.GetDouble(25);
            var limitations = reader.GetString(26);
            await reader.CloseAsync();

            var rows = new List<AnalysisResultMapper.AnnotationRow>();
            await using var ann = dataSource.CreateCommand("""
                SELECT file_id, type FROM analysis.annotation_refs
                WHERE tenant_id = $1 AND image_id = $2 AND analysis_result_id = $3;
                """);
            ann.Parameters.AddWithValue(identity.TenantId);
            ann.Parameters.AddWithValue(identity.AnalysisId);
            ann.Parameters.AddWithValue(resultId.Value);
            await using var ar = await ann.ExecuteReaderAsync(ct);
            while (await ar.ReadAsync(ct)) rows.Add(new(ar.GetGuid(0), ar.GetString(1)));

            result = AnalysisResultMapper.Map(version, risk, metrics, warnings, isMock, modelVersion,
                configVersion, thresholdVersion, processingMs, limitations, rows) with {ResultId=resultId.Value};
        }

        AnalysisErrorDetail? error = null;
        if (processingStatus is "FAILED" or "LOW_QUALITY" or "REJECTED" or "DUPLICATE")
        {
            var code = processingStatus switch
            {
                "DUPLICATE" => "ERR_DUPLICATE_REQUEST",
                "LOW_QUALITY" => "ERR_IMAGE_LOW_QUALITY",
                "REJECTED" => "ERR_VALIDATION",
                _ => jobErrorCode ?? "ERR_AI_FAILED"
            };
            var message = code switch
            {
                "ERR_DUPLICATE_REQUEST" => "Ảnh trùng đã được bỏ qua.",
                "ERR_IMAGE_LOW_QUALITY" => "Chất lượng ảnh chưa đạt yêu cầu.",
                "ERR_TIMEOUT" => "Quá thời gian xử lý.",
                "ERR_NETWORK" => "Dịch vụ xử lý tạm thời không khả dụng.",
                _ => "Không thể hoàn tất phân tích ảnh."
            };
            error = new AnalysisErrorDetail(code, message, correlationId, jobRetryable);
        }

        return new AnalysisDetailResponse(
            analysisId, batchId, patientId, processingStatus, reviewStatus,
            createdAt, updatedAt, ownerId, requesterId,
            new AnalysisImageRef(fileId, "original"),
            job, result, error, existingAnalysisId, reviewVersion);
    }
}
