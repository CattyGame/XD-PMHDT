using System.Net.Http.Json;
namespace AURA.Notification.Api;
public interface IAccountResolver
{
    Task<Guid?> ResolveAccountIdAsync(string firebaseUid,CancellationToken ct);
    Task<string?> ResolveFirebaseUidAsync(Guid accountId,CancellationToken ct);
}
// Identity alone owns UID mapping and current account status. No cache or GUID shortcut.
public sealed class AccountResolver(NotificationAuthorityClient client):IAccountResolver
{
    private sealed record DirectoryEntry(Guid AccountId,string FirebaseUid,string AccountStatus);
    private async Task<DirectoryEntry?> ResolveAsync(object query,CancellationToken ct)
    {
        using var response=await client.SendAsync("aura-identity","identity:directory","/internal/v1/identity/directory/resolve",query,ct);
        if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;
        response.EnsureSuccessStatusCode();
        var entry=await response.Content.ReadFromJsonAsync<DirectoryEntry>(cancellationToken:ct);
        if(entry is null || entry.AccountId==Guid.Empty || string.IsNullOrWhiteSpace(entry.FirebaseUid))throw new HttpRequestException("Invalid Identity directory response.");
        return entry.AccountStatus=="ACTIVE"?entry:null;
    }
    public async Task<Guid?> ResolveAccountIdAsync(string firebaseUid,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(firebaseUid))return null;
        var entry=await ResolveAsync(new{firebaseUid},ct);
        if(entry is not null && entry.FirebaseUid!=firebaseUid)throw new HttpRequestException("Identity UID mismatch.");
        return entry?.AccountId;
    }
    public async Task<string?> ResolveFirebaseUidAsync(Guid accountId,CancellationToken ct)
    {
        if(accountId==Guid.Empty)return null;
        var entry=await ResolveAsync(new{accountId},ct);
        if(entry is not null && entry.AccountId!=accountId)throw new HttpRequestException("Identity account mismatch.");
        return entry?.FirebaseUid;
    }
}
