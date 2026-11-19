using AURA.Billing.Api.Models;
using Npgsql;
using AURA.Common.Audit;

namespace AURA.Billing.Api.Data;

public sealed class PackageRepository(NpgsqlDataSource dataSource)
{
    private long _readCount;

    public long ReadCount => Interlocked.Read(ref _readCount);

    public async Task VerifyDatabaseAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, price, credits, updated_at
            FROM billing_poc.service_packages
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
            SELECT id, name, price, credits
            FROM billing_poc.service_packages
            ORDER BY id;
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
                reader.GetInt32(3)));
        }

        return packages;
    }

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
