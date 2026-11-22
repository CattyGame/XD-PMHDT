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
    }

    public async Task<UpdatePackageResponse?> UpdateAsync(
        int id,
        UpdatePackageRequest request,
        CancellationToken cancellationToken,
        Guid actorAccountId,
        string? correlationId)
    {
        // The database update completes before cache invalidation.
        var updated =
            await _repository.UpdateAsync(id, request, cancellationToken, actorAccountId, correlationId);

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
