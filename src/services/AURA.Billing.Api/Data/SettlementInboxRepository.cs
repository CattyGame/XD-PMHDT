using System.Data;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace AURA.Billing.Api.Data;

public enum SettlementInboxIngestStatus
{
    Inserted,
    Duplicate,
    PayloadConflict
}

public sealed record SettlementInboxEvent(
    string ConsumerName,
    Guid EventId,
    string EventType,
    string SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AnalysisId,
    string CorrelationId,
    string PayloadSha256);

public sealed record SettlementInboxIngestResult(
    SettlementInboxIngestStatus Status,
    string CurrentStatus);

public sealed record SettlementInboxClaim(
    string ConsumerName,
    Guid EventId,
    string EventType,
    string SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid AnalysisId,
    string CorrelationId,
    string PayloadSha256,
    short ResolverAttempts,
    Guid LeaseToken,
    DateTimeOffset LeaseUntil);

public sealed record SettlementInboxRetryResult(
    string Status,
    short ResolverAttempts,
    DateTimeOffset? NextAttemptAt);

public sealed record SettlementInboxDecision(
    Guid ReservationId,
    int Generation,
    int? ResultVersion,
    bool IsMock,
    string ProcessingStatus,
    string SettlementAction,
    string SettlementReason);

/// <summary>
/// Persists the durable Billing settlement inbox. Network calls must happen outside
/// database transactions. Final reservation/wallet/ledger and inbox updates can share
/// a caller-owned transaction through the InTransaction methods below.
/// </summary>
public sealed class SettlementInboxRepository(NpgsqlDataSource dataSource)
{
    private const string TableName = "billing.settlement_inbox";
    private static readonly Regex ConsumerPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex CorrelationPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ErrorCodePattern = new("^[A-Z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    public async Task VerifyDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT consumer_name, event_id, event_type, schema_version, occurred_at,
                   tenant_id, analysis_id, correlation_id, payload_sha256, status,
                   resolver_attempts, next_attempt_at, lease_token, lease_until,
                   last_error_code, resolved_reservation_id, resolved_generation,
                   result_version, is_mock, processing_status, settlement_action,
                   settlement_reason, created_at, updated_at, completed_at
            FROM {TableName}
            LIMIT 0
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts only event identity and validated metadata; raw event payloads are never persisted.
    /// Same event ID + same hash is a harmless duplicate. Same event ID + different hash is held.
    /// </summary>
    public async Task<SettlementInboxIngestResult> IngestAsync(
        SettlementInboxEvent incoming,
        CancellationToken cancellationToken)
    {
        var item = NormalizeAndValidate(incoming);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO billing.settlement_inbox (
                consumer_name, event_id, event_type, schema_version, occurred_at,
                tenant_id, analysis_id, correlation_id, payload_sha256
            )
            VALUES (
                @consumer_name, @event_id, @event_type, @schema_version, @occurred_at,
                @tenant_id, @analysis_id, @correlation_id, @payload_sha256
            )
            ON CONFLICT (consumer_name, event_id) DO NOTHING
            RETURNING status
            """, connection, transaction))
        {
            AddEventParameters(insert, item);
            var insertedStatus = await insert.ExecuteScalarAsync(cancellationToken) as string;
            if (insertedStatus is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new SettlementInboxIngestResult(
                    SettlementInboxIngestStatus.Inserted, insertedStatus);
            }
        }

        string existingHash;
        string existingStatus;
        await using (var existing = new NpgsqlCommand("""
            SELECT payload_sha256, status
            FROM billing.settlement_inbox
            WHERE consumer_name = @consumer_name AND event_id = @event_id
            FOR UPDATE
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("consumer_name", item.ConsumerName);
            existing.Parameters.AddWithValue("event_id", item.EventId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Settlement inbox conflict row disappeared after ON CONFLICT handling.");
            }
            existingHash = reader.GetString(0);
            existingStatus = reader.GetString(1);
        }

        if (string.Equals(existingHash, item.PayloadSha256, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new SettlementInboxIngestResult(
                SettlementInboxIngestStatus.Duplicate, existingStatus);
        }

        await using (var holdConflict = new NpgsqlCommand("""
            UPDATE billing.settlement_inbox
            SET conflict_payload_sha256 = COALESCE(conflict_payload_sha256, @conflict_payload_sha256),
                conflict_prior_status = COALESCE(conflict_prior_status, status),
                conflict_prior_settlement_action = COALESCE(conflict_prior_settlement_action, settlement_action),
                conflict_prior_settlement_reason = COALESCE(conflict_prior_settlement_reason, settlement_reason),
                conflict_prior_completed_at = COALESCE(conflict_prior_completed_at, completed_at),
                status = 'HOLD',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_until = NULL,
                last_error_code = 'PAYLOAD_CONFLICT',
                settlement_action = NULL,
                settlement_reason = NULL,
                completed_at = NULL,
                updated_at = now()
            WHERE consumer_name = @consumer_name AND event_id = @event_id
            """, connection, transaction))
        {
            holdConflict.Parameters.AddWithValue("conflict_payload_sha256", item.PayloadSha256);
            holdConflict.Parameters.AddWithValue("consumer_name", item.ConsumerName);
            holdConflict.Parameters.AddWithValue("event_id", item.EventId);
            var updated = await holdConflict.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException(
                    "Settlement inbox payload-conflict transition did not update exactly one row.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new SettlementInboxIngestResult(
            SettlementInboxIngestStatus.PayloadConflict, "HOLD");
    }

    /// <summary>
    /// Claims one due row using SKIP LOCKED. A probe attempt is durably counted at claim time,
    /// so a process crash cannot cause more than three resolver probes for the same event.
    /// </summary>
    public async Task<SettlementInboxClaim?> ClaimNextDueAsync(
        string consumerName,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var consumer = NormalizeConsumer(consumerName);
        if (leaseDuration < TimeSpan.FromSeconds(10) || leaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration), "Lease duration must be between 10 seconds and 5 minutes.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // If the process crashed while holding the third probe lease, recover it as HOLD.
        await using (var exhausted = new NpgsqlCommand("""
            UPDATE billing.settlement_inbox
            SET status = 'HOLD',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_until = NULL,
                last_error_code = 'RESOLVER_ATTEMPTS_EXHAUSTED',
                settlement_action = NULL,
                settlement_reason = NULL,
                completed_at = NULL,
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND status = 'PENDING'
              AND resolver_attempts >= 3
              AND (lease_until IS NULL OR lease_until <= now())
            """, connection, transaction))
        {
            exhausted.Parameters.AddWithValue("consumer_name", consumer);
            await exhausted.ExecuteNonQueryAsync(cancellationToken);
        }

        var leaseToken = Guid.NewGuid();
        SettlementInboxClaim? claim = null;
        await using (var claimCommand = new NpgsqlCommand("""
            WITH candidate AS (
                SELECT consumer_name, event_id
                FROM billing.settlement_inbox
                WHERE consumer_name = @consumer_name
                  AND status = 'PENDING'
                  AND resolver_attempts < 3
                  AND next_attempt_at <= now()
                  AND (lease_until IS NULL OR lease_until <= now())
                ORDER BY next_attempt_at, created_at, event_id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE billing.settlement_inbox AS inbox
            SET lease_token = @lease_token,
                lease_until = now() + @lease_duration,
                resolver_attempts = inbox.resolver_attempts + 1,
                updated_at = now()
            FROM candidate
            WHERE inbox.consumer_name = candidate.consumer_name
              AND inbox.event_id = candidate.event_id
            RETURNING inbox.consumer_name, inbox.event_id, inbox.event_type,
                      inbox.schema_version, inbox.occurred_at, inbox.tenant_id,
                      inbox.analysis_id, inbox.correlation_id, inbox.payload_sha256,
                      inbox.resolver_attempts, inbox.lease_token, inbox.lease_until
            """, connection, transaction))
        {
            claimCommand.Parameters.AddWithValue("consumer_name", consumer);
            claimCommand.Parameters.AddWithValue("lease_token", leaseToken);
            claimCommand.Parameters.AddWithValue("lease_duration", leaseDuration);
            await using var reader = await claimCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                claim = new SettlementInboxClaim(
                    reader.GetString(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetGuid(5),
                    reader.GetGuid(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetInt16(9),
                    reader.GetGuid(10),
                    reader.GetFieldValue<DateTimeOffset>(11));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    /// <summary>
    /// Records a failed resolver probe. Delays are 5s after probe 1 and 15s after probe 2;
    /// probe 3 moves the event to HOLD. The attempt count was already incremented by ClaimNextDueAsync.
    /// </summary>
    public async Task<SettlementInboxRetryResult?> ScheduleResolverRetryAsync(
        SettlementInboxClaim claim,
        string errorCode,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        ValidateErrorCode(errorCode);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        SettlementInboxRetryResult? result = null;
        await using (var command = new NpgsqlCommand("""
            UPDATE billing.settlement_inbox
            SET status = CASE WHEN @attempts >= 3 THEN 'HOLD' ELSE 'PENDING' END,
                next_attempt_at = CASE
                    WHEN @attempts = 1 THEN now() + interval '5 seconds'
                    WHEN @attempts = 2 THEN now() + interval '15 seconds'
                    ELSE NULL
                END,
                lease_token = NULL,
                lease_until = NULL,
                last_error_code = @error_code,
                settlement_action = NULL,
                settlement_reason = NULL,
                completed_at = NULL,
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND status = 'PENDING'
              AND lease_token = @lease_token
              AND lease_until > now()
              AND resolver_attempts = @attempts
              AND @attempts BETWEEN 1 AND 3
            RETURNING status, resolver_attempts, next_attempt_at
            """, connection, transaction))
        {
            // Bind each parameter exactly once; AddClaimParameters would also bind attempts.
            command.Parameters.AddWithValue("consumer_name", claim.ConsumerName);
            command.Parameters.AddWithValue("event_id", claim.EventId);
            command.Parameters.AddWithValue("lease_token", claim.LeaseToken);
            command.Parameters.AddWithValue("attempts", claim.ResolverAttempts);
            command.Parameters.AddWithValue("error_code", errorCode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                result = new SettlementInboxRetryResult(
                    reader.GetString(0),
                    reader.GetInt16(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        if (result is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>Moves a claimed event to HOLD without making any reservation or wallet change.</summary>
    public async Task<bool> MarkHoldAsync(
        SettlementInboxClaim claim,
        string errorCode,
        CancellationToken cancellationToken)
    {
        ValidateClaim(claim);
        ValidateErrorCode(errorCode);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        await using var command = new NpgsqlCommand("""
            UPDATE billing.settlement_inbox
            SET status = 'HOLD',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_until = NULL,
                last_error_code = @error_code,
                settlement_action = NULL,
                settlement_reason = NULL,
                completed_at = NULL,
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND status = 'PENDING'
              AND lease_token = @lease_token
              AND lease_until > now()
              AND resolver_attempts = @attempts
            """, connection, transaction);
        AddClaimParameters(command, claim);
        command.Parameters.AddWithValue("error_code", errorCode);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);

        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Must be called inside the settlement transaction before touching the reservation.
    /// It locks the inbox row and verifies that the caller still owns a live lease.
    /// </summary>
    internal async Task<bool> HasValidLeaseForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SettlementInboxClaim claim,
        CancellationToken cancellationToken)
    {
        ValidateTransaction(connection, transaction);
        ValidateClaim(claim);

        await using var command = new NpgsqlCommand("""
            SELECT 1
            FROM billing.settlement_inbox
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND status = 'PENDING'
              AND lease_token = @lease_token
              AND lease_until > now()
              AND resolver_attempts = @attempts
            FOR UPDATE
            """, connection, transaction);
        AddClaimParameters(command, claim);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null && value is not DBNull;
    }

    /// <summary>
    /// Completes the inbox row in the same transaction as the reservation/wallet/ledger transition.
    /// The caller must first obtain the valid lease with HasValidLeaseForUpdateAsync.
    /// </summary>
    internal async Task<bool> MarkCompletedInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SettlementInboxClaim claim,
        SettlementInboxDecision decision,
        CancellationToken cancellationToken)
    {
        ValidateTransaction(connection, transaction);
        ValidateClaim(claim);
        ValidateDecision(claim, decision);

        await using var command = new NpgsqlCommand("""
            UPDATE billing.settlement_inbox
            SET status = 'COMPLETED',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_until = NULL,
                last_error_code = NULL,
                resolved_reservation_id = @reservation_id,
                resolved_generation = @generation,
                result_version = @result_version,
                is_mock = @is_mock,
                processing_status = @processing_status,
                settlement_action = @settlement_action,
                settlement_reason = @settlement_reason,
                completed_at = now(),
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND status = 'PENDING'
              AND lease_token = @lease_token
              AND lease_until > now()
              AND resolver_attempts = @attempts
            """, connection, transaction);
        AddClaimParameters(command, claim);
        command.Parameters.AddWithValue("reservation_id", decision.ReservationId);
        command.Parameters.AddWithValue("generation", decision.Generation);
        command.Parameters.Add("result_version", NpgsqlDbType.Integer).Value =
            (object?)decision.ResultVersion ?? DBNull.Value;
        command.Parameters.AddWithValue("is_mock", decision.IsMock);
        command.Parameters.AddWithValue("processing_status", decision.ProcessingStatus);
        command.Parameters.AddWithValue("settlement_action", decision.SettlementAction);
        command.Parameters.AddWithValue("settlement_reason", decision.SettlementReason);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Marks a resolver-authorized no-op as IGNORED in the shared settlement transaction.</summary>
    internal async Task<bool> MarkIgnoredInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SettlementInboxClaim claim,
        string reason,
        CancellationToken cancellationToken)
    {
        ValidateTransaction(connection, transaction);
        ValidateClaim(claim);
        if (reason is not ("NO_RESERVATION" or "NOT_APPLICABLE"))
        {
            throw new ArgumentException("Ignored reason is not supported by the settlement inbox contract.", nameof(reason));
        }

        await using var command = new NpgsqlCommand("""
            UPDATE billing.settlement_inbox
            SET status = 'IGNORED',
                next_attempt_at = NULL,
                lease_token = NULL,
                lease_until = NULL,
                last_error_code = NULL,
                resolved_reservation_id = NULL,
                resolved_generation = NULL,
                result_version = NULL,
                is_mock = NULL,
                processing_status = NULL,
                settlement_action = 'NONE',
                settlement_reason = @settlement_reason,
                completed_at = now(),
                updated_at = now()
            WHERE consumer_name = @consumer_name
              AND event_id = @event_id
              AND status = 'PENDING'
              AND lease_token = @lease_token
              AND lease_until > now()
              AND resolver_attempts = @attempts
            """, connection, transaction);
        AddClaimParameters(command, claim);
        command.Parameters.AddWithValue("settlement_reason", reason);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static SettlementInboxEvent NormalizeAndValidate(SettlementInboxEvent incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var consumer = NormalizeConsumer(incoming.ConsumerName);
        if (incoming.EventId == Guid.Empty || incoming.TenantId == Guid.Empty || incoming.AnalysisId == Guid.Empty)
        {
            throw new ArgumentException("Settlement event identifiers must be non-empty.", nameof(incoming));
        }
        if (incoming.EventType is not ("analysis.completed" or "analysis.failed"))
        {
            throw new ArgumentException("Unsupported Analysis event type.", nameof(incoming));
        }
        if (incoming.SchemaVersion is not ("0.7" or "0.8"))
        {
            throw new ArgumentException("Settlement inbox accepts Analysis event schema0.7 or0.8 only.", nameof(incoming));
        }
        var correlationId = incoming.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId) || !CorrelationPattern.IsMatch(correlationId))
        {
            throw new ArgumentException("Correlation ID does not match the allowed format.", nameof(incoming));
        }

        var payloadSha256 = incoming.PayloadSha256;
        if (string.IsNullOrWhiteSpace(payloadSha256) || !HashPattern.IsMatch(payloadSha256))
        {
            throw new ArgumentException("Payload SHA-256 must be 64 lowercase hexadecimal characters.", nameof(incoming));
        }
        if (incoming.OccurredAt == default)
        {
            throw new ArgumentException("Event occurredAt is required.", nameof(incoming));
        }

        return incoming with
        {
            ConsumerName = consumer,
            CorrelationId = correlationId.Trim(),
            PayloadSha256 = payloadSha256,
            OccurredAt = incoming.OccurredAt.ToUniversalTime()
        };
    }

    private static string NormalizeConsumer(string consumerName)
    {
        if (string.IsNullOrWhiteSpace(consumerName))
        {
            throw new ArgumentException("Consumer name is required.", nameof(consumerName));
        }
        var value = consumerName.Trim();
        if (!ConsumerPattern.IsMatch(value))
        {
            throw new ArgumentException("Consumer name contains unsupported characters or exceeds 64 characters.", nameof(consumerName));
        }
        return value;
    }

    private static void ValidateClaim(SettlementInboxClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        _ = NormalizeConsumer(claim.ConsumerName);
        if (claim.EventId == Guid.Empty || claim.LeaseToken == Guid.Empty
            || claim.ResolverAttempts is < 1 or > 3)
        {
            throw new ArgumentException("Settlement inbox claim is incomplete or outside the attempt limit.", nameof(claim));
        }
    }

    private static void ValidateErrorCode(string errorCode)
    {
        if (!ErrorCodePattern.IsMatch(errorCode ?? string.Empty))
        {
            throw new ArgumentException("Error code must use 1-64 uppercase letters, digits or underscores.", nameof(errorCode));
        }
    }

    private static void ValidateDecision(SettlementInboxClaim claim, SettlementInboxDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.ReservationId == Guid.Empty || decision.Generation <= 0 || decision.IsMock)
        {
            throw new ArgumentException("A completed settlement requires a real, bound reservation and generation.", nameof(decision));
        }

        // The event is only a reconciliation trigger. Analysis' authoritative state may have
        // advanced since it was emitted, so stale/out-of-order event types must not override
        // a valid resolved settlement decision.
        var validCapture = decision.SettlementAction == "CAPTURE"
            && decision.SettlementReason == "VALID_RESULT"
            && decision.ProcessingStatus == "COMPLETED"
            && decision.ResultVersion is > 0;
        var validRelease = decision.SettlementAction == "RELEASE"
            && decision.SettlementReason is "VALIDATION_ABORT" or "STORAGE_ABORT" or "ANALYSIS_ABORT" or "FINAL_FAILURE" or "LOW_QUALITY"
            && decision.ProcessingStatus is "FAILED" or "LOW_QUALITY" or "REJECTED"
            && decision.ResultVersion is null;

        if (!validCapture && !validRelease)
        {
            throw new ArgumentException("Settlement decision does not satisfy the Billing event/resolution contract.", nameof(decision));
        }
    }

    private static void AddEventParameters(NpgsqlCommand command, SettlementInboxEvent item)
    {
        command.Parameters.AddWithValue("consumer_name", item.ConsumerName);
        command.Parameters.AddWithValue("event_id", item.EventId);
        command.Parameters.AddWithValue("event_type", item.EventType);
        command.Parameters.AddWithValue("schema_version", item.SchemaVersion);
        command.Parameters.AddWithValue("occurred_at", item.OccurredAt);
        command.Parameters.AddWithValue("tenant_id", item.TenantId);
        command.Parameters.AddWithValue("analysis_id", item.AnalysisId);
        command.Parameters.AddWithValue("correlation_id", item.CorrelationId);
        command.Parameters.AddWithValue("payload_sha256", item.PayloadSha256);
    }

    private static void AddClaimParameters(NpgsqlCommand command, SettlementInboxClaim claim)
    {
        command.Parameters.AddWithValue("consumer_name", claim.ConsumerName);
        command.Parameters.AddWithValue("event_id", claim.EventId);
        command.Parameters.AddWithValue("lease_token", claim.LeaseToken);
        command.Parameters.AddWithValue("attempts", claim.ResolverAttempts);
    }

    private static void ValidateTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Settlement inbox transaction connection must be open.");
        }
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("The supplied transaction does not belong to the supplied connection.", nameof(transaction));
        }
    }
}
