using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using AURA.ApiGateway;

var builder = WebApplication.CreateBuilder(args);

const string corsPolicyName = "AURA.Web";
const string correlationHeader = "X-Correlation-Id";

var allowedOrigins = builder.Configuration
    .GetSection("Gateway:AllowedOrigins")
    .Get<string[]>()
    ?? [];

var maxRequestBodyBytes = builder.Configuration
    .GetValue<long>("Gateway:MaxRequestBodyBytes");

var maxUploadBodyBytes = builder.Configuration
    .GetValue<long>("Gateway:MaxUploadBodyBytes");

if (allowedOrigins.Length == 0)
{
    throw new InvalidOperationException(
        "Gateway:AllowedOrigins must contain at least one origin.");
}

foreach (var origin in allowedOrigins)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp &&
         uri.Scheme != Uri.UriSchemeHttps) ||
        origin != uri.GetLeftPart(UriPartial.Authority))
    {
        throw new InvalidOperationException(
            "Each Gateway:AllowedOrigins entry must be an HTTP/HTTPS " +
            "origin without a path or trailing slash.");
    }
}

if (maxRequestBodyBytes <= 0 ||
    maxUploadBodyBytes < maxRequestBodyBytes)
{
    throw new InvalidOperationException(
        "Gateway request body limits are invalid.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBodyBytes;
});

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
var uploadTimeoutSeconds = builder.Configuration.GetValue<int>(
    "Gateway:UploadTimeoutSeconds");

if (uploadTimeoutSeconds <= 0)
{
    throw new InvalidOperationException(
        "Gateway:UploadTimeoutSeconds must be positive.");
}

builder.Services.AddRequestTimeouts(options =>
{
    options.AddPolicy("analysis-upload", new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(uploadTimeoutSeconds),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        WriteTimeoutResponse = async context =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";

            // The request timeout token has already been cancelled.
            // Use a separate bounded token to write the error response.
            using var responseTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = new
                    {
                        code = "ERR_GATEWAY_TIMEOUT",
                        message = "Yêu cầu vượt thời gian xử lý cho phép.",
                        correlationId = context.TraceIdentifier,
                        retryable = true
                    }
                },
                cancellationToken: responseTimeout.Token);
        }
    });
});
builder.Services.AddCors(options =>
{
    options.AddPolicy(corsPolicyName, policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .WithMethods(
                "GET",
                "POST",
                "PUT",
                "PATCH",
                "DELETE",
                "OPTIONS")
            .WithHeaders(
                "Authorization",
                "Content-Type",
                "Idempotency-Key",
                correlationHeader,
                "X-Clinic-Id",
                "If-Match",
                "If-None-Match")
            .WithExposedHeaders(
                correlationHeader,
                "Idempotent-Replayed",
                "Location",
                "Retry-After",
                "ETag");
    });
});

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(
        builder.Configuration.GetSection("ReverseProxy"));
        builder.Services.AddGatewayFirebaseAuth(builder.Configuration);
var app = builder.Build();

app.Use(async (context, next) =>
{
    var suppliedValues = context.Request.Headers[correlationHeader];
    var suppliedId = suppliedValues.Count == 1
        ? suppliedValues[0]
        : null;

    var correlationId = IsValidCorrelationId(suppliedId)
        ? suppliedId!
        : Guid.NewGuid().ToString("N");

    context.TraceIdentifier = correlationId;

    context.Request.Headers[correlationHeader] = correlationId;

    context.Response.OnStarting(() =>
    {
        context.Response.Headers[correlationHeader] = correlationId;
        return Task.CompletedTask;
    });

    using var scope = app.Logger.BeginScope(
        new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        });

    await next(context);
});

app.UseExceptionHandler();
app.UseAuraPrometheusMetrics();

app.Use(async (context, next) =>
{
    context.Request.Headers.Remove("X-Account-Id");
    context.Request.Headers.Remove("X-Role");
    context.Request.Headers.Remove("X-Tenant-Id");
    context.Request.Headers.Remove("X-User-Authorization");

    await next(context);
});

app.UseRouting();
app.UseCors(corsPolicyName);
app.UseRequestTimeouts();

app.Use(async (context, next) =>
{
    await next(context);

    var timeoutFeature = context.Features
        .Get<IHttpRequestTimeoutFeature>();

    if (
        timeoutFeature is not null &&
        timeoutFeature.RequestTimeoutToken.IsCancellationRequested &&
        !context.Response.HasStarted)
    {
        // YARP may return normally after cancellation with status 400.
        // Let the outer request-timeout middleware produce the 504 response.
        throw new OperationCanceledException(
            "Gateway request deadline exceeded.",
            timeoutFeature.RequestTimeoutToken);
    }
});

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var trimmedPath = path.TrimEnd('/');

    var isLargeUploadRoute =
        HttpMethods.IsPost(context.Request.Method) &&
        (string.Equals(trimmedPath, "/api/v1/analyses", StringComparison.OrdinalIgnoreCase) ||
         trimmedPath.StartsWith("/api/v1/analyses/", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(trimmedPath, "/api/v1/batches", StringComparison.OrdinalIgnoreCase) ||
         trimmedPath.StartsWith("/api/v1/batches/", StringComparison.OrdinalIgnoreCase));

    const long standardRouteLimit = 5L * 1024 * 1024; // 5MB limit for non-upload endpoints (DoS protection)

    var limit = isLargeUploadRoute
        ? maxUploadBodyBytes
        : Math.Min(maxRequestBodyBytes > 0 ? maxRequestBodyBytes : standardRouteLimit, standardRouteLimit);

    if (context.Request.ContentLength is long contentLength &&
        contentLength > limit)
    {
        await Results.Json(
            new
            {
                error = new
                {
                    code = "ERR_PAYLOAD_TOO_LARGE",
                    message = "Dung lượng yêu cầu vượt giới hạn cho phép.",
                    correlationId = context.TraceIdentifier,
                    retryable = false
                }
            },
            statusCode: StatusCodes.Status413PayloadTooLarge)
            .ExecuteAsync(context);

        return;
    }

    var bodySizeFeature = context.Features
        .Get<IHttpMaxRequestBodySizeFeature>();

    if (bodySizeFeature is { IsReadOnly: false })
    {
        bodySizeFeature.MaxRequestBodySize = limit;
    }

    await next(context);
});

app.UseMiddleware<GatewayFirebaseAuthMiddleware>();

app.MapHealthChecks("/health");
app.MapAuraMetrics();

app.MapReverseProxy()
    .RequireCors(corsPolicyName);

app.Run();

static bool IsValidCorrelationId(string? value)
{
    if (string.IsNullOrEmpty(value) || value.Length > 64)
    {
        return false;
    }

    foreach (var character in value)
    {
        var allowed =
            character is >= 'a' and <= 'z' ||
            character is >= 'A' and <= 'Z' ||
            character is >= '0' and <= '9' ||
            character is '-' or '_' or '.';

        if (!allowed)
        {
            return false;
        }
    }

    return true;
}