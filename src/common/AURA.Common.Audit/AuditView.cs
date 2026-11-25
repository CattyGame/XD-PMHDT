namespace AURA.Common.Audit;

public sealed record AuditView(Guid EventId, string SourceService, Guid ActorAccountId, string Action,
    string ResourceType, string ResourceIdHash, long? ResourceVersion, string CorrelationId,
    DateTimeOffset OccurredAt, string Outcome)
{
    public static AuditView From(AuditEvent e) => new(e.EventId, AuditStore.Schema(e.Source) + "-api", e.ActorAccountId,
        e.Action.ToString(), e.ResourceType.ToString(), AuditEvent.Correlation($"{e.Source}/{e.ResourceType}/{e.ResourceId}"),
        e.ResourceVersion, e.CorrelationId, e.OccurredAt, e.Outcome switch
        { AuditOutcome.Denied => "DENY", AuditOutcome.Failed => "FAIL", _ => "ALLOW" });
}
