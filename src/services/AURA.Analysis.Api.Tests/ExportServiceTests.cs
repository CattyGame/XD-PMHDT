using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Campaign;
using AURA.Analysis.Api.Exports;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class ExportServiceTests
{
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
