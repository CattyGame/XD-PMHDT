using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Feedback;

public sealed class FeedbackService(
    IAnalysisQueryRepository analyses,
    IPatientAccessResolver access,
    IFeedbackRepository feedback)
{
    public async Task<FeedbackReceipt> CreateAsync(
        Guid doctorId,
        Guid clinicId,
        Guid analysisId,
        FeedbackCreateRequest request,
        string correlationId,
        CancellationToken ct)
    {
        FeedbackRequestValidator.Validate(request);
        if (doctorId == Guid.Empty || clinicId == Guid.Empty || analysisId == Guid.Empty)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

        // This lookup returns only the scope needed to ask Profile for current access.
        var identity = await analyses.FindIdentityAsync(analysisId, ct)
            ?? throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

        var decision = await access.ResolveAsync(new PatientAccessRequest(
            doctorId, clinicId, identity.PatientId, "DOCTOR_REVIEW", analysisId)
        {
            CorrelationId = correlationId
        }, ct);
        if (!decision.Allowed || decision.TenantId != identity.TenantId
            || decision.OwnerPrincipalId != clinicId)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

        var current = await analyses.ReadAsync(identity, correlationId, ct)
            ?? throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");
        if (current.Result is null || current.Result.ResultId != request.ResultId
            || !string.Equals(current.Result.ModelVersion, request.ModelVersion, StringComparison.Ordinal)
            || current.ReviewVersion != request.ReviewVersion)
            throw new UploadFailure(409, "ERR_CONFLICT", "Kết quả hoặc phiên bản review đã thay đổi.");

        return await feedback.InsertAsync(new FeedbackRecord(
            identity.TenantId,
            clinicId,
            analysisId,
            request.ResultId,
            request.ModelVersion,
            request.ReviewVersion,
            doctorId,
            request.FeedbackType,
            request.Comment,
            correlationId), ct);
    }
}
