using System.Globalization;
using System.Net;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Http;

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/analytics/clinics/{clinicId}/summary",
            (HttpContext c, string clinicId, string? from, string? to) => Handle(c, async () =>
            {
                var user = await User(c);
                var window = Window(from, to);
                var result = await Service(c).GetClinicSummaryAsync(user.AccountId, ResourceId(clinicId),
                    window, Correlation(c), c.RequestAborted);
                return Results.Json(result, UploadJson.Options);
            }));

        app.MapGet("/api/v1/admin/analytics/summary",
            (HttpContext c, string? from, string? to) => Handle(c, async () =>
            {
                var user = await User(c);
                var result = await Service(c).GetAdminSummaryAsync(user.AccountId, Window(from, to),
                    Correlation(c), c.RequestAborted);
                return Results.Json(result, UploadJson.Options);
            }));

        app.MapGet("/api/v1/doctors/me/summary",
            (HttpContext c, string? from, string? to) => Handle(c, async () =>
            {
                var user = await User(c);
                var clinic = Clinic(c) ?? throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "required"));
                var result = await Service(c).GetDoctorSummaryAsync(user.AccountId, clinic, Window(from, to),
                    Correlation(c), c.RequestAborted);
                return Results.Json(result, UploadJson.Options);
            }));

        app.MapGet("/api/v1/analyses/patients/{patientId}/trends",
            (HttpContext c, string patientId, string? from, string? to, int? limit) => Handle(c, async () =>
            {
                var user = await User(c);
                var clinic = Clinic(c) ?? throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "required"));
                var result = await Service(c).GetPatientTrendsAsync(user.AccountId, clinic, ResourceId(patientId),
                    Window(from, to), limit ?? 100, Correlation(c), c.RequestAborted);
                return Results.Json(result, UploadJson.Options);
            }));
    }

    private static AnalyticsService Service(HttpContext c) => c.RequestServices.GetRequiredService<AnalyticsService>();

    private static async Task<AnalysisUserContext> User(HttpContext c) =>
        await c.RequestServices.GetRequiredService<IAnalysisUserIdentityResolver>().ResolveAsync(c.Request, c.RequestAborted);

    private static Guid ResourceId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

    private static Guid? Clinic(HttpContext c)
    {
        if (!c.Request.Query.TryGetValue("clinicContextId", out var values)) return null;
        if (values.Count != 1 || !Guid.TryParse(values[0], out var clinic) || clinic == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("clinicContextId", "invalid_uuid"));
        return clinic;
    }

    private static AnalyticsWindow Window(string? from, string? to)
    {
        if (!TryDate(from, out var start) || !TryDate(to, out var end))
            throw UploadFailure.Validation(new ErrorDetail("from/to", "must_be_utc_date_time"));
        return AnalyticsWindow.Create(start, end);
    }

    private static bool TryDate(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out parsed);

    private static string Correlation(HttpContext c) => (string)c.Items["CorrelationId"]!;

    private static async Task<IResult> Handle(HttpContext c, Func<Task<IResult>> action)
    {
        var header = c.Request.Headers["X-Correlation-Id"];
        var candidate = header.Count == 1 ? header[0] : null;
        var id = candidate is { Length: > 0 and <= 64 }
            && candidate.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-')
            ? candidate : Guid.NewGuid().ToString("D");
        c.Items["CorrelationId"] = id;
        c.Response.Headers["X-Correlation-Id"] = id;
        c.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (UploadFailure e) { return Error(c, e); }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            c.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AnalyticsEndpoints")
                .LogError("Analytics dependency failure: {ExceptionType}; stack: {StackTrace}", error.GetType().Name, error.StackTrace);
            return Error(c, UploadFailure.Dependency("Analysis dependencies"));
        }
    }

    private static IResult Error(HttpContext c, UploadFailure failure) => Results.Json(new
    {
        error = new
        {
            code = failure.Code,
            message = failure.Message,
            correlationId = Correlation(c),
            retryable = failure.Retryable,
            details = failure.Details
        }
    }, UploadJson.Options, statusCode: failure.Status);
}
