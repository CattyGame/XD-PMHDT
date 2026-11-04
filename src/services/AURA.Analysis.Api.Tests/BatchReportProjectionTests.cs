using System.Text;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class BatchReportProjectionTests
{
    [Fact]
    public void Export_keeps_doctor_correction_separate_from_AI_and_counts_only_available_values()
    {
        var image=Guid.NewGuid();var reviewId=Guid.NewGuid();
        var snapshot=BatchStatusProjection.Create(new(Guid.NewGuid(),DateTime.UtcNow,
            [new(image,"case.png","COMPLETED","CORRECTION_REQUIRED",null,false,null,"LOW"),
             new(Guid.NewGuid(),"unavailable.png","COMPLETED","PENDING",null,false,null,null)]));
        var reviews=new Dictionary<Guid,DoctorReportReview?>{[image]=new(Guid.NewGuid(),false,"HIGH",null,"Correction","Review",DateTime.UtcNow){ReviewId=reviewId,ReviewVersion=3}};
        var csv=Encoding.UTF8.GetString(AnalysisReportService.RenderBatchCsv(snapshot,DateTimeOffset.UtcNow,reviews));
        Assert.Contains("aiRiskLevel,doctorConclusion,reviewId,reviewVersion",csv);
        Assert.Contains($"\"LOW\",\"HIGH\",\"{reviewId:D}\",\"3\"",csv);
        Assert.Contains("\"0.00%\",\"1\",\"100.00%\",\"1\"",csv);
        Assert.Contains("\"UNAVAILABLE\",\"NOT_REVIEWED\"",csv);
    }
    [Fact]
    public void No_available_measurements_is_NA_instead_of_a_claimed_zero_percent()
    {
        var snapshot=BatchStatusProjection.Create(new(Guid.NewGuid(),DateTime.UtcNow,
            [new(Guid.NewGuid(),null,"COMPLETED","PENDING",null,false,null,null)]));
        var csv=Encoding.UTF8.GetString(AnalysisReportService.RenderBatchCsv(snapshot,DateTimeOffset.UtcNow,new Dictionary<Guid,DoctorReportReview?>()));
        Assert.Contains("\"N/A\",\"0\",\"N/A\",\"0\"",csv);
        Assert.DoesNotContain("0.00%",csv);
    }
    [Fact]
    public void Snapshot_digest_survives_jsonb_property_order_and_numeric_notation()
        =>Assert.Equal(ReportSnapshotDigest.Compute("{\"b\":1e-3,\"a\":[2,3]}"),
            ReportSnapshotDigest.Compute("{\"a\":[2,3],\"b\":0.001}"));
}
