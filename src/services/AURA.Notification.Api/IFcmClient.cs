using FirebaseAdmin.Messaging;

namespace AURA.Notification.Api;

public sealed record FcmSendOutcome(
    string DeviceToken,
    Guid DeviceId,
    bool IsSuccess,
    string? MessageId,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record FcmMulticastResult(
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<FcmSendOutcome> Responses);

public interface IFcmClient
{
    Task<FcmMulticastResult> SendMulticastAsync(
        IReadOnlyList<ActiveDeviceToken> devices,
        MulticastMessage message,
        CancellationToken ct);
}
