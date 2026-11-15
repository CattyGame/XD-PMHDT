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
    private readonly SemaphoreSlim refresh = new(1, 1);
    private string? serviceToken;
    private string? credential;
    private DateTimeOffset refreshAt;

    public async Task<AccountAuthoritySnapshot?> GetAsync(Guid accountId, CancellationToken ct)
    {
        var client = clients.CreateClient("IdentityClient");
        var token = await GetServiceTokenAsync(client, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/identity/accounts/{accountId:D}/context");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AccountAuthoritySnapshot>(cancellationToken: ct);
        if (result?.AccountId != accountId || result.Roles is null) throw new HttpRequestException("Invalid Identity context.");
        return result;
    }

    private async Task<string> GetServiceTokenAsync(HttpClient client, CancellationToken ct)
    {
        var secret = configuration["ServiceAuth:ClientSecret"];
        if (configuration["ServiceAuth:ClientId"] != "profile-api" || string.IsNullOrWhiteSpace(secret))
            throw new HttpRequestException("Profile service identity is not configured.");
        await refresh.WaitAsync(ct);
        try
        {
            if (serviceToken is not null && credential == secret && refreshAt > DateTimeOffset.UtcNow) return serviceToken;
            using var issued = await client.PostAsJsonAsync("/internal/v1/identity/service-tokens", new
            {
                clientId = "profile-api", clientSecret = secret,
                audience = "aura-identity", scope = "identity:context"
            }, ct);
            if (!issued.IsSuccessStatusCode) throw new HttpRequestException("Identity authority unavailable.");
            var token = await issued.Content.ReadFromJsonAsync<Token>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(token?.AccessToken) || token.TokenType != "Bearer" || token.ExpiresIn is <= 0 or > 300)
                throw new HttpRequestException("Invalid Identity service token.");
            serviceToken = token.AccessToken;
            credential = secret;
            refreshAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 30);
            return serviceToken;
        }
        finally { refresh.Release(); }
    }
    private sealed record Token(string? AccessToken, string? TokenType, int ExpiresIn);
}
