using System.Net.Http.Headers;
using System.Security.Claims;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Http.Features;

namespace AURA.ApiGateway;

public static class GatewayFirebaseAuthRegistration
{
    public static IServiceCollection AddGatewayFirebaseAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Initialized only when a protected request requires verification.
        services.AddSingleton<FirebaseApp>(_ =>
        {
            var projectId = configuration["Firebase:ProjectId"] ?? "aura-dev-97716";

            if (string.IsNullOrWhiteSpace(projectId))
            {
                throw new InvalidOperationException(
                    "Missing Firebase:ProjectId.");
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            {
                var defaultCredPath = configuration["Firebase:CredentialPath"]
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".aura", "credentials", "aura-dev-service-account.json");

                if (File.Exists(defaultCredPath))
                {
                    Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", defaultCredPath);
                }
            }

            return FirebaseApp.Create(
                new AppOptions
                {
                    ProjectId = projectId,
                    Credential = GoogleCredential.GetApplicationDefault()
                },
                "aura-gateway");
        });

        services.AddSingleton<FirebaseAuth>(provider =>
            FirebaseAuth.GetAuth(
                provider.GetRequiredService<FirebaseApp>()));

        return services;
    }
}

public sealed class GatewayFirebaseAuthMiddleware(
    RequestDelegate next,
    ILogger<GatewayFirebaseAuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var isConsultationHub = context.Request.Path.StartsWithSegments("/hubs/consultations");
        if (!isConsultationHub && !RequiresUserToken(context.Request))
        {
            await next(context);
            return;
        }

        if (isConsultationHub)
        {
            var tokenQuery = ExtractAndRedactHubToken(context.Request);
            if (!tokenQuery.Valid || tokenQuery.Token is not null && !IsHubQueryTokenTransport(context.Request))
            {
                await Unauthorized(context);
                return;
            }
            if (string.IsNullOrWhiteSpace(context.Request.Headers.Authorization) && tokenQuery.Token is not null)
                context.Request.Headers.Authorization = "Bearer " + tokenQuery.Token;
        }

        var authorization = context.Request.Headers.Authorization;

        if (
            authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(
                authorization[0],
                out var header) ||
            !string.Equals(
                header.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            await Unauthorized(context);
            return;
        }

        FirebaseToken verifiedToken;

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted);

        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var auth = context.RequestServices
                .GetRequiredService<FirebaseAuth>();

            verifiedToken = await auth.VerifyIdTokenAsync(
                header.Parameter,
                checkRevoked: true,
                cancellationToken: timeout.Token);
        }
        catch (FirebaseAuthException exception)
            when (
                exception.AuthErrorCode is
                    AuthErrorCode.InvalidIdToken or
                    AuthErrorCode.ExpiredIdToken or
                    AuthErrorCode.RevokedIdToken or
                    AuthErrorCode.UserNotFound)
        {
            await Unauthorized(context);
            return;
        }
        catch (ArgumentException)
        {
            await Unauthorized(context);
            return;
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await Unavailable(context);
            return;
        }
        catch (Exception exception)
        {
            // Do not log token, credentials or exception message.
            logger.LogWarning(
                "Gateway Firebase verification unavailable. Type: {ErrorType}",
                exception.GetType().Name);

            await Unavailable(context);
            return;
        }

        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        verifiedToken.Uid),
                    new Claim("sub", verifiedToken.Uid)
                ],
                authenticationType: "Firebase"));

        // Preserve the original Authorization header for the destination API.
        // Do not infer internal account IDs or roles from the Firebase UID.
        await next(context);
    }

    private static bool RequiresUserToken(HttpRequest request)
    {
        if (
            !request.Path.StartsWithSegments("/api/v1") ||
            HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        var path = (request.Path.Value ?? "").TrimEnd('/');

        // Support camera ingestion via InvokeHTTP with Service Token / device credentials
        if (string.Equals(path, "/api/v1/analyses/camera", StringComparison.OrdinalIgnoreCase))
        {
            var authHeader = request.Headers.Authorization.ToString();
            var hasDeviceOrServiceToken = request.Headers.ContainsKey("x-device-id") ||
                                          request.Headers.ContainsKey("x-service-token") ||
                                          authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            if (hasDeviceOrServiceToken)
            {
                return false;
            }
        }

        // Exempt webhook callback and sandbox payment simulator from Firebase token (secured via HMAC SHA-256)
        if (string.Equals(path, "/api/v1/billing/payments/callback", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/v1/billing/sandbox/pay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var anonymousGet =
            HttpMethods.IsGet(request.Method) &&
            (
                string.Equals(
                    path,
                    "/api/v1/platform/ping",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    path,
                    "/api/v1/billing/packages",
                    StringComparison.OrdinalIgnoreCase)
            );

        return !anonymousGet;
    }

    private static (bool Valid, string? Token) ExtractAndRedactHubToken(HttpRequest request)
    {
        var query = request.Query;
        var values = query["access_token"];
        var remaining = query.Where(pair => !string.Equals(pair.Key, "access_token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        request.QueryString = QueryString.Create(remaining);
        request.Query = new QueryCollection(remaining);
        if (values.Count == 0) return (true, null);
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) return (false, null);
        return (true, values[0]);
    }

    private static bool IsHubQueryTokenTransport(HttpRequest request) =>
        string.Equals(request.Path.Value, "/hubs/consultations", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsGet(request.Method) &&
        (request.HttpContext.Features.Get<IHttpWebSocketFeature>()?.IsWebSocketRequest == true || request.Headers["Accept"].Any(value =>
            value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true));

    private static Task Unauthorized(HttpContext context)
    {
        context.Response.Headers["WWW-Authenticate"] = "Bearer";

        return WriteError(
            context,
            StatusCodes.Status401Unauthorized,
            "ERR_UNAUTHORIZED",
            "Cần Firebase ID token hợp lệ.",
            retryable: false);
    }

    private static Task Unavailable(HttpContext context)
    {
        return WriteError(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "ERR_AUTH_UNAVAILABLE",
            "Chưa xác minh được phiên đăng nhập. Hãy thử lại.",
            retryable: true);
    }

    private static Task WriteError(
        HttpContext context,
        int status,
        string code,
        string message,
        bool retryable)
    {
        context.Response.StatusCode = status;
        context.Response.Headers["Cache-Control"] = "no-store";

        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code,
                message,
                correlationId = context.TraceIdentifier,
                retryable
            }
        });
    }
}
