using Npgsql;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Feedback;

public sealed class PostgresFeedbackRepository(NpgsqlDataSource dataSource) : IFeedbackRepository
{
    public async Task<FeedbackReceipt> InsertAsync(FeedbackRecord feedback, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection=await dataSource.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO analysis.feedback
                (id, tenant_id, clinic_id, analysis_id, result_id, model_version, review_version,
                 doctor_id, kind, comment, correlation_id)
            SELECT $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11
            WHERE EXISTS (
                SELECT 1
                FROM analysis.analysis_results result
                LEFT JOIN LATERAL (
                    SELECT review.version, review.analysis_result_id
                    FROM analysis.doctor_reviews review
                    WHERE review.tenant_id = result.tenant_id AND review.image_id = result.image_id
                    ORDER BY review.version DESC LIMIT 1
                ) current_review ON true
                WHERE result.tenant_id = $2 AND result.image_id = $4
                  AND result.id = $5 AND result.model_version = $6
                  AND COALESCE(current_review.version, 0) = $7
                  AND (current_review.version IS NULL OR current_review.analysis_result_id = result.id)
            )
            """,connection,transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(feedback.TenantId);
        command.Parameters.AddWithValue(feedback.ClinicId);
        command.Parameters.AddWithValue(feedback.AnalysisId);
        command.Parameters.AddWithValue(feedback.ResultId);
        command.Parameters.AddWithValue(feedback.ModelVersion);
        command.Parameters.AddWithValue(feedback.ReviewVersion);
        command.Parameters.AddWithValue(feedback.DoctorId);
        command.Parameters.AddWithValue(feedback.FeedbackType);
        command.Parameters.AddWithValue(feedback.Comment);
        command.Parameters.AddWithValue(feedback.CorrelationId);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new UploadFailure(409, "ERR_CONFLICT", "Kết quả hoặc phiên bản review đã thay đổi.");

        await using var audit=new NpgsqlCommand("""
            INSERT INTO analysis.audit_logs
                (id,tenant_id,analysis_request_id,image_id,actor_id,actor_type,action,target_type,
                 requested_target_id,outcome,correlation_id,metadata)
            SELECT $1,$2,i.analysis_request_id,$3,$4,'DOCTOR','ANALYSIS_FEEDBACK','analysis.feedback',$5,
                   'ALLOWED',$6,$7::jsonb
            FROM analysis.images i WHERE i.tenant_id=$2 AND i.id=$3
            """,connection,transaction);
        audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(feedback.TenantId);audit.Parameters.AddWithValue(feedback.AnalysisId);
        audit.Parameters.AddWithValue(feedback.DoctorId);audit.Parameters.AddWithValue(id.ToString("D"));audit.Parameters.AddWithValue(feedback.CorrelationId);
        audit.Parameters.AddWithValue(System.Text.Json.JsonSerializer.Serialize(new{feedback.FeedbackType,feedback.ReviewVersion}));
        if(await audit.ExecuteNonQueryAsync(ct)!=1) throw new UploadFailure(409,"ERR_CONFLICT","Không tìm thấy analysis để audit feedback.");
        await transaction.CommitAsync(ct);

        return new FeedbackReceipt(id, "ACCEPTED", feedback.ReviewVersion);
    }
}
