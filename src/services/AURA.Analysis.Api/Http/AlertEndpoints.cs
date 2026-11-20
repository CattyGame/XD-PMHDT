using AURA.Analysis.Api.Alerts;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Http;

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/clinics/{clinicId}/alerts", (HttpContext context, string clinicId, int? limit, string? cursor) => Handle(context, async () =>
        {
            var user = await CurrentUser(context);
            var clinic = ParseId(clinicId, "clinicId");
            var page = await context.RequestServices.GetRequiredService<AlertService>().ListAsync(
                user.AccountId, clinic, limit ?? 20, cursor, Correlation(context), context.RequestAborted);
            return Results.Json(page, UploadJson.Options);
        }));

        app.MapPatch("/api/v1/clinics/{clinicId}/alerts/{alertId}",
            (HttpContext context, string clinicId, string alertId, AlertStateChangeRequest request) => Handle(context, async () =>
        {
            var user = await CurrentUser(context);
            var response = await context.RequestServices.GetRequiredService<AlertService>().ChangeStateAsync(
                user.AccountId, ParseId(clinicId, "clinicId"), ParseId(alertId, "alertId"), request,
                Correlation(context), context.RequestAborted);
            return Results.Json(response, UploadJson.Options);
        }));
    }

    private static async Task<AnalysisUserContext> CurrentUser(HttpContext context)
    {
        var user = await context.RequestServices.GetRequiredService<IAnalysisUserIdentityResolver>()
            .ResolveAsync(context.Request, context.RequestAborted);
        AnalysisUserPolicy.RequireClinicalRole(user);
        return user;
    }

    private static Guid ParseId(string value, string field) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id
        : throw UploadFailure.Validation(new ErrorDetail(field, "invalid_uuid"));

    private static async Task<IResult> Handle(HttpContext context, Func<Task<IResult>> action)
    {
        var incoming = context.Request.Headers["X-Correlation-Id"];
        var correlation = incoming.Count == 1 && incoming[0] is { Length: > 0 and <= 64 } value
            && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-')
            ? value : Guid.NewGuid().ToString("D");
        context.Items["CorrelationId"] = correlation;
        context.Response.Headers["X-Correlation-Id"] = correlation;
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await action();
        }
        catch (UploadFailure error)
        {
            return Results.Json(new
            {
                error = new { code = error.Code, message = error.Message, correlationId = correlation,
                    retryable = error.Retryable, details = error.Details }
            }, UploadJson.Options, statusCode: error.Status);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var error = UploadFailure.Dependency("Analysis alert dependencies");
            return Results.Json(new
            {
                error = new { code = error.Code, message = error.Message, correlationId = correlation, retryable = error.Retryable }
            }, UploadJson.Options, statusCode: error.Status);
        }
    }

    private static string Correlation(HttpContext context) => (string)context.Items["CorrelationId"]!;
}
