using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AURA.Analysis.Api.Outbox;

/// <summary>Kiem tra payload outbox theo contract Sprint 3 va tinh exchange/routing key.
/// Domain event: eventId/eventType, schemaVersion "0.7". Job command: messageId/messageType, schemaVersion "0.6".
/// Hai envelope khac nhau, KHONG tron. Loi o day la loi vinh vien (NEEDS_ATTENTION ngay, khong goi broker).</summary>
public static class OutboxMessageMapper
{
    public const string JobType = "analysis.job.run";
    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
        { "analysis.requested", "analysis.completed", "analysis.failed", "batch.completed" };
    // analysis.reviewed nam ngoai demo Sprint 3 -> khong phat.

    private static readonly Regex CorrelationRx = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "imageBase64","image_base64","base64","objectKey","object_key","sas","sasUrl","sasToken",
        "token","accessToken","password","secret","authorization","url"
    };

    public sealed record MapResult(BrokerMessage? Message, string? ErrorCode)
    {
        public bool Ok => Message is not null;
    }

    public static MapResult Map(OutboxRecord r, OutboxOptions o)
    {
        var body = Encoding.UTF8.GetBytes(r.PayloadJson);
        if (body.Length > o.MaxBodyBytes) return Fail(OutboxErrors.MessageTooLarge);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(r.PayloadJson); } catch (JsonException) { return Fail(OutboxErrors.InvalidMessage); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail(OutboxErrors.InvalidMessage);
            if (HasForbiddenName(root)) return Fail(OutboxErrors.ForbiddenField);

            bool isJob = root.TryGetProperty("messageType", out _);
            bool isEvent = root.TryGetProperty("eventType", out _);
            if (isJob == isEvent) return Fail(OutboxErrors.InvalidMessage); // phai dung mot trong hai envelope

            string typeProp = isJob ? "messageType" : "eventType";
            string idProp = isJob ? "messageId" : "eventId";
            string version = isJob ? "0.6" : "0.7";

            var type = Str(root, typeProp);
            if (type is null) return Fail(OutboxErrors.InvalidMessage);
            if (isJob ? type != JobType : !EventTypes.Contains(type)) return Fail(OutboxErrors.UnroutableType);
            if (!string.Equals(type, r.EventType, StringComparison.Ordinal)) return Fail(OutboxErrors.TypeMismatch);

            if (Str(root, "schemaVersion") != version) return Fail(OutboxErrors.SchemaVersionMismatch);
            if (!string.Equals(r.SchemaVersion, version, StringComparison.Ordinal)) return Fail(OutboxErrors.SchemaVersionMismatch);

            if (!Guid(root, idProp, out var id)) return Fail(OutboxErrors.InvalidMessage);
            if (id != r.Id) return Fail(OutboxErrors.IdMismatch); // eventId/messageId phai on dinh = outbox.id

            if (!Guid(root, "tenantId", out var tenant)) return Fail(OutboxErrors.InvalidMessage);
            if (tenant != r.TenantId) return Fail(OutboxErrors.TenantMismatch);

            var occurred = Str(root, "occurredAt");
            if (occurred is null || !occurred.EndsWith('Z') ||
                !DateTimeOffset.TryParse(occurred, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                return Fail(OutboxErrors.InvalidMessage);

            var corr = Str(root, "correlationId");
            if (corr is null || !CorrelationRx.IsMatch(corr)) return Fail(OutboxErrors.InvalidMessage);

            if (!Guid(root, "analysisId", out _)) return Fail(OutboxErrors.InvalidMessage);
            if (isJob)
            {
                if (!Guid(root, "jobId", out _) || !Guid(root, "batchId", out _) || !Guid(root, "imageFileId", out _))
                    return Fail(OutboxErrors.InvalidMessage);
                if (!root.TryGetProperty("attempt", out var att) || att.ValueKind != JsonValueKind.Number
                    || !att.TryGetInt32(out var n) || n < 1) return Fail(OutboxErrors.InvalidMessage);
            }
            else if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Fail(OutboxErrors.InvalidMessage);

            if (!OutboxContractValidator.IsValid(root, isJob)) return Fail(OutboxErrors.InvalidMessage);
            var exchange = isJob ? o.JobsExchange : o.EventsExchange;
            return new MapResult(new BrokerMessage(exchange, type, id.ToString(), type, corr, body), null);
        }
    }

    private static MapResult Fail(string code) => new(null, code);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Guid(JsonElement e, string name, out Guid g)
    {
        g = default;
        var s = Str(e, name);
        return s is not null && System.Guid.TryParseExact(s, "D", out g) && g != System.Guid.Empty;
    }

    private static bool HasForbiddenName(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (Forbidden.Contains(p.Name) || HasForbiddenName(p.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray()) if (HasForbiddenName(i)) return true;
                return false;
            default: return false;
        }
    }
}
