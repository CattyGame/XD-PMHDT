using AURA.Analysis.Api.Feedback;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class FeedbackValidationTests
{
    [Fact]
    public void Accepts_contract_feedback_kind_and_comment_at_4000_character_limit()
    {
        var request = new FeedbackCreateRequest(
            Guid.NewGuid(), "model-v1", 2, "SEGMENTATION", new string('x', 4000));

        FeedbackRequestValidator.Validate(request);
    }

    [Fact]
    public void Rejects_unrecognized_feedback_kind()
    {
        var request = new FeedbackCreateRequest(
            Guid.NewGuid(), "model-v1", 2, "DIAGNOSIS", "needs a closer look");

        var failure = Assert.Throws<UploadFailure>(() => FeedbackRequestValidator.Validate(request));

        Assert.Equal(400, failure.Status);
        Assert.Equal("ERR_VALIDATION", failure.Code);
    }
}
