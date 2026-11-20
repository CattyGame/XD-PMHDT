using System.Text.Json.Serialization;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Feedback;

public sealed record FeedbackCreateRequest(
    [property: JsonPropertyName("resultId")] Guid ResultId,
    [property: JsonPropertyName("modelVersion")] string ModelVersion,
    [property: JsonPropertyName("reviewVersion")] int ReviewVersion,
    [property: JsonPropertyName("feedbackType")] string FeedbackType,
    [property: JsonPropertyName("comment")] string Comment);

public sealed record FeedbackReceipt(
    [property: JsonPropertyName("feedbackId")] Guid FeedbackId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] int Version);

public sealed record FeedbackRecord(
    Guid TenantId,
    Guid ClinicId,
    Guid AnalysisId,
    Guid ResultId,
    string ModelVersion,
    int ReviewVersion,
    Guid DoctorId,
    string FeedbackType,
    string Comment,
    string CorrelationId);

public interface IFeedbackRepository
{
    Task<FeedbackReceipt> InsertAsync(FeedbackRecord feedback, CancellationToken ct);
}

public static class FeedbackRequestValidator
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "SEGMENTATION", "VESSEL_TYPE", "CALIBER", "QUALITY", "OTHER"
    };

    public static void Validate(FeedbackCreateRequest request)
    {
        if (request.ResultId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("resultId", "required_uuid"));
        if (string.IsNullOrWhiteSpace(request.ModelVersion) || request.ModelVersion.Length > 100)
            throw UploadFailure.Validation(new ErrorDetail("modelVersion", "required_max_length_100"));
        if (request.ReviewVersion < 0)
            throw UploadFailure.Validation(new ErrorDetail("reviewVersion", "must_be_non_negative"));
        if (!Kinds.Contains(request.FeedbackType))
            throw UploadFailure.Validation(new ErrorDetail("feedbackType", "unsupported_feedback_type"));
        if (string.IsNullOrWhiteSpace(request.Comment) || request.Comment.Length > 4000)
            throw UploadFailure.Validation(new ErrorDetail("comment", "required_max_length_4000"));
    }
}
