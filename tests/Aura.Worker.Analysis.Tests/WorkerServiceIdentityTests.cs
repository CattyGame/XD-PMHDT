using System.Net;
using System.Text;
using System.Text.Json;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Aura.Worker.Analysis.Tests;

public class WorkerServiceIdentityTests
{
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;return respond(request);}
    }

    [Fact]
    public async Task Missing_worker_credentials_fail_closed_without_ephemeral_signing_or_network()
    {
        var handler=new Handler(_=>throw new Exception("Should not call Identity without credentials."));
        var provider=new ServiceTokenProvider(new HttpClient(handler){BaseAddress=new Uri("http://identity.test")},Options.Create(new ServiceAuthOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>provider.GetTokenAsync("aura-ai","ai:analyze",default));
        Assert.Equal(0,handler.Calls);
    }

    [Fact]
    public async Task Service_token_is_requested_from_Identity_with_only_allowed_audience_scope()
    {
        var handler=new Handler(async request=> {
            Assert.Equal("/internal/v1/identity/service-tokens",request.RequestUri!.AbsolutePath);
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("analysis-worker",body.RootElement.GetProperty("clientId").GetString());
            Assert.Equal("aura-ai",body.RootElement.GetProperty("audience").GetString());
            return new(HttpStatusCode.OK){Content=new StringContent("{\"accessToken\":\"identity.signed.token\",\"tokenType\":\"Bearer\",\"expiresIn\":300}",Encoding.UTF8,"application/json")};
        });
        var provider=new ServiceTokenProvider(new HttpClient(handler){BaseAddress=new Uri("http://identity.test")},
            Options.Create(new ServiceAuthOptions{ClientSecret="unit-fixture-only"}));
        Assert.Equal("identity.signed.token",await provider.GetTokenAsync("aura-ai","ai:analyze",default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>provider.GetTokenAsync("aura-billing","billing:capture",default));
        Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task Profile_recheck_preserves_distinct_actor_patient_clinic_case_and_rejects_wrong_tenant()
    {
        var context=new WorkerAccessContext(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var handler=new Handler(async request=> {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var b=body.RootElement;
            Assert.Equal(context.ActorAccountId,b.GetProperty("actorAccountId").GetGuid());
            Assert.Equal(context.PatientId,b.GetProperty("patientId").GetGuid());
            Assert.Equal(context.ClinicContextId,b.GetProperty("clinicContextId").GetGuid());
            Assert.Equal(context.AnalysisId,b.GetProperty("analysisId").GetGuid());
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{allowed=true,tenantId=Guid.NewGuid()}),Encoding.UTF8,"application/json")};
        });
        var tokens=new Mock<IServiceTokenProvider>();tokens.Setup(p=>p.GetTokenAsync("aura-profile","profile:resolve",It.IsAny<CancellationToken>())).ReturnsAsync("service.token.fixture");
        var client=new ProfileResolveClient(new HttpClient(handler),Options.Create(new ProfileClientOptions()),tokens.Object,NullLogger<ProfileResolveClient>.Instance);
        var result=await client.RecheckAccessAsync(context,"ANALYSIS_READ",default);
        Assert.False(result.Allowed);
    }
}
