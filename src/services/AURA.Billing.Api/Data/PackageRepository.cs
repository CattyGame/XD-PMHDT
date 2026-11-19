using AURA.Billing.Api.Models;
using Npgsql;
using AURA.Common.Audit;
using System.Text.Json;

namespace AURA.Billing.Api.Data;

public sealed class PackageRepository(NpgsqlDataSource dataSource)
{
    private long _readCount;

    public long ReadCount => Interlocked.Read(ref _readCount);

    public async Task VerifyDatabaseAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, package_id, price_amount, currency_code, credits
            FROM billing.package_price_versions
            LIMIT 0;
            """;

        await using var command = dataSource.CreateCommand(sql);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PackageDto>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT package_id, display_name, price_amount, credits, id, currency_code, version_number
            FROM billing.package_price_versions
            WHERE status = 'ACTIVE'
            ORDER BY package_id;
            """;

        Interlocked.Increment(ref _readCount);

        await using var command = dataSource.CreateCommand(sql);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        var packages = new List<PackageDto>();

        while (await reader.ReadAsync(cancellationToken))
        {
            packages.Add(new PackageDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetInt32(3), reader.GetGuid(4), reader.GetString(5), reader.GetInt32(6)));
        }

        return packages;
    }

    public async Task<PricePublication> PublishAsync(int? packageId, PublishPriceRequest request, Guid actor,
        Guid key, string? correlationId, CancellationToken ct)
    {
        var fingerprint = AuditEvent.Correlation(JsonSerializer.Serialize(new { packageId, request }));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // Catalog writes are rare. Serialize version checks, new ID allocation and replay in one transaction.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(383, 1)", connection, tx))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var replay = new NpgsqlCommand("""
            SELECT p.id, p.package_id, p.version_number, p.display_name, p.price_amount, p.currency_code, p.credits, c.request_hash
            FROM billing.catalog_publications c JOIN billing.package_price_versions p ON p.id=c.price_version_id
            WHERE c.actor_account_id=$1 AND c.idempotency_key=$2
            """, connection, tx))
        {
            replay.Parameters.Add(new() { Value = actor });
            replay.Parameters.Add(new() { Value = key });
            await using var row = await replay.ExecuteReaderAsync(ct);
            if (await row.ReadAsync(ct))
                return row.GetString(7) == fingerprint
                    ? new(ReadPublished(row), Replayed: true) : new(null, Conflict: true);
        }

        int id;
        int latest;
        if (packageId is null)
        {
            await using var allocate = new NpgsqlCommand("""
                SELECT coalesce(max(package_id),0)+1 FROM billing.package_price_versions
                """, connection, tx);
            id = (int)(await allocate.ExecuteScalarAsync(ct))!;
            latest = 0;
        }
        else
        {
            id = packageId.Value;
            await using var version = new NpgsqlCommand("""
                SELECT max(version_number) FROM billing.package_price_versions WHERE package_id=$1
                """, connection, tx);
            version.Parameters.Add(new() { Value = id });
            if (await version.ExecuteScalarAsync(ct) is not int previous) return new(null);
            latest = previous;
        }
        if (request.ExpectedVersion != latest) return new(null, Conflict: true);

        await using (var retire = new NpgsqlCommand("""
            UPDATE billing.package_price_versions SET status='RETIRED', effective_to=clock_timestamp()
            WHERE package_id=$1 AND status='ACTIVE'
            """, connection, tx))
        {
            retire.Parameters.Add(new() { Value = id });
            await retire.ExecuteNonQueryAsync(ct);
        }
        PackagePriceVersionRecord price;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO billing.package_price_versions
                (package_id,version_number,display_name,price_amount,currency_code,credits,status,effective_from)
            VALUES ($1,$2,$3,$4,$5,$6,'ACTIVE',clock_timestamp())
            RETURNING id,package_id,version_number,display_name,price_amount,currency_code,credits
            """, connection, tx))
        {
            insert.Parameters.Add(new() { Value = id });
            insert.Parameters.Add(new() { Value = latest + 1 });
            insert.Parameters.Add(new() { Value = request.DisplayName.Trim() });
            insert.Parameters.Add(new() { Value = request.MajorAmount()!.Value });
            insert.Parameters.Add(new() { Value = request.Currency });
            insert.Parameters.Add(new() { Value = request.Credits });
            await using var row = await insert.ExecuteReaderAsync(ct);
            await row.ReadAsync(ct);
            price = ReadPublished(row);
        }
        await using (var save = new NpgsqlCommand("INSERT INTO billing.catalog_publications VALUES($1,$2,$3,$4)", connection, tx))
        {
            save.Parameters.Add(new() { Value = actor });
            save.Parameters.Add(new() { Value = key });
            save.Parameters.Add(new() { Value = fingerprint });
            save.Parameters.Add(new() { Value = price.Id });
            await save.ExecuteNonQueryAsync(ct);
        }
        await AuditStore.AppendAsync(connection, tx, AuditEvent.Create(AuditSource.Billing, actor, AuditAction.PriceChanged,
            AuditResource.Package, id.ToString(System.Globalization.CultureInfo.InvariantCulture), correlationId,
            resourceVersion: price.VersionNumber), ct);
        await tx.CommitAsync(ct);
        return new(price);
    }

    private static PackagePriceVersionRecord ReadPublished(NpgsqlDataReader row) =>
        new(row.GetGuid(0), row.GetInt32(1), row.GetInt32(2), row.GetString(3), row.GetDecimal(4),
            row.GetString(5), row.GetInt32(6), "ACTIVE");

    public async Task<PackageDto?> UpdateAsync(
        int id,
        UpdatePackageRequest request,
        CancellationToken cancellationToken,
        Guid actorAccountId,
        string? correlationId)
    {
        const string sql = """
            UPDATE billing_poc.service_packages
            SET name = @name,
                price = @price,
                credits = @credits,
                updated_at = now()
            WHERE id = @id
            RETURNING id, name, price, credits;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, tx);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("price", request.Price);
        command.Parameters.AddWithValue("credits", request.Credits);

        PackageDto updated;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            updated = new PackageDto(reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2), reader.GetInt32(3));
        }
        await AuditStore.AppendAsync(connection, tx, AuditEvent.Create(AuditSource.Billing, actorAccountId,
            AuditAction.PriceChanged, AuditResource.Package, id.ToString(System.Globalization.CultureInfo.InvariantCulture), correlationId), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated;
    }
}
