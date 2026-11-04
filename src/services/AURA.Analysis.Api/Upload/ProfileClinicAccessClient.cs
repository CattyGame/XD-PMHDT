using System.Net.Http.Json;

namespace AURA.Analysis.Api.Upload;

public sealed record ClinicAccessRequest(Guid ActorAccountId, Guid ClinicId, string Action);
public sealed record ClinicAccessDecision(bool Allowed, Guid TenantId, string? Role, string? Reason = null);
public interface IClinicAccessResolver
{
    Task<ClinicAccessDecision> ResolveAsync(ClinicAccessRequest request, CancellationToken ct);
}
public sealed class ProfileClinicAccessClient(HttpClient http, IServiceTokenProvider tokens) : IClinicAccessResolver
{
    public async Task<ClinicAccessDecision> ResolveAsync(ClinicAccessRequest request, CancellationToken ct)
    {
        try
        {
            using var message=new HttpRequestMessage(HttpMethod.Post,"/internal/v1/profile/clinic-access/resolve") { Content=JsonContent.Create(request) };
            ServiceHttp.SetBearer(message,await tokens.GetAsync("aura-profile","profile:resolve",ct));
            using var response=await http.SendAsync(message,ct);
            if(!response.IsSuccessStatusCode)throw UploadFailure.Dependency("Profile");
            var result=await response.Content.ReadFromJsonAsync<ClinicAccessDecision>(cancellationToken:ct);
            if(result is null || (result.Allowed && result.TenantId==Guid.Empty))throw UploadFailure.Dependency("Profile");
            return result;
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(UploadFailure){throw;}
        catch(Exception e)when(e is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {throw UploadFailure.Dependency("Profile");}
    }
}
