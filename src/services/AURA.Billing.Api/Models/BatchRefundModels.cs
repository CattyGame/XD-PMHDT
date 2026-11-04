namespace AURA.Billing.Api.Models;

public sealed record BatchRefundItemRequest(
    Guid ReservationId,
    string? Reason = "BATCH_PARTIAL_REFUND");

public sealed record BatchRefundRequest(
    Guid TenantId,
    Guid? BatchId,
    IReadOnlyList<BatchRefundItemRequest> Items);

public sealed record BatchRefundItemResult(
    Guid ReservationId,
    string Status,
    bool Refunded,
    string? Message = null);

public sealed record BatchRefundResponse(
    Guid TenantId,
    Guid? BatchId,
    int TotalRequested,
    int RefundedCount,
    int CreditsRefunded,
    IReadOnlyList<BatchRefundItemResult> Results);

public sealed record ReconcileStaleRequest(
    int? OlderThanMinutes = 30,
    int? Limit = 100);

/// <summary>Created only by authenticated server handlers; never accepted from a public request body.</summary>
public sealed record RefundAuthorization(Guid? ApprovedBy,string Source,IReadOnlyDictionary<Guid,string>? Evidence = null)
{
    public bool Allows(Guid reservationId) => Source=="SYSTEM_ADMIN" && ApprovedBy is {} actor && actor!=Guid.Empty
        || Source=="ANALYSIS_TERMINAL" && Evidence?.TryGetValue(reservationId,out var evidence)==true && !string.IsNullOrWhiteSpace(evidence);
}
