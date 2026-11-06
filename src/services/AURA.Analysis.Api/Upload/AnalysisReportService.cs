using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Text;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Campaign;
using Microsoft.AspNetCore.Http;
using Npgsql;
using SkiaSharp;

namespace AURA.Analysis.Api.Upload;

public sealed record MedicalReportExportRequest(string Format);
public sealed record ReportDownloadResponse(Guid ReportId, string Format, string DownloadUrl, DateTime ExpiresAt);
public sealed record DoctorReportReview(Guid DoctorId, bool IsApproved, string? CorrectedRiskLevel,
    string? IcdrLevel, string? Reason, string? Notes, DateTime CreatedAt)
{
    public Guid ReviewId { get; init; }
    public int ReviewVersion { get; init; }
}

public sealed record BatchArchiveManifestItem(string CaseCode, Guid AnalysisId, string ProcessingStatus,
    string? ErrorCode, Guid? ResultId, int? ResultVersion, Guid? ReviewId, int? ReviewVersion,
    Guid? ReportId, string? SnapshotSha256, string? PdfEntry, string? PdfSha256);

public sealed class AnalysisReportOptions
{
    public string PublicApiBaseUrl { get; set; } = "http://localhost:5000";
}

/// <summary>Generates authorized, de-identified batch exports and single-case PDF reports in private Blob storage.</summary>
public sealed class AnalysisReportService(
    AnalysisQueryService analyses,
    BatchStatusService batches,
    IFileAccessRepository files,
    IPrivateFileStorage storage,
    StorageOptions storageOptions,
    AnalysisReportOptions reportOptions,
    NpgsqlDataSource dataSource,
    TimeProvider clock)
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public async Task<ReportDownloadResponse> CreateAnalysisPdfAsync(Guid requesterId, Guid? clinicId, Guid analysisId,
        string? correlationId, CancellationToken ct)
    {
        var detail = await analyses.GetAsync(requesterId, clinicId, analysisId, ct, correlationId);
        if (detail.ProcessingStatus != "COMPLETED" || detail.Result is null)
            throw new UploadFailure(409, "ERR_INVALID_STATE", "Chỉ có thể xuất báo cáo khi phân tích đã hoàn tất.");

        var original = await files.FindAsync(detail.AnalysisId, detail.Image.FileId, ct)
            ?? throw UploadFailure.Dependency("Analysis image metadata");
        var overlayRef = detail.Result.Annotations.FirstOrDefault(x => x.Kind == "overlay")
            ?? throw new UploadFailure(409, "ERR_REPORT_SOURCE_MISSING", "Chưa có ảnh overlay để tạo báo cáo.");
        var overlay = await files.FindAsync(detail.AnalysisId, overlayRef.FileId, ct)
            ?? throw UploadFailure.Dependency("Analysis overlay metadata");
        var review = await ReadReviewAsync(detail.AnalysisId, original.TenantId,detail.Result.ResultId,detail.ReviewVersion, ct);

        await using var originalStream = await storage.OpenReadAsync(storageOptions.OriginalContainer, original.ObjectKey, ct);
        await using var overlayStream = await storage.OpenReadAsync(storageOptions.AnnotationContainer, overlay.ObjectKey, ct);
        var originalBytes = await ReadImageBytesAsync(originalStream, ct);
        var overlayBytes = await ReadImageBytesAsync(overlayStream, ct);
        if(original.Sha256 is not {Length:64} || overlay.Sha256 is not {Length:64}
            || Hash(originalBytes)!=original.Sha256 || Hash(overlayBytes)!=overlay.Sha256)
            throw new UploadFailure(409,"ERR_REPORT_SOURCE_MISSING","Nguồn ảnh báo cáo không khớp metadata đã lưu.");

        var reportId = Guid.NewGuid();
        var snapshot=new{reportId,detail.AnalysisId,detail.PatientId,detail.BatchId,detail.Image,detail.Result,review,
            originalSha256=Hash(originalBytes),overlaySha256=Hash(overlayBytes)};
        var snapshotJson=JsonSerializer.Serialize(snapshot,UploadJson.Options);
        var digest=ReportSnapshotDigest.Compute(snapshotJson);
        var metrics = detail.Result.Metrics;
        var secureAnalysisUrl = new Uri(new Uri(reportOptions.PublicApiBaseUrl.TrimEnd('/') + "/"),
            $"api/v1/analyses/{detail.AnalysisId:D}").AbsoluteUri;
        var pdfBytes = MedicalReportPdfRenderer.Render(new MedicalReportPdfData(
            reportId, detail.AnalysisId, detail.BatchId, detail.PatientId, clinicId,
            review?.DoctorId, DateTime.SpecifyKind(detail.CreatedAt, DateTimeKind.Utc), review is null?"PENDING":review.IsApproved?"REVIEWED":"CORRECTION_REQUIRED",
            detail.Result.ModelVersion, detail.Result.RiskLevel, review?.IsApproved == true,
            review?.IsApproved == true ? review.IcdrLevel : null, review?.Notes, review?.Reason,
            metrics.VesselDensity, metrics.TortuosityIndex, metrics.AvRatio, detail.Result.Disclaimer,
            secureAnalysisUrl, originalBytes, overlayBytes)
            {ResultId=detail.Result.ResultId,ResultVersion=detail.Result.Version,ReviewId=review?.ReviewId,
                ReviewVersion=review?.ReviewVersion,ConfigVersion=detail.Result.ConfigVersion,ThresholdVersion=detail.Result.ThresholdVersion,
                CorrectedRiskLevel=review?.CorrectedRiskLevel,SnapshotDigest=digest});
        await using var pdf = new MemoryStream(pdfBytes, writable: false);
        return await StoreAndSignAsync(reportId, "pdf", original.TenantId, detail.BatchId, detail.AnalysisId,
            "application/pdf", pdf,requesterId,clinicId,snapshotJson,ct);
    }

    private static async Task<byte[]> ReadImageBytesAsync(Stream stream, CancellationToken ct)
    {
        await using var output = new MemoryStream();
        var buffer=new byte[65536];
        while(true)
        {
            var count=await stream.ReadAsync(buffer,ct);if(count==0)break;
            if(output.Length+count>10_485_760)throw UploadFailure.Dependency("Report image size");
            await output.WriteAsync(buffer.AsMemory(0,count),ct);
        }
        if (output.Length is < 1 or > 10_485_760)
            throw UploadFailure.Dependency("Report image size");
        return output.ToArray();
    }

    public async Task<ReportDownloadResponse> CreateBatchReportAsync(Guid requesterId, Guid? clinicId, Guid batchId,
        string format, string? correlationId, CancellationToken ct)
    {
        var status = await batches.GetAsync(requesterId, clinicId, batchId, ct, correlationId);
        if(status.BatchStatus=="IN_PROGRESS")throw new UploadFailure(409,"ERR_INVALID_STATE","Chỉ xuất tổng kết khi tiếp nhận và xử lý đã hoàn tất.");
        var normalized = (format ?? "").Trim().ToLowerInvariant();
        if (normalized is not ("pdf" or "csv" or "zip"))
            throw UploadFailure.Validation(new ErrorDetail("format", "must_be_pdf_csv_or_zip"));

        byte[] bytes;
        var reportId = Guid.NewGuid();
        var tenantId = await ReadTenantIdAsync(batchId, ct);
        var sources=new List<object>();var pinned=new List<BatchPublicItem>();
        var expectedSources=new Dictionary<Guid,(Guid ResultId,int ResultVersion,Guid? ReviewId)>();
        var doctorReviews=new Dictionary<Guid,DoctorReportReview?>();
        foreach(var item in status.Items)
        {
            if(item.ProcessingStatus!="COMPLETED"){pinned.Add(item);continue;}
            var detail=await analyses.GetAsync(requesterId,clinicId,item.AnalysisId,ct,correlationId);
            if(detail.ProcessingStatus!="COMPLETED" || detail.Result is null)throw new UploadFailure(409,"ERR_INVALID_STATE","Trạng thái ca bệnh đã thay đổi.");
            var review=await ReadReviewAsync(item.AnalysisId,tenantId,detail.Result.ResultId,detail.ReviewVersion,ct);
            sources.Add(new{detail.AnalysisId,detail.Image,detail.Result,review});
            expectedSources.Add(item.AnalysisId,(detail.Result.ResultId,detail.Result.Version,review?.ReviewId));
            doctorReviews.Add(item.AnalysisId,review);
            pinned.Add(item with{RiskLevel=detail.Result.RiskLevel,ReviewStatus=review is null?"PENDING":review.IsApproved?"REVIEWED":"CORRECTION_REQUIRED"});
        }
        status=status with{Items=pinned};
        if(normalized=="zip")
        {
            using var archiveBytes=new MemoryStream();
            var manifestItems=new List<BatchArchiveManifestItem>();
            using(var zip=new ZipArchive(archiveBytes,ZipArchiveMode.Create,true))
            {
                for(var itemIndex=0;itemIndex<status.Items.Count;itemIndex++)
                {
                    var item=status.Items[itemIndex];
                    var caseCode=$"CASE-{itemIndex+1:D4}";
                    if(item.ProcessingStatus!="COMPLETED")
                    {
                        manifestItems.Add(new(caseCode,item.AnalysisId,item.ProcessingStatus,item.Error?.Code,
                            null,null,null,null,null,null,null,null));
                        continue;
                    }
                    var report=await CreateAnalysisPdfAsync(requesterId,clinicId,item.AnalysisId,correlationId,ct);
                    Guid resultId;
                    Guid? reviewId;
                    string snapshotSha256;
                    await using(var source=dataSource.CreateCommand("SELECT (snapshot#>>'{result,resultId}')::uuid,(snapshot#>>'{review,reviewId}')::uuid,snapshot_sha256 FROM analysis.report_snapshots WHERE id=$1"))
                    {
                        source.Parameters.AddWithValue(report.ReportId);await using var reader=await source.ExecuteReaderAsync(ct);
                        var expected=expectedSources[item.AnalysisId];
                        if(!await reader.ReadAsync(ct) || reader.GetGuid(0)!=expected.ResultId || (reader.IsDBNull(1)?(Guid?)null:reader.GetGuid(1))!=expected.ReviewId)
                            throw new UploadFailure(409,"ERR_INVALID_STATE","Nguồn báo cáo thay đổi trong lúc xuất. Hãy tạo snapshot mới.");
                        resultId=reader.GetGuid(0);
                        reviewId=reader.IsDBNull(1)?null:reader.GetGuid(1);
                        snapshotSha256=reader.GetString(2);
                    }
                    await using var input=await ReadDownloadAsync(requesterId,clinicId,report.ReportId,correlationId,ct);
                    var pdfEntry=$"case-{item.AnalysisId:D}.pdf";
                    await using var output=zip.CreateEntry(pdfEntry,CompressionLevel.Fastest).Open();
                    var pdfSha256=await CopyAndHashAsync(input,output,ct);
                    var sourceReview=doctorReviews.GetValueOrDefault(item.AnalysisId);
                    manifestItems.Add(new(caseCode,item.AnalysisId,item.ProcessingStatus,item.Error?.Code,
                        resultId,expectedSources[item.AnalysisId].ResultVersion,reviewId,sourceReview?.ReviewVersion,
                        report.ReportId,snapshotSha256,pdfEntry,pdfSha256));
                    sources.Add(new{item.AnalysisId,report.ReportId,snapshotDigest=snapshotSha256});
                }
                await using (var summary = zip.CreateEntry("summary.csv").Open())
                {
                    await summary.WriteAsync(RenderBatchCsv(status, clock.GetUtcNow(), doctorReviews), ct);
                }
                var manifest=JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion="1.0",
                    batchId,
                    reportId,
                    generatedAtUtc=clock.GetUtcNow(),
                    batchStatus=status.BatchStatus,
                    summary=status.Summary,
                    items=manifestItems
                },ManifestJsonOptions);
                await using(var manifestStream=zip.CreateEntry("manifest.json",CompressionLevel.Fastest).Open())
                    await manifestStream.WriteAsync(manifest,ct);
            }
            bytes=archiveBytes.ToArray();
        }
        else if (normalized == "csv")
            bytes = RenderBatchCsv(status, clock.GetUtcNow(),doctorReviews);
        else
            bytes = RenderBatchPdf(status, reportId, clock.GetUtcNow(),doctorReviews);

        await using var content = new MemoryStream(bytes, writable: false);
        return await StoreAndSignAsync(reportId, normalized, tenantId, batchId, batchId,
            normalized == "csv" ? "text/csv" : normalized=="zip"?"application/zip":"application/pdf", content,
            requesterId,clinicId,JsonSerializer.Serialize(new{reportId,status,sources},UploadJson.Options),ct);
    }

    private async Task<Guid> ReadTenantIdAsync(Guid batchId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT tenant_id FROM analysis.batches WHERE id=$1;");
        command.Parameters.AddWithValue(batchId);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is Guid tenant && tenant != Guid.Empty) return tenant;
        throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");
    }

    private async Task<DoctorReportReview?> ReadReviewAsync(Guid analysisId, Guid tenantId,Guid resultId,int reviewVersion, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT doctor_id,is_approved,corrected_risk_level,icdr_level,reason,doctor_notes,created_at,id,version
            FROM analysis.doctor_reviews
            WHERE tenant_id=$1 AND image_id=$2 AND analysis_result_id=$3 AND version<=$4
            ORDER BY version DESC LIMIT 1;
            """);
        command.Parameters.AddWithValue(tenantId);
        command.Parameters.AddWithValue(analysisId);
        command.Parameters.AddWithValue(resultId);command.Parameters.AddWithValue(reviewVersion);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetDateTime(6)){ReviewId=reader.GetGuid(7),ReviewVersion=reader.GetInt32(8)};
    }

    private async Task<ReportDownloadResponse> StoreAndSignAsync(Guid reportId, string format, Guid tenantId,
        Guid batchId, Guid resourceId, string contentType, Stream content,Guid requesterId,Guid? clinicId,string snapshot,CancellationToken ct)
    {
        var key = $"{tenantId:D}/{batchId:D}/{resourceId:D}/{reportId:D}";
        await storage.PutAsync(storageOptions.ReportContainer, key, content, contentType, ct);
        try
        {
            var expiry=clock.GetUtcNow().Add(LinkLifetime).UtcDateTime;
            await using var command=dataSource.CreateCommand("""
                INSERT INTO analysis.report_snapshots(id,tenant_id,batch_id,analysis_id,requester_id,clinic_id,format,object_key,content_type,snapshot,snapshot_sha256,expires_at)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                """);
            command.Parameters.AddWithValue(reportId);command.Parameters.AddWithValue(tenantId);command.Parameters.AddWithValue(batchId);
            command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid,resourceId==batchId?DBNull.Value:resourceId);
            command.Parameters.AddWithValue(requesterId);command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid,(object?)clinicId??DBNull.Value);
            command.Parameters.AddWithValue(format);command.Parameters.AddWithValue(key);command.Parameters.AddWithValue(contentType);
            command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Jsonb,snapshot);command.Parameters.AddWithValue(ReportSnapshotDigest.Compute(snapshot));command.Parameters.AddWithValue(expiry);
            await command.ExecuteNonQueryAsync(ct);
            var selector=clinicId is {} clinic?$"?clinicContextId={clinic:D}":"";
            return new(reportId,format,$"/api/v1/reports/{reportId:D}/content{selector}",expiry);
        }
        catch { throw; } // An ambiguous DB acknowledgement must never delete a possibly referenced private report.
    }
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static async Task<string> CopyAndHashAsync(Stream input,Stream output,CancellationToken ct)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[64*1024];
        while(true)
        {
            var count=await input.ReadAsync(buffer,ct);
            if(count==0)break;
            hash.AppendData(buffer,0,count);
            await output.WriteAsync(buffer.AsMemory(0,count),ct);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public async Task<Stream> ReadDownloadAsync(Guid requesterId,Guid? clinicId,Guid reportId,string? correlation,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("SELECT tenant_id,batch_id,analysis_id,requester_id,clinic_id,object_key,expires_at FROM analysis.report_snapshots WHERE id=$1");
        command.Parameters.AddWithValue(reportId);await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct) || reader.GetGuid(3)!=requesterId || (reader.IsDBNull(4)?(Guid?)null:reader.GetGuid(4))!=clinicId
            || reader.GetDateTime(6)<=clock.GetUtcNow().UtcDateTime)throw new UploadFailure(404,"ERR_NOT_FOUND","Không tìm thấy báo cáo hoặc đã hết hạn.");
        var batch=reader.GetGuid(1);var analysis=reader.IsDBNull(2)?(Guid?)null:reader.GetGuid(2);var key=reader.GetString(5);await reader.CloseAsync();
        if(analysis is {} id)await analyses.GetAsync(requesterId,clinicId,id,ct,correlation);
        else await batches.GetAsync(requesterId,clinicId,batch,ct,correlation);
        return await storage.OpenReadAsync(storageOptions.ReportContainer,key,ct);
    }
    public async Task<PrivateContent> ReadDownloadContentAsync(Guid requesterId,Guid? clinicId,Guid reportId,string? correlation,CancellationToken ct)
    {
        var stream=await ReadDownloadAsync(requesterId,clinicId,reportId,correlation,ct);
        try
        {
            await using var command=dataSource.CreateCommand("SELECT content_type FROM analysis.report_snapshots WHERE id=$1");command.Parameters.AddWithValue(reportId);
            var type=await command.ExecuteScalarAsync(ct) as string;
            if(type is not ("application/pdf" or "text/csv" or "application/zip"))throw UploadFailure.Dependency("Report metadata");
            return new(stream,type);
        }
        catch{await stream.DisposeAsync();throw;}
    }

    public static byte[] RenderBatchCsv(BatchStatusResponse report, DateTimeOffset generatedAt,IReadOnlyDictionary<Guid,DoctorReportReview?> reviews)
    {
        var suspicious = report.Items.Count(x => x.ProcessingStatus == "COMPLETED" && x.RiskLevel is "MODERATE" or "HIGH");
        var denominator = report.Items.Count(x => x.ProcessingStatus == "COMPLETED" && x.RiskLevel is not null);
        var rate = Rate(suspicious,denominator);
        var reviewed=report.Items.Where(i=>reviews.GetValueOrDefault(i.AnalysisId) is not null).ToArray();
        var reviewedAvailable=reviewed.Where(i=>DoctorConclusion(i,reviews.GetValueOrDefault(i.AnalysisId)) is not null).ToArray();
        var reviewedRate=Rate(reviewedAvailable.Count(i=>DoctorConclusion(i,reviews.GetValueOrDefault(i.AnalysisId)) is "HIGH" or "MODERATE"),reviewedAvailable.Length);
        using var output = new MemoryStream();
        using (var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        {
            writer.WriteLine("recordType,caseCode,processingStatus,reviewStatus,aiRiskLevel,doctorConclusion,reviewId,reviewVersion,errorCode,totalAccepted,completed,failed,aiAtypicalRate,aiAvailableCount,doctorAtypicalRate,doctorAvailableCount,generatedAtUtc");
            writer.WriteLine(CsvRow("summary", "", report.BatchStatus, "", "", "", "", "", "", report.Summary.Total,
                report.Summary.Succeeded, report.Summary.Failed, rate,denominator,reviewedRate,reviewedAvailable.Length, generatedAt.ToString("O", CultureInfo.InvariantCulture)));
            var index = 0;
            foreach (var item in report.Items)
            {
                index++;
                var review=reviews.GetValueOrDefault(item.AnalysisId);
                writer.WriteLine(CsvRow("case", $"CASE-{index:D4}", item.ProcessingStatus, item.ReviewStatus,
                    item.RiskLevel ?? "UNAVAILABLE",DoctorConclusion(item,review)??"NOT_REVIEWED",review?.ReviewId,review?.ReviewVersion,
                    item.Error?.Code ?? "", "", "", "", "", "", "", "", ""));
            }
            writer.Flush();
        }
        return output.ToArray();
    }

    private static string CsvRow(params object?[] values) => CampaignCsvWriter.Row(values);
    private static string Rate(int count,int denominator)=>denominator==0?"N/A":(100d*count/denominator).ToString("F2",CultureInfo.InvariantCulture)+"%";
    private static string? DoctorConclusion(BatchPublicItem item,DoctorReportReview? review)=>review is null?null:review.CorrectedRiskLevel??(review.IsApproved?item.RiskLevel:null);

    private static byte[] RenderBatchPdf(BatchStatusResponse report, Guid reportId, DateTimeOffset generatedAt,IReadOnlyDictionary<Guid,DoctorReportReview?> reviews)
    {
        using var output = new MemoryStream();
        using var document = SKDocument.CreatePdf(output);
        var suspicious = report.Items.Count(x => x.ProcessingStatus == "COMPLETED" && x.RiskLevel is "MODERATE" or "HIGH");
        var denominator = report.Items.Count(x => x.ProcessingStatus == "COMPLETED" && x.RiskLevel is not null);
        var rate = Rate(suspicious,denominator);
        var page = document.BeginPage(595, 842);
        DrawText(page, "AURA - Batch Summary Report", 36, 42, 22, true);
        DrawText(page, $"Batch: {report.BatchId:D}    Report: {reportId:D}", 36, 72);
        DrawText(page, $"Generated (UTC): {generatedAt:yyyy-MM-dd HH:mm:ss}", 36, 96);
        DrawText(page, $"Status: {report.BatchStatus}    Total: {report.Summary.Total}    Completed: {report.Summary.Succeeded}    Failed: {report.Summary.Failed}", 36, 124);
        DrawText(page, $"AI research indicator: {rate} ({suspicious}/{denominator} available; not a clinical diagnosis)", 36, 150, 10, true);
        DrawText(page, "Case (de-identified)", 36, 194, 10, true);
        DrawText(page, "Processing", 198, 194, 10, true);
        DrawText(page, "Review", 310, 194, 10, true);
        DrawText(page, "AI", 410, 194, 10, true);
        DrawText(page, "Doctor", 490, 194, 10, true);
        var rowIndex = 0;
        foreach (var item in report.Items)
        {
            if (rowIndex > 0 && rowIndex % 28 == 0)
            {
                document.EndPage();
                page = document.BeginPage(595, 842);
                DrawText(page, "AURA - Batch Summary (continued)", 36, 42, 18, true);
                DrawText(page, "Case (de-identified)", 36, 72, 10, true);
                DrawText(page, "Processing", 198, 72, 10, true);
                DrawText(page, "Review", 310, 72, 10, true);
                DrawText(page, "AI", 410, 72, 10, true);
                DrawText(page, "Doctor", 490, 72, 10, true);
            }
            var y = (rowIndex % 28) * 21 + (rowIndex < 28 ? 217 : 95);
            DrawText(page, $"CASE-{rowIndex + 1:D4}", 36, y, 9);
            DrawText(page, item.ProcessingStatus, 198, y, 9);
            DrawText(page, item.ReviewStatus, 310, y, 9);
            DrawText(page, item.RiskLevel ?? "-", 410, y, 9);
            DrawText(page, DoctorConclusion(item,reviews.GetValueOrDefault(item.AnalysisId))??"Not reviewed", 490, y, 8);
            rowIndex++;
        }
        document.EndPage();
        document.Close();
        return output.ToArray();
    }

    private static void DrawText(SKCanvas canvas, string text, float x, float y, float size = 10, bool bold = false)
    {
        using var typeface = SKTypeface.FromFamilyName("Arial", bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var font = new SKFont(typeface, size);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

}
