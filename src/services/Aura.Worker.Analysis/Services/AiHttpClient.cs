using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aura.Worker.Analysis.Configuration;

namespace Aura.Worker.Analysis.Services;

public interface IAiHttpClient
{
    Task<AiCallResult> AnalyzeAsync(AiAnalyzeRequest request, CancellationToken cancellationToken);
}

public class AiCallResult
{
    public bool IsSuccess { get; set; }
    public AiAnalyzeResponse? Response { get; set; }
    public bool IsLowQuality { get; set; }
    public bool IsTransientError { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class AiHttpClient : IAiHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly AiClientOptions _options;
    private readonly IServiceTokenProvider _tokenProvider;
    private readonly ILogger<AiHttpClient> _logger;

    public AiHttpClient(
        HttpClient httpClient,
        IOptions<AiClientOptions> options,
        IServiceTokenProvider tokenProvider,
        ILogger<AiHttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<AiCallResult> AnalyzeAsync(AiAnalyzeRequest analyzeRequest, CancellationToken cancellationToken)
    {
        try
        {
            var token = _tokenProvider.GenerateToken(_options.Audience, _options.Scope);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/api/v1/analyze");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var jsonContent = JsonSerializer.Serialize(analyzeRequest);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            using var response = await _httpClient.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var aiResponse = JsonSerializer.Deserialize<AiAnalyzeResponse>(responseBody);
                if (aiResponse == null)
                {
                    return new AiCallResult
                    {
                        IsSuccess = false,
                        IsTransientError = false,
                        ErrorCode = "ERR_AI_OUTPUT_INVALID",
                        ErrorMessage = "Failed to deserialize AI response."
                    };
                }

                return new AiCallResult
                {
                    IsSuccess = true,
                    Response = aiResponse
                };
            }

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity) // 422 Low Quality Image
            {
                _logger.LogWarning("AI returned 422 UnprocessableEntity for request {RequestId}", analyzeRequest.RequestId);
                return new AiCallResult
                {
                    IsSuccess = false,
                    IsLowQuality = true,
                    IsTransientError = false,
                    ErrorCode = "ERR_IMAGE_LOW_QUALITY",
                    ErrorMessage = "Image quality is too low for reliable retina vessel segmentation."
                };
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.GatewayTimeout ||
                response.StatusCode == HttpStatusCode.RequestTimeout ||
                (int)response.StatusCode >= 500)
            {
                return new AiCallResult
                {
                    IsSuccess = false,
                    IsTransientError = true,
                    ErrorCode = "ERR_AI_FAILED",
                    ErrorMessage = $"AI service error HTTP {(int)response.StatusCode}: {responseBody}"
                };
            }

            // Client errors (400, 401, 403) are terminal non-transient
            return new AiCallResult
            {
                IsSuccess = false,
                IsTransientError = false,
                ErrorCode = response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden
                    ? "ERR_UNAUTHORIZED"
                    : "ERR_VALIDATION",
                ErrorMessage = $"AI rejected request with HTTP {(int)response.StatusCode}: {responseBody}"
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AI call timed out after {Timeout}s", _options.TimeoutSeconds);
            return new AiCallResult
            {
                IsSuccess = false,
                IsTransientError = true,
                ErrorCode = "ERR_TIMEOUT",
                ErrorMessage = $"AI processing timed out after {_options.TimeoutSeconds}s"
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error calling AI service");
            return new AiCallResult
            {
                IsSuccess = false,
                IsTransientError = true,
                ErrorCode = "ERR_NETWORK",
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling AI service");
            return new AiCallResult
            {
                IsSuccess = false,
                IsTransientError = false,
                ErrorCode = "ERR_AI_FAILED",
                ErrorMessage = ex.Message
            };
        }
    }
}
