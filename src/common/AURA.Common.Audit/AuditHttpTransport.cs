using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace AURA.Common.Audit;

public sealed record AuditReceipt(Guid EventId, string PayloadSha256);

public sealed class AuditHttpTransport(IHttpClientFactory clients, IConfiguration config, AuditSource source) : IAuditTransport
{
    public async Task SendAsync(AuditEvent e, CancellationToken ct)
    {
        if (e.Source != source) throw new ArgumentException("Audit owner mismatch.");
        var clientId = AuditStore.Schema(source) + "-api";
        var secret = config["ServiceAuth:ClientSecret"];
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Audit service credentials are required.");
        using var issued = await clients.CreateClient("AuditIdentity").PostAsJsonAsync("/internal/v1/identity/service-tokens",
            new { clientId, clientSecret = secret, audience = "aura-profile", scope = "profile:audit" }, ct);
        if (!issued.IsSuccessStatusCode) throw new HttpRequestException("Audit service issuance unavailable.");
        var token = await issued.Content.ReadFromJsonAsync<Token>(cancellationToken: ct);
        if (token is not { TokenType: "Bearer", ExpiresIn: > 0 and <= 300 } || string.IsNullOrWhiteSpace(token.AccessToken))
            throw new HttpRequestException("Invalid audit service credential response.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/audit-events")
        { Content = JsonContent.Create(e, options: AuditJson.Options) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await clients.CreateClient("AuditProfile").SendAsync(message, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Audit collector did not acknowledge the event.");
        var receipt = await response.Content.ReadFromJsonAsync<AuditReceipt>(AuditJson.Options, ct);
        if (receipt?.EventId != e.EventId || receipt.PayloadSha256 != AuditJson.Hash(e))
            throw new HttpRequestException("Audit receipt did not match the submitted event.");
    }
    private sealed record Token(string? AccessToken, string? TokenType, int ExpiresIn);
}
