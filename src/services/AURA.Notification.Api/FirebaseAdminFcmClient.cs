using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace AURA.Notification.Api;

public sealed class FirebaseAdminFcmClient : IFcmClient
{
    private readonly FirebaseApp _firebaseApp;
    private readonly ILogger<FirebaseAdminFcmClient> _logger;

    public FirebaseAdminFcmClient(
        FirebaseApp firebaseApp,
        ILogger<FirebaseAdminFcmClient> logger)
    {
        _firebaseApp = firebaseApp;
        _logger = logger;
    }

    public async Task<FcmMulticastResult> SendMulticastAsync(
        IReadOnlyList<ActiveDeviceToken> devices,
        MulticastMessage message,
        CancellationToken ct)
    {
        var messaging = FirebaseMessaging.GetMessaging(_firebaseApp);
        var batchResponse = await messaging.SendEachForMulticastAsync(message, ct);

        var outcomes = new List<FcmSendOutcome>(batchResponse.Responses.Count);
        for (var i = 0; i < batchResponse.Responses.Count; i++)
        {
            var resp = batchResponse.Responses[i];
            var device = devices[i];

            if (resp.IsSuccess)
            {
                outcomes.Add(new FcmSendOutcome(
                    device.Token,
                    device.DeviceId,
                    true,
                    resp.MessageId,
                    null,
                    null));
            }
            else
            {
                var errorCode = resp.Exception?.MessagingErrorCode.ToString() ?? "Unknown";
                outcomes.Add(new FcmSendOutcome(
                    device.Token,
                    device.DeviceId,
                    false,
                    null,
                    errorCode,
                    resp.Exception?.Message));
            }
        }

        return new FcmMulticastResult(
            batchResponse.SuccessCount,
            batchResponse.FailureCount,
            outcomes);
    }
}
