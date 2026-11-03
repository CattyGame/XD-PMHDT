using Npgsql;

namespace AURA.Analysis.Api.Upload;

public sealed record InternalResolveRequest(Guid EventId, Guid AnalysisId, Guid TenantId);

public sealed record SettlementResolutionResponse(
    Guid EventId,
    Guid AnalysisId,
    Guid TenantId,
    bool Applicable,
    string SettlementAction,
    string Reason,
    Guid? ReservationId,
    int? Generation,
    string ProcessingStatus,
    int? ResultVersion,
    bool? IsMock);

public sealed record NotificationContextResponse(
    Guid EventId,
    Guid AnalysisId,
    Guid TenantId,
    bool Applicable,
    IReadOnlyList<Guid> RecipientAccountIds,
    string ProcessingStatus,
    int? ResultVersion);

public interface IInternalResolversRepository
{
    Task<InternalAnalysisState?> GetStateAsync(Guid analysisId, Guid tenantId, CancellationToken ct);
}

public sealed record InternalAnalysisState(
    Guid AnalysisId,
    Guid TenantId,
    Guid BatchId,
    Guid OwnerId,
    string ProcessingStatus,
    Guid? ReservationId,
    int? Generation,
    int? ResultVersion,
    bool? IsMock);

public sealed class PostgresInternalResolversRepository(NpgsqlDataSource dataSource) : IInternalResolversRepository
{
    public async Task<InternalAnalysisState?> GetStateAsync(Guid analysisId, Guid tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (analysisId == Guid.Empty || tenantId == Guid.Empty) return null;

        await using var command = dataSource.CreateCommand("""
            SELECT i.id, i.tenant_id, i.analysis_request_id, r.owner_id, i.processing_status,
                   cr.reservation_id, cr.generation,
                   ar.version, ar.is_mock
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id = i.tenant_id AND r.id = i.analysis_request_id
            LEFT JOIN LATERAL (
                SELECT x.reservation_id, x.generation
                FROM analysis.credit_reservations x
                WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
                ORDER BY x.generation DESC LIMIT 1
            ) cr ON true
            LEFT JOIN LATERAL (
                SELECT x.version, x.is_mock
                FROM analysis.analysis_results x
                WHERE x.tenant_id = i.tenant_id AND x.image_id = i.id
                ORDER BY x.version DESC LIMIT 1
            ) ar ON true
            WHERE i.id = $1 AND i.tenant_id = $2;
            """);
        command.Parameters.AddWithValue(analysisId);
        command.Parameters.AddWithValue(tenantId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var reservationId = reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5);
        var generation = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
        var resultVersion = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
        var isMock = reader.IsDBNull(8) ? (bool?)null : reader.GetBoolean(8);

        return new InternalAnalysisState(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            reader.GetString(4), reservationId, generation, resultVersion, isMock);
    }
}

public sealed class InternalResolversService(IInternalResolversRepository repository)
{
    private static UploadFailure NotFound() => new(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên hoặc tenant không khớp.");

    public async Task<SettlementResolutionResponse> ResolveSettlementAsync(InternalResolveRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.EventId == Guid.Empty || request.AnalysisId == Guid.Empty || request.TenantId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("body", "invalid_identifiers"));

        var state = await repository.GetStateAsync(request.AnalysisId, request.TenantId, ct);
        if (state is null) throw NotFound();

        // Check if analysis has an associated reservation
        if (state.ReservationId is null || state.Generation is null)
        {
            return new SettlementResolutionResponse(
                request.EventId, request.AnalysisId, request.TenantId,
                false, "NONE", "NO_RESERVATION",
                null, null, state.ProcessingStatus, null, state.IsMock ?? false);
        }

        // State Machine for settlement
        if (state.ProcessingStatus == "COMPLETED")
        {
            // N08: If no valid result or version exists, do NOT synthesize version 1 and do NOT capture
            if (state.ResultVersion is null or <= 0)
            {
                return new SettlementResolutionResponse(
                    request.EventId, request.AnalysisId, request.TenantId,
                    false, "NONE", "RECONCILE_REQUIRED",
                    state.ReservationId, state.Generation, state.ProcessingStatus, null, state.IsMock ?? false);
            }

            // If mock in real namespace, hold for reconciliation
            if (state.IsMock == true)
            {
                return new SettlementResolutionResponse(
                    request.EventId, request.AnalysisId, request.TenantId,
                    false, "NONE", "RECONCILE_REQUIRED",
                    state.ReservationId, state.Generation, state.ProcessingStatus, state.ResultVersion, true);
            }

            return new SettlementResolutionResponse(
                request.EventId, request.AnalysisId, request.TenantId,
                true, "CAPTURE", "VALID_RESULT",
                state.ReservationId, state.Generation, state.ProcessingStatus, state.ResultVersion.Value, false);
        }

        if (state.ProcessingStatus is "FAILED" or "LOW_QUALITY" or "REJECTED")
        {
            var reason = state.ProcessingStatus == "LOW_QUALITY" ? "LOW_QUALITY" : "FINAL_FAILURE";
            return new SettlementResolutionResponse(
                request.EventId, request.AnalysisId, request.TenantId,
                true, "RELEASE", reason,
                state.ReservationId, state.Generation, state.ProcessingStatus, null, state.IsMock ?? false);
        }

        // Active / in-progress statuses
        return new SettlementResolutionResponse(
            request.EventId, request.AnalysisId, request.TenantId,
            false, "NONE", "NOT_TERMINAL",
            state.ReservationId, state.Generation, state.ProcessingStatus, null, state.IsMock ?? false);
    }

    public async Task<NotificationContextResponse> ResolveNotificationContextAsync(InternalResolveRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.EventId == Guid.Empty || request.AnalysisId == Guid.Empty || request.TenantId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("body", "invalid_identifiers"));

        var state = await repository.GetStateAsync(request.AnalysisId, request.TenantId, ct);
        if (state is null) throw NotFound();

        // In Sprint 3 PERSONAL tenant demo, recipient is the owner account ID verified by Profile
        var recipients = new[] { state.OwnerId };

        return new NotificationContextResponse(
            request.EventId, request.AnalysisId, request.TenantId,
            true, recipients, state.ProcessingStatus, state.ResultVersion);
    }
}
