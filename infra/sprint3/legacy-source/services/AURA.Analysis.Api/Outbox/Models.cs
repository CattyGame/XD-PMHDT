namespace AURA.Analysis.Api.Outbox;

public sealed record OutboxRecord(
    Guid Id, Guid TenantId, string EventType, string SchemaVersion,
    string PayloadJson, DateTimeOffset OccurredAt, int DeliveryAttempts, long ClaimGeneration = 0);

public sealed record OutboxClaim(
    DateTimeOffset Now, int BatchSize, TimeSpan Lease, int MaxAttempts,
    DateTimeOffset MinOccurredAt, IReadOnlyCollection<Guid> ExcludedTenants);

public sealed record OutboxBacklog(
    long Pending, long Due, long InFlight, long NeedsAttention, DateTimeOffset? OldestPendingOccurredAt);

public enum PublishOutcome
{
    /// <summary>Broker confirm (ack) va KHONG bi basic.return. Chi ket qua nay moi duoc danh dau published.</summary>
    Confirmed,
    Nacked,
    /// <summary>Message bi tra lai (mandatory, khong route duoc) - coi la CHUA publish.</summary>
    Returned,
    /// <summary>Het thoi gian cho confirm: ket qua chua ro, cho phep gui lai cung ID.</summary>
    ConfirmTimeout,
    BrokerUnavailable
}

public sealed record BrokerMessage(
    string Exchange, string RoutingKey, string MessageId, string Type,
    string CorrelationId, ReadOnlyMemory<byte> Body);

public sealed record OutboxRunResult(int Claimed, int Published, int Retried, int NeedsAttention, int Released)
{
    public static readonly OutboxRunResult Empty = new(0, 0, 0, 0, 0);
}

/// <summary>Ma loi luu vao last_error_code (khong chua du lieu nhay cam).</summary>
public static class OutboxErrors
{
    public const string InvalidMessage = "INVALID_MESSAGE";
    public const string TypeMismatch = "TYPE_MISMATCH";
    public const string IdMismatch = "ID_MISMATCH";
    public const string SchemaVersionMismatch = "SCHEMA_VERSION_MISMATCH";
    public const string TenantMismatch = "TENANT_MISMATCH";
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";
    public const string ForbiddenField = "FORBIDDEN_FIELD";
    public const string UnroutableType = "UNROUTABLE_EVENT_TYPE";
    public const string BrokerNack = "BROKER_NACK";
    public const string BrokerReturned = "BROKER_RETURNED";
    public const string ConfirmTimeout = "CONFIRM_TIMEOUT";
    public const string BrokerUnavailable = "BROKER_UNAVAILABLE";
    public const string AttemptsExhausted = "PUBLISH_ATTEMPTS_EXHAUSTED";
}
