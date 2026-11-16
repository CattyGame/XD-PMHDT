using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class PatientHistoryTests
{
    private sealed class Repository : IPatientHistoryRepository
    {
        public int Reads; public Guid Tenant, Clinic, Patient; public int Limit;
        public IReadOnlyList<PatientHistoryItem> Items = [];
        public Task<IReadOnlyList<PatientHistoryItem>> ListAsync(Guid tenant,Guid clinic,Guid patient,int limit,Guid? before,CancellationToken ct)
        { Reads++; Tenant=tenant; Clinic=clinic; Patient=patient; Limit=limit; return Task.FromResult(Items); }
    }
    [Fact]
    public async Task Uses_authoritative_tenant_and_patient_scope_then_returns_a_bounded_cursor_page()
    {
        var actor=Guid.NewGuid();var patient=Guid.NewGuid();var access=new FakeAccess();var repo=new Repository();
        repo.Items=Enumerable.Range(0,3).Select(_=>new PatientHistoryItem(Guid.NewGuid(),Guid.NewGuid(),"FAILED","PENDING",DateTime.UtcNow)).ToArray();
        var response=await new PatientHistoryService(repo,access).GetAsync(actor,actor,patient,2,null,"history-test",default);
        Assert.Equal(FakeAccess.Tenant,repo.Tenant);Assert.Equal(actor,repo.Clinic);Assert.Equal(patient,repo.Patient);
        Assert.Equal(3,repo.Limit);Assert.Equal(2,response.Items.Count);Assert.Equal(repo.Items[1].AnalysisId,response.NextBeforeId);
        Assert.Equal("ANALYSIS_READ",access.LastRequest!.Action);Assert.Null(access.LastRequest.AnalysisId);
        Assert.Equal(patient,response.PatientId);
    }
    [Fact]
    public async Task Revocation_or_wrong_owner_denies_before_reading_history()
    {
        var actor=Guid.NewGuid();var patient=Guid.NewGuid();var access=new FakeAccess();var repo=new Repository();
        var service=new PatientHistoryService(repo,access);
        await service.GetAsync(actor,actor,patient,20,null,null,default);
        access.Allowed=false;
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>service.GetAsync(actor,actor,patient,20,null,null,default))).Status);
        access.Allowed=true;
        Assert.Equal(404,(await Assert.ThrowsAsync<UploadFailure>(()=>service.GetAsync(actor,Guid.NewGuid(),patient,20,null,null,default))).Status);
        Assert.Equal(1,repo.Reads);
    }
    [Theory][InlineData(0)][InlineData(101)]
    public async Task Invalid_page_size_never_calls_authority_or_database(int limit)
    {
        var repo=new Repository();var access=new FakeAccess();var actor=Guid.NewGuid();
        Assert.Equal(400,(await Assert.ThrowsAsync<UploadFailure>(()=>new PatientHistoryService(repo,access).GetAsync(actor,actor,Guid.NewGuid(),limit,null,null,default))).Status);
        Assert.Equal(0,repo.Reads);Assert.Equal(0,access.Calls);
    }
}
