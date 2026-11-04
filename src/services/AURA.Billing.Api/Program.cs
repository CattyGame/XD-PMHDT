using AURA.Billing.Api;
using System.Text.RegularExpressions;
using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Billing.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Billing currently runs only in Development in this repository.
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Billing is restricted to Development until the production security configuration is approved.");
}

var billingEnvironment = (builder.Configuration["Billing:Environment"] ?? "TEST")
    .Trim().ToUpperInvariant();
if (billingEnvironment is not ("LIVE" or "TEST"))
{
    throw new InvalidOperationException("Billing:Environment must be LIVE or TEST.");
}

var isMock = builder.Configuration.GetValue<bool>("Billing:IsMock");
if (isMock && billingEnvironment != "TEST")
{
    throw new InvalidOperationException("Mock Billing mode must use the TEST wallet environment.");
}

builder.Services.AddSingleton(new BillingRuntimeOptions(billingEnvironment, isMock));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Missing PostgreSQL connection string.");
    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("Missing Redis connection string.");

    var options = ConfigurationOptions.Parse(connectionString);
    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 1000;
    options.ConnectRetry = 1;
    options.SyncTimeout = 1000;
    options.AsyncTimeout = 1000;
    return ConnectionMultiplexer.Connect(options);
});

var identityBaseUrl = builder.Configuration["Services:Identity:BaseUrl"]
    ?? "http://127.0.0.1:5004";
builder.Services.AddHttpClient("IdentityApi", client =>
{
    client.BaseAddress = new Uri(identityBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var analysisBaseUrl = builder.Configuration["Services:Analysis:BaseUrl"]
    ?? "http://127.0.0.1:5001";
builder.Services.AddHttpClient("AnalysisApi", client =>
{
    client.BaseAddress = new Uri(analysisBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(5);
});
var profileBaseUrl = builder.Configuration["Services:Profile:BaseUrl"]
    ?? "http://127.0.0.1:5005";
builder.Services.AddHttpClient("ProfileApi", client =>
{
    client.BaseAddress = new Uri(profileBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<PackageRepository>();
builder.Services.AddSingleton<PackageCatalogService>();
builder.Services.AddSingleton<OrderRepository>();
builder.Services.AddSingleton<PaymentSignatureService>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<BillingOwnerAuthorizer>();
builder.Services.AddSingleton<CreditReservationRepository>();
builder.Services.AddSingleton<CreditSettlementService>();
builder.Services.AddSingleton<StaleReservationReconciliationService>();
builder.Services.AddHostedService<StaleReservationReconciliationWorker>();
builder.Services.AddSingleton<ServiceTokenValidator>();
builder.Services.AddSingleton<SystemAdminAuthorizer>();
builder.Services.AddSingleton<ProfileAccessClient>();
builder.Services.AddSingleton<AnalysisSettlementResolverClient>();
builder.Services.AddSingleton<SettlementInboxRepository>();
builder.Services.Configure<BillingSettlementOptions>(
    builder.Configuration.GetSection(BillingSettlementOptions.Section));
builder.Services.AddHostedService<AnalysisSettlementWorker>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("AURA.Billing.Api.UnhandledException");
    logger.LogError(exception, "Unhandled Billing API error.");

    var correlationId = context.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("D");
    var databaseUnavailable = exception is NpgsqlException or TimeoutException;
    context.Response.StatusCode = databaseUnavailable
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new BillingErrorEnvelope(
        new BillingErrorDetails(
            databaseUnavailable ? "ERR_BILLING_DEPENDENCY_UNAVAILABLE" : "ERR_INTERNAL",
            databaseUnavailable ? "Billing storage is temporarily unavailable." : "Đã xảy ra lỗi nội bộ.",
            correlationId,
            databaseUnavailable)));
}));

app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId)
        || correlationId.Length > 64
        || !Regex.IsMatch(correlationId, "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant))
    {
        correlationId = Guid.NewGuid().ToString("D");
    }

    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    await next();
});

app.UseSwagger();
app.UseSwaggerUI();

static IResult BillingError(
    HttpContext context,
    int statusCode,
    string code,
    string message,
    bool retryable = false) =>
    Results.Json(new BillingErrorEnvelope(new BillingErrorDetails(
        code,
        message,
        context.Items["CorrelationId"] as string ?? Guid.NewGuid().ToString("D"),
        retryable)), statusCode: statusCode);

static bool TryReadIdempotencyKey(HttpContext context, out Guid key)
{
    key = Guid.Empty;
    var value = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
    return Guid.TryParse(value, out key) && key != Guid.Empty;
}

static IResult ReservationConflict(HttpContext context, ReservationOperationResult result) =>
    result.Status == ReservationOperationStatus.IdempotencyConflict
        ? BillingError(context, StatusCodes.Status409Conflict, "ERR_IDEMPOTENCY_CONFLICT",
            "Idempotency-Key đã được dùng cho một yêu cầu khác.")
        : BillingError(context, StatusCodes.Status409Conflict, "ERR_RESERVATION_STATE_CONFLICT",
            "Trạng thái reservation không cho phép thao tác này.");

await app.Services.GetRequiredService<PackageRepository>()
    .VerifyDatabaseAsync(CancellationToken.None);
await app.Services.GetRequiredService<OrderRepository>()
    .VerifyDatabaseAsync(CancellationToken.None);
await app.Services.GetRequiredService<CreditReservationRepository>()
    .VerifyDatabaseAsync(CancellationToken.None);

app.MapHealthChecks("/health");

// Public package catalog remains anonymous and continues to read the legacy PoC catalog.
app.MapGet("/api/v1/billing/packages", async (
    PackageCatalogService service,
    CancellationToken cancellationToken) =>
{
    var response = await service.GetAllAsync(cancellationToken);
    return Results.Ok(response);
})
.WithName("GetPackageCatalog")
.WithTags("Package Catalog")
.Produces<PackageCatalogResponse>();

// Legacy catalog update is retained for compatibility, but now requires SYSTEM_ADMIN.
app.MapPut("/api/v1/billing/packages/{id:int}", async Task<IResult> (
    int id,
    UpdatePackageRequest request,
    HttpContext context,
    PackageCatalogService service,
    SystemAdminAuthorizer adminAuthorizer,
    CancellationToken cancellationToken) =>
{
    var authorization = await adminAuthorizer.AuthorizeAsync(context.Request, cancellationToken);
    if (!authorization.Allowed)
    {
        return BillingError(context, authorization.StatusCode, authorization.Code,
            authorization.Message, authorization.Retryable);
    }

    if (string.IsNullOrWhiteSpace(request.Name)
        || request.Name.Length > 100
        || request.Price < 0
        || request.Credits <= 0)
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Name phải có từ 1 đến 100 ký tự; price không âm và credits phải lớn hơn 0.");
    }

    var response = await service.UpdateAsync(id, request, cancellationToken);
    return response is null
        ? BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Không tìm thấy package.")
        : Results.Ok(response);
})
.WithName("UpdatePackage")
.WithTags("Package Catalog")
.Produces<UpdatePackageResponse>()
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound);

// =============================================================================
// SCRUM-346: Cổng Thanh toán Sandbox, Gói cước và Cấp Credit Idempotent
// =============================================================================

app.MapPost("/api/v1/billing/orders", async Task<IResult> (
    CreateOrderRequest request,
    HttpContext context,
    OrderService orderService,
    BillingRuntimeOptions runtimeOptions,
    BillingOwnerAuthorizer ownerAuthorizer,
    CancellationToken cancellationToken) =>
{
    if (!TryReadIdempotencyKey(context, out var idempotencyKey))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Header Idempotency-Key hợp lệ dạng UUID là bắt buộc.");
    }

    if (request.PackageId <= 0 ||
        request.BillingOwner is null ||
        request.BillingOwner.Id == Guid.Empty ||
        !(string.Equals(request.BillingOwner.Type, "ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(request.BillingOwner.Type, "CLINIC", StringComparison.OrdinalIgnoreCase)))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "PackageId phải lớn hơn 0 và BillingOwner (ACCOUNT hoặc CLINIC) là bắt buộc.");
    }

    var authority = await ownerAuthorizer.AuthorizeAsync(context.Request,request.BillingOwner.Type.ToUpperInvariant(),request.BillingOwner.Id,true,cancellationToken);
    if(!authority.Allowed)return BillingError(context,authority.StatusCode,authority.Code,"Billing owner access denied.",authority.StatusCode==503);
    if(request.TenantId.HasValue && request.TenantId!=authority.TenantId)return BillingError(context,403,"ERR_FORBIDDEN","Tenant does not match authoritative context.");
    request=request with {TenantId=authority.TenantId};

    var result = await orderService.CreateOrderAsync(
        request, idempotencyKey, runtimeOptions, cancellationToken);

    if (result.Status == OrderOperationStatus.PackageNotFound)
    {
        return BillingError(context, StatusCodes.Status404NotFound, "ERR_PACKAGE_NOT_FOUND",
            "Không tìm thấy gói cước hoặc phiên bản giá tương ứng đang kích hoạt.");
    }

    if (result.Status == OrderOperationStatus.IdempotencyConflict)
    {
        return BillingError(context, StatusCodes.Status409Conflict, "ERR_IDEMPOTENCY_CONFLICT",
            "Idempotency-Key đã được sử dụng cho một đơn hàng khác.");
    }

    if (result.Status == OrderOperationStatus.Replayed && result.Order is not null)
    {
        context.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Ok(result.Order);
    }

    context.Response.Headers["Location"] = $"/api/v1/billing/orders/{result.Order!.OrderId:D}";
    return Results.Created($"/api/v1/billing/orders/{result.Order!.OrderId:D}", result.Order);
})
.WithName("CreateBillingOrder")
.WithTags("Billing Orders")
.Produces<CreateOrderResponse>(StatusCodes.Status201Created)
.Produces<CreateOrderResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict);

app.MapGet("/api/v1/billing/orders/{orderId:guid}", async Task<IResult> (
    Guid orderId,
    HttpContext context,
    OrderRepository repository,
    BillingOwnerAuthorizer ownerAuthorizer,
    CancellationToken cancellationToken) =>
{
    var authenticated=await ownerAuthorizer.AuthenticateAsync(context.Request,cancellationToken);
    if(!authenticated.Allowed)return BillingError(context,authenticated.StatusCode,authenticated.Code,"Authentication required.",authenticated.StatusCode==503);
    var order = await repository.GetOrderByIdAsync(orderId, cancellationToken);
    if (order is null)
    {
        return BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Không tìm thấy đơn hàng.");
    }

    var authority=await ownerAuthorizer.AuthorizeAsync(context.Request,order.BillingOwnerType,order.BillingOwnerId,false,cancellationToken);
    if(!authority.Allowed)return BillingError(context,authority.StatusCode,authority.Code,"Billing owner access denied.",authority.StatusCode==503);
    if(authority.TenantId!=order.TenantId)return BillingError(context,404,"ERR_NOT_FOUND","Order not found.");

    return Results.Ok(order);
})
.WithName("GetBillingOrder")
.WithTags("Billing Orders")
.Produces<OrderRecord>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound);

app.MapPost("/api/v1/billing/payments/callback", async Task<IResult> (
    PaymentCallbackPayload payload,
    HttpContext context,
    OrderService orderService,
    CancellationToken cancellationToken) =>
{
    if (payload.OrderId == Guid.Empty ||
        string.IsNullOrWhiteSpace(payload.TransactionId) ||
        payload.Amount <= 0 ||
        string.IsNullOrWhiteSpace(payload.Currency) ||
        string.IsNullOrWhiteSpace(payload.Signature))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Payload callback thiếu thông tin bắt buộc hoặc chữ ký không hợp lệ.");
    }

    var paymentMethod = context.Request.Headers["X-Payment-Method"].FirstOrDefault() ?? "SANDBOX_MOCK";
    var result = await orderService.ProcessCallbackAsync(payload, paymentMethod, cancellationToken);

    if (!result.Success)
    {
        if (result.Status == "ERR_INVALID_SIGNATURE")
        {
            return BillingError(context, StatusCodes.Status400BadRequest, "ERR_INVALID_SIGNATURE",
                result.Message);
        }

        if (result.Status == "NOT_FOUND")
        {
            return BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND",
                result.Message);
        }

        if (result.Status == "ERR_CONCURRENT_PROCESSING")
        {
            return BillingError(context, StatusCodes.Status409Conflict, "ERR_CONCURRENT_PROCESSING",
                result.Message, retryable: true);
        }

        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_PAYMENT_FAILED",
            result.Message);
    }

    if (result.Replayed)
    {
        context.Response.Headers["Idempotent-Replayed"] = "true";
    }

    return Results.Ok(result);
})
.WithName("PaymentWebhookCallback")
.WithTags("Billing Payments")
.Produces<PaymentCallbackResult>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict);

app.MapPost("/api/v1/billing/sandbox/pay", async Task<IResult> (
    SandboxPaymentRequest request,
    HttpContext context,
    OrderService orderService,
    OrderRepository repository,
    BillingOwnerAuthorizer ownerAuthorizer,
    CancellationToken cancellationToken) =>
{
    if (request.OrderId == Guid.Empty)
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "OrderId là bắt buộc.");
    }

    var authenticated=await ownerAuthorizer.AuthenticateAsync(context.Request,cancellationToken);
    if(!authenticated.Allowed)return BillingError(context,authenticated.StatusCode,authenticated.Code,"Authentication required.",authenticated.StatusCode==503);
    var order = await repository.GetOrderByIdAsync(request.OrderId, cancellationToken);
    if (order is null)
    {
        return BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Không tìm thấy đơn hàng.");
    }

    var authority=await ownerAuthorizer.AuthorizeAsync(context.Request,order.BillingOwnerType,order.BillingOwnerId,true,cancellationToken);
    if(!authority.Allowed)return BillingError(context,authority.StatusCode,authority.Code,"Billing owner access denied.",authority.StatusCode==503);
    if(authority.TenantId!=order.TenantId)return BillingError(context,404,"ERR_NOT_FOUND","Order not found.");

    var result = await orderService.SimulateSandboxPaymentAsync(request, cancellationToken);
    if (result.Status == "NOT_FOUND")
    {
        return BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", result.Message);
    }
    if (result.Status == "FORBIDDEN")
    {
        return BillingError(context, StatusCodes.Status403Forbidden, "ERR_FORBIDDEN", result.Message);
    }

    if (result.Replayed)
    {
        context.Response.Headers["Idempotent-Replayed"] = "true";
    }

    return Results.Ok(result);
})
.WithName("SimulateSandboxPayment")
.WithTags("Billing Sandbox")
.Produces<SandboxPaymentResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound);

app.MapPost("/internal/v1/billing/reservations", async Task<IResult> (
    ReserveCreditRequest request,
    HttpContext context,
    ServiceTokenValidator tokenValidator,
    ProfileAccessClient profileAccessClient,
    CreditReservationRepository repository,
    BillingRuntimeOptions runtimeOptions,
    CancellationToken cancellationToken) =>
{
    var tokenResult = await tokenValidator.ValidateAsync(
        context.Request, "billing:reserve", "analysis-api", cancellationToken);
    if (!tokenResult.Succeeded)
    {
        return BillingError(context, tokenResult.StatusCode, tokenResult.Code,
            tokenResult.Message, tokenResult.Retryable);
    }

    if (!TryReadIdempotencyKey(context, out var idempotencyKey))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Header Idempotency-Key hợp lệ dạng UUID là bắt buộc.");
    }

    if (request.AnalysisId == Guid.Empty
        || request.TenantId == Guid.Empty
        || request.ActorAccountId == Guid.Empty
        || request.PatientId == Guid.Empty
        || request.BillingOwner is null
        || request.BillingOwner.Id == Guid.Empty
        || request.Units != 1
        || request.Generation < 1
        || !(string.Equals(request.BillingOwner.Type, "ACCOUNT", StringComparison.OrdinalIgnoreCase)
             || string.Equals(request.BillingOwner.Type, "CLINIC", StringComparison.OrdinalIgnoreCase)))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Reserve yêu cầu UUID hợp lệ, billingOwner ACCOUNT/CLINIC, units=1 và generation dương.");
    }

    var normalized = request with
    {
        BillingOwner = request.BillingOwner with { Type = request.BillingOwner.Type.Trim().ToUpperInvariant() }
    };
    var profileDecision = await profileAccessClient.ValidateForAnalysisCreateAsync(
        normalized, cancellationToken);
    if (!profileDecision.Allowed)
    {
        return BillingError(context,
            profileDecision.DependencyUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status403Forbidden,
            profileDecision.Code,
            profileDecision.Message,
            retryable: profileDecision.DependencyUnavailable);
    }

    var fingerprint = ReservationFingerprint.ForReserve(normalized, runtimeOptions);
    var result = await repository.ReserveAsync(
        normalized, idempotencyKey, fingerprint, runtimeOptions, cancellationToken);

    if (result.Status == ReservationOperationStatus.Created && result.Reservation is not null)
    {
        context.Response.Headers["Location"] = $"/internal/v1/billing/reservations/{result.Reservation.ReservationId:D}";
        return Results.Created(
            $"/internal/v1/billing/reservations/{result.Reservation.ReservationId:D}",
            result.Reservation);
    }

    if (result.Status == ReservationOperationStatus.Replayed && result.Reservation is not null)
    {
        context.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Ok(result.Reservation);
    }

    if (result.Status == ReservationOperationStatus.InsufficientCredit)
    {
        return BillingError(context, StatusCodes.Status402PaymentRequired,
            "ERR_INSUFFICIENT_CREDIT", "Không đủ credit để tạo reservation.");
    }

    return ReservationConflict(context, result);
})
.WithName("ReserveAnalysisCredit")
.WithTags("Billing Internal")
.Produces<ReservationResponse>(StatusCodes.Status201Created)
.Produces<ReservationResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status402PaymentRequired)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status409Conflict)
.Produces(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/internal/v1/billing/reservations/{reservationId:guid}", async Task<IResult> (
    Guid reservationId,
    HttpContext context,
    ServiceTokenValidator tokenValidator,
    CreditReservationRepository repository,
    CancellationToken cancellationToken) =>
{
    var tokenResult = await tokenValidator.ValidateAsync(
        context.Request, "billing:read", "analysis-api", cancellationToken);
    if (!tokenResult.Succeeded)
    {
        return BillingError(context, tokenResult.StatusCode, tokenResult.Code,
            tokenResult.Message, tokenResult.Retryable);
    }

    var reservation = await repository.GetAsync(reservationId, cancellationToken);
    return reservation is null
        ? BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Không tìm thấy reservation.")
        : Results.Ok(reservation);
})
.WithName("GetBillingReservation")
.WithTags("Billing Internal")
.Produces<ReservationResponse>()
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/internal/v1/billing/reservations/{reservationId:guid}/capture", async Task<IResult> (
    Guid reservationId,
    HttpContext context,
    ServiceTokenValidator tokenValidator,
    CreditReservationRepository repository,
    CancellationToken cancellationToken) =>
{
    // The event consumer should call the domain service in-process. This route is
    // retained for the internal contract and requires an explicit billing:capture scope.
    var tokenResult = await tokenValidator.ValidateAsync(
        context.Request, "billing:capture", null, cancellationToken);
    if (!tokenResult.Succeeded)
    {
        return BillingError(context, tokenResult.StatusCode, tokenResult.Code,
            tokenResult.Message, tokenResult.Retryable);
    }

    if (!TryReadIdempotencyKey(context, out var idempotencyKey))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Header Idempotency-Key hợp lệ dạng UUID là bắt buộc.");
    }

    var result = await repository.CaptureAsync(
        reservationId, idempotencyKey, ReservationFingerprint.ForCapture(reservationId), cancellationToken);
    if (result.Status == ReservationOperationStatus.Changed && result.Reservation is not null)
    {
        return Results.Ok(result.Reservation);
    }
    if (result.Status == ReservationOperationStatus.Replayed && result.Reservation is not null)
    {
        context.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Ok(result.Reservation);
    }
    if (result.Status == ReservationOperationStatus.NotFound)
    {
        return BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Không tìm thấy reservation.");
    }
    if (result.Status == ReservationOperationStatus.MockLiveBlocked)
    {
        return BillingError(context, StatusCodes.Status409Conflict, "ERR_MOCK_LIVE_FORBIDDEN",
            "Mock result không được capture vào ví LIVE.");
    }
    return ReservationConflict(context, result);
})
.WithName("CaptureBillingReservation")
.WithTags("Billing Internal")
.Produces<ReservationResponse>()
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict)
.Produces(StatusCodes.Status503ServiceUnavailable);

app.MapPost("/internal/v1/billing/reservations/{reservationId:guid}/release", async Task<IResult> (
    Guid reservationId,
    ReleaseCreditRequest request,
    HttpContext context,
    ServiceTokenValidator tokenValidator,
    CreditReservationRepository repository,
    CancellationToken cancellationToken) =>
{
    var tokenResult = await tokenValidator.ValidateAsync(
        context.Request, "billing:release", "analysis-api", cancellationToken);
    if (!tokenResult.Succeeded)
    {
        return BillingError(context, tokenResult.StatusCode, tokenResult.Code,
            tokenResult.Message, tokenResult.Retryable);
    }

    if (!TryReadIdempotencyKey(context, out var idempotencyKey))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Header Idempotency-Key hợp lệ dạng UUID là bắt buộc.");
    }

    var allowedReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        "VALIDATION_ABORT", "STORAGE_ABORT", "ANALYSIS_ABORT", "FINAL_FAILURE", "LOW_QUALITY",
        "STALE_RESERVATION_AUTO_REFUND", "BATCH_PARTIAL_REFUND"
    };
    var reason = request.Reason?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(reason) || !allowedReasons.Contains(reason))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "reason phải là VALIDATION_ABORT, STORAGE_ABORT, ANALYSIS_ABORT, FINAL_FAILURE, LOW_QUALITY, STALE_RESERVATION_AUTO_REFUND hoặc BATCH_PARTIAL_REFUND.");
    }

    var result = await repository.ReleaseAsync(
        reservationId, idempotencyKey, ReservationFingerprint.ForRelease(reservationId, reason),
        reason, cancellationToken);
    if (result.Status == ReservationOperationStatus.Changed && result.Reservation is not null)
    {
        return Results.Ok(result.Reservation);
    }
    if (result.Status == ReservationOperationStatus.Replayed && result.Reservation is not null)
    {
        context.Response.Headers["Idempotent-Replayed"] = "true";
        return Results.Ok(result.Reservation);
    }
    if (result.Status == ReservationOperationStatus.NotFound)
    {
        return BillingError(context, StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Không tìm thấy reservation.");
    }
    return ReservationConflict(context, result);
})
.WithName("ReleaseBillingReservation")
.WithTags("Billing Internal")
.Produces<ReservationResponse>()
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict)
.Produces(StatusCodes.Status503ServiceUnavailable);

// =============================================================================
// SCRUM-348: Billing Cấp Tổ chức, Reconciliation Engine & Batch Partial Refund
// =============================================================================

app.MapPost("/api/v1/admin/billing/reservations/batch-refund", async Task<IResult> (
    BatchRefundRequest request,HttpContext context,SystemAdminAuthorizer admins,BillingOwnerAuthorizer identities,CreditReservationRepository repository,CancellationToken ct) =>
{
    var permitted=await admins.AuthorizeAsync(context.Request,ct);
    if(!permitted.Allowed)return BillingError(context,permitted.StatusCode,permitted.Code,permitted.Message,permitted.Retryable);
    var actor=await identities.AuthenticateAsync(context.Request,ct);
    if(!actor.Allowed)return BillingError(context,actor.StatusCode,actor.Code,"Verified administrator identity required.",actor.StatusCode==503);
    if(!TryReadIdempotencyKey(context,out var key) || request.TenantId==Guid.Empty || request.Items is null || request.Items.Count is <1 or >100
        || request.Items.Any(i=>i.ReservationId==Guid.Empty || string.IsNullOrWhiteSpace(i.Reason) || i.Reason.Length>500))
        return BillingError(context,400,"ERR_VALIDATION","Refund requires tenant,1–100 reservations,explicit reasons and Idempotency-Key.");
    var result=await repository.ExecuteBatchRefundAsync(request,key,true,ct,new RefundAuthorization(actor.AccountId,"SYSTEM_ADMIN"));
    return Results.Ok(result);
});

app.MapPost("/internal/v1/billing/reservations/batch-refund", async Task<IResult> (
    BatchRefundRequest request,
    HttpContext context,
    ServiceTokenValidator tokenValidator,
    CreditReservationRepository repository,
    AnalysisSettlementResolverClient resolver,
    CancellationToken cancellationToken) =>
{
    var tokenResult = await tokenValidator.ValidateAnyScopeAsync(
        context.Request, ["billing:refund", "billing:release"], "analysis-api", cancellationToken);
    if (!tokenResult.Succeeded)
    {
        return BillingError(context, tokenResult.StatusCode, tokenResult.Code,
            tokenResult.Message, tokenResult.Retryable);
    }

    if (!TryReadIdempotencyKey(context, out var batchIdempotencyKey))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "Header Idempotency-Key hợp lệ dạng UUID là bắt buộc.");
    }

    if (request.TenantId == Guid.Empty || request.Items is null || request.Items.Count is <1 or >100 || request.Items.Any(i=>i.ReservationId==Guid.Empty))
    {
        return BillingError(context, StatusCodes.Status400BadRequest, "ERR_VALIDATION",
            "TenantId và danh sách items cần hoàn credit không được rỗng.");
    }

    var canRefundCaptured = tokenResult.HasScope("billing:refund");
    var evidence=new Dictionary<Guid,string>();
    {
        foreach(var item in request.Items)
        {
            var reservation=await repository.GetAsync(item.ReservationId,request.TenantId,cancellationToken);
            if(reservation is null)return BillingError(context,404,"ERR_NOT_FOUND","Không tìm thấy reservation.");
            if(reservation.State is not ("CAPTURED" or "RESERVED"))continue;
            if(reservation.State=="CAPTURED" && !canRefundCaptured)return BillingError(context,403,"ERR_FORBIDDEN","Captured refund requires billing:refund.");
            var eventId=new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"refund-evidence|{item.ReservationId:D}|{reservation.Generation}"))[..16]);
            SettlementResolutionResponse decision;
            try{decision=await resolver.ResolveAsync(eventId,reservation.AnalysisId,request.TenantId,context.Items["CorrelationId"] as string,cancellationToken);}
            catch(SettlementResolverCallException){return BillingError(context,503,"ERR_REFUND_EVIDENCE_UNAVAILABLE","Refund outcome could not be verified.",true);}
            if(!decision.Applicable || decision.SettlementAction!="RELEASE" || decision.TenantId!=request.TenantId || decision.AnalysisId!=reservation.AnalysisId || decision.ReservationId!=item.ReservationId || decision.Generation!=reservation.Generation)
                return BillingError(context,409,"ERR_REFUND_EVIDENCE_REQUIRED","Captured refund requires authoritative terminal-failure evidence or an audited SYSTEM_ADMIN approval.");
            evidence[item.ReservationId]=$"{eventId:D}|{reservation.AnalysisId:D}|{reservation.Generation}|{decision.Reason}";
        }
    }
    var response = await repository.ExecuteBatchRefundAsync(request, batchIdempotencyKey, canRefundCaptured, cancellationToken,new RefundAuthorization(null,"ANALYSIS_TERMINAL",evidence));
    return Results.Ok(response);
})
.WithName("BatchRefundReservations")
.WithTags("Billing Internal")
.Produces<BatchRefundResponse>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status403Forbidden);

app.MapPost("/internal/v1/billing/reconcile-stale", async Task<IResult> (
    ReconcileStaleRequest? request,
    HttpContext context,
    ServiceTokenValidator tokenValidator,
    StaleReservationReconciliationService reconciliationService,
    CancellationToken cancellationToken) =>
{
    var tokenResult = await tokenValidator.ValidateAsync(
        context.Request, "billing:release", null, cancellationToken);
    if (!tokenResult.Succeeded)
    {
        return BillingError(context, tokenResult.StatusCode, tokenResult.Code,
            tokenResult.Message, tokenResult.Retryable);
    }

    var threshold = request?.OlderThanMinutes ?? 30;
    var limit = request?.Limit ?? 100;

    var report = await reconciliationService.ReconcileStaleReservationsAsync(
        threshold, limit, cancellationToken);

    return Results.Ok(report);
})
.WithName("TriggerStaleReconciliation")
.WithTags("Billing Internal")
.Produces<ReconciliationReport>(StatusCodes.Status200OK);

app.UseAuraPrometheusMetrics();

app.Run();
