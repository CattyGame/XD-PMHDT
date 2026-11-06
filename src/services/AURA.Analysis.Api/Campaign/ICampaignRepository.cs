namespace AURA.Analysis.Api.Campaign;

public interface ICampaignRepository
{
    Task<CampaignResponse> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest request, CancellationToken ct);
    Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(Guid tenantId, string? status, CancellationToken ct);
    Task<CampaignResponse> GetCampaignByIdAsync(Guid tenantId, Guid campaignId, CancellationToken ct);
    Task<CampaignResponse> UpdateCampaignStatusAsync(Guid tenantId, Guid campaignId, string newStatus, CancellationToken ct);
    Task LinkBatchAsync(Guid tenantId, Guid campaignId, Guid batchId, CancellationToken ct);
    Task<CampaignSummaryResponse> GetCampaignSummaryAsync(Guid tenantId, Guid campaignId, CancellationToken ct);
    Task<string> ExportCampaignReportCsvAsync(Guid tenantId, Guid campaignId, CancellationToken ct);
}

