using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AURA.Analysis.Api.Upload;

public interface ICameraDelegation
{
    Task<CameraGrant> ResolveAsync(AuthenticationHeaderValue bearer, Guid patient, Guid tenant,
        Guid? clinic, string device, CancellationToken ct);
}

public sealed record CameraGrant(bool Allowed, Guid? ActorAccountId, Guid? PatientId, Guid? TenantId,
    string? TenantKind, Guid? OwnerPrincipalId, CameraBillingOwner? BillingOwner, Guid? GrantId, Guid? ClinicContextId);

public sealed record CameraBillingOwner(string Type, Guid Id);

public sealed class CameraDelegationClient(HttpClient profile) : ICameraDelegation
{
    public async Task<CameraGrant> ResolveAsync(AuthenticationHeaderValue bearer, Guid patient, Guid tenant,
        Guid? clinic, string device, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/camera-delegation/resolve")
        {
            Content = JsonContent.Create(new { patientId = patient, expectedTenantId = tenant,
                clinicContextId = clinic, deviceId = device, action = "ANALYSIS_CREATE" })
        };
        request.Headers.Authorization = bearer;
        using var response = await profile.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Camera token is invalid or expired.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new UploadFailure(403, "ERR_FORBIDDEN", "Camera delegation is denied.");
        if (!response.IsSuccessStatusCode) throw UploadFailure.Dependency("Camera authority");
        var grant = await response.Content.ReadFromJsonAsync<CameraGrant>(cancellationToken: ct);
        if (grant?.Allowed == false) throw new UploadFailure(403, "ERR_FORBIDDEN", "Camera delegation is denied.");
        if (grant is null || grant.ActorAccountId is not { } actor || actor == Guid.Empty
            || grant.GrantId is not { } id || id == Guid.Empty || grant.PatientId != patient || grant.TenantId != tenant
            || grant.ClinicContextId != clinic || grant.OwnerPrincipalId != (clinic ?? actor)
            || grant.TenantKind != (clinic.HasValue ? "CLINIC" : "PERSONAL")
            || grant.BillingOwner?.Type != (clinic.HasValue ? "CLINIC" : "ACCOUNT") || grant.BillingOwner.Id != (clinic ?? actor))
            throw UploadFailure.Dependency("Camera authority response");
        return grant;
    }
}
