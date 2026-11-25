namespace AURA.Notification.Api;

public sealed class NotificationDeliveryWorker(
    NotificationDeliveryJobRepository jobs,
    DeviceRegistrationRepository devices,
    IAccountResolver accounts,
    IFcmClient fcm,
    PushTemplates templates,
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
            var template = await templates.ForEventAsync(job.Type, timeout.Token);
            if (template is { Enabled: false }) { await FailAsync("TEMPLATE_DISABLED", false); return; }
            var (title, body) = PushTemplates.Render(template);
            var uid = await accounts.ResolveFirebaseUidAsync(job.AccountId, timeout.Token);
            if (string.IsNullOrWhiteSpace(uid)) { await FailAsync("ACCOUNT_INACTIVE", false); return; }
            var token = await devices.GetTokenAsync(_projectId, uid, job.DeviceId, timeout.Token);
            if (string.IsNullOrWhiteSpace(token)) { await FailAsync("DEVICE_REMOVED", false); return; }

            var origin = new Uri(configuration["Notification:PublicBaseUrl"] ?? "http://localhost:5173");
            if ((origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback)) ||
                origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                throw new InvalidOperationException("Invalid public notification origin.");
            if (!job.TargetPath.StartsWith('/') || job.TargetPath.StartsWith("//", StringComparison.Ordinal) ||
                job.TargetPath.Contains('\\') || !Uri.TryCreate(origin, job.TargetPath, out var target) ||
                target.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
            {
                await FailAsync("INVALID_TARGET_PATH", false);
                return;
            }
            var message = new FirebaseAdmin.Messaging.MulticastMessage
            {
                Tokens = [token],
                Notification = new() { Title = title, Body = body },
                Data = new Dictionary<string, string>
                {
                    ["notificationId"] = job.InboxItemId.ToString("D"),
                    ["targetPath"] = job.TargetPath,
                    ["url"] = job.TargetPath
                },
                Webpush = new()
                {
                    Notification = new() { Title = title, Body = body, Tag = $"notification-{job.InboxItemId:D}" },
                    FcmOptions = new() { Link = target.AbsoluteUri }
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
