using System.Net;
using System.Net.Http.Json;
using AURA.Notification.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AURA.M1.RegressionTests;

public class NotificationAuthorityTokenTests
{
    [Fact]
    public async Task Reuses_machine_token_but_resolves_every_resource_and_refreshes_after_secret_change()
    {
        var handler = new AuthorityHandler(300);
        var configuration = Config();
        var client = new NotificationAuthorityClient(new Clients(handler), configuration);
        using var first = await client.SendAsync("aura-analysis", "analysis:notify", "/context", new { }, default);
        using var second = await client.SendAsync("aura-analysis", "analysis:notify", "/context", new { }, default);
        Assert.True((await first.Content.ReadFromJsonAsync<Decision>())!.Allowed);
        Assert.False((await second.Content.ReadFromJsonAsync<Decision>())!.Allowed);
        Assert.Equal(2, handler.Resolutions);
        Assert.Equal(1, handler.Issued);
        configuration["ServiceAuth:ClientSecret"] = "rotated-fixture-secret";
        using var third = await client.SendAsync("aura-analysis", "analysis:notify", "/context", new { }, default);
        Assert.Equal(2, handler.Issued);
    }

    [Fact]
    public async Task Invalid_machine_token_is_not_cached_or_sent_to_a_resource_owner()
    {
        foreach (var token in new[] { "invalid token", new string('a', 16385) })
        {
            var handler = new AuthorityHandler(300) { AccessToken = token };
            var client = new NotificationAuthorityClient(new Clients(handler), Config());
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(
                "aura-analysis", "analysis:notify", "/context", new { }, default));
            Assert.Equal(0, handler.Resolutions);
        }
    }

    [Fact]
    public async Task Concurrent_requests_share_one_refresh_and_keep_all_authority_calls()
    {
        var handler = new AuthorityHandler(300) { DelayIssue = true };
        var client = new NotificationAuthorityClient(new Clients(handler), Config());
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.SendAsync("aura-analysis", "analysis:notify", "/context", new { }, default)));
        foreach (var response in responses) response.Dispose();
        Assert.Equal(1, handler.Issued);
        Assert.Equal(8, handler.Resolutions);
    }

    [Fact]
    public async Task Short_lived_tokens_are_refreshed_and_audiences_never_share_tokens()
    {
        var handler = new AuthorityHandler(20);
        var client = new NotificationAuthorityClient(new Clients(handler), Config());
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.SendAsync("aura-analysis", "analysis:notify", "/context", new { }, default);
        }
        Assert.Equal(2, handler.Issued);
        handler.Lifetime = 300;
        using var analysis = await client.SendAsync("aura-analysis", "analysis:notify", "/context", new { }, default);
        using var billing = await client.SendAsync("aura-billing", "billing:notify", "/context", new { }, default);
        Assert.Equal(4, handler.Issued);
    }

    static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ServiceAuth:ClientId"] = "notification-api", ["ServiceAuth:ClientSecret"] = "fixture-secret"
    }).Build();
    sealed record Decision(bool Allowed);
    sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://fixture") };
    }
    sealed class AuthorityHandler(int lifetime) : HttpMessageHandler
    {
        public int Issued, Resolutions;
        public int Lifetime = lifetime;
        public string AccessToken = "fixture-token";
        public bool DelayIssue;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("service-tokens"))
            {
                Interlocked.Increment(ref Issued);
                if (DelayIssue) await Task.Delay(10, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { accessToken = AccessToken, tokenType = "Bearer", expiresIn = Lifetime }) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new Decision(Interlocked.Increment(ref Resolutions) == 1)) };
        }
    }
}
