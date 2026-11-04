using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AURA.Billing.Api.Auth;

public sealed record BillingOwnerAuthorization(bool Allowed, Guid AccountId, Guid TenantId, int StatusCode, string Code);
/// <summary>Every public wallet/order operation binds the verified user to server-resolved ownership.</summary>
public sealed class BillingOwnerAuthorizer(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<BillingOwnerAuthorization> AuthorizeAsync(HttpRequest request,string ownerType,Guid ownerId,bool mutate,CancellationToken ct)
    {
        BillingOwnerAuthorization Deny(int status,string code)=>new(false,Guid.Empty,Guid.Empty,status,code);
        var values=request.Headers.Authorization;
        if(values.Count!=1 || !AuthenticationHeaderValue.TryParse(values[0],out var bearer) || !string.Equals(bearer.Scheme,"Bearer",StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(bearer.Parameter))return Deny(401,"ERR_UNAUTHORIZED");
        try
        {
            using var me=new HttpRequestMessage(HttpMethod.Get,"/api/v1/identity/me");me.Headers.Authorization=bearer;
            using var response=await clients.CreateClient("IdentityApi").SendAsync(me,ct);
            if(response.StatusCode==HttpStatusCode.Unauthorized)return Deny(401,"ERR_UNAUTHORIZED");
            if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)return Deny(403,"ERR_FORBIDDEN");
            if(!response.IsSuccessStatusCode)return Deny(503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");
            var identity=await response.Content.ReadFromJsonAsync<Identity>(cancellationToken:ct);
            if(identity?.AccountId is null || identity.AccountId==Guid.Empty || identity.AccountStatus!="ACTIVE")return Deny(403,"ERR_FORBIDDEN");
            using var issued=await clients.CreateClient("IdentityApi").PostAsJsonAsync("/internal/v1/identity/service-tokens",new
            {clientId=configuration["ServiceAuth:ClientId"],clientSecret=configuration["ServiceAuth:ClientSecret"],audience="aura-profile",scope="profile:resolve"},ct);
            if(!issued.IsSuccessStatusCode)return Deny(503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");
            var token=await issued.Content.ReadFromJsonAsync<Token>(cancellationToken:ct);
            if(string.IsNullOrWhiteSpace(token?.AccessToken))return Deny(503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");
            using var resolve=new HttpRequestMessage(HttpMethod.Post,"/internal/v1/profile/billing-owner/resolve")
            {Content=JsonContent.Create(new {actorAccountId=identity.AccountId,ownerType,ownerId,action=mutate?"BILLING_MANAGE":"BILLING_READ"})};
            resolve.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.AccessToken);
            using var resolved=await clients.CreateClient("ProfileApi").SendAsync(resolve,ct);
            if(!resolved.IsSuccessStatusCode)return Deny(503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");
            var decision=await resolved.Content.ReadFromJsonAsync<OwnerDecision>(cancellationToken:ct);
            if(decision?.Allowed!=true)return Deny(403,"ERR_FORBIDDEN");
            if(decision.TenantId==Guid.Empty)return Deny(503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");
            return new(true,identity.AccountId,decision.TenantId,200,"");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {return Deny(503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");}
    }
    public async Task<BillingOwnerAuthorization> AuthenticateAsync(HttpRequest request,CancellationToken ct)
    {
        var values=request.Headers.Authorization;
        if(values.Count!=1 || !AuthenticationHeaderValue.TryParse(values[0],out var bearer) || !string.Equals(bearer.Scheme,"Bearer",StringComparison.OrdinalIgnoreCase))return new(false,Guid.Empty,Guid.Empty,401,"ERR_UNAUTHORIZED");
        try
        {
            using var me=new HttpRequestMessage(HttpMethod.Get,"/api/v1/identity/me");me.Headers.Authorization=bearer;
            using var response=await clients.CreateClient("IdentityApi").SendAsync(me,ct);
            if(response.StatusCode==HttpStatusCode.Unauthorized)return new(false,Guid.Empty,Guid.Empty,401,"ERR_UNAUTHORIZED");
            if(!response.IsSuccessStatusCode)return new(false,Guid.Empty,Guid.Empty,response.StatusCode==HttpStatusCode.Forbidden?403:503,"ERR_FORBIDDEN");
            var identity=await response.Content.ReadFromJsonAsync<Identity>(cancellationToken:ct);
            return identity?.AccountStatus=="ACTIVE" && identity.AccountId!=Guid.Empty ? new(true,identity.AccountId,Guid.Empty,200,"") : new(false,Guid.Empty,Guid.Empty,403,"ERR_FORBIDDEN");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException){return new(false,Guid.Empty,Guid.Empty,503,"ERR_AUTH_DEPENDENCY_UNAVAILABLE");}
    }
    private sealed record Identity(Guid AccountId,string? AccountStatus);
    private sealed record Token(string? AccessToken);
    private sealed record OwnerDecision(bool Allowed,Guid TenantId);
}
