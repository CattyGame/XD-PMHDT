namespace AURA.Analysis.Api.Outbox;

/// <summary>Cau hinh publisher (W3-M2-01). Mac dinh TAT: Enabled=false de khong tu bat khi chua co broker/credential.
/// Gia tri mac dinh khop config/common.yaml (outboxPollSeconds, outboxMaxPublishAttempts, publisherConfirmTimeoutSeconds).</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    public bool Enabled { get; set; } = false;
    public int PollSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 10;
    /// <summary>Toi da 10 lan thu publish; sau do NEEDS_ATTENTION (giu record, khong xoa).</summary>
    public int MaxPublishAttempts { get; set; } = 10;
    public int ConfirmTimeoutSeconds { get; set; } = 5;
    /// <summary>Lease phai du cho ca batch: BatchSize x ConfirmTimeoutSeconds + 5.</summary>
    public int LeaseSeconds { get; set; } = 60;
    public int MaxBodyBytes { get; set; } = 65536;
    public int[] BackoffSeconds { get; set; } = [5, 15, 30, 60, 120];
    /// <summary>BAT BUOC khi Enabled: chi quet outbox tu moc nay tro di. Chan viec vo tinh quet outbox fixture 08 da dong bang.</summary>
    public DateTimeOffset? MinOccurredAtUtc { get; set; }
    public Guid[] ExcludedTenantIds { get; set; } = [];
    public string JobsExchange { get; set; } = "aura.s3.jobs";
    public string EventsExchange { get; set; } = "aura.s3.events";

    public void Validate()
    {
        if (PollSeconds < 1) throw new InvalidOperationException("Outbox:PollSeconds phai >= 1");
        if (BatchSize < 1 || BatchSize > 100) throw new InvalidOperationException("Outbox:BatchSize phai trong 1..100");
        if (MaxPublishAttempts < 1) throw new InvalidOperationException("Outbox:MaxPublishAttempts phai >= 1");
        if (ConfirmTimeoutSeconds < 1) throw new InvalidOperationException("Outbox:ConfirmTimeoutSeconds phai >= 1");
        if (BackoffSeconds.Length == 0 || BackoffSeconds.Any(s => s < 1)) throw new InvalidOperationException("Outbox:BackoffSeconds khong hop le");
        if (LeaseSeconds < BatchSize * ConfirmTimeoutSeconds + 5)
            throw new InvalidOperationException("Outbox:LeaseSeconds phai >= BatchSize*ConfirmTimeoutSeconds+5 de lease khong het han giua batch");
        if (Enabled && MinOccurredAtUtc is null)
            throw new InvalidOperationException("Outbox:MinOccurredAtUtc bat buoc khi Enabled (chan quet outbox fixture 08)");
    }
}
