using AURA.Analysis.Api.Alerts;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AlertServiceTests
{
    [Fact]
    public async Task Profile_dependency_error_does_not_fallback_to_doctor_scope_or_read_data()
    {
        var access = new FakeAccess(_ => Task.FromException<AnalyticsAccessGrant>(UploadFailure.Dependency("Profile")));
        var repository = new FakeAlerts();
        var service = new AlertService(repository, access);

        var failure = await Assert.ThrowsAsync<UploadFailure>(() => service.ListAsync(
            Guid.NewGuid(), Guid.NewGuid(), 20, null, "corr", CancellationToken.None));

        Assert.Equal(503, failure.Status);
        Assert.Equal(1, access.Calls);
        Assert.Equal(0, repository.ReadCandidatesCalls);
    }

    [Fact]
    public async Task Doctor_assignment_response_at_500_cap_fails_closed_before_querying_alert_data()
    {
        var access = new FakeAccess(request => request.ResourceScope == "CLINIC"
            ? Task.FromException<AnalyticsAccessGrant>(new UploadFailure(403, "ERR_FORBIDDEN", "denied", details: [new("authority", "ROLE_DENIED")]))
            : Task.FromResult(new AnalyticsAccessGrant(Guid.NewGuid(), request.ClinicId,
                null, Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray(), 1)));
        var repository = new FakeAlerts();
        var service = new AlertService(repository, access);

        var failure = await Assert.ThrowsAsync<UploadFailure>(() => service.ListAsync(
            Guid.NewGuid(), Guid.NewGuid(), 20, null, "corr", CancellationToken.None));

        Assert.Equal(503, failure.Status);
        Assert.Equal(2, access.Calls);
        Assert.Equal(0, repository.ReadCandidatesCalls);
    }

    private sealed class FakeAccess(Func<AnalyticsAccessRequest, Task<AnalyticsAccessGrant>> result) : IAnalyticsAccessResolver
    {
        public int Calls { get; private set; }
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct)
        {
            Calls++;
            return result(request);
        }
    }

    private sealed class FakeAlerts : IAlertRepository
    {
        public int ReadCandidatesCalls { get; private set; }
        public Task<IReadOnlyList<AlertCandidate>> ReadCandidatesAsync(Guid tenantId, Guid clinicId,
            IReadOnlyCollection<Guid>? patientIds, int limit, CancellationToken ct)
        {
            ReadCandidatesCalls++;
            return Task.FromResult<IReadOnlyList<AlertCandidate>>([]);
        }
        public Task MaterializeSourcesAsync(Guid clinicId, Guid actorId, IReadOnlyCollection<AlertSource> sources,
            string correlationId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AlertStateRecord>> ReadStatesAsync(Guid tenantId, Guid clinicId,
            IReadOnlyCollection<Guid> analysisIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<AlertStateRecord>>([]);
        public Task<AlertStateRecord> ChangeStateAsync(Guid clinicId, Guid actorId, AlertSource source,
            int expectedVersion, string targetStatus, string reason, string correlationId, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
