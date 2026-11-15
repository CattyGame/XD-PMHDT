using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AURA.Profile.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class HttpChecks
{
    public static async Task RunAsync(ProfileRepository repository,Guid owner,Guid stranger,Guid clinic,Guid staff,Guid patient)
    {
        var authority=new IdentityTransport { AccountId=owner };
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["ServiceAuth:ClientId"]="profile-api",["ServiceAuth:ClientSecret"]="test-only-boundary-secret" }).Build();
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(new UserIdentityResolver(new Clients(authority),config));
        await using var app=builder.Build();
        app.Use((context,next)=>{context.Items["CorrelationId"]="test-correlation";return next();});
        app.MapProfilePublicMembershipEndpoints();app.MapProfilePatientEndpoints();await app.StartAsync();
        var client=app.GetTestClient();var passed=0;
        void Verify(bool condition,string name){if(!condition)throw new InvalidOperationException("HTTP FAIL: "+name);Console.WriteLine("HTTP PASS: "+name);passed++;}
        async Task<HttpResponseMessage> Get(bool bearer)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"/api/v1/clinics");
            if(bearer)request.Headers.Authorization=new("Bearer","verified-fixture-token");
            request.Headers.Add("X-Actor-ID",owner.ToString());
            return await client.SendAsync(request);
        }
        using(var response=await Get(true))
        {
            var body=await response.Content.ReadFromJsonAsync<ClinicList>();
            Verify(response.StatusCode==HttpStatusCode.OK && body!.Items.Single().ClinicId==clinic,"Clinic discovery positive HTTP200");
        }
        using(var response=await Get(false))Verify(response.StatusCode==HttpStatusCode.Unauthorized,"Missing bearer denied despite forged actor header");
        authority.AccountId=stranger;
        using(var response=await Get(true))
        {
            var body=await response.Content.ReadFromJsonAsync<ClinicList>();
            Verify(response.StatusCode==HttpStatusCode.OK && body!.Items.Length==0,"Forged actor header cannot select another account's memberships");
        }
        async Task<HttpResponseMessage> Patient(HttpMethod method, string path, object? body = null)
        {
            using var request=new HttpRequestMessage(method,path);
            request.Headers.Authorization=new("Bearer","verified-fixture-token");
            if(body is not null)request.Content=JsonContent.Create(body);
            return await client.SendAsync(request);
        }
        var patientRoute=$"/api/v1/clinics/{clinic:D}/patients/{patient:D}";
        authority.AccountId=owner;
        using(var response=await Patient(HttpMethod.Get,patientRoute))
        {
            var value=await response.Content.ReadFromJsonAsync<JsonElement>();
            Verify(response.StatusCode==HttpStatusCode.OK && value.GetProperty("medicalHistory").ValueKind==JsonValueKind.Object,"Admin detail retains medical history positive control");
        }
        authority.AccountId=staff;
        foreach(var method in new[]{HttpMethod.Get,HttpMethod.Patch})
        using(var response=await Patient(method,patientRoute,method==HttpMethod.Patch?new{}:null))
        {
            var value=await response.Content.ReadFromJsonAsync<JsonElement>();
            Verify(response.StatusCode==HttpStatusCode.OK && value.GetProperty("medicalHistory").ValueKind==JsonValueKind.Null,$"Staff {method} never returns stored medical history");
        }
        using(var response=await Patient(HttpMethod.Patch,patientRoute,new{medicalHistory=new{notes="blocked"}}))
            Verify(response.StatusCode==HttpStatusCode.Forbidden,"Staff cannot write clinical history through PATCH");
        using(var response=await Patient(HttpMethod.Post,$"/api/v1/clinics/{clinic:D}/patients",new{fullName="staff fixture",medicalHistory=new{notes="blocked"}}))
            Verify(response.StatusCode==HttpStatusCode.Forbidden,"Staff cannot write clinical history through registration");
        using(var response=await Patient(HttpMethod.Post,$"/api/v1/clinics/{clinic:D}/patients",new{fullName="staff administrative fixture"}))
        {
            var value=await response.Content.ReadFromJsonAsync<JsonElement>();
            Verify(response.StatusCode==HttpStatusCode.Created && value.GetProperty("medicalHistory").ValueKind==JsonValueKind.Null,"Staff administrative registration remains available without PHI history");
        }
        await repository.RevokeClinicMemberAsync(clinic,staff);
        using(var response=await Patient(HttpMethod.Patch,patientRoute,new{}))
            Verify(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,"Revoked Staff loses PATCH immediately");
        var email=$"http-{Guid.NewGuid():N}@example.test";
        var invited=await repository.InviteClinicMemberAsync(clinic,email,"STAFF");
        var membership=invited.Result!;
        authority.AccountId=Guid.NewGuid();authority.Email=email;authority.EmailVerified=false;
        async Task<HttpResponseMessage> Accept(string token)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/clinics/invitations/{membership.MembershipId:D}/accept")
            {Content=JsonContent.Create(new {token})};
            request.Headers.Authorization=new("Bearer","verified-fixture-token");return await client.SendAsync(request);
        }
        using(var response=await Accept(membership.InvitationToken!))Verify(response.StatusCode==HttpStatusCode.Forbidden,"Unverified token email cannot accept invitation");
        authority.EmailVerified=true;
        using(var response=await Accept("invalid"))Verify(response.StatusCode==HttpStatusCode.BadRequest,"Wrong one-time token denied via actual HTTP endpoint");
        using(var response=await Accept(membership.InvitationToken!))Verify(response.StatusCode==HttpStatusCode.OK,"Verified email and valid token accept via actual HTTP endpoint");
        using(var response=await Accept(membership.InvitationToken!))Verify(response.StatusCode!=HttpStatusCode.OK,"Invitation replay denied via actual HTTP endpoint");
        Console.WriteLine($"Profile actual HTTP endpoints: {passed}/{passed} PASS; Identity transport is an explicit authoritative-boundary test double.");
    }
    private sealed record ClinicList(ActorClinic[] Items);
    private sealed class Clients(HttpMessageHandler handler):IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(handler,false){BaseAddress=new Uri("https://identity.test"),Timeout=TimeSpan.FromSeconds(5)};}
    private sealed class IdentityTransport:HttpMessageHandler
    {
        public Guid AccountId {get;set;}public string? Email {get;set;}public bool EmailVerified {get;set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            object payload=request.RequestUri!.AbsolutePath.EndsWith("/service-tokens",StringComparison.Ordinal)
                ?new {accessToken="test-scoped-service-token",tokenType="Bearer"}
                :new {accountId=AccountId,accountStatus="ACTIVE",roles=new[]{"PATIENT"},email=Email,emailVerified=EmailVerified};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")});
        }
    }
}
