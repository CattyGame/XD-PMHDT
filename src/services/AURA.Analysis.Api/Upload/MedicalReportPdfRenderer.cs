using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;
using SkiaSharp;

namespace AURA.Analysis.Api.Upload;

public sealed record MedicalReportPdfData(
    Guid ReportId,
    Guid AnalysisId,
    Guid BatchId,
    Guid PatientId,
    Guid? ClinicId,
    Guid? DoctorId,
    DateTime ExamDateUtc,
    string ReviewStatus,
    string ModelVersion,
    string? AiRiskLevel,
    bool DoctorApproved,
    string? IcdrLevel,
    string? DoctorNotes,
    string? DoctorReason,
    double? VesselDensity,
    double? TortuosityIndex,
    double? AvRatio,
    string Disclaimer,
    string SecureAnalysisUrl,
    byte[] OriginalImage,
    byte[] VesselMaskImage);

/// <summary>Renders one case as a compressed A4 clinical report. ICDR is only shown when stored on an approved review.</summary>
public static class MedicalReportPdfRenderer
{
    public const int MaximumPdfBytes = 1_200_000;

    public static byte[] Render(MedicalReportPdfData data)
    {
        if (data.ReportId == Guid.Empty || data.AnalysisId == Guid.Empty || data.BatchId == Guid.Empty
            || data.PatientId == Guid.Empty || data.ExamDateUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(data.ModelVersion) || string.IsNullOrWhiteSpace(data.SecureAnalysisUrl)
            || data.OriginalImage is not { Length: > 0 } || data.VesselMaskImage is not { Length: > 0 })
            throw new ArgumentException("Invalid medical report data.", nameof(data));

        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(data.SecureAnalysisUrl, QRCodeGenerator.ECCLevel.M);
        using var qrRenderer = new PngByteQRCode(qrData);
        var qrPng = qrRenderer.GetGraphic(5);

        foreach (var (maxDimension, quality) in new[] { (1600, 82), (1200, 72), (960, 60) })
        {
            var original = OptimizeJpeg(data.OriginalImage, maxDimension, quality);
            var mask = OptimizeJpeg(data.VesselMaskImage, maxDimension, quality);
            var pdf = RenderPdf(data, original, mask, qrPng);
            if (pdf.Length < MaximumPdfBytes) return pdf;
        }

        throw new InvalidOperationException("The compressed medical report exceeds the 1.2 MB limit.");
    }

    private static byte[] OptimizeJpeg(byte[] bytes, int maxDimension, int quality)
    {
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("The report image could not be decoded.");
        var scale = Math.Min(1d, (double)maxDimension / Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = width == bitmap.Width && height == bitmap.Height
            ? null
            : bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions());
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("The report image could not be compressed.");
        return encoded.ToArray();
    }

    private static byte[] RenderPdf(MedicalReportPdfData data, byte[] original, byte[] mask, byte[] qrPng) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style.FontSize(9));

            page.Header().Column(header =>
            {
                header.Item().Row(row =>
                {
                    row.RelativeItem().Column(title =>
                    {
                        title.Item().Text("AURA | FUNDUS ANALYSIS REPORT").FontSize(17).Bold().FontColor(Colors.Blue.Darken2);
                        title.Item().PaddingTop(3).Text($"Report {data.ReportId:D}  ·  Analysis {data.AnalysisId:D}").FontSize(8).FontColor(Colors.Grey.Darken2);
                    });
                    row.ConstantItem(58).Height(58).Image(qrPng).FitArea();
                });
                header.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            });

            page.Content().PaddingVertical(10).Column(content =>
            {
                content.Spacing(8);
                content.Item().Text("CASE INFORMATION").Bold().FontSize(10);
                content.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn();
                        columns.RelativeColumn();
                    });
                    AddInfo(table, "Clinic ID", data.ClinicId?.ToString("D") ?? "Not provided");
                    AddInfo(table, "Patient ID", data.PatientId.ToString("D"));
                    AddInfo(table, "Doctor ID", data.DoctorId?.ToString("D") ?? "No doctor review recorded");
                    AddInfo(table, "Examination date (UTC)", data.ExamDateUtc.ToString("yyyy-MM-dd HH:mm 'UTC'"));
                    AddInfo(table, "Batch ID", data.BatchId.ToString("D"));
                    AddInfo(table, "Review status", data.ReviewStatus);
                });

                content.Item().PaddingTop(2).Text("FUNDUS IMAGES").Bold().FontSize(10);
                content.Item().Row(row =>
                {
                    row.Spacing(10);
                    AddImagePanel(row.RelativeItem(), "Original color fundus", original);
                    AddImagePanel(row.RelativeItem(), "Vessel mask / overlay", mask);
                });

                content.Item().PaddingTop(2).Text("QUANTITATIVE MEASUREMENTS").Bold().FontSize(10);
                content.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(1.5f);
                        columns.RelativeColumn(1);
                        columns.RelativeColumn(1.8f);
                    });
                    table.Header(header =>
                    {
                        HeaderCell(header.Cell(), "Measurement");
                        HeaderCell(header.Cell(), "Value");
                        HeaderCell(header.Cell(), "Source");
                    });
                    MetricRow(table, "vessel_density", data.VesselDensity, data.ModelVersion);
                    MetricRow(table, "tortuosity_index", data.TortuosityIndex, data.ModelVersion);
                    MetricRow(table, "av_ratio", data.AvRatio, data.ModelVersion);
                });

                content.Item().PaddingTop(2).Text("CLINICAL REVIEW").Bold().FontSize(10);
                content.Item().Text(text =>
                {
                    text.Span("AI model version: ").SemiBold();
                    text.Span(data.ModelVersion);
                    text.Span("  |  AI result: ").SemiBold();
                    text.Span(data.AiRiskLevel ?? "Not classified");
                });
                content.Item().Text(text =>
                {
                    text.Span("ICDR grade: ").SemiBold();
                    text.Span(data.DoctorApproved
                        ? data.IcdrLevel ?? "Not recorded by approving doctor"
                        : "Not assigned; no approved doctor review");
                });
                content.Item().Text(text =>
                {
                    text.Span("Doctor revision note: ").SemiBold();
                    text.Span(string.IsNullOrWhiteSpace(data.DoctorNotes) ? "No note recorded." : data.DoctorNotes);
                });
                if (!string.IsNullOrWhiteSpace(data.DoctorReason))
                    content.Item().Text(text => { text.Span("Correction reason: ").SemiBold(); text.Span(data.DoctorReason); });
                content.Item().PaddingTop(2).Text(data.Disclaimer).FontSize(8).FontColor(Colors.Grey.Darken2);
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.Span("Secure case record · scan QR to open the authenticated analysis endpoint · Page ").FontSize(7);
                text.CurrentPageNumber().FontSize(7);
            });
        })).GeneratePdf();

    private static void AddInfo(TableDescriptor table, string label, string value)
    {
        table.Cell().Element(InfoCell).Text(label).SemiBold();
        table.Cell().Element(InfoCell).Text(value);
    }

    private static IContainer InfoCell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4);

    private static void HeaderCell(IContainer cell, string text) =>
        cell.Background(Colors.Blue.Darken2).Padding(5).Text(text).FontColor(Colors.White).Bold();

    private static void MetricRow(TableDescriptor table, string name, double? value, string modelVersion)
    {
        var formatted = value is { } number && double.IsFinite(number)
            ? number.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : "Not available";
        table.Cell().Element(InfoCell).Text(name);
        table.Cell().Element(InfoCell).Text(formatted);
        table.Cell().Element(InfoCell).Text(modelVersion);
    }

    private static void AddImagePanel(IContainer container, string label, byte[] image) =>
        container.Border(0.75f).BorderColor(Colors.Grey.Lighten1).Padding(5).Column(column =>
        {
            column.Item().Text(label).SemiBold().FontSize(8);
            column.Item().PaddingTop(4).Height(150).Image(image).FitArea();
        });
}
