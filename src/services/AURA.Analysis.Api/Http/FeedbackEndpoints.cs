using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Feedback;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Http;

public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/analyses/{analysisId}/feedback", (HttpContext context, string analysisId, FeedbackCreateRequest request) => Handle(context, async () =>
        {
            var user = await context.RequestServices.GetRequiredService<IAnalysisUserIdentityResolver>()
                .ResolveAsync(context.Request, context.RequestAborted);
            AnalysisUserPolicy.RequireDoctor(user);
            var clinicId = context.Request.Query.TryGetValue("clinicContextId", out var values)
                && Guid.TryParse(values.FirstOrDefault(), out var parsed) && parsed != Guid.Empty
                ? parsed
                : throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "required"));
            if (!Guid.TryParse(analysisId, out var id) || id == Guid.Empty)
                throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

            var receipt = await context.RequestServices.GetRequiredService<FeedbackService>().CreateAsync(
                user.AccountId, clinicId, id, request, Correlation(context), context.RequestAborted);
            return Results.Json(receipt, UploadJson.Options, statusCode: StatusCodes.Status201Created);
        }));
    }

    private static async Task<IResult> Handle(HttpContext context, Func<Task<IResult>> action)
    {
        var id = Guid.NewGuid().ToString("D");
        context.Items["CorrelationId"] = id;
        context.Response.Headers["X-Correlation-Id"] = id;
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await action();
        }
        catch (UploadFailure error)
        {
            return Results.Json(new
            {
                error = new { code = error.Code, message = error.Message, correlationId = id, retryable = error.Retryable, details = error.Details }
            }, UploadJson.Options, statusCode: error.Status);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var failure = UploadFailure.Dependency("Analysis feedback dependencies");
            return Results.Json(new
            {
                error = new { code = failure.Code, message = failure.Message, correlationId = id, retryable = failure.Retryable }
            }, UploadJson.Options, statusCode: failure.Status);
        }
    }

    private static string Correlation(HttpContext context) => (string)context.Items["CorrelationId"]!;
}
