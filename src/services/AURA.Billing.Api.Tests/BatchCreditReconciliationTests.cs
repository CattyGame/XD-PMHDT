using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Billing.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AURA.Billing.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_SCRUM370_TEST_CONNECTION_STRING")))
            Skip = "Requires AURA_SCRUM370_TEST_CONNECTION_STRING (isolated PostgreSQL integration database).";
    }
}

public sealed class BatchCreditReconciliationTests
{
    [PostgresFact]
    public async Task Large_partial_failure_batch_never_overdraws_and_charges_only_successful_images()
    {
        var connectionString = Environment.GetEnvironmentVariable("AURA_SCRUM370_TEST_CONNECTION_STRING")!;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var walletId = Guid.NewGuid();
        var openingCreditKey = Guid.NewGuid();
        const int initialCredits = 80;
        const int batchImageCount = 100;
        const int expectedSuccessfulImages = 55;

        await CreateWalletAsync(dataSource, tenantId, clinicId, walletId, openingCreditKey, initialCredits);
        try
        {
            var repository = new CreditReservationRepository(dataSource,
                NullLogger<CreditReservationRepository>.Instance);
            var settlement = new CreditSettlementService(repository,
                NullLogger<CreditSettlementService>.Instance);
            var requests = Enumerable.Range(0, batchImageCount).Select(index =>
            {
                var analysisId = Guid.NewGuid();
                var request = new ReserveCreditRequest(analysisId, tenantId, actorId, Guid.NewGuid(),
                    new BillingOwnerRequest("CLINIC", clinicId), 1, 1);
                var key = Guid.NewGuid();
                var fingerprint = $"scrum370-{index:D3}-{Guid.NewGuid():N}";
                return (Index: index, Request: request, Key: key, Fingerprint: fingerprint);
            }).ToArray();

            var reserveResults = await Task.WhenAll(requests.Select(item => repository.ReserveAsync(
                item.Request, item.Key, item.Fingerprint, new BillingRuntimeOptions("TEST", false),
                CancellationToken.None)));

            var accepted = requests.Zip(reserveResults)
                .Where(pair => pair.Second.Status == ReservationOperationStatus.Created)
                .ToArray();
            var rejected = reserveResults.Count(result => result.Status == ReservationOperationStatus.InsufficientCredit);
            Assert.Equal(initialCredits, accepted.Length);
            Assert.Equal(batchImageCount - initialCredits, rejected);
            Assert.All(accepted, pair => Assert.NotNull(pair.Second.Reservation));
            Assert.Equal(0, await GetWalletBalanceAsync(dataSource, walletId));

            var processingResults = accepted.Select(async (pair, ordinal) =>
            {
                var reservation = pair.Second.Reservation!;
                var succeeded = ordinal < expectedSuccessfulImages;
                var resolution = settlement.Resolve(new SettlementEventContext(
                    EventId: Guid.NewGuid(),
                    AnalysisId: pair.First.Request.AnalysisId,
                    TenantId: tenantId,
                    ReservationId: reservation.ReservationId,
                    Generation: 1,
                    ProcessingStatus: succeeded ? "COMPLETED" : "FAILED",
                    IsTerminal: true,
                    HasValidResult: succeeded,
                    IsRetryable: false,
                    FailureReason: succeeded ? null : "ANALYSIS_ABORT",
                    IsMock: false));

                var executed = await settlement.ExecuteAsync(resolution, Guid.NewGuid(), CancellationToken.None);
                Assert.True(executed.Succeeded);
                Assert.Equal(succeeded ? "CAPTURED" : "RELEASED", executed.Reservation?.State);
                return succeeded;
            });
            var outcomes = await Task.WhenAll(processingResults);

            var totals = await ReadSettlementTotalsAsync(dataSource, walletId);
            Assert.Equal(expectedSuccessfulImages, outcomes.Count(success => success));
            Assert.Equal(expectedSuccessfulImages, totals.CapturedReservations);
            Assert.Equal(initialCredits - expectedSuccessfulImages, totals.ReleasedReservations);
            Assert.Equal(expectedSuccessfulImages, totals.CaptureLedgerRows);
            Assert.Equal(initialCredits - expectedSuccessfulImages, totals.ReleaseLedgerRows);
            Assert.Equal(expectedSuccessfulImages, totals.NetCreditsSpent);
            Assert.Equal(initialCredits - expectedSuccessfulImages, totals.WalletBalance);
            Assert.True(totals.MinimumLedgerBalance >= 0);
            Assert.Equal(totals.WalletBalance, totals.NetLedgerBalance);
        }
        finally
        {
            await DeleteWalletFixtureAsync(dataSource, tenantId, walletId);
        }
    }

    private static async Task CreateWalletAsync(NpgsqlDataSource dataSource, Guid tenantId, Guid clinicId,
        Guid walletId, Guid openingCreditKey, int openingCredits)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var wallet = new NpgsqlCommand("""
            INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance)
            VALUES (@wallet_id, 'CLINIC', @clinic_id, 'TEST', @balance);
            """, connection, transaction))
        {
            wallet.Parameters.AddWithValue("wallet_id", walletId);
            wallet.Parameters.AddWithValue("clinic_id", clinicId);
            wallet.Parameters.AddWithValue("balance", openingCredits);
            await wallet.ExecuteNonQueryAsync();
        }

        await using (var opening = new NpgsqlCommand("""
            INSERT INTO billing.credit_ledger_entries
                (wallet_id, tenant_id, entry_type, amount, balance_after, idempotency_key,
                 request_fingerprint, metadata)
            VALUES (@wallet_id, @tenant_id, 'CREDIT', @amount, @amount, @key,
                    @fingerprint, '{"source":"SCRUM-370 test fixture"}'::jsonb);
            """, connection, transaction))
        {
            opening.Parameters.AddWithValue("wallet_id", walletId);
            opening.Parameters.AddWithValue("tenant_id", tenantId);
            opening.Parameters.AddWithValue("amount", openingCredits);
            opening.Parameters.AddWithValue("key", openingCreditKey);
            opening.Parameters.AddWithValue("fingerprint", "scrum370-fixture-credit");
            await opening.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<int> GetWalletBalanceAsync(NpgsqlDataSource dataSource, Guid walletId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT balance FROM billing.credit_wallets WHERE id = @wallet_id;");
        command.Parameters.AddWithValue("wallet_id", walletId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<SettlementTotals> ReadSettlementTotalsAsync(NpgsqlDataSource dataSource, Guid walletId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM billing.credit_reservations WHERE wallet_id = @wallet_id AND state = 'CAPTURED'),
                (SELECT count(*) FROM billing.credit_reservations WHERE wallet_id = @wallet_id AND state = 'RELEASED'),
                (SELECT count(*) FROM billing.credit_ledger_entries WHERE wallet_id = @wallet_id AND entry_type = 'CAPTURE'),
                (SELECT count(*) FROM billing.credit_ledger_entries WHERE wallet_id = @wallet_id AND entry_type = 'RELEASE'),
                (SELECT COALESCE(-SUM(amount), 0) FROM billing.credit_ledger_entries
                    WHERE wallet_id = @wallet_id AND entry_type IN ('RESERVE', 'RELEASE')),
                (SELECT balance FROM billing.credit_wallets WHERE id = @wallet_id),
                (SELECT COALESCE(SUM(amount), 0) FROM billing.credit_ledger_entries WHERE wallet_id = @wallet_id),
                (SELECT MIN(balance_after) FROM billing.credit_ledger_entries WHERE wallet_id = @wallet_id);
            """);
        command.Parameters.AddWithValue("wallet_id", walletId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SettlementTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetInt32(7));
    }

    private static async Task DeleteWalletFixtureAsync(NpgsqlDataSource dataSource, Guid tenantId, Guid walletId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var ledger = new NpgsqlCommand(
                         "DELETE FROM billing.credit_ledger_entries WHERE wallet_id = @wallet_id;", connection, transaction))
        {
            ledger.Parameters.AddWithValue("wallet_id", walletId);
            await ledger.ExecuteNonQueryAsync();
        }
        await using (var reservations = new NpgsqlCommand(
                         "DELETE FROM billing.credit_reservations WHERE tenant_id = @tenant_id AND wallet_id = @wallet_id;",
                         connection, transaction))
        {
            reservations.Parameters.AddWithValue("tenant_id", tenantId);
            reservations.Parameters.AddWithValue("wallet_id", walletId);
            await reservations.ExecuteNonQueryAsync();
        }
        await using (var wallet = new NpgsqlCommand(
                         "DELETE FROM billing.credit_wallets WHERE id = @wallet_id;", connection, transaction))
        {
            wallet.Parameters.AddWithValue("wallet_id", walletId);
            await wallet.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private sealed record SettlementTotals(long CapturedReservations, long ReleasedReservations,
        long CaptureLedgerRows, long ReleaseLedgerRows, long NetCreditsSpent, int WalletBalance,
        long NetLedgerBalance, int MinimumLedgerBalance);
}
