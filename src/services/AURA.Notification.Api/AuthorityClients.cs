using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace AURA.Notification.Api;

public sealed class NotificationAuthorityClient(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<HttpResponseMessage> SendAsync(string audience,string scope,string path,object body,CancellationToken ct)
    {
        var identity=clients.CreateClient("IdentityAuthority");
        using var issued=await identity.PostAsJsonAsync("/internal/v1/identity/service-tokens",new {
            clientId=configuration["ServiceAuth:ClientId"],clientSecret=configuration["ServiceAuth:ClientSecret"],audience,scope},ct);
        issued.EnsureSuccessStatusCode();
        var token=await issued.Content.ReadFromJsonAsync<Token>(cancellationToken:ct);
        if(string.IsNullOrWhiteSpace(token?.AccessToken))throw new HttpRequestException("Missing service authority token.");
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body)};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.AccessToken);
        return await clients.CreateClient(audience=="aura-identity"?"IdentityAuthority":"AnalysisAuthority").SendAsync(request,ct);
    }
    private sealed record Token(string AccessToken);
}
public sealed record NotificationContext(Guid EventId,Guid AnalysisId,Guid TenantId,bool Applicable,IReadOnlyList<Guid> RecipientAccountIds,string ProcessingStatus,int? ResultVersion);
public interface INotificationContextResolver
{
    Task<NotificationContext> ResolveAsync(Guid eventId,Guid analysisId,Guid tenantId,CancellationToken ct);
}
public sealed class NotificationContextResolver(NotificationAuthorityClient client):INotificationContextResolver
{
    public async Task<NotificationContext> ResolveAsync(Guid eventId,Guid analysisId,Guid tenantId,CancellationToken ct)
    {
        using var response=await client.SendAsync("aura-analysis","analysis:notify","/internal/v1/analysis/notification-context/resolve",new{eventId,analysisId,tenantId},ct);
        response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<NotificationContext>(cancellationToken:ct);
        if(result is null || result.EventId!=eventId || result.AnalysisId!=analysisId || result.TenantId!=tenantId || result.RecipientAccountIds is null)
            throw new HttpRequestException("Notification authority returned mismatched context.");
        return result;
    }
}
