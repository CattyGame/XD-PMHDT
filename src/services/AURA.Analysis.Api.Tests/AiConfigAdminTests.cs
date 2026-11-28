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

    private static async Task<(WebApplication App, HttpClient Client, AiConfigService Service, FakePersistentAiCoreHandler AiCore, FileBackedAiConfigCommandJournal Journal, RecordingAuditWriter Audit)> StartHostAsync(
        string? stateFile = null,
        RecordingAuditWriter? sharedAudit = null)
    {
        var path = stateFile ?? Path.Combine(Path.GetTempPath(), $"aura-aiconfig-test-{Guid.NewGuid():N}.json");
        var journalPath = path + ".journal.json";
        var aiCoreHandler = new FakePersistentAiCoreHandler(path);
        var audit = sharedAudit ?? new RecordingAuditWriter();
        var journal = new FileBackedAiConfigCommandJournal(journalPath, audit);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAnalysisUserIdentityResolver>(new HeaderRoleIdentityResolver());
        builder.Services.AddSingleton<IAuditWriter>(audit);
        builder.Services.AddSingleton<IAiConfigCommandJournal>(journal);
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
        return (app, client, service, aiCoreHandler, journal, audit);
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
        var (app, client, _, _, _, _) = await StartHostAsync();
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
        var (app, client, service, aiCore, _, audit) = await StartHostAsync();
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
        var (app, client, _, aiCore, _, _) = await StartHostAsync();
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
            var (app1, client1, _, _, _, _) = await StartHostAsync(stateFile);
            await using (app1)
            using (client1)
            {
                using var putResp = await client1.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
                    new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "persist across restart" }));
                Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
            }

            // Simulate process restart with a brand-new host reading only persisted state
            var (app2, client2, service2, _, _, audit2) = await StartHostAsync(stateFile);
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
            CleanupTempFiles(stateFile);
        }
    }

    [Fact]
    public async Task Real_smoke_failure_does_not_change_active_runtime()
    {
        var (app, client, service, aiCore, _, audit) = await StartHostAsync();
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
        var (app, client, _, aiCore, _, audit) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        var admin = Guid.NewGuid();
        aiCore.DropNextAckAfterCommit = true;
        using var failedAck = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote with lost ack" },
            idempotencyKey: "req-lost-ack-1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedAck.StatusCode);
        Assert.Equal(2, aiCore.ActiveVersion);
        Assert.Empty(audit.Events);

        // Retry completes journal + single deterministic audit without invoking AI Core a second time once reconciled/committed
        using var retryOk = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote with lost ack" },
            idempotencyKey: "req-lost-ack-1"));
        Assert.Equal(HttpStatusCode.OK, retryOk.StatusCode);
        using var doc = JsonDocument.Parse(await retryOk.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Single(audit.Events);
        Assert.Equal(2, audit.Events[0].ResourceVersion);

        // Different payload reusing expectedVersion=1 with another key must reject 409; reusing same key with different payload also rejects 409
        using var conflictOther = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "different reason on old version" },
            idempotencyKey: "req-other"));
        Assert.Equal(HttpStatusCode.Conflict, conflictOther.StatusCode);

        using var conflictSameKey = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "different reason reusing key" },
            idempotencyKey: "req-lost-ack-1"));
        Assert.Equal(HttpStatusCode.Conflict, conflictSameKey.StatusCode);
    }

    [Fact]
    public async Task Repeated_successful_command_replay_creates_only_one_audit_event()
    {
        var (app, client, _, _, _, audit) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        var admin = Guid.NewGuid();
        using var first = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "idempotent put" },
            idempotencyKey: "same-key-twice"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "idempotent put" },
            idempotencyKey: "same-key-twice"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Single(audit.Events);
        Assert.Equal(2, audit.Events[0].ResourceVersion);
        Assert.Equal(admin, audit.Events[0].ActorAccountId);
    }

    [Fact]
    public async Task Lost_ack_then_restart_without_manual_retry_reconciles_single_audit()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"aura-f3-lostack-{Guid.NewGuid():N}.json");
        var sharedAudit = new RecordingAuditWriter();
        try
        {
            var admin = Guid.NewGuid();
            var (app1, client1, _, aiCore1, _, _) = await StartHostAsync(stateFile, sharedAudit);
            await using (app1)
            using (client1)
            {
                aiCore1.DropNextAckAfterCommit = true;
                using var failedAck = await client1.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
                    new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "lost ack before restart" },
                    idempotencyKey: "lost-ack-no-retry"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failedAck.StatusCode);
                Assert.Equal(2, aiCore1.ActiveVersion);
                Assert.Empty(sharedAudit.Events);
            }

            // Simulate host restart without operator manual retry; GET triggers automatic reconciliation
            var (app2, client2, _, _, _, _) = await StartHostAsync(stateFile, sharedAudit);
            await using (app2)
            using (client2)
            {
                using var getResp = await client2.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN"));
                Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
                Assert.Single(sharedAudit.Events);
                Assert.Equal(2, sharedAudit.Events[0].ResourceVersion);
                Assert.Equal(admin, sharedAudit.Events[0].ActorAccountId);
                Assert.Equal(V13ManifestId.ToString("D"), sharedAudit.Events[0].ResourceId);
            }
        }
        finally
        {
            CleanupTempFiles(stateFile);
        }
    }

    [Fact]
    public async Task Audit_outage_then_recovery_and_rollback_reconcile_single_deterministic_audit()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"aura-f3-outage-{Guid.NewGuid():N}.json");
        var sharedAudit = new RecordingAuditWriter();
        try
        {
            var admin = Guid.NewGuid();
            var (app1, client1, service1, aiCore1, journal1, _) = await StartHostAsync(stateFile, sharedAudit);
            await using (app1)
            using (client1)
            {
                journal1.FailNextCommit = true;
                using var outageResp = await client1.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
                    new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "audit outage case" },
                    idempotencyKey: "outage-put-1"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, outageResp.StatusCode);
                Assert.Equal(2, aiCore1.ActiveVersion);
                Assert.Empty(sharedAudit.Events);

                // Recovery without manual replay heals exact audit row
                Assert.Equal(1, await service1.ReconcilePendingAsync());
                Assert.Single(sharedAudit.Events);
                Assert.Equal(2, sharedAudit.Events[0].ResourceVersion);

                // Rollback with lost ACK also reconciles on restart with deterministic event
                aiCore1.DropNextAckAfterCommit = true;
                await Assert.ThrowsAsync<UploadFailure>(() =>
                    service1.RollbackAsync(admin, 2, "rollback lost ack", "rb-lost-ack-1", "corr-rb", default));
                Assert.Equal(3, aiCore1.ActiveVersion);
                Assert.Single(sharedAudit.Events);
            }

            var (app2, _, service2, _, _, _) = await StartHostAsync(stateFile, sharedAudit);
            await using (app2)
            {
                Assert.Equal(1, await service2.ReconcilePendingAsync());
                Assert.Equal(2, sharedAudit.Events.Count);
                Assert.Equal(3, sharedAudit.Events[1].ResourceVersion);
                Assert.Equal(BaseManifestId.ToString("D"), sharedAudit.Events[1].ResourceId);
            }
        }
        finally
        {
            CleanupTempFiles(stateFile);
        }
    }

    [Fact]
    public async Task Lost_ack_then_busy_retry_keeps_pending_and_reconciles_single_audit()
    {
        var stateFile = Path.Combine(Path.GetTempPath(), $"aura-r3-02-{Guid.NewGuid():N}.json");
        var sharedAudit = new RecordingAuditWriter();
        try
        {
            var admin = Guid.NewGuid();
            var (app1, client1, _, aiCore1, _, _) = await StartHostAsync(stateFile, sharedAudit);
            await using (app1)
            using (client1)
            {
                aiCore1.DropNextAckAfterCommit = true;
                using var failedAck = await client1.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
                    new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "r3-02 busy retry" },
                    idempotencyKey: "r3-02-key-1"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, failedAck.StatusCode);
                Assert.Equal(2, aiCore1.ActiveVersion);

                // Retry while AI Core slot is busy (503 ERR_INFERENCE_SLOT_BUSY) MUST keep command PENDING
                aiCore1.SimulateSlotBusy = true;
                using var busyRetry = await client1.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
                    new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "r3-02 busy retry" },
                    idempotencyKey: "r3-02-key-1"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, busyRetry.StatusCode);
                Assert.Empty(sharedAudit.Events);
                aiCore1.SimulateSlotBusy = false;
            }

            // Restart without manual retry -> reconcile MUST recover exact 1 audit row
            var (app2, _, service2, _, _, _) = await StartHostAsync(stateFile, sharedAudit);
            await using (app2)
            {
                Assert.Equal(1, await service2.ReconcilePendingAsync());
                Assert.Single(sharedAudit.Events);
                Assert.Equal(2, sharedAudit.Events[0].ResourceVersion);
                Assert.Equal(admin, sharedAudit.Events[0].ActorAccountId);
            }
        }
        finally
        {
            CleanupTempFiles(stateFile);
        }
    }

    private static void CleanupTempFiles(string stateFile)
    {
        if (File.Exists(stateFile)) File.Delete(stateFile);
        if (File.Exists(stateFile + ".journal.json")) File.Delete(stateFile + ".journal.json");
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        private readonly object _lock = new();
        public List<AuditEvent> Events { get; } = new();

        public Task AppendAsync(AuditEvent e, CancellationToken ct)
        {
            AuditEvent.Validate(e);
            lock (_lock)
            {
                var existing = Events.FirstOrDefault(x => x.EventId == e.EventId);
                if (existing is null)
                {
                    Events.Add(e);
                }
                else if (AuditJson.Hash(existing) != AuditJson.Hash(e))
                {
                    throw new AuditConflictException();
                }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FileBackedAiConfigCommandJournal(string journalFilePath, IAuditWriter auditWriter) : IAiConfigCommandJournal
    {
        private readonly object _lock = new();
        public bool FailNextCommit { get; set; }

        private List<AiConfigCommandRecord> LoadAll()
        {
            if (!File.Exists(journalFilePath))
                return new List<AiConfigCommandRecord>();
            return JsonSerializer.Deserialize<List<AiConfigCommandRecord>>(File.ReadAllText(journalFilePath)) ?? new List<AiConfigCommandRecord>();
        }

        private void SaveAll(List<AiConfigCommandRecord> items)
        {
            var tmp = journalFilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items));
            File.Move(tmp, journalFilePath, overwrite: true);
        }

        public Task<AiConfigCommandRecord> BeginOrLoadAsync(AiConfigCommandRequest request, CancellationToken ct)
        {
            lock (_lock)
            {
                var items = LoadAll();
                var idx = items.FindIndex(x => x.IdempotencyKey == request.IdempotencyKey);
                if (idx >= 0)
                {
                    var existing = items[idx];
                    if (!string.Equals(existing.PayloadFingerprint, request.PayloadFingerprint, StringComparison.Ordinal))
                        throw new UploadFailure(409, "ERR_IDEMPOTENCY_CONFLICT", "Idempotency-Key conflict.");
                    if (existing.Status == AiConfigCommandStatus.Failed)
                    {
                        existing = existing with { Status = AiConfigCommandStatus.Pending, FailureCode = null };
                        items[idx] = existing;
                        SaveAll(items);
                    }
                    return Task.FromResult(existing);
                }

                var created = new AiConfigCommandRecord(
                    Guid.NewGuid(),
                    request.IdempotencyKey,
                    request.Action,
                    request.ActorAccountId,
                    request.ExpectedVersion,
                    request.ArtifactManifestId,
                    request.ThresholdConfigSha256,
                    request.Reason,
                    request.PayloadFingerprint,
                    request.CorrelationId,
                    AiConfigCommandStatus.Pending,
                    null,
                    null,
                    DateTimeOffset.UtcNow);
                items.Add(created);
                SaveAll(items);
                return Task.FromResult(created);
            }
        }

        public async Task CommitWithAuditAsync(AiConfigCommandRecord command, AiConfigView receipt, CancellationToken ct)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new InvalidOperationException("Simulated transient audit DB outage.");
            }

            var ev = AiConfigCommandJournalHelpers.BuildDeterministicAuditEvent(command, receipt);
            await auditWriter.AppendAsync(ev, ct);
            lock (_lock)
            {
                var items = LoadAll();
                var idx = items.FindIndex(x => x.CommandId == command.CommandId);
                if (idx >= 0)
                {
                    items[idx] = items[idx] with
                    {
                        Status = AiConfigCommandStatus.Committed,
                        Receipt = receipt,
                        FailureCode = null
                    };
                    SaveAll(items);
                }
            }
        }

        public Task MarkFailedAsync(Guid commandId, string failureCode, CancellationToken ct)
        {
            lock (_lock)
            {
                var items = LoadAll();
                var idx = items.FindIndex(x => x.CommandId == commandId && x.Status == AiConfigCommandStatus.Pending);
                if (idx >= 0)
                {
                    items[idx] = items[idx] with { Status = AiConfigCommandStatus.Failed, FailureCode = failureCode };
                    SaveAll(items);
                }
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiConfigCommandRecord>> ListPendingAsync(int limit, CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<AiConfigCommandRecord>>(
                    LoadAll().Where(x => x.Status == AiConfigCommandStatus.Pending).Take(limit).ToList());
            }
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

        private sealed record StoredReceipt(int Version, Guid ManifestId, string Sha, DateTimeOffset ActivatedAt, string Fingerprint);

        private sealed record PersistedState(
            int Version,
            Guid ManifestId,
            string Sha,
            DateTimeOffset ActivatedAt,
            List<Guid> History,
            Dictionary<string, StoredReceipt> Idempotency);

        public bool FailSmoke { get; set; }
        public bool SimulateSlotBusy { get; set; }
        public bool DropNextAckAfterCommit { get; set; }
        public int InternalPutCount { get; private set; }
        public string? LastAuthorizationHeader { get; private set; }

        public int ActiveVersion => Load().Version;
        public Guid ActiveManifestId => Load().ManifestId;

        private PersistedState Load()
        {
            if (!File.Exists(stateFilePath))
                return new PersistedState(1, BaseManifestId, BaseSha, DateTimeOffset.UtcNow, new(), new());
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
                return JsonView(state.ManifestId, state.Sha, state.Version, state.ActivatedAt);

            const string cmdPrefix = "/internal/v1/ai/config/commands/";
            if (request.Method == HttpMethod.Get && path.StartsWith(cmdPrefix, StringComparison.Ordinal))
            {
                var key = Uri.UnescapeDataString(path[cmdPrefix.Length..]);
                if (state.Idempotency.TryGetValue(key, out var rec))
                    return JsonView(rec.ManifestId, rec.Sha, rec.Version, rec.ActivatedAt);
                return JsonErr(404, "ERR_COMMAND_NOT_FOUND", "Command receipt not found.");
            }

            if (request.Method == HttpMethod.Put && path == "/internal/v1/ai/config")
            {
                if (SimulateSlotBusy)
                    return JsonErr(503, "ERR_INFERENCE_SLOT_BUSY", "Inference slot is busy.");
                InternalPutCount++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var expectedVersion = doc.RootElement.GetProperty("expectedVersion").GetInt32();
                var manifestId = doc.RootElement.GetProperty("artifactManifestId").GetGuid();
                var sha = doc.RootElement.GetProperty("thresholdConfigSha256").GetString()!;
                var reason = doc.RootElement.GetProperty("reason").GetString()!;
                var idemKey = request.Headers.TryGetValues("Idempotency-Key", out var k) ? k.FirstOrDefault() : null;
                var fingerprint = $"ACTIVATE|{expectedVersion}|{manifestId:D}|{sha}|{reason}";

                if (idemKey is not null && state.Idempotency.TryGetValue(idemKey, out var stored))
                {
                    if (stored.Fingerprint != fingerprint)
                        return JsonErr(409, "ERR_IDEMPOTENCY_CONFLICT", "Idempotency-Key conflict.");
                    return JsonView(stored.ManifestId, stored.Sha, stored.Version, stored.ActivatedAt);
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
                var nextVer = state.Version + 1;
                var activatedAt = DateTimeOffset.UtcNow;
                var idem = new Dictionary<string, StoredReceipt>(state.Idempotency);
                if (idemKey is not null)
                    idem[idemKey] = new StoredReceipt(nextVer, manifestId, sha, activatedAt, fingerprint);
                var next = new PersistedState(nextVer, manifestId, sha, activatedAt, history, idem);
                Save(next);

                if (DropNextAckAfterCommit)
                {
                    DropNextAckAfterCommit = false;
                    throw new HttpRequestException("Simulated lost ACK after AI Core commit.");
                }
                return JsonView(manifestId, sha, nextVer, activatedAt);
            }

            if (request.Method == HttpMethod.Post && path == "/internal/v1/ai/config/rollback")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var expectedVersion = doc.RootElement.GetProperty("expectedVersion").GetInt32();
                var reason = doc.RootElement.GetProperty("reason").GetString()!;
                var idemKey = request.Headers.TryGetValues("Idempotency-Key", out var k) ? k.FirstOrDefault() : null;
                var fingerprint = $"ROLLBACK|{expectedVersion}|{reason}";

                if (idemKey is not null && state.Idempotency.TryGetValue(idemKey, out var stored))
                {
                    if (stored.Fingerprint != fingerprint)
                        return JsonErr(409, "ERR_IDEMPOTENCY_CONFLICT", "Idempotency-Key conflict.");
                    return JsonView(stored.ManifestId, stored.Sha, stored.Version, stored.ActivatedAt);
                }

                if (expectedVersion != state.Version)
                    return JsonErr(409, "ERR_VERSION_CONFLICT", "Version mismatch.");
                if (state.History.Count == 0)
                    return JsonErr(409, "ERR_NO_PREVIOUS_VERSION", "No previous version.");
                if (FailSmoke)
                    return JsonErr(503, "ERR_SMOKE_CHECK_FAILED", "Rollback smoke failed.");

                var targetId = state.History[^1];
                var nextHistory = state.History.Take(state.History.Count - 1).ToList();
                var nextVer = state.Version + 1;
                var activatedAt = DateTimeOffset.UtcNow;
                var idem = new Dictionary<string, StoredReceipt>(state.Idempotency);
                if (idemKey is not null)
                    idem[idemKey] = new StoredReceipt(nextVer, targetId, Catalog[targetId], activatedAt, fingerprint);
                var next = new PersistedState(nextVer, targetId, Catalog[targetId], activatedAt, nextHistory, idem);
                Save(next);

                if (DropNextAckAfterCommit)
                {
                    DropNextAckAfterCommit = false;
                    throw new HttpRequestException("Simulated lost ACK after AI Core rollback commit.");
                }
                return JsonView(targetId, Catalog[targetId], nextVer, activatedAt);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonView(Guid manifestId, string sha, int version, DateTimeOffset activatedAt) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    artifactManifestId = manifestId,
                    thresholdConfigSha256 = sha,
                    version,
                    activatedAt
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
