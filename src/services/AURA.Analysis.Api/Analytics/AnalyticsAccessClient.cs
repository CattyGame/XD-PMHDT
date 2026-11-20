using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Analytics;

public sealed record AnalyticsAccessRequest(
    Guid ActorId,
    string Operation,
    string ResourceScope,
    Guid? ClinicId,
    Guid? PatientId,
    string? CorrelationId,
    string? Purpose = null,
    bool IncludeMasks = false,
    bool MasksConsentGranted = false);

public sealed record AnalyticsAccessGrant(
    Guid? TenantId,
    Guid? ClinicId,
    Guid? PatientId,
    IReadOnlyCollection<Guid>? AuthorizedPatientIds,
    long PermissionVersion,
    bool IncludeMasksAllowed = false);

public interface IAnalyticsAccessResolver
{
    Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct);
}

/// <summary>
/// Adapter calling Profile's versioned canonical operation authority endpoint for ANALYTICS_READ.
/// Fails closed with retryable 503 on dependency/contract failures.
/// </summary>
public sealed class ProfileAnalyticsAccessClient(HttpClient http, IServiceTokenProvider tokens) : IAnalyticsAccessResolver
{
    public async Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.ActorId == Guid.Empty || string.IsNullOrWhiteSpace(request.Operation))
        {
            throw UploadFailure.Dependency("Profile analytics authority");
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/profile/operations/resolve")
            {
                Content = JsonContent.Create(new
                {
                    actorAccountId = request.ActorId,
                    operation = request.Operation,
                    resourceScope = request.ResourceScope,
                    clinicId = request.ClinicId,
                    patientId = request.PatientId,
                    purpose = request.Purpose,
                    includeMasks = request.IncludeMasks,
                    masksConsentGranted = request.MasksConsentGranted
                })
            };

            ServiceHttp.SetBearer(message, await tokens.GetAsync("aura-profile", "profile:resolve", ct, request.CorrelationId));
            ServiceHttp.AddCorrelation(message, request.CorrelationId);

            using var response = await http.SendAsync(message, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw UploadFailure.Dependency("Profile analytics authority");
            }

            var reply = await response.Content.ReadFromJsonAsync<OperationAuthorityReply>(cancellationToken: ct);
            if (reply is null || reply.PermissionVersion < 1)
            {
                throw UploadFailure.Dependency("Profile analytics authority");
            }

            if (!reply.Allowed)
            {
                if (reply.Reason is "CLINIC_NOT_FOUND" or "PATIENT_NOT_FOUND" or "WRONG_CLINIC" or "NOT_A_MEMBER")
                {
                    throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên trong phạm vi được phép.");
                }

                if (reply.Reason == "INVALID_REQUEST")
                {
                    throw UploadFailure.Validation(new ErrorDetail("authority", "invalid_request"));
                }

                throw new UploadFailure(403, "ERR_FORBIDDEN", "Không có quyền thực hiện thao tác phân tích này.",
                    details: reply.Reason == "ROLE_DENIED" ? [new ErrorDetail("authority", "ROLE_DENIED")] : null);
            }

            return new AnalyticsAccessGrant(
                reply.TenantId,
                reply.ClinicId,
                reply.PatientId,
                reply.AuthorizedPatientIds,
                reply.PermissionVersion,
                reply.IncludeMasksAllowed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UploadFailure)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            throw UploadFailure.Dependency("Profile analytics authority");
        }
    }

    private sealed record OperationAuthorityReply(
        long PermissionVersion,
        bool Allowed,
        bool Revoked,
        string? Code,
        string? Reason,
        Guid? TenantId,
        Guid? ClinicId,
        Guid? PatientId,
        string? ParticipantRole,
        IReadOnlyList<Guid>? AuthorizedPatientIds,
        bool IncludeMasksAllowed);
}

/// <summary>
/// Explicit unavailable stub preserved for offline/unit test fixtures without HTTP composition.
/// </summary>
public sealed class AnalyticsAccessUnavailableResolver : IAnalyticsAccessResolver
{
    public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromException<AnalyticsAccessGrant>(UploadFailure.Dependency("Profile analytics authority"));
    }
}
