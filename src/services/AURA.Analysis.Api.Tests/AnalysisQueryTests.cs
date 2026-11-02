using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalysisQueryTests
{
    private sealed class FakeQueryRepository : IAnalysisQueryRepository
    {
        public AnalysisIdentity? Identity;
        public AnalysisDetailResponse? Response;

        public Task<AnalysisIdentity?> FindIdentityAsync(Guid analysisId, CancellationToken ct) =>
            Task.FromResult(Identity);

        public Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity identity, string? correlationId, CancellationToken ct) =>
            Task.FromResult(Response);
    }

    private sealed class FakeInternalRepository : IInternalResolversRepository
    {
        public InternalAnalysisState? State;

        public Task<InternalAnalysisState?> GetStateAsync(Guid analysisId, Guid tenantId, CancellationToken ct) =>
            Task.FromResult(State);
    }

    [Fact]
    public async Task AC1_AC2_GetAsync_Authorized_ReturnsAnalysisDetails()
    {
        var requesterId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var tenantId = FakeAccess.Tenant;

        var repo = new FakeQueryRepository
        {
            Identity = new AnalysisIdentity(analysisId, batchId, tenantId, patientId, requesterId),
            Response = new AnalysisDetailResponse(
                analysisId, batchId, patientId, "COMPLETED", "PENDING",
                DateTime.UtcNow, DateTime.UtcNow, requesterId, requesterId,
                new AnalysisImageRef(Guid.NewGuid(), "original"),
                new AnalysisJobDetail(Guid.NewGuid(), "SUCCEEDED", 0, 1),
                new AnalysisResultDetail(
                    false, null, null, null, false, "frangi-v0.2", "s3-frangi-1.0.0", "frangi-threshold-0.02-v1",
                    "disclaimer", 1, new AnalysisMetricsDetail(0.05, null, null, null, null),
                    Array.Empty<string>(), Array.Empty<AnalysisAnnotationRef>(),
                    new[] { new AnalysisWarningDetail("RISK_UNAVAILABLE", "warning") }, 100, "limitations"),
                null)
        };

        var access = new FakeAccess { Allowed = true };
        var service = new AnalysisQueryService(repo, access);

        var response = await service.GetAsync(requesterId, null, analysisId, CancellationToken.None);

        Assert.Equal(analysisId, response.AnalysisId);
        Assert.Equal(analysisId, access.LastRequest!.AnalysisId);
        Assert.Equal("COMPLETED", response.ProcessingStatus);
        Assert.NotNull(response.Result);
        Assert.False(response.Result.RiskAvailable);
        Assert.Null(response.Result.RiskLevel);
    }

    [Fact]
    public async Task AC2_GetAsync_RevokedOrCrossTenant_Throws404NotFound()
    {
        var requesterId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var batchId = Guid.NewGuid();

        var repo = new FakeQueryRepository
        {
            Identity = new AnalysisIdentity(analysisId, batchId, FakeAccess.Tenant, patientId, requesterId)
        };

        // Access denied by Profile
        var access = new FakeAccess { Allowed = false };
        var service = new AnalysisQueryService(repo, access);

        var ex = await Assert.ThrowsAsync<UploadFailure>(() =>
            service.GetAsync(requesterId, null, analysisId, CancellationToken.None));

        Assert.Equal(404, ex.Status);
        Assert.Equal("ERR_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task AC3_GetAsync_InProgress_ReturnsActiveStatusWithoutCompletedResult()
    {
        var requesterId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var tenantId = FakeAccess.Tenant;

        var repo = new FakeQueryRepository
        {
            Identity = new AnalysisIdentity(analysisId, batchId, tenantId, patientId, requesterId),
            Response = new AnalysisDetailResponse(
                analysisId, batchId, patientId, "PROCESSING", "NOT_REQUIRED",
                DateTime.UtcNow, DateTime.UtcNow, requesterId, requesterId,
                new AnalysisImageRef(Guid.NewGuid(), "original"),
                new AnalysisJobDetail(Guid.NewGuid(), "RUNNING", 0, 1),
                null, // Result MUST be null when PROCESSING
                null)
        };

        var access = new FakeAccess { Allowed = true };
        var service = new AnalysisQueryService(repo, access);

        var response = await service.GetAsync(requesterId, null, analysisId, CancellationToken.None);

        Assert.Equal("PROCESSING", response.ProcessingStatus);
        Assert.Null(response.Result);
    }

    [Fact]
    public async Task InternalResolvers_CompletedAnalysis_ResolvesCapture()
    {
        var analysisId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        var repo = new FakeInternalRepository
        {
            State = new InternalAnalysisState(
                analysisId, tenantId, Guid.NewGuid(), Guid.NewGuid(),
                "COMPLETED", reservationId, 1, 1, false)
        };

        var service = new InternalResolversService(repo);
        var request = new InternalResolveRequest(Guid.NewGuid(), analysisId, tenantId);

        var response = await service.ResolveSettlementAsync(request, CancellationToken.None);

        Assert.True(response.Applicable);
        Assert.Equal("CAPTURE", response.SettlementAction);
        Assert.Equal("VALID_RESULT", response.Reason);
        Assert.Equal(reservationId, response.ReservationId);
        Assert.Equal(1, response.Generation);
    }

    [Fact]
    public async Task InternalResolvers_FailedOrLowQuality_ResolvesRelease()
    {
        var analysisId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        var repo = new FakeInternalRepository
        {
            State = new InternalAnalysisState(
                analysisId, tenantId, Guid.NewGuid(), Guid.NewGuid(),
                "LOW_QUALITY", reservationId, 1, null, null)
        };

        var service = new InternalResolversService(repo);
        var request = new InternalResolveRequest(Guid.NewGuid(), analysisId, tenantId);

        var response = await service.ResolveSettlementAsync(request, CancellationToken.None);

        Assert.True(response.Applicable);
        Assert.Equal("RELEASE", response.SettlementAction);
        Assert.Equal("LOW_QUALITY", response.Reason);
        Assert.Equal(reservationId, response.ReservationId);
    }

    [Fact]
    public async Task InternalResolvers_MockInRealNamespace_HoldsForReconciliation()
    {
        var analysisId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();

        var repo = new FakeInternalRepository
        {
            State = new InternalAnalysisState(
                analysisId, tenantId, Guid.NewGuid(), Guid.NewGuid(),
                "COMPLETED", reservationId, 1, 1, true) // isMock = true
        };

        var service = new InternalResolversService(repo);
        var request = new InternalResolveRequest(Guid.NewGuid(), analysisId, tenantId);

        var response = await service.ResolveSettlementAsync(request, CancellationToken.None);

        Assert.False(response.Applicable);
        Assert.Equal("NONE", response.SettlementAction);
        Assert.Equal("RECONCILE_REQUIRED", response.Reason);
    }

    [Fact]
    public async Task InternalResolvers_NotificationContext_ResolvesOwnerRecipient()
    {
        var analysisId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        var repo = new FakeInternalRepository
        {
            State = new InternalAnalysisState(
                analysisId, tenantId, Guid.NewGuid(), ownerId,
                "COMPLETED", Guid.NewGuid(), 1, 1, false)
        };

        var service = new InternalResolversService(repo);
        var request = new InternalResolveRequest(Guid.NewGuid(), analysisId, tenantId);

        var response = await service.ResolveNotificationContextAsync(request, CancellationToken.None);

        Assert.True(response.Applicable);
        Assert.Contains(ownerId, response.RecipientAccountIds);
        Assert.Equal("COMPLETED", response.ProcessingStatus);
    }
}
