using System.Net;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsAccessClientTests
{
    [Fact]
    public async Task Unconfigured_profile_authority_fails_closed_with_retryable_dependency_error()
    {
        var resolver = new AnalyticsAccessUnavailableResolver();
        var request = new AnalyticsAccessRequest(Guid.NewGuid(), "ANALYTICS_READ", "CLINIC", Guid.NewGuid(), null, null);
        var error = await Assert.ThrowsAsync<UploadFailure>(
            () => resolver.ResolveAsync(request, CancellationToken.None));

        Assert.Equal(503, error.Status);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Profile_analytics_client_resolves_versioned_grant()
    {
        var tenantId = Guid.NewGuid();
        var clinicId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK, new
        {
            permissionVersion = 2,
            allowed = true,
            revoked = false,
            tenantId,
            clinicId,
            authorizedPatientIds = new[] { patientId }
        });
        var client = new ProfileAnalyticsAccessClient(new HttpClient(handler) { BaseAddress = new Uri("https://profile.test") }, new StubTokens());

        var grant = await client.ResolveAsync(new(Guid.NewGuid(), "ANALYTICS_READ", "DOCTOR_ASSIGNMENT", clinicId, null, "corr-1"), CancellationToken.None);

        Assert.Equal(2, grant.PermissionVersion);
        Assert.Equal(tenantId, grant.TenantId);
        Assert.Equal(clinicId, grant.ClinicId);
        Assert.Equal(new[] { patientId }, grant.AuthorizedPatientIds);
        Assert.Equal("/internal/v1/profile/operations/resolve", handler.LastPath);
    }

    [Fact]
    public async Task Profile_research_authority_receives_purpose_and_mask_request()
    {
        var clinicId = Guid.NewGuid();
        var handler = new StubHandler(HttpStatusCode.OK, new
        {
            permissionVersion = 1, allowed = true, revoked = false,
            tenantId = Guid.NewGuid(), clinicId, includeMasksAllowed = false
        });
        var client = new ProfileAnalyticsAccessClient(new HttpClient(handler) { BaseAddress = new Uri("https://profile.test") }, new StubTokens());

        await client.ResolveAsync(new AnalyticsAccessRequest(Guid.NewGuid(), "RESEARCH_EXPORT", "CLINIC",
            clinicId, null, "corr", "retinal research", true, false), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("retinal research", body.RootElement.GetProperty("purpose").GetString());
        Assert.True(body.RootElement.GetProperty("includeMasks").GetBoolean());
        Assert.False(body.RootElement.GetProperty("masksConsentGranted").GetBoolean());
    }

    [Theory]
    [InlineData("ADMIN_NO_PHI_WILDCARD", 403, "ERR_FORBIDDEN")]
    [InlineData("ROLE_DENIED", 403, "ERR_FORBIDDEN")]
    [InlineData("UNKNOWN_OPERATION", 403, "ERR_FORBIDDEN")]
    [InlineData("NOT_A_MEMBER", 404, "ERR_NOT_FOUND")]
    public async Task Profile_analytics_client_maps_denial_reasons_without_leaking_wildcard(string reason, int expectedStatus, string expectedCode)
    {
        var handler = new StubHandler(HttpStatusCode.OK, new
        {
            permissionVersion = 1,
            allowed = false,
            revoked = false,
            code = reason,
            reason
        });
        var client = new ProfileAnalyticsAccessClient(new HttpClient(handler) { BaseAddress = new Uri("https://profile.test") }, new StubTokens());

        var failure = await Assert.ThrowsAsync<UploadFailure>(() =>
            client.ResolveAsync(new(Guid.NewGuid(), "ANALYTICS_READ", "CLINIC", Guid.NewGuid(), null, null), CancellationToken.None));

        Assert.Equal(expectedStatus, failure.Status);
        Assert.Equal(expectedCode, failure.Code);
    }

    [Fact]
    public async Task Profile_analytics_client_preserves_only_role_denial_for_scope_fallback()
    {
        var handler = new StubHandler(HttpStatusCode.OK, new
        {
            permissionVersion = 1,
            allowed = false,
            revoked = false,
            code = "ROLE_DENIED",
            reason = "ROLE_DENIED"
        });
        var client = new ProfileAnalyticsAccessClient(new HttpClient(handler) { BaseAddress = new Uri("https://profile.test") }, new StubTokens());

        var failure = await Assert.ThrowsAsync<UploadFailure>(() =>
            client.ResolveAsync(new(Guid.NewGuid(), "ANALYTICS_READ", "CLINIC", Guid.NewGuid(), null, null), CancellationToken.None));

        Assert.Equal(new[] { new ErrorDetail("authority", "ROLE_DENIED") }, failure.Details);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Profile_analytics_client_fails_closed_on_invalid_permission_version(int permissionVersion)
    {
        var handler = new StubHandler(HttpStatusCode.OK, new
        {
            permissionVersion,
            allowed = true,
            revoked = false,
            tenantId = Guid.NewGuid(),
            clinicId = Guid.NewGuid()
        });
        var client = new ProfileAnalyticsAccessClient(new HttpClient(handler) { BaseAddress = new Uri("https://profile.test") }, new StubTokens());

        var failure = await Assert.ThrowsAsync<UploadFailure>(() =>
            client.ResolveAsync(new(Guid.NewGuid(), "ANALYTICS_READ", "CLINIC", Guid.NewGuid(), null, null), CancellationToken.None));

        Assert.Equal(503, failure.Status);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task Profile_analytics_client_fails_closed_when_profile_returns_503()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, new { error = "down" });
        var client = new ProfileAnalyticsAccessClient(new HttpClient(handler) { BaseAddress = new Uri("https://profile.test") }, new StubTokens());

        var failure = await Assert.ThrowsAsync<UploadFailure>(() =>
            client.ResolveAsync(new(Guid.NewGuid(), "ANALYTICS_READ", "CLINIC", Guid.NewGuid(), null, null), CancellationToken.None));

        Assert.Equal(503, failure.Status);
        Assert.True(failure.Retryable);
    }

    private sealed class StubTokens : IServiceTokenProvider
    {
        public Task<string> GetAsync(string audience, string scope, CancellationToken ct, string? correlationId = null) =>
            Task.FromResult("service-token");
    }

    private sealed class StubHandler(HttpStatusCode status, object payload) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }
    }
}
