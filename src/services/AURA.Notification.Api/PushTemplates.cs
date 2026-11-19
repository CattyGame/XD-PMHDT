using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AURA.Common.Audit;
using Npgsql;

namespace AURA.Notification.Api;

public sealed record PushTemplate(Guid Id, string EventType, string Title, string Body, bool Enabled, int Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PushTemplateChange(int ExpectedVersion, string Title, string Body, [property: JsonRequired] bool Enabled, string Reason);

public sealed class PushTemplates(NpgsqlDataSource source)
{
    public const string DefaultBody = "Có thông báo mới. Đăng nhập để xem chi tiết.";
    public const string DefaultTitle = "AURA";

    // A free-text field with a blacklist cannot prove that a name or clinical detail was excluded.
    public static bool IsAllowed(string? title, string? body) =>
        title is "AURA" or "{{app_name}}" or "Thông báo từ {{app_name}}" &&
        body is DefaultBody or "Đăng nhập để xem chi tiết." or "{{event_label}}. Đăng nhập để xem chi tiết.";

    public static (string Title, string Body) Render(PushTemplate? template)
    {
        if (template is null || !IsAllowed(template.Title, template.Body)) return (DefaultTitle, DefaultBody);
        var label = template.EventType == "PAYMENT_SUCCESS" ? "Có cập nhật thanh toán" : "Có thông báo mới";
        return (template.Title.Replace("{{app_name}}", DefaultTitle, StringComparison.Ordinal),
            template.Body.Replace("{{event_label}}", label, StringComparison.Ordinal));
    }

    public async Task<PushTemplate?> ForEventAsync(string eventType, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT id,event_type,title,body,enabled,version FROM notification.push_templates WHERE event_type=$1
            """);
        command.Parameters.Add(new() { Value = eventType });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<PushTemplate>> ListAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT id,event_type,title,body,enabled,version FROM notification.push_templates ORDER BY event_type
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var templates = new List<PushTemplate>();
        while (await reader.ReadAsync(ct)) templates.Add(Read(reader));
        return templates;
    }

    public async Task<PushTemplate?> UpdateAsync(Guid id, PushTemplateChange change, Guid actor, string correlation, CancellationToken ct)
    {
        if (!IsAllowed(change.Title, change.Body)) throw new ArgumentException("Unsupported push template.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            UPDATE notification.push_templates SET title=$1,body=$2,enabled=$3,version=version+1
            WHERE id=$4 AND version=$5 RETURNING id,event_type,title,body,enabled,version
            """, connection, tx);
        foreach (var value in new object[] { change.Title, change.Body, change.Enabled, id, change.ExpectedVersion })
            command.Parameters.Add(new() { Value = value });
        PushTemplate updated;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            updated = Read(reader);
        }
        await AuditStore.AppendAsync(connection, tx, AuditEvent.Create(AuditSource.Notification, actor,
            AuditAction.NotificationTemplateChanged, AuditResource.NotificationTemplate, id.ToString("D"), correlation,
            resourceVersion: updated.Version), ct);
        await tx.CommitAsync(ct);
        return updated;
    }

    private static PushTemplate Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetBoolean(4), r.GetInt32(5));
}

public static class PushTemplateEndpoints
{
    public static void MapPushTemplates(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/notification-templates");
        admin.MapGet("", async Task<IResult> (HttpContext context, IHttpClientFactory clients, PushTemplates templates, CancellationToken ct) =>
        {
            var (status, _) = await Authorize(context, clients, ct);
            return status == 200 ? Results.Ok(new { items = await templates.ListAsync(ct) }) : Error(status);
        });
        admin.MapPut("/{templateId:guid}", async Task<IResult> (Guid templateId, PushTemplateChange body, HttpContext context,
            IHttpClientFactory clients, PushTemplates templates, CancellationToken ct) =>
        {
            var (status, actor) = await Authorize(context, clients, ct);
            if (status != 200) return Error(status);
            if (templateId == Guid.Empty || body.ExpectedVersion < 1 || !PushTemplates.IsAllowed(body.Title, body.Body) ||
                string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 250)
                return Results.Json(new { error = new { code = "ERR_INVALID_PUSH_TEMPLATE" } }, statusCode: 400);
            var changed = await templates.UpdateAsync(templateId, body, actor, context.TraceIdentifier, ct);
            return changed is not null ? Results.Ok(changed) :
                Results.Json(new { error = new { code = "ERR_VERSION_CONFLICT" } }, statusCode: 409);
        });
    }

    private static async Task<(int Status, Guid Actor)> Authorize(HttpContext context, IHttpClientFactory clients, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers[0], out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(bearer.Parameter))
            return (401, Guid.Empty);
        try
        {
            using var query = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
            query.Headers.Authorization = bearer;
            using var response = await clients.CreateClient("IdentityAuthority").SendAsync(query, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (401, Guid.Empty);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.NotFound) return (403, Guid.Empty);
            if (!response.IsSuccessStatusCode) return (503, Guid.Empty);
            var identity = await response.Content.ReadFromJsonAsync<AdminIdentity>(cancellationToken: ct);
            return identity is { AccountStatus: "ACTIVE" } && identity.AccountId != Guid.Empty &&
                identity.Roles?.Contains("SYSTEM_ADMIN", StringComparer.Ordinal) == true
                ? (200, identity.AccountId) : (403, Guid.Empty);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return (503, Guid.Empty);
        }
    }

    private static IResult Error(int status) => Results.Json(new { error = new
        { code = status == 401 ? "ERR_UNAUTHORIZED" : status == 403 ? "ERR_FORBIDDEN" : "ERR_AUTHORITY_UNAVAILABLE" } }, statusCode: status);

    private sealed record AdminIdentity(Guid AccountId, string? AccountStatus, string[]? Roles);
}
