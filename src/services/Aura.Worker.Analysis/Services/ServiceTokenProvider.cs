using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Aura.Worker.Analysis.Configuration;

namespace Aura.Worker.Analysis.Services;

public interface IServiceTokenProvider
{
    Task<string> GetTokenAsync(string audience,string scope,CancellationToken ct);
}

/// <summary>Only Identity issues trusted service tokens; missing credentials never generate a local signing key.</summary>
public sealed class ServiceTokenProvider(HttpClient client,IOptions<ServiceAuthOptions> options) : IServiceTokenProvider
{
    public async Task<string> GetTokenAsync(string audience,string scope,CancellationToken ct)
    {
        var o=options.Value;
        if(o.ClientId!="analysis-worker" || string.IsNullOrWhiteSpace(o.ClientSecret)
            || (audience,scope) is not (("aura-ai","ai:analyze") or ("aura-profile","profile:resolve")))
            throw new InvalidOperationException("Worker service identity credentials or requested scope are not configured.");
        using var response=await client.PostAsJsonAsync("/internal/v1/identity/service-tokens",
            new {clientId=o.ClientId,clientSecret=o.ClientSecret,audience,scope},ct);
        response.EnsureSuccessStatusCode();
        var token=await response.Content.ReadFromJsonAsync<Reply>(cancellationToken:ct);
        if(token is null || token.TokenType!="Bearer" || token.ExpiresIn is <=0 or >300
            || string.IsNullOrWhiteSpace(token.AccessToken) || token.AccessToken.Length>16384
            || token.AccessToken.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('.' or '_' or '-')))
            throw new InvalidDataException("Identity returned an invalid token response.");
        return token.AccessToken;
    }
    private sealed record Reply(string AccessToken,string TokenType,int ExpiresIn);
}
