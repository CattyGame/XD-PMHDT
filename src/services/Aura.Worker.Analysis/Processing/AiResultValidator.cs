using Aura.Worker.Analysis.Services;

namespace Aura.Worker.Analysis.Processing;

public class ValidationOutcome
{
    public bool IsValid { get; set; }
    public bool IsMockHold { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class AiResultValidator
{
    public static ValidationOutcome Validate(AiAnalyzeResponse response)
    {
        // 1. Check is_mock in real namespace
        if (response.IsMock)
        {
            return new ValidationOutcome
            {
                IsValid = false,
                IsMockHold = true,
                ErrorCode = "ERR_AI_MOCK_DETECTED",
                ErrorMessage = "AI returned mock=true in real namespace; holding for investigation."
            };
        }

        // 2. Validate Image Info & Geometry
        if (response.ImageInfo == null || response.ImageInfo.Width <= 0 || response.ImageInfo.Height <= 0)
        {
            return new ValidationOutcome
            {
                IsValid = false,
                ErrorCode = "ERR_AI_OUTPUT_INVALID",
                ErrorMessage = "Invalid image geometry in AI response."
            };
        }

        // 3. Validate Segmentation outputs
        if (response.Segmentation == null ||
            string.IsNullOrWhiteSpace(response.Segmentation.MaskBase64) ||
            string.IsNullOrWhiteSpace(response.Segmentation.OverlayBase64))
        {
            return new ValidationOutcome
            {
                IsValid = false,
                ErrorCode = "ERR_AI_OUTPUT_INVALID",
                ErrorMessage = "Missing mask or overlay base64 in segmentation output."
            };
        }

        // 4. Validate Risk Invariants for Frangi v0.2
        if (!response.RiskAvailable)
        {
            if (response.RiskAssessment != null)
            {
                return new ValidationOutcome
                {
                    IsValid = false,
                    ErrorCode = "ERR_AI_OUTPUT_INVALID",
                    ErrorMessage = "Risk assessment must be null when risk_available is false."
                };
            }

            var hasWarning = response.Warnings.Any(w => w.Code == "RISK_UNAVAILABLE");
            if (!hasWarning)
            {
                return new ValidationOutcome
                {
                    IsValid = false,
                    ErrorCode = "ERR_AI_OUTPUT_INVALID",
                    ErrorMessage = "Expected RISK_UNAVAILABLE warning when risk_available is false."
                };
            }
        }

        // 5. Validate Metrics
        if (response.Metrics == null || response.Metrics.VesselDensity < 0.0 || response.Metrics.VesselDensity > 1.0)
        {
            return new ValidationOutcome
            {
                IsValid = false,
                ErrorCode = "ERR_AI_OUTPUT_INVALID",
                ErrorMessage = "Vessel density must be between 0.0 and 1.0."
            };
        }

        if (response.Research is { } research)
        {
            var a = research.Avr;
            var d = research.Dr;
            bool Positive(double? value) => value is { } number && double.IsFinite(number) && number > 0;
            bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
            var avrValid = a is not null && (a.MeasurementAvailable is true
                ? Positive(a.Avr) && Positive(a.CraePx) && Positive(a.CrvePx)
                  && Math.Abs(a.Avr!.Value - a.CraePx!.Value / a.CrvePx!.Value) <= 1e-6
                  && a.UnavailableReason is null && !string.IsNullOrWhiteSpace(response.Segmentation.AvOverlayBase64)
                : a.MeasurementAvailable is false && a.Avr is null && a.CraePx is null && a.CrvePx is null
                  && !string.IsNullOrWhiteSpace(a.UnavailableReason));
            if (response.RiskAvailable || !avrValid || a!.Avr != response.Metrics.AvRatio || string.IsNullOrWhiteSpace(a.ModelVersion) || !Hash(a.ModelSha256)
                || d is null || d.ProbabilityDr is not (>= 0 and <= 1) || !double.IsFinite(d.ProbabilityDr.Value)
                || string.IsNullOrWhiteSpace(d.ModelVersion) || !Hash(d.CheckpointSha256)
                || d.GradcamMethod != "GRAD_CAM" || d.GradcamGeometry != "IMAGENET_CENTER_CROP_224"
                || string.IsNullOrWhiteSpace(response.Segmentation.GradcamBase64))
                return new ValidationOutcome { IsValid = false, ErrorCode = "ERR_AI_OUTPUT_INVALID",
                    ErrorMessage = "Invalid integrated research metadata or AVR measurement invariants." };
        }
        else if (response.Segmentation.AvOverlayBase64 is not null || response.Segmentation.GradcamBase64 is not null)
            return new ValidationOutcome { IsValid = false, ErrorCode = "ERR_AI_OUTPUT_INVALID", ErrorMessage = "Research annotations require research provenance." };

        return new ValidationOutcome { IsValid = true };
    }
}
