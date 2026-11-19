using System.Net.Http.Headers;
using System.Security.Claims;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;

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
        if (!context.Request.IsHttps && IsCameraPost(context.Request) &&
            context.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("Gateway:RequireCameraHttps"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!RequiresUserToken(context.Request))
        {
            await next(context);
            return;
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

    private static bool IsCameraPost(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        string.Equals(request.Path.Value?.TrimEnd('/'), "/api/v1/analyses/camera", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresUserToken(HttpRequest request)
    {
        if (
            !request.Path.StartsWithSegments("/api/v1") ||
            HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        var path = (request.Path.Value ?? "").TrimEnd('/');

        // Camera bearer is verified by Profile against the authoritative camera grant.
        if (IsCameraPost(request))
            return false;

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