using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class DoctorReviewServiceTests
{
    private sealed class AnalysisRepository(AnalysisIdentity? identity) : IAnalysisQueryRepository
    {
        public Task<AnalysisIdentity?> FindIdentityAsync(Guid analysisId, CancellationToken ct) => Task.FromResult(identity);
        public Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity value, string? correlationId, CancellationToken ct) =>
            Task.FromResult<AnalysisDetailResponse?>(null);
    }

    private sealed class AccessResolver(bool allowed) : IPatientAccessResolver
    {
        public PatientAccessRequest? LastRequest { get; private set; }
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new AccessDecision(allowed, FakeAccess.Tenant, "CLINIC", request.ClinicId!.Value,
                "CLINIC:" + request.ClinicId.Value, 1));
        }
    }

    private sealed class ReviewRepository : IDoctorReviewRepository
    {
        public DoctorReviewCommand? LastCommand { get; private set; }
        public UploadFailure? Failure { get; set; }
        public Task<DoctorReviewDto> SubmitAsync(DoctorReviewCommand command, CancellationToken ct)
        {
            LastCommand = command;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new DoctorReviewDto(Guid.NewGuid(), command.AnalysisId,
                command.BaseVersion + 1, command.IsApproved, command.CorrectedRiskLevel, command.DoctorNotes,
                command.DoctorId, DateTimeOffset.UtcNow, command.Reason, 1));
        }
    }

    [Fact]
    public async Task SubmitAsync_AuthorizedDoctor_PersistsSeparateRevisionWithExpectedVersion()
    {
        var doctor = Guid.NewGuid();
        var clinic = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var repo = new ReviewRepository();
        var access = new AccessResolver(true);
        var service = new DoctorReviewService(
            new AnalysisRepository(new AnalysisIdentity(analysisId, Guid.NewGuid(), FakeAccess.Tenant, patient, clinic)),
            access,
            repo);

        var result = await service.SubmitAsync(doctor, clinic, analysisId,
            new DoctorReviewRequest(false, 3, " high ", "  Reconcile fundus image  ", "  Clinical note  "),
            "corr-342", CancellationToken.None);

        Assert.Equal("DOCTOR_REVIEW", access.LastRequest!.Action);
        Assert.Equal(analysisId, access.LastRequest.AnalysisId);
        Assert.Equal(4, result.Version);
        Assert.Equal(3, repo.LastCommand!.BaseVersion);
        Assert.Equal("HIGH", repo.LastCommand.CorrectedRiskLevel);
        Assert.Equal("Reconcile fundus image", repo.LastCommand.Reason);
        Assert.Equal("Clinical note", repo.LastCommand.DoctorNotes);
        Assert.Equal(doctor, repo.LastCommand.DoctorId);
    }

    [Fact]
    public async Task SubmitAsync_UnassignedDoctor_IsForbiddenAndDoesNotWriteRevision()
    {
        var analysisId = Guid.NewGuid();
        var clinic = Guid.NewGuid();
        var doctor = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var repo = new ReviewRepository();
        var service = new DoctorReviewService(
            new AnalysisRepository(new AnalysisIdentity(analysisId, Guid.NewGuid(), FakeAccess.Tenant, patient, clinic)),
            new AccessResolver(false),
            repo);

        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.SubmitAsync(doctor, clinic, analysisId,
            new DoctorReviewRequest(true, 0), "corr-342", CancellationToken.None));

        Assert.Equal(403, error.Status);
        Assert.Null(repo.LastCommand);
    }

    [Fact]
    public async Task SubmitAsync_CorrectionRequiresRiskLevelAndReason()
    {
        var repo = new ReviewRepository();
        var service = new DoctorReviewService(
            new AnalysisRepository(new AnalysisIdentity(Guid.NewGuid(), Guid.NewGuid(), FakeAccess.Tenant, Guid.NewGuid(), Guid.NewGuid())),
            new AccessResolver(true),
            repo);

        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.SubmitAsync(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new DoctorReviewRequest(false, 0, "HIGH", "  "), "corr-342", CancellationToken.None));

        Assert.Equal(400, error.Status);
        Assert.Null(repo.LastCommand);
    }

    [Fact]
    public async Task SubmitAsync_RepositoryVersionConflict_IsReturnedAsHttp409()
    {
        var repo = new ReviewRepository
        {
            Failure = new UploadFailure(409, "ERR_CONCURRENT_REVIEW", "Stale review version.")
        };
        var clinic = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var service = new DoctorReviewService(
            new AnalysisRepository(new AnalysisIdentity(Guid.NewGuid(), Guid.NewGuid(), FakeAccess.Tenant, patient, clinic)),
            new AccessResolver(true),
            repo);

        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.SubmitAsync(Guid.NewGuid(), clinic,
            Guid.NewGuid(), new DoctorReviewRequest(true, 0), "corr-342", CancellationToken.None));

        Assert.Equal(409, error.Status);
        Assert.Equal("ERR_CONCURRENT_REVIEW", error.Code);
    }
}
