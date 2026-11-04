using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AURA.Profile.Api;

public sealed record AccountAuthoritySnapshot(Guid AccountId, string AccountStatus, IReadOnlyList<string> Roles);
public interface IAccountAuthority
{
    Task<AccountAuthoritySnapshot?> GetAsync(Guid accountId, CancellationToken ct);
}

/// <summary>Identity owns account status and global roles. No Profile cross-schema reads or role cache.</summary>
public sealed class AccountAuthority(IHttpClientFactory clients, IConfiguration configuration) : IAccountAuthority
{
    public async Task<AccountAuthoritySnapshot?> GetAsync(Guid accountId, CancellationToken ct)
    {
        var client = clients.CreateClient("IdentityClient");
        using var issued = await client.PostAsJsonAsync("/internal/v1/identity/service-tokens", new
        {
            clientId = configuration["ServiceAuth:ClientId"], clientSecret = configuration["ServiceAuth:ClientSecret"],
            audience = "aura-identity", scope = "identity:context"
        }, ct);
        if (!issued.IsSuccessStatusCode) throw new HttpRequestException("Identity authority unavailable.");
        var token = await issued.Content.ReadFromJsonAsync<Token>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(token?.AccessToken)) throw new HttpRequestException("Invalid Identity service token.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/identity/accounts/{accountId:D}/context");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AccountAuthoritySnapshot>(cancellationToken: ct);
        if (result?.AccountId != accountId || result.Roles is null) throw new HttpRequestException("Invalid Identity context.");
        return result;
    }
    private sealed record Token(string? AccessToken);
}
