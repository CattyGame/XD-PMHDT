using System.Net;
using System.Net.Http.Json;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aura.Worker.Analysis.Tests.Services;

public class ServiceTokenProviderTests
{
    [Fact]
    public async Task Concurrent_retries_reuse_only_the_matching_short_lived_machine_token()
    {
        var handler = new Issuer();
        var provider = Provider(handler, new ServiceAuthOptions { ClientSecret = "fixture-secret" });
        var values = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            provider.GetTokenAsync(i % 2 == 0 ? "aura-ai" : "aura-profile", i % 2 == 0 ? "ai:analyze" : "profile:resolve", default)));
        Assert.Equal(2, handler.Calls);
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(i % 2 == 0 ? "ai.test.token" : "profile.test.token", values[i]);
    }

    [Fact]
    public async Task Credential_rotation_invalidates_reuse_and_wrong_identity_cannot_use_the_cache()
    {
        var options = new ServiceAuthOptions { ClientSecret = "first-secret" };
        var handler = new Issuer();
        var provider = Provider(handler, options);
        await provider.GetTokenAsync("aura-ai", "ai:analyze", default);
        options.ClientSecret = "rotated-secret";
        await provider.GetTokenAsync("aura-ai", "ai:analyze", default);
        Assert.Equal(new[] { "first-secret", "rotated-secret" }, handler.Secrets);
        options.ClientId = "foreign-client";
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync("aura-ai", "ai:analyze", default));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_and_issuance_failure_does_not_fall_back_to_it()
    {
        var handler = new Issuer { Lifetime = 1 };
        var provider = Provider(handler, new ServiceAuthOptions { ClientSecret = "fixture-secret" });
        await provider.GetTokenAsync("aura-ai", "ai:analyze", default);
        handler.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetTokenAsync("aura-ai", "ai:analyze", default));
        Assert.Equal(2, handler.Calls);
    }

    private static ServiceTokenProvider Provider(Issuer handler, ServiceAuthOptions options) =>
        new(new Clients(handler), Options.Create(options));

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://identity.test") };
    }

    private sealed class Issuer : HttpMessageHandler
    {
        public int Calls;
        public int Lifetime = 300;
        public bool Fail;
        public List<string> Secrets = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var body = await request.Content!.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
            lock (Secrets) Secrets.Add(body.GetProperty("clientSecret").GetString()!);
            await Task.Delay(5, ct);
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                accessToken = body.GetProperty("audience").GetString() == "aura-ai" ? "ai.test.token" : "profile.test.token",
                tokenType = "Bearer", expiresIn = Lifetime }) };
        }
    }
}
