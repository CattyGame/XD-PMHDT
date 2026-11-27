using System.Globalization;
using System.Text;
using AURA.Billing.Api.Auth;
using AURA.Billing.Api.Data;
using AURA.Billing.Api.Models;
using AURA.Billing.Api.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace AURA.Billing.Api;

public static class BillingCatalogEndpoints
{
    public static void MapBillingCatalog(this WebApplication app)
    {
        app.MapGet("/api/v1/billing/packages", async (PackageRepository repo, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var prices = await repo.GetAllAsync(ct);
            return Results.Ok(new PackageCatalogResponse(prices, "postgres", false, 0, repo.ReadCount));
        }).WithName("GetPackageCatalog").WithTags("Package Catalog").Produces<PackageCatalogResponse>();

        var admin = app.MapGroup("/api/v1/admin/billing");
        admin.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        admin.MapPost("/packages", (PublishPriceRequest body, HttpContext context, PackageRepository repo,
            SystemAdminAuthorizer auth, CancellationToken ct) => Publish(null, body, context, repo, auth, ct));
        admin.MapPost("/packages/{packageId:int}/price-versions", (int packageId, PublishPriceRequest body, HttpContext context,
            PackageRepository repo, SystemAdminAuthorizer auth, CancellationToken ct) => Publish(packageId, body, context, repo, auth, ct));
        admin.MapGet("/packages", async Task<IResult> (HttpContext context, PackageRepository repo,
            SystemAdminAuthorizer auth, int? limit, string? cursor, CancellationToken ct) =>
        {
            var rights = await auth.AuthorizeAsync(context.Request, ct);
            if (!rights.Allowed) return Error(context, rights.StatusCode, rights.Code, rights.Message, rights.Retryable);
            var take = limit ?? 20; var after = 0;
            if (take is < 1 or > 100) return Invalid(context);
            if (cursor is not null)
            {
                try
                {
                    if (cursor.Length>32 || !int.TryParse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)),
                        NumberStyles.None,CultureInfo.InvariantCulture,out after) || after<1) return Invalid(context);
                }
                catch (FormatException) { return Invalid(context); }
            }
            var rows = await repo.ListAdminAsync(after,take+1,ct);
            var items = rows.Take(take).ToArray();
            var nextCursor = rows.Count>take ? WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(
                items[^1].PackageId.ToString(CultureInfo.InvariantCulture))) : null;
            return Results.Ok(new { items, nextCursor });
        });
        admin.MapPatch("/packages/{packageId:int}/status", async Task<IResult> (int packageId, RetirePackageRequest body,
            HttpContext context, PackageRepository repo, SystemAdminAuthorizer auth, CancellationToken ct) =>
        {
            var rights = await auth.AuthorizeAsync(context.Request,ct);
            if (!rights.Allowed) return Error(context,rights.StatusCode,rights.Code,rights.Message,rights.Retryable);
            var keys = context.Request.Headers["Idempotency-Key"];
            if (packageId<1 || body.ExpectedVersion<1 || body.Status!="RETIRED" || string.IsNullOrWhiteSpace(body.Reason)
                || body.Reason.Length>250 || keys.Count!=1 || !Guid.TryParse(keys[0],out var key) || key==Guid.Empty) return Invalid(context);
            var result = await repo.RetireAsync(packageId,body,rights.AccountId,key,context.TraceIdentifier,ct);
            if (result.Conflict) return Error(context,409,"ERR_VERSION_CONFLICT","Package version or idempotency key changed.");
            if (result.Price is not { } price) return Error(context,404,"ERR_PACKAGE_NOT_FOUND","Package not found.");
            if (result.Replayed) context.Response.Headers["Idempotent-Replayed"]="true";
            return Results.Ok(new { packageId, priceVersionId=price.Id, version=price.VersionNumber, status="RETIRED" });
        });
        admin.MapGet("/summary", async Task<IResult> (HttpContext context, BillingHistoryRepository repo,
            SystemAdminAuthorizer auth, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var rights = await auth.AuthorizeAsync(context.Request, ct);
            if (!rights.Allowed) return Error(context, rights.StatusCode, rights.Code, rights.Message, rights.Retryable);
            var end = to ?? DateTimeOffset.UtcNow;
            if (end.Year < 2000) return Invalid(context);
            var start = from ?? end.AddDays(-30);
            if (!ValidRange(start, end)) return Invalid(context);
            return Results.Ok(new { from = start, to = end, groups = await repo.SummaryAsync(start.UtcDateTime, end.UtcDateTime, ct) });
        });

        app.MapGet("/api/v1/billing/summary", async Task<IResult> (HttpContext context, string ownerType, Guid ownerId,
            DateTimeOffset from, DateTimeOffset to, BillingOwnerAuthorizer auth, BillingHistoryRepository repo,
            BillingRuntimeOptions billing, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl="no-store";
            if (ownerType != "CLINIC" || !ValidOwner(ownerType,ownerId) || from.Offset != TimeSpan.Zero
                || to.Offset != TimeSpan.Zero || !ValidRange(from,to)) return Invalid(context);
            var rights = await auth.AuthorizeAsync(context.Request,ownerType,ownerId,false,ct);
            if (!rights.Allowed) return Error(context,rights.StatusCode,rights.Code,"Billing owner access denied.",rights.StatusCode==503);
            return Results.Ok(new { from,to,groups=await repo.OwnerSummaryAsync(ownerType,ownerId,rights.TenantId,
                billing.Environment,from.UtcDateTime,to.UtcDateTime,ct) });
        });
        app.MapGet("/api/v1/billing/orders", async Task<IResult> (HttpContext context, string ownerType, Guid ownerId,
            int? limit, string? cursor, BillingOwnerAuthorizer auth, BillingHistoryRepository repo, BillingRuntimeOptions billing, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var take = limit ?? 20;
            if (!ValidOwner(ownerType, ownerId) || take is < 1 or > 100 || !ReadCursor(cursor, out var before, out var beforeId))
                return Invalid(context);
            var rights = await auth.AuthorizeAsync(context.Request, ownerType, ownerId, false, ct);
            if (!rights.Allowed) return Error(context, rights.StatusCode, rights.Code, "Billing owner access denied.", rights.StatusCode == 503);
            var rows = await repo.OrdersAsync(ownerType, ownerId, rights.TenantId, billing.Environment, before, beforeId, take + 1, ct);
            return Page(rows, take, row => (row.CreatedAt, row.Id), billing.Environment);
        });
        app.MapGet("/api/v1/billing/usage", async Task<IResult> (HttpContext context, string ownerType, Guid ownerId,
            int? limit, string? cursor, DateTimeOffset from, DateTimeOffset to, BillingOwnerAuthorizer auth,
            BillingHistoryRepository repo, BillingRuntimeOptions billing, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var take = limit ?? 20;
            if (!ValidOwner(ownerType, ownerId) || !ValidRange(from, to) || take is < 1 or > 100 ||
                !ReadCursor(cursor, out var before, out var beforeId)) return Invalid(context);
            var rights = await auth.AuthorizeAsync(context.Request, ownerType, ownerId, false, ct);
            if (!rights.Allowed) return Error(context, rights.StatusCode, rights.Code, "Billing owner access denied.", rights.StatusCode == 503);
            var rows = await repo.UsageAsync(ownerType, ownerId, rights.TenantId, billing.Environment,
                from.UtcDateTime, to.UtcDateTime, before, beforeId, take + 1, ct);
            return Page(rows, take, row => (row.CreatedAt, row.Id), billing.Environment);
        });
    }

    private static async Task<IResult> Publish(int? packageId, PublishPriceRequest body, HttpContext context,
        PackageRepository repo, SystemAdminAuthorizer auth, CancellationToken ct)
    {
        var rights = await auth.AuthorizeAsync(context.Request, ct);
        if (!rights.Allowed) return Error(context, rights.StatusCode, rights.Code, rights.Message, rights.Retryable);
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !Guid.TryParse(keys[0], out var key) || key == Guid.Empty ||
            (packageId is null ? body.ExpectedVersion != 0 : packageId <= 0 || body.ExpectedVersion < 1) ||
            string.IsNullOrWhiteSpace(body.DisplayName) || body.DisplayName.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 250 || body.Credits <= 0 || body.MajorAmount() is null)
            return Invalid(context);
        var publication = await repo.PublishAsync(packageId, body, rights.AccountId, key, context.TraceIdentifier, ct);
        if (publication.Conflict) return Error(context, 409, "ERR_VERSION_CONFLICT", "Package version or idempotency key changed.");
        if (publication.Price is not { } price) return Error(context, 404, "ERR_PACKAGE_NOT_FOUND", "Package not found.");
        var result = new { price.PackageId, priceVersionId = price.Id, version = price.VersionNumber, price.DisplayName,
            price = price.PriceAmount, amountMinor = body.AmountMinor, currency = price.CurrencyCode, price.Credits, status = "ACTIVE" };
        if (publication.Replayed)
        {
            context.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Ok(result);
        }
        return Results.Json(result, statusCode: 201);
    }

    private static bool ValidOwner(string ownerType, Guid ownerId) => ownerType is "ACCOUNT" or "CLINIC" && ownerId != Guid.Empty;
    private static bool ValidRange(DateTimeOffset from, DateTimeOffset to) =>
        from.Year >= 2000 && to.Year <= 9998 && to > from && to - from <= TimeSpan.FromDays(366);

    private static bool ReadCursor(string? cursor, out DateTime at, out Guid id)
    {
        at = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        id = Guid.Empty;
        if (cursor is null) return true;
        if (cursor.Length > 96) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)).Split('|');
            return parts.Length == 2 &&
                DateTime.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) &&
                at.Kind == DateTimeKind.Utc && at.Year >= 2000 &&
                Guid.TryParseExact(parts[1], "D", out id) && id != Guid.Empty;
        }
        catch (FormatException) { return false; }
    }

    private static IResult Page<T>(List<T> rows, int take, Func<T, (DateTime At, Guid Id)> cursor, string environment)
    {
        var items = rows.Take(take).ToArray();
        string? next = null;
        if (rows.Count > take)
        {
            var (at, id) = cursor(items[^1]);
            next = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{at:O}|{id:D}")));
        }
        return Results.Ok(new { items, nextCursor = next, environment });
    }

    private static IResult Invalid(HttpContext context) => Error(context, 400, "ERR_VALIDATION", "Invalid billing request.");
    private static IResult Error(HttpContext context, int status, string code, string message, bool retryable = false) =>
        Results.Json(new BillingErrorEnvelope(new BillingErrorDetails(code, message, context.TraceIdentifier, retryable)),
            statusCode: status);
}
