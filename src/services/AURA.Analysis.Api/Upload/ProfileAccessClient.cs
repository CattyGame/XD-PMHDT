using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AURA.Analysis.Api.Upload;

/// <summary>Resolves access using Analysis's own service identity; RequesterId is an authoritative account ID.</summary>
public sealed class ProfileAccessClient(HttpClient http, IServiceTokenProvider tokens) : IPatientAccessResolver
{
    public async Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.RequesterId == Guid.Empty || request.PatientId == Guid.Empty
            || request.ClinicId == Guid.Empty || request.Action is not ("ANALYSIS_CREATE" or "ANALYSIS_READ" or "FILE_READ" or "DOCTOR_REVIEW"))
            throw UploadFailure.Dependency("Profile");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/access/resolve")
            {
                Content = JsonContent.Create(new
                {
                    actorAccountId = request.RequesterId, request.PatientId,
                    clinicContextId = request.ClinicId,
                    action = request.Action == "FILE_READ" ? "ANALYSIS_FILE_READ" : request.Action,
                    analysisId = request.AnalysisId
                })
            };
            ServiceHttp.SetBearer(message, await tokens.GetAsync("aura-profile", "profile:resolve", ct, request.CorrelationId));
            ServiceHttp.AddCorrelation(message, request.CorrelationId);
            using var response = await http.SendAsync(message, ct);
            if (response.StatusCode != HttpStatusCode.OK) throw UploadFailure.Dependency("Profile");
            var dto = await response.Content.ReadFromJsonAsync<ResolveReply>(cancellationToken: ct);
            if (dto?.Allowed is null) throw UploadFailure.Dependency("Profile");
            if (dto.Allowed == false) return new(false, Guid.Empty, "", Guid.Empty, "", dto.PermissionVersion);
            if (dto.TenantId is null || dto.TenantId == Guid.Empty || dto.OwnerPrincipalId is null
                || dto.OwnerPrincipalId == Guid.Empty || dto.PermissionVersion < 1 || dto.BillingOwner is null
                || dto.BillingOwner.Id == Guid.Empty || dto.BillingOwner.Id != dto.OwnerPrincipalId
                || !((dto.TenantKind == "PERSONAL" && dto.BillingOwner.Type == "ACCOUNT")
                     || (dto.TenantKind == "CLINIC" && dto.BillingOwner.Type == "CLINIC")))
                throw UploadFailure.Dependency("Profile");
            return new(true, dto.TenantId.Value, dto.TenantKind!, dto.OwnerPrincipalId.Value,
                $"{dto.BillingOwner.Type}:{dto.BillingOwner.Id:D}", dto.PermissionVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            throw UploadFailure.Dependency("Profile");
        }
    }

    private sealed record Owner(string? Type, Guid Id);
    private sealed record ResolveReply(bool? Allowed, Guid? TenantId, string? TenantKind,
        Guid? OwnerPrincipalId, Owner? BillingOwner, long PermissionVersion);
}
