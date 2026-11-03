using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Billing.Api.Models;
using Npgsql;
using NpgsqlTypes;

namespace AURA.Billing.Api.Data;

/// <summary>
/// Persists reservations, ledger rows and wallet balance changes in one PostgreSQL
/// transaction. The available balance is debited when reserving and returned only
/// when a RESERVED reservation is released.
/// </summary>
public sealed class CreditReservationRepository(
    NpgsqlDataSource dataSource,
    ILogger<CreditReservationRepository> logger)
{
    private const string ReservationColumns =
        "id, analysis_id, generation, units, state, created_at";

    public async Task VerifyDatabaseAsync(CancellationToken cancellationToken)
    {
        var probes = new[]
        {
            $"SELECT {ReservationColumns} FROM billing.credit_reservations LIMIT 0",
            "SELECT id, owner_type, owner_id, environment, balance FROM billing.credit_wallets LIMIT 0",
            "SELECT id, wallet_id, tenant_id, reservation_id, entry_type, amount, balance_after, idempotency_key, request_fingerprint FROM billing.credit_ledger_entries LIMIT 0"
        };

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var sql in probes)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        }
    }

    public async Task<ReservationOperationResult> ReserveAsync(
        ReserveCreditRequest request,
        Guid idempotencyKey,
        string fingerprint,
        BillingRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            // Serialize this reserve idempotency scope before the first lookup.
            // Otherwise a competing commit between the key lookup and generation
            // lookup can make an exact replay look like an invalid generation.
            // Transaction ownership releases the lock on commit/rollback/disposal.
            await using (var scopeCommand = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(hashtextextended(@scope, 0))
                """, connection, transaction))
            {
                scopeCommand.Parameters.AddWithValue("scope",
                    $"billing-reserve-v1|{request.TenantId:D}|{idempotencyKey:D}");
                await scopeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var priorByKey = await FindByIdempotencyKeyAsync(
                connection, transaction, request.TenantId, idempotencyKey, cancellationToken);
            if (priorByKey is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return string.Equals(priorByKey.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? new ReservationOperationResult(ReservationOperationStatus.Replayed, priorByKey.Response)
                    : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
            }

            // A later generation is legal only after the previous generation was released.
            await using (var previousCommand = new NpgsqlCommand("""
                SELECT generation, state
                FROM billing.credit_reservations
                WHERE tenant_id = @tenant_id AND analysis_id = @analysis_id
                ORDER BY generation DESC
                LIMIT 1
                FOR UPDATE
                """, connection, transaction))
            {
                previousCommand.Parameters.AddWithValue("tenant_id", request.TenantId);
                previousCommand.Parameters.AddWithValue("analysis_id", request.AnalysisId);
                await using var previousReader = await previousCommand.ExecuteReaderAsync(cancellationToken);
                if (await previousReader.ReadAsync(cancellationToken))
                {
                    var previousGeneration = previousReader.GetInt32(0);
                    var previousState = previousReader.GetString(1);
                    if (request.Generation != previousGeneration + 1
                        || !string.Equals(previousState, "RELEASED", StringComparison.Ordinal))
                    {
                        await previousReader.DisposeAsync();
                        await transaction.RollbackAsync(cancellationToken);
                        return new ReservationOperationResult(ReservationOperationStatus.Conflict);
                    }
                }
                else if (request.Generation != 1)
                {
                    await previousReader.DisposeAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return new ReservationOperationResult(ReservationOperationStatus.Conflict);
                }
            }

            Guid walletId;
            int currentBalance;
            await using (var walletCommand = new NpgsqlCommand("""
                SELECT id, balance
                FROM billing.credit_wallets
                WHERE owner_type = @owner_type
                  AND owner_id = @owner_id
                  AND environment = @environment
                FOR UPDATE
                """, connection, transaction))
            {
                walletCommand.Parameters.AddWithValue("owner_type", request.BillingOwner.Type.Trim().ToUpperInvariant());
                walletCommand.Parameters.AddWithValue("owner_id", request.BillingOwner.Id);
                walletCommand.Parameters.AddWithValue("environment", options.Environment);
                await using var walletReader = await walletCommand.ExecuteReaderAsync(cancellationToken);
                if (!await walletReader.ReadAsync(cancellationToken))
                {
                    await walletReader.DisposeAsync();
                    await transaction.RollbackAsync(cancellationToken);
                    return new ReservationOperationResult(ReservationOperationStatus.InsufficientCredit);
                }

                walletId = walletReader.GetGuid(0);
                currentBalance = walletReader.GetInt32(1);
            }

            // A concurrent request may have committed this key while this transaction waited for the wallet lock.
            var concurrentByKey = await FindByIdempotencyKeyAsync(
                connection, transaction, request.TenantId, idempotencyKey, cancellationToken);
            if (concurrentByKey is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return string.Equals(concurrentByKey.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? new ReservationOperationResult(ReservationOperationStatus.Replayed, concurrentByKey.Response)
                    : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
            }

            if (currentBalance < request.Units)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ReservationOperationResult(ReservationOperationStatus.InsufficientCredit);
            }

            int balanceAfterReserve;
            await using (var debitCommand = new NpgsqlCommand("""
                UPDATE billing.credit_wallets
                SET balance = balance - @units, updated_at = now()
                WHERE id = @wallet_id AND balance >= @units
                RETURNING balance
                """, connection, transaction))
            {
                debitCommand.Parameters.AddWithValue("units", request.Units);
                debitCommand.Parameters.AddWithValue("wallet_id", walletId);
                var value = await debitCommand.ExecuteScalarAsync(cancellationToken);
                if (value is not int resultBalance)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new ReservationOperationResult(ReservationOperationStatus.InsufficientCredit);
                }
                balanceAfterReserve = resultBalance;
            }

            Guid reservationId;
            DateTimeOffset createdAt;
            await using (var insertReservation = new NpgsqlCommand("""
                INSERT INTO billing.credit_reservations
                    (tenant_id, analysis_id, generation, wallet_id, actor_account_id,
                     patient_id, billing_owner_type, billing_owner_id, units, environment,
                     is_mock, state, idempotency_key, request_fingerprint)
                VALUES
                    (@tenant_id, @analysis_id, @generation, @wallet_id, @actor_account_id,
                     @patient_id, @owner_type, @owner_id, @units, @environment,
                     @is_mock, 'RESERVED', @idempotency_key, @request_fingerprint)
                RETURNING id, created_at
                """, connection, transaction))
            {
                insertReservation.Parameters.AddWithValue("tenant_id", request.TenantId);
                insertReservation.Parameters.AddWithValue("analysis_id", request.AnalysisId);
                insertReservation.Parameters.AddWithValue("generation", request.Generation);
                insertReservation.Parameters.AddWithValue("wallet_id", walletId);
                insertReservation.Parameters.AddWithValue("actor_account_id", request.ActorAccountId);
                insertReservation.Parameters.AddWithValue("patient_id", request.PatientId);
                insertReservation.Parameters.AddWithValue("owner_type", request.BillingOwner.Type.Trim().ToUpperInvariant());
                insertReservation.Parameters.AddWithValue("owner_id", request.BillingOwner.Id);
                insertReservation.Parameters.AddWithValue("units", request.Units);
                insertReservation.Parameters.AddWithValue("environment", options.Environment);
                insertReservation.Parameters.AddWithValue("is_mock", options.IsMock);
                insertReservation.Parameters.AddWithValue("idempotency_key", idempotencyKey);
                insertReservation.Parameters.AddWithValue("request_fingerprint", fingerprint);

                await using var reader = await insertReservation.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("Reservation insert did not return an id.");
                }
                reservationId = reader.GetGuid(0);
                createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            }

            await using (var insertLedger = new NpgsqlCommand("""
                INSERT INTO billing.credit_ledger_entries
                    (wallet_id, tenant_id, reservation_id, entry_type, amount,
                     balance_after, idempotency_key, request_fingerprint, metadata)
                VALUES
                    (@wallet_id, @tenant_id, @reservation_id, 'RESERVE', @amount,
                     @balance_after, @idempotency_key, @request_fingerprint, @metadata)
                """, connection, transaction))
            {
                insertLedger.Parameters.AddWithValue("wallet_id", walletId);
                insertLedger.Parameters.AddWithValue("tenant_id", request.TenantId);
                insertLedger.Parameters.AddWithValue("reservation_id", reservationId);
                insertLedger.Parameters.AddWithValue("amount", -request.Units);
                insertLedger.Parameters.AddWithValue("balance_after", balanceAfterReserve);
                insertLedger.Parameters.AddWithValue("idempotency_key", idempotencyKey);
                insertLedger.Parameters.AddWithValue("request_fingerprint", fingerprint);
                insertLedger.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new
                {
                    analysisId = request.AnalysisId,
                    generation = request.Generation,
                    environment = options.Environment,
                    isMock = options.IsMock
                });
                await insertLedger.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new ReservationOperationResult(
                ReservationOperationStatus.Created,
                new ReservationResponse(reservationId, request.AnalysisId, request.Generation,
                    request.Units, "RESERVED", createdAt));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogInformation("A Billing reservation uniqueness guard rejected a concurrent request.");
            var existing = await FindByIdempotencyKeyAsync(request.TenantId, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? new ReservationOperationResult(ReservationOperationStatus.Replayed, existing.Response)
                    : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
            }
            return new ReservationOperationResult(ReservationOperationStatus.Conflict);
        }
    }

    public async Task<ReservationResponse?> GetAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {ReservationColumns} FROM billing.credit_reservations WHERE id = @id";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", reservationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader) : null;
    }

    public Task<ReservationOperationResult> CaptureAsync(
        Guid reservationId, Guid idempotencyKey, string fingerprint, CancellationToken cancellationToken) =>
        TransitionAsync(reservationId, idempotencyKey, fingerprint, "CAPTURE", null, cancellationToken);

    public Task<ReservationOperationResult> ReleaseAsync(
        Guid reservationId, Guid idempotencyKey, string fingerprint, string reason, CancellationToken cancellationToken) =>
        TransitionAsync(reservationId, idempotencyKey, fingerprint, "RELEASE", reason, cancellationToken);

    /// <summary>
    /// SCRUM-366: Executes an atomic, high-performance batch partial refund for large batches (>= 100 items).
    /// Enforces pessimistic locking (SELECT FOR UPDATE) on wallets, deterministic lock ordering by ReservationId,
    /// differentiates between RELEASE (held reservations) and REFUND (captured reservations via ADJUSTMENT),
    /// and ensures strict idempotent deduplication via SHA256(batchIdempotencyKey | reservationId).
    /// </summary>
    public async Task<BatchRefundResponse> ExecuteBatchRefundAsync(
        BatchRefundRequest request,
        Guid batchIdempotencyKey,
        bool canRefundCaptured = false,
        CancellationToken cancellationToken = default)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            return new BatchRefundResponse(request.TenantId, request.BatchId, 0, 0, 0, Array.Empty<BatchRefundItemResult>());
        }

        // Sort items by ReservationId to eliminate deadlock risk across concurrent transactions
        var indexedItems = request.Items
            .Select((item, idx) => (item, idx))
            .OrderBy(x => x.item.ReservationId)
            .ToList();

        var itemResults = new BatchRefundItemResult[request.Items.Count];
        var totalRefundedCount = 0;
        var totalCreditsRefunded = 0;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            foreach (var (item, originalIndex) in indexedItems)
            {
                if (item.ReservationId == Guid.Empty)
                {
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "INVALID_ID", false, "ReservationId rỗng.");
                    continue;
                }

                var itemReason = string.IsNullOrWhiteSpace(item.Reason) ? "BATCH_PARTIAL_REFUND" : item.Reason.Trim().ToUpperInvariant();
                var validReleaseReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "VALIDATION_ABORT",
                    "STORAGE_ABORT",
                    "ANALYSIS_ABORT",
                    "FINAL_FAILURE",
                    "LOW_QUALITY",
                    "STALE_RESERVATION_AUTO_REFUND",
                    "BATCH_PARTIAL_REFUND"
                };
                var dbReleaseReason = validReleaseReasons.Contains(itemReason)
                    ? itemReason
                    : "BATCH_PARTIAL_REFUND";

                var itemKeyBytes = SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{batchIdempotencyKey:D}|{item.ReservationId:D}"));
                var itemGuidBytes = new byte[16];
                Array.Copy(itemKeyBytes, itemGuidBytes, 16);
                var itemKey = new Guid(itemGuidBytes);
                var fingerprint = ReservationFingerprint.ForRelease(item.ReservationId, dbReleaseReason);

                // 1. Check idempotency in credit_ledger_entries
                await using (var checkKeyCmd = new NpgsqlCommand("""
                    SELECT entry_type, request_fingerprint
                    FROM billing.credit_ledger_entries
                    WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
                    LIMIT 1;
                    """, connection, transaction))
                {
                    checkKeyCmd.Parameters.AddWithValue("tenant_id", request.TenantId);
                    checkKeyCmd.Parameters.AddWithValue("idempotency_key", itemKey);
                    await using var keyReader = await checkKeyCmd.ExecuteReaderAsync(cancellationToken);
                    if (await keyReader.ReadAsync(cancellationToken))
                    {
                        var entryType = keyReader.GetString(0);
                        await keyReader.DisposeAsync();
                        itemResults[originalIndex] = new BatchRefundItemResult(
                            item.ReservationId,
                            entryType == "ADJUSTMENT" ? "ALREADY_REFUNDED" : "ALREADY_RELEASED",
                            false,
                            "Đã được hoàn credit trước đó (Replayed).");
                        continue;
                    }
                }

                // 2. Lock reservation row with FOR UPDATE
                ReservationStoredRecord? stored;
                await using (var resCmd = new NpgsqlCommand("""
                    SELECT id, tenant_id, analysis_id, generation, wallet_id, units,
                           environment, is_mock, state, release_reason, created_at
                    FROM billing.credit_reservations
                    WHERE id = @id
                    FOR UPDATE;
                    """, connection, transaction))
                {
                    resCmd.Parameters.AddWithValue("id", item.ReservationId);
                    await using var reader = await resCmd.ExecuteReaderAsync(cancellationToken);
                    stored = await reader.ReadAsync(cancellationToken) ? ReadStoredRecord(reader) : null;
                }

                if (stored is null)
                {
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "NOT_FOUND", false, "Không tìm thấy reservation.");
                    continue;
                }

                // Multi-tenant check: ensure reservation belongs to the requesting tenant
                if (stored.TenantId != request.TenantId)
                {
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "FORBIDDEN", false, "Reservation không thuộc tenant này.");
                    continue;
                }

                // Branch A: State is RESERVED -> Normal RELEASE
                if (string.Equals(stored.State, "RESERVED", StringComparison.OrdinalIgnoreCase))
                {
                    await using (var updateResCmd = new NpgsqlCommand("""
                        UPDATE billing.credit_reservations
                        SET state = 'RELEASED',
                            release_reason = @release_reason,
                            updated_at = now(),
                            released_at = now(),
                            captured_at = NULL
                        WHERE id = @id AND state = 'RESERVED';
                        """, connection, transaction))
                    {
                        updateResCmd.Parameters.Add("release_reason", NpgsqlDbType.Varchar).Value = dbReleaseReason;
                        updateResCmd.Parameters.AddWithValue("id", item.ReservationId);
                        var updated = await updateResCmd.ExecuteNonQueryAsync(cancellationToken);
                        if (updated != 1)
                        {
                            itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "CONFLICT", false, "Tranh chấp trạng thái reservation.");
                            continue;
                        }
                    }

                    // Update wallet balance with pessimistic lock
                    int balanceAfter;
                    await using (var updateWalletCmd = new NpgsqlCommand("""
                        UPDATE billing.credit_wallets
                        SET balance = balance + @units, updated_at = now()
                        WHERE id = @wallet_id
                        RETURNING balance;
                        """, connection, transaction))
                    {
                        updateWalletCmd.Parameters.AddWithValue("units", stored.Units);
                        updateWalletCmd.Parameters.AddWithValue("wallet_id", stored.WalletId);
                        var balObj = await updateWalletCmd.ExecuteScalarAsync(cancellationToken);
                        balanceAfter = (int)balObj!;
                    }

                    // Insert ledger entry: RELEASE
                    await using (var insertLedgerCmd = new NpgsqlCommand("""
                        INSERT INTO billing.credit_ledger_entries
                            (wallet_id, tenant_id, reservation_id, entry_type, amount,
                             balance_after, idempotency_key, request_fingerprint, metadata)
                        VALUES
                            (@wallet_id, @tenant_id, @reservation_id, 'RELEASE', @amount,
                             @balance_after, @idempotency_key, @request_fingerprint, @metadata::jsonb);
                        """, connection, transaction))
                    {
                        insertLedgerCmd.Parameters.AddWithValue("wallet_id", stored.WalletId);
                        insertLedgerCmd.Parameters.AddWithValue("tenant_id", stored.TenantId);
                        insertLedgerCmd.Parameters.AddWithValue("reservation_id", item.ReservationId);
                        insertLedgerCmd.Parameters.AddWithValue("amount", stored.Units);
                        insertLedgerCmd.Parameters.AddWithValue("balance_after", balanceAfter);
                        insertLedgerCmd.Parameters.AddWithValue("idempotency_key", itemKey);
                        insertLedgerCmd.Parameters.AddWithValue("request_fingerprint", fingerprint);
                        insertLedgerCmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
                        {
                            operation = "RELEASE",
                            batch_id = request.BatchId,
                            releaseReason = itemReason,
                            environment = stored.Environment,
                            isMock = stored.IsMock
                        }));
                        await insertLedgerCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    totalRefundedCount++;
                    totalCreditsRefunded += stored.Units;
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "RELEASED", true);
                }
                // Branch B: State is CAPTURED -> Post-analysis REFUND via ADJUSTMENT
                else if (string.Equals(stored.State, "CAPTURED", StringComparison.OrdinalIgnoreCase) &&
                         itemReason.Contains("REFUND", StringComparison.OrdinalIgnoreCase))
                {
                    if (!canRefundCaptured)
                    {
                        itemResults[originalIndex] = new BatchRefundItemResult(
                            item.ReservationId, "FORBIDDEN", false, "Yêu cầu scope billing:refund để hoàn tiền reservation đã CAPTURED.");
                        continue;
                    }
                    // ADR-005 D4 and R09: Full-refund toi da mot lan moi captured reservation (doi key/reason khong tao refund moi)
                    await using (var checkPriorRefundCmd = new NpgsqlCommand("""
                        SELECT 1 FROM billing.credit_ledger_entries
                        WHERE reservation_id = @reservation_id 
                          AND entry_type = 'ADJUSTMENT'
                          AND (metadata->>'operation' = 'REFUND' OR metadata->>'operation' = 'BATCH_REFUND')
                        LIMIT 1;
                        """, connection, transaction))
                    {
                        checkPriorRefundCmd.Parameters.AddWithValue("reservation_id", item.ReservationId);
                        var priorRefund = await checkPriorRefundCmd.ExecuteScalarAsync(cancellationToken);
                        if (priorRefund != null)
                        {
                            itemResults[originalIndex] = new BatchRefundItemResult(
                                item.ReservationId, "ALREADY_REFUNDED", false, "Reservation da duoc hoan tien truoc do (toi da mot lan).");
                            continue;
                        }
                    }
                    int balanceAfter;
                    await using (var updateWalletCmd = new NpgsqlCommand("""
                        UPDATE billing.credit_wallets
                        SET balance = balance + @units, updated_at = now()
                        WHERE id = @wallet_id
                        RETURNING balance;
                        """, connection, transaction))
                    {
                        updateWalletCmd.Parameters.AddWithValue("units", stored.Units);
                        updateWalletCmd.Parameters.AddWithValue("wallet_id", stored.WalletId);
                        var balObj = await updateWalletCmd.ExecuteScalarAsync(cancellationToken);
                        balanceAfter = (int)balObj!;
                    }

                    // Insert ledger entry: ADJUSTMENT (Refund)
                    await using (var insertLedgerCmd = new NpgsqlCommand("""
                        INSERT INTO billing.credit_ledger_entries
                            (wallet_id, tenant_id, reservation_id, entry_type, amount,
                             balance_after, idempotency_key, request_fingerprint, metadata)
                        VALUES
                            (@wallet_id, @tenant_id, @reservation_id, 'ADJUSTMENT', @amount,
                             @balance_after, @idempotency_key, @request_fingerprint, @metadata::jsonb);
                        """, connection, transaction))
                    {
                        insertLedgerCmd.Parameters.AddWithValue("wallet_id", stored.WalletId);
                        insertLedgerCmd.Parameters.AddWithValue("tenant_id", stored.TenantId);
                        insertLedgerCmd.Parameters.AddWithValue("reservation_id", item.ReservationId);
                        insertLedgerCmd.Parameters.AddWithValue("amount", stored.Units);
                        insertLedgerCmd.Parameters.AddWithValue("balance_after", balanceAfter);
                        insertLedgerCmd.Parameters.AddWithValue("idempotency_key", itemKey);
                        insertLedgerCmd.Parameters.AddWithValue("request_fingerprint", fingerprint);
                        insertLedgerCmd.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
                        {
                            operation = "REFUND",
                            batch_id = request.BatchId,
                            refundReason = itemReason,
                            environment = stored.Environment,
                            isMock = stored.IsMock
                        }));
                        await insertLedgerCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    totalRefundedCount++;
                    totalCreditsRefunded += stored.Units;
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "REFUNDED", true);
                }
                else if (string.Equals(stored.State, "RELEASED", StringComparison.OrdinalIgnoreCase))
                {
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "ALREADY_RELEASED", false, "Reservation đã ở trạng thái RELEASED.");
                }
                else
                {
                    itemResults[originalIndex] = new BatchRefundItemResult(item.ReservationId, "CONFLICT", false, $"Trạng thái '{stored.State}' không cho phép hoàn credit.");
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return new BatchRefundResponse(
                request.TenantId,
                request.BatchId,
                request.Items.Count,
                totalRefundedCount,
                totalCreditsRefunded,
                itemResults);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Failed to execute batch refund for TenantId: {TenantId}, BatchId: {BatchId}",
                request.TenantId, request.BatchId);
            throw;
        }
    }

    /// <summary>
    /// Applies a capture using a caller-owned connection and transaction. The caller is
    /// responsible for committing or rolling back the transaction, allowing the reservation,
    /// wallet, ledger and settlement inbox to be committed atomically.
    /// </summary>
    internal Task<ReservationOperationResult> CaptureInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        Guid tenantId,
        Guid analysisId,
        int generation,
        Guid idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken) =>
        TransitionInTransactionAsync(
            connection, transaction, reservationId, tenantId, analysisId, generation,
            idempotencyKey, fingerprint, "CAPTURE", null, cancellationToken);

    /// <summary>
    /// Applies a release using a caller-owned connection and transaction. The caller is
    /// responsible for committing or rolling back the transaction, allowing the reservation,
    /// wallet, ledger and settlement inbox to be committed atomically.
    /// </summary>
    internal Task<ReservationOperationResult> ReleaseInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        Guid tenantId,
        Guid analysisId,
        int generation,
        Guid idempotencyKey,
        string fingerprint,
        string reason,
        CancellationToken cancellationToken) =>
        TransitionInTransactionAsync(
            connection, transaction, reservationId, tenantId, analysisId, generation,
            idempotencyKey, fingerprint, "RELEASE", reason, cancellationToken);

    private async Task<ReservationOperationResult> TransitionAsync(
        Guid reservationId,
        Guid idempotencyKey,
        string fingerprint,
        string operation,
        string? releaseReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);

            var result = await TransitionInTransactionAsync(
                connection, transaction, reservationId,
                expectedTenantId: null,
                expectedAnalysisId: null,
                expectedGeneration: null,
                idempotencyKey, fingerprint, operation, releaseReason, cancellationToken);

            if (result.Status == ReservationOperationStatus.Changed)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            logger.LogInformation("A Billing reservation transition uniqueness guard rejected a concurrent request.");
            return new ReservationOperationResult(ReservationOperationStatus.Conflict);
        }
    }

    /// <summary>
    /// Performs the reservation, wallet and ledger transition inside an existing transaction.
    /// This method deliberately does not commit or roll back; its caller owns transaction scope.
    /// When expected binding values are supplied, all four identifiers must match the locked row.
    /// </summary>
    private static async Task<ReservationOperationResult> TransitionInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid reservationId,
        Guid? expectedTenantId,
        Guid? expectedAnalysisId,
        int? expectedGeneration,
        Guid idempotencyKey,
        string fingerprint,
        string operation,
        string? releaseReason,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The supplied Billing transaction connection must be open.");
        }

        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("The supplied Npgsql transaction does not belong to the supplied connection.", nameof(transaction));
        }

        ReservationStoredRecord? stored;
        await using (var reservationCommand = new NpgsqlCommand("""
            SELECT id, tenant_id, analysis_id, generation, wallet_id, units,
                   environment, is_mock, state, release_reason, created_at
            FROM billing.credit_reservations
            WHERE id = @id
            FOR UPDATE
            """, connection, transaction))
        {
            reservationCommand.Parameters.AddWithValue("id", reservationId);
            await using var reader = await reservationCommand.ExecuteReaderAsync(cancellationToken);
            stored = await reader.ReadAsync(cancellationToken)
                ? ReadStoredRecord(reader)
                : null;
        }

        if (stored is null)
        {
            return new ReservationOperationResult(ReservationOperationStatus.NotFound);
        }

        if ((expectedTenantId.HasValue && stored.TenantId != expectedTenantId.Value)
            || (expectedAnalysisId.HasValue && stored.Response.AnalysisId != expectedAnalysisId.Value)
            || (expectedGeneration.HasValue && stored.Response.Generation != expectedGeneration.Value))
        {
            return new ReservationOperationResult(ReservationOperationStatus.Conflict);
        }

        // Check the operation key before terminal-state checks so an HTTP retry
        // with the same key receives the original result.
        // Ledger rows are append-only and the runtime has SELECT/INSERT only.
        // The reservation row lock serializes transitions for this reservation;
        // ledger uniqueness still guards keys shared by different reservations.
        await using (var keyCommand = new NpgsqlCommand("""
            SELECT entry_type, request_fingerprint
            FROM billing.credit_ledger_entries
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            """, connection, transaction))
        {
            keyCommand.Parameters.AddWithValue("tenant_id", stored.TenantId);
            keyCommand.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            await using var keyReader = await keyCommand.ExecuteReaderAsync(cancellationToken);
            if (await keyReader.ReadAsync(cancellationToken))
            {
                var entryType = keyReader.GetString(0);
                var priorFingerprint = keyReader.GetString(1);
                await keyReader.DisposeAsync();
                return string.Equals(entryType, operation, StringComparison.Ordinal)
                    && string.Equals(priorFingerprint, fingerprint, StringComparison.Ordinal)
                    ? new ReservationOperationResult(ReservationOperationStatus.Replayed, stored.Response)
                    : new ReservationOperationResult(ReservationOperationStatus.IdempotencyConflict);
            }
        }

        if (!string.Equals(stored.State, "RESERVED", StringComparison.Ordinal))
        {
            return new ReservationOperationResult(ReservationOperationStatus.Conflict);
        }

        if (operation == "CAPTURE" && stored.IsMock
            && !string.Equals(stored.Environment, "TEST", StringComparison.Ordinal))
        {
            return new ReservationOperationResult(ReservationOperationStatus.MockLiveBlocked, stored.Response);
        }

        int currentBalance;
        await using (var balanceCommand = new NpgsqlCommand("""
            SELECT balance FROM billing.credit_wallets WHERE id = @wallet_id FOR UPDATE
            """, connection, transaction))
        {
            balanceCommand.Parameters.AddWithValue("wallet_id", stored.WalletId);
            var value = await balanceCommand.ExecuteScalarAsync(cancellationToken);
            if (value is not int balance)
            {
                return new ReservationOperationResult(ReservationOperationStatus.Conflict);
            }
            currentBalance = balance;
        }

        var balanceAfter = currentBalance;
        if (operation == "RELEASE")
        {
            await using var creditCommand = new NpgsqlCommand("""
                UPDATE billing.credit_wallets
                SET balance = balance + @units, updated_at = now()
                WHERE id = @wallet_id AND balance <= 2147483647 - @units
                RETURNING balance
                """, connection, transaction);
            creditCommand.Parameters.AddWithValue("units", stored.Units);
            creditCommand.Parameters.AddWithValue("wallet_id", stored.WalletId);
            var value = await creditCommand.ExecuteScalarAsync(cancellationToken);
            if (value is not int creditedBalance)
            {
                return new ReservationOperationResult(ReservationOperationStatus.Conflict);
            }
            balanceAfter = creditedBalance;
        }

        var finalState = operation == "CAPTURE" ? "CAPTURED" : "RELEASED";
        await using (var updateReservation = new NpgsqlCommand("""
            UPDATE billing.credit_reservations
            SET state = @state,
                release_reason = @release_reason,
                updated_at = now(),
                captured_at = CASE WHEN @state = 'CAPTURED' THEN now() ELSE NULL END,
                released_at = CASE WHEN @state = 'RELEASED' THEN now() ELSE NULL END
            WHERE id = @id AND state = 'RESERVED'
            """, connection, transaction))
        {
            updateReservation.Parameters.AddWithValue("state", finalState);
            updateReservation.Parameters.Add("release_reason", NpgsqlDbType.Varchar).Value = (object?)releaseReason ?? DBNull.Value;
            updateReservation.Parameters.AddWithValue("id", reservationId);
            var updated = await updateReservation.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                return new ReservationOperationResult(ReservationOperationStatus.Conflict);
            }
        }

        await using (var insertLedger = new NpgsqlCommand("""
            INSERT INTO billing.credit_ledger_entries
                (wallet_id, tenant_id, reservation_id, entry_type, amount,
                 balance_after, idempotency_key, request_fingerprint, metadata)
            VALUES
                (@wallet_id, @tenant_id, @reservation_id, @entry_type, @amount,
                 @balance_after, @idempotency_key, @request_fingerprint, @metadata)
            """, connection, transaction))
        {
            insertLedger.Parameters.AddWithValue("wallet_id", stored.WalletId);
            insertLedger.Parameters.AddWithValue("tenant_id", stored.TenantId);
            insertLedger.Parameters.AddWithValue("reservation_id", reservationId);
            insertLedger.Parameters.AddWithValue("entry_type", operation);
            insertLedger.Parameters.AddWithValue("amount", operation == "CAPTURE" ? 0 : stored.Units);
            insertLedger.Parameters.AddWithValue("balance_after", balanceAfter);
            insertLedger.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insertLedger.Parameters.AddWithValue("request_fingerprint", fingerprint);
            insertLedger.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new
            {
                operation,
                releaseReason,
                environment = stored.Environment,
                isMock = stored.IsMock
            });
            await insertLedger.ExecuteNonQueryAsync(cancellationToken);
        }

        // Return the known terminal state without opening a second connection.
        // The outer transaction may still roll back if the inbox update fails.
        var finalResponse = stored.Response with { State = finalState };
        return new ReservationOperationResult(ReservationOperationStatus.Changed, finalResponse);
    }
    private async Task<ReservationStoredRecord?> FindByIdempotencyKeyAsync(
        Guid tenantId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await FindByIdempotencyKeyAsync(connection, null, tenantId, idempotencyKey, cancellationToken);
    }

    private static async Task<ReservationStoredRecord?> FindByIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid tenantId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, tenant_id, analysis_id, generation, units, state, created_at,
                   request_fingerprint
            FROM billing.credit_reservations
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var response = new ReservationResponse(
            reader.GetGuid(0), reader.GetGuid(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6));
        return new ReservationStoredRecord(response, reader.GetGuid(1), Guid.Empty, 0,
            string.Empty, false, null, reader.GetString(7), reader.GetString(5));
    }

    private static ReservationResponse ReadResponse(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetInt32(3),
        reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));

    private static ReservationStoredRecord ReadStoredRecord(NpgsqlDataReader reader)
    {
        var response = new ReservationResponse(
            reader.GetGuid(0), reader.GetGuid(2), reader.GetInt32(3), reader.GetInt32(5),
            reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(10));
        return new ReservationStoredRecord(
            response,
            reader.GetGuid(1),
            reader.GetGuid(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            string.Empty,
            reader.GetString(8));
    }

    private sealed record ReservationStoredRecord(
    ReservationResponse Response,
    Guid TenantId,
    Guid WalletId,
    int Units,
    string Environment,
    bool IsMock,
    string? ReleaseReason,
    string Fingerprint,
    string State);

    public async Task<IReadOnlyList<StaleReservationRecord>> GetStaleReservationsAsync(
        int thresholdMinutes,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, tenant_id, analysis_id, wallet_id, units, updated_at
            FROM billing.credit_reservations
            WHERE state = 'RESERVED'
              AND updated_at < now() - (@threshold_minutes * interval '1 minute')
            ORDER BY updated_at ASC
            LIMIT @limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("threshold_minutes", thresholdMinutes);
        command.Parameters.AddWithValue("limit", limit);

        var list = new List<StaleReservationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new StaleReservationRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetInt32(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return list;
    }
}

public sealed record StaleReservationRecord(
    Guid ReservationId,
    Guid TenantId,
    Guid AnalysisId,
    Guid WalletId,
    int Units,
    DateTimeOffset UpdatedAt);

