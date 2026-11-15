using System.Net;
using System.Net.Http.Json;
using AURA.Profile.Api;
using Microsoft.Extensions.Configuration;

internal static class AuthorityChecks
{
    public static async Task RunAsync()
    {
        var handler = new Transport();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["ServiceAuth:ClientId"]="profile-api", ["ServiceAuth:ClientSecret"]="fixture-only-secret" }).Build();
        var authority = new AccountAuthority(new Clients(handler), config);
        var actor = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => authority.GetAsync(actor, default)));
        if (handler.Issues != 1 || handler.Contexts != 8) throw new Exception("Cache only service JWT, never account authority; suppress refresh stampede.");
        handler.Status="DISABLED";
        var disabled = await authority.GetAsync(actor, default);
        if (disabled?.AccountStatus!="DISABLED" || handler.Contexts!=9) throw new Exception("Account disable must be visible with the same cached service JWT.");
        config["ServiceAuth:ClientSecret"]="rotated-fixture-secret";
        await authority.GetAsync(actor, default);
        if (handler.Issues!=2) throw new Exception("Credential rotation must invalidate cached JWT.");
        config["ServiceAuth:ClientSecret"]="short-lived-fixture-secret";
        handler.Lifetime=1;
        await authority.GetAsync(actor,default);
        handler.FailIssue=true;
        try { await authority.GetAsync(actor,default); throw new Exception("Reused an expiring service token after issuance failure."); }
        catch(HttpRequestException) { }
        if(handler.Issues!=4 || handler.Contexts!=11) throw new Exception("Expired refresh must fail closed before context call.");
        Console.WriteLine("Authority PASS: singleton service JWT reuse; concurrent refresh; every call reads live account status; credential rotation.");
    }
    private sealed class Clients(HttpMessageHandler handler):IHttpClientFactory
    { public HttpClient CreateClient(string name)=>new(handler,false){BaseAddress=new Uri("https://identity.test")}; }
    private sealed class Transport:HttpMessageHandler
    {
        public int Issues; public int Contexts; public string Status="ACTIVE"; public int Lifetime=300; public bool FailIssue;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            object value;
            if(request.RequestUri!.AbsolutePath.EndsWith("/service-tokens"))
            {
                Interlocked.Increment(ref Issues);await Task.Delay(10,ct);
                if(FailIssue)return new(HttpStatusCode.ServiceUnavailable);
                value=new{accessToken="fixture-service-token",tokenType="Bearer",expiresIn=Lifetime};
            }
            else
            {
                Interlocked.Increment(ref Contexts);
                var id=Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[^2]);
                value=new{accountId=id,accountStatus=Status,roles=new[]{"PATIENT"}};
            }
            return new(HttpStatusCode.OK){Content=JsonContent.Create(value)};
        }
    }
}
