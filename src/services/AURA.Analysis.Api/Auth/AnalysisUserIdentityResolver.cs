using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AURA.Analysis.Api.Upload;
using Microsoft.AspNetCore.Http;

namespace AURA.Analysis.Api.Auth;

/// <summary>Server-side identity only. Never serialize this as an Analysis API response.</summary>
public sealed record AnalysisUserContext(Guid AccountId, IReadOnlyList<string> Roles);

public interface IAnalysisUserIdentityResolver
{
    Task<AnalysisUserContext> ResolveAsync(HttpRequest request, CancellationToken ct);
}

/// <summary>Identity verifies the user token and resolves Firebase UID to account/roles.
/// This client neither decodes unverified JWT claims nor trusts client-supplied identity headers.</summary>
public sealed class AnalysisUserIdentityResolver(IHttpClientFactory clients, IServiceTokenProvider tokens)
    : IAnalysisUserIdentityResolver
{
    private const int MaxContextBytes = 32_768;

    public async Task<AnalysisUserContext> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var values = request.Headers.Authorization;
        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var bearer)
            || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || !ValidUserBearer(bearer.Parameter)) throw Unauthorized();
        var correlation = request.HttpContext.Items["CorrelationId"] as string;
        if (correlation is not { Length: > 0 and <= 64 }
            || correlation.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            correlation = null;
        try
        {
            var client = clients.CreateClient("AnalysisIdentity");
            // Bound the entire operation, including service issuance and streamed context JSON.
            if (client.Timeout <= TimeSpan.Zero || client.Timeout > TimeSpan.FromSeconds(30))
                throw UploadFailure.Dependency("Identity");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(client.Timeout);
            var serviceToken = await tokens.GetAsync("aura-identity", "identity:context", deadline.Token, correlation);
            using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/identity/context");
            ServiceHttp.SetBearer(message, serviceToken);
            message.Headers.Add("X-User-Authorization", "Bearer " + bearer!.Parameter);
            ServiceHttp.AddCorrelation(message, correlation);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw Unauthorized();
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UploadFailure(409, "ERR_ACCOUNT_NOT_INITIALIZED", "Tài khoản chưa hoàn tất bootstrap.", true);
            // A context 403 rejects this service's audience/scope. It is not the user's role denial.
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > MaxContextBytes)
                throw UploadFailure.Dependency("Identity");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[MaxContextBytes + 1];
            int length = 0;
            while (length < buffer.Length)
            {
                var read = await body.ReadAsync(buffer.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxContextBytes) throw UploadFailure.Dependency("Identity");
            var resolved = JsonSerializer.Deserialize<IdentityContextReply>(buffer.AsSpan(0, length), UploadJson.Options);
            deadline.Token.ThrowIfCancellationRequested();
            if (resolved is null || resolved.AccountId == Guid.Empty || string.IsNullOrWhiteSpace(resolved.AccountStatus)
                || resolved.Roles is null || resolved.Roles.Length > 32
                || resolved.Roles.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 64 || r.Any(char.IsControl)))
                throw UploadFailure.Dependency("Identity");
            if (resolved.AccountStatus != "ACTIVE")
                throw new UploadFailure(403, "ERR_ACCOUNT_DISABLED", "Tài khoản không ở trạng thái hoạt động.");
            return new(resolved.AccountId, Array.AsReadOnly(resolved.Roles.Distinct(StringComparer.Ordinal).ToArray()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UploadFailure) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException
            or InvalidOperationException or NotSupportedException)
        {
            throw UploadFailure.Dependency("Identity");
        }
    }

    private static bool ValidUserBearer(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 16_384 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static UploadFailure Unauthorized() => new(401, "ERR_UNAUTHORIZED", "Cần token người dùng hợp lệ.");
    private sealed record IdentityContextReply(Guid AccountId, string? AccountStatus, string[]? Roles);
}

/// <summary>Functional gate for create/list/get Analysis, batch and file access.
/// Resource ownership/assignment/tenant permission still requires Profile on each operation.
/// This is not the separate DOCTOR review policy or a service/NiFi ingest policy.</summary>
public static class AnalysisUserPolicy
{
    public static void RequireDoctor(AnalysisUserContext context)
    {
        if (context.AccountId == Guid.Empty || context.Roles is null)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
        if (!context.Roles.Contains("DOCTOR", StringComparer.Ordinal))
            throw new UploadFailure(403, "ERR_FORBIDDEN", "Chức năng này chỉ dành cho bác sĩ.");
    }

    public static void RequireClinicalRole(AnalysisUserContext context)
    {
        if (context.AccountId == Guid.Empty || context.Roles is null)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
        if (context.Roles.Contains("SYSTEM_ADMIN", StringComparer.Ordinal)
            || !context.Roles.Any(r => r is "PATIENT" or "DOCTOR" or "CLINIC_ADMIN"))
            throw new UploadFailure(403, "ERR_FORBIDDEN", "Vai trò không được phép dùng chức năng này.");
    }

    public static void RequireSystemAdmin(AnalysisUserContext context)
    {
        if (context.AccountId == Guid.Empty || context.Roles is null)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần xác thực tài khoản.");
        if (!context.Roles.Contains("SYSTEM_ADMIN", StringComparer.Ordinal))
            throw new UploadFailure(403, "ERR_FORBIDDEN", "Chỉ SYSTEM_ADMIN được phép quản lý cấu hình AI.");
    }
}
