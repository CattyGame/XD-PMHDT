using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Upload;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Npgsql;

namespace AURA.Analysis.Api.Exports;

public sealed class ExportService(
    IExportRepository snapshots, ICampaignRepository campaigns, IClinicAccessResolver clinicAccess,
    IAnalyticsAccessResolver researchAccess, IPrivateFileStorage storage, StorageOptions storageOptions,
    TimeProvider clock)
{
    private static readonly TimeSpan Lifetime=TimeSpan.FromMinutes(15);

    public async Task<ExportReceipt> CreateCampaignReportAsync(Guid actor, Guid clinic, Guid campaignId,
        CampaignReportRequest request, string key, string? correlation, CancellationToken ct)
    {
        ValidateKey(key);
        if(request.Format is not ("CSV" or "PDF")) throw UploadFailure.Validation(new ErrorDetail("format","must_be_csv_or_pdf"));
        var decision=await clinicAccess.ResolveAsync(new ClinicAccessRequest(actor,clinic,"CAMPAIGN_MANAGE"),ct);
        if(!decision.Allowed||decision.TenantId==Guid.Empty) throw new UploadFailure(403,"ERR_FORBIDDEN","Không có quyền xuất báo cáo chiến dịch.");
        var fingerprint=Fingerprint(new{campaignId,clinic,request.Format});
        var replay=await snapshots.FindByKeyAsync(decision.TenantId,actor,"CAMPAIGN_REPORT",key,ct);
        if(replay is not null) return Replay(replay,fingerprint);
        var patientScope=await snapshots.ReadCampaignPatientScopeAsync(decision.TenantId,campaignId,ct);
        await RequirePatientsCurrentAsync(actor,decision.TenantId,clinic,patientScope,correlation,ct);
        var summary=await campaigns.GetCampaignSummaryAsync(decision.TenantId,campaignId,ct);
        var bytes=request.Format=="CSV"
            ? Encoding.UTF8.GetBytes(await campaigns.ExportCampaignReportCsvAsync(decision.TenantId,campaignId,ct))
            : RenderCampaignPdf(summary);
        var snapshotJson=JsonSerializer.Serialize(new{purpose="CAMPAIGN_REPORT",campaignId,createdAt=clock.GetUtcNow(),correlationId=correlation,summary});
        return await StoreAsync(decision.TenantId,clinic,actor,"CAMPAIGN_REPORT",campaignId,request.Format,key,fingerprint,
            patientScope,"CAMPAIGN_REPORT",snapshotJson,correlation,bytes,request.Format=="CSV"?"text/csv":"application/pdf",ct);
    }

    public async Task<ExportReceipt> CreateResearchExportAsync(Guid actor, ResearchExportRequest request,
        string key, string? correlation, CancellationToken ct)
    {
        ValidateKey(key); ResearchExportRequestValidator.Validate(request);
        if(request.IncludeMasks) throw new UploadFailure(403,"ERR_FORBIDDEN","Không có nguồn chấp thuận hiện hành cho mask.");
        var grant=await researchAccess.ResolveAsync(new AnalyticsAccessRequest(actor,"RESEARCH_EXPORT","CLINIC",
            request.ClinicContextId,null,correlation,request.Purpose,false,false),ct);
        var tenant=grant.TenantId.GetValueOrDefault(); var clinic=grant.ClinicId.GetValueOrDefault();
        if(tenant==Guid.Empty||clinic!=request.ClinicContextId) throw new UploadFailure(403,"ERR_FORBIDDEN","Phạm vi xuất dữ liệu không hợp lệ.");
        if(request.CampaignId is { } campaignId) _=await campaigns.GetCampaignByIdAsync(tenant,campaignId,ct);
        var ids=grant.AuthorizedPatientIds?.Distinct().ToArray()
            ??throw new UploadFailure(503,"ERR_SERVICE_UNAVAILABLE","Profile không trả về phạm vi bệnh nhân hữu hạn.");
        if(ids.Length==0) throw new UploadFailure(403,"ERR_FORBIDDEN","Không có bệnh nhân nào được cấp quyền xuất.");
        if(ids.Length>=500) throw new UploadFailure(503,"ERR_SERVICE_UNAVAILABLE","Phạm vi được cấp vượt giới hạn xử lý an toàn.");
        var fingerprint=Fingerprint(new{request.ClinicContextId,request.CampaignId,request.Purpose,request.Format,request.IncludeMasks});
        var replay=await snapshots.FindByKeyAsync(tenant,actor,"RESEARCH_EXPORT",key,ct);
        if(replay is not null) return Replay(replay,fingerprint);
        var rows=await snapshots.ReadResearchRowsAsync(tenant,clinic,request.CampaignId,ids,ct);
        if(rows.Count>10000) throw new UploadFailure(413,"ERR_PAYLOAD_TOO_LARGE","Phạm vi xuất vượt giới hạn 10000 bản ghi.");
        if(rows.Any(x=>!ids.Contains(x.PatientId))) throw new UploadFailure(503,"ERR_SERVICE_UNAVAILABLE","Phạm vi kết quả không khớp quyền hiện hành.");
        var pseudonyms=new Dictionary<Guid,string>();
        var projection=rows.Select(x=>new ResearchExportRow(Pseudonym(x.PatientId,pseudonyms),x.RiskLevel,x.MeasurementAvailable,x.Avr)).ToArray();
        var bytes=request.Format.Equals("CSV",StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(ResearchExportWriter.WriteCsv(projection))
            : RenderResearchPdf(projection);
        var snapshotJson=JsonSerializer.Serialize(new{purpose=request.Purpose,campaignId=request.CampaignId,createdAt=clock.GetUtcNow(),correlationId=correlation,
            sources=rows.Select(x=>new{x.PatientId,x.ResultId,x.ModelVersion,x.ConfigVersion,x.ThresholdVersion,x.ReviewVersion}).ToArray()});
        return await StoreAsync(tenant,clinic,actor,"RESEARCH_EXPORT",request.CampaignId,request.Format.ToUpperInvariant(),key,
            fingerprint,ids,request.Purpose,snapshotJson,correlation,bytes,request.Format.Equals("CSV",StringComparison.OrdinalIgnoreCase)?"text/csv":"application/pdf",ct);
    }

    public async Task<ExportReceipt> GetStatusAsync(Guid actor,Guid clinic,Guid id,string? correlation,CancellationToken ct)
    {
        var item=await RequireCurrentAsync(actor,clinic,id,correlation,ct);
        return Receipt(item);
    }

    public async Task<PrivateContent> OpenContentAsync(Guid actor,Guid clinic,Guid id,string? correlation,CancellationToken ct)
    {
        var item=await RequireCurrentAsync(actor,clinic,id,correlation,ct);
        return new(await storage.OpenReadAsync(storageOptions.ReportContainer,item.ObjectKey,ct),item.ContentType);
    }

    public async Task<bool> HasSnapshotAsync(Guid id,CancellationToken ct)=>await snapshots.FindByIdAsync(id,ct) is not null;

    private async Task<ExportSnapshot> RequireCurrentAsync(Guid actor,Guid clinic,Guid id,string? correlation,CancellationToken ct)
    {
        var item=await snapshots.FindByIdAsync(id,ct);
        if(item is null||item.ClinicId!=clinic) throw new UploadFailure(404,"ERR_NOT_FOUND","Không tìm thấy báo cáo hoặc đã hết hạn.");
        if(item.Kind=="CAMPAIGN_REPORT")
        {
            var decision=await clinicAccess.ResolveAsync(new ClinicAccessRequest(actor,clinic,"CAMPAIGN_READ"),ct);
            if(!decision.Allowed||decision.TenantId!=item.TenantId) throw new UploadFailure(403,"ERR_FORBIDDEN","Quyền truy cập báo cáo đã thay đổi.");
            await RequirePatientsCurrentAsync(actor,item.TenantId,clinic,item.PatientScope,correlation,ct);
        }
        else
        {
            var grant=await researchAccess.ResolveAsync(new AnalyticsAccessRequest(actor,"RESEARCH_EXPORT","CLINIC",clinic,null,correlation,
                item.Purpose,false,false),ct);
            if(grant.TenantId!=item.TenantId||grant.ClinicId!=clinic) throw new UploadFailure(403,"ERR_FORBIDDEN","Quyền truy cập báo cáo đã thay đổi.");
            var allowed=grant.AuthorizedPatientIds;
            if(allowed is {Count:>=500}) throw new UploadFailure(503,"ERR_SERVICE_UNAVAILABLE","Phạm vi hiện hành vượt giới hạn xác minh an toàn.");
            if(item.PatientScope.Count>0&&(allowed is null||item.PatientScope.Any(p=>!allowed.Contains(p))))
                throw new UploadFailure(403,"ERR_FORBIDDEN","Phạm vi được phép đã thay đổi; không thể tải báo cáo.");
        }
        return item;
    }

    private async Task RequirePatientsCurrentAsync(Guid actor,Guid tenant,Guid clinic,IReadOnlyList<Guid> patients,string? correlation,CancellationToken ct)
    {
        if(patients.Count>=500) throw new UploadFailure(503,"ERR_SERVICE_UNAVAILABLE","Phạm vi chiến dịch vượt giới hạn xác minh quyền hiện hành.");
        foreach(var patient in patients)
        {
            var grant=await researchAccess.ResolveAsync(new AnalyticsAccessRequest(actor,"ANALYTICS_READ","PATIENT",clinic,patient,correlation),ct);
            if(grant.TenantId!=tenant||grant.ClinicId!=clinic||grant.PatientId!=patient)
                throw new UploadFailure(403,"ERR_FORBIDDEN","Quyền truy cập một phần dữ liệu báo cáo không còn hiệu lực.");
        }
    }

    private async Task<ExportReceipt> StoreAsync(Guid tenant,Guid clinic,Guid actor,string kind,Guid? campaign,string format,
        string key,string fingerprint,IReadOnlyList<Guid> scope,string purpose,string snapshotJson,string? correlation,byte[] bytes,string contentType,CancellationToken ct)
    {
        if(bytes.LongLength>AnalysisReportService.MaximumBatchExportBytes)
            throw new UploadFailure(413,"ERR_REPORT_TOO_LARGE","Báo cáo vượt giới hạn dung lượng cho phép.");
        var id=Guid.NewGuid(); var objectKey=$"scrum385/{id:D}/{id:N}.{(format=="CSV"?"csv":"pdf")}";
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expires=clock.GetUtcNow().Add(Lifetime);
        await using(var stream=new MemoryStream(bytes,false)) await storage.PutAsync(storageOptions.ReportContainer,objectKey,stream,contentType,ct);
        var item=new ExportSnapshot(id,tenant,clinic,actor,kind,campaign,format,objectKey,contentType,hash,fingerprint,purpose,snapshotJson,key,scope,expires,correlation??Guid.NewGuid().ToString("D"));
        try { await snapshots.InsertAsync(item,ct); }
        catch (Npgsql.PostgresException e) when(e.SqlState=="23505")
        {
            await storage.DeleteAsync(storageOptions.ReportContainer,objectKey,ct);
            var existing=await snapshots.FindByKeyAsync(tenant,actor,kind,key,ct);
            if(existing is not null) return Replay(existing,fingerprint);
            throw;
        }
        return Receipt(item);
    }

    private static ExportReceipt Replay(ExportSnapshot item,string fingerprint)
    {
        if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(item.Fingerprint),Convert.FromHexString(fingerprint)))
            throw new UploadFailure(409,"ERR_CONFLICT","Idempotency-Key đã được dùng với nội dung khác.");
        return Receipt(item);
    }
    private static ExportReceipt Receipt(ExportSnapshot item)=>new(item.Id,"COMPLETED",$"/api/v1/reports/{item.Id:D}?clinicContextId={item.ClinicId:D}",item.ExpiresAt);
    private static string Fingerprint(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
    private static void ValidateKey(string key)
    { if(string.IsNullOrWhiteSpace(key)||key.Length>128||key.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_' and not '.')) throw UploadFailure.Validation(new ErrorDetail("Idempotency-Key","required_max_128_safe_chars")); }
    private static string Pseudonym(Guid patient,Dictionary<Guid,string> map)
    { if(!map.TryGetValue(patient,out var value)){value="CASE-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(6));map.Add(patient,value);}return value; }
    private static byte[] RenderCampaignPdf(CampaignSummaryResponse s)=>Document.Create(d=>d.Page(p=>{p.Margin(32);p.Content().Column(c=>{c.Item().Text("AURA - Campaign Summary").FontSize(18).Bold();c.Item().Text($"Status: {s.Status}");c.Item().Text($"Images: {s.TotalImages}; completed: {s.CompletedImages}; failed: {s.FailedImages}");c.Item().Text($"LOW: {s.NormalCases}; MODERATE/HIGH: {s.SuspectedCases}");c.Item().Text("Research summary only; not a clinical diagnosis.");});})).GeneratePdf();
    private static byte[] RenderResearchPdf(IReadOnlyList<ResearchExportRow> rows)=>Document.Create(d=>d.Page(p=>{p.Margin(32);p.Content().Column(c=>{c.Item().Text("AURA - De-identified Research Export").FontSize(18).Bold();c.Item().Text($"Cases: {rows.Count}");foreach(var r in rows)c.Item().Text($"{r.CaseCode} | {r.RiskLevel??"UNKNOWN"} | measurement: {r.MeasurementAvailable} | AVR: {r.Avr?.ToString()??"N/A"}");c.Item().Text("Research summary only; not a clinical diagnosis.");});})).GeneratePdf();
}
