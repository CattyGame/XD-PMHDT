using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace AURA.Billing.Api.Auth;

public sealed record ServiceTokenValidationResult(
    bool Succeeded,
    ClaimsPrincipal? Principal,
    int StatusCode,
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyCollection<string>? Scopes = null)
{
    public bool HasScope(string scope) =>
        Scopes is not null && Scopes.Contains(scope, StringComparer.Ordinal);
}

/// <summary>
/// Validates service-to-service JWTs. The trusted public key is read from
/// configuration or fetched from Identity over the private Compose network.
/// No token or credential is logged.
/// </summary>
public sealed class ServiceTokenValidator(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<ServiceTokenValidator> logger)
{
    private readonly string _issuer =
        configuration["ServiceAuth:Issuer"] ?? "aura-identity-local";
    private readonly string _audience =
        configuration["ServiceAuth:Audience"] ?? "aura-billing";
    private readonly string? _publicKeyPath = configuration["ServiceAuth:PublicKeyPath"];
    private readonly string? _publicKeyPem = configuration["ServiceAuth:PublicKeyPem"];
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private SecurityKey? _cachedKey;

    public async Task<ServiceTokenValidationResult> ValidateAsync(
        HttpRequest request,
        string expectedScope,
        string? expectedClientId,
        CancellationToken cancellationToken)
    {
        var (error, principal, scopes) = await ValidateCoreAsync(request, expectedClientId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!scopes.Contains(expectedScope, StringComparer.Ordinal))
        {
            return Failure(403, "ERR_FORBIDDEN", $"Service token thiếu scope bắt buộc: {expectedScope}.");
        }

        return new ServiceTokenValidationResult(true, principal, 200, string.Empty, string.Empty, false, scopes);
    }

    public async Task<ServiceTokenValidationResult> ValidateAnyScopeAsync(
        HttpRequest request,
        IReadOnlyCollection<string> expectedScopes,
        string? expectedClientId,
        CancellationToken cancellationToken)
    {
        var (error, principal, scopes) = await ValidateCoreAsync(request, expectedClientId, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!expectedScopes.Any(s => scopes.Contains(s, StringComparer.Ordinal)))
        {
            return Failure(403, "ERR_FORBIDDEN", $"Service token thiếu ít nhất một trong các scope bắt buộc: {string.Join(", ", expectedScopes)}.");
        }

        return new ServiceTokenValidationResult(true, principal, 200, string.Empty, string.Empty, false, scopes);
    }

    private async Task<(ServiceTokenValidationResult? Error, ClaimsPrincipal? Principal, string[] Scopes)> ValidateCoreAsync(
        HttpRequest request,
        string? expectedClientId,
        CancellationToken cancellationToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (Failure(401, "ERR_UNAUTHORIZED", "Thiếu service token."), null, Array.Empty<string>());
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return (Failure(401, "ERR_UNAUTHORIZED", "Thiếu service token."), null, Array.Empty<string>());
        }

        SecurityKey? key;
        try
        {
            key = await GetPublicKeyAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to load the trusted Identity public key.");
            return (Failure(503, "ERR_SERVICE_AUTH_UNAVAILABLE",
                "Chưa thể xác thực service token vì khóa công khai chưa sẵn sàng.", retryable: true), null, Array.Empty<string>());
        }

        if (key is null)
        {
            return (Failure(503, "ERR_SERVICE_AUTH_UNAVAILABLE",
                "Chưa thể xác thực service token vì khóa công khai chưa sẵn sàng.", retryable: true), null, Array.Empty<string>());
        }

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        if (!handler.CanReadToken(token))
        {
            return (Failure(401, "ERR_UNAUTHORIZED", "Service token không đúng định dạng JWT."), null, Array.Empty<string>());
        }

        ClaimsPrincipal principal;
        try
        {
            principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            }, out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt
                || !string.Equals(jwt.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal))
            {
                return (Failure(401, "ERR_UNAUTHORIZED", "Service token phải sử dụng RS256."), null, Array.Empty<string>());
            }
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return (Failure(403, "ERR_FORBIDDEN", "Audience của service token không hợp lệ."), null, Array.Empty<string>());
        }
        catch (SecurityTokenException)
        {
            return (Failure(401, "ERR_UNAUTHORIZED", "Service token không hợp lệ hoặc đã hết hạn."), null, Array.Empty<string>());
        }
        catch (ArgumentException)
        {
            return (Failure(401, "ERR_UNAUTHORIZED", "Service token không hợp lệ."), null, Array.Empty<string>());
        }

        var clientId = principal.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (Failure(401, "ERR_UNAUTHORIZED", "Service token thiếu client_id."), null, Array.Empty<string>());
        }

        if (!string.IsNullOrWhiteSpace(expectedClientId)
            && !string.Equals(clientId, expectedClientId, StringComparison.Ordinal))
        {
            return (Failure(403, "ERR_FORBIDDEN", "Caller không được phép gọi endpoint này."), null, Array.Empty<string>());
        }

        var scopeClaim = principal.FindFirst("scope")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value;
        var scopes = (scopeClaim ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return (null, principal, scopes);
    }

    private async Task<SecurityKey?> GetPublicKeyAsync(CancellationToken cancellationToken)
    {
        if (_cachedKey is not null)
        {
            return _cachedKey;
        }

        await _keyLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            if (!string.IsNullOrWhiteSpace(_publicKeyPem))
            {
                _cachedKey = CreateKey(_publicKeyPem);
                return _cachedKey;
            }

            if (!string.IsNullOrWhiteSpace(_publicKeyPath))
            {
                if (!File.Exists(_publicKeyPath))
                {
                    throw new FileNotFoundException("Configured service public key was not found.");
                }

                _cachedKey = CreateKey(await File.ReadAllTextAsync(_publicKeyPath, cancellationToken));
                return _cachedKey;
            }

            var client = httpClientFactory.CreateClient("IdentityApi");
            using var response = await client.GetAsync(
                "/internal/v1/identity/public-key", cancellationToken);
            response.EnsureSuccessStatusCode();
            var pem = await response.Content.ReadAsStringAsync(cancellationToken);
            _cachedKey = CreateKey(pem);
            return _cachedKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static SecurityKey CreateKey(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa);
    }

    private static ServiceTokenValidationResult Failure(
        int statusCode,
        string code,
        string message,
        bool retryable = false) =>
        new(false, null, statusCode, code, message, retryable);
}
