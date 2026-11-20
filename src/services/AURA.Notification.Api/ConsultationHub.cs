using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace AURA.Notification.Api;

public sealed record ConsultationConnectionSession(
    HubCallerContext Context,
    Guid AccountId,
    DateTimeOffset TokenExpiresAt,
    ConcurrentDictionary<Guid, ConsultationConversation> Conversations);

public sealed class ConsultationConnectionRegistry
{
    private const int MaximumConnections = 5000;
    private readonly ConcurrentDictionary<string, ConsultationConnectionSession> _connections = new(StringComparer.Ordinal);

    public bool TryRegister(HubCallerContext context, Guid accountId, DateTimeOffset tokenExpiresAt)
    {
        if (_connections.Count >= MaximumConnections) return false;
        return _connections.TryAdd(context.ConnectionId, new ConsultationConnectionSession(
            context, accountId, tokenExpiresAt, new ConcurrentDictionary<Guid, ConsultationConversation>()));
    }

    public bool TryGet(string connectionId, out ConsultationConnectionSession session) => _connections.TryGetValue(connectionId, out session!);
    public IReadOnlyList<KeyValuePair<string, ConsultationConnectionSession>> Snapshot() => _connections.ToArray();
    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);
}

public sealed class ConsultationHub(
    ConsultationService service,
    ConsultationConnectionRegistry connections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var uid = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var expiryText = Context.User?.FindFirstValue("firebase_token_exp");
        if (string.IsNullOrWhiteSpace(uid) || !long.TryParse(expiryText, out var expiry))
        {
            Context.Abort();
            return;
        }
        var tokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry);
        if (tokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            Context.Abort();
            return;
        }
        try
        {
            var accountId = await service.ResolveCurrentAccountAsync(uid, Context.ConnectionAborted);
            if (!connections.TryRegister(Context, accountId, tokenExpiresAt))
            {
                Context.Abort();
                return;
            }
            await base.OnConnectedAsync();
        }
        catch
        {
            Context.Abort();
        }
    }

    public async Task JoinConversation(Guid conversationId)
    {
        var accountId = CurrentAccountId();
        ConsultationConversation conversation;
        try
        {
            conversation = await service.AuthorizeConversationAsync(conversationId, accountId, "CONSULTATION_JOIN", Context.ConnectionAborted);
        }
        catch (ConsultationException exception) { throw new HubException(exception.Code); }
        catch (HttpRequestException) { throw new HubException("ERR_AUTHORITY_UNAVAILABLE"); }
        if (!connections.TryGet(Context.ConnectionId, out var session))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(conversationId, accountId), Context.ConnectionAborted);
        session.Conversations[conversationId] = conversation;
    }

    public async Task<ConsultationMessage?> SendMessage(Guid conversationId, Guid clientMessageId, string text)
    {
        _ = RequireJoined(conversationId);
        try
        {
            var sent = await service.SendAsync(
                Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!, conversationId,
                new ConsultationMessageCreateRequest(clientMessageId, text), Context.ConnectionAborted);
            return sent.Message;
        }
        catch (ConsultationException exception) { throw new HubException(exception.Code); }
        catch (HttpRequestException) { throw new HubException("ERR_AUTHORITY_UNAVAILABLE"); }
    }

    public async Task MarkRead(Guid conversationId, Guid lastReadMessageId)
    {
        _ = RequireJoined(conversationId);
        try
        {
            await service.MarkReadAsync(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!, conversationId,
                new ConsultationReadRequest(lastReadMessageId), Context.ConnectionAborted);
        }
        catch (ConsultationException exception) { throw new HubException(exception.Code); }
        catch (HttpRequestException) { throw new HubException("ERR_AUTHORITY_UNAVAILABLE"); }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private Guid CurrentAccountId() => connections.TryGet(Context.ConnectionId, out var session)
        ? session.AccountId
        : throw new HubException("ERR_UNAUTHORIZED");

    private Guid RequireJoined(Guid conversationId) =>
        connections.TryGet(Context.ConnectionId, out var session) && session.Conversations.ContainsKey(conversationId)
            ? session.AccountId
            : throw new HubException("ERR_CONVERSATION_NOT_JOINED");

    internal static string GroupName(Guid conversationId, Guid accountId) =>
        $"consultation:{conversationId:N}:account:{accountId:N}";
}

public sealed class ConsultationRealtimePublisher(
    IHubContext<ConsultationHub> hub,
    ConsultationConnectionRegistry connections,
    ILogger<ConsultationRealtimePublisher> logger) : IConsultationRealtimePublisher
{
    public Task PublishMessageAsync(Guid conversationId, Guid accountId, ConsultationMessage message, CancellationToken ct) =>
        hub.Clients.Group(ConsultationHub.GroupName(conversationId, accountId)).SendAsync("MessagePersisted", new MessagePersistedEvent(
            "MessagePersisted", conversationId, message.MessageId, message.ClientMessageId,
            message.SenderAccountId, message.Text, message.SentAt), ct);

    public Task PublishReadCursorAsync(Guid conversationId, Guid accountId, Guid readerAccountId, ConsultationReadCursor cursor, CancellationToken ct) =>
        hub.Clients.Group(ConsultationHub.GroupName(conversationId, accountId)).SendAsync("ReadCursorAdvanced", new ReadCursorAdvancedEvent(
            "ReadCursorAdvanced", conversationId, readerAccountId, cursor.LastReadMessageId, cursor.ReadAt), ct);

    public async Task AccessRevokedAsync(Guid conversationId, Guid accountId, CancellationToken ct)
    {
        var groupName = ConsultationHub.GroupName(conversationId, accountId);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await hub.Clients.Group(groupName).SendAsync("AccessRevoked",
                new AccessRevokedEvent("AccessRevoked", conversationId, "ACCESS_REVOKED"), timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("AccessRevoked signal could not be published. Type: {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            foreach (var (connectionId, session) in connections.Snapshot())
            {
                if (session.AccountId != accountId || !session.Conversations.TryRemove(conversationId, out _)) continue;
                try { await hub.Groups.RemoveFromGroupAsync(connectionId, groupName, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning("Revoked SignalR group cleanup failed. Type: {ErrorType}", ex.GetType().Name); }
                finally { session.Context.Abort(); }
            }
        }
    }

    private sealed record MessagePersistedEvent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("conversationId")] Guid ConversationId,
        [property: JsonPropertyName("messageId")] Guid MessageId,
        [property: JsonPropertyName("clientMessageId")] Guid ClientMessageId,
        [property: JsonPropertyName("senderAccountId")] Guid SenderAccountId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("sentAt")] DateTimeOffset SentAt);

    private sealed record ReadCursorAdvancedEvent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("conversationId")] Guid ConversationId,
        [property: JsonPropertyName("accountId")] Guid AccountId,
        [property: JsonPropertyName("lastReadMessageId")] Guid LastReadMessageId,
        [property: JsonPropertyName("readAt")] DateTimeOffset ReadAt);

    private sealed record AccessRevokedEvent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("conversationId")] Guid ConversationId,
        [property: JsonPropertyName("code")] string Code);
}

public sealed class ConsultationConnectionMonitor(
    ConsultationConnectionRegistry connections,
    ConsultationService service,
    IConsultationRealtimePublisher realtime,
    ILogger<ConsultationConnectionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = connections.Snapshot();
            var now = DateTimeOffset.UtcNow;
            foreach (var (connectionId, session) in snapshot)
            {
                if (session.TokenExpiresAt <= now)
                {
                    connections.Remove(connectionId);
                    session.Context.Abort();
                }
            }

            var checks = snapshot
                .Where(entry => entry.Value.TokenExpiresAt > now)
                .SelectMany(entry => entry.Value.Conversations.ToArray()
                    .Select(conversation => CheckAccessAsync(conversation.Key, conversation.Value, entry.Value, stoppingToken)));
            await Task.WhenAll(checks);
        }
    }

    private async Task CheckAccessAsync(Guid conversationId, ConsultationConversation conversation,
        ConsultationConnectionSession session, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (!await service.HasCurrentAccessAsync(conversation, session.AccountId, timeout.Token))
                await realtime.AccessRevokedAsync(conversationId, session.AccountId, stoppingToken);
        }
        catch (Exception ex) when (ConsultationConnectionFailurePolicy.ShouldCloseForAuthorityFailure(ex, stoppingToken))
        {
            logger.LogWarning("Consultation connection closed because live authority could not be verified.");
            await realtime.AccessRevokedAsync(conversationId, session.AccountId, stoppingToken);
        }
    }
}

public static class ConsultationConnectionFailurePolicy
{
    public static bool ShouldCloseForAuthorityFailure(Exception exception, CancellationToken stoppingToken) =>
        exception is HttpRequestException or TimeoutException or IOException ||
        exception is OperationCanceledException && !stoppingToken.IsCancellationRequested;
}
