using System.Text.Json;
using AURA.Notification.Api;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Http;

namespace AURA.Notification.ConsultationChecks;

internal static class RuleChecks
{
    public static void Run()
    {
        Require(!ConsultationMessageRules.IsValidText(null), "null message must be rejected");
        Require(!ConsultationMessageRules.IsValidText("   "), "whitespace-only message must be rejected");
        Require(ConsultationMessageRules.IsValidText(new string('a', 4000)), "4000 ASCII characters must be accepted");
        Require(!ConsultationMessageRules.IsValidText(new string('a', 4001)), "4001 characters must be rejected");
        var fourByteText = string.Concat(Enumerable.Repeat("😀", 4000));
        Require(ConsultationMessageRules.IsValidText(fourByteText), "4000 four-byte Unicode scalars meet the exact UTF-8 limit");
        Require(System.Text.Encoding.UTF8.GetByteCount(fourByteText) == 16000,
            "the largest valid 4000-scalar four-byte payload is 16000 UTF-8 bytes");
        Require(!ConsultationMessageRules.IsValidText(fourByteText + "😀"), "more than 4000 Unicode scalars must be rejected");
        Require(typeof(FirebaseToken).GetProperty(nameof(FirebaseToken.ExpirationTimeSeconds)) is not null,
            "hub expiry must use FirebaseAdmin's normalized exp property, not the custom-claims dictionary");
        Require(File.ReadAllText("src/services/AURA.Notification.Api/Program.cs")
                .Contains("verifiedToken.ExpirationTimeSeconds", StringComparison.Ordinal),
            "Notification must copy the normalized Firebase token expiration into the hub principal");
        var json = JsonSerializer.Serialize(new { text = "<script>alert(1)</script>" });
        Require(!json.Contains("<script>", StringComparison.Ordinal), "JSON serializer must not emit script text as HTML markup");
        Console.WriteLine("PASS: Consultation text enforces scalar and UTF-8 byte limits and JSON serialization escapes markup.");

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?access_token=secret-value&id=abc");
        var token = ConsultationTokenQuery.ExtractAndRedact(context.Request);
        Require(token is { Present: true, Valid: true, Token: "secret-value" }, "single hub access token should be extracted");
        Require(!context.Request.QueryString.Value!.Contains("secret-value", StringComparison.Ordinal), "redacted URL must not retain the token");
        Require(!context.Request.Query.ContainsKey("access_token") && context.Request.Query["id"] == "abc", "remaining SignalR query values should be preserved");

        var duplicate = new DefaultHttpContext();
        duplicate.Request.QueryString = new QueryString("?access_token=first&access_token=second");
        var invalid = ConsultationTokenQuery.ExtractAndRedact(duplicate.Request);
        Require(invalid.Present && !invalid.Valid && duplicate.Request.QueryString.Value == "", "duplicate token parameters should be rejected and removed");
        var sse = new DefaultHttpContext();
        sse.Request.Method = "GET";
        sse.Request.Path = "/hubs/consultations";
        sse.Request.Headers["Accept"] = "text/event-stream";
        Require(ConsultationTokenQuery.IsQueryTokenTransport(sse.Request), "query token should be accepted only for SignalR SSE transport");
        sse.Request.Path = "/hubs/consultations/negotiate";
        Require(!ConsultationTokenQuery.IsQueryTokenTransport(sse.Request), "SignalR negotiation subpaths must use the Authorization header");
        sse.Request.Path = "/hubs/consultations";
        sse.Request.Method = "POST";
        Require(!ConsultationTokenQuery.IsQueryTokenTransport(sse.Request), "non-GET hub requests must reject query credentials");
        Console.WriteLine("PASS: SignalR query access_token is removed before downstream processing; duplicate values are rejected.");

        using var gateway = JsonDocument.Parse(File.ReadAllText("src/gateway/AURA.ApiGateway/appsettings.json"));
        var routes = gateway.RootElement.GetProperty("ReverseProxy").GetProperty("Routes");
        Require(routes.GetProperty("consultation-api").GetProperty("ClusterId").GetString() == "notification-cluster",
            "REST consultation route must target Notification");
        Require(routes.GetProperty("consultation-hub").GetProperty("ClusterId").GetString() == "notification-cluster",
            "SignalR route must target Notification");
        Require(routes.GetProperty("consultation-hub").GetProperty("Match").GetProperty("Path").GetString() == "/hubs/consultations/{**catch-all}",
            "SignalR route must forward negotiate and websocket paths");
        var notificationOpenApi = File.ReadAllText("contracts/notification-openapi.yaml");
        Require(notificationOpenApi.Contains("required: [id, status, version]", StringComparison.Ordinal) &&
                notificationOpenApi.Contains("Retry-After:", StringComparison.Ordinal),
            "Notification OpenAPI must document the receipt schema and throttling retry header");
        Require(File.ReadAllText("src/gateway/AURA.ApiGateway/GatewayFirebaseAuthMiddleware.cs")
                .Contains("IsHubQueryTokenTransport", StringComparison.Ordinal),
            "gateway must restrict query credentials to SignalR streaming transports");
        var compose = File.ReadAllText("compose.dev.yaml");
        var notificationService = compose.Split("  notification-api:", StringSplitOptions.None).Last();
        Require(notificationService.Contains("Services__Profile__BaseUrl: \"http://profile-api:8080\"", StringComparison.Ordinal),
            "Notification must be configured with Profile authority URL");
        Require(notificationService.Contains("ConnectionStrings__Redis:", StringComparison.Ordinal),
            "Notification must use the Redis SignalR backplane");
        var failurePolicyType = typeof(ConsultationHub).Assembly.GetType("AURA.Notification.Api.ConsultationConnectionFailurePolicy");
        Require(failurePolicyType is not null, "SignalR authority timeout handling policy must be available");
        var shouldClose = failurePolicyType!.GetMethod("ShouldCloseForAuthorityFailure");
        Require(shouldClose is not null, "SignalR authority timeout handling policy must be callable");
        Require((bool)shouldClose!.Invoke(null, [new TaskCanceledException(), CancellationToken.None])!,
            "an HTTP client timeout must revoke and close the session");
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        Require(!(bool)shouldClose.Invoke(null, [new OperationCanceledException(), stopping.Token])!,
            "host shutdown cancellation must not be treated as a permission revocation");
        using var hubContract = JsonDocument.Parse(File.ReadAllText("contracts/consultation-hub-v1.schema.json"));
        var eventTypes = hubContract.RootElement.GetProperty("oneOf").EnumerateArray()
            .Select(item => item.GetProperty("properties").GetProperty("type").GetProperty("const").GetString()).ToHashSet();
        Require(eventTypes.SetEquals(new[] { "JoinConversation", "SendMessage", "MarkRead", "MessagePersisted", "AccessRevoked", "ReadCursorAdvanced" }),
            "SignalR contract must retain the six agreed method/event names");
        Console.WriteLine("PASS: Gateway consultation routes and SignalR method/event schema match the shared contract.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
