using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AURA.Notification.Api;
using Microsoft.Extensions.Configuration;
namespace AURA.Notification.InboxChecks;
public static class AuthorityBoundaryChecks
{
    public static async Task RunAsync(Action<bool,string,string> assert)
    {
        var account=Guid.NewGuid();var uid=Guid.NewGuid().ToString();
        var status="ACTIVE";var unavailable=false;var mismatch=false;var directoryCalls=0;var scoped=false;
        var eventId=Guid.NewGuid();var analysisId=Guid.NewGuid();var tenantId=Guid.NewGuid();
        var handler=new Handler(async request=>
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("service-tokens")){
                var body=await request.Content!.ReadAsStringAsync();
                scoped|=body.Contains("identity:directory");
                return new(HttpStatusCode.OK){Content=JsonContent.Create(new{accessToken="explicit-test-authority-token"})};
            }
            if(unavailable)return new(HttpStatusCode.ServiceUnavailable);
            if(request.Headers.Authorization?.Parameter!="explicit-test-authority-token")throw new Exception("Missing authority bearer");
            if(path.EndsWith("notification-context/resolve"))return new(HttpStatusCode.OK){Content=JsonContent.Create(new{eventId=mismatch?Guid.NewGuid():eventId,analysisId,tenantId,applicable=true,recipientAccountIds=new[]{account},processingStatus="COMPLETED",resultVersion=1})};
            directoryCalls++;
            if(status!="ACTIVE")return new(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{accountId=account,firebaseUid=uid,accountStatus=status})};
        });
        var factory=new Factory(handler);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ServiceAuth:ClientId","notification-api"},{"ServiceAuth:ClientSecret","explicit-test-secret"}}).Build();
        var client=new NotificationAuthorityClient(factory,config);var resolver=new AccountResolver(client);
        assert(await resolver.ResolveAccountIdAsync(uid,default)==account,"Identity: UUID-shaped UID must use canonical mapping","");
        assert(directoryCalls==1 && scoped,"Identity: mapping obtains identity:directory bearer","");
        status="SUSPENDED";
        assert(await resolver.ResolveAccountIdAsync(uid,default) is null && directoryCalls==2,"Identity: current suspension is not bypassed by cached mapping","");
        unavailable=true;var denied=false;try{await resolver.ResolveAccountIdAsync(uid,default);}catch(HttpRequestException){denied=true;}
        assert(denied,"Identity: outage throws, never GUID or SQL fallback","");
        unavailable=false;status="ACTIVE";
        var contexts=new NotificationContextResolver(client);
        assert((await contexts.ResolveAsync(eventId,analysisId,tenantId,default)).RecipientAccountIds.Single()==account,"Analysis: canonical notification context HTTP contract","");
        mismatch=true;denied=false;try{await contexts.ResolveAsync(eventId,analysisId,tenantId,default);}catch(HttpRequestException){denied=true;}
        assert(denied,"Analysis: cross-event authority response rejected","");
        unavailable=true;denied=false;try{await contexts.ResolveAsync(eventId,analysisId,tenantId,default);}catch(HttpRequestException){denied=true;}
        assert(denied,"Analysis: unavailable context preserves retry obligation","");
        assert(!new RabbitMqConsumerOptions().AutoStart && string.IsNullOrEmpty(new RabbitMqConsumerOptions().Password),"Broker: startup and credentials explicitly opt in","");
    }
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request);}
    private sealed class Factory(HttpMessageHandler handler):IHttpClientFactory
    {public HttpClient CreateClient(string name)=>new(handler,false){BaseAddress=new Uri("https://explicit-authority.test")};}
}
