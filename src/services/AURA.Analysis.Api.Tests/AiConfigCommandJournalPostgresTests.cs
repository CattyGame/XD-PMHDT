using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AURA.Analysis.Api.Upload;
using AURA.Common.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AiConfigCommandJournalPostgresTests
{
    private static readonly Guid BaseManifestId = Guid.Parse("11111111-1111-4111-8111-111111110301");
    private const string BaseSha = "0b0451cad981b69471920b374c19042f169b991a26c49e2c3e29395db7c6a0ee";

    private static readonly Guid V13ManifestId = Guid.Parse("11111111-1111-4111-8111-111111110405");
    private const string V13Sha = "dd444ce85afdcda830cdc825ace92d34d13e476739bdd2575890be13e8305087";

    private static string ConnectionString() =>
        Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!;

    [PostgresFact]
    public async Task Migration_creates_ai_config_commands_table_and_grants()
    {
        await using var db = NpgsqlDataSource.Create(ConnectionString());
        await using var cmd = db.CreateCommand("""
            SELECT to_regclass('analysis.ai_config_commands') IS NOT NULL,
                   has_table_privilege('aura_analysis_app', 'analysis.ai_config_commands', 'SELECT'),
                   has_table_privilege('aura_analysis_app', 'analysis.ai_config_commands', 'INSERT'),
                   NOT has_table_privilege('aura_analysis_app', 'analysis.ai_config_commands', 'DELETE');
            """);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal((true, true, true, true),
            (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
    }

    [PostgresFact]
    public async Task Lost_ack_then_restart_reconciles_single_durable_audit_in_postgres()
    {
        await using var db = NpgsqlDataSource.Create(ConnectionString());
        var journal = new PostgresAiConfigCommandJournal(db);
        var fakeUpstream = new StubAiRuntimeHandler();
        var admin = Guid.NewGuid();
        var key = "pg-lostack-" + Guid.NewGuid().ToString("N");

        var svc1 = new AiConfigService(BuildClient(fakeUpstream), journal);
        fakeUpstream.DropNextAck = true;

        var depErr = await Assert.ThrowsAsync<UploadFailure>(() =>
            svc1.ActivateAsync(
                admin,
                new AiConfigChange(1, V13ManifestId, V13Sha, "postgres lost ack reconcile"),
                key,
                "corr-pg-1",
                CancellationToken.None));
        Assert.Equal(503, depErr.Status);

        // Lookup inserted command_id
        Guid commandId;
        await using (var q = db.CreateCommand("SELECT command_id, status FROM analysis.ai_config_commands WHERE idempotency_key = $1"))
        {
            q.Parameters.AddWithValue(key);
            await using var r = await q.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            commandId = r.GetGuid(0);
            Assert.Equal("PENDING", r.GetString(1));
        }
        Assert.Equal(0, await CountAuditByEventIdAsync(db, commandId));

        // Recreate AiConfigService (restart) WITHOUT manual operator retry and run reconciliation
        var svc2 = new AiConfigService(BuildClient(fakeUpstream), new PostgresAiConfigCommandJournal(db));
        var reconciled = await svc2.ReconcilePendingAsync(CancellationToken.None);
        Assert.True(reconciled >= 1);

        Assert.Equal(1, await CountAuditByEventIdAsync(db, commandId));
        Assert.Equal(1, await CountOutboxByEventIdAsync(db, commandId));
        await using (var q = db.CreateCommand("SELECT status, committed_version FROM analysis.ai_config_commands WHERE command_id = $1"))
        {
            q.Parameters.AddWithValue(commandId);
            await using var r = await q.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal("COMMITTED", r.GetString(0));
            Assert.Equal(2, r.GetInt32(1));
        }

        // Replaying the exact same command multiple times never creates a second audit row
        var replayed = await svc2.ActivateAsync(
            admin,
            new AiConfigChange(1, V13ManifestId, V13Sha, "postgres lost ack reconcile"),
            key,
            "corr-pg-1",
            CancellationToken.None);
        Assert.Equal(2, replayed.Version);
        Assert.Equal(1, await CountAuditByEventIdAsync(db, commandId));

        // Different payload reusing key returns 409 conflict
        var conflict = await Assert.ThrowsAsync<UploadFailure>(() =>
            svc2.ActivateAsync(
                admin,
                new AiConfigChange(1, BaseManifestId, BaseSha, "different payload"),
                key,
                "corr-pg-2",
                CancellationToken.None));
        Assert.Equal(409, conflict.Status);
        Assert.Equal("ERR_IDEMPOTENCY_CONFLICT", conflict.Code);
    }

    private static HttpAiRuntimeConfigClient BuildClient(StubAiRuntimeHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://ai-core.pg.test") };
        return new HttpAiRuntimeConfigClient(http, new StubTokenProvider(), NullLogger<HttpAiRuntimeConfigClient>.Instance);
    }

    private static async Task<long> CountAuditByEventIdAsync(NpgsqlDataSource db, Guid eventId)
    {
        await using var cmd = db.CreateCommand("SELECT COUNT(*) FROM analysis.audit_events WHERE event_id = $1");
        cmd.Parameters.AddWithValue(eventId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountOutboxByEventIdAsync(NpgsqlDataSource db, Guid eventId)
    {
        await using var cmd = db.CreateCommand("SELECT COUNT(*) FROM analysis.audit_outbox WHERE event_id = $1");
        cmd.Parameters.AddWithValue(eventId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private sealed class StubTokenProvider : IServiceTokenProvider
    {
        public Task<string> GetAsync(string audience, string scope, CancellationToken ct, string? correlationId = null) =>
            Task.FromResult("token.aura-ai.ai-config");
    }

    private sealed class StubAiRuntimeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (int Version, Guid ManifestId, string Sha, DateTimeOffset ActivatedAt)> _committed = new();
        private int _version = 1;
        public bool DropNextAck { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            const string prefix = "/internal/v1/ai/config/commands/";
            if (request.Method == HttpMethod.Get && path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var k = Uri.UnescapeDataString(path[prefix.Length..]);
                if (_committed.TryGetValue(k, out var rec))
                    return OkView(rec.ManifestId, rec.Sha, rec.Version, rec.ActivatedAt);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Put && path == "/internal/v1/ai/config")
            {
                var key = request.Headers.GetValues("Idempotency-Key").First();
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var manifestId = doc.RootElement.GetProperty("artifactManifestId").GetGuid();
                var sha = doc.RootElement.GetProperty("thresholdConfigSha256").GetString()!;
                if (!_committed.TryGetValue(key, out var rec))
                {
                    _version++;
                    rec = (_version, manifestId, sha, DateTimeOffset.UtcNow);
                    _committed[key] = rec;
                }
                if (DropNextAck)
                {
                    DropNextAck = false;
                    throw new HttpRequestException("Simulated lost ACK.");
                }
                return OkView(rec.ManifestId, rec.Sha, rec.Version, rec.ActivatedAt);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage OkView(Guid manifestId, string sha, int version, DateTimeOffset activatedAt) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { artifactManifestId = manifestId, thresholdConfigSha256 = sha, version, activatedAt })
            };
    }
}
