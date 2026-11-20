using System.Text.Json;
using AURA.Analysis.Api.Upload;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Exports;
using AURA.Analysis.Api.Alerts;
using AURA.Analysis.Api.Feedback;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AnalysisContractResponseTests
{
    [Fact]
    public void Serialize_runtime_DTOs_for_required_response_schema_gate()
    {
        var now=DateTime.UtcNow;var id=Guid.NewGuid();var clinic=Guid.NewGuid();var patient=Guid.NewGuid();
        var risk=new AnalysisResultDetail(true,"LOW",.1,.9,false,"fixture-model","fixture-config","fixture-threshold","Research fixture",1,
            new(.3,1.1,null,null,null),[],[],[],1,"Synthetic fixture"){ResultId=Guid.NewGuid()};
        var item=new UploadInventoryItem(Guid.NewGuid(),patient,"fixture.png",new string('a',64),"FUNDUS","left","ACCEPTED",null,id)
            {Length=100,ContentType="image/png",ChildBatchId=Guid.NewGuid(),Receipt=new(id,"fixture.png","QUEUED",null)};
        var inventory=new UploadInventory(id,now,now.AddHours(1),"FINALIZED",$"/api/v1/batches/{id:D}",[item])
            {RequesterId=Guid.NewGuid(),TenantId=Guid.NewGuid(),OwnerId=clinic,ClinicId=clinic};
        var status=BatchStatusProjection.Create(new(id,now,[new(id,"fixture.png","COMPLETED","REVIEWED",null,false,null,"LOW")]))
            with{TotalDeclared=1,AdmissionRejected=0,UploadPending=0};
        var samples=new List<object>();
        void Add(string name,string schema,object value)=>samples.Add(new{name,schema,value=JsonSerializer.SerializeToElement(value,UploadJson.Options)});
        Add("completed analysis","Analysis",new AnalysisDetailResponse(id,id,patient,"COMPLETED","REVIEWED",now,now,clinic,Guid.NewGuid(),new(Guid.NewGuid()),null,risk,null,ReviewVersion:1));
        Add("available risk","AnalysisResult",risk);
        Add("unavailable risk","AnalysisResult",risk with{RiskAvailable=false,RiskLevel=null,RiskScore=null,ConfidenceScore=null});
        Add("terminal batch","Batch",status);
        Add("approved review","DoctorReview",new DoctorReviewDto(Guid.NewGuid(),id,1,true,null,"Bác sĩ đã duyệt",Guid.NewGuid(),now,null,1,"NO_DR"));
        Add("private report","ReportDownload",new ReportDownloadResponse(Guid.NewGuid(),"pdf","/api/v1/reports/fixture/content",now.AddMinutes(15)));
        Add("inventory","UploadInventory",inventory);
        Add("accepted item","UploadInventoryItem",item);
        Add("clinic analytics summary","AnalyticsSummary",new AnalyticsSummary("UNIQUE_ANALYSIS",now,now.AddDays(1),1,
            new(0,1,0,0,0,0),"LATEST_CLINICIAN_REVIEW",new(0,1,0,0,0),1,0,0,now,
            new("UNAVAILABLE",[],"BILLING_API_UNAVAILABLE")));
        Add("patient analytics trends","Trends",new PatientAnalyticsTrends(patient,
            [new(Guid.NewGuid(),now,"model-v1","config-v1","threshold-v1","ratio",1,.67m,null,true,false,null,null)],null));
        Add("protected export receipt","ExportReceipt",new ExportReceipt(Guid.NewGuid(),"COMPLETED","/api/v1/reports/11111111-1111-4111-8111-111111111111?clinicContextId=22222222-2222-4222-8222-222222222222",now.AddDays(7)));
        var alert=new AlertResponse(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),0,"scrum385-v1","RESEARCH_NOTICE","OPEN",1);
        Add("alert page","AlertsPage",new AlertsPage([alert],null));
        Add("feedback receipt","FeedbackReceipt",new FeedbackReceipt(Guid.NewGuid(),"ACCEPTED",0));
        if(Environment.GetEnvironmentVariable("AURA_M2_ARTIFACTS") is {Length:>0} directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"analysis-responses.json"),JsonSerializer.Serialize(samples));
        }
        Assert.Equal(13,samples.Count);
    }
}
