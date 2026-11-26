using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Upload;
using AURA.Common.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AiConfigAdminTests
{
    private static readonly Guid BaseManifestId = Guid.Parse("11111111-1111-4111-8111-111111110301");
    private const string BaseSha = "0b0451cad981b69471920b374c19042f169b991a26c49e2c3e29395db7c6a0ee";

    private static readonly Guid V13ManifestId = Guid.Parse("11111111-1111-4111-8111-111111110405");
    private const string V13Sha = "dd444ce85afdcda830cdc825ace92d34d13e476739bdd2575890be13e8305087";

    private static async Task<(WebApplication App, HttpClient Client, AiConfigService Service, FakePersistentAiCoreHandler AiCore, RecordingAuditWriter Audit)> StartHostAsync(string? stateFile = null)
    {
        var path = stateFile ?? Path.Combine(Path.GetTempPath(), $"aura-aiconfig-test-{Guid.NewGuid():N}.json");
        var aiCoreHandler = new FakePersistentAiCoreHandler(path);
        var audit = new RecordingAuditWriter();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAnalysisUserIdentityResolver>(new HeaderRoleIdentityResolver());
        builder.Services.AddSingleton<IAuditWriter>(audit);
        builder.Services.AddSingleton<IServiceTokenProvider>(new FakeServiceTokenProvider());
        builder.Services.AddSingleton(aiCoreHandler);
        builder.Services.AddTransient<IAiRuntimeConfigClient>(sp =>
        {
            var http = new HttpClient(aiCoreHandler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://ai-core.test")
            };
            return new HttpAiRuntimeConfigClient(
                http,
                sp.GetRequiredService<IServiceTokenProvider>(),
                sp.GetRequiredService<ILogger<HttpAiRuntimeConfigClient>>());
        });
        builder.Services.AddTransient<AiConfigService>();

        var app = builder.Build();
        app.MapAnalysisEndpoints();
        await app.StartAsync();
        var client = app.GetTestClient();
        var service = app.Services.GetRequiredService<AiConfigService>();
        return (app, client, service, aiCoreHandler, audit);
    }

    private static HttpRequestMessage WithActor(HttpMethod method, string url, Guid accountId, string? role, object? body = null, string? idempotencyKey = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Add("X-Test-Account-Id", accountId.ToString("D"));
        if (role is not null) msg.Headers.Add("X-Test-Role", role);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) msg.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) msg.Content = JsonContent.Create(body);
        return msg;
    }

    private static HttpRequestMessage WithRawJson(string url, Guid accountId, string role, string rawJson)
    {
        var msg = new HttpRequestMessage(HttpMethod.Put, url);
        msg.Headers.Add("X-Test-Account-Id", accountId.ToString("D"));
        msg.Headers.Add("X-Test-Role", role);
        msg.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return msg;
    }

    [Fact]
    public async Task Enforces_rbac_401_unauthenticated_403_non_admin_and_200_system_admin()
    {
        var (app, client, _, _, _) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        using var unauthGet = await client.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", Guid.Empty, null));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthGet.StatusCode);

        using var unauthPut = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", Guid.Empty, null,
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "no auth" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthPut.StatusCode);

        var actor = Guid.NewGuid();
        foreach (var role in new[] { "PATIENT", "DOCTOR", "CLINIC_ADMIN" })
        {
            using var getForbidden = await client.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", actor, role));
            Assert.Equal(HttpStatusCode.Forbidden, getForbidden.StatusCode);

            using var putForbidden = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", actor, role,
                new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "forbidden" }));
            Assert.Equal(HttpStatusCode.Forbidden, putForbidden.StatusCode);
        }

        using var adminGet = await client.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", actor, "SYSTEM_ADMIN"));
        Assert.Equal(HttpStatusCode.OK, adminGet.StatusCode);
        using var getDoc = JsonDocument.Parse(await adminGet.Content.ReadAsStringAsync());
        Assert.Equal(1, getDoc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(BaseManifestId.ToString("D"), getDoc.RootElement.GetProperty("artifactManifestId").GetString());
        Assert.Equal(BaseSha, getDoc.RootElement.GetProperty("thresholdConfigSha256").GetString());
    }

    [Fact]
    public async Task Rejects_400_404_409_503_fail_closed_preserving_active_version_then_activates_and_rolls_back()
    {
        var (app, client, service, aiCore, audit) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        var admin = Guid.NewGuid();

        using var extraFields = await client.SendAsync(WithRawJson(
            "/api/v1/admin/ai/config",
            admin,
            "SYSTEM_ADMIN",
            $$"""{"expectedVersion":1,"artifactManifestId":"{{V13ManifestId:D}}","thresholdConfigSha256":"{{V13Sha}}","reason":"bad","weightsPath":"/tmp/evil.pt","weightsUrl":"https://evil.example/m.pt"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, extraFields.StatusCode);

        using var badSha = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = "not-sha", reason = "bad sha" }));
        Assert.Equal(HttpStatusCode.BadRequest, badSha.StatusCode);

        using var blankReason = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "   " }));
        Assert.Equal(HttpStatusCode.BadRequest, blankReason.StatusCode);

        using var unknownId = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = Guid.Parse("99999999-9999-4999-8999-999999999999"), thresholdConfigSha256 = V13Sha, reason = "unknown" }));
        Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);

        using var staleVer = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 99, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "stale version" }));
        Assert.Equal(HttpStatusCode.Conflict, staleVer.StatusCode);

        using var mismatchSha = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = new string('0', 64), reason = "tampered hash" }));
        Assert.Equal(HttpStatusCode.Conflict, mismatchSha.StatusCode);

        // 503 smoke failure keeps version=1 unchanged
        aiCore.FailSmoke = true;
        using var failedSmoke = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "smoke fails" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedSmoke.StatusCode);
        var stillActive = await service.GetActiveAsync();
        Assert.Equal(1, stillActive.Version);
        Assert.Equal(BaseManifestId, stillActive.ArtifactManifestId);
        Assert.Empty(audit.Events);

        // 200 activation succeeds when smoke passes and records durable audit
        aiCore.FailSmoke = false;
        using var activatedResp = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote v1.3 verified" }));
        Assert.Equal(HttpStatusCode.OK, activatedResp.StatusCode);
        using var activatedDoc = JsonDocument.Parse(await activatedResp.Content.ReadAsStringAsync());
        Assert.Equal(2, activatedDoc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(V13ManifestId.ToString("D"), activatedDoc.RootElement.GetProperty("artifactManifestId").GetString());

        // Atomic rollback restores BaseManifestId at version=3 and records durable owner audit
        var rolledBack = await service.RollbackAsync(admin, expectedVersion: 2, reason: "rollback verified");
        Assert.Equal(3, rolledBack.Version);
        Assert.Equal(BaseManifestId, rolledBack.ArtifactManifestId);
        Assert.Equal(BaseSha, rolledBack.ThresholdConfigSha256);

        Assert.Equal(2, audit.Events.Count);
        Assert.Equal(AuditAction.AiConfigChanged, audit.Events[0].Action);
        Assert.Equal(AuditResource.AiConfig, audit.Events[0].ResourceType);
        Assert.Equal(V13ManifestId.ToString("D"), audit.Events[0].ResourceId);
        Assert.Equal(2, audit.Events[0].ResourceVersion);
        Assert.Equal(admin, audit.Events[0].ActorAccountId);
        Assert.Equal(AuditAction.AiConfigChanged, audit.Events[1].Action);
        Assert.Equal(BaseManifestId.ToString("D"), audit.Events[1].ResourceId);
        Assert.Equal(3, audit.Events[1].ResourceVersion);
    }

    [Fact]
    public async Task Admin_put_changes_real_inference_config_not_only_view()
    {
        var (app, client, _, aiCore, _) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        Assert.Equal(1, aiCore.ActiveVersion);
        Assert.Equal(BaseManifestId, aiCore.ActiveManifestId);

        var admin = Guid.NewGuid();
        using var putResp = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote v1.3" }));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        Assert.Equal(1, aiCore.InternalPutCount);
        Assert.Equal(2, aiCore.ActiveVersion);
        Assert.Equal(V13ManifestId, aiCore.ActiveManifestId);
        Assert.Equal("Bearer token.aura-ai.ai-config", aiCore.LastAuthorizationHeader);
    }

    [Fact]
    public async Task Restart_preserves_active_version_and_history()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"aura-aiconfig-restart-{Guid.NewGuid():N}.json");
        try
        {
            var admin = Guid.NewGuid();
            var (app1, client1, _, _, _) = await StartHostAsync(stateFile);
            await using (app1)
            using (client1)
            {
                using var putResp = await client1.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
                    new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "persist across restart" }));
                Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
            }

            // Simulate process restart with a brand-new host reading only persisted state
            var (app2, client2, service2, _, audit2) = await StartHostAsync(stateFile);
            await using (app2)
            using (client2)
            {
                using var getResp = await client2.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN"));
                Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
                using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
                Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
                Assert.Equal(V13ManifestId.ToString("D"), doc.RootElement.GetProperty("artifactManifestId").GetString());

                var rolledBack = await service2.RollbackAsync(admin, expectedVersion: 2, reason: "rollback after restart");
                Assert.Equal(3, rolledBack.Version);
                Assert.Equal(BaseManifestId, rolledBack.ArtifactManifestId);
                Assert.Single(audit2.Events);
                Assert.Equal(3, audit2.Events[0].ResourceVersion);
            }
        }
        finally
        {
            if (File.Exists(stateFile)) File.Delete(stateFile);
        }
    }

    [Fact]
    public async Task Real_smoke_failure_does_not_change_active_runtime()
    {
        var (app, client, service, aiCore, audit) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        aiCore.FailSmoke = true;
        var admin = Guid.NewGuid();
        using var failedPut = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "broken artifact must fail smoke" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedPut.StatusCode);

        Assert.Equal(1, aiCore.ActiveVersion);
        Assert.Equal(BaseManifestId, aiCore.ActiveManifestId);
        Assert.Equal(BaseManifestId, (await service.GetActiveAsync()).ArtifactManifestId);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Lost_ack_retry_replays_idempotently_and_records_durable_audit()
    {
        var (app, client, _, aiCore, audit) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        var admin = Guid.NewGuid();
        // Simulate network loss AFTER AI Core committed version 2
        aiCore.DropNextAckAfterCommit = true;
        using var failedAck = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote with lost ack" },
            idempotencyKey: "req-lost-ack-1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedAck.StatusCode);
        Assert.Equal(2, aiCore.ActiveVersion);
        Assert.Empty(audit.Events);

        // Operator retries the exact same command with expectedVersion=1 -> AI Core replays version=2 cleanly
        using var retryOk = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote with lost ack" },
            idempotencyKey: "req-lost-ack-1"));
        Assert.Equal(HttpStatusCode.OK, retryOk.StatusCode);
        using var doc = JsonDocument.Parse(await retryOk.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Single(audit.Events);
        Assert.Equal(2, audit.Events[0].ResourceVersion);

        // Different payload reusing expectedVersion=1 must still reject with 409
        using var conflictOther = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "different reason on old version" },
            idempotencyKey: "req-other"));
        Assert.Equal(HttpStatusCode.Conflict, conflictOther.StatusCode);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEvent> Events { get; } = new();
        public Task AppendAsync(AuditEvent e, CancellationToken ct)
        {
            Events.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServiceTokenProvider : IServiceTokenProvider
    {
        public Task<string> GetAsync(string audience, string scope, CancellationToken ct, string? correlationId = null)
        {
            Assert.Equal("aura-ai", audience);
            Assert.Equal("ai:config", scope);
            return Task.FromResult("token.aura-ai.ai-config");
        }
    }

    private sealed class FakePersistentAiCoreHandler(string stateFilePath) : HttpMessageHandler
    {
        private static readonly Dictionary<Guid, string> Catalog = new()
        {
            [BaseManifestId] = BaseSha,
            [V13ManifestId] = V13Sha
        };

        private sealed record PersistedState(int Version, Guid ManifestId, string Sha, DateTimeOffset ActivatedAt,
            List<Guid> History, string? LastKey, int? LastExpectedVersion, string? LastFingerprint);

        public bool FailSmoke { get; set; }
        public bool DropNextAckAfterCommit { get; set; }
        public int InternalPutCount { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }

        public int ActiveVersion => Load().Version;
        public Guid ActiveManifestId => Load().ManifestId;

        private PersistedState Load()
        {
            if (!File.Exists(stateFilePath))
                return new PersistedState(1, BaseManifestId, BaseSha, DateTimeOffset.UtcNow, new(), null, null, null);
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(stateFilePath))!;
        }

        private void Save(PersistedState state)
        {
            var tmp = stateFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, stateFilePath, overwrite: true);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            var state = Load();
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (request.Method == HttpMethod.Get && path == "/internal/v1/ai/config")
                return JsonOk(state);

            if (request.Method == HttpMethod.Put && path == "/internal/v1/ai/config")
            {
                InternalPutCount++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var expectedVersion = doc.RootElement.GetProperty("expectedVersion").GetInt32();
                var manifestId = doc.RootElement.GetProperty("artifactManifestId").GetGuid();
                var sha = doc.RootElement.GetProperty("thresholdConfigSha256").GetString()!;
                var reason = doc.RootElement.GetProperty("reason").GetString()!;
                var idemKey = request.Headers.TryGetValues("Idempotency-Key", out var k) ? k.FirstOrDefault() : null;
                var fingerprint = $"{manifestId:D}|{sha}|{reason}";

                if ((idemKey is not null && idemKey == state.LastKey)
                    || (expectedVersion == state.LastExpectedVersion && fingerprint == state.LastFingerprint))
                {
                    if (idemKey is not null && state.LastKey == idemKey && fingerprint != state.LastFingerprint)
                        return JsonErr(409, "ERR_IDEMPOTENCY_CONFLICT", "Idempotency-Key conflict.");
                    return JsonOk(state);
                }

                if (!Catalog.TryGetValue(manifestId, out var expectedSha))
                    return JsonErr(404, "ERR_MANIFEST_NOT_FOUND", "Unknown manifest.");
                if (expectedVersion != state.Version)
                    return JsonErr(409, "ERR_VERSION_CONFLICT", "Version mismatch.");
                if (!string.Equals(expectedSha, sha, StringComparison.Ordinal))
                    return JsonErr(409, "ERR_HASH_MISMATCH", "SHA-256 mismatch.");
                if (FailSmoke)
                    return JsonErr(503, "ERR_SMOKE_CHECK_FAILED", "Smoke failed.");

                var history = new List<Guid>(state.History) { state.ManifestId };
                var next = new PersistedState(state.Version + 1, manifestId, sha, DateTimeOffset.UtcNow, history,
                    idemKey, expectedVersion, fingerprint);
                Save(next);

                if (DropNextAckAfterCommit)
                {
                    DropNextAckAfterCommit = false;
                    throw new HttpRequestException("Simulated lost ACK after AI Core commit.");
                }
                return JsonOk(next);
            }

            if (request.Method == HttpMethod.Post && path == "/internal/v1/ai/config/rollback")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var expectedVersion = doc.RootElement.GetProperty("expectedVersion").GetInt32();
                if (expectedVersion != state.Version)
                    return JsonErr(409, "ERR_VERSION_CONFLICT", "Version mismatch.");
                if (state.History.Count == 0)
                    return JsonErr(409, "ERR_NO_PREVIOUS_VERSION", "No previous version.");
                if (FailSmoke)
                    return JsonErr(503, "ERR_SMOKE_CHECK_FAILED", "Rollback smoke failed.");

                var targetId = state.History[^1];
                var nextHistory = state.History.Take(state.History.Count - 1).ToList();
                var next = new PersistedState(state.Version + 1, targetId, Catalog[targetId], DateTimeOffset.UtcNow,
                    nextHistory, null, expectedVersion, "rollback");
                Save(next);
                return JsonOk(next);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonOk(PersistedState s) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    artifactManifestId = s.ManifestId,
                    thresholdConfigSha256 = s.Sha,
                    version = s.Version,
                    activatedAt = s.ActivatedAt
                })
            };

        private static HttpResponseMessage JsonErr(int status, string code, string msg) =>
            new((HttpStatusCode)status)
            {
                Content = JsonContent.Create(new { error_code = code, message = msg })
            };
    }

    private sealed class HeaderRoleIdentityResolver : IAnalysisUserIdentityResolver
    {
        public Task<AnalysisUserContext> ResolveAsync(HttpRequest request, CancellationToken ct)
        {
            var id = request.Headers.TryGetValue("X-Test-Account-Id", out var raw) && Guid.TryParse(raw.ToString(), out var parsed)
                ? parsed
                : Guid.Empty;
            var roles = request.Headers.TryGetValue("X-Test-Role", out var r) && !string.IsNullOrWhiteSpace(r.ToString())
                ? new[] { r.ToString() }
                : Array.Empty<string>();
            return Task.FromResult(new AnalysisUserContext(id, roles));
        }
    }
}
