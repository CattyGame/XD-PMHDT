using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AURA.Common.Audit;
using Microsoft.Extensions.Logging;

namespace AURA.Analysis.Api.Upload;

/// <summary>Public admin view matching analysis-openapi-0.9.yaml AiConfigView.</summary>
public sealed record AiConfigView(
    Guid ArtifactManifestId,
    string ThresholdConfigSha256,
    int Version,
    DateTimeOffset ActivatedAt);

/// <summary>Admin change request matching analysis-openapi-0.9.yaml AiConfigChange.</summary>
public sealed record AiConfigChange(
    int ExpectedVersion,
    Guid ArtifactManifestId,
    string ThresholdConfigSha256,
    string Reason);

public interface IAiRuntimeConfigClient
{
    Task<AiConfigView> GetActiveAsync(string? correlationId, CancellationToken ct);
    Task<AiConfigView> ActivateAsync(AiConfigChange change, string? idempotencyKey, string? correlationId, CancellationToken ct);
    Task<AiConfigView> RollbackAsync(int expectedVersion, string reason, string? idempotencyKey, string? correlationId, CancellationToken ct);
}

public sealed class HttpAiRuntimeConfigClient(
    HttpClient http,
    IServiceTokenProvider tokens,
    ILogger<HttpAiRuntimeConfigClient> logger) : IAiRuntimeConfigClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AiConfigView> GetActiveAsync(string? correlationId, CancellationToken ct)
    {
        var token = await tokens.GetAsync("aura-ai", "ai:config", ct, correlationId);
        using var message = new HttpRequestMessage(HttpMethod.Get, "/internal/v1/ai/config");
        ServiceHttp.SetBearer(message, token);
        ServiceHttp.AddCorrelation(message, correlationId);
        return await SendAndReadAsync(message, ct);
    }

    public async Task<AiConfigView> ActivateAsync(AiConfigChange change, string? idempotencyKey, string? correlationId, CancellationToken ct)
    {
        var token = await tokens.GetAsync("aura-ai", "ai:config", ct, correlationId);
        using var message = new HttpRequestMessage(HttpMethod.Put, "/internal/v1/ai/config")
        {
            Content = JsonContent.Create(change, options: JsonOptions)
        };
        ServiceHttp.SetBearer(message, token);
        ServiceHttp.AddCorrelation(message, correlationId);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.Trim());
        return await SendAndReadAsync(message, ct);
    }

    public async Task<AiConfigView> RollbackAsync(int expectedVersion, string reason, string? idempotencyKey, string? correlationId, CancellationToken ct)
    {
        var token = await tokens.GetAsync("aura-ai", "ai:config", ct, correlationId);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/ai/config/rollback")
        {
            Content = JsonContent.Create(new { expectedVersion, reason }, options: JsonOptions)
        };
        ServiceHttp.SetBearer(message, token);
        ServiceHttp.AddCorrelation(message, correlationId);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.Trim());
        return await SendAndReadAsync(message, ct);
    }

    private async Task<AiConfigView> SendAndReadAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("AI Core runtime config request failed: {ExceptionType}", ex.GetType().Name);
            throw UploadFailure.Dependency("AI Core runtime config");
        }

        using (response)
        {
            var rawBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw MapError(response.StatusCode, rawBody);

            AiConfigView? view;
            try
            {
                view = JsonSerializer.Deserialize<AiConfigView>(rawBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Invalid JSON payload from AI Core runtime config: {ExceptionType}", ex.GetType().Name);
                throw UploadFailure.Dependency("AI Core runtime config");
            }

            if (view is null
                || view.ArtifactManifestId == Guid.Empty
                || view.Version < 1
                || string.IsNullOrWhiteSpace(view.ThresholdConfigSha256)
                || !Regex.IsMatch(view.ThresholdConfigSha256, @"\A[a-f0-9]{64}\z"))
            {
                throw UploadFailure.Dependency("AI Core runtime config");
            }

            return view;
        }
    }

    private static UploadFailure MapError(HttpStatusCode statusCode, string rawBody)
    {
        string? errorCode = null;
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.String)
                    errorCode = ec.GetString();
                if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    message = msg.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON upstream error falls through to status mapping below.
        }

        return (int)statusCode switch
        {
            400 => UploadFailure.Validation(new ErrorDetail("request", errorCode ?? "invalid_request")),
            401 or 403 => UploadFailure.Dependency("AI Core runtime config"),
            404 => new UploadFailure(404, errorCode ?? "ERR_MANIFEST_NOT_FOUND", message ?? "Không tìm thấy artifactManifestId trong registry phía server."),
            409 => new UploadFailure(409, errorCode ?? "ERR_VERSION_CONFLICT", message ?? "Xung đột phiên bản hoặc mã băm cấu hình AI."),
            _ => new UploadFailure(503, errorCode ?? "ERR_SMOKE_CHECK_FAILED", message ?? "AI Core runtime config unavailable or smoke verification failed.", retryable: true)
        };
    }
}

/// <summary>
/// Admin facade forwarding AI runtime configuration reads/activations/rollbacks to the authoritative
/// AI Core RUNTIME_REGISTRY while recording durable owner audit events via IAuditWriter.
/// </summary>
public sealed class AiConfigService(
    IAiRuntimeConfigClient runtimeClient,
    IAuditWriter auditWriter)
{
    private static readonly HashSet<string> AllowedChangeFields =
        new(StringComparer.Ordinal) { "expectedVersion", "artifactManifestId", "thresholdConfigSha256", "reason" };

    public Task<AiConfigView> GetActiveAsync(CancellationToken ct = default) =>
        GetActiveAsync(correlationId: null, ct);

    public Task<AiConfigView> GetActiveAsync(string? correlationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return runtimeClient.GetActiveAsync(correlationId, ct);
    }

    public static AiConfigChange ParseStrictChange(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw UploadFailure.Validation(new ErrorDetail("body", "must_be_json_object"));

        foreach (var prop in body.EnumerateObject())
        {
            if (!AllowedChangeFields.Contains(prop.Name))
                throw UploadFailure.Validation(new ErrorDetail(prop.Name, "unexpected_field_forbidden"));
        }

        if (!body.TryGetProperty("expectedVersion", out var versionEl)
            || versionEl.ValueKind != JsonValueKind.Number
            || !versionEl.TryGetInt32(out var expectedVersion)
            || expectedVersion < 1)
            throw UploadFailure.Validation(new ErrorDetail("expectedVersion", "must_be_positive_integer"));

        if (!body.TryGetProperty("artifactManifestId", out var manifestEl)
            || manifestEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(manifestEl.GetString(), out var artifactManifestId)
            || artifactManifestId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("artifactManifestId", "invalid_uuid"));

        if (!body.TryGetProperty("thresholdConfigSha256", out var shaEl)
            || shaEl.ValueKind != JsonValueKind.String
            || shaEl.GetString() is not { } sha
            || !Regex.IsMatch(sha, @"\A[a-f0-9]{64}\z"))
            throw UploadFailure.Validation(new ErrorDetail("thresholdConfigSha256", "invalid_sha256_hex"));

        if (!body.TryGetProperty("reason", out var reasonEl)
            || reasonEl.ValueKind != JsonValueKind.String
            || reasonEl.GetString() is not { } reason
            || string.IsNullOrWhiteSpace(reason)
            || reason.Length > 1000)
            throw UploadFailure.Validation(new ErrorDetail("reason", "required_1_to_1000_chars"));

        return new AiConfigChange(expectedVersion, artifactManifestId, sha, reason.Trim());
    }

    public Task<AiConfigView> ActivateAsync(Guid actorAccountId, AiConfigChange change, CancellationToken ct = default) =>
        ActivateAsync(actorAccountId, change, idempotencyKey: null, correlationId: null, ct);

    public async Task<AiConfigView> ActivateAsync(
        Guid actorAccountId,
        AiConfigChange change,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (actorAccountId == Guid.Empty)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần danh tính SYSTEM_ADMIN hợp lệ.");

        var next = await runtimeClient.ActivateAsync(change, idempotencyKey, correlationId, ct);
        await auditWriter.AppendAsync(
            AuditEvent.Create(
                AuditSource.Analysis,
                actorAccountId,
                AuditAction.AiConfigChanged,
                AuditResource.AiConfig,
                next.ArtifactManifestId.ToString("D"),
                correlationId,
                tenantId: null,
                resourceVersion: next.Version,
                outcome: AuditOutcome.Succeeded),
            ct);
        return next;
    }

    public Task<AiConfigView> RollbackAsync(Guid actorAccountId, int expectedVersion, string reason, CancellationToken ct = default) =>
        RollbackAsync(actorAccountId, expectedVersion, reason, idempotencyKey: null, correlationId: null, ct);

    public async Task<AiConfigView> RollbackAsync(
        Guid actorAccountId,
        int expectedVersion,
        string reason,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (actorAccountId == Guid.Empty)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần danh tính SYSTEM_ADMIN hợp lệ.");
        if (expectedVersion < 1)
            throw UploadFailure.Validation(new ErrorDetail("expectedVersion", "must_be_positive_integer"));
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            throw UploadFailure.Validation(new ErrorDetail("reason", "required_1_to_1000_chars"));

        var restored = await runtimeClient.RollbackAsync(expectedVersion, reason.Trim(), idempotencyKey, correlationId, ct);
        await auditWriter.AppendAsync(
            AuditEvent.Create(
                AuditSource.Analysis,
                actorAccountId,
                AuditAction.AiConfigChanged,
                AuditResource.AiConfig,
                restored.ArtifactManifestId.ToString("D"),
                correlationId,
                tenantId: null,
                resourceVersion: restored.Version,
                outcome: AuditOutcome.Succeeded),
            ct);
        return restored;
    }
}
