using System.Text.Json;

namespace AURA.Analysis.Api.Upload;

/// <summary>
/// Dựng AnalysisResultDetail từ các cột THẬT của analysis.analysis_results (migration 001)
/// và analysis.annotation_refs. Không còn dùng cột result_payload (không tồn tại).
/// Hàm thuần, không đụng DB nên test được bằng unit test.
/// </summary>
public static class AnalysisResultMapper
{
    public const string ResearchDisclaimer = "Kết quả phân vùng nghiên cứu, không phải chẩn đoán.";

    public sealed record AnnotationRow(Guid FileId, string Type);

    public static AnalysisResultDetail Map(
        int version, string riskAssessmentJson, string metricsJson, string warningsJson,
        bool isMock, string modelVersion, string configVersion, string thresholdVersion,
        double processingTimeMs, string limitations, IEnumerable<AnnotationRow> annotations)
    {
        string? riskLevel = null; double? riskScore = null; double? confidence = null;
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(riskAssessmentJson) ? "null" : riskAssessmentJson))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                riskLevel = Str(doc.RootElement, "riskLevel", "risk_level");
                riskScore = Num(doc.RootElement, "riskScore", "risk_score");
                confidence = Num(doc.RootElement, "confidenceScore", "confidence_score");
            }
        }
        var riskAvailable = riskLevel is "LOW" or "MODERATE" or "HIGH"
            && riskScore is >= 0 and <= 1 && confidence is >= 0 and <= 1;
        if (!riskAvailable)
        {
            riskLevel = null;
            riskScore = null;
            confidence = null;
        }

        AnalysisMetricsDetail metrics;
        JsonElement? research = null;
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(metricsJson) ? "{}" : metricsJson))
        {
            var m = doc.RootElement;
            if (m.TryGetProperty("research", out var extra) && extra.ValueKind == JsonValueKind.Object)
                research = extra.Clone();
            metrics = new AnalysisMetricsDetail(
                Num(m, "vesselDensity", "vessel_density"),
                Num(m, "tortuosityIndex", "tortuosity_index"),
                Num(m, "avRatio", "av_ratio"),
                Num(m, "fractalDimension", "fractal_dimension"),
                Num(m, "branchingPoints", "branching_points") is { } b ? (int)b : null);
        }

        var warnings = new List<AnalysisWarningDetail>();
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(warningsJson) ? "[]" : warningsJson))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var w in doc.RootElement.EnumerateArray())
                    if (w.ValueKind == JsonValueKind.Object)
                        warnings.Add(new(Str(w, "code") ?? "UNKNOWN", Str(w, "message") ?? ""));
        }

        // Runtime chỉ sinh mask/overlay; kind trả về dạng chữ thường theo OpenAPI 0.7.
        var refs = annotations
            .OrderBy(a => a.Type, StringComparer.Ordinal)
            .Select(a => new AnalysisAnnotationRef(a.FileId, a.Type.ToLowerInvariant()))
            .ToList();

        return new AnalysisResultDetail(
            riskAvailable, riskLevel, riskScore, confidence, isMock,
            modelVersion, configVersion, thresholdVersion, ResearchDisclaimer, version,
            metrics, Array.Empty<string>(), refs, warnings, processingTimeMs, limitations) { Research = research };
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        return null;
    }

    private static double? Num(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number) return p.GetDouble();
        return null;
    }
}
