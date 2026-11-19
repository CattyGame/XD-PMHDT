using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Upload;
using QuestPDF.Infrastructure;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class MedicalReportPdfRendererTests
{
    public MedicalReportPdfRendererTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public void Clinic_QR_keeps_context_without_embedding_authentication_tokens()
    {
        var image = Guid.NewGuid(); var clinic = Guid.NewGuid();
        var options = new AnalysisReportOptions { PublicApiBaseUrl = "https://aura.example" };
        Assert.Equal($"https://aura.example/api/v1/analyses/{image:D}?clinicContextId={clinic:D}", options.AnalysisUrl(image,clinic));
        Assert.Equal($"https://aura.example/api/v1/analyses/{image:D}", options.AnalysisUrl(image,null));
    }

    [Theory]
    [InlineData("https://user:password@aura.example")]
    [InlineData("https://aura.example?token=secret")]
    [InlineData("https://aura.example#secret")]
    [InlineData("http://aura.example")]
    public void QR_rejects_public_origins_with_credentials_or_insecure_transport(string origin)
        => Assert.Throws<InvalidOperationException>(() => new AnalysisReportOptions { PublicApiBaseUrl=origin }.AnalysisUrl(Guid.NewGuid(),null));

    [Fact]
    public void Batch_100_long_statuses_paginate_without_fixed_position_text()
    {
        var status = BatchStatusProjection.Create(new(Guid.NewGuid(),DateTime.UtcNow,
            Enumerable.Range(0,100).Select(_ => new BatchStatusRow(Guid.NewGuid(),"synthetic.png","COMPLETED","CORRECTION_REQUIRED",null,false,null,"MODERATE")).ToArray()));
        var pdf = AnalysisReportService.RenderBatchPdf(status,Guid.NewGuid(),DateTimeOffset.UtcNow,new Dictionary<Guid,DoctorReportReview?>());
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf,0,8));
        Assert.True(pdf.Length < AnalysisReportService.MaximumBatchExportBytes);
        SaveArtifact("batch100.pdf",pdf);
    }

    [Fact]
    public void Renders_a4_case_report_under_1_2_mb()
    {
        var analysisId = Guid.NewGuid();
        var report = new MedicalReportPdfData(Guid.NewGuid(), analysisId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, DateTime.UtcNow, "PENDING", "retina-model-7", "HIGH", false, null,
            "Review requested", "Needs second look", 0.42, 1.12, 0.73, "Research use only.",
            $"https://aura.example/api/v1/analyses/{analysisId:D}", TestImages.Png(128, 128), TestImages.Png(128, 128));

        var pdf = MedicalReportPdfRenderer.Render(report);
        var header = Encoding.ASCII.GetString(pdf, 0, 8);
        Assert.StartsWith("%PDF-", header);
        Assert.True(pdf.Length < MedicalReportPdfRenderer.MaximumPdfBytes);
    }

    [Fact]
    public void Vietnamese_notes_and_high_resolution_images_render_with_provenance()
    {
        var image = Guid.NewGuid();
        var data = new MedicalReportPdfData(Guid.NewGuid(),image,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
            Guid.NewGuid(),DateTime.UtcNow,"REVIEWED","research-fixture-v1","LOW",true,"NO_DR",
            "Bác sĩ đã kiểm tra ảnh đáy mắt. Mạch máu võng mạc cần được theo dõi định kỳ. Không dùng chỉ số AI thay thế chẩn đoán lâm sàng.",
            null,.42,1.12,null,"Dữ liệu tổng hợp dùng kiểm thử, không phải hồ sơ bệnh nhân.",
            $"https://aura.example/api/v1/analyses/{image:D}",TestImages.Png(2048,2048),TestImages.Png(2048,2048))
            {ResultId=Guid.NewGuid(),ResultVersion=1,ReviewId=Guid.NewGuid(),ReviewVersion=1,ConfigVersion="fixture-1",ThresholdVersion="fixture-1",SnapshotDigest=new string('a',64)};
        var pdf=MedicalReportPdfRenderer.Render(data);
        Assert.True(pdf.Length < MedicalReportPdfRenderer.MaximumPdfBytes);
        SaveArtifact("vietnamese-case.pdf",pdf);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Integrated_research_report_keeps_null_measurements_and_distinct_model_sources(bool measured)
    {
        var id = Guid.NewGuid();
        var research = JsonSerializer.SerializeToElement(new {
            avr = new { measurement_available=measured, avr=measured?(double?)0.67:null,
                crae_px=measured?(double?)13.4:null,crve_px=measured?(double?)20:null,
                unavailable_reason=measured?null:"INSUFFICIENT_SIX_VESSEL_SETS",model_version="avr-v8-test" },
            dr = new { probability_dr=0.61,model_version="resnet34-test" }
        });
        var data = new MedicalReportPdfData(Guid.NewGuid(),id,Guid.NewGuid(),Guid.NewGuid(),null,null,
            DateTime.UtcNow,"PENDING","frangi-v0.2",null,false,null,null,null,.42,1.12,
            measured?0.67:null,"Synthetic software test, not a patient record.",
            $"https://aura.example/api/v1/analyses/{id:D}",TestImages.Png(128,128),TestImages.Png(128,128))
            { Research=research };
        var pdf = MedicalReportPdfRenderer.Render(data);
        Assert.StartsWith("%PDF-",Encoding.ASCII.GetString(pdf,0,8));
        Assert.True(pdf.Length < MedicalReportPdfRenderer.MaximumPdfBytes);
        SaveArtifact(measured?"integrated-avr.pdf":"integrated-null-avr.pdf",pdf);
    }

    [Fact]
    public void Integrated_research_report_renders_artifact_hashes_when_present()
    {
        var id = Guid.NewGuid();
        var research = JsonSerializer.SerializeToElement(new
        {
            avr = new
            {
                measurement_available = true,
                avr = 0.67,
                crae_px = 13.4,
                crve_px = 20.0,
                model_version = "avr-v8-rrwnet",
                model_sha256 = new string('a', 64)
            },
            dr = new
            {
                probability_dr = 0.61,
                model_version = "resnet34-hrf-v1",
                checkpoint_sha256 = new string('b', 64),
                gradcam_method = "GRAD_CAM",
                gradcam_geometry = "IMAGENET_CENTER_CROP_224"
            }
        });
        var data = new MedicalReportPdfData(Guid.NewGuid(), id, Guid.NewGuid(), Guid.NewGuid(), null, null,
            DateTime.UtcNow, "PENDING", "frangi-v0.2", null, false, null, null, null, .42, 1.12,
            0.67, "Synthetic software test, not a patient record.",
            $"https://aura.example/api/v1/analyses/{id:D}", TestImages.Png(128, 128), TestImages.Png(128, 128))
            { Research = research };

        var pdf = MedicalReportPdfRenderer.Render(data);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 8));
        Assert.True(pdf.Length < MedicalReportPdfRenderer.MaximumPdfBytes);
    }

    private static void SaveArtifact(string name, byte[] bytes)
    {
        if (Environment.GetEnvironmentVariable("AURA_M2_ARTIFACTS") is not {Length:>0} root) return;
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root,name),bytes);
    }
}
