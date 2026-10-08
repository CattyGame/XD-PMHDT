using AURA.Billing.Api.Models;
using Npgsql;

namespace AURA.Billing.Api.Data;

public sealed class PackageRepository(NpgsqlDataSource dataSource)
{
    private long _readCount;

    public long ReadCount => Interlocked.Read(ref _readCount);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS billing_poc;

            CREATE TABLE IF NOT EXISTS billing_poc.service_packages (
                id integer PRIMARY KEY,
                name varchar(100) NOT NULL,
                price numeric(12, 2) NOT NULL CHECK (price >= 0),
                credits integer NOT NULL CHECK (credits > 0),
                updated_at timestamptz NOT NULL DEFAULT now()
            );

            INSERT INTO billing_poc.service_packages
                (id, name, price, credits)
            VALUES
                (1, 'Basic', 100000, 10),
                (2, 'Standard', 250000, 30),
                (3, 'Clinic Demo', 700000, 100)
            ON CONFLICT (id) DO NOTHING;
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        CancellationToken cancellationToken)
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

        await using var command = dataSource.CreateCommand(sql);

        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("price", request.Price);
        command.Parameters.AddWithValue("credits", request.Credits);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PackageDto(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetDecimal(2),
            reader.GetInt32(3));
    }
}