using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AURA.Notification.Api;

public sealed record InboxItemResponse(
    [property: JsonPropertyName("notificationId")] Guid NotificationId,
    [property: JsonPropertyName("eventId")] Guid EventId,
    [property: JsonPropertyName("analysisId")] Guid? AnalysisId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isRead")] bool IsRead,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("readAt")] DateTimeOffset? ReadAt,
    [property: JsonPropertyName("targetPath")] string TargetPath,
    [property: JsonPropertyName("orderId")] Guid? OrderId = null);

public sealed record InboxListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<InboxItemResponse> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor);

public sealed record UnreadCountResponse(
    [property: JsonPropertyName("unreadCount")] int UnreadCount);

public sealed record SaveInboxItemCommand(
    Guid AccountId,
    Guid TenantId,
    Guid EventId,
    Guid? AnalysisId,
    string Type,
    string Title,
    string TargetPath,
    string? Metadata = null,
    Guid? OrderId = null);

public enum SaveInboxOutcome
{
    Created,
    Deduplicated
}

public sealed record SaveInboxResult(
    SaveInboxOutcome Outcome,
    Guid NotificationId,
    DateTimeOffset CreatedAt);

public enum MarkReadOutcome
{
    Success,
    NotFound,
    Forbidden
}

public static class InboxCursor
{
    public static string Encode(DateTimeOffset createdAt, Guid id)
    {
        var raw = $"{createdAt.ToUnixTimeMilliseconds()}|{id:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|');
            if (parts.Length != 2)
            {
                return false;
            }

            if (!long.TryParse(parts[0], out var millis))
            {
                return false;
            }

            if (!Guid.TryParse(parts[1], out id) || id == Guid.Empty)
            {
                return false;
            }

            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(millis);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
