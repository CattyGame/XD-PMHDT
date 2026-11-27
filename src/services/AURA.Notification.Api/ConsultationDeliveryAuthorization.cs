using System.Text.Json;
using FirebaseAdmin.Messaging;

namespace AURA.Notification.Api;

public interface IConsultationDeliveryAuthorizer
{
    Task<bool> HasCurrentReadAccessAsync(Guid conversationId, Guid recipientAccountId, Guid tenantId,
        CancellationToken ct);
}

public sealed class ConsultationDeliveryAuthorizer(
    ConsultationRepository conversations,
    ConsultationService service) : IConsultationDeliveryAuthorizer
{
    public async Task<bool> HasCurrentReadAccessAsync(Guid conversationId, Guid recipientAccountId,
        Guid tenantId, CancellationToken ct)
    {
        if (conversationId == Guid.Empty || recipientAccountId == Guid.Empty || tenantId == Guid.Empty)
            return false;
        var conversation = await conversations.GetConversationAsync(conversationId, ct);
        if (conversation is null || conversation.TenantId != tenantId
            || (recipientAccountId != conversation.PatientAccountId && recipientAccountId != conversation.DoctorAccountId))
            return false;
        return await service.HasCurrentAccessAsync(conversation, recipientAccountId, "CONSULTATION_READ", ct);
    }
}

public static class ConsultationDeliveryGuard
{
    /// <summary>Performs the final current-authority check immediately before invoking the push provider.</summary>
    public static async Task<bool> SendIfAuthorizedAsync(
        Func<CancellationToken, Task<bool>> hasCurrentAccess,
        Func<CancellationToken, Task> sendToProvider,
        CancellationToken ct)
    {
        if (!await hasCurrentAccess(ct)) return false;
        await sendToProvider(ct);
        return true;
    }

    public static bool TryGetConversationId(string metadata, out Guid conversationId)
    {
        conversationId = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("conversationId", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out conversationId)
                && conversationId != Guid.Empty;
        }
        catch (JsonException) { return false; }
    }
}

public sealed class ConsultationPushDispatcher(
    IConsultationDeliveryAuthorizer authorization,
    IFcmClient fcm)
{
    public async Task<FcmMulticastResult?> SendIfAuthorizedAsync(
        Guid conversationId, Guid recipientAccountId, Guid tenantId, Guid deviceId, string deviceToken,
        MulticastMessage message, CancellationToken ct)
    {
        FcmMulticastResult? result = null;
        var sent = await ConsultationDeliveryGuard.SendIfAuthorizedAsync(
            token => authorization.HasCurrentReadAccessAsync(conversationId, recipientAccountId, tenantId, token),
            async token => result = await fcm.SendMulticastAsync(
                [new ActiveDeviceToken(deviceId, deviceToken)], message, token),
            ct);
        return sent ? result : null;
    }
}
