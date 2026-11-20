using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsHttpTests
{
    [Theory]
    [InlineData("/api/v1/analytics/clinics/{0}/summary?from=2026-11-15T00:00:00Z&to=2026-11-16T00:00:00Z")]
    [InlineData("/api/v1/admin/analytics/summary?from=2026-11-15T00:00:00Z&to=2026-11-16T00:00:00Z")]
    [InlineData("/api/v1/doctors/me/summary?clinicContextId={1}&from=2026-11-15T00:00:00Z&to=2026-11-16T00:00:00Z")]
    [InlineData("/api/v1/analyses/patients/{2}/trends?clinicContextId={1}&from=2026-11-15T00:00:00Z&to=2026-11-16T00:00:00Z")]
    public async Task Planned_analytics_routes_are_mapped_and_fail_closed_when_profile_authority_is_unavailable(string template)
    {
        await using var rig = await Rig.Start();
        var path = string.Format(template, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var response = await rig.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ERR_NETWORK", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, rig.Repository.SummaryReads);
        Assert.Equal(0, rig.Repository.TrendReads);
    }

    [Fact]
    public async Task Invalid_window_is_rejected_before_profile_or_database_calls()
    {
        await using var rig = await Rig.Start();
        using var response = await rig.Client.GetAsync(
            $"/api/v1/analytics/clinics/{Guid.NewGuid()}/summary?from=2026-11-16T07:00:00%2B07:00&to=2026-11-17T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, rig.Repository.SummaryReads);
        Assert.Equal(0, rig.Access.Calls);
    }

    private sealed class Identity : IAnalysisUserIdentityResolver
    {
        public Task<AnalysisUserContext> ResolveAsync(HttpRequest request, CancellationToken ct) =>
            Task.FromResult(new AnalysisUserContext(Guid.NewGuid(), ["PATIENT"]));
    }

    private sealed class Repository : IAnalyticsRepository
    {
        public int SummaryReads;
        public int TrendReads;
        public Task<AnalyticsSummaryRow> ReadSummaryAsync(AnalyticsDataScope scope, AnalyticsWindow window, CancellationToken ct)
        {
            SummaryReads++;
            return Task.FromResult(new AnalyticsSummaryRow(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }
        public Task<IReadOnlyList<AnalyticsTrendRow>> ReadPatientTrendsAsync(
            Guid tenantId, Guid clinicId, Guid patientId, AnalyticsWindow window, int limit, CancellationToken ct)
        {
            TrendReads++;
            return Task.FromResult<IReadOnlyList<AnalyticsTrendRow>>([]);
        }
    }

    private sealed class Access : IAnalyticsAccessResolver
    {
        public int Calls;
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
        {
            Calls++;
            return new AnalyticsAccessUnavailableResolver().ResolveAsync(request, ct);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly Repository Repository = new();
        public readonly Access Access = new();
        public WebApplication App = null!;
        public HttpClient Client = null!;

        public static async Task<Rig> Start()
        {
            var rig = new Rig();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IAnalysisUserIdentityResolver, Identity>();
            builder.Services.AddSingleton<IAnalyticsAccessResolver>(rig.Access);
            builder.Services.AddSingleton<IAnalyticsRepository>(rig.Repository);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddTransient<AnalyticsService>();
            rig.App = builder.Build();
            rig.App.MapAnalyticsEndpoints();
            await rig.App.StartAsync();
            rig.Client = rig.App.GetTestClient();
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
