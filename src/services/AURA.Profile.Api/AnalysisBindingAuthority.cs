using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace AURA.Profile.Api;

public sealed record AnalysisBinding(Guid AnalysisId, Guid TenantId, Guid PatientId);
public interface IAnalysisBindingAuthority
{
    Task<AnalysisBinding?> GetAsync(Guid analysisId, CancellationToken ct);
}

/// <summary>Analysis owns image-to-patient binding; Profile never queries the Analysis schema.</summary>
public sealed class AnalysisBindingAuthority(IHttpClientFactory clients, IConfiguration configuration) : IAnalysisBindingAuthority
{
    public async Task<AnalysisBinding?> GetAsync(Guid analysisId, CancellationToken ct)
    {
        using var issued = await clients.CreateClient("IdentityClient").PostAsJsonAsync("/internal/v1/identity/service-tokens", new
        {
            clientId = configuration["ServiceAuth:ClientId"], clientSecret = configuration["ServiceAuth:ClientSecret"],
            audience = "aura-analysis", scope = "analysis:binding"
        }, ct);
        if (!issued.IsSuccessStatusCode) throw new HttpRequestException("Analysis binding service identity unavailable.");
        var token = await issued.Content.ReadFromJsonAsync<Token>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(token?.AccessToken)) throw new HttpRequestException("Invalid service token.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/analysis/bindings/{analysisId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await clients.CreateClient("AnalysisClient").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var binding = await response.Content.ReadFromJsonAsync<AnalysisBinding>(cancellationToken: ct);
        if (binding?.AnalysisId != analysisId || binding.TenantId == Guid.Empty || binding.PatientId == Guid.Empty)
            throw new HttpRequestException("Invalid authoritative Analysis binding.");
        return binding;
    }
    private sealed record Token(string? AccessToken);
}
