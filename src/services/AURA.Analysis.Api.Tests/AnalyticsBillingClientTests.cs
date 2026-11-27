using System.Net;
using AURA.Analysis.Api.Analytics;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsBillingClientTests
{
    [Fact]
    public async Task Clinic_summary_forwards_user_token_and_keeps_currency_environment_groups_separate()
    {
        var window = AnalyticsWindow.Create(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        var handler = new Handler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("the-user-token", request.Headers.Authorization?.Parameter);
            Assert.Contains("ownerType=CLINIC", request.RequestUri!.Query);
            return Task.FromResult(Json(HttpStatusCode.OK,
                $$"""{"from":"{{window.From:O}}","to":"{{window.To:O}}","groups":[{"environment":"TEST","currency":"USD","paidOrders":2,"paidAmount":12.5},{"environment":"LIVE","currency":"USD","paidOrders":1,"paidAmount":7.5},{"environment":"TEST","currency":"VND","paidOrders":3,"paidAmount":100}] }"""));
        });
        var client = new BillingAnalyticsClient(new HttpClient(handler) { BaseAddress = new Uri("http://billing.test") });

        var result = await client.GetClinicSummaryAsync(Guid.NewGuid(), window, "the-user-token", default);

        Assert.Equal("AVAILABLE", result.Status);
        Assert.Equal(3, result.Groups.Count);
        Assert.Contains(result.Groups, group => group.Environment == "TEST" && group.Currency == "USD" && group.PaidOrders == 2);
        Assert.Contains(result.Groups, group => group.Environment == "LIVE" && group.Currency == "USD" && group.Amount == 7.5m);
    }

    [Fact]
    public async Task Billing_forbidden_is_unavailable_without_fallback_data()
    {
        var window = AnalyticsWindow.Create(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        var client = new BillingAnalyticsClient(new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))))
        { BaseAddress = new Uri("http://billing.test") });

        var result = await client.GetClinicSummaryAsync(Guid.NewGuid(), window, "user-token", default);

        Assert.Equal("UNAVAILABLE", result.Status);
        Assert.Empty(result.Groups);
        Assert.Equal("BILLING_SCOPE_FORBIDDEN", result.UnavailableReason);
    }

    [Fact]
    public async Task Empty_valid_billing_summary_is_available_with_no_groups()
    {
        var window = AnalyticsWindow.Create(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        var handler = new Handler((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            $$"""{"from":"{{window.From:O}}","to":"{{window.To:O}}","groups":[]}""")));
        var client = new BillingAnalyticsClient(new HttpClient(handler) { BaseAddress = new Uri("http://billing.test") });

        var result = await client.GetAdminSummaryAsync(window, "user-token", default);

        Assert.Equal("AVAILABLE", result.Status);
        Assert.Empty(result.Groups);
        Assert.Null(result.UnavailableReason);
    }

    [Fact]
    public async Task Billing_outage_is_unavailable_and_not_reported_as_zero_revenue()
    {
        var window = AnalyticsWindow.Create(DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-02-01T00:00:00Z"));
        var client = new BillingAnalyticsClient(new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        { BaseAddress = new Uri("http://billing.test") });

        var result = await client.GetAdminSummaryAsync(window, "user-token", default);

        Assert.Equal("UNAVAILABLE", result.Status);
        Assert.Empty(result.Groups);
        Assert.Equal("BILLING_API_UNAVAILABLE", result.UnavailableReason);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
