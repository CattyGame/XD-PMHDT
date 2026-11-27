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
        {
            LastRequest = request;
            return Denied
                ? Task.FromException<AnalyticsAccessGrant>(new UploadFailure(403, "ERR_FORBIDDEN", "denied"))
                : Task.FromResult(Grant);
        }
        public bool Denied;
    }

    private sealed class Repository : IAnalyticsRepository
    {
        public int SummaryReads;
        public int TrendReads;
        public AnalyticsDataScope? Scope;
        public IReadOnlyList<AnalyticsTrendRow> TrendRows = [];
        public string Revision = "0/1";
        public Task<string> ReadDataRevisionAsync(CancellationToken ct) => Task.FromResult(Revision);
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

    private sealed class Cache : IAnalyticsSummaryCache
    {
        public readonly Dictionary<string, AnalyticsSummaryRow> Values = new(StringComparer.Ordinal);
        public TimeSpan? LastTtl;
        public bool ThrowOnRead;
        public Task<AnalyticsSummaryRow?> TryGetAsync(string key, CancellationToken ct) =>
            ThrowOnRead ? Task.FromException<AnalyticsSummaryRow?>(new TimeoutException("cache unavailable"))
                : Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        public Task TrySetAsync(string key, AnalyticsSummaryRow value, TimeSpan ttl, CancellationToken ct)
        { LastTtl = ttl; Values[key] = value; return Task.CompletedTask; }
    }

    private sealed class Revenue : IAnalyticsRevenueClient
    {
        public string? Bearer;
        public AnalyticsRevenueSummary Result = new("AVAILABLE", [new("USD", "TEST", 2, 12.50m)], null);
        public Task<AnalyticsRevenueSummary> GetClinicSummaryAsync(Guid clinicId, AnalyticsWindow window, string? userBearer, CancellationToken ct)
        { Bearer = userBearer; return Task.FromResult(Result); }
        public Task<AnalyticsRevenueSummary> GetAdminSummaryAsync(AnalyticsWindow window, string? userBearer, CancellationToken ct)
        { Bearer = userBearer; return Task.FromResult(Result); }
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
    public async Task Revenue_is_unavailable_if_billing_client_is_not_configured()
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
    public async Task Authorized_clinic_summary_uses_billing_result_and_forwards_only_the_user_bearer()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid();
        var access = new Access { Grant = new(Guid.NewGuid(), clinic, null, null, 7) };
        var repository = new Repository(); var revenue = new Revenue();
        var service = new AnalyticsService(access, repository, TimeProvider.System, revenue);

        var result = await service.GetClinicSummaryAsync(actor, clinic, Window(), "c1", default, "firebase-user-token");

        Assert.Equal("AVAILABLE", result.Revenue.Status);
        Assert.Equal(new AnalyticsRevenueGroup("USD", "TEST", 2, 12.50m), Assert.Single(result.Revenue.Groups));
        Assert.Equal("firebase-user-token", revenue.Bearer);
    }

    [Fact]
    public async Task Summary_cache_is_partitioned_by_permission_scope_and_database_revision()
    {
        var actor = Guid.NewGuid(); var clinic = Guid.NewGuid();
        var access = new Access { Grant = new(Guid.NewGuid(), clinic, null, null, 1) };
        var repository = new Repository(); var cache = new Cache();
        var service = new AnalyticsService(access, repository, TimeProvider.System, cache: cache);
        var window = Window();

        await service.GetClinicSummaryAsync(actor, clinic, window, null, default);
        await service.GetClinicSummaryAsync(actor, clinic, window, null, default);
        Assert.Equal(1, repository.SummaryReads);
        Assert.Equal(TimeSpan.FromSeconds(30), cache.LastTtl);
        repository.Revision = "0/2";
        await service.GetClinicSummaryAsync(actor, clinic, window, null, default);
        Assert.Equal(2, repository.SummaryReads);
        access.Grant = access.Grant with { PermissionVersion = 2 };
        await service.GetClinicSummaryAsync(actor, clinic, window, null, default);
        Assert.Equal(3, repository.SummaryReads);
        var anotherClinic = Guid.NewGuid();
        access.Grant = access.Grant with { ClinicId = anotherClinic };
        await service.GetClinicSummaryAsync(actor, anotherClinic, window, null, default);
        Assert.Equal(4, repository.SummaryReads);
        Assert.Equal(4, cache.Values.Count);
    }

    [Fact]
    public async Task Redis_loss_rebuilds_summary_from_postgres_after_authority_check()
    {
        var clinic = Guid.NewGuid(); var access = new Access { Grant = new(Guid.NewGuid(), clinic, null, null, 1) };
        var repository = new Repository(); var cache = new Cache { ThrowOnRead = true };
        var service = new AnalyticsService(access, repository, TimeProvider.System, cache: cache);

        var result = await service.GetClinicSummaryAsync(Guid.NewGuid(), clinic, Window(), null, default);

        Assert.NotNull(access.LastRequest);
        Assert.Equal(1, repository.SummaryReads);
        Assert.Equal("UNIQUE_ANALYSIS", result.Unit);
    }

    [Fact]
    public async Task Revoked_authority_cannot_read_a_warm_summary_cache()
    {
        var clinic = Guid.NewGuid(); var access = new Access { Grant = new(Guid.NewGuid(), clinic, null, null, 4) };
        var repository = new Repository(); var cache = new Cache();
        var service = new AnalyticsService(access, repository, TimeProvider.System, cache: cache);
        var window = Window();
        await service.GetClinicSummaryAsync(Guid.NewGuid(), clinic, window, null, default);
        access.Denied = true;

        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.GetClinicSummaryAsync(
            Guid.NewGuid(), clinic, window, null, default));

        Assert.Equal(403, error.Status);
        Assert.Equal(1, repository.SummaryReads);
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
