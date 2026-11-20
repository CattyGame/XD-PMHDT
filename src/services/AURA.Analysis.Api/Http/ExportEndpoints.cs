using AURA.Analysis.Api.Exports;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Http;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/campaigns/{campaignId}/reports",(HttpContext c,string campaignId,CampaignReportRequest request)=>Handle(c,async()=>
        {
            var user=await User(c); var clinic=Clinic(c);
            var receipt=await Service(c).CreateCampaignReportAsync(user.AccountId,clinic,Id(campaignId,"campaignId"),request,Key(c),Correlation(c),c.RequestAborted);
            return Results.Json(receipt,UploadJson.Options,statusCode:StatusCodes.Status202Accepted);
        }));
        app.MapPost("/api/v1/research/exports",(HttpContext c,ResearchExportRequest request)=>Handle(c,async()=>
        {
            var user=await User(c);
            var receipt=await Service(c).CreateResearchExportAsync(user.AccountId,request,Key(c),Correlation(c),c.RequestAborted);
            return Results.Json(receipt,UploadJson.Options,statusCode:StatusCodes.Status202Accepted);
        }));
        app.MapGet("/api/v1/reports/{reportId}",(HttpContext c,string reportId)=>Handle(c,async()=>
        {
            var user=await User(c);var receipt=await Service(c).GetStatusAsync(user.AccountId,Clinic(c),Id(reportId,"reportId"),Correlation(c),c.RequestAborted);
            return Results.Json(receipt,UploadJson.Options);
        }));
    }

    internal static ExportService Service(HttpContext c)=>c.RequestServices.GetRequiredService<ExportService>();
    internal static Guid Clinic(HttpContext c)=>c.Request.Query.TryGetValue("clinicContextId",out var v)&&Guid.TryParse(v.FirstOrDefault(),out var id)&&id!=Guid.Empty?id:
        throw UploadFailure.Validation(new ErrorDetail("clinicContextId","required"));
    internal static async Task<AnalysisUserContext> User(HttpContext c)
    {
        var user=await c.RequestServices.GetRequiredService<IAnalysisUserIdentityResolver>().ResolveAsync(c.Request,c.RequestAborted);
        if(user is null||user.AccountId==Guid.Empty)throw new UploadFailure(401,"ERR_UNAUTHORIZED","Yêu cầu cần xác thực danh tính hợp lệ.");return user;
    }
    internal static Guid Id(string raw,string field)=>Guid.TryParse(raw,out var id)&&id!=Guid.Empty?id:throw UploadFailure.Validation(new ErrorDetail(field,"invalid_uuid"));
    private static string Key(HttpContext c)=>c.Request.Headers.TryGetValue("Idempotency-Key",out var v)&&v.Count==1?v[0]??"":throw UploadFailure.Validation(new ErrorDetail("Idempotency-Key","required"));
    internal static string Correlation(HttpContext c)=>c.Items["CorrelationId"] as string??Guid.NewGuid().ToString("D");
    private static async Task<IResult> Handle(HttpContext c,Func<Task<IResult>> action)
    {
        var incoming=c.Request.Headers["X-Correlation-Id"];var id=incoming.Count==1&&incoming[0] is {Length:>0 and <=64} x&&x.All(ch=>char.IsAsciiLetterOrDigit(ch)||ch is '.' or '_' or '-')?x:Guid.NewGuid().ToString("D");
        c.Items["CorrelationId"]=id;c.Response.Headers["X-Correlation-Id"]=id;c.Response.Headers.CacheControl="no-store";
        try{return await action();}
        catch(UploadFailure e){return Results.Json(new{error=new{code=e.Code,message=e.Message,correlationId=id,retryable=e.Retryable,details=e.Details}},UploadJson.Options,statusCode:e.Status);}
        catch(OperationCanceledException) when(c.RequestAborted.IsCancellationRequested){throw;}
        catch(Exception){var e=UploadFailure.Dependency("Analysis export dependencies");return Results.Json(new{error=new{code=e.Code,message=e.Message,correlationId=id,retryable=e.Retryable}},UploadJson.Options,statusCode:e.Status);}
    }
}
