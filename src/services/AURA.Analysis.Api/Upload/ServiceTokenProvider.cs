using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AURA.Analysis.Api.Upload;

public interface IServiceTokenProvider
{
    Task<string> GetAsync(string audience, string scope, CancellationToken ct, string? correlationId = null);
}

/// <summary>Uses Analysis's own credentials. Never substitutes a user token or a Development secret.</summary>
public sealed class ServiceTokenProvider(IHttpClientFactory clients, IConfiguration configuration) : IServiceTokenProvider
{
    public async Task<string> GetAsync(string audience, string scope, CancellationToken ct, string? correlationId = null)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = (audience, scope) switch
        {
            ("aura-identity", "identity:context") => true,
            ("aura-profile", "profile:resolve") => true,
            ("aura-billing", "billing:reserve" or "billing:read" or "billing:release" or "billing:refund") => true,
            _ => false
        };
        var clientId = configuration["ServiceAuth:ClientId"];
        var secret = configuration["ServiceAuth:ClientSecret"];
        if (!allowed || clientId != "analysis-api" || string.IsNullOrWhiteSpace(secret))
            throw UploadFailure.Dependency("Identity");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/identity/service-tokens")
            {
                Content = JsonContent.Create(new { clientId, clientSecret = secret, audience, scope })
            };
            ServiceHttp.AddCorrelation(message, correlationId);
            using var response = await clients.CreateClient("AnalysisIdentity").SendAsync(message, ct);
            if (response.StatusCode != HttpStatusCode.OK) throw UploadFailure.Dependency("Identity");
            var reply = await response.Content.ReadFromJsonAsync<TokenReply>(cancellationToken: ct);
            if (reply is null || !ServiceHttp.ValidBearer(reply.AccessToken)
                || !string.Equals(reply.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase)
                || reply.ExpiresIn is <= 0 or > 300)
                throw UploadFailure.Dependency("Identity");
            return reply.AccessToken!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
            or OperationCanceledException or InvalidOperationException or NotSupportedException)
        {
            throw UploadFailure.Dependency("Identity");
        }
    }

    private sealed record TokenReply(string? AccessToken, string? TokenType, int ExpiresIn);
}

internal static class ServiceHttp
{
    public static bool ValidBearer(string? token) => !string.IsNullOrWhiteSpace(token)
        && token.Length <= 16384 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static void AddCorrelation(HttpRequestMessage message, string? correlationId)
    {
        if (correlationId is { Length: > 0 and <= 64 }
            && correlationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            message.Headers.Add("X-Correlation-Id", correlationId);
    }

    public static void SetBearer(HttpRequestMessage message, string token)
    {
        if (!ValidBearer(token)) throw UploadFailure.Dependency("Identity");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
