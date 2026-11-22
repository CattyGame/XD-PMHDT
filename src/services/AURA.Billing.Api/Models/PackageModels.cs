using AURA.Billing.Api.Data;
using System.Text.Json.Serialization;

namespace AURA.Billing.Api.Models;

public sealed record PackageDto(
    int Id,
    string Name,
    decimal Price,
    int Credits,
    Guid? PriceVersionId = null,
    string? Currency = null,
    int? Version = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PublishPriceRequest([property: JsonRequired] int ExpectedVersion, string DisplayName, [property: JsonRequired] long AmountMinor,
    string Currency, int Credits, string Reason)
{
    public decimal? MajorAmount() => Currency switch
    {
        "VND" when AmountMinor is >= 0 and <= 9_999_999_999 => AmountMinor,
        "USD" or "EUR" when AmountMinor is >= 0 and <= 999_999_999_999 => AmountMinor / 100m,
        _ => null
    };
}

public sealed record PricePublication(PackagePriceVersionRecord? Price, bool Replayed = false, bool Conflict = false);

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
