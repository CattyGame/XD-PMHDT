namespace AURA.Notification.Api;

public sealed class NotificationDeliveryWorker(
    NotificationDeliveryJobRepository jobs,
    DeviceRegistrationRepository devices,
    IAccountResolver accounts,
    IFcmClient fcm,
    IConfiguration configuration,
    ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    private readonly string _projectId = configuration["Firebase:ProjectId"] ?? "aura-dev-97716";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DispatchPendingAsync(stoppingToken) == 0)
                    await Task.Delay(1500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Delivery loop unavailable: {ErrorType}", ex.GetType().Name);
                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        // Start every claimed job immediately; bound network time below the lease duration.
        var batch = await jobs.ClaimPendingJobsAsync(4, TimeSpan.FromSeconds(30), ct);
        await Task.WhenAll(batch.Select(job => ProcessJobAsync(job, ct)));
        return batch.Count;
    }

    private async Task ProcessJobAsync(ClaimedDeliveryJob job, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var uid = await accounts.ResolveFirebaseUidAsync(job.AccountId, timeout.Token);
            if (string.IsNullOrWhiteSpace(uid)) { await FailAsync("ACCOUNT_INACTIVE", false); return; }
            var token = await devices.GetTokenAsync(_projectId, uid, job.DeviceId, timeout.Token);
            if (string.IsNullOrWhiteSpace(token)) { await FailAsync("DEVICE_REMOVED", false); return; }

            var origin = new Uri(configuration["Notification:PublicBaseUrl"] ?? "http://localhost:5173");
            if ((origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback)) ||
                origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new InvalidOperationException("Invalid public notification origin.");
            var message = new FirebaseAdmin.Messaging.MulticastMessage
            {
                Tokens = [token],
                Notification = new() { Title = job.Title, Body = "Có thông báo mới. Đăng nhập để xem chi tiết." },
                Data = new Dictionary<string, string>
                {
                    ["notificationId"] = job.InboxItemId.ToString("D"),
                    ["targetPath"] = job.TargetPath,
                    ["url"] = job.TargetPath
                },
                Webpush = new()
                {
                    Notification = new() { Title = job.Title, Body = "Có thông báo mới. Đăng nhập để xem chi tiết.", Tag = $"notification-{job.InboxItemId:D}" },
                    FcmOptions = new() { Link = new Uri(origin, job.TargetPath).AbsoluteUri }
                }
            };
            var result = await fcm.SendMulticastAsync([new ActiveDeviceToken(job.DeviceId, token)], message, timeout.Token);
            if (result.SuccessCount == 1 && result.FailureCount == 0)
                await jobs.CompleteJobAsync(job.Id, job.LeaseToken, ct);
            else
            {
                var code = result.Responses.FirstOrDefault(x => !x.IsSuccess)?.ErrorCode ?? "PROVIDER_FAILURE";
                var permanent = code is "Unregistered" or "InvalidArgument" or "SenderIdMismatch";
                if (code == "Unregistered")
                    await devices.DeleteIfTokenMatchesAsync(_projectId, uid, job.DeviceId, token, ct);
                await FailAsync(code, !permanent);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Delivery job {JobId} failed: {ErrorType}", job.Id, ex.GetType().Name);
            await FailAsync(ex is OperationCanceledException ? "PROVIDER_TIMEOUT" : "DEPENDENCY_UNAVAILABLE", true);
        }

        Task FailAsync(string code, bool transient) => jobs.FailJobAsync(job.Id, job.LeaseToken,
            code, transient, job.Attempts, job.MaxAttempts, ct);
    }
}
