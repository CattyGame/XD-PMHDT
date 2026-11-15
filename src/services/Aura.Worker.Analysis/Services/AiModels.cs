using System.Text.Json.Serialization;

namespace Aura.Worker.Analysis.Services;

public class AiAnalyzeRequest
{
    [JsonPropertyName("request_id")]
    public Guid RequestId { get; set; }

    [JsonPropertyName("patient_id")]
    public object? PatientId { get; set; } = null;

    [JsonPropertyName("eye_side")]
    public string EyeSide { get; set; } = "unknown";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "retina_vessels";

    [JsonPropertyName("modality")]
    public string Modality { get; set; } = "FUNDUS";

    [JsonPropertyName("image_base64")]
    public string ImageBase64 { get; set; } = string.Empty;

    [JsonPropertyName("include_mask")]
    public bool IncludeMask { get; set; } = true;

    [JsonPropertyName("include_overlay")]
    public bool IncludeOverlay { get; set; } = true;
}

public class AiAnalyzeResponse
{
    [JsonPropertyName("request_id")]
    public Guid RequestId { get; set; }

    [JsonPropertyName("patient_id")]
    public object? PatientId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("warnings")]
    public List<AiWarning> Warnings { get; set; } = [];

    [JsonPropertyName("is_mock")]
    public bool IsMock { get; set; }

    [JsonPropertyName("model_version")]
    public string ModelVersion { get; set; } = string.Empty;

    [JsonPropertyName("config_version")]
    public string ConfigVersion { get; set; } = string.Empty;

    [JsonPropertyName("threshold_version")]
    public string ThresholdVersion { get; set; } = string.Empty;

    [JsonPropertyName("limitations")]
    public string Limitations { get; set; } = string.Empty;

    [JsonPropertyName("processing_time_ms")]
    public double ProcessingTimeMs { get; set; }

    [JsonPropertyName("image_info")]
    public AiImageInfo ImageInfo { get; set; } = default!;

    [JsonPropertyName("metrics")]
    public AiMetrics Metrics { get; set; } = default!;

    [JsonPropertyName("risk_available")]
    public bool RiskAvailable { get; set; }

    [JsonPropertyName("risk_assessment")]
    public AiRiskAssessment? RiskAssessment { get; set; }

    [JsonPropertyName("segmentation")]
    public AiSegmentation Segmentation { get; set; } = default!;

    [JsonPropertyName("research")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiResearchResults? Research { get; set; }
}

public sealed class AiResearchResults
{
    [JsonPropertyName("avr")] public AiAvrResearch? Avr { get; set; }
    [JsonPropertyName("dr")] public AiDrResearch? Dr { get; set; }
}

public sealed class AiAvrResearch
{
    [JsonPropertyName("measurement_available")] public bool? MeasurementAvailable { get; set; }
    [JsonPropertyName("avr")] public double? Avr { get; set; }
    [JsonPropertyName("crae_px")] public double? CraePx { get; set; }
    [JsonPropertyName("crve_px")] public double? CrvePx { get; set; }
    [JsonPropertyName("unavailable_reason")] public string? UnavailableReason { get; set; }
    [JsonPropertyName("model_version")] public string ModelVersion { get; set; } = "";
    [JsonPropertyName("model_sha256")] public string ModelSha256 { get; set; } = "";
}

public sealed class AiDrResearch
{
    [JsonPropertyName("probability_dr")] public double? ProbabilityDr { get; set; }
    [JsonPropertyName("model_version")] public string ModelVersion { get; set; } = "";
    [JsonPropertyName("checkpoint_sha256")] public string CheckpointSha256 { get; set; } = "";
    [JsonPropertyName("gradcam_method")] public string GradcamMethod { get; set; } = "";
    [JsonPropertyName("gradcam_geometry")] public string GradcamGeometry { get; set; } = "";
}

public class AiWarning
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class AiImageInfo
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    [JsonPropertyName("eye_side")]
    public string EyeSide { get; set; } = string.Empty;

    [JsonPropertyName("modality")]
    public string Modality { get; set; } = "FUNDUS";
}

public class AiMetrics
{
    [JsonPropertyName("vessel_density")]
    public double VesselDensity { get; set; }

    [JsonPropertyName("tortuosity_index")]
    public double? TortuosityIndex { get; set; }

    [JsonPropertyName("av_ratio")]
    public double? AvRatio { get; set; }

    [JsonPropertyName("fractal_dimension")]
    public double? FractalDimension { get; set; }

    [JsonPropertyName("branching_points")]
    public int? BranchingPoints { get; set; }
}

public class AiRiskAssessment
{
    [JsonPropertyName("risk_score")]
    public double? RiskScore { get; set; }

    [JsonPropertyName("risk_level")]
    public string? RiskLevel { get; set; }

    [JsonPropertyName("confidence_score")]
    public double? ConfidenceScore { get; set; }

    [JsonPropertyName("indicators")]
    public List<string> Indicators { get; set; } = [];

    [JsonPropertyName("disclaimer")]
    public string Disclaimer { get; set; } = string.Empty;
}

public class AiSegmentation
{
    [JsonPropertyName("mask_format")]
    public string MaskFormat { get; set; } = "png_base64";

    [JsonPropertyName("mask_base64")]
    public string MaskBase64 { get; set; } = string.Empty;

    [JsonPropertyName("overlay_base64")]
    public string OverlayBase64 { get; set; } = string.Empty;

    [JsonPropertyName("av_overlay_base64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvOverlayBase64 { get; set; }

    [JsonPropertyName("gradcam_base64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GradcamBase64 { get; set; }
}

public class AiErrorResponse
{
    [JsonPropertyName("error_code")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}
