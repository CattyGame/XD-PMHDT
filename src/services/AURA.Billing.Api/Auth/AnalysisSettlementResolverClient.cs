using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AURA.Billing.Api.Auth;

/// <summary>
/// Gọi Analysis để xác minh quyết định settlement từ event đã được lưu bền vững.
/// Không tự suy ra reservationId hoặc generation từ analysisId.
/// </summary>
public sealed class AnalysisSettlementResolverClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AnalysisSettlementResolverClient> logger)
{
    private const string ResolverPath =
        "/internal/v1/analysis/settlements/resolve";

    private const string Audience = "aura-analysis";
    private const string Scope = "analysis:reconcile";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedActions =
        new(StringComparer.Ordinal)
        {
            "CAPTURE", "RELEASE", "NONE"
        };

    private static readonly HashSet<string> AllowedReasons =
        new(StringComparer.Ordinal)
        {
            "VALID_RESULT",
            "FINAL_FAILURE",
            "LOW_QUALITY",
            "STALE_EVENT",
            "NOT_TERMINAL",
            "NO_RESERVATION",
            "RECONCILE_REQUIRED",
            "VALIDATION_ABORT",
            "STORAGE_ABORT",
            "ANALYSIS_ABORT",
        };

    private static readonly HashSet<string> AllowedStatuses =
        new(StringComparer.Ordinal)
        {
            "PENDING",
            "QUEUED",
            "PROCESSING",
            "RETRY_READY",
            "COMPLETED",
            "FAILED",
            "LOW_QUALITY",
            "REJECTED"
        };

    public async Task<SettlementResolutionResponse> ResolveAsync(
        Guid eventId,
        Guid analysisId,
        Guid tenantId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty
            || analysisId == Guid.Empty
            || tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "eventId, analysisId và tenantId phải là UUID khác rỗng.");
        }

        try
        {
            var accessToken = await RequestServiceTokenAsync(
                correlationId, cancellationToken);

            using var message = new HttpRequestMessage(
                HttpMethod.Post, ResolverPath)
            {
                Content = JsonContent.Create(
                    new AnalysisSettlementResolveRequest(
                        eventId, analysisId, tenantId),
                    options: JsonOptions)
            };

            message.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            AddCorrelation(message, correlationId);

            var analysisHttp = httpClientFactory.CreateClient("AnalysisApi");

            using var response = await analysisHttp.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure(
                    "ANALYSIS_RESOLVER",
                    response.StatusCode);
            }

            SettlementResolutionResponse? resolution;

            try
            {
                resolution = await response.Content
                    .ReadFromJsonAsync<SettlementResolutionResponse>(
                        JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                throw Failure(
                    "ERR_ANALYSIS_RESOLVER_INVALID_RESPONSE",
                    retryable: false);
            }

            if (resolution is null)
            {
                throw Failure(
                    "ERR_ANALYSIS_RESOLVER_EMPTY_RESPONSE",
                    retryable: false);
            }

            ValidateResolution(
                resolution, eventId, analysisId, tenantId);

            return resolution;
        }
        catch (SettlementResolverCallException exception)
        {
            logger.LogWarning(
                "Billing settlement dependency failed. Code={ErrorCode}; Retryable={Retryable}; HttpStatus={StatusCode}.",
                exception.Code,
                exception.Retryable,
                (int?)exception.StatusCode);

            throw;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeout, không phải cancellation do host dừng.
            throw Failure(
                "ERR_ANALYSIS_RESOLVER_TIMEOUT",
                retryable: true);
        }
        catch (HttpRequestException)
        {
            throw Failure(
                "ERR_ANALYSIS_RESOLVER_UNAVAILABLE",
                retryable: true);
        }
        catch (IOException)
        {
            throw Failure(
                "ERR_ANALYSIS_RESOLVER_IO",
                retryable: true);
        }
    }

    private async Task<string> RequestServiceTokenAsync(
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var clientId = configuration["ServiceAuth:ClientId"] ?? string.Empty;
        var clientSecret = configuration["ServiceAuth:ClientSecret"];

        if (!string.Equals(
                clientId, "billing-api", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw Failure(
                "ERR_BILLING_SERVICE_IDENTITY_NOT_CONFIGURED",
                retryable: false);
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/v1/identity/service-tokens")
        {
            Content = JsonContent.Create(
                new ServiceTokenRequest(
                    clientId, clientSecret, Audience, Scope),
                options: JsonOptions)
        };

        AddCorrelation(message, correlationId);

        var identityHttp = httpClientFactory.CreateClient("IdentityApi");

        using var response = await identityHttp.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpFailure(
                "IDENTITY_SERVICE_TOKEN",
                response.StatusCode);
        }

        ServiceTokenResponse? token;

        try
        {
            token = await response.Content
                .ReadFromJsonAsync<ServiceTokenResponse>(
                    JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            throw Failure(
                "ERR_IDENTITY_INVALID_TOKEN_RESPONSE",
                retryable: false);
        }

        if (token is null
            || !string.Equals(
                token.TokenType, "Bearer",
                StringComparison.OrdinalIgnoreCase)
            || token.ExpiresIn is <= 0 or > 300
            || !IsValidBearer(token.AccessToken))
        {
            throw Failure(
                "ERR_IDENTITY_INVALID_TOKEN_RESPONSE",
                retryable: false);
        }

        return token.AccessToken!;
    }

    internal static void ValidateResolution(
        SettlementResolutionResponse result,
        Guid expectedEventId,
        Guid expectedAnalysisId,
        Guid expectedTenantId)
    {
        if (result.EventId != expectedEventId
            || result.AnalysisId != expectedAnalysisId
            || result.TenantId != expectedTenantId)
        {
            throw Failure(
                "ERR_ANALYSIS_RESOLVER_ID_MISMATCH",
                retryable: false);
        }

        if (!AllowedActions.Contains(result.SettlementAction)
            || !AllowedReasons.Contains(result.Reason)
            || !AllowedStatuses.Contains(result.ProcessingStatus))
        {
            throw Failure(
                "ERR_ANALYSIS_RESOLVER_INVALID_DECISION",
                retryable: false);
        }

        if (!result.Applicable)
        {
            if (result.SettlementAction != "NONE")
            {
                throw Failure(
                    "ERR_ANALYSIS_RESOLVER_INVALID_DECISION",
                    retryable: false);
            }

            return;
        }

        if (result.SettlementAction == "CAPTURE")
        {
            var valid = result.Reason == "VALID_RESULT"
                && result.ProcessingStatus == "COMPLETED"
                && result.ReservationId is { } reservationId
                && reservationId != Guid.Empty
                && result.Generation is > 0
                && result.ResultVersion is > 0
                && result.IsMock == false;

            if (!valid)
            {
                throw Failure(
                    "ERR_ANALYSIS_RESOLVER_INVALID_CAPTURE",
                    retryable: false);
            }
        }
        else if (result.SettlementAction == "RELEASE")
        {
            var validReason = result.Reason is
                "VALIDATION_ABORT" or "STORAGE_ABORT" or "ANALYSIS_ABORT" or "FINAL_FAILURE" or "LOW_QUALITY";

            var validStatus = result.ProcessingStatus is
                "FAILED" or "LOW_QUALITY" or "REJECTED";

            var valid = validReason
                && validStatus
                && result.ReservationId is { } reservationId
                && reservationId != Guid.Empty
                && result.Generation is > 0
                && result.ResultVersion is null
                && result.IsMock is false or null;

            if (!valid)
            {
                throw Failure(
                    "ERR_ANALYSIS_RESOLVER_INVALID_RELEASE",
                    retryable: false);
            }
        }
    }

    private static SettlementResolverCallException CreateHttpFailure(
        string dependency,
        HttpStatusCode statusCode)
    {
        var retryable = statusCode == HttpStatusCode.RequestTimeout
            || (int)statusCode == 429
            || (int)statusCode >= 500;

        return Failure(
            $"ERR_{dependency}_HTTP_{(int)statusCode}",
            retryable,
            statusCode);
    }

    private static SettlementResolverCallException Failure(
        string code,
        bool retryable,
        HttpStatusCode? statusCode = null) =>
        new(code, retryable, statusCode);

    private static bool IsValidBearer(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Length <= 16384
        && token.All(c =>
            char.IsAsciiLetterOrDigit(c)
            || c is '.' or '_' or '-');

    private static void AddCorrelation(
        HttpRequestMessage message,
        string? correlationId)
    {
        if (correlationId is { Length: > 0 and <= 64 }
            && correlationId.All(c =>
                char.IsAsciiLetterOrDigit(c)
                || c is '.' or '_' or '-'))
        {
            message.Headers.Add(
                "X-Correlation-Id", correlationId);
        }
    }

    private sealed record ServiceTokenRequest(
        string ClientId,
        string ClientSecret,
        string Audience,
        string Scope);

    private sealed record ServiceTokenResponse(
        string? AccessToken,
        string? TokenType,
        int ExpiresIn);
}

public sealed record AnalysisSettlementResolveRequest(
    Guid EventId,
    Guid AnalysisId,
    Guid TenantId);

public sealed record SettlementResolutionResponse(
    Guid EventId,
    Guid AnalysisId,
    Guid TenantId,
    bool Applicable,
    string SettlementAction,
    string Reason,
    Guid? ReservationId,
    int? Generation,
    string ProcessingStatus,
    int? ResultVersion,
    bool? IsMock);

public sealed class SettlementResolverCallException(
    string code,
    bool retryable,
    HttpStatusCode? statusCode = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}