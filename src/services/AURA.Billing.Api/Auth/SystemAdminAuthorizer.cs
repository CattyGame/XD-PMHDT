using System.Net;
using System.Text.Json;

namespace AURA.Billing.Api.Auth;

public sealed record AdminAuthorizationResult(
    bool Allowed,
    int StatusCode,
    string Code,
    string Message,
    bool Retryable = false,
    Guid AccountId = default);

/// <summary>
/// Delegates Firebase ID-token validation and current account-role lookup to Identity.
/// Billing never infers SYSTEM_ADMIN from request-body fields or headers.
/// </summary>
public sealed class SystemAdminAuthorizer(
    IHttpClientFactory httpClientFactory,
    ILogger<SystemAdminAuthorizer> logger)
{
    public async Task<AdminAuthorizationResult> AuthorizeAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Denied(401, "ERR_UNAUTHORIZED", "Cần Firebase ID token hợp lệ.");
        }

        try
        {
            var client = httpClientFactory.CreateClient("IdentityApi");
            using var message = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
            message.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Denied(401, "ERR_UNAUTHORIZED", "Firebase ID token không hợp lệ hoặc đã hết hạn.");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden
                || response.StatusCode == HttpStatusCode.Conflict
                || response.StatusCode == HttpStatusCode.NotFound)
            {
                return Denied(403, "ERR_FORBIDDEN", "Tài khoản không được phép thay đổi package.");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Identity role check returned HTTP {StatusCode}.", (int)response.StatusCode);
                return Denied(503, "ERR_AUTH_DEPENDENCY_UNAVAILABLE",
                    "Không thể xác minh quyền quản trị lúc này.", retryable: true);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var identity = await JsonSerializer.DeserializeAsync<IdentityMeResponse>(
                stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);

            if (identity is null || !string.Equals(identity.AccountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return Denied(403, "ERR_ACCOUNT_DISABLED", "Tài khoản không hoạt động hoặc chưa được khởi tạo.");
            }

            if (identity.Roles?.Contains("SYSTEM_ADMIN", StringComparer.Ordinal) != true)
            {
                return Denied(403, "ERR_FORBIDDEN", "Yêu cầu quyền SYSTEM_ADMIN.");
            }

            if (identity.AccountId == Guid.Empty)
                return Denied(503, "ERR_AUTH_DEPENDENCY_UNAVAILABLE", "Không thể xác minh tài khoản quản trị.", true);
            return new AdminAuthorizationResult(true, 200, string.Empty, string.Empty, AccountId: identity.AccountId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or JsonException
            or OperationCanceledException)
        {
            logger.LogWarning(exception, "Identity role check is unavailable.");
            return Denied(503, "ERR_AUTH_DEPENDENCY_UNAVAILABLE",
                "Không thể xác minh quyền quản trị lúc này.", retryable: true);
        }
    }

    private static AdminAuthorizationResult Denied(
        int statusCode, string code, string message, bool retryable = false) =>
        new(false, statusCode, code, message, retryable);

    private sealed record IdentityMeResponse(string? AccountStatus, IReadOnlyList<string>? Roles, Guid AccountId);
}
