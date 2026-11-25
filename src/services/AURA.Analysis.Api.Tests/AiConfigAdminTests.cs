using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Http;
using AURA.Analysis.Api.Upload;
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

    private static async Task<(WebApplication App, HttpClient Client, AiConfigService Service, MutableSmokeVerifier Smoke)> StartHostAsync()
    {
        var smoke = new MutableSmokeVerifier();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAnalysisUserIdentityResolver>(new HeaderRoleIdentityResolver());
        builder.Services.AddSingleton<IAiManifestCatalog>(new VerifiedServerAiManifestCatalog());
        builder.Services.AddSingleton<IAiCandidateSmokeVerifier>(smoke);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AiConfigService>();

        var app = builder.Build();
        app.MapAnalysisEndpoints();
        await app.StartAsync();
        var client = app.GetTestClient();
        var service = app.Services.GetRequiredService<AiConfigService>();
        return (app, client, service, smoke);
    }

    private static HttpRequestMessage WithActor(HttpMethod method, string url, Guid accountId, string? role, object? body = null)
    {
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Add("X-Test-Account-Id", accountId.ToString("D"));
        if (role is not null) msg.Headers.Add("X-Test-Role", role);
        if (body is not null)
            msg.Content = JsonContent.Create(body);
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
        var (app, client, _, _) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        // 401 when AccountId == Guid.Empty
        using var unauthGet = await client.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", Guid.Empty, null));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthGet.StatusCode);

        using var unauthPut = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", Guid.Empty, null,
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "no auth" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthPut.StatusCode);

        // 403 for non-SYSTEM_ADMIN roles
        var actor = Guid.NewGuid();
        foreach (var role in new[] { "PATIENT", "DOCTOR", "CLINIC_ADMIN" })
        {
            using var getForbidden = await client.SendAsync(WithActor(HttpMethod.Get, "/api/v1/admin/ai/config", actor, role));
            Assert.Equal(HttpStatusCode.Forbidden, getForbidden.StatusCode);

            using var putForbidden = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", actor, role,
                new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "forbidden" }));
            Assert.Equal(HttpStatusCode.Forbidden, putForbidden.StatusCode);
        }

        // 200 for SYSTEM_ADMIN
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
        var (app, client, service, smoke) = await StartHostAsync();
        await using var _ = app;
        using var __ = client;

        var admin = Guid.NewGuid();

        // 400 on extra weightsPath / weightsUrl fields
        using var extraFields = await client.SendAsync(WithRawJson(
            "/api/v1/admin/ai/config",
            admin,
            "SYSTEM_ADMIN",
            $$"""{"expectedVersion":1,"artifactManifestId":"{{V13ManifestId:D}}","thresholdConfigSha256":"{{V13Sha}}","reason":"bad","weightsPath":"/tmp/evil.pt","weightsUrl":"https://evil.example/m.pt"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, extraFields.StatusCode);

        // 400 on invalid SHA-256 or blank reason
        using var badSha = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = "not-sha", reason = "bad sha" }));
        Assert.Equal(HttpStatusCode.BadRequest, badSha.StatusCode);

        using var blankReason = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "   " }));
        Assert.Equal(HttpStatusCode.BadRequest, blankReason.StatusCode);

        // 404 unknown manifest ID
        using var unknownId = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = Guid.Parse("99999999-9999-4999-8999-999999999999"), thresholdConfigSha256 = V13Sha, reason = "unknown" }));
        Assert.Equal(HttpStatusCode.NotFound, unknownId.StatusCode);

        // 409 stale expectedVersion
        using var staleVer = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 99, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "stale version" }));
        Assert.Equal(HttpStatusCode.Conflict, staleVer.StatusCode);

        // 409 SHA-256 mismatch
        using var mismatchSha = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = new string('0', 64), reason = "tampered hash" }));
        Assert.Equal(HttpStatusCode.Conflict, mismatchSha.StatusCode);

        // 503 smoke failure keeps version=1 unchanged
        smoke.Result = false;
        using var failedSmoke = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "smoke fails" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedSmoke.StatusCode);
        var stillActive = await service.GetActiveAsync();
        Assert.Equal(1, stillActive.Version);
        Assert.Equal(BaseManifestId, stillActive.ArtifactManifestId);

        // 200 activation succeeds when smoke passes
        smoke.Result = true;
        using var activatedResp = await client.SendAsync(WithActor(HttpMethod.Put, "/api/v1/admin/ai/config", admin, "SYSTEM_ADMIN",
            new { expectedVersion = 1, artifactManifestId = V13ManifestId, thresholdConfigSha256 = V13Sha, reason = "promote v1.3 verified" }));
        Assert.Equal(HttpStatusCode.OK, activatedResp.StatusCode);
        using var activatedDoc = JsonDocument.Parse(await activatedResp.Content.ReadAsStringAsync());
        Assert.Equal(2, activatedDoc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(V13ManifestId.ToString("D"), activatedDoc.RootElement.GetProperty("artifactManifestId").GetString());

        // Atomic rollback restores BaseManifestId at version=3 and records audit trail
        var rolledBack = await service.RollbackAsync(admin, expectedVersion: 2, reason: "rollback verified");
        Assert.Equal(3, rolledBack.Version);
        Assert.Equal(BaseManifestId, rolledBack.ArtifactManifestId);
        Assert.Equal(BaseSha, rolledBack.ThresholdConfigSha256);

        var audit = service.AuditTrail();
        Assert.Equal(2, audit.Count);
        Assert.Equal("ACTIVATE_MANIFEST", audit[0].Action);
        Assert.Equal(V13ManifestId, audit[0].ArtifactManifestId);
        Assert.Equal("ROLLBACK_MANIFEST", audit[1].Action);
        Assert.Equal(BaseManifestId, audit[1].ArtifactManifestId);
    }

    private sealed class MutableSmokeVerifier : IAiCandidateSmokeVerifier
    {
        public bool Result { get; set; } = true;
        public Task<bool> VerifyCandidateAsync(AiManifestDescriptor candidate, CancellationToken ct) => Task.FromResult(Result);
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
