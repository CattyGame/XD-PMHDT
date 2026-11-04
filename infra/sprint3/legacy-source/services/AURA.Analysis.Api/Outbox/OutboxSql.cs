namespace AURA.Analysis.Api.Outbox;

public static class OutboxSql
{
    public const string Claim = """
        WITH due AS (
            SELECT id FROM analysis.outbox_events
            WHERE published_at IS NULL AND needs_attention_at IS NULL
              AND delivery_attempts < @max_attempts
              AND (next_publish_at IS NULL OR next_publish_at <= @now)
              AND (locked_until IS NULL OR locked_until <= @now)
              AND occurred_at >= @min_occurred_at
              AND NOT (tenant_id = ANY(@excluded_tenants))
            ORDER BY occurred_at, id
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED)
        UPDATE analysis.outbox_events o
           SET locked_until = @now + make_interval(secs => @lease_seconds),
               delivery_attempts = o.delivery_attempts + 1,
               claim_generation = o.claim_generation + 1
          FROM due WHERE o.id = due.id
        RETURNING o.id, o.tenant_id, o.event_type, o.schema_version, o.payload::text,
                  o.occurred_at, o.delivery_attempts, o.claim_generation
        """;

    public const string RenewLease = """
        UPDATE analysis.outbox_events
           SET locked_until = GREATEST(locked_until, @at + make_interval(secs => @lease_seconds))
         WHERE id = @id AND claim_generation = @generation
           AND published_at IS NULL AND needs_attention_at IS NULL
           AND locked_until > @at AND locked_until > clock_timestamp()
        """;

    public const string MarkPublished = """
        UPDATE analysis.outbox_events
           SET published_at = @at, locked_until = NULL, next_publish_at = NULL,
               last_error_code = NULL, last_error_at = NULL
         WHERE id = @id AND claim_generation = @generation
           AND published_at IS NULL AND needs_attention_at IS NULL
           AND locked_until > @at AND locked_until > clock_timestamp()
        """;

    public const string MarkRetry = """
        UPDATE analysis.outbox_events
           SET last_error_code = @code, last_error_at = @at,
               next_publish_at = @next, locked_until = NULL
         WHERE id = @id AND claim_generation = @generation
           AND published_at IS NULL AND needs_attention_at IS NULL
           AND locked_until > @at AND locked_until > clock_timestamp()
        """;

    public const string MarkNeedsAttention = """
        UPDATE analysis.outbox_events
           SET needs_attention_at = @at, last_error_code = @code,
               last_error_at = @at, locked_until = NULL
         WHERE id = @id AND claim_generation = @generation
           AND published_at IS NULL AND needs_attention_at IS NULL
           AND locked_until > @at AND locked_until > clock_timestamp()
        """;

    public const string ReleaseUnattempted = """
        UPDATE analysis.outbox_events
           SET delivery_attempts = GREATEST(delivery_attempts - 1, 0), locked_until = NULL
         WHERE id = @id AND claim_generation = @generation
           AND published_at IS NULL AND needs_attention_at IS NULL
           AND locked_until > @at AND locked_until > clock_timestamp()
        """;

    public const string SweepExhausted = """
        UPDATE analysis.outbox_events
           SET needs_attention_at = @now, last_error_code = 'PUBLISH_ATTEMPTS_EXHAUSTED', last_error_at = @now
         WHERE published_at IS NULL AND needs_attention_at IS NULL
           AND delivery_attempts >= @max_attempts
           AND (locked_until IS NULL OR locked_until <= @now)
           AND occurred_at >= @min_occurred_at
           AND NOT (tenant_id = ANY(@excluded_tenants))
        """;

    public const string Backlog = """
        SELECT
          count(*) FILTER (WHERE published_at IS NULL AND needs_attention_at IS NULL),
          count(*) FILTER (WHERE published_at IS NULL AND needs_attention_at IS NULL
                           AND (next_publish_at IS NULL OR next_publish_at <= @now)
                           AND (locked_until IS NULL OR locked_until <= @now)),
          count(*) FILTER (WHERE published_at IS NULL AND needs_attention_at IS NULL AND locked_until > @now),
          count(*) FILTER (WHERE published_at IS NULL AND needs_attention_at IS NOT NULL),
          min(occurred_at) FILTER (WHERE published_at IS NULL AND needs_attention_at IS NULL)
        FROM analysis.outbox_events
        WHERE occurred_at >= @min_occurred_at AND NOT (tenant_id = ANY(@excluded_tenants))
        """;
}
