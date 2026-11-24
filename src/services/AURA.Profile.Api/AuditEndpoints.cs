using System.Net.Http.Headers;
using System.Text.Json;
using AURA.Common.Audit;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace AURA.Profile.Api;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/v1/profile/audit-events", async Task<IResult> (
            HttpContext c, ServiceTokenValidator validator, AuditProjection projection) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            var values = c.Request.Headers.Authorization;
            if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var bearer)
                || !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                return Error(c, 401, "ERR_UNAUTHORIZED");
            var (valid, principal, code, _) = validator.ValidateToken(bearer.Parameter ?? "", "aura-profile", "profile:audit");
            if (!valid) return Error(c, code == "ERR_FORBIDDEN" ? 403 : 401, code ?? "ERR_UNAUTHORIZED");
            try
            {
                if (!c.Request.HasJsonContentType()) return Error(c, 415, "ERR_UNSUPPORTED_MEDIA_TYPE");
                if (c.Request.ContentLength > 8192) return Error(c, 413, "ERR_PAYLOAD_TOO_LARGE");
                using var bytes = new MemoryStream();
                var buffer = new byte[1024];
                int read;
                while ((read = await c.Request.Body.ReadAsync(buffer, c.RequestAborted)) > 0)
                {
                    if (bytes.Length + read > 8192) return Error(c, 413, "ERR_PAYLOAD_TOO_LARGE");
                    bytes.Write(buffer, 0, read);
                }
                using var document = JsonDocument.Parse(bytes.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Error(c, 400, "ERR_VALIDATION");
                var fields = document.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
                if (fields.Length != fields.Distinct(StringComparer.OrdinalIgnoreCase).Count()) return Error(c, 400, "ERR_VALIDATION");
                var e = document.RootElement.Deserialize<AuditEvent>(AuditJson.Options)
                    ?? throw new ArgumentException("Invalid audit event.");
                AuditEvent.Validate(e);
                if (principal?.FindFirst("client_id")?.Value != AuditStore.Schema(e.Source) + "-api")
                    return Error(c, 403, "ERR_AUDIT_SOURCE_MISMATCH");
                await projection.AcceptAsync(e, c.RequestAborted);
                return Results.Json(new AuditReceipt(e.EventId, AuditJson.Hash(e)), AuditJson.Options);
            }
            catch (AuditConflictException) { return Error(c, 409, "ERR_AUDIT_REPLAY_CONFLICT"); }
            catch (Exception e) when (e is JsonException or ArgumentException or BadHttpRequestException)
            { return Error(c, 400, "ERR_VALIDATION"); }
            catch (Exception e) when (e is NpgsqlException or TimeoutException)
            { return Error(c, 503, "ERR_AUDIT_STORAGE_UNAVAILABLE", true); }
        }).WithName("CollectAuditEvent");

        app.MapGet("/api/v1/admin/audit-events", async Task<IResult> (HttpContext c,
            UserIdentityResolver identities, AuditProjection projection, int? limit, string? cursor,
            DateTimeOffset? from, DateTimeOffset? to, string? source, string? action) =>
        {
            c.Response.Headers.CacheControl = "private, no-store";
            var identity = await identities.ResolveAsync(c.Request, c.RequestAborted);
            if (!identity.Succeeded) return Error(c, identity.StatusCode, identity.Code, identity.Retryable);
            if (!identity.Roles.Contains("SYSTEM_ADMIN", StringComparer.Ordinal)) return Error(c, 403, "ERR_FORBIDDEN");
            try
            {
                AuditSource? owner = null;
                AuditAction? operation = null;
                if (source is not null)
                {
                    if (!Enum.TryParse<AuditSource>(source, false, out var s) || !Enum.IsDefined(s)) throw new ArgumentException();
                    owner = s;
                }
                if (action is not null)
                {
                    if (!Enum.TryParse<AuditAction>(action, false, out var a) || !Enum.IsDefined(a)) throw new ArgumentException();
                    operation = a;
                }
                var page = await projection.QueryAsync(limit ?? 20, cursor, from, to, owner, operation, c.RequestAborted);
                return Results.Json(new { items = page.Items.Select(AuditView.From), page.NextCursor }, AuditJson.Options);
            }
            catch (ArgumentException) { return Error(c, 400, "ERR_VALIDATION"); }
            catch (Exception e) when (e is NpgsqlException or TimeoutException)
            { return Error(c, 503, "ERR_AUDIT_STORAGE_UNAVAILABLE", true); }
        }).WithName("ListRedactedAuditEvents");
    }

    private static IResult Error(HttpContext c, int status, string code, bool retryable = false) =>
        Results.Json(new { error = new { code, message = "Audit request could not be completed.",
            correlationId = Guid.TryParse(c.Items["CorrelationId"]?.ToString(), out var id) ? id.ToString("D") : Guid.NewGuid().ToString("D"), retryable } }, statusCode: status);
}
