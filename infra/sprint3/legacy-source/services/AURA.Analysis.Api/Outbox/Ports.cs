namespace AURA.Analysis.Api.Outbox;

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxRecord>> ClaimAsync(OutboxClaim claim, CancellationToken ct);
    Task<bool> RenewLeaseAsync(OutboxRecord record, DateTimeOffset at, TimeSpan lease, CancellationToken ct);
    Task<bool> MarkPublishedAsync(OutboxRecord record, DateTimeOffset at, CancellationToken ct);
    Task<bool> MarkRetryAsync(OutboxRecord record, string errorCode, DateTimeOffset at, DateTimeOffset nextPublishAt, CancellationToken ct);
    Task<bool> MarkNeedsAttentionAsync(OutboxRecord record, string errorCode, DateTimeOffset at, CancellationToken ct);
    Task<int> ReleaseUnattemptedAsync(IReadOnlyCollection<OutboxRecord> records, DateTimeOffset at, CancellationToken ct);
    Task<int> SweepExhaustedAsync(OutboxClaim scope, CancellationToken ct);
    Task<OutboxBacklog> GetBacklogAsync(OutboxClaim scope, CancellationToken ct);
}

public interface IBrokerPublisher
{
    Task<PublishOutcome> PublishAsync(BrokerMessage message, TimeSpan confirmTimeout, CancellationToken ct);
}
