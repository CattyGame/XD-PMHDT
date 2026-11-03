using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Upload;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;

namespace AURA.Analysis.Api.Http;

public interface IAnalysisUploadHandler
{ Task<UploadResult> CreateAsync(UploadCommand command,CancellationToken ct); }

public sealed class AnalysisUploadGate(int capacity) : IDisposable
{
    internal readonly SemaphoreSlim Slots=new(capacity,capacity);
    public void Dispose()=>Slots.Dispose();
}

public static class AnalysisEndpoints
{
    public static void MapAnalysisEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/analyses",(Func<HttpContext,Task<IResult>>)(c=>Handle(c,()=>Upload(c))));
        app.MapPost("/api/v1/batches",(Func<HttpContext,Task<IResult>>)(c=>Handle(c,()=>Upload(c))));
        app.MapGet("/api/v1/batches/{batchId}",(HttpContext c,string batchId)=>Handle(c,async()=>
        {
            var user=await User(c);var clinic=Clinic(c);
            return Results.Json(await c.RequestServices.GetRequiredService<BatchStatusService>().GetAsync(user.AccountId,clinic,
                ResourceId(batchId),c.RequestAborted,Correlation(c)),UploadJson.Options);
        }));
        app.MapPost("/api/v1/analyses/{analysisId}/reports",(HttpContext c,string analysisId,ReportFormatRequest request)=>Handle(c,async()=>
        {
            var user=await User(c);var clinic=Clinic(c);
            if (!string.Equals(request.Format,"pdf",StringComparison.OrdinalIgnoreCase))
                throw UploadFailure.Validation(new ErrorDetail("format","must_be_pdf"));
            var report=await c.RequestServices.GetRequiredService<AnalysisReportService>().CreateAnalysisPdfAsync(
                user.AccountId,clinic,ResourceId(analysisId),Correlation(c),c.RequestAborted);
            return Results.Json(report,UploadJson.Options,statusCode:StatusCodes.Status201Created);
        }));
        app.MapGet("/api/v1/analyses/{analysisId}/reports/{reportId}/download",(HttpContext c,string analysisId,string reportId)=>Handle(c,async()=>
        {
            var user=await User(c);var clinic=Clinic(c);
            var stream=await c.RequestServices.GetRequiredService<AnalysisReportService>().OpenReportStreamAsync(
                user.AccountId,clinic,ResourceId(analysisId),ResourceId(reportId),c.RequestAborted);
            c.Response.Headers.CacheControl="no-store";
            return Results.Stream(stream,"application/pdf");
        }));
        app.MapPost("/api/v1/batches/{batchId}/reports",(HttpContext c,string batchId,ReportFormatRequest request)=>Handle(c,async()=>
        {
            var user=await User(c);var clinic=Clinic(c);
            var report=await c.RequestServices.GetRequiredService<AnalysisReportService>().CreateBatchReportAsync(
                user.AccountId,clinic,ResourceId(batchId),request.Format,Correlation(c),c.RequestAborted);
            return Results.Json(report,UploadJson.Options,statusCode:StatusCodes.Status201Created);
        }));
        app.MapGet("/api/v1/batches/{batchId}/reports/{reportId}/download",(HttpContext c,string batchId,string reportId)=>Handle(c,async()=>
        {
            var user=await User(c);var clinic=Clinic(c);
            var (stream, contentType)=await c.RequestServices.GetRequiredService<AnalysisReportService>().OpenBatchReportStreamAsync(
                user.AccountId,clinic,ResourceId(batchId),ResourceId(reportId),c.RequestAborted);
            c.Response.Headers.CacheControl="no-store";
            return Results.Stream(stream, contentType);
        }));
        app.MapGet("/api/v1/analyses/{analysisId}",(HttpContext c,string analysisId)=>Handle(c,async()=>
        {
            var user=await User(c);var clinic=Clinic(c);
            return Results.Json(await c.RequestServices.GetRequiredService<AnalysisQueryService>().GetAsync(user.AccountId,clinic,
                ResourceId(analysisId),c.RequestAborted,Correlation(c)),UploadJson.Options);
        }));
        app.MapGet("/api/v1/doctor/worklist",(HttpContext c)=>Handle(c,async()=>
        {
            var user=await User(c);AnalysisUserPolicy.RequireDoctor(user);
            var clinic=Clinic(c) ?? throw UploadFailure.Validation(new ErrorDetail("clinicContextId","required"));
            var response=await c.RequestServices.GetRequiredService<DoctorWorklistService>().GetAsync(
                c.Request,user.AccountId,clinic,c.RequestAborted,Correlation(c));
            return Results.Json(response,UploadJson.Options);
        }));
        app.MapGet("/api/v1/doctor/analyses/{analysisId}",(HttpContext c,string analysisId)=>Handle(c,async()=>
        {
            var user=await User(c);AnalysisUserPolicy.RequireDoctor(user);
            var clinic=Clinic(c) ?? throw UploadFailure.Validation(new ErrorDetail("clinicContextId","required"));
            var response=await c.RequestServices.GetRequiredService<AnalysisQueryService>().GetAsync(
                user.AccountId,clinic,ResourceId(analysisId),c.RequestAborted,Correlation(c),"DOCTOR_REVIEW");
            return Results.Json(response,UploadJson.Options);
        }));
        app.MapPost("/api/v1/doctor/analyses/{analysisId}/reviews",(HttpContext c,string analysisId,DoctorReviewRequest request)=>Handle(c,async()=>
        {
            var user=await User(c);AnalysisUserPolicy.RequireDoctor(user);
            var clinic=Clinic(c) ?? throw UploadFailure.Validation(new ErrorDetail("clinicContextId","required"));
            var review=await c.RequestServices.GetRequiredService<DoctorReviewService>().SubmitAsync(
                user.AccountId,clinic,ResourceId(analysisId),request,Correlation(c),c.RequestAborted);
            return Results.Json(review,UploadJson.Options,statusCode:StatusCodes.Status201Created);
        }));
        app.MapGet("/api/v1/analyses/{analysisId}/files/{fileId}/access",(HttpContext c,string analysisId,string fileId)=>Handle(c,async()=>
        {
            c.Response.Headers.CacheControl="no-store";
            var user=await User(c);var clinic=Clinic(c);
            return Results.Json(await c.RequestServices.GetRequiredService<FileAccessService>().GetAsync(user.AccountId,clinic,
                ResourceId(analysisId),ResourceId(fileId),c.RequestAborted),UploadJson.Options);
        }));
        app.MapPost("/internal/v1/analysis/settlements/resolve",(HttpContext c,InternalResolveRequest request)=>Handle(c,async()=>
        {
            ServiceAuthValidator.RequireServiceScope(c.Request,"analysis:reconcile");
            return Results.Json(await c.RequestServices.GetRequiredService<InternalResolversService>().ResolveSettlementAsync(
                request,c.RequestAborted),UploadJson.Options);
        }));
        app.MapPost("/internal/v1/analysis/notification-context/resolve",(HttpContext c,InternalResolveRequest request)=>Handle(c,async()=>
        {
            ServiceAuthValidator.RequireServiceScope(c.Request,"analysis:notify");
            return Results.Json(await c.RequestServices.GetRequiredService<InternalResolversService>().ResolveNotificationContextAsync(
                request,c.RequestAborted),UploadJson.Options);
        }));
    }

    private static async Task<IResult> Handle(HttpContext c,Func<Task<IResult>> action)
    {
        var header=c.Request.Headers["X-Correlation-Id"];
        var candidate=header.Count==1?header[0]:null;
        var id=candidate is {Length:>0 and <=64} && candidate.All(x=>char.IsAsciiLetterOrDigit(x)||x is '.' or '_' or '-')
            ?candidate:Guid.NewGuid().ToString("D");
        c.Items["CorrelationId"]=id;c.Response.Headers["X-Correlation-Id"]=id;
        c.Response.Headers.CacheControl="no-store";
        try{return await action();}
        catch(UploadFailure e){return Error(c,e);}
        catch(OperationCanceledException) when(c.RequestAborted.IsCancellationRequested){throw;}
        catch(BadHttpRequestException e) when(e.StatusCode==413){return Error(c,TooLarge());}
        catch(BadHttpRequestException){return Error(c,UploadFailure.Validation(new ErrorDetail("body","invalid_request")));}
        catch(InvalidDataException){return Error(c,UploadFailure.Validation(new ErrorDetail("body","invalid_multipart")));}
        catch(Exception){return Error(c,UploadFailure.Dependency("Analysis dependencies"));}
    }
    private static IResult Error(HttpContext c,UploadFailure e)=>Results.Json(new{error=new{
        code=e.Code,message=e.Message,correlationId=Correlation(c),retryable=e.Retryable,details=e.Details
    }},UploadJson.Options,statusCode:e.Status);
    private static string Correlation(HttpContext c)=>(string)c.Items["CorrelationId"]!;
    private static async Task<AnalysisUserContext> User(HttpContext c)
    {
        var user=await c.RequestServices.GetRequiredService<IAnalysisUserIdentityResolver>().ResolveAsync(c.Request,c.RequestAborted);
        AnalysisUserPolicy.RequireClinicalRole(user);return user;
    }
    private static Guid ResourceId(string value)=>Guid.TryParse(value,out var id)&&id!=Guid.Empty?id
        :throw new UploadFailure(404,"ERR_NOT_FOUND","Không tìm thấy tài nguyên.");
    private static Guid? Clinic(HttpContext c)
    {
        if(!c.Request.Query.TryGetValue("clinicContextId",out var values))return null;
        if(values.Count!=1||!Guid.TryParse(values[0],out var id)||id==Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("clinicContextId","invalid_uuid"));
        return id; // Untrusted selector only; Profile must authorize the resource/tenant.
    }
    private static UploadFailure TooLarge()=>new(413,"ERR_PAYLOAD_TOO_LARGE","Request vượt giới hạn kích thước.");

    private static async Task<IResult> Upload(HttpContext c)
    {
        var user=await User(c);var clinic=Clinic(c);
        var keys=c.Request.Headers["Idempotency-Key"];
        if(keys.Count!=1||!Guid.TryParse(keys[0],out var key)||key==Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("Idempotency-Key","invalid_uuid"));
        var options=c.RequestServices.GetRequiredService<UploadOptions>();
        if(c.Request.ContentLength>options.MaxRequestBytes)throw TooLarge();
        if(!MediaTypeHeaderValue.TryParse(c.Request.ContentType,out var media)||!media.MediaType.Equals("multipart/form-data",StringComparison.OrdinalIgnoreCase))
            throw new UploadFailure(415,"ERR_UNSUPPORTED_MEDIA_TYPE","Cần multipart/form-data.");
        var boundary=HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if(string.IsNullOrEmpty(boundary)||boundary.Length>128)throw UploadFailure.Validation(new ErrorDetail("body","invalid_boundary"));
        var gate=c.RequestServices.GetRequiredService<AnalysisUploadGate>();
        if(!await gate.Slots.WaitAsync(0,c.RequestAborted))throw UploadRecovery.InProgress();
        var buffers=new List<FileBufferingReadStream>();
        try
        {
            var limit=c.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if(limit is {IsReadOnly:false})limit.MaxRequestBodySize=options.MaxRequestBytes;
            var reader=new MultipartReader(boundary,new LimitedReadStream(c.Request.Body,options.MaxRequestBytes))
                {HeadersCountLimit=16,HeadersLengthLimit=8192};
            var fields=new Dictionary<string,string>(StringComparer.Ordinal);
            var files=new List<IUploadedFile>();
            async Task<MultipartSection?> Next()
            {
                try{return await reader.ReadNextSectionAsync(c.RequestAborted);}
                catch(IOException){throw UploadFailure.Validation(new ErrorDetail("body","invalid_multipart"));}
            }
            while(await Next() is { } section)
            {
                if(!ContentDispositionHeaderValue.TryParse(section.ContentDisposition,out var disposition)||!disposition.DispositionType.Equals("form-data",StringComparison.OrdinalIgnoreCase))
                    throw UploadFailure.Validation(new ErrorDetail("body","invalid_disposition"));
                var name=HeaderUtilities.RemoveQuotes(disposition.Name).Value??"";
                if(disposition.FileName.HasValue||disposition.FileNameStar.HasValue)
                {
                    if(name!="images"||files.Count>=options.MaxImagesPerRequest)throw UploadFailure.Validation(new ErrorDetail("images","invalid_count_or_field"));
                    var buffer=new FileBufferingReadStream(new LimitedReadStream(section.Body,options.MaxImageBytes),65536,null,Path.GetTempPath());
                    buffers.Add(buffer);
                    await buffer.CopyToAsync(Stream.Null,c.RequestAborted);buffer.Position=0;
                    var filename=HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue?disposition.FileNameStar:disposition.FileName).Value??"image";
                    var form=new FormFile(buffer,0,buffer.Length,name,filename){Headers=new HeaderDictionary(),ContentType=section.ContentType??"application/octet-stream"};
                    files.Add(new BufferedImage(form));
                }
                else
                {
                    if(name is not ("patientId" or "modality" or "eyeSide")||fields.ContainsKey(name))throw UploadFailure.Validation(new ErrorDetail(name,"unknown_or_repeated_field"));
                    using var value=new MemoryStream();
                    await new LimitedReadStream(section.Body,256).CopyToAsync(value,c.RequestAborted);
                    try{fields.Add(name,new UTF8Encoding(false,true).GetString(value.ToArray()));}
                    catch(DecoderFallbackException){throw UploadFailure.Validation(new ErrorDetail(name,"invalid_encoding"));}
                }
            }
            fields.TryGetValue("patientId",out var patient);fields.TryGetValue("modality",out var modality);fields.TryGetValue("eyeSide",out var eye);
            var result=await c.RequestServices.GetRequiredService<IAnalysisUploadHandler>().CreateAsync(
                new(user.AccountId,clinic,key,Correlation(c),patient,modality,eye,files),c.RequestAborted);
            c.Response.Headers["Location"]=result.Response.StatusUrl;
            if(result.Replayed)c.Response.Headers["Idempotent-Replayed"]="true";
            return Results.Json(result.Response,UploadJson.Options,statusCode:202);
        }
        finally
        {
            try{foreach(var buffer in buffers)await buffer.DisposeAsync();}
            finally{gate.Slots.Release();}
        }
    }
    private sealed class BufferedImage(IFormFile file):IUploadedFile
    {public string FileName=>file.FileName;public string? ContentType=>file.ContentType;public long Length=>file.Length;public Stream OpenReadStream()=>file.OpenReadStream();}
    private sealed class LimitedReadStream(Stream inner,long maximum):Stream
    {
        private long _read;
        private int Count(int n){_read=checked(_read+n);if(_read>maximum)throw TooLarge();return n;}
        public override int Read(byte[] b,int o,int n)
        {try{return Count(inner.Read(b,o,n));}catch(IOException){throw UploadFailure.Validation(new ErrorDetail("body","invalid_multipart"));}}
        public override async ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default)
        {try{return Count(await inner.ReadAsync(b,ct));}catch(IOException){throw UploadFailure.Validation(new ErrorDetail("body","invalid_multipart"));}}
        public override Task<int> ReadAsync(byte[] b,int o,int n,CancellationToken ct)=>ReadAsync(b.AsMemory(o,n),ct).AsTask();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
}
