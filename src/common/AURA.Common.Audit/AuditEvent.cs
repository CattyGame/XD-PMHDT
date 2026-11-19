using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AURA.Common.Audit;

public enum AuditSource { Identity, Profile, Analysis, Billing, Notification }
public enum AuditAction { ImageRead, ReportDownload, RoleChanged, PriceChanged, ClinicStatusChanged, NotificationRead,
    MembershipInvited, MembershipGranted, MembershipRevoked }
public enum AuditResource { Image, Report, Account, Package, Clinic, Membership }
public enum AuditOutcome { Authorized, Succeeded, Denied, Failed }

// No request body, path, headers, filenames or free-text metadata can enter this contract.
public sealed record AuditEvent([property: JsonRequired] Guid EventId, [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] AuditSource Source, [property: JsonRequired] Guid ActorAccountId,
    [property: JsonRequired] Guid? TenantId, [property: JsonRequired] AuditAction Action,
    [property: JsonRequired] AuditResource ResourceType, [property: JsonRequired] string ResourceId,
    [property: JsonRequired] long? ResourceVersion, [property: JsonRequired] string CorrelationId,
    [property: JsonRequired] AuditOutcome Outcome, [property: JsonRequired] DateTimeOffset OccurredAt)
{
    public static AuditEvent Create(AuditSource source, Guid actor, AuditAction action, AuditResource resource,
        string resourceId, string? correlation, Guid? tenantId = null, long? resourceVersion = null,
        AuditOutcome outcome = AuditOutcome.Succeeded)
    {
        var result = new AuditEvent(Guid.NewGuid(), 1, source, actor, tenantId, action, resource, resourceId,
            resourceVersion, Correlation(correlation ?? Guid.NewGuid().ToString("D")), outcome, DateTimeOffset.UtcNow);
        Validate(result);
        return result;
    }

    public static string Correlation(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void Validate(AuditEvent e)
    {
        var opaqueId = e.ResourceId is { Length: > 0 and <= 40 } &&
            ((Guid.TryParseExact(e.ResourceId, "D", out var id) && id != Guid.Empty) ||
             (int.TryParse(e.ResourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0));
        if (e.EventId == Guid.Empty || e.SchemaVersion != 1 || !Enum.IsDefined(e.Source)
            || e.ActorAccountId == Guid.Empty || e.TenantId == Guid.Empty || !Enum.IsDefined(e.Action)
            || !Enum.IsDefined(e.ResourceType) || !opaqueId || e.ResourceVersion < 1
            || !Enum.IsDefined(e.Outcome) || e.OccurredAt == default || e.OccurredAt.Offset != TimeSpan.Zero
            || e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(5)
            || e.CorrelationId is not { Length: 64 } || e.CorrelationId.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Invalid audit metadata.");
    }
}

public static class AuditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Hash(AuditEvent e) => AuditEvent.Correlation(JsonSerializer.Serialize(e, Options));
}

public sealed class AuditConflictException() : Exception("Audit event identity conflicts with its persisted payload.");
