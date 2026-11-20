using AURA.Analysis.Api.Feedback;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class FeedbackServiceTests
{
    [Fact]
    public async Task Denied_doctor_does_not_read_analysis_content_or_insert_feedback()
    {
        var identity = new AnalysisIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var queries = new FakeAnalysisQuery(identity);
        var repository = new FakeFeedbackRepository();
        var service = new FeedbackService(queries, new DeniedAccess(), repository);

        var failure = await Assert.ThrowsAsync<UploadFailure>(() => service.CreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), identity.AnalysisId,
            new FeedbackCreateRequest(Guid.NewGuid(), "model-v1", 1, "QUALITY", "blurry image"),
            "feedback-test", CancellationToken.None));

        Assert.Equal(404, failure.Status);
        Assert.Equal(0, queries.ReadCalls);
        Assert.Equal(0, repository.InsertCalls);
    }

    private sealed class DeniedAccess : IPatientAccessResolver
    {
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct) =>
            Task.FromResult(new AccessDecision(false, Guid.Empty, "", Guid.Empty, "", 1));
    }

    private sealed class FakeAnalysisQuery(AnalysisIdentity identity) : IAnalysisQueryRepository
    {
        public int ReadCalls { get; private set; }
        public Task<AnalysisIdentity?> FindIdentityAsync(Guid analysisId, CancellationToken ct) =>
            Task.FromResult<AnalysisIdentity?>(analysisId == identity.AnalysisId ? identity : null);
        public Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity request, string? correlationId, CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult<AnalysisDetailResponse?>(null);
        }
    }

    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        public int InsertCalls { get; private set; }
        public Task<FeedbackReceipt> InsertAsync(FeedbackRecord feedback, CancellationToken ct)
        {
            InsertCalls++;
            return Task.FromResult(new FeedbackReceipt(Guid.NewGuid(), "ACCEPTED", feedback.ReviewVersion));
        }
    }
}
