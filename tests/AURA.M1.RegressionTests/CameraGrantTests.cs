using AURA.Profile.Api;
using AURA.Profile.Api.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AURA.M1.RegressionTests;

public class CameraGrantTests
{
    [M1PostgresFact]
    public async Task Camera_grant_binds_signed_identity_and_current_clinic_actor()
    {
        await using var db = await Database.CreateAsync();
        var actor = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var repository = new ProfileRepository(db.Profile, new Accounts(actor));
        var clinic = await repository.RegisterClinicAsync(actor, new("Camera fixture", null, null, null, null));
        await db.SqlAsync("UPDATE profile.clinics SET status='ACTIVE' WHERE clinic_id=$1", clinic.ClinicId);
        await db.SqlAsync("INSERT INTO profile.patients(patient_id,tenant_id,owner_principal_id,full_name) VALUES($1,$2,$3,'Fixture patient')",
            patient, clinic.TenantId, clinic.ClinicId);
        await db.SqlAsync("""
            INSERT INTO profile.camera_grants(service_principal_id,client_id,device_id,patient_id,tenant_id,actor_account_id,expires_at)
            VALUES($1,'camera-fixture','device-1',$2,$3,$4,now()+interval '1 hour')
            """, principal, patient, clinic.TenantId, actor);
        var request = new CameraDelegationResolveRequest(Guid.Empty, patient, principal, "camera-fixture", "device-1", clinic.TenantId, "ANALYSIS_CREATE", clinic.ClinicId);
        var allowed = await repository.ResolveCameraDelegationAsync(request);
        Assert.True(allowed.Allowed);
        Assert.Equal(actor, allowed.ActorAccountId);
        Assert.Equal(clinic.ClinicId, allowed.OwnerPrincipalId);
        Assert.Equal("CLINIC", allowed.BillingOwner!.Type);
        Assert.False((await repository.ResolveCameraDelegationAsync(request with { ServicePrincipalId = Guid.NewGuid() })).Allowed);
        Assert.False((await repository.ResolveCameraDelegationAsync(request with { ExpectedTenantId = Guid.NewGuid() })).Allowed);
        Assert.False((await repository.ResolveCameraDelegationAsync(request with { ClinicContextId = Guid.NewGuid() })).Allowed);
        Assert.False((await repository.ResolveCameraDelegationAsync(request with { ClinicContextId = null })).Allowed);
        Assert.False((await repository.ResolveCameraDelegationAsync(request with { DeviceId = "device-2" })).Allowed);
        Assert.False((await repository.ResolveCameraDelegationAsync(request with { ActorAccountId = Guid.NewGuid() })).Allowed);

        using var key = RSA.Create(2048);
        var signing = new RsaSecurityKey(key)
        {
            KeyId = "aura-local-" + Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16]
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuth:Issuer"] = "camera-fixture", ["ServiceAuth:Audience"] = "aura-profile",
            ["ServiceAuth:PublicKeyPem"] = key.ExportSubjectPublicKeyInfoPem()
        }).Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(sp => new ServiceTokenValidator(config, sp.GetRequiredService<ILogger<ServiceTokenValidator>>()));
        await using var app = builder.Build();
        app.MapCameraDelegation();
        await app.StartAsync();
        using var client = app.GetTestClient();
        async Task<HttpResponseMessage> Send(Guid subject, string scope = "profile:camera", string audience = "aura-profile",
            bool expired = false, Guid? claimedPrincipal = null)
        {
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken("camera-fixture", audience,
                [new Claim("sub", subject.ToString("D")), new Claim("client_id", "camera-fixture"), new Claim("scope", scope)],
                now.AddMinutes(-5), expired ? now.AddMinutes(-2) : now.AddMinutes(2),
                new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
            using var http = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/camera-delegation/resolve")
            {
                Content = JsonContent.Create(request with { ServicePrincipalId = claimedPrincipal ?? Guid.Empty, ClientId = "" })
            };
            http.Headers.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
            return await client.SendAsync(http);
        }
        using (var accepted = await Send(principal))
            Assert.True((await accepted.Content.ReadFromJsonAsync<CameraDelegationResolveResponse>())!.Allowed);
        using (var forged = await Send(Guid.NewGuid(), claimedPrincipal: principal))
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using (var wrongScope = await Send(principal, scope: "profile:resolve"))
            Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        using (var wrongAudience = await Send(principal, audience: "aura-analysis"))
            Assert.Equal(HttpStatusCode.Forbidden, wrongAudience.StatusCode);
        using (var expired = await Send(principal, expired: true))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        await db.SqlAsync("UPDATE profile.camera_grants SET status='REVOKED' WHERE patient_id=$1", patient);
        using var revoked = await Send(principal);
        Assert.False((await revoked.Content.ReadFromJsonAsync<CameraDelegationResolveResponse>())!.Allowed);
    }

    private sealed class Accounts(Guid actor) : IAccountAuthority
    {
        public Task<AccountAuthoritySnapshot?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<AccountAuthoritySnapshot?>(id == actor ? new(actor, "ACTIVE", ["PATIENT"]) : null);
    }
}
