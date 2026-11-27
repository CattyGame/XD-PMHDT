using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Buffers;

namespace AURA.Analysis.Api.Analytics;

public interface IAnalyticsRevenueClient
{
    Task<AnalyticsRevenueSummary> GetClinicSummaryAsync(Guid clinicId, AnalyticsWindow window,
        string? userBearer, CancellationToken ct);
    Task<AnalyticsRevenueSummary> GetAdminSummaryAsync(AnalyticsWindow window,
        string? userBearer, CancellationToken ct);
}

public sealed class BillingAnalyticsClient(HttpClient http) : IAnalyticsRevenueClient
{
    public Task<AnalyticsRevenueSummary> GetClinicSummaryAsync(Guid clinicId, AnalyticsWindow window,
        string? userBearer, CancellationToken ct)
    {
        if (clinicId == Guid.Empty) throw new ArgumentException("A clinic is required.", nameof(clinicId));
        return GetAsync($"/api/v1/billing/summary?ownerType=CLINIC&ownerId={clinicId:D}&from={Uri.EscapeDataString(window.From.ToString("O"))}&to={Uri.EscapeDataString(window.To.ToString("O"))}",
            window, userBearer, ct);
    }

    public Task<AnalyticsRevenueSummary> GetAdminSummaryAsync(AnalyticsWindow window,
        string? userBearer, CancellationToken ct) =>
        GetAsync($"/api/v1/admin/billing/summary?from={Uri.EscapeDataString(window.From.ToString("O"))}&to={Uri.EscapeDataString(window.To.ToString("O"))}",
            window, userBearer, ct);

    private async Task<AnalyticsRevenueSummary> GetAsync(string path, AnalyticsWindow window,
        string? userBearer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userBearer) || userBearer.Length > 16_384
            || userBearer.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            return Unavailable("BILLING_USER_AUTH_REQUIRED");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userBearer);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return Unavailable("BILLING_SCOPE_FORBIDDEN");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Unavailable("BILLING_USER_AUTH_REQUIRED");
            if (!response.IsSuccessStatusCode)
                return Unavailable("BILLING_API_UNAVAILABLE");

            if (response.Content.Headers.ContentLength is > 65_536)
                return Unavailable("BILLING_RESPONSE_INVALID");
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (buffer.Length <= 65_536)
                {
                    var read = await body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, 65_537 - (int)buffer.Length)), ct);
                    if (read == 0) break;
                    await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
            if (buffer.Length > 65_536) return Unavailable("BILLING_RESPONSE_INVALID");
            var payload = JsonSerializer.Deserialize<BillingSummaryReply>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is null || payload.From.Offset != TimeSpan.Zero || payload.To.Offset != TimeSpan.Zero
                || payload.From != window.From || payload.To != window.To || payload.Groups is null
                || payload.Groups.Count > 200)
                return Unavailable("BILLING_RESPONSE_INVALID");

            var keys = new HashSet<(string Environment, string Currency)>();
            var groups = new List<AnalyticsRevenueGroup>(payload.Groups.Count);
            foreach (var group in payload.Groups)
            {
                if (group is null || group.Environment is not ("TEST" or "LIVE")
                    || group.Currency is null || group.Currency.Length != 3
                    || group.Currency.Any(c => c is < 'A' or > 'Z')
                    || group.PaidOrders < 0 || group.PaidAmount < 0
                    || !keys.Add((group.Environment, group.Currency)))
                    return Unavailable("BILLING_RESPONSE_INVALID");
                groups.Add(new AnalyticsRevenueGroup(group.Currency, group.Environment, group.PaidOrders, group.PaidAmount));
            }
            return new AnalyticsRevenueSummary("AVAILABLE", groups, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return Unavailable("BILLING_API_UNAVAILABLE");
        }
    }

    private static AnalyticsRevenueSummary Unavailable(string reason) => new("UNAVAILABLE", [], reason);
    private sealed record BillingSummaryReply(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<BillingMoneyGroup>? Groups);
    private sealed record BillingMoneyGroup(string? Environment, string? Currency, long PaidOrders, decimal PaidAmount);
}

public interface IAnalyticsSummaryCache
{
    Task<AnalyticsSummaryRow?> TryGetAsync(string key, CancellationToken ct);
    Task TrySetAsync(string key, AnalyticsSummaryRow value, TimeSpan ttl, CancellationToken ct);
}

public sealed class RedisAnalyticsSummaryCache(StackExchange.Redis.IConnectionMultiplexer redis) : IAnalyticsSummaryCache
{
    public async Task<AnalyticsSummaryRow?> TryGetAsync(string key, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var value = await redis.GetDatabase().StringGetAsync(key);
            ct.ThrowIfCancellationRequested();
            return value.HasValue ? System.Text.Json.JsonSerializer.Deserialize<AnalyticsSummaryRow>(value.ToString()) : null;
        }
        catch (StackExchange.Redis.RedisException) { return null; }
        catch (TimeoutException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task TrySetAsync(string key, AnalyticsSummaryRow value, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            await redis.GetDatabase().StringSetAsync(key, json, ttl);
        }
        catch (StackExchange.Redis.RedisException) { }
        catch (TimeoutException) { }
    }
}
