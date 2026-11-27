namespace AURA.Notification.Api;

public interface IConsultationRealtimePublisher
{
    Task PublishMessageAsync(Guid conversationId, Guid accountId, ConsultationMessage message, CancellationToken ct);
    Task PublishReadCursorAsync(Guid conversationId, Guid accountId, Guid readerAccountId, ConsultationReadCursor cursor, CancellationToken ct);
    Task AccessRevokedAsync(Guid conversationId, Guid accountId, CancellationToken ct);
}

public sealed class ConsultationException(int statusCode, string code, string message, bool retryable = false) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed record ConsultationSendResult(PersistConsultationMessageOutcome Outcome, ConsultationMessage? Message);

public sealed class ConsultationService(
    ConsultationRepository repository,
    ConsultationAuthorityClient authority,
    IAccountResolver accountResolver,
    IConsultationRealtimePublisher realtime,
    ILogger<ConsultationService> logger)
{
    public async Task<CreateConsultationResult> CreateAsync(
        string firebaseUid, ConsultationCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ValidateIds(request.AnalysisId, request.ClinicContextId, request.DoctorAccountId);
        var key = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw Error(400, "ERR_VALIDATION", "Header Idempotency-Key bắt buộc và dài tối đa 128 ký tự.");
        var accountId = await ResolveAccountAsync(firebaseUid, ct);
        var decision = await ResolveAsync(accountId, request.ClinicContextId, request.AnalysisId,
            "CONSULTATION_CREATE", request.DoctorAccountId, null, ct);
        RequireAllowed(decision);
        if (decision.TenantId is null || decision.PatientId is null || decision.PatientAccountId is null ||
            decision.DoctorAccountId != request.DoctorAccountId || decision.AssignmentId is null)
            throw Error(503, "ERR_AUTHORITY_UNAVAILABLE", "Profile chưa trả đủ dữ liệu thẩm quyền tư vấn.", true);

        var fingerprint = ConsultationMessageRules.Fingerprint($"{request.AnalysisId:D}\n{request.ClinicContextId:D}\n{request.DoctorAccountId:D}");
        return await repository.CreateConversationAsync(new CreateConsultationCommand(
            accountId, request.AnalysisId, request.ClinicContextId, decision.TenantId.Value, decision.PatientId.Value,
            decision.PatientAccountId.Value, decision.DoctorAccountId.Value, decision.AssignmentId.Value,
            key, fingerprint), ct);
    }

    public async Task<ConsultationConversationsPage> ListAsync(string firebaseUid, int? limit, string? cursor, CancellationToken ct)
    {
        var take = limit ?? 20;
        if (take is < 1 or > 100) throw Error(400, "ERR_VALIDATION", "limit phải nằm trong khoảng 1–100.");
        Guid? before = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!Guid.TryParse(cursor, out var parsed) || parsed == Guid.Empty)
                throw Error(400, "ERR_VALIDATION", "cursor không hợp lệ.");
            before = parsed;
        }

        var accountId = await ResolveAccountAsync(firebaseUid, ct);
        var visible = new List<ConsultationConversationResponse>();
        while (true)
        {
            var page = await repository.ListConversationsAsync(accountId, take, before, ct);
            for (var index = 0; index < page.Items.Count; index++)
            {
                var item = page.Items[index];
                var nextCursor = item.Conversation.ConversationId.ToString("D");
                var decision = await ResolveForConversationAsync(item.Conversation, accountId, "CONSULTATION_READ", ct);
                if (decision.Allowed)
                    visible.Add(ToResponse(item.Conversation, item.UnreadCount));
                if (visible.Count == take)
                {
                    var hasMore = index < page.Items.Count - 1 || page.NextCursor is not null;
                    return new ConsultationConversationsPage(visible, hasMore ? nextCursor : null);
                }
            }
            if (page.NextCursor is null)
                return new ConsultationConversationsPage(visible, null);
            before = Guid.Parse(page.NextCursor);
        }
    }

    public async Task<ConsultationMessagesPage> GetMessagesAsync(
        string firebaseUid, Guid conversationId, int? limit, Guid? beforeId, CancellationToken ct)
    {
        var accountId = await ResolveAccountAsync(firebaseUid, ct);
        var conversation = await RequireConversationAsync(conversationId, ct);
        await AuthorizeAsync(conversation, accountId, "CONSULTATION_READ", ct);
        var take = limit ?? 20;
        if (take is < 1 or > 100) throw Error(400, "ERR_VALIDATION", "limit phải nằm trong khoảng 1–100.");
        if (beforeId == Guid.Empty) throw Error(400, "ERR_VALIDATION", "beforeId không hợp lệ.");
        return await repository.GetMessagesAsync(conversationId, accountId, take, beforeId, ct);
    }

    public async Task<ConsultationSendResult> SendAsync(
        string firebaseUid, Guid conversationId, ConsultationMessageCreateRequest request, CancellationToken ct)
    {
        if (request.ClientMessageId == Guid.Empty || !ConsultationMessageRules.IsValidText(request.Text))
            throw Error(400, "ERR_VALIDATION", "Tin nhắn cần có clientMessageId và chứa 1–4000 ký tự, tối đa 16 KiB UTF-8.");
        var accountId = await ResolveAccountAsync(firebaseUid, ct);
        var conversation = await RequireConversationAsync(conversationId, ct);
        await AuthorizeAsync(conversation, accountId, "CONSULTATION_SEND", ct);

        var fingerprint = ConsultationMessageRules.Fingerprint(request.Text);
        var persisted = await repository.PersistMessageAsync(
            conversationId, accountId, request.ClientMessageId, request.Text, fingerprint, ct);
        if (persisted.Outcome == PersistConsultationMessageOutcome.PayloadConflict)
            throw Error(409, "ERR_CONFLICT", "clientMessageId đã được dùng với nội dung khác.");
        if (persisted.Outcome == PersistConsultationMessageOutcome.RateLimited)
            throw Error(429, "ERR_RATE_LIMITED", "Đã đạt giới hạn 30 tin nhắn mỗi phút.");
        if (persisted.Outcome == PersistConsultationMessageOutcome.NotParticipant)
            throw Error(404, "ERR_NOT_FOUND", "Không tìm thấy cuộc hội thoại.");

        if (persisted.Outcome == PersistConsultationMessageOutcome.Created && persisted.Message is not null)
        {
            var senderFailure = await PublishToAuthorizedParticipantsAsync(conversation, persisted.Message, ct);
            if (senderFailure is not null) throw senderFailure;
        }
        return new ConsultationSendResult(persisted.Outcome, persisted.Message);
    }

    public async Task<(AdvanceConsultationReadOutcome Outcome, ConsultationReadCursor? Cursor)> MarkReadAsync(
        string firebaseUid, Guid conversationId, ConsultationReadRequest request, CancellationToken ct)
    {
        if (request.LastReadMessageId == Guid.Empty) throw Error(400, "ERR_VALIDATION", "lastReadMessageId không hợp lệ.");
        var accountId = await ResolveAccountAsync(firebaseUid, ct);
        var conversation = await RequireConversationAsync(conversationId, ct);
        await AuthorizeAsync(conversation, accountId, "CONSULTATION_READ", ct);
        var result = await repository.AdvanceReadCursorAsync(conversationId, accountId, request.LastReadMessageId, ct);
        if (result.Outcome == AdvanceConsultationReadOutcome.InvalidMessage)
            throw Error(400, "ERR_VALIDATION", "lastReadMessageId không thuộc cuộc hội thoại.");
        if (result.Outcome == AdvanceConsultationReadOutcome.Advanced && result.Cursor is not null)
            await PublishReadToAuthorizedParticipantsAsync(conversation, accountId, result.Cursor, ct);
        return result;
    }

    public async Task<ConsultationConversation> AuthorizeConversationAsync(
        Guid conversationId, Guid accountId, string action, CancellationToken ct)
    {
        var conversation = await RequireConversationAsync(conversationId, ct);
        await AuthorizeAsync(conversation, accountId, action, ct);
        return conversation;
    }

    public async Task<Guid> ResolveCurrentAccountAsync(string firebaseUid, CancellationToken ct) =>
        await ResolveAccountAsync(firebaseUid, ct);

    public async Task<bool> HasCurrentAccessAsync(ConsultationConversation conversation, Guid accountId, CancellationToken ct)
        => await HasCurrentAccessAsync(conversation, accountId, "CONSULTATION_JOIN", ct);

    public async Task<bool> HasCurrentAccessAsync(ConsultationConversation conversation, Guid accountId,
        string action, CancellationToken ct)
    {
        if (accountId != conversation.PatientAccountId && accountId != conversation.DoctorAccountId)
            return false;
        var decision = await ResolveForConversationAsync(conversation, accountId, action, ct);
        return decision.Allowed;
    }

    private async Task<Guid> ResolveAccountAsync(string firebaseUid, CancellationToken ct)
    {
        var accountId = await accountResolver.ResolveAccountIdAsync(firebaseUid, ct);
        return accountId is Guid id && id != Guid.Empty
            ? id
            : throw Error(401, "ERR_UNAUTHORIZED", "Không thể xác định tài khoản đang hoạt động.");
    }

    private async Task<ConsultationConversation> RequireConversationAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) throw Error(400, "ERR_VALIDATION", "conversationId không hợp lệ.");
        return await repository.GetConversationAsync(id, ct)
            ?? throw Error(404, "ERR_NOT_FOUND", "Không tìm thấy cuộc hội thoại.");
    }

    private async Task AuthorizeAsync(ConsultationConversation conversation, Guid accountId, string action, CancellationToken ct)
    {
        if (accountId != conversation.PatientAccountId && accountId != conversation.DoctorAccountId)
            throw Error(404, "ERR_NOT_FOUND", "Không tìm thấy cuộc hội thoại.");
        var decision = await ResolveForConversationAsync(conversation, accountId, action, ct);
        RequireAllowed(decision);
    }

    private async Task<ConsultationAccessDecision> ResolveForConversationAsync(
        ConsultationConversation conversation, Guid accountId, string action, CancellationToken ct)
    {
        var decision = await ResolveAsync(accountId, conversation.ClinicContextId, conversation.AnalysisId,
            action, conversation.DoctorAccountId, conversation.PatientId, ct);
        if (decision.Allowed &&
            (decision.PatientAccountId != conversation.PatientAccountId ||
             decision.DoctorAccountId != conversation.DoctorAccountId ||
             decision.AssignmentId != conversation.AssignmentId ||
             decision.TenantId != conversation.TenantId ||
             decision.ParticipantRole != (accountId == conversation.PatientAccountId ? "PATIENT" : "DOCTOR")))
        {
            return decision with { Allowed = false, Revoked = true, Code = "ACCESS_REVOKED", Reason = "ASSIGNMENT_CHANGED" };
        }
        return decision;
    }

    private Task<ConsultationAccessDecision> ResolveAsync(
        Guid accountId, Guid clinicId, Guid analysisId, string action, Guid? doctorId, Guid? patientId, CancellationToken ct) =>
        authority.ResolveAsync(new ConsultationAccessRequest(accountId, clinicId, analysisId, action, doctorId, patientId), ct);

    private void RequireAllowed(ConsultationAccessDecision decision)
    {
        if (!decision.Allowed)
            throw Error(403, decision.Revoked ? "ERR_ACCESS_REVOKED" : "ERR_FORBIDDEN",
                "Không còn quyền truy cập cuộc hội thoại.");
    }

    private async Task<ConsultationException?> PublishToAuthorizedParticipantsAsync(
        ConsultationConversation conversation, ConsultationMessage message, CancellationToken ct)
    {
        ConsultationException? senderFailure = null;
        foreach (var recipient in new[] { conversation.PatientAccountId, conversation.DoctorAccountId }.Distinct())
        {
            try
            {
                var decision = await ResolveForConversationAsync(conversation, recipient, "CONSULTATION_READ", ct);
                if (decision.Allowed)
                    await realtime.PublishMessageAsync(conversation.ConversationId, recipient, message, ct);
                else
                {
                    await realtime.AccessRevokedAsync(conversation.ConversationId, recipient, ct);
                    if (recipient == message.SenderAccountId)
                        senderFailure = Error(403, "ERR_ACCESS_REVOKED", "Quyền tư vấn đã bị thu hồi.");
                }
            }
            catch (Exception ex) when (ConsultationConnectionFailurePolicy.ShouldCloseForAuthorityFailure(ex, ct))
            {
                logger.LogWarning("Consultation live delivery skipped because authority or transport was unavailable.");
                await realtime.AccessRevokedAsync(conversation.ConversationId, recipient, ct);
                if (recipient == message.SenderAccountId)
                    senderFailure = Error(503, "ERR_AUTHORITY_UNAVAILABLE", "Chưa xác minh được quyền gửi.", true);
            }
        }
        return senderFailure;
    }

    private async Task PublishReadToAuthorizedParticipantsAsync(
        ConsultationConversation conversation, Guid readerAccountId, ConsultationReadCursor cursor, CancellationToken ct)
    {
        foreach (var recipient in new[] { conversation.PatientAccountId, conversation.DoctorAccountId }.Distinct())
        {
            try
            {
                var decision = await ResolveForConversationAsync(conversation, recipient, "CONSULTATION_READ", ct);
                if (decision.Allowed)
                    await realtime.PublishReadCursorAsync(conversation.ConversationId, recipient, readerAccountId, cursor, ct);
                else
                    await realtime.AccessRevokedAsync(conversation.ConversationId, recipient, ct);
            }
            catch (Exception ex) when (ConsultationConnectionFailurePolicy.ShouldCloseForAuthorityFailure(ex, ct))
            {
                logger.LogWarning("Consultation read receipt delivery skipped because authority or transport was unavailable.");
                await realtime.AccessRevokedAsync(conversation.ConversationId, recipient, ct);
            }
        }
    }

    private static ConsultationConversationResponse ToResponse(ConsultationConversation conversation, int unreadCount) =>
        new(conversation.ConversationId, conversation.AnalysisId, conversation.ClinicContextId, conversation.Status, unreadCount);

    private static void ValidateIds(Guid analysisId, Guid clinicId, Guid doctorId)
    {
        if (analysisId == Guid.Empty || clinicId == Guid.Empty || doctorId == Guid.Empty)
            throw Error(400, "ERR_VALIDATION", "analysisId, clinicContextId và doctorAccountId phải là UUID hợp lệ.");
    }

    private static ConsultationException Error(int status, string code, string message, bool retryable = false) => new(status, code, message, retryable);
}
