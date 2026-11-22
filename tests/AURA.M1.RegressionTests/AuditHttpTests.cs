using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Common.Audit;
using AURA.Profile.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace AURA.M1.RegressionTests;

public class AuditHttpTests
{
    [M1PostgresFact]
    public async Task Actual_http_transport_requires_a_matching_committed_receipt()
    {
        await using var db=await Database.CreateAsync();await using var rig=await Rig.Start(db);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {["ServiceAuth:ClientSecret"]="fixture-secret"}).Build();
        var clients=new TransportClients(rig);
        var transport=new AuditHttpTransport(clients,config,AuditSource.Billing);
        var e=AuditEvent.Create(AuditSource.Billing,Guid.NewGuid(),AuditAction.PriceChanged,AuditResource.Package,"1","transport");
        await transport.SendAsync(e,default);await transport.SendAsync(e,default);
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        clients.BadReceipt=true;
        await Assert.ThrowsAsync<HttpRequestException>(()=>transport.SendAsync(e,default));
    }
    [M1PostgresFact]
    public async Task Real_signed_collector_auth_source_binding_and_replay_gate_precede_projection_writes()
    {
        await using var db = await Database.CreateAsync();
        await using var rig = await Rig.Start(db);
        var e = AuditEvent.Create(AuditSource.Billing, Guid.NewGuid(), AuditAction.PriceChanged, AuditResource.Package, "1", "correlation");
        async Task<HttpResponseMessage> Send(string? token, AuditEvent body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/audit-events")
            { Content = JsonContent.Create(body, options: AuditJson.Options) };
            if (token is not null) req.Headers.Authorization = new("Bearer", token);
            return await rig.Client.SendAsync(req);
        }
        using (var ok = await Send(rig.Token(), e)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var replay = await Send(rig.Token(), e)) Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var conflict = await Send(rig.Token(), e with { ResourceId = "2" })) Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var missing = await Send(null, e)) Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using (var wrongScope = await Send(rig.Token(scope: "profile:resolve"), e)) Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        using (var wrongAudience = await Send(rig.Token(audience: "aura-billing"), e)) Assert.Equal(HttpStatusCode.Forbidden, wrongAudience.StatusCode);
        using (var expired = await Send(rig.Token(expired: true), e)) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using (var forgedSource = await Send(rig.Token(client: "notification-api"), e)) Assert.Equal(HttpStatusCode.Forbidden, forgedSource.StatusCode);
        using var extra = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/audit-events")
        { Content = new StringContent(JsonSerializer.Serialize(e, AuditJson.Options)[..^1] + ",\"patient_name\":\"PRIVATE\"}", Encoding.UTF8, "application/json") };
        extra.Headers.Authorization = new("Bearer", rig.Token());
        using var rejected = await rig.Client.SendAsync(extra); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_projection"));
        await db.SqlAsync("""
            CREATE FUNCTION profile.audit_fixture_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-only collector storage failure'; END $$;
            CREATE TRIGGER audit_fixture_failure BEFORE INSERT ON profile.audit_projection FOR EACH ROW EXECUTE FUNCTION profile.audit_fixture_failure();
            """);
        var retry=e with {EventId=Guid.NewGuid()};
        using(var down=await Send(rig.Token(),retry))Assert.Equal(HttpStatusCode.ServiceUnavailable,down.StatusCode);
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_inbox"));
        await db.SqlAsync("DROP TRIGGER audit_fixture_failure ON profile.audit_projection");
        using(var recovered=await Send(rig.Token(),retry))Assert.Equal(HttpStatusCode.OK,recovered.StatusCode);
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM profile.audit_inbox"));
    }

    [M1PostgresFact]
    public async Task Admin_query_checks_current_roles_and_returns_hashes_not_resource_ids()
    {
        await using var db = await Database.CreateAsync();
        await using var rig = await Rig.Start(db);
        var e = AuditEvent.Create(AuditSource.Analysis, Guid.NewGuid(), AuditAction.ImageRead, AuditResource.Image,
            Guid.NewGuid().ToString("D"), "Bearer PRIVATE_TOKEN", outcome: AuditOutcome.Authorized);
        await new AuditProjection(db.Admin).AcceptAsync(e, default);
        using (var missing = await rig.Client.GetAsync("/api/v1/admin/audit-events")) Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        rig.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture-user");
        using (var ok = await rig.Client.GetAsync("/api/v1/admin/audit-events"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True(ok.Headers.CacheControl!.NoStore);
            var json = await ok.Content.ReadAsStringAsync();
            Assert.DoesNotContain(e.ResourceId, json); Assert.DoesNotContain("PRIVATE_TOKEN", json);
            var item = JsonDocument.Parse(json).RootElement.GetProperty("items")[0];
            Assert.Equal("analysis-api", item.GetProperty("sourceService").GetString());
            Assert.Equal("ALLOW", item.GetProperty("outcome").GetString());
            Assert.Equal(64, item.GetProperty("resourceIdHash").GetString()!.Length);
        }
        using (var bad = await rig.Client.GetAsync("/api/v1/admin/audit-events?limit=101")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        rig.Identity.Admin = false;
        rig.Client.DefaultRequestHeaders.Add("X-Role", "SYSTEM_ADMIN");
        using (var revoked = await rig.Client.GetAsync("/api/v1/admin/audit-events")) Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        rig.Identity.Unavailable = true;
        using var down = await rig.Client.GetAsync("/api/v1/admin/audit-events"); Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public WebApplication App = null!;
        public HttpClient Client = null!;
        public readonly IdentityFixture Identity = new();
        private readonly RSA key = RSA.Create(2048);
        public static async Task<Rig> Start(Database db)
        {
            var r = new Rig();
            var b = WebApplication.CreateBuilder(); b.WebHost.UseTestServer(); b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ServiceAuth:Issuer"]="audit-fixture",["ServiceAuth:Audience"]="aura-profile",
                ["ServiceAuth:PublicKeyPem"]=r.key.ExportSubjectPublicKeyInfoPem(),
                ["ServiceAuth:PublicKeyPath"]=null,["ServiceAuth:PreviousPublicKeyPem"]=null,["ServiceAuth:PreviousPublicKeyPath"]=null,
                ["ServiceAuth:ClientId"]="profile-api",["ServiceAuth:ClientSecret"]="test-only-not-a-real-secret"
            });
            b.Services.AddSingleton(db.Profile);
            b.Services.AddSingleton<AuditProjection>(); b.Services.AddSingleton<ServiceTokenValidator>();
            b.Services.AddSingleton<UserIdentityResolver>(); b.Services.AddSingleton<IHttpClientFactory>(new Clients(r.Identity));
            r.App=b.Build();r.App.MapAuditEndpoints();await r.App.StartAsync();r.Client=r.App.GetTestClient();return r;
        }
        public string Token(string audience="aura-profile",string scope="profile:audit",string client="billing-api",bool expired=false)
        {
            var security=new RsaSecurityKey(key){KeyId="aura-local-"+Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant()[..16]};
            var now=DateTime.UtcNow;
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("audit-fixture",audience,
                [new Claim("sub",Guid.NewGuid().ToString("D")),new Claim("client_id",client),new Claim("scope",scope)],
                now.AddMinutes(-10),expired?now.AddMinutes(-5):now.AddMinutes(2),new SigningCredentials(security,SecurityAlgorithms.RsaSha256)));
        }
        public async ValueTask DisposeAsync(){Client.Dispose();await App.DisposeAsync();key.Dispose();}
    }
    private sealed class Clients(IdentityFixture identity):IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(identity,false){BaseAddress=new Uri("https://identity.fixture"),Timeout=TimeSpan.FromSeconds(2)};}
    private sealed class TransportClients(Rig rig):IHttpClientFactory
    {
        public bool BadReceipt;
        public HttpClient CreateClient(string name)=> name=="AuditProfile" && !BadReceipt ? rig.App.GetTestClient()
            :new HttpClient(new IssueOrReceipt(rig,name=="AuditIdentity")){BaseAddress=new Uri("https://audit.fixture")};
    }
    private sealed class IssueOrReceipt(Rig rig,bool issue):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK){Content=issue?JsonContent.Create(new{accessToken=rig.Token(),tokenType="Bearer",expiresIn=300})
                :JsonContent.Create(new AuditReceipt(Guid.NewGuid(),new string('0',64)))});
    }
    private sealed class IdentityFixture:HttpMessageHandler
    {
        public bool Admin=true,Unavailable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            if(Unavailable)throw new HttpRequestException("test-only authority outage");
            object body=r.RequestUri!.AbsolutePath.EndsWith("service-tokens",StringComparison.Ordinal)
                ?new{accessToken="service-fixture",tokenType="Bearer",expiresIn=300}
                :new{accountId=Guid.Parse("11111111-1111-4111-8111-111111111111"),accountStatus="ACTIVE",roles=Admin?new[]{"SYSTEM_ADMIN"}:new[]{"PATIENT"}};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(body)});
        }
    }
}
