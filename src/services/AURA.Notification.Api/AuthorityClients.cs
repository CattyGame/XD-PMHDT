using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AURA.Notification.Api;

public sealed class NotificationAuthorityClient(IHttpClientFactory clients, IConfiguration configuration)
{
    public async Task<HttpResponseMessage> SendAsync(string audience, string scope, string path, object body, CancellationToken ct)
    {
        var identity = clients.CreateClient("IdentityAuthority");
        using var issued = await identity.PostAsJsonAsync("/internal/v1/identity/service-tokens", new
        {
            clientId = configuration["ServiceAuth:ClientId"],
            clientSecret = configuration["ServiceAuth:ClientSecret"],
            audience,
            scope
        }, ct);
        issued.EnsureSuccessStatusCode();
        var token = await issued.Content.ReadFromJsonAsync<Token>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(token?.AccessToken)) throw new HttpRequestException("Missing service authority token.");

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var clientName = audience switch
        {
            "aura-identity" => "IdentityAuthority",
            "aura-billing" => "BillingAuthority",
            "aura-profile" => "ProfileAuthority",
            _ => "AnalysisAuthority"
        };
        return await clients.CreateClient(clientName).SendAsync(request, ct);
    }

    private sealed record Token(string AccessToken);
}

public sealed record ConsultationAccessRequest(
    Guid ActorAccountId,
    Guid ClinicContextId,
    Guid AnalysisId,
    string Action,
    Guid? DoctorAccountId = null,
    Guid? PatientId = null);

public sealed record ConsultationDoctorAuthority(Guid AccountId, string DisplayName);

public sealed record ConsultationAccessDecision(
    bool Allowed,
    bool Revoked,
    string? Code,
    string? Reason,
    Guid? TenantId,
    Guid? ClinicContextId,
    Guid? PatientId,
    Guid? PatientAccountId,
    Guid? AnalysisId,
    string? ParticipantRole,
    Guid? DoctorAccountId,
    Guid? AssignmentId,
    int PermissionVersion,
    IReadOnlyList<ConsultationDoctorAuthority> EligibleDoctors);

public sealed class ConsultationAuthorityClient(NotificationAuthorityClient authorityClient)
{
    public async Task<ConsultationAccessDecision> ResolveAsync(
        ConsultationAccessRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await authorityClient.SendAsync(
            "aura-profile",
            "profile:resolve",
            "/internal/v1/profile/consultation-access/resolve",
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        ConsultationAccessDecision? decision;
        try
        {
            decision = await response.Content.ReadFromJsonAsync<ConsultationAccessDecision>(cancellationToken: cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new HttpRequestException("Profile consultation authority returned malformed JSON.");
        }
        if (decision is null ||
            (decision.Allowed && decision.Revoked) ||
            decision.ClinicContextId != request.ClinicContextId ||
            decision.AnalysisId != request.AnalysisId ||
            decision.DoctorAccountId != request.DoctorAccountId ||
            (request.PatientId is not null && decision.PatientId != request.PatientId) ||
            decision.PermissionVersion < 1 ||
            decision.EligibleDoctors is null)
        {
            throw new HttpRequestException("Profile consultation authority returned a mismatched or incomplete decision.");
        }

        if (decision.Allowed &&
            (decision.TenantId is null || decision.PatientId is null ||
             decision.PatientAccountId is null || decision.DoctorAccountId is null || decision.AssignmentId is null ||
             decision.ParticipantRole is not ("PATIENT" or "DOCTOR") ||
             decision.ParticipantRole == "PATIENT" && decision.PatientAccountId != request.ActorAccountId ||
             decision.ParticipantRole == "DOCTOR" && decision.DoctorAccountId != request.ActorAccountId))
        {
            throw new HttpRequestException("Profile consultation authority returned an incomplete grant.");
        }

        return decision;
    }
}

public sealed class NotificationContextRejectedException : HttpRequestException
{
    public NotificationContextRejectedException() : base("Notification context is not applicable.") { }
}

public sealed record NotificationContext(
    Guid EventId,
    Guid AnalysisId,
    Guid TenantId,
    bool Applicable,
    IReadOnlyList<Guid> RecipientAccountIds,
    string ProcessingStatus,
    int? ResultVersion,
    Guid? ClinicContextId = null);

public sealed record PaymentNotificationContext(
    Guid EventId,
    Guid OrderId,
    Guid TenantId,
    Guid RecipientAccountId,
    string Status);

public interface INotificationContextResolver
{
    Task<NotificationContext> ResolveAsync(Guid eventId, Guid analysisId, Guid tenantId, CancellationToken ct);
    Task<PaymentNotificationContext> ResolvePaymentAsync(Guid eventId, Guid orderId, Guid tenantId, CancellationToken ct);
}

public sealed class NotificationContextResolver(NotificationAuthorityClient client) : INotificationContextResolver
{
    public async Task<NotificationContext> ResolveAsync(Guid eventId, Guid analysisId, Guid tenantId, CancellationToken ct)
    {
        using var response = await client.SendAsync(
            "aura-analysis",
            "analysis:notify",
            "/internal/v1/analysis/notification-context/resolve",
            new { eventId, analysisId, tenantId },
            ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<NotificationContext>(cancellationToken: ct);
        if (result is null || result.EventId != eventId || result.AnalysisId != analysisId || result.TenantId != tenantId || result.RecipientAccountIds is null)
            throw new HttpRequestException("Notification authority returned mismatched context.");
        return result;
    }

    public async Task<PaymentNotificationContext> ResolvePaymentAsync(Guid eventId, Guid orderId, Guid tenantId, CancellationToken ct)
    {
        using var response = await client.SendAsync(
            "aura-billing", "billing:notify", "/internal/v1/billing/notification-context/resolve",
            new { eventId, orderId, tenantId }, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            throw new NotificationContextRejectedException();
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaymentNotificationContext>(cancellationToken: ct);
        if (result is null || result.EventId != eventId || result.OrderId != orderId || result.TenantId != tenantId ||
            result.RecipientAccountId == Guid.Empty || result.Status != "COMPLETED")
            throw new NotificationContextRejectedException();
        return result;
    }
}
