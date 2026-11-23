using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Upload;
using Npgsql;
using QuestPDF.Infrastructure;
using Xunit;
using AURA.Common.Audit;
using AURA.Analysis.Api.Auth;
using AURA.Analysis.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace AURA.Analysis.Api.Tests;

public sealed class ReportSnapshotPostgresTests
{
    private sealed class ReportIdentity(Guid actor):IAnalysisUserIdentityResolver
    {public Task<AnalysisUserContext> ResolveAsync(HttpRequest request,CancellationToken ct)=>Task.FromResult(new AnalysisUserContext(actor,["PATIENT"]));}
    private sealed class ReportAuditWriter:IAuditWriter
    {
        public readonly List<AuditEvent> Events=[];public bool Down;
        public Task AppendAsync(AuditEvent e,CancellationToken ct)
        {if(Down)throw new IOException("Test-only audit outage");Events.Add(e);return Task.CompletedTask;}
    }
    [PostgresFact]
    public async Task Batch_notification_does_not_require_a_legacy_analysis_request()
    {
        await using var db=NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("AURA_SCRUM345_TEST_CONNECTION_STRING")!);
        var batch=Guid.NewGuid();var tenant=Guid.NewGuid();var actor=Guid.NewGuid();var clinic=Guid.NewGuid();
        await using(var command=db.CreateCommand("INSERT INTO analysis.batches(id,tenant_id,owner_id,requester_id,patient_id,total_images,status,clinic_id) VALUES($1,$2,$3,$3,$4,1,'COMPLETED',$5)"))
        {
            command.Parameters.AddWithValue(batch);command.Parameters.AddWithValue(tenant);
            command.Parameters.AddWithValue(actor);command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(clinic);
            await command.ExecuteNonQueryAsync();
        }
        await using(var command=db.CreateCommand("INSERT INTO analysis.batch_completion_events(batch_id,event_id) VALUES($1,$1)"))
        {command.Parameters.AddWithValue(batch);await command.ExecuteNonQueryAsync();}
        var response=await new PostgresInternalResolversRepository(db).GetBatchNotificationAsync(new(batch,batch,tenant),default);
        Assert.NotNull(response);Assert.Equal(actor,Assert.Single(response.RecipientAccountIds));
        Assert.Equal(clinic,response.ClinicContextId);
    }
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
            new(.1,1.2,null,null,null),[],[new(overlay,"overlay")],[],1,"No clinical validation")
            {ResultId=result1,Research=JsonSerializer.SerializeToElement(new {
                avr=new {measurement_available=false,avr=(double?)null,crae_px=(double?)null,crve_px=(double?)null,
                    unavailable_reason="INSUFFICIENT_SIX_VESSEL_SETS",model_version="avr-v8-test"},
                dr=new {probability_dr=.61,model_version="resnet34-test"}
            })};
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
            var research=root.GetProperty("result").GetProperty("research");
            Assert.Equal(.61,research.GetProperty("dr").GetProperty("probability_dr").GetDouble());
            Assert.Equal(JsonValueKind.Null,research.GetProperty("avr").GetProperty("avr").ValueKind);
            Assert.Equal(review1,root.GetProperty("review").GetProperty("reviewId").GetGuid());
            Assert.Equal("LOW",root.GetProperty("review").GetProperty("correctedRiskLevel").GetString());
            Assert.Equal(64,root.GetProperty("originalSha256").GetString()!.Length);Assert.Equal(64,reader.GetString(1).Length);
        }
        var secondVersionDetail=detail with
        {
            Result=resultDetail with
            {
                ResultId=result2,Version=2,ModelVersion="fixture-model-v2",
                ConfigVersion="fixture-config-v2",ThresholdVersion="fixture-threshold-v2"
            },
            // Simulates a stale pre-v2 review version. The v1 review belongs to result1 and
            // the v2 review is newer, so neither may be attached to this AI v2 snapshot.
            ReviewVersion=1
        };
        var secondVersionReports=new AnalysisReportService(
            new AnalysisQueryService(new Query(secondVersionDetail,new(image,batch,tenant,patient,actor)),access),
            new(new PostgresBatchStatusRepository(db),access),files,storage,new(),new(),db,clock);
        var v2Export=await secondVersionReports.CreateAnalysisPdfAsync(actor,null,image,"report-v2-regression",default);
        await using(var readV2=db.CreateCommand("SELECT snapshot::text,snapshot_sha256 FROM analysis.report_snapshots WHERE id=$1"))
        {
            readV2.Parameters.AddWithValue(v2Export.ReportId);await using var reader=await readV2.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
            var json=reader.GetString(0);using var snapshot=JsonDocument.Parse(json);var root=snapshot.RootElement;
            Assert.Equal(reader.GetString(1),ReportSnapshotDigest.Compute(json));
            Assert.Equal(result2,root.GetProperty("result").GetProperty("resultId").GetGuid());
            Assert.Equal(2,root.GetProperty("result").GetProperty("version").GetInt32());
            Assert.Equal("fixture-model-v2",root.GetProperty("result").GetProperty("modelVersion").GetString());
            Assert.Equal("fixture-config-v2",root.GetProperty("result").GetProperty("configVersion").GetString());
            Assert.True(!root.TryGetProperty("review",out var review) || review.ValueKind==JsonValueKind.Null);
        }
        var httpBuilder=WebApplication.CreateBuilder();httpBuilder.WebHost.UseTestServer();httpBuilder.Logging.ClearProviders();
        var auditWriter=new ReportAuditWriter();
        httpBuilder.Services.AddSingleton(reports);httpBuilder.Services.AddSingleton<IAuditWriter>(auditWriter);
        httpBuilder.Services.AddSingleton<IAnalysisUserIdentityResolver>(new ReportIdentity(actor));
        await using(var httpApp=httpBuilder.Build())
        {
            httpApp.MapAnalysisEndpoints();await httpApp.StartAsync();using var client=httpApp.GetTestClient();
            using(var response=await client.GetAsync($"/api/v1/reports/{exported.ReportId:D}/content"))
            {
                Assert.Equal(HttpStatusCode.OK,response.StatusCode);
                Assert.Equal("application/pdf",response.Content.Headers.ContentType!.MediaType);
                var audit=Assert.Single(auditWriter.Events);Assert.Equal(AuditAction.ReportDownload,audit.Action);
                Assert.Equal(exported.ReportId.ToString("D"),audit.ResourceId);Assert.Equal(actor,audit.ActorAccountId);
                Assert.Equal(tenant,audit.TenantId);Assert.Equal(1,audit.ResourceVersion);
            }
            auditWriter.Down=true;
            using var blocked=await client.GetAsync($"/api/v1/reports/{exported.ReportId:D}/content");
            Assert.Equal(HttpStatusCode.ServiceUnavailable,blocked.StatusCode);
            Assert.DoesNotContain("%PDF-",await blocked.Content.ReadAsStringAsync());
            Assert.Single(auditWriter.Events);
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
        var otherActor=await Assert.ThrowsAsync<UploadFailure>(()=>reports.ReadDownloadAsync(Guid.NewGuid(),null,exported.ReportId,null,default));
        Assert.Equal(404,otherActor.Status);
        var otherClinic=await Assert.ThrowsAsync<UploadFailure>(()=>reports.ReadDownloadAsync(actor,Guid.NewGuid(),exported.ReportId,null,default));
        Assert.Equal(404,otherClinic.Status);

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
        storage.ReadOverride=(container,key)=>container=="aura-reports"
            ?new MemoryStream(new byte[MedicalReportPdfRenderer.MaximumPdfBytes])
            :new MemoryStream(storage.Files[container+"/"+key]);
        var oversized=await Assert.ThrowsAsync<UploadFailure>(()=>batchReports.CreateBatchReportAsync(actor,null,batch,"zip","oversized-archive-fixture",default));
        Assert.Equal(413,oversized.Status);Assert.Equal("ERR_REPORT_TOO_LARGE",oversized.Code);
        storage.ReadOverride=null;
        var beforeExpiry=clock.Now;
        clock.Now=new DateTimeOffset(DateTime.SpecifyKind(exported.ExpiresAt,DateTimeKind.Utc));
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>reports.ReadDownloadAsync(actor,null,exported.ReportId,null,default))).Status);
        clock.Now=beforeExpiry;
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
        var clinicContext=Guid.NewGuid();
        await Write("UPDATE analysis.analysis_requests SET clinic_context_id=$1 WHERE id=$2",clinicContext,batch);
        Assert.Equal(clinicContext,(await contexts.ResolveNotificationContextAsync(new(batch,batch,tenant),default)).ClinicContextId);
        Assert.Equal(actor,Assert.Single(notification.RecipientAccountIds));
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>contexts.ResolveNotificationContextAsync(new(Guid.NewGuid(),batch,tenant),default))).Status);
    }
}
