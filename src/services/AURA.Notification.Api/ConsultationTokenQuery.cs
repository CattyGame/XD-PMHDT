using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AURA.Notification.Api;

public sealed record ConsultationTokenQueryResult(bool Present, bool Valid, string? Token);

public static class ConsultationTokenQuery
{
    public static bool IsQueryTokenTransport(HttpRequest request) =>
        string.Equals(request.Path.Value, "/hubs/consultations", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsGet(request.Method) &&
        (request.HttpContext.Features.Get<IHttpWebSocketFeature>()?.IsWebSocketRequest == true || request.Headers.Accept.Any(value =>
            value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true));

    public static ConsultationTokenQueryResult ExtractAndRedact(HttpRequest request)
    {
        var query = request.Query;
        var values = query["access_token"];
        if (values.Count == 0) return new ConsultationTokenQueryResult(false, true, null);

        var token = values.Count == 1 ? values[0] : null;
        var remaining = query.Where(pair => !string.Equals(pair.Key, "access_token", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        request.QueryString = QueryString.Create(remaining);
        request.Query = new QueryCollection(remaining);
        return new ConsultationTokenQueryResult(true, values.Count == 1 && !string.IsNullOrWhiteSpace(token), token);
    }
}
