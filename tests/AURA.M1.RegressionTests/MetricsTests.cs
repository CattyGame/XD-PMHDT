using System.Globalization;
using AURA.Billing.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AURA.M1.RegressionTests;

public class MetricsTests
{
    [Fact]
    public async Task Labels_use_route_templates_and_count_unhandled_errors_as_500()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseAuraPrometheusMetrics();
        app.Run(context =>
        {
            context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
                RoutePatternFactory.Parse("/patients/{patientId}"), 0, new EndpointMetadataCollection(), "fixture"));
            throw new InvalidOperationException("test-only failure");
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/patients/private-patient-name";
        context.Request.Method = "UNTRUSTED_METHOD";
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(context));
        var text = AuraPrometheusMetrics.GenerateMetricsText();
        Assert.DoesNotContain("private-patient-name", text);
        Assert.DoesNotContain("UNTRUSTED_METHOD", text);
        Assert.Contains("method=\"OTHER\",status=\"500\",path=\"/patients/{patientId}\"", text);
    }

    [Fact]
    public void Exposition_uses_decimal_points_under_non_english_locale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
            var text = AuraPrometheusMetrics.GenerateMetricsText();
            Assert.Contains("le=\"0.005\"", text);
            Assert.DoesNotContain("le=\"0,005\"", text);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
