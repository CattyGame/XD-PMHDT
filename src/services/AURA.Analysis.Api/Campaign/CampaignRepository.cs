using System.Globalization;
using System.Text;
using AURA.Analysis.Api.Upload;
using Npgsql;

namespace AURA.Analysis.Api.Campaign;

public sealed class CampaignRepository(NpgsqlDataSource dataSource)
{
    public async Task<CampaignResponse> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Name))
            throw UploadFailure.Validation(new ErrorDetail("name", "Tên chiến dịch không được để trống."));
        if (string.IsNullOrWhiteSpace(request.Location))
            throw UploadFailure.Validation(new ErrorDetail("location", "Địa điểm khám không được để trống."));
        if (request.EndDate < request.StartDate)
            throw UploadFailure.Validation(new ErrorDetail("endDate", "Thời gian kết thúc phải sau hoặc bằng thời gian bắt đầu."));
        if (request.TargetPatientCount < 0)
            throw UploadFailure.Validation(new ErrorDetail("targetPatientCount", "Số lượng bệnh nhân mục tiêu không được âm."));

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO analysis.campaigns (
                id, tenant_id, name, description, location, doctor_in_charge_id,
                target_patient_count, status, start_date, end_date, created_at, updated_at
            ) VALUES (
                $1, $2, $3, $4, $5, $6,
                $7, $8, $9, $10, $11, $11
            ) RETURNING created_at, updated_at;
            """);

        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(request.Name.Trim());
        cmd.Parameters.AddWithValue((object?)request.Description?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue(request.Location.Trim());
        cmd.Parameters.AddWithValue((object?)request.DoctorInChargeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(request.TargetPatientCount);
        cmd.Parameters.AddWithValue(CampaignStatus.Planned);
        cmd.Parameters.AddWithValue(request.StartDate);
        cmd.Parameters.AddWithValue(request.EndDate);
        cmd.Parameters.AddWithValue(now);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw UploadFailure.Dependency("Analysis database");

        var createdAt = reader.GetDateTime(0);
        var updatedAt = reader.GetDateTime(1);

        return new CampaignResponse(
            id,
            tenantId,
            request.Name.Trim(),
            request.Description?.Trim(),
            request.Location.Trim(),
            request.DoctorInChargeId,
            request.TargetPatientCount,
            CampaignStatus.Planned,
            request.StartDate,
            request.EndDate,
            createdAt,
            updatedAt);
    }

    public async Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(Guid tenantId, string? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var query = """
            SELECT id, tenant_id, name, description, location, doctor_in_charge_id,
                   target_patient_count, status, start_date, end_date, created_at, updated_at
            FROM analysis.campaigns
            WHERE tenant_id = $1
            """ + (string.IsNullOrWhiteSpace(status) ? "" : " AND status = $2 ") + """
            ORDER BY created_at DESC;
            """;

        await using var cmd = dataSource.CreateCommand(query);
        cmd.Parameters.AddWithValue(tenantId);
        if (!string.IsNullOrWhiteSpace(status))
            cmd.Parameters.AddWithValue(status.Trim().ToUpperInvariant());

        var list = new List<CampaignResponse>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CampaignResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetDateTime(8),
                reader.GetDateTime(9),
                reader.GetDateTime(10),
                reader.GetDateTime(11)));
        }

        return list;
    }

    public async Task<CampaignResponse> GetCampaignByIdAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand("""
            SELECT id, tenant_id, name, description, location, doctor_in_charge_id,
                   target_patient_count, status, start_date, end_date, created_at, updated_at
            FROM analysis.campaigns
            WHERE tenant_id = $1 AND id = $2;
            """);

        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(campaignId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");

        return new CampaignResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetDateTime(8),
            reader.GetDateTime(9),
            reader.GetDateTime(10),
            reader.GetDateTime(11));
    }

    public async Task<CampaignResponse> UpdateCampaignStatusAsync(Guid tenantId, Guid campaignId, string newStatus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = newStatus?.Trim().ToUpperInvariant() ?? "";
        if (!CampaignStatus.All.Contains(normalized))
            throw UploadFailure.Validation(new ErrorDetail("status", $"Trạng thái không hợp lệ. Phải là một trong: {string.Join(", ", CampaignStatus.All)}"));

        await using var cmd = dataSource.CreateCommand("""
            UPDATE analysis.campaigns
            SET status = $3, updated_at = now()
            WHERE tenant_id = $1 AND id = $2
            RETURNING id, tenant_id, name, description, location, doctor_in_charge_id,
                      target_patient_count, status, start_date, end_date, created_at, updated_at;
            """);

        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(campaignId);
        cmd.Parameters.AddWithValue(normalized);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát để cập nhật trạng thái.");

        return new CampaignResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetDateTime(8),
            reader.GetDateTime(9),
            reader.GetDateTime(10),
            reader.GetDateTime(11));
    }

    public async Task LinkBatchAsync(Guid tenantId, Guid campaignId, Guid batchId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Kiểm tra campaign
        await using var campCmd = dataSource.CreateCommand("""
            SELECT id, status FROM analysis.campaigns
            WHERE tenant_id = $1 AND id = $2;
            """);
        campCmd.Parameters.AddWithValue(tenantId);
        campCmd.Parameters.AddWithValue(campaignId);

        await using (var campReader = await campCmd.ExecuteReaderAsync(ct))
        {
            if (!await campReader.ReadAsync(ct))
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy chiến dịch tầm soát.");

            var status = campReader.GetString(1);
            // AC3: Kiểm tra nếu CLOSED thì cấm tuyệt đối nạp ảnh / liên kết batch
            if (string.Equals(status, CampaignStatus.Closed, StringComparison.OrdinalIgnoreCase))
            {
                throw new UploadFailure(409, "ERR_CAMPAIGN_CLOSED", "Chiến dịch đã kết thúc (CLOSED), không cho phép nạp thêm ảnh chụp mới.");
            }
        }

        // 2. Kiểm tra batch
        await using var batchCmd = dataSource.CreateCommand("""
            SELECT id FROM analysis.batches
            WHERE tenant_id = $1 AND id = $2;
            """);
        batchCmd.Parameters.AddWithValue(tenantId);
        batchCmd.Parameters.AddWithValue(batchId);

        await using (var batchReader = await batchCmd.ExecuteReaderAsync(ct))
        {
            if (!await batchReader.ReadAsync(ct))
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy batch ảnh trong phòng khám này.");
        }

        // 3. Cập nhật liên kết
        await using var linkCmd = dataSource.CreateCommand("""
            UPDATE analysis.batches
            SET campaign_id = $3, updated_at = now()
            WHERE tenant_id = $1 AND id = $2;
            """);
        linkCmd.Parameters.AddWithValue(tenantId);
        linkCmd.Parameters.AddWithValue(batchId);
        linkCmd.Parameters.AddWithValue(campaignId);

        await linkCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CampaignSummaryResponse> GetCampaignSummaryAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var campaign = await GetCampaignByIdAsync(tenantId, campaignId, ct);

        // 1. Tổng hợp từ analysis.batches
        await using var batchSumCmd = dataSource.CreateCommand("""
            SELECT
                COUNT(id) AS total_batches,
                COALESCE(SUM(total_images), 0) AS total_images,
                COALESCE(SUM(completed_images), 0) AS completed_images,
                COALESCE(SUM(failed_images), 0) AS failed_images
            FROM analysis.batches
            WHERE tenant_id = $1 AND campaign_id = $2;
            """);
        batchSumCmd.Parameters.AddWithValue(tenantId);
        batchSumCmd.Parameters.AddWithValue(campaignId);

        int totalBatches = 0;
        int totalImages = 0;
        int completedImages = 0;
        int failedImages = 0;

        await using (var reader = await batchSumCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                totalBatches = Convert.ToInt32(reader["total_batches"]);
                totalImages = Convert.ToInt32(reader["total_images"]);
                completedImages = Convert.ToInt32(reader["completed_images"]);
                failedImages = Convert.ToInt32(reader["failed_images"]);
            }
        }

        int pendingImages = Math.Max(0, totalImages - completedImages - failedImages);
        double completionRatePercent = totalImages > 0
            ? Math.Round((double)completedImages / totalImages * 100.0, 2)
            : 0.0;

        // 2. Thống kê lâm sàng từ analysis.images và analysis_results
        await using var clinicalCmd = dataSource.CreateCommand("""
            SELECT
                COUNT(DISTINCT r.patient_id) AS unique_patients,
                COUNT(CASE WHEN ar.risk_level = 'LOW' THEN 1 END) AS normal_cases,
                COUNT(CASE WHEN ar.risk_level IN ('MODERATE', 'HIGH') THEN 1 END) AS suspected_cases,
                COUNT(CASE WHEN ar.risk_level = 'HIGH' THEN 1 END) AS high_risk_cases
            FROM analysis.batches b
            JOIN analysis.images i ON i.tenant_id = b.tenant_id AND (i.batch_id = b.id OR i.analysis_request_id = b.id)
            JOIN analysis.analysis_requests r ON r.tenant_id = i.tenant_id AND r.id = i.analysis_request_id
            LEFT JOIN LATERAL (
                SELECT x.risk_assessment->>'riskLevel' AS risk_level
                FROM analysis.analysis_results x
                JOIN analysis.jobs xj ON xj.tenant_id = x.tenant_id AND xj.id = x.job_id AND xj.image_id = x.image_id
                WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
                ORDER BY x.version DESC LIMIT 1
            ) ar ON true
            WHERE b.tenant_id = $1 AND b.campaign_id = $2;
            """);
        clinicalCmd.Parameters.AddWithValue(tenantId);
        clinicalCmd.Parameters.AddWithValue(campaignId);

        int uniquePatients = 0;
        int normalCases = 0;
        int suspectedCases = 0;
        int highRiskCases = 0;

        await using (var reader = await clinicalCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                uniquePatients = Convert.ToInt32(reader["unique_patients"]);
                normalCases = Convert.ToInt32(reader["normal_cases"]);
                suspectedCases = Convert.ToInt32(reader["suspected_cases"]);
                highRiskCases = Convert.ToInt32(reader["high_risk_cases"]);
            }
        }

        return new CampaignSummaryResponse(
            campaign.Id,
            tenantId,
            campaign.Name,
            campaign.Status,
            campaign.TargetPatientCount,
            totalBatches,
            totalImages,
            completedImages,
            pendingImages,
            failedImages,
            completionRatePercent,
            normalCases,
            suspectedCases,
            highRiskCases,
            uniquePatients);
    }

    public async Task<string> ExportCampaignReportCsvAsync(Guid tenantId, Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var summary = await GetCampaignSummaryAsync(tenantId, campaignId, ct);

        // Lấy danh sách ảnh chi tiết thuộc chiến dịch
        await using var cmd = dataSource.CreateCommand("""
            SELECT
                i.processing_status,
                i.review_status,
                ar.risk_level,
                i.created_at
            FROM analysis.batches b
            JOIN analysis.images i ON i.tenant_id = b.tenant_id AND (i.batch_id = b.id OR i.analysis_request_id = b.id)
            LEFT JOIN LATERAL (
                SELECT x.risk_assessment->>'riskLevel' AS risk_level
                FROM analysis.analysis_results x
                JOIN analysis.jobs xj ON xj.tenant_id = x.tenant_id AND xj.id = x.job_id AND xj.image_id = x.image_id
                WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
                ORDER BY x.version DESC LIMIT 1
            ) ar ON true
            WHERE b.tenant_id = $1 AND b.campaign_id = $2
            ORDER BY i.created_at ASC;
            """);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(campaignId);

        var sb = new StringBuilder();
        sb.AppendLine(CampaignCsvWriter.Row("recordType", "caseCode", "campaignName", "campaignStatus",
            "targetPatients", "totalBatches", "totalImages", "completedImages", "pendingImages", "failedImages",
            "completionRatePercent", "normalCases", "suspectedCases", "highRiskCases", "uniquePatients",
            "processingStatus", "reviewStatus", "riskLevel", "createdAtUtc"));
        sb.AppendLine(CampaignCsvWriter.Row("summary", "", summary.CampaignName, summary.Status,
            summary.TargetPatientCount, summary.TotalBatches, summary.TotalImages, summary.CompletedImages,
            summary.PendingImages, summary.FailedImages, summary.CompletionRatePercent, summary.NormalCases,
            summary.SuspectedCases, summary.HighRiskCases, summary.UniquePatients, "", "", "", ""));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var index = 0;
        while (await reader.ReadAsync(ct))
        {
            index++;
            sb.AppendLine(CampaignCsvWriter.Row("case", $"CASE-{index:D4}", "", "", "", "", "", "", "", "", "", "", "", "", "",
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "UNKNOWN" : reader.GetString(2),
                reader.GetDateTime(3).ToString("O", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }
}
