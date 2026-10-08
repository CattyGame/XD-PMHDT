using System.Text.Json;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using StackExchange.Redis;

namespace AURA.Billing.Api.Services;

public sealed class PackageCatalogService
{
    private readonly PackageRepository _repository;
    private readonly IDatabase _cache;
    private readonly ILogger<PackageCatalogService> _logger;
    private readonly string _cacheKey;
    private readonly int _ttlSeconds;

    public PackageCatalogService(
        PackageRepository repository,
        IConnectionMultiplexer redis,
        IConfiguration configuration,
        ILogger<PackageCatalogService> logger)
    {
        _repository = repository;
        _cache = redis.GetDatabase();
        _logger = logger;

        _cacheKey = configuration["Cache:PackageCatalogKey"]
            ?? throw new InvalidOperationException(
                "Missing Cache:PackageCatalogKey.");

        _ttlSeconds =
            configuration.GetValue<int>("Cache:PackageCatalogTtlSeconds");

        if (_ttlSeconds <= 0)
        {
            throw new InvalidOperationException(
                "Cache TTL must be greater than zero.");
        }
    }

    public async Task<PackageCatalogResponse> GetAllAsync(
        CancellationToken cancellationToken)
    {
        var redisFailed = false;

        try
        {
            var cached = await _cache.StringGetAsync(_cacheKey);

            if (cached.HasValue)
            {
                var packages =
                    JsonSerializer.Deserialize<List<PackageDto>>(
                        cached.ToString());

                if (packages is not null)
                {
                    _logger.LogInformation(
                        "Package catalog cache HIT.");

                    return new PackageCatalogResponse(
                        packages,
                        "redis",
                        true,
                        _ttlSeconds,
                        _repository.ReadCount);
                }
            }

            _logger.LogInformation("Package catalog cache MISS.");
        }
        catch (RedisException)
        {
            redisFailed = true;

            _logger.LogWarning(
                "Redis read failed. Falling back to PostgreSQL.");
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Invalid cached JSON. Reloading from PostgreSQL.");
        }

        var data = await _repository.GetAllAsync(cancellationToken);

        try
        {
            await _cache.StringSetAsync(
                _cacheKey,
                JsonSerializer.Serialize(data),
                TimeSpan.FromSeconds(_ttlSeconds));
        }
        catch (RedisException)
        {
            redisFailed = true;

            _logger.LogWarning(
                "Redis write failed. Returning PostgreSQL data.");
        }

        return new PackageCatalogResponse(
            data,
            redisFailed ? "postgres-fallback" : "postgres",
            false,
            _ttlSeconds,
            _repository.ReadCount);
    }

    public async Task<UpdatePackageResponse?> UpdateAsync(
        int id,
        UpdatePackageRequest request,
        CancellationToken cancellationToken)
    {
        // The database update completes before cache invalidation.
        var updated =
            await _repository.UpdateAsync(id, request, cancellationToken);

        if (updated is null)
        {
            return null;
        }

        var invalidated = true;

        try
        {
            // A missing key is also a successful invalidation outcome.
            await _cache.KeyDeleteAsync(_cacheKey);

            _logger.LogInformation(
                "Package {PackageId} updated; catalog cache invalidated.",
                id);
        }
        catch (RedisException)
        {
            invalidated = false;

            _logger.LogWarning(
                "Package {PackageId} updated in PostgreSQL, " +
                "but cache invalidation failed.",
                id);
        }

        return new UpdatePackageResponse(updated, invalidated);
    }
}