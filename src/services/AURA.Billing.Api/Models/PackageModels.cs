namespace AURA.Billing.Api.Models;

public sealed record PackageDto(
    int Id,
    string Name,
    decimal Price,
    int Credits);

public sealed record UpdatePackageRequest(
    string Name,
    decimal Price,
    int Credits);

public sealed record PackageCatalogResponse(
    IReadOnlyList<PackageDto> Data,
    string Source,
    bool CacheHit,
    int TtlSeconds,
    long DatabaseReadCount);

public sealed record UpdatePackageResponse(
    PackageDto Data,
    bool CacheInvalidationSucceeded);