using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AURA.Notification.Api;
using Microsoft.Extensions.Configuration;

namespace AURA.Notification.ConsultationChecks;

internal static class AuthorityChecks
{
    public static async Task RunAsync()
    {
        var actorAccountId = Guid.NewGuid();
        var accessRequest = new ConsultationAccessRequest(actorAccountId, Guid.NewGuid(), Guid.NewGuid(),
            "CONSULTATION_READ", Guid.NewGuid(), Guid.NewGuid());
        var calls = new List<(string Client, HttpRequestMessage Request, string? Body)>();
        var factory = new FakeFactory(name => CreateHttpClient(name, new DelegateHandler(async request =>
        {
            calls.Add((name, request, request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            if (name == "IdentityAuthority")
                return Json(HttpStatusCode.OK, "{\"accessToken\":\"profile-service-token\"}");
            return Json(HttpStatusCode.OK, DecisionJson(allowed: true, accessRequest));
        })));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuth:ClientId"] = "notification-api",
            ["ServiceAuth:ClientSecret"] = "test-secret"
        }).Build();
        var sut = new ConsultationAuthorityClient(new NotificationAuthorityClient(factory, config));
        var result = await sut.ResolveAsync(accessRequest, CancellationToken.None);
        Require(result.Allowed, "profile authority grant should be returned");
        var issuance = calls.Single(c => c.Client == "IdentityAuthority").Request;
        var issuanceBody = JsonDocument.Parse(calls.Single(c => c.Client == "IdentityAuthority").Body!).RootElement;
        Require(issuanceBody.GetProperty("audience").GetString() == "aura-profile", "service token audience must be aura-profile");
        Require(issuanceBody.GetProperty("scope").GetString() == "profile:resolve", "service token scope must be profile:resolve");
        var resolve = calls.Single(c => c.Client == "ProfileAuthority").Request;
        Require(resolve.RequestUri!.AbsolutePath == "/internal/v1/profile/consultation-access/resolve", "authority route must match Profile contract");
        Require(resolve.Headers.Authorization?.Parameter == "profile-service-token", "service token must be forwarded to Profile");
        var body = JsonDocument.Parse(calls.Single(c => c.Client == "ProfileAuthority").Body!).RootElement;
        Require(body.GetProperty("actorAccountId").GetGuid() == accessRequest.ActorAccountId, "actor account ID must be forwarded");
        Require(body.GetProperty("action").GetString() == "CONSULTATION_READ", "requested action must be forwarded");
        Console.WriteLine("PASS: Profile consultation authority uses the exact audience, scope, route, and request IDs.");

        await ExpectDeniedAsync(DecisionJson(allowed: false, accessRequest));
        await ExpectFailureAsync("{\"allowed\":true}");
        await ExpectFailureAsync(DecisionJson(allowed: true, accessRequest, mismatchAnalysis: true));
        await ExpectFailureAsync(DecisionJson(allowed: true, accessRequest, conflictingRevocation: true));
        await ExpectFailureAsync(DecisionJson(allowed: true, accessRequest, mismatchedActor: true));
        await ExpectFailureAsync(null, HttpStatusCode.ServiceUnavailable);

        async Task ExpectDeniedAsync(string json)
        {
            var denied = CreateSut((_, _) => Json(HttpStatusCode.OK, json));
            var decision = await denied.ResolveAsync(accessRequest, CancellationToken.None);
            Require(!decision.Allowed, "denied authority result must not be converted into a grant");
            Console.WriteLine("PASS: Profile denial remains an explicit deny.");
        }

        async Task ExpectFailureAsync(string? json, HttpStatusCode status = HttpStatusCode.OK)
        {
            var failed = CreateSut((_, _) => Json(status, json));
            try
            {
                await failed.ResolveAsync(accessRequest, CancellationToken.None);
                throw new InvalidOperationException("Expected authority response to fail closed.");
            }
            catch (HttpRequestException) { }
            catch (JsonException) { }
            Console.WriteLine("PASS: malformed, mismatched, or unavailable Profile authority fails closed.");
        }

        ConsultationAuthorityClient CreateSut(Func<string, HttpRequestMessage, HttpResponseMessage> profile)
        {
            var localFactory = new FakeFactory(name => CreateHttpClient(name, new DelegateHandler(request => Task.FromResult(
                name == "IdentityAuthority"
                    ? Json(HttpStatusCode.OK, "{\"accessToken\":\"profile-service-token\"}")
                    : profile(name, request)))));
            return new ConsultationAuthorityClient(new NotificationAuthorityClient(localFactory, config));
        }
    }

    private static string DecisionJson(bool allowed, ConsultationAccessRequest request, bool mismatchAnalysis = false,
        bool conflictingRevocation = false, bool mismatchedActor = false) => JsonSerializer.Serialize(new
    {
        allowed,
        revoked = conflictingRevocation,
        code = allowed ? null : "NOT_AUTHORIZED_PARTICIPANT",
        reason = allowed ? null : "NOT_ASSIGNED",
        tenantId = Guid.NewGuid(),
        clinicContextId = request.ClinicContextId,
        patientId = request.PatientId,
        patientAccountId = mismatchedActor ? Guid.NewGuid() : request.ActorAccountId,
        analysisId = mismatchAnalysis ? Guid.NewGuid() : request.AnalysisId,
        participantRole = "PATIENT",
        doctorAccountId = request.DoctorAccountId,
        assignmentId = Guid.NewGuid(),
        permissionVersion = 1,
        eligibleDoctors = Array.Empty<object>()
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string? json) => new(status)
    {
        Content = new StringContent(json ?? "", Encoding.UTF8, "application/json")
    };

    private static HttpClient CreateHttpClient(string name, HttpMessageHandler handler) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri(name == "IdentityAuthority" ? "http://identity/" : "http://profile/")
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeFactory(Func<string, HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create(name);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
