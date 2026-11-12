using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Upload;
using Npgsql;
using QuestPDF.Infrastructure;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class ReportSnapshotPostgresTests
{
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Query(AnalysisDetailResponse detail,AnalysisIdentity identity):IAnalysisQueryRepository
    {
        public Task<AnalysisIdentity?> FindIdentityAsync(Guid id,CancellationToken ct)=>Task.FromResult<AnalysisIdentity?>(id==identity.AnalysisId?identity:null);
        public Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity id,string? correlation,CancellationToken ct)=>Task.FromResult<AnalysisDetailResponse?>(detail);
    }
    private sealed class FixedBatchRepository(BatchIdentity identity,BatchStatusSnapshot snapshot):IBatchStatusRepository
    {
        public Task<BatchIdentity?> FindIdentityAsync(Guid id,CancellationToken ct)=>Task.FromResult<BatchIdentity?>(id==identity.BatchId?identity:null);
        public Task<BatchStatusSnapshot?> ReadAsync(BatchIdentity requested,CancellationToken ct)=>Task.FromResult<BatchStatusSnapshot?>(requested==identity?snapshot:null);
    }
    [PostgresFact]
    public async Task Persisted_snapshot_pins_result_review_assets_and_rechecks_auth_and_expiry()
    {
        QuestPDF.Settings.License=LicenseType.Community;
        var cs=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!){Options="-c role=aura_analysis_app"};
        await using var db=NpgsqlDataSource.Create(cs.ConnectionString);
        async Task Write(string sql,params object[] values)
        {await using var cmd=db.CreateCommand(sql);foreach(var v in values)cmd.Parameters.AddWithValue(v);await cmd.ExecuteNonQueryAsync();}
        var actor=Guid.NewGuid();var patient=Guid.NewGuid();var batch=Guid.NewGuid();var image=Guid.NewGuid();var file=Guid.NewGuid();var overlay=Guid.NewGuid();
        var result1=Guid.NewGuid();var result2=Guid.NewGuid();var review1=Guid.NewGuid();var review2=Guid.NewGuid();var job1=Guid.NewGuid();var job2=Guid.NewGuid();
        var tenant=FakeAccess.Tenant;
        await Write("INSERT INTO analysis.batches(id,tenant_id,owner_id,requester_id,patient_id,total_images) VALUES($1,$2,$3,$3,$4,1)",batch,tenant,actor,patient);
        await Write("""
            INSERT INTO analysis.analysis_requests(id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,request_fingerprint,request_schema_version,accepted_response,correlation_id)
            VALUES($1,$2,$3,$3,$4,$5,$6,'0.6','{}','report-regression')
            """,batch,tenant,actor,patient,Guid.NewGuid(),new string('a',64));
        await Write("""
            INSERT INTO analysis.images(id,tenant_id,analysis_request_id,batch_id,file_id,modality,eye_side,object_key,mime_type,size_bytes,width,height,sha256,processing_status)
            VALUES($1,$2,$3,$3,$4,'FUNDUS','left','report/original/'||$1::text,'image/png',100,64,64,$5,'COMPLETED')
            """,image,tenant,batch,file,new string('b',64));
        foreach(var (job,version,result) in new[]{(job1,1,result1),(job2,2,result2)})
        {
            await Write("INSERT INTO analysis.jobs(id,tenant_id,image_id,message_id,attempt_no,status,correlation_id) VALUES($1,$2,$3,$4,$5,'SUCCEEDED','report-regression')",job,tenant,image,Guid.NewGuid(),version);
            await Write("""
                INSERT INTO analysis.analysis_results(id,tenant_id,image_id,job_id,version,result_schema_version,risk_assessment,metrics,is_mock,model_version,config_version,threshold_version,processing_time_ms,limitations)
                VALUES($1,$2,$3,$4,$5,'1.3','{}','{}',false,'fixture-model','fixture-config','fixture-threshold',1,'Research fixture')
                """,result,tenant,image,job,version);
        }
        foreach(var (review,result,version,risk) in new[]{(review1,result1,1,"LOW"),(review2,result2,2,"HIGH")})
            await Write("""
                INSERT INTO analysis.doctor_reviews(id,tenant_id,image_id,analysis_result_id,doctor_id,version,base_version,is_approved,corrected_risk_level,reason,doctor_notes)
                VALUES($1,$2,$3,$4,$5,$6,$7,false,$8,'Correction fixture',$9)
                """,review,tenant,image,result,Guid.NewGuid(),version,version-1,risk,$"review-v{version}");
        var access=new FakeAccess();var storage=new FakeStorage();var imageBytes=TestImages.Png(64,64);
        storage.Files["aura-originals/report/original"]=imageBytes;storage.Files["aura-annotations/report/overlay"]=imageBytes;
        var imageHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes)).ToLowerInvariant();
        var files=new FakeFileRepo();files.Records.Add(new(image,file,tenant,patient,actor,StoredFileKind.Original,"report/original"){Sha256=imageHash});
        files.Records.Add(new(image,overlay,tenant,patient,actor,StoredFileKind.Annotation,"report/overlay"){Sha256=imageHash});
        var resultDetail=new AnalysisResultDetail(true,"MODERATE",.5,.8,false,"fixture-model","fixture-config","fixture-threshold","Research fixture",1,
            new(.1,1.2,null,null,null),[],[new(overlay,"overlay")],[],1,"No clinical validation"){ResultId=result1};
        var detail=new AnalysisDetailResponse(image,batch,patient,"COMPLETED","REVIEWED",DateTime.UtcNow,DateTime.UtcNow,actor,actor,new(file),null,resultDetail,null,ReviewVersion:2);
        var analyses=new AnalysisQueryService(new Query(detail,new(image,batch,tenant,patient,actor)),access);
        var clock=new Clock();var reports=new AnalysisReportService(analyses,new(new PostgresBatchStatusRepository(db),access),files,storage,new(),new(),db,clock);
        var exported=await reports.CreateAnalysisPdfAsync(actor,null,image,"report-regression",default);
        Assert.StartsWith("/api/v1/reports/",exported.DownloadUrl);Assert.Null(storage.LastReadAccess);
        await using(var read=db.CreateCommand("SELECT snapshot::text,snapshot_sha256 FROM analysis.report_snapshots WHERE id=$1"))
        {
            read.Parameters.AddWithValue(exported.ReportId);await using var reader=await read.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
            using var snapshot=JsonDocument.Parse(reader.GetString(0));var root=snapshot.RootElement;
            Assert.Equal(reader.GetString(1),ReportSnapshotDigest.Compute(reader.GetString(0)));
            Assert.Equal(image,root.GetProperty("analysisId").GetGuid());
            Assert.Equal(patient,root.GetProperty("patientId").GetGuid());
            Assert.Equal(result1,root.GetProperty("result").GetProperty("resultId").GetGuid());
            Assert.Equal(review1,root.GetProperty("review").GetProperty("reviewId").GetGuid());
            Assert.Equal("LOW",root.GetProperty("review").GetProperty("correctedRiskLevel").GetString());
            Assert.Equal(64,root.GetProperty("originalSha256").GetString()!.Length);Assert.Equal(64,reader.GetString(1).Length);
        }
        var download=await reports.ReadDownloadContentAsync(actor,null,exported.ReportId,null,default);
        Assert.Equal("application/pdf",download.ContentType);
        await using(var stream=download.Stream)
        using(var bytes=new MemoryStream())
        {
            await stream.CopyToAsync(bytes);
            var pdf=Encoding.Latin1.GetString(bytes.ToArray());
            Assert.StartsWith("%PDF-",pdf);
            Assert.Contains("startxref",pdf);
            Assert.EndsWith("%%EOF",pdf.TrimEnd());
        }
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>
            reports.ReadDownloadContentAsync(Guid.NewGuid(),null,exported.ReportId,null,default))).Status);

        var batchIdentity=new BatchIdentity(batch,tenant,patient,actor);
        var batchSnapshot=new BatchStatusSnapshot(batch,DateTime.SpecifyKind(DateTime.UtcNow,DateTimeKind.Utc),
            [new(image,"case.png","COMPLETED","REVIEWED",new(job2,"SUCCEEDED",1,2),false,null,"MODERATE"),
             new(Guid.NewGuid(),"failed.png","FAILED","NOT_REQUIRED",new(Guid.NewGuid(),"DEAD_LETTER",0,1),false,"ERR_AI_FAILED",null)]);
        var batchStatus=new BatchStatusService(new FixedBatchRepository(batchIdentity,batchSnapshot),access);
        var batchReports=new AnalysisReportService(analyses,batchStatus,files,storage,new(),new(),db,clock);
        var zipExport=await batchReports.CreateBatchReportAsync(actor,null,batch,"zip","zip-manifest-regression",default);
        var zipContent=await batchReports.ReadDownloadContentAsync(actor,null,zipExport.ReportId,"zip-manifest-regression",default);
        Assert.Equal("application/zip",zipContent.ContentType);
        await using(var zipStream=zipContent.Stream)
        using(var archive=new System.IO.Compression.ZipArchive(zipStream,System.IO.Compression.ZipArchiveMode.Read))
        {
            var manifestEntry=Assert.Single(archive.Entries,x=>x.FullName=="manifest.json");
            Assert.Contains(archive.Entries,x=>x.FullName=="summary.csv");
            var pdfEntry=Assert.Single(archive.Entries,x=>x.FullName==$"case-{image:D}.pdf");
            using var manifestStream=manifestEntry.Open();using var manifest=await JsonDocument.ParseAsync(manifestStream);
            var manifestItems=manifest.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2,manifestItems.Length);
            var manifestItem=manifestItems[0];
            Assert.Equal(batch,manifest.RootElement.GetProperty("batchId").GetGuid());
            Assert.Equal(zipExport.ReportId,manifest.RootElement.GetProperty("reportId").GetGuid());
            Assert.Equal(image,manifestItem.GetProperty("analysisId").GetGuid());
            Assert.Equal(result1,manifestItem.GetProperty("resultId").GetGuid());
            Assert.Equal(1,manifestItem.GetProperty("resultVersion").GetInt32());
            Assert.Equal(review1,manifestItem.GetProperty("reviewId").GetGuid());
            Assert.Equal(1,manifestItem.GetProperty("reviewVersion").GetInt32());
            var itemReportId=manifestItem.GetProperty("reportId").GetGuid();
            var pdfDigest=manifestItem.GetProperty("pdfSha256").GetString();
            await using var pdfStream=pdfEntry.Open();using var pdfBytes=new MemoryStream();await pdfStream.CopyToAsync(pdfBytes);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdfBytes.ToArray())).ToLowerInvariant(),pdfDigest);
            await using var manifestSnapshot=db.CreateCommand("SELECT snapshot_sha256 FROM analysis.report_snapshots WHERE id=$1");
            manifestSnapshot.Parameters.AddWithValue(itemReportId);
            Assert.Equal((string?)await manifestSnapshot.ExecuteScalarAsync(),manifestItem.GetProperty("snapshotSha256").GetString());
            Assert.Equal("FAILED",manifestItems[1].GetProperty("processingStatus").GetString());
            Assert.Equal("ERR_AI_FAILED",manifestItems[1].GetProperty("errorCode").GetString());
            Assert.Equal(JsonValueKind.Null,manifestItems[1].GetProperty("resultId").ValueKind);
            Assert.Equal(JsonValueKind.Null,manifestItems[1].GetProperty("pdfEntry").ValueKind);
        }
        access.Allowed=false;
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>reports.ReadDownloadAsync(actor,null,exported.ReportId,null,default))).Status);
        access.Allowed=true;clock.Now=clock.Now.AddMinutes(16);
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>reports.ReadDownloadAsync(actor,null,exported.ReportId,null,default))).Status);
        var denied=await Assert.ThrowsAsync<PostgresException>(()=>Write("UPDATE analysis.report_snapshots SET snapshot='{}' WHERE id=$1",exported.ReportId));
        Assert.Equal("42501",denied.SqlState);
        await Write("INSERT INTO analysis.batch_completion_events(batch_id,event_id) VALUES($1,$1)",batch);
        var contexts=new InternalResolversService(new PostgresInternalResolversRepository(db));
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>contexts.ResolveNotificationContextAsync(new(Guid.NewGuid(),image,tenant),default))).Status);
        var notification=await contexts.ResolveNotificationContextAsync(new(batch,batch,tenant),default);
        Assert.Equal(actor,Assert.Single(notification.RecipientAccountIds));
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>contexts.ResolveNotificationContextAsync(new(Guid.NewGuid(),batch,tenant),default))).Status);
    }
}
