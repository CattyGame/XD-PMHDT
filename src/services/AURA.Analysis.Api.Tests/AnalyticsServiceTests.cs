using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalyticsServiceTests
{
    private sealed class Access : IAnalyticsAccessResolver
    {
        public AnalyticsAccessGrant Grant = new(Guid.NewGuid(), Guid.NewGuid(), null, null, 1);
        public AnalyticsAccessRequest? LastRequest;
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
        { LastRequest = request; return Task.FromResult(Grant); }
    }

    private sealed class Repository : IAnalyticsRepository
    {
        public int SummaryReads;
        public int TrendReads;
        public AnalyticsDataScope? Scope;
        public IReadOnlyList<AnalyticsTrendRow> TrendRows = [];
        public Task<AnalyticsSummaryRow> ReadSummaryAsync(AnalyticsDataScope scope, AnalyticsWindow window, CancellationToken ct)
        {
            SummaryReads++;
            Scope = scope;
            return Task.FromResult(new AnalyticsSummaryRow(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }
        public Task<IReadOnlyList<AnalyticsTrendRow>> ReadPatientTrendsAsync(
            Guid tenantId, Guid clinicId, Guid patientId, AnalyticsWindow window, int limit, CancellationToken ct) =>
            CountTrends();
        private Task<IReadOnlyList<AnalyticsTrendRow>> CountTrends()
        {
            TrendReads++;
            return Task.FromResult(TrendRows);
        }
    }

    [Fact]
    public async Task Clinic_summary_requires_matching_current_grant_before_database_read()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid();
        var access = new Access { Grant = new(Guid.NewGuid(), Guid.NewGuid(), null, null, 1) };
        var repository = new Repository();
        var service = new AnalyticsService(access, repository, TimeProvider.System);

        var failure = await Assert.ThrowsAsync<UploadFailure>(() => service.GetClinicSummaryAsync(
            actor, clinic, Window(), null, default));

        Assert.Equal(404, failure.Status);
        Assert.Equal(0, repository.SummaryReads);
    }

    [Fact]
    public async Task Revenue_is_explicitly_unavailable_until_billing_read_api_exists()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid();
        var access = new Access { Grant = new(Guid.NewGuid(), clinic, null, null, 1) };
        var repository = new Repository();
        var service = new AnalyticsService(access, repository, TimeProvider.System);

        var result = await service.GetClinicSummaryAsync(actor, clinic, Window(), null, default);

        Assert.Equal("UNAVAILABLE", result.Revenue.Status);
        Assert.Empty(result.Revenue.Groups);
        Assert.Equal("BILLING_API_UNAVAILABLE", result.Revenue.UnavailableReason);
    }

    [Fact]
    public async Task Doctor_summary_is_restricted_to_authorized_assignments_and_latest_doctor_activity()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid(); var tenant = Guid.NewGuid();
        var patients = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var access = new Access { Grant = new(tenant, clinic, null, patients, 4) };
        var repository = new Repository();
        var service = new AnalyticsService(access, repository, TimeProvider.System);

        await service.GetDoctorSummaryAsync(actor, clinic, Window(), null, default);

        Assert.Equal("ANALYTICS_READ", access.LastRequest!.Operation);
        Assert.Equal("DOCTOR_ASSIGNMENT", access.LastRequest.ResourceScope);
        Assert.Equal(actor, repository.Scope!.DoctorId);
        Assert.Equal(patients, repository.Scope.AuthorizedPatientIds);
    }

    [Fact]
    public async Task Patient_trend_does_not_connect_incompatible_model_config_or_units()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid(); var tenant = Guid.NewGuid(); var patient = Guid.NewGuid();
        var access = new Access { Grant = new(tenant, clinic, patient, null, 2) };
        var repository = new Repository();
        var created = DateTimeOffset.UtcNow;
        repository.TrendRows = [
            Trend(Guid.NewGuid(), created, "m1", "c1", "t1", "ratio"),
            Trend(Guid.NewGuid(), created.AddMinutes(1), "m1", "c1", "t1", "ratio"),
            Trend(Guid.NewGuid(), created.AddMinutes(2), "m2", "c1", "t1", "ratio"),
            Trend(Guid.NewGuid(), created.AddMinutes(3), "m2", "c1", "t1", "native_px")];
        var service = new AnalyticsService(access, repository, TimeProvider.System);

        var trends = await service.GetPatientTrendsAsync(actor, clinic, patient, Window(), 100, null, default);

        Assert.Collection(trends.Items,
            first => Assert.False(first.CanConnectToPrevious),
            second => Assert.True(second.CanConnectToPrevious),
            third => Assert.False(third.CanConnectToPrevious),
            fourth => Assert.False(fourth.CanConnectToPrevious));
    }

    [Fact]
    public async Task Patient_trend_rejects_authority_for_another_patient_before_database_read()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid();
        var access = new Access { Grant = new(Guid.NewGuid(), clinic, Guid.NewGuid(), null, 1) };
        var repository = new Repository();
        var failure = await Assert.ThrowsAsync<UploadFailure>(() => new AnalyticsService(access, repository, TimeProvider.System)
            .GetPatientTrendsAsync(actor, clinic, Guid.NewGuid(), Window(), 100, null, default));

        Assert.Equal(404, failure.Status);
        Assert.Equal(0, repository.TrendReads);
    }

    private static AnalyticsWindow Window() => AnalyticsWindow.Create(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
    private static AnalyticsTrendRow Trend(Guid id, DateTimeOffset created, string model, string config, string threshold, string units) =>
        new(id, created, model, config, threshold, units, 1, .67m, null, true, .3m, .4m);
}
