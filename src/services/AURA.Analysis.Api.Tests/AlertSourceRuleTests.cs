using AURA.Analysis.Api.Alerts;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AlertSourceRuleTests
{
    [Fact]
    public void Corrected_high_review_creates_clinician_alert_with_versioned_source()
    {
        var facts = new AlertSourceFacts(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            4,
            "COMPLETED",
            "MODERATE",
            "HIGH",
            true,
            "AVAILABLE");

        var alerts = AlertSourceRules.Derive(facts);

        Assert.Equal(new[] { "CLINICIAN_REVIEW_HIGH", "RESEARCH_NOTICE" }, alerts.Select(x => x.Kind));
        Assert.All(alerts, alert => Assert.Equal("scrum385-v1", alert.RuleVersion));
    }

    [Fact]
    public void Alert_id_is_stable_for_the_same_source_tuple()
    {
        var source = new AlertSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, 1, "scrum385-v1", "CLINICIAN_REVIEW_HIGH");

        Assert.Equal(AlertSourceRules.ToAlertId(source), AlertSourceRules.ToAlertId(source));
        Assert.NotEqual(AlertSourceRules.ToAlertId(source), AlertSourceRules.ToAlertId(source with { ReviewVersion = 4 }));
    }

    [Theory]
    [InlineData("LOW_QUALITY", "HIGH", true, "UNAVAILABLE")]
    [InlineData("COMPLETED", "HIGH", false, "UNKNOWN")]
    [InlineData("COMPLETED", "LOW", true, "UNAVAILABLE")]
    public void Ineligible_result_does_not_create_clinician_high_alert(
        string status, string risk, bool reviewed, string measurement)
    {
        var facts = new AlertSourceFacts(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            status, risk, null, reviewed, measurement);

        var alerts = AlertSourceRules.Derive(facts);

        Assert.DoesNotContain(alerts, x => x.Kind == "CLINICIAN_REVIEW_HIGH");
        Assert.DoesNotContain(alerts, x => x.Kind == "RESEARCH_NOTICE");
    }
}
