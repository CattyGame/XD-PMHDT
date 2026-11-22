using System.Security.Cryptography;
using System.Text;

namespace AURA.Notification.Api;

public sealed record ConsultationCreateRequest(Guid AnalysisId, Guid ClinicContextId, Guid DoctorAccountId);
public sealed record ConsultationMessageCreateRequest(Guid ClientMessageId, string Text);
public sealed record ConsultationReadRequest(Guid LastReadMessageId);

public sealed record ConsultationConversation(
    Guid ConversationId,
    Guid AnalysisId,
    Guid ClinicContextId,
    Guid TenantId,
    Guid PatientId,
    Guid PatientAccountId,
    Guid DoctorAccountId,
    Guid AssignmentId,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record ConsultationConversationResponse(
    Guid ConversationId,
    Guid AnalysisId,
    Guid ClinicContextId,
    string Status,
    int UnreadCount);

public sealed record ConsultationConversationsPage(
    IReadOnlyList<ConsultationConversationResponse> Items,
    string? NextCursor);

public sealed record ConsultationConversationListItem(
    ConsultationConversation Conversation,
    int UnreadCount,
    DateTimeOffset UpdatedAt);

public sealed record ConsultationConversationsPageData(
    IReadOnlyList<ConsultationConversationListItem> Items,
    string? NextCursor);

public sealed record ConsultationMessage(
    Guid MessageId,
    Guid ConversationId,
    Guid ClientMessageId,
    Guid SenderAccountId,
    string Text,
    DateTimeOffset SentAt,
    string DeliveryState);

public sealed record ConsultationMessagesPage(
    IReadOnlyList<ConsultationMessage> Items,
    Guid? NextBeforeId);

public sealed record ConsultationReadCursor(Guid LastReadMessageId, DateTimeOffset ReadAt, int Version);

public sealed record CreateConsultationCommand(
    Guid ActorAccountId,
    Guid AnalysisId,
    Guid ClinicContextId,
    Guid TenantId,
    Guid PatientId,
    Guid PatientAccountId,
    Guid DoctorAccountId,
    Guid AssignmentId,
    string IdempotencyKey,
    string RequestFingerprint);

public enum CreateConsultationOutcome { Created, Replay, PayloadConflict }
public sealed record CreateConsultationResult(CreateConsultationOutcome Outcome, ConsultationConversation Conversation);

public enum PersistConsultationMessageOutcome { Created, Replay, PayloadConflict, RateLimited, NotParticipant }
public sealed record PersistConsultationMessageResult(PersistConsultationMessageOutcome Outcome, ConsultationMessage? Message);

public enum AdvanceConsultationReadOutcome { Advanced, Unchanged, InvalidMessage }

public static class ConsultationMessageRules
{
    public static bool IsValidText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var characters = 0;
        foreach (var _ in text.EnumerateRunes()) characters++;
        return characters <= 4000 && Encoding.UTF8.GetByteCount(text) <= 16 * 1024;
    }

    public static string Fingerprint(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
