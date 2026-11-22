using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Exports;
using AURA.Analysis.Api.Upload;
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

public sealed class ExportServiceTests
{
    [Fact]
    public async Task Export_download_uses_shared_audit_and_fails_closed_after_audit_outage_or_scope_revocation()
    {
        var actor=Guid.NewGuid();var tenant=Guid.NewGuid();var clinic=Guid.NewGuid();var patient=Guid.NewGuid();
        var repository=new ExportRepositoryFake();var authority=new RevocableResearchAuthority(tenant,clinic,[patient]);
        var exports=new ExportService(repository,new CampaignsFake(),new ClinicAuthorityFake(),authority,new StorageFake(),new(),TimeProvider.System);
        var receipt=await exports.CreateResearchExportAsync(actor,new(clinic,null,"research fixture","CSV",false),"audit-export",null,default);
        var audit=new ExportAuditWriter();
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
        builder.Services.AddSingleton(exports);builder.Services.AddSingleton<IAuditWriter>(audit);
        builder.Services.AddSingleton<IAnalysisUserIdentityResolver>(new ExportIdentity(actor));
        await using var app=builder.Build();app.MapAnalysisEndpoints();await app.StartAsync();using var client=app.GetTestClient();
        var url=$"/api/v1/reports/{receipt.ReportId:D}/content?clinicContextId={clinic:D}";
        using(var response=await client.GetAsync(url)) Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var e=Assert.Single(audit.Events);Assert.Equal(AuditAction.ReportDownload,e.Action);
        Assert.Equal(receipt.ReportId.ToString("D"),e.ResourceId);Assert.Equal(tenant,e.TenantId);Assert.Equal(1,e.ResourceVersion);
        audit.Down=true;
        using(var response=await client.GetAsync(url)) Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        Assert.Single(audit.Events);
        audit.Down=false;authority.AuthorizedPatients=[];
        using(var response=await client.GetAsync(url)) Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        Assert.Equal(AuditOutcome.Denied,audit.Events.Last().Outcome);
    }

    private sealed class ExportIdentity(Guid actor):IAnalysisUserIdentityResolver
    { public Task<AnalysisUserContext> ResolveAsync(HttpRequest request,CancellationToken ct)=>Task.FromResult(new AnalysisUserContext(actor,["PATIENT"])); }
    private sealed class ExportAuditWriter:IAuditWriter
    {
        public bool Down;public List<AuditEvent> Events=[];
        public Task AppendAsync(AuditEvent e,CancellationToken ct)
        { if(Down)throw new IOException("Test-only audit outage");Events.Add(e);return Task.CompletedTask; }
    }
    [Fact]
    public async Task Rejects_truncated_500_patient_authority_scope_before_reading_results()
    {
        var repo=new ExportRepositoryFake();var ids=Enumerable.Range(0,500).Select(_=>Guid.NewGuid()).ToArray();
        var service=new ExportService(repo,new CampaignsFake(),new ClinicAuthorityFake(),new ResearchAuthorityFake(ids),
            new StorageFake(),new StorageOptions(),TimeProvider.System);
        var request=new ResearchExportRequest(Guid.NewGuid(),null,"approved study","CSV",false);
        var error=await Assert.ThrowsAsync<UploadFailure>(()=>service.CreateResearchExportAsync(Guid.NewGuid(),request,"key-1",null,CancellationToken.None));
        Assert.Equal(503,error.Status);Assert.Equal(0,repo.ResearchReads);
    }

    [Fact]
    public async Task Rejects_mask_export_before_calling_authority_without_consent_evidence()
    {
        var authority=new ResearchAuthorityFake([]);var repo=new ExportRepositoryFake();
        var service=new ExportService(repo,new CampaignsFake(),new ClinicAuthorityFake(),authority,new StorageFake(),new StorageOptions(),TimeProvider.System);
        var request=new ResearchExportRequest(Guid.NewGuid(),null,"approved study","CSV",true);
        var error=await Assert.ThrowsAsync<UploadFailure>(()=>service.CreateResearchExportAsync(Guid.NewGuid(),request,"key-1",null,CancellationToken.None));
        Assert.Equal(403,error.Status);Assert.Equal(0,authority.Calls);Assert.Equal(0,repo.ResearchReads);
    }

    [Fact]
    public async Task Replays_same_export_receipt_and_rejects_changed_payload_for_same_key()
    {
        var clinic=Guid.NewGuid();var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var repo=new ExportRepositoryFake();
        var authority=new ResearchAuthorityFake([patient],tenant);
        repo.Rows=[new ResearchSourceRow(patient,Guid.NewGuid(),"model-v1","config-v1","threshold-v1",1,"HIGH",true,.7m)];
        var service=new ExportService(repo,new CampaignsFake(),new ClinicAuthorityFake(),authority,new StorageFake(),new StorageOptions(),TimeProvider.System);
        var request=new ResearchExportRequest(clinic,null,"retinal screening study","CSV",false);

        var first=await service.CreateResearchExportAsync(Guid.NewGuid(),request,"same-key",null,CancellationToken.None);
        var replay=await service.CreateResearchExportAsync(repo.Saved!.ActorId,request,"same-key",null,CancellationToken.None);
        Assert.Equal(first.ReportId,replay.ReportId);Assert.Equal(1,repo.ResearchReads);
        var changed=request with{Purpose="different study"};
        var error=await Assert.ThrowsAsync<UploadFailure>(()=>service.CreateResearchExportAsync(repo.Saved.ActorId,changed,"same-key",null,CancellationToken.None));
        Assert.Equal(409,error.Status);Assert.Equal(1,repo.ResearchReads);
    }

    [Fact]
    [Trait("TestKind", "ContractMock")]
    public async Task Research_export_status_is_denied_after_patient_scope_is_revoked()
    {
        var clinic=Guid.NewGuid();var tenant=Guid.NewGuid();var patient=Guid.NewGuid();var actor=Guid.NewGuid();
        var repo=new ExportRepositoryFake();
        repo.Rows=[new ResearchSourceRow(patient,Guid.NewGuid(),"model-v1","config-v1","threshold-v1",1,"HIGH",true,.7m)];
        var authority=new RevocableResearchAuthority(tenant,clinic,[patient]);
        var service=new ExportService(repo,new CampaignsFake(),new ClinicAuthorityFake(),authority,
            new StorageFake(),new StorageOptions(),TimeProvider.System);
        var request=new ResearchExportRequest(clinic,null,"approved study","CSV",false);

        var receipt=await service.CreateResearchExportAsync(actor,request,"scope-revoke",null,CancellationToken.None);
        authority.AuthorizedPatients=[];

        var failure=await Assert.ThrowsAsync<UploadFailure>(()=>service.GetStatusAsync(actor,clinic,receipt.ReportId,null,CancellationToken.None));

        Assert.Equal(403,failure.Status);
    }

    private sealed class ExportRepositoryFake:IExportRepository
    {
        public int ResearchReads{get;private set;}
        public ExportSnapshot? Saved{get;private set;}
        public IReadOnlyList<ResearchSourceRow> Rows{get;set;}=[];
        public Task<ExportSnapshot?> FindByKeyAsync(Guid t,Guid a,string k,string key,CancellationToken ct)=>Task.FromResult<ExportSnapshot?>(Saved is { } s&&s.TenantId==t&&s.ActorId==a&&s.Kind==k&&s.IdempotencyKey==key?s:null);
        public Task<ExportSnapshot?> FindByIdAsync(Guid id,CancellationToken ct)=>Task.FromResult<ExportSnapshot?>(Saved is { } s&&s.Id==id?s:null);
        public Task<IReadOnlyList<Guid>> ReadCampaignPatientScopeAsync(Guid t,Guid c,CancellationToken ct)=>Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<ResearchSourceRow>> ReadResearchRowsAsync(Guid t,Guid c,Guid? campaign,IReadOnlyCollection<Guid> ids,CancellationToken ct){ResearchReads++;return Task.FromResult(Rows);}
        public Task InsertAsync(ExportSnapshot value,CancellationToken ct){Saved=value;return Task.CompletedTask;}
    }
    private sealed class ResearchAuthorityFake(IReadOnlyCollection<Guid> ids,Guid? tenantId=null):IAnalyticsAccessResolver
    {
        public int Calls{get;private set;}
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request,CancellationToken ct){Calls++;return Task.FromResult(new AnalyticsAccessGrant(tenantId??Guid.NewGuid(),request.ClinicId,null,ids,1));}
    }
    private sealed class RevocableResearchAuthority(Guid tenantId,Guid clinicId,IReadOnlyCollection<Guid> initialPatients):IAnalyticsAccessResolver
    {
        public IReadOnlyCollection<Guid> AuthorizedPatients{get;set;}=initialPatients;
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request,CancellationToken ct)=>
            Task.FromResult(new AnalyticsAccessGrant(tenantId,clinicId,null,AuthorizedPatients,1));
    }
    private sealed class ClinicAuthorityFake:IClinicAccessResolver
    {
        public Task<ClinicAccessDecision> ResolveAsync(ClinicAccessRequest request,CancellationToken ct)=>Task.FromResult(new ClinicAccessDecision(true,Guid.NewGuid(),"CLINIC_ADMIN"));
    }
    private sealed class CampaignsFake:ICampaignRepository
    {
        public Task<CampaignResponse> CreateCampaignAsync(Guid t,CreateCampaignRequest r,CancellationToken c)=>throw new NotImplementedException();
        public Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(Guid t,string? s,CancellationToken c)=>throw new NotImplementedException();
        public Task<CampaignResponse> GetCampaignByIdAsync(Guid t,Guid id,CancellationToken c)=>throw new NotImplementedException();
        public Task<CampaignResponse> UpdateCampaignStatusAsync(Guid t,Guid id,string s,CancellationToken c)=>throw new NotImplementedException();
        public Task LinkBatchAsync(Guid t,Guid id,Guid b,CancellationToken c)=>throw new NotImplementedException();
        public Task<CampaignSummaryResponse> GetCampaignSummaryAsync(Guid t,Guid id,CancellationToken c)=>throw new NotImplementedException();
        public Task<string> ExportCampaignReportCsvAsync(Guid t,Guid id,CancellationToken c)=>throw new NotImplementedException();
    }
    private sealed class StorageFake:IPrivateFileStorage
    {
        public Task PutAsync(string c,string k,Stream s,string t,CancellationToken ct)=>Task.CompletedTask;
        public Task<Stream> OpenReadAsync(string c,string k,CancellationToken ct)=>Task.FromResult<Stream>(Stream.Null);
        public Task DeleteAsync(string c,string k,CancellationToken ct)=>Task.CompletedTask;
        public Task<FileReadAccess> CreateReadAccessAsync(string c,string k,TimeSpan ttl,CancellationToken ct)=>throw new NotImplementedException();
    }
}
