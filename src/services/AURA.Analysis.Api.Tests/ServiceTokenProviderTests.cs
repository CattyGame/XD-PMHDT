using System.Net;
using System.Text.Json;
using AURA.Analysis.Api.Upload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public class ServiceTokenProviderTests
{
    [Fact]
    public async Task Concurrent_requests_reuse_only_the_same_target_service_token()
    {
        var (provider, handler, _) = Make();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => provider.GetAsync("aura-profile", "profile:resolve", default)));
        Assert.Equal(1, handler.Calls);
        await provider.GetAsync("aura-identity", "identity:context", default);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Token_inside_refresh_window_is_not_reused_when_issuer_fails()
    {
        var (provider, handler, _) = Make();
        handler.Reply = (_, _, _) => Task.FromResult(ServiceReplyHandler.Json(
            """{"accessToken":"short-lived","tokenType":"Bearer","expiresIn":1}"""));
        Assert.Equal("short-lived", await provider.GetAsync("aura-profile", "profile:resolve", default));
        handler.Reply = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<UploadFailure>(() => provider.GetAsync("aura-profile", "profile:resolve", default));
        Assert.Equal(2, handler.Calls);
    }
    private static (ServiceTokenProvider Provider, ServiceReplyHandler Handler, ServiceHttpFactory Factory) Make(
        string? clientId = "analysis-api", string? secret = "fixture-only-secret")
    {
        var handler = new ServiceReplyHandler();
        handler.Reply = (_, _, _) => Task.FromResult(ServiceReplyHandler.Json(
            """{"accessToken":"issued-service-token","tokenType":"Bearer","expiresIn":300}"""));
        var factory = new ServiceHttpFactory(new HttpClient(handler) { BaseAddress = new Uri("http://identity.test") });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceAuth:ClientId"] = clientId, ["ServiceAuth:ClientSecret"] = secret
        }).Build();
        return (new ServiceTokenProvider(factory, config), handler, factory);
    }

    [Fact]
    public async Task Sends_own_credentials_and_exact_target_scope()
    {
        var (provider, handler, factory) = Make();
        Assert.Equal("issued-service-token", await provider.GetAsync("aura-billing", "billing:reserve", default, "corr-1"));
        Assert.Equal("AnalysisIdentity", Assert.Single(factory.Names));
        Assert.Equal("/internal/v1/identity/service-tokens", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("corr-1", Assert.Single(handler.Request.Headers.GetValues("X-Correlation-Id")));
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("analysis-api", json.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("fixture-only-secret", json.RootElement.GetProperty("clientSecret").GetString());
        Assert.Equal("aura-billing", json.RootElement.GetProperty("audience").GetString());
        Assert.Equal("billing:reserve", json.RootElement.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData(null, "fixture-only-secret")]
    [InlineData("profile-api", "fixture-only-secret")]
    [InlineData("analysis-api", null)]
    [InlineData("analysis-api", " ")]
    public async Task Missing_configuration_never_uses_a_default_secret(string? client, string? secret)
    {
        var (provider, handler, _) = Make(client, secret);
        var failure = await Assert.ThrowsAsync<UploadFailure>(() => provider.GetAsync("aura-profile", "profile:resolve", default));
        Assert.Equal(503, failure.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("aura-billing", "billing:capture")]
    [InlineData("aura-profile", "identity:context")]
    [InlineData("unknown", "billing:reserve")]
    public async Task Does_not_request_unapproved_targets_or_scopes(string audience, string scope)
    {
        var (provider, handler, _) = Make();
        await Assert.ThrowsAsync<UploadFailure>(() => provider.GetAsync(audience, scope, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(307)]
    [InlineData(500)]
    public async Task Issuance_failure_is_dependency_error_without_automatic_retry(int status)
    {
        var (provider, handler, _) = Make();
        handler.Reply = (_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        var failure = await Assert.ThrowsAsync<UploadFailure>(() => provider.GetAsync("aura-identity", "identity:context", default));
        Assert.Equal(503, failure.Status);
        Assert.True(failure.Retryable);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("""{"accessToken":"t","tokenType":"Basic","expiresIn":300}""")]
    [InlineData("""{"accessToken":"t","tokenType":"Bearer","expiresIn":0}""")]
    [InlineData("""{"accessToken":"t","tokenType":"Bearer","expiresIn":301}""")]
    [InlineData("""{"accessToken":"line\nbreak","tokenType":"Bearer","expiresIn":300}""")]
    [InlineData("""{"accessToken":"bad,token","tokenType":"Bearer","expiresIn":300}""")]
    [InlineData("""{"accessToken":"bad\u0000token","tokenType":"Bearer","expiresIn":300}""")]
    public async Task Rejects_malformed_issuance_reply(string body)
    {
        var (provider, handler, _) = Make();
        handler.Reply = (_, _, _) => Task.FromResult(ServiceReplyHandler.Json(body));
        Assert.Equal(503, (await Assert.ThrowsAsync<UploadFailure>(() =>
            provider.GetAsync("aura-profile", "profile:resolve", default))).Status);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_issue_a_token()
    {
        var (provider, handler, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync("aura-profile", "profile:resolve", cts.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Dependency_timeout_is_retryable()
    {
        var (provider, handler, _) = Make();
        handler.Reply = (_, _, _) => throw new TaskCanceledException("simulated timeout");
        Assert.True((await Assert.ThrowsAsync<UploadFailure>(() =>
            provider.GetAsync("aura-profile", "profile:resolve", default))).Retryable);
    }

    [Fact]
    public void Registered_identity_http_client_disables_redirects()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:Identity:BaseUrl"] = "http://identity.test"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddAnalysisServiceClients(config);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("AnalysisIdentity");
        Assert.Equal(new Uri("http://identity.test"), client.BaseAddress);
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("AnalysisIdentity");
        while (handler is DelegatingHandler d) handler = d.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }
}
