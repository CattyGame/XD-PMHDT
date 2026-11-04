using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AURA.Profile.Api;

public sealed record UserIdentityResolution(
    bool Succeeded, Guid AccountId, int StatusCode, string Code, string Message, bool Retryable, IReadOnlyList<string> Roles, string? Email = null, bool EmailVerified = false);

/// <summary>
/// Identity is the authority for Firebase verification and UID-to-account mapping.
/// No user identity is derived from request headers, GUIDs, or locally hashed UIDs.
/// </summary>
public sealed class UserIdentityResolver(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<UserIdentityResolution> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1
            || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization.Parameter)
            || authorization.Parameter.Any(char.IsWhiteSpace)
            || authorization.Parameter.Contains(','))
        {
            return Unauthorized();
        }

        var clientId = configuration["ServiceAuth:ClientId"];
        var clientSecret = configuration["ServiceAuth:ClientSecret"];
        if (clientId != "profile-api" || string.IsNullOrWhiteSpace(clientSecret))
        {
            return Unavailable();
        }

        try
        {
            var identity = clients.CreateClient("IdentityClient");
            using var tokenMessage = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/identity/service-tokens")
            {
                Content = JsonContent.Create(new
                {
                    clientId,
                    clientSecret,
                    audience = "aura-identity",
                    scope = "identity:context"
                })
            };
            ForwardCorrelation(request, tokenMessage);
            using var tokenResponse = await identity.SendAsync(tokenMessage, ct);
            if (tokenResponse.StatusCode != HttpStatusCode.OK)
            {
                return Unavailable();
            }
            var issued = await tokenResponse.Content.ReadFromJsonAsync<IssuedServiceToken>(cancellationToken: ct);
            if (issued is null || string.IsNullOrWhiteSpace(issued.AccessToken)
                || !string.Equals(issued.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return Unavailable();
            }

            using var contextMessage = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/identity/context");
            contextMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.AccessToken);
            // Ignore client-supplied X-User-Authorization; copy only the actual public bearer token.
            contextMessage.Headers.Add("X-User-Authorization", "Bearer " + authorization.Parameter);
            ForwardCorrelation(request, contextMessage);
            using var contextResponse = await identity.SendAsync(contextMessage, ct);
            if (contextResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Unauthorized();
            }
            if (contextResponse.StatusCode == HttpStatusCode.NotFound)
            {
                return Failure(409, "ERR_PROFILE_NOT_READY", "Tài khoản chưa hoàn tất bootstrap hồ sơ.", true);
            }
            if (contextResponse.StatusCode != HttpStatusCode.OK)
            {
                // A 403 here rejects this service's scope/audience, not the user's account status.
                return Unavailable();
            }
            var resolved = await contextResponse.Content.ReadFromJsonAsync<IdentityContext>(cancellationToken: ct);
            if (resolved is null || resolved.AccountId == Guid.Empty
                || string.IsNullOrWhiteSpace(resolved.AccountStatus))
            {
                return Unavailable();
            }
            if (!string.Equals(resolved.AccountStatus, "ACTIVE", StringComparison.Ordinal))
            {
                return Failure(403, "ERR_ACCOUNT_DISABLED", "Tài khoản không ở trạng thái hoạt động.", false);
            }
            return new(true, resolved.AccountId, 200, string.Empty, string.Empty, false, resolved.Roles ?? Array.Empty<string>(), resolved.Email, resolved.EmailVerified);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException or JsonException or OperationCanceledException)
        {
            return Unavailable();
        }
    }

    private static void ForwardCorrelation(HttpRequest request, HttpRequestMessage message)
    {
        if (request.HttpContext.Items["CorrelationId"] is string correlationId
            && correlationId.Length is > 0 and <= 64
            && correlationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            message.Headers.Add("X-Correlation-Id", correlationId);
        }
    }

    private static UserIdentityResolution Failure(int status, string code, string message, bool retryable) =>
        new(false, Guid.Empty, status, code, message, retryable, Array.Empty<string>());

    private static UserIdentityResolution Unauthorized() =>
        Failure(401, "ERR_UNAUTHORIZED", "Cần Firebase ID token hợp lệ để truy cập hồ sơ.", false);

    private static UserIdentityResolution Unavailable() =>
        Failure(503, "ERR_IDENTITY_DEPENDENCY_UNAVAILABLE", "Chưa thể xác minh danh tính qua Identity.", true);

    private sealed record IssuedServiceToken(string? AccessToken, string? TokenType);
    private sealed record IdentityContext(Guid AccountId, string? AccountStatus, IReadOnlyList<string>? Roles, string? Email, bool EmailVerified);
}
