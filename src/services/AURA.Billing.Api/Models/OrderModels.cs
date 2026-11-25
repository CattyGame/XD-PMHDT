namespace AURA.Billing.Api.Models;

public sealed record CreateOrderRequest(
    Guid? TenantId,
    BillingOwnerRequest BillingOwner,
    int PackageId,
    Guid? PackagePriceVersionId = null,
    string? PaymentMethod = "SANDBOX_MOCK");

public sealed record CreateOrderResponse(
    Guid OrderId,
    string Status,
    decimal Amount,
    string Currency,
    int Credits,
    string PackageName,
    string PaymentUrl,
    DateTimeOffset CreatedAt,
    Guid? PriceVersionId = null);

public sealed record OrderRecord(
    Guid Id,
    Guid TenantId,
    string BillingOwnerType,
    Guid BillingOwnerId,
    int PackageId,
    Guid PackagePriceVersionId,
    string PackageNameSnapshot,
    decimal PriceAmountSnapshot,
    string CurrencyCodeSnapshot,
    int CreditsSnapshot,
    string Environment,
    string Status,
    Guid IdempotencyKey,
    string RequestFingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PaymentCallbackPayload(
    Guid OrderId,
    string TransactionId,
    decimal Amount,
    string Currency,
    string PaymentStatus,
    long Timestamp,
    string Signature);

public sealed record PaymentCallbackResult(
    bool Success,
    string Status,
    string Message,
    string TransactionId,
    int CreditsAdded,
    int BalanceAfter,
    bool Replayed);

public sealed record SandboxPaymentRequest(
    Guid OrderId,
    bool SimulateFailure = false);

public sealed record SandboxPaymentResponse(
    Guid OrderId,
    string TransactionId,
    string Status,
    string Message,
    int CreditsAdded,
    int BalanceAfter,
    bool Replayed);
