using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    Task<AiConfigView?> GetCommandReceiptAsync(string idempotencyKey, string? correlationId, CancellationToken ct);
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

    public async Task<AiConfigView?> GetCommandReceiptAsync(string idempotencyKey, string? correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var token = await tokens.GetAsync("aura-ai", "ai:config", ct, correlationId);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"/internal/v1/ai/config/commands/{Uri.EscapeDataString(idempotencyKey.Trim())}");
        ServiceHttp.SetBearer(message, token);
        ServiceHttp.AddCorrelation(message, correlationId);

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
            logger.LogWarning("AI Core command receipt lookup failed: {ExceptionType}", ex.GetType().Name);
            throw UploadFailure.Dependency("AI Core runtime config");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            var rawBody = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw MapError(response.StatusCode, rawBody);
            return ParseViewOrThrow(rawBody);
        }
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
            return ParseViewOrThrow(rawBody);
        }
    }

    private AiConfigView ParseViewOrThrow(string rawBody)
    {
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
/// Admin facade forwarding AI runtime configuration commands to the authoritative AI Core
/// while persisting command intent before outbound calls and reconciling deterministic audits.
/// </summary>
public sealed class AiConfigService(
    IAiRuntimeConfigClient runtimeClient,
    IAiConfigCommandJournal journal)
{
    private static readonly TimeSpan UncommittedCleanupGrace = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> AllowedChangeFields =
        new(StringComparer.Ordinal) { "expectedVersion", "artifactManifestId", "thresholdConfigSha256", "reason" };

    public Task<AiConfigView> GetActiveAsync(CancellationToken ct = default) =>
        GetActiveAsync(correlationId: null, ct);

    public async Task<AiConfigView> GetActiveAsync(string? correlationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await ReconcilePendingAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort reconciliation during read; GET still queries the authoritative AI Core state.
        }
        return await runtimeClient.GetActiveAsync(correlationId, ct);
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

    public Task<AiConfigView> ActivateAsync(
        Guid actorAccountId,
        AiConfigChange change,
        string? idempotencyKey,
        string? correlationId,
        CancellationToken ct = default) =>
        ExecuteCommandAsync(
            actorAccountId,
            "ACTIVATE_MANIFEST",
            change.ExpectedVersion,
            change.ArtifactManifestId,
            change.ThresholdConfigSha256,
            change.Reason,
            idempotencyKey,
            correlationId,
            key => runtimeClient.ActivateAsync(change, key, correlationId, ct),
            ct);

    public Task<AiConfigView> RollbackAsync(Guid actorAccountId, int expectedVersion, string reason, CancellationToken ct = default) =>
        RollbackAsync(actorAccountId, expectedVersion, reason, idempotencyKey: null, correlationId: null, ct);

    public Task<AiConfigView> RollbackAsync(
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

        var trimmedReason = reason.Trim();
        return ExecuteCommandAsync(
            actorAccountId,
            "ROLLBACK_MANIFEST",
            expectedVersion,
            artifactManifestId: null,
            sha256: null,
            trimmedReason,
            idempotencyKey,
            correlationId,
            key => runtimeClient.RollbackAsync(expectedVersion, trimmedReason, key, correlationId, ct),
            ct);
    }

    public async Task<int> ReconcilePendingAsync(CancellationToken ct = default)
    {
        var pending = await journal.ListPendingAsync(50, ct);
        var reconciled = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var cmd in pending)
        {
            ct.ThrowIfCancellationRequested();
            AiConfigView? receipt;
            try
            {
                receipt = await runtimeClient.GetCommandReceiptAsync(cmd.IdempotencyKey, cmd.CorrelationId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (UploadFailure)
            {
                continue;
            }

            if (receipt is not null)
            {
                await journal.CommitWithAuditAsync(cmd, receipt, ct);
                reconciled++;
            }
            else if (cmd.CreatedAt != default && (now - cmd.CreatedAt) > UncommittedCleanupGrace)
            {
                await journal.MarkFailedAsync(cmd.CommandId, "NOT_COMMITTED_UPSTREAM", ct);
            }
        }

        return reconciled;
    }

    private async Task<AiConfigView> ExecuteCommandAsync(
        Guid actorAccountId,
        string action,
        int expectedVersion,
        Guid? artifactManifestId,
        string? sha256,
        string reason,
        string? idempotencyKey,
        string? correlationId,
        Func<string, Task<AiConfigView>> sendUpstream,
        CancellationToken ct)
    {
        if (actorAccountId == Guid.Empty)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần danh tính SYSTEM_ADMIN hợp lệ.");

        var fingerprint = AiConfigCommandJournalHelpers.ComputeFingerprint(
            actorAccountId, action, expectedVersion, artifactManifestId, sha256, reason);
        var stableKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? AiConfigCommandJournalHelpers.DeriveStableIdempotencyKey(fingerprint)
            : idempotencyKey.Trim();
        var corr = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("D") : correlationId.Trim();

        var cmd = await journal.BeginOrLoadAsync(
            new AiConfigCommandRequest(
                actorAccountId, action, expectedVersion, artifactManifestId, sha256, reason.Trim(), stableKey, fingerprint, corr),
            ct);

        if (cmd.Status == AiConfigCommandStatus.Committed && cmd.Receipt is not null)
            return cmd.Receipt;

        AiConfigView receipt;
        try
        {
            receipt = await sendUpstream(cmd.IdempotencyKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UploadFailure failure) when (IsDefiniteUpstreamRejection(failure))
        {
            await journal.MarkFailedAsync(cmd.CommandId, failure.Code, ct);
            throw;
        }

        await journal.CommitWithAuditAsync(cmd, receipt, ct);
        return receipt;
    }

    private static bool IsDefiniteUpstreamRejection(UploadFailure failure) =>
        failure.Status is 400 or 404 or 409
        || (failure.Status == 503 && string.Equals(failure.Code, "ERR_SMOKE_CHECK_FAILED", StringComparison.Ordinal));
}

public sealed class AiConfigReconciliationWorker(
    IServiceScopeFactory scopes,
    ILogger<AiConfigReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiConfigService>();
                await service.ReconcilePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("AI config command reconciliation tick failed: {ExceptionType}", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
