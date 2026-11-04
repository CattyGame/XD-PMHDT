using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aura.Worker.Analysis.Configuration;

namespace Aura.Worker.Analysis.Services;

public sealed record WorkerAccessContext(Guid TenantId, Guid ActorAccountId, Guid PatientId, Guid? ClinicContextId, Guid AnalysisId);

public interface IProfileResolveClient
{
    Task<ProfileResolveResult> RecheckAccessAsync(WorkerAccessContext context, string action, CancellationToken cancellationToken);
}

public class ProfileResolveResult
{
    public bool Allowed { get; set; }
    public string? Reason { get; set; }
    public bool IsTransientError { get; set; }
}

public class ProfileResolveClient : IProfileResolveClient
{
    private readonly HttpClient _httpClient;
    private readonly ProfileClientOptions _options;
    private readonly IServiceTokenProvider _tokenProvider;
    private readonly ILogger<ProfileResolveClient> _logger;

    public ProfileResolveClient(
        HttpClient httpClient,
        IOptions<ProfileClientOptions> options,
        IServiceTokenProvider tokenProvider,
        ILogger<ProfileResolveClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<ProfileResolveResult> RecheckAccessAsync(WorkerAccessContext context, string action, CancellationToken cancellationToken)
    {
        try
        {
            var token = await _tokenProvider.GetTokenAsync(_options.Audience, _options.Scope, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/internal/v1/profile/access/resolve");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var body = new
            {
                actorAccountId = context.ActorAccountId,
                patientId = context.PatientId,
                clinicContextId = context.ClinicContextId,
                analysisId = context.AnalysisId,
                action = action
            };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(content);
                var isAllowed = doc.RootElement.TryGetProperty("allowed", out var allowedProp) && allowedProp.GetBoolean()
                    && doc.RootElement.TryGetProperty("tenantId", out var tenantProp)
                    && tenantProp.TryGetGuid(out var resolvedTenant) && resolvedTenant == context.TenantId;
                var reason = doc.RootElement.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
                return new ProfileResolveResult
                {
                    Allowed = isAllowed,
                    IsTransientError = false,
                    Reason = reason ?? (isAllowed ? null : "Access denied by Profile policy")
                };
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            {
                return new ProfileResolveResult
                {
                    Allowed = false,
                    IsTransientError = true,
                    Reason = $"Profile service unavailable (HTTP {(int)response.StatusCode})"
                };
            }

            return new ProfileResolveResult
            {
                Allowed = false,
                IsTransientError = false,
                Reason = $"Access revoked or unauthorized (HTTP {(int)response.StatusCode})"
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Transient network error contacting Profile service");
            return new ProfileResolveResult { Allowed = false, IsTransientError = true, Reason = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error rechecking Profile access");
            return new ProfileResolveResult { Allowed = false, IsTransientError = false, Reason = ex.Message };
        }
    }
}
