using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;

namespace AURA.Billing.Api.Services;

public sealed record ReconciliationReport(
    int TotalScanned,
    int ReleasedCount,
    int CreditsRefunded,
    IReadOnlyList<Guid> ReleasedReservations);

public sealed class StaleReservationReconciliationService(
    CreditReservationRepository repository,
    AnalysisSettlementResolverClient settlementResolver,
    ILogger<StaleReservationReconciliationService> logger)
{
    public const string AutoRefundReason = "STALE_RESERVATION_AUTO_REFUND";

    public async Task<ReconciliationReport> ReconcileStaleReservationsAsync(
        int thresholdMinutes = 30,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var staleReservations = await repository.GetStaleReservationsAsync(
            thresholdMinutes, limit, cancellationToken);

        if (staleReservations.Count == 0)
        {
            return new ReconciliationReport(0, 0, 0, Array.Empty<Guid>());
        }

        logger.LogInformation(
            "Reconciliation scan found {Count} stale reservation(s) older than {Threshold} minutes.",
            staleReservations.Count, thresholdMinutes);

        var releasedIds = new List<Guid>();
        var totalCreditsRefunded = 0;

        foreach (var res in staleReservations)
        {
            try
            {
                // D4 ADR-005: Tuyệt đối không tự động hoàn tiền/release dựa thuần túy vào thời gian timeout.
                // Bắt buộc phải xác minh quyết định terminal từ Analysis service.
                SettlementResolutionResponse decision;
                try
                {
                    decision = await settlementResolver.ResolveAsync(
                        Guid.NewGuid(),
                        res.AnalysisId,
                        res.TenantId,
                        correlationId: null,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "[RECONCILIATION] Unable to query Analysis terminal evidence for reservation {ReservationId} (AnalysisId: {AnalysisId}). Preserving RESERVED state (fail-closed).",
                        res.ReservationId, res.AnalysisId);
                    continue;
                }

                // R08 ADR-005: Kiem tra exact mapping tenant + analysis + reservationId truoc khi transition
                if (decision.TenantId != res.TenantId || decision.AnalysisId != res.AnalysisId)
                {
                    logger.LogWarning(
                        "[RECONCILIATION] Identification mismatch for reservation {ReservationId}. Expected AnalysisId={AnalysisId}, TenantId={TenantId}; got Decision AnalysisId={DecisionAnalysisId}, TenantId={DecisionTenantId}. HOLD for audit, skipping settlement.",
                        res.ReservationId, res.AnalysisId, res.TenantId, decision.AnalysisId, decision.TenantId);
                    continue;
                }

                if (decision.ReservationId.HasValue && decision.ReservationId.Value != Guid.Empty && decision.ReservationId.Value != res.ReservationId)
                {
                    logger.LogWarning(
                        "[RECONCILIATION] Generation/Reservation mismatch! Current scanned reservation is {ReservationId}, but Analysis terminal evidence belongs to reservation {DecisionReservationId} (Generation: {Generation}). HOLD for audit, skipping settlement.",
                        res.ReservationId, decision.ReservationId.Value, decision.Generation);
                    continue;
                }

                if (string.Equals(decision.SettlementAction, "RELEASE", StringComparison.OrdinalIgnoreCase))
                {
                    var idempotencyKey = Guid.NewGuid();
                    var releaseReason = !string.IsNullOrWhiteSpace(decision.Reason)
                        ? decision.Reason
                        : AutoRefundReason;
                    var fingerprint = ReservationFingerprint.ForRelease(res.ReservationId, releaseReason);

                    var result = await repository.ReleaseAsync(
                        res.ReservationId,
                        idempotencyKey,
                        fingerprint,
                        releaseReason,
                        cancellationToken);

                    if (result.Status == ReservationOperationStatus.Changed)
                    {
                        releasedIds.Add(res.ReservationId);
                        totalCreditsRefunded += res.Units;

                        logger.LogWarning(
                            "[RECONCILIATION] Authoritative terminal failure verified. Successfully released reservation {ReservationId} (AnalysisId: {AnalysisId}, Reason: {Reason}).",
                            res.ReservationId, res.AnalysisId, releaseReason);
                    }
                }
                else if (string.Equals(decision.SettlementAction, "CAPTURE", StringComparison.OrdinalIgnoreCase))
                {
                    var idempotencyKey = Guid.NewGuid();
                    var fingerprint = ReservationFingerprint.ForCapture(res.ReservationId);

                    var result = await repository.CaptureAsync(
                        res.ReservationId,
                        idempotencyKey,
                        fingerprint,
                        cancellationToken);

                    logger.LogInformation(
                        "[RECONCILIATION] Authoritative terminal completion verified. Captured reservation {ReservationId} (Status: {Status}).",
                        res.ReservationId, result.Status);
                }
                else
                {
                    logger.LogInformation(
                        "[RECONCILIATION] Reservation {ReservationId} (AnalysisId: {AnalysisId}) is NOT terminal (Action: {Action}, Reason: {Reason}). Preserving RESERVED invariant.",
                        res.ReservationId, res.AnalysisId, decision.SettlementAction, decision.Reason);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error while reconciling stale reservation {ReservationId}.", res.ReservationId);
            }
        }

        return new ReconciliationReport(
            staleReservations.Count,
            releasedIds.Count,
            totalCreditsRefunded,
            releasedIds);
    }
}
