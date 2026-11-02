using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Billing.Api.Models;
using Npgsql;

namespace AURA.Billing.Api.Data;

public sealed class OrderRepository(
    NpgsqlDataSource dataSource,
    ILogger<OrderRepository> logger)
{
    public async Task VerifyDatabaseAsync(CancellationToken cancellationToken)
    {
        var probes = new[]
        {
            "SELECT id, tenant_id, package_id, status FROM billing.orders LIMIT 0",
            "SELECT id, order_id, transaction_id, status FROM billing.payments LIMIT 0",
            "SELECT id, package_id, version_number, status FROM billing.package_price_versions LIMIT 0"
        };

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        foreach (var sql in probes)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        }
    }

    public async Task<PackagePriceVersionRecord?> GetActivePriceVersionAsync(
        int packageId,
        Guid? priceVersionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var sql = priceVersionId.HasValue
            ? """
              SELECT id, package_id, version_number, display_name, price_amount, currency_code, credits, status
              FROM billing.package_price_versions
              WHERE package_id = @package_id AND id = @price_version_id AND status = 'ACTIVE'
              LIMIT 1;
              """
            : """
              SELECT id, package_id, version_number, display_name, price_amount, currency_code, credits, status
              FROM billing.package_price_versions
              WHERE package_id = @package_id AND status = 'ACTIVE'
              ORDER BY version_number DESC
              LIMIT 1;
              """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("package_id", packageId);
        if (priceVersionId.HasValue)
        {
            command.Parameters.AddWithValue("price_version_id", priceVersionId.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new PackagePriceVersionRecord(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7));
        }

        return null;
    }

    public async Task<OrderRecord?> FindByIdempotencyKeyAsync(
        Guid tenantId,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, tenant_id, billing_owner_type, billing_owner_id, package_id,
                   package_price_version_id, package_name_snapshot, price_amount_snapshot,
                   currency_code_snapshot, credits_snapshot, environment, status,
                   idempotency_key, request_fingerprint, created_at, updated_at
            FROM billing.orders
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadOrderRecord(reader);
        }

        return null;
    }

    public async Task<OrderRecord?> GetOrderByIdAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, tenant_id, billing_owner_type, billing_owner_id, package_id,
                   package_price_version_id, package_name_snapshot, price_amount_snapshot,
                   currency_code_snapshot, credits_snapshot, environment, status,
                   idempotency_key, request_fingerprint, created_at, updated_at
            FROM billing.orders
            WHERE id = @order_id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("order_id", orderId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadOrderRecord(reader);
        }

        return null;
    }

    public async Task<OrderRecord> CreateOrderAsync(
        Guid orderId,
        Guid tenantId,
        string ownerType,
        Guid ownerId,
        PackagePriceVersionRecord priceVersion,
        string environment,
        Guid idempotencyKey,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO billing.orders (
                id, tenant_id, billing_owner_type, billing_owner_id, package_id,
                package_price_version_id, package_name_snapshot, price_amount_snapshot,
                currency_code_snapshot, credits_snapshot, environment, status,
                idempotency_key, request_fingerprint, created_at, updated_at
            ) VALUES (
                @id, @tenant_id, @owner_type, @owner_id, @package_id,
                @price_version_id, @name_snapshot, @price_snapshot,
                @currency_snapshot, @credits_snapshot, @environment, 'PENDING',
                @idempotency_key, @fingerprint, now(), now()
            )
            RETURNING created_at, updated_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("owner_type", ownerType);
        command.Parameters.AddWithValue("owner_id", ownerId);
        command.Parameters.AddWithValue("package_id", priceVersion.PackageId);
        command.Parameters.AddWithValue("price_version_id", priceVersion.Id);
        command.Parameters.AddWithValue("name_snapshot", priceVersion.DisplayName);
        command.Parameters.AddWithValue("price_snapshot", priceVersion.PriceAmount);
        command.Parameters.AddWithValue("currency_snapshot", priceVersion.CurrencyCode);
        command.Parameters.AddWithValue("credits_snapshot", priceVersion.Credits);
        command.Parameters.AddWithValue("environment", environment);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("fingerprint", fingerprint);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var createdAt = reader.GetDateTime(0);
        var updatedAt = reader.GetDateTime(1);

        return new OrderRecord(
            orderId, tenantId, ownerType, ownerId, priceVersion.PackageId,
            priceVersion.Id, priceVersion.DisplayName, priceVersion.PriceAmount,
            priceVersion.CurrencyCode, priceVersion.Credits, environment,
            "PENDING", idempotencyKey, fingerprint, createdAt, updatedAt);
    }

    public async Task<PaymentCallbackResult> ProcessCallbackInTransactionAsync(
        PaymentCallbackPayload payload,
        string paymentMethod,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock the order row
        const string selectOrderSql = """
            SELECT id, tenant_id, billing_owner_type, billing_owner_id, package_id,
                   package_price_version_id, package_name_snapshot, price_amount_snapshot,
                   currency_code_snapshot, credits_snapshot, environment, status,
                   idempotency_key, request_fingerprint, created_at, updated_at
            FROM billing.orders
            WHERE id = @order_id
            FOR UPDATE;
            """;

        OrderRecord order;
        await using (var orderCmd = new NpgsqlCommand(selectOrderSql, connection, transaction))
        {
            orderCmd.Parameters.AddWithValue("order_id", payload.OrderId);
            await using var reader = await orderCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PaymentCallbackResult(
                    false, "NOT_FOUND", "Không tìm thấy đơn hàng.", payload.TransactionId, 0, 0, false);
            }
            order = ReadOrderRecord(reader);
        }

        // 2. If order already COMPLETED -> Idempotent replay
        if (string.Equals(order.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Order {OrderId} is already COMPLETED. Replaying callback without adding duplicate credits.",
                payload.OrderId);

            // Read current balance for reporting
            var currentBalance = await GetWalletBalanceAsync(
                connection, null, order.BillingOwnerType, order.BillingOwnerId, order.Environment, cancellationToken);

            return new PaymentCallbackResult(
                true, "COMPLETED", "Giao dịch đã được ghi nhận trước đó.", payload.TransactionId, 0, currentBalance, true);
        }

        if (!string.Equals(order.Status, "PENDING", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new PaymentCallbackResult(
                false, order.Status, $"Trạng thái đơn hàng '{order.Status}' không cho phép thanh toán.",
                payload.TransactionId, 0, 0, false);
        }

        // 3. Verify amount and currency
        if (payload.Amount != order.PriceAmountSnapshot ||
            !string.Equals(payload.Currency, order.CurrencyCodeSnapshot, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Order {OrderId} payment amount/currency mismatch. Expected {ExpectedAmount} {ExpectedCurrency}, got {ActualAmount} {ActualCurrency}.",
                order.Id, order.PriceAmountSnapshot, order.CurrencyCodeSnapshot, payload.Amount, payload.Currency);

            await using var cancelCmd = new NpgsqlCommand("""
                UPDATE billing.orders SET status = 'CANCELLED', updated_at = now() WHERE id = @id;
                """, connection, transaction);
            cancelCmd.Parameters.AddWithValue("id", order.Id);
            await cancelCmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new PaymentCallbackResult(
                false, "CANCELLED", "Số tiền hoặc loại tiền tệ không khớp đơn hàng.",
                payload.TransactionId, 0, 0, false);
        }

        // 4. Update order to COMPLETED
        await using (var updateOrderCmd = new NpgsqlCommand("""
            UPDATE billing.orders SET status = 'COMPLETED', updated_at = now() WHERE id = @id;
            """, connection, transaction))
        {
            updateOrderCmd.Parameters.AddWithValue("id", order.Id);
            await updateOrderCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 5. Insert billing.payments
        var rawPayloadJson = JsonSerializer.Serialize(payload);
        await using (var insertPaymentCmd = new NpgsqlCommand("""
            INSERT INTO billing.payments (
                id, order_id, payment_method, transaction_id, amount, currency, status,
                signature, raw_payload, created_at, updated_at
            ) VALUES (
                gen_random_uuid(), @order_id, @payment_method, @transaction_id, @amount, @currency, 'SUCCESS',
                @signature, @raw_payload::jsonb, now(), now()
            );
            """, connection, transaction))
        {
            insertPaymentCmd.Parameters.AddWithValue("order_id", order.Id);
            insertPaymentCmd.Parameters.AddWithValue("payment_method", paymentMethod);
            insertPaymentCmd.Parameters.AddWithValue("transaction_id", payload.TransactionId);
            insertPaymentCmd.Parameters.AddWithValue("amount", payload.Amount);
            insertPaymentCmd.Parameters.AddWithValue("currency", payload.Currency);
            insertPaymentCmd.Parameters.AddWithValue("signature", payload.Signature);
            insertPaymentCmd.Parameters.AddWithValue("raw_payload", rawPayloadJson);
            await insertPaymentCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 6. Upsert wallet & increment balance
        Guid walletId;
        int newBalance;
        await using (var walletSelectCmd = new NpgsqlCommand("""
            SELECT id, balance
            FROM billing.credit_wallets
            WHERE owner_type = @owner_type AND owner_id = @owner_id AND environment = @environment
            FOR UPDATE;
            """, connection, transaction))
        {
            walletSelectCmd.Parameters.AddWithValue("owner_type", order.BillingOwnerType);
            walletSelectCmd.Parameters.AddWithValue("owner_id", order.BillingOwnerId);
            walletSelectCmd.Parameters.AddWithValue("environment", order.Environment);

            await using var walletReader = await walletSelectCmd.ExecuteReaderAsync(cancellationToken);
            if (await walletReader.ReadAsync(cancellationToken))
            {
                walletId = walletReader.GetGuid(0);
                var currentBalance = walletReader.GetInt32(1);
                newBalance = currentBalance + order.CreditsSnapshot;
                await walletReader.DisposeAsync();

                await using var updateWalletCmd = new NpgsqlCommand("""
                    UPDATE billing.credit_wallets
                    SET balance = @new_balance, updated_at = now()
                    WHERE id = @wallet_id;
                    """, connection, transaction);
                updateWalletCmd.Parameters.AddWithValue("new_balance", newBalance);
                updateWalletCmd.Parameters.AddWithValue("wallet_id", walletId);
                await updateWalletCmd.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await walletReader.DisposeAsync();
                walletId = Guid.NewGuid();
                newBalance = order.CreditsSnapshot;

                await using var insertWalletCmd = new NpgsqlCommand("""
                    INSERT INTO billing.credit_wallets (id, owner_type, owner_id, environment, balance, created_at, updated_at)
                    VALUES (@id, @owner_type, @owner_id, @environment, @balance, now(), now());
                    """, connection, transaction);
                insertWalletCmd.Parameters.AddWithValue("id", walletId);
                insertWalletCmd.Parameters.AddWithValue("owner_type", order.BillingOwnerType);
                insertWalletCmd.Parameters.AddWithValue("owner_id", order.BillingOwnerId);
                insertWalletCmd.Parameters.AddWithValue("environment", order.Environment);
                insertWalletCmd.Parameters.AddWithValue("balance", newBalance);
                await insertWalletCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 7. Insert credit ledger entry
        var metadataJson = JsonSerializer.Serialize(new
        {
            order_id = order.Id,
            transaction_id = payload.TransactionId,
            payment_method = paymentMethod,
            amount_paid = payload.Amount,
            currency = payload.Currency
        });

        await using (var ledgerCmd = new NpgsqlCommand("""
            INSERT INTO billing.credit_ledger_entries (
                wallet_id, tenant_id, order_id, reservation_id, entry_type, amount, balance_after,
                idempotency_key, request_fingerprint, metadata, created_at
            ) VALUES (
                @wallet_id, @tenant_id, @order_id, NULL, 'CREDIT', @amount, @balance_after,
                @idempotency_key, @fingerprint, @metadata::jsonb, now()
            );
            """, connection, transaction))
        {
            ledgerCmd.Parameters.AddWithValue("wallet_id", walletId);
            ledgerCmd.Parameters.AddWithValue("tenant_id", order.TenantId);
            ledgerCmd.Parameters.AddWithValue("order_id", order.Id);
            ledgerCmd.Parameters.AddWithValue("amount", order.CreditsSnapshot);
            ledgerCmd.Parameters.AddWithValue("balance_after", newBalance);
            ledgerCmd.Parameters.AddWithValue("idempotency_key", order.IdempotencyKey);
            ledgerCmd.Parameters.AddWithValue("fingerprint", $"order:{order.Id:D}:tx:{payload.TransactionId}");
            ledgerCmd.Parameters.AddWithValue("metadata", metadataJson);
            await ledgerCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Order {OrderId} completed successfully. Added {Credits} credits. New balance: {Balance}.",
            order.Id, order.CreditsSnapshot, newBalance);

        return new PaymentCallbackResult(
            true, "COMPLETED", "Thanh toán thành công và đã nạp credit vào ví.",
            payload.TransactionId, order.CreditsSnapshot, newBalance, false);
    }

    private static async Task<int> GetWalletBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string ownerType,
        Guid ownerId,
        string environment,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT balance
            FROM billing.credit_wallets
            WHERE owner_type = @owner_type AND owner_id = @owner_id AND environment = @environment;
            """, connection, transaction);
        command.Parameters.AddWithValue("owner_type", ownerType);
        command.Parameters.AddWithValue("owner_id", ownerId);
        command.Parameters.AddWithValue("environment", environment);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int balance ? balance : 0;
    }

    private static OrderRecord ReadOrderRecord(NpgsqlDataReader reader)
    {
        return new OrderRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetInt32(4),
            reader.GetGuid(5),
            reader.GetString(6),
            reader.GetDecimal(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetGuid(12),
            reader.GetString(13),
            reader.GetDateTime(14),
            reader.GetDateTime(15));
    }

    public async Task<(bool Allowed, string Code, string Message)> VerifyClinicAdminAuthorityAsync(
        Guid clinicId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string clinicSql = "SELECT status FROM profile.clinics WHERE clinic_id = @clinic_id LIMIT 1;";
        await using (var clinicCmd = new NpgsqlCommand(clinicSql, connection))
        {
            clinicCmd.Parameters.AddWithValue("clinic_id", clinicId);
            var clinicStatusObj = await clinicCmd.ExecuteScalarAsync(cancellationToken);
            if (clinicStatusObj is not string clinicStatus)
            {
                return (false, "ERR_CLINIC_NOT_FOUND", "Không tìm thấy phòng khám.");
            }
            if (!string.Equals(clinicStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "ERR_FORBIDDEN", $"Phòng khám đang ở trạng thái '{clinicStatus}', không thể thực hiện giao dịch.");
            }
        }

        const string memberSql = """
            SELECT role, status
            FROM profile.memberships
            WHERE clinic_id = @clinic_id AND account_id = @actor_id
            LIMIT 1;
            """;
        await using var memberCmd = new NpgsqlCommand(memberSql, connection);
        memberCmd.Parameters.AddWithValue("clinic_id", clinicId);
        memberCmd.Parameters.AddWithValue("actor_id", actorId);
        await using var reader = await memberCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (false, "ERR_FORBIDDEN", "Người dùng không phải là thành viên của phòng khám này.");
        }
        var role = reader.GetString(0);
        var status = reader.GetString(1);
        if (!string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ERR_FORBIDDEN", "Tài khoản thành viên trong phòng khám không hoạt động.");
        }
        if (!string.Equals(role, "CLINIC_ADMIN", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ERR_FORBIDDEN", "Chỉ ClinicAdmin mới có quyền quản lý và mua gói credit cho phòng khám.");
        }

        return (true, string.Empty, string.Empty);
    }
}

public sealed record PackagePriceVersionRecord(
    Guid Id,
    int PackageId,
    int VersionNumber,
    string DisplayName,
    decimal PriceAmount,
    string CurrencyCode,
    int Credits,
    string Status);
