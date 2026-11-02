using System.Text;
using AURA.Analysis.Api.Upload;
using QuestPDF.Infrastructure;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class MedicalReportPdfRendererTests
{
    public MedicalReportPdfRendererTests() => QuestPDF.Settings.License = LicenseType.Community;

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
}
