using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using StackExchange.Redis;

namespace AURA.Billing.Api.Services;

public sealed class OrderService
{
    private readonly OrderRepository _repository;
    private readonly PaymentSignatureService _signatureService;
    private readonly IDatabase _redis;
    private readonly ILogger<OrderService> _logger;
    private readonly IBillingPaymentEventPublisher? _paymentEventPublisher;
    private readonly string _webhookSecret;
    private readonly string _sandboxGatewayUrl;

    public OrderService(
        OrderRepository repository,
        PaymentSignatureService signatureService,
        IConnectionMultiplexer redisMultiplexer,
        IConfiguration configuration,
        ILogger<OrderService> logger,
        IBillingPaymentEventPublisher? paymentEventPublisher = null)
    {
        _repository = repository;
        _signatureService = signatureService;
        _redis = redisMultiplexer.GetDatabase();
        _logger = logger;
        _paymentEventPublisher = paymentEventPublisher;

        _webhookSecret = configuration["Billing:PaymentWebhookSecret"]
            ?? "aura_billing_sandbox_hmac_secret_2026";

        _sandboxGatewayUrl = configuration["Billing:SandboxPaymentGatewayUrl"]
            ?? "http://localhost:5173/billing/sandbox-checkout";
    }

    public async Task<OrderOperationResult> CreateOrderAsync(
        CreateOrderRequest request,
        Guid idempotencyKey,
        BillingRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken)
    {
        var tenantId = request.TenantId ?? request.BillingOwner.Id;
        var fingerprint = ComputeOrderFingerprint(request, idempotencyKey);

        // 1. Check idempotency
        var existingOrder = await _repository.FindByIdempotencyKeyAsync(tenantId, idempotencyKey, cancellationToken);
        if (existingOrder is not null)
        {
            if (string.Equals(existingOrder.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                var existingResponse = BuildOrderResponse(existingOrder);
                return new OrderOperationResult(OrderOperationStatus.Replayed, existingResponse);
            }

            return new OrderOperationResult(OrderOperationStatus.IdempotencyConflict);
        }

        // 2. Fetch active price version
        var priceVersion = await _repository.GetActivePriceVersionAsync(
            request.PackageId, request.PackagePriceVersionId, cancellationToken);

        if (priceVersion is null)
        {
            return new OrderOperationResult(OrderOperationStatus.PackageNotFound);
        }

        // 3. Create order
        var orderId = Guid.NewGuid();
        var orderRecord = await _repository.CreateOrderAsync(
            orderId,
            tenantId,
            request.BillingOwner.Type.Trim().ToUpperInvariant(),
            request.BillingOwner.Id,
            priceVersion,
            runtimeOptions.Environment,
            idempotencyKey,
            fingerprint,
            cancellationToken);

        var response = BuildOrderResponse(orderRecord);
        return new OrderOperationResult(OrderOperationStatus.Created, response);
    }

    public async Task<PaymentCallbackResult> ProcessCallbackAsync(
        PaymentCallbackPayload payload,
        string paymentMethod,
        CancellationToken cancellationToken)
    {
        // 1. HMAC SHA-256 Signature Verification
        var isValid = _signatureService.VerifySignature(
            payload.OrderId,
            payload.TransactionId,
            payload.Amount,
            payload.Currency,
            payload.PaymentStatus,
            payload.Timestamp,
            payload.Signature,
            _webhookSecret);

        if (!isValid)
        {
            _logger.LogWarning(
                "[SECURITY AUDIT] Invalid HMAC SHA-256 signature for OrderId: {OrderId}, TransactionId: {TransactionId}. Possible tampering or spoofed callback.",
                payload.OrderId, payload.TransactionId);

            return new PaymentCallbackResult(
                false, "ERR_INVALID_SIGNATURE",
                "Chữ ký HMAC SHA-256 không hợp lệ hoặc payload đã bị can thiệp trái phép.",
                payload.TransactionId, 0, 0, false);
        }

        // 2. Redis Distributed Lock on Order ID (chống Replay Attack)
        var lockKey = $"lock:billing:order:{payload.OrderId:D}";
        var lockToken = Guid.NewGuid().ToString("D");
        var lockExpiry = TimeSpan.FromSeconds(10);

        var acquired = false;
        try
        {
            // Thử lấy lock tối đa 3 lần với khoảng cách 100ms
            for (var i = 0; i < 3; i++)
            {
                acquired = await _redis.LockTakeAsync(lockKey, lockToken, lockExpiry);
                if (acquired)
                {
                    break;
                }
                await Task.Delay(100, cancellationToken);
            }

            if (!acquired)
            {
                _logger.LogWarning(
                    "Could not acquire Redis distributed lock for OrderId {OrderId}. Concurrent callback in progress.",
                    payload.OrderId);

                return new PaymentCallbackResult(
                    false, "ERR_CONCURRENT_PROCESSING",
                    "Giao dịch đang được xử lý bởi một yêu cầu khác. Vui lòng thử lại sau.",
                    payload.TransactionId, 0, 0, false);
            }

            // 3. Process payment and update ledger in single ACID transaction
            var callbackResult = await _repository.ProcessCallbackInTransactionAsync(
                payload, paymentMethod, cancellationToken);

            if (callbackResult.Success && string.Equals(callbackResult.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) && !callbackResult.Replayed && _paymentEventPublisher is not null)
            {
                await _paymentEventPublisher.PublishPaymentSuccessAsync(payload.OrderId, cancellationToken);
            }

            return callbackResult;
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    await _redis.LockReleaseAsync(lockKey, lockToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release Redis lock {LockKey}", lockKey);
                }
            }
        }
    }

    public async Task<SandboxPaymentResponse> SimulateSandboxPaymentAsync(
        SandboxPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var order = await _repository.GetOrderByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            return new SandboxPaymentResponse(
                request.OrderId, string.Empty, "NOT_FOUND", "Không tìm thấy đơn hàng.", 0, 0, false);
        }

        if (!string.Equals(order.Environment, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            return new SandboxPaymentResponse(
                request.OrderId, string.Empty, "FORBIDDEN", "Cổng thanh toán Sandbox chỉ áp dụng cho đơn hàng trong môi trường TEST.", 0, 0, false);
        }

        var transactionId = $"SANDBOX-TX-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var status = request.SimulateFailure ? "FAILED" : "SUCCESS";

        var signature = _signatureService.ComputeSignature(
            order.Id, transactionId, order.PriceAmountSnapshot, order.CurrencyCodeSnapshot,
            status, timestamp, _webhookSecret);

        var payload = new PaymentCallbackPayload(
            order.Id, transactionId, order.PriceAmountSnapshot, order.CurrencyCodeSnapshot,
            status, timestamp, signature);

        var result = await ProcessCallbackAsync(payload, "SANDBOX_MOCK", cancellationToken);

        return new SandboxPaymentResponse(
            order.Id, transactionId, result.Status, result.Message,
            result.CreditsAdded, result.BalanceAfter, result.Replayed);
    }

    private CreateOrderResponse BuildOrderResponse(OrderRecord order)
    {
        var paymentUrl = FormattableString.Invariant(
            $"{_sandboxGatewayUrl}?orderId={order.Id:D}&amount={order.PriceAmountSnapshot:0.00}&currency={order.CurrencyCodeSnapshot}&credits={order.CreditsSnapshot}&packageName={Uri.EscapeDataString(order.PackageNameSnapshot)}");

        return new CreateOrderResponse(
            order.Id,
            order.Status,
            order.PriceAmountSnapshot,
            order.CurrencyCodeSnapshot,
            order.CreditsSnapshot,
            order.PackageNameSnapshot,
            paymentUrl,
            order.CreatedAt);
    }

    private static string ComputeOrderFingerprint(CreateOrderRequest request, Guid idempotencyKey)
    {
        var raw = JsonSerializer.Serialize(new
        {
            tenant = request.TenantId,
            owner_type = request.BillingOwner.Type.Trim().ToUpperInvariant(),
            owner_id = request.BillingOwner.Id,
            package_id = request.PackageId,
            price_version = request.PackagePriceVersionId,
            idempotency_key = idempotencyKey
        });

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }


}

public enum OrderOperationStatus
{
    Created,
    Replayed,
    IdempotencyConflict,
    PackageNotFound
}

public sealed record OrderOperationResult(
    OrderOperationStatus Status,
    CreateOrderResponse? Order = null);
