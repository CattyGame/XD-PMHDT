using Aura.Worker.Analysis.Processing;
using Aura.Worker.Analysis.Services;
using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aura.Worker.Analysis.Tests;

public class AiResultValidatorTests
{
    private AiAnalyzeResponse CreateValidResponse()
    {
        return new AiAnalyzeResponse
        {
            RequestId = Guid.NewGuid(),
            Timestamp = "2026-10-30T12:00:00Z",
            Status = "WARNING",
            IsMock = false,
            ModelVersion = "frangi-v0.2",
            ConfigVersion = "s3-frangi-1.0.0",
            ThresholdVersion = "frangi-threshold-0.02-v1",
            Limitations = "Segmentation research baseline",
            ProcessingTimeMs = 120,
            ImageInfo = new AiImageInfo
            {
                Width = 1024,
                Height = 768,
                Channels = 3,
                EyeSide = "left",
                Modality = "FUNDUS"
            },
            Metrics = new AiMetrics
            {
                VesselDensity = 0.08,
                TortuosityIndex = null,
                AvRatio = null,
                FractalDimension = null,
                BranchingPoints = null
            },
            RiskAvailable = false,
            RiskAssessment = null,
            Warnings = new List<AiWarning>
            {
                new() { Code = "RISK_UNAVAILABLE", Message = "Frangi provides vessel segmentation only." }
            },
            Segmentation = new AiSegmentation
            {
                MaskFormat = "png_base64",
                MaskBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZfoAAAAASUVORK5CYII=",
                OverlayBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZfoAAAAASUVORK5CYII="
            }
        };
    }

    [Fact]
    public void Validate_ValidFrangiSprint3Response_ReturnsValid()
    {
        var response = CreateValidResponse();

        var result = AiResultValidator.Validate(response);

        Assert.True(result.IsValid);
        Assert.False(result.IsMockHold);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Validate_MockDetectedInRealNamespace_ReturnsMockHold()
    {
        var response = CreateValidResponse();
        response.IsMock = true;

        var result = AiResultValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.True(result.IsMockHold);
        Assert.Equal("ERR_AI_MOCK_DETECTED", result.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidGeometry_ReturnsError()
    {
        var response = CreateValidResponse();
        response.ImageInfo.Width = 0;

        var result = AiResultValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Equal("ERR_AI_OUTPUT_INVALID", result.ErrorCode);
    }

    [Fact]
    public void Validate_RiskAvailableFalse_WithRiskAssessment_ReturnsError()
    {
        var response = CreateValidResponse();
        response.RiskAvailable = false;
        response.RiskAssessment = new AiRiskAssessment { RiskScore = 0.5, RiskLevel = "LOW" };

        var result = AiResultValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Equal("ERR_AI_OUTPUT_INVALID", result.ErrorCode);
    }

    [Fact]
    public void Validate_MissingRiskUnavailableWarning_ReturnsError()
    {
        var response = CreateValidResponse();
        response.Warnings.Clear();

        var result = AiResultValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Equal("ERR_AI_OUTPUT_INVALID", result.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidVesselDensity_ReturnsError()
    {
        var response = CreateValidResponse();
        response.Metrics.VesselDensity = 1.5; // Invalid > 1.0

        var result = AiResultValidator.Validate(response);

        Assert.False(result.IsValid);
        Assert.Equal("ERR_AI_OUTPUT_INVALID", result.ErrorCode);
    }

    private AiAnalyzeResponse IntegratedResponse()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(CreateValidResponse()))!;
        json["research"] = JsonNode.Parse("""
            {"avr":{"measurement_available":false,"avr":null,"crae_px":null,"crve_px":null,
            "unavailable_reason":"INSUFFICIENT_SIX_VESSEL_SETS","model_version":"avr-v8",
            "model_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},
            "dr":{"probability_dr":0.61,"model_version":"resnet34",
            "checkpoint_sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "gradcam_method":"GRAD_CAM","gradcam_geometry":"IMAGENET_CENTER_CROP_224"}}
            """);
        json["segmentation"]!["gradcam_base64"] = "crop";
        return JsonSerializer.Deserialize<AiAnalyzeResponse>(json.ToJsonString())!;
    }

    [Fact]
    public void Integrated_Result_Roundtrip_PreservesResearchAndCropWithoutInventingRisk()
    {
        var value = IntegratedResponse();
        Assert.True(AiResultValidator.Validate(value).IsValid);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.True(json.RootElement.TryGetProperty("research", out var research));
        Assert.Equal(0.61, research.GetProperty("dr").GetProperty("probability_dr").GetDouble());
        Assert.Equal(JsonValueKind.Null, research.GetProperty("avr").GetProperty("avr").ValueKind);
        Assert.Equal("crop", json.RootElement.GetProperty("segmentation").GetProperty("gradcam_base64").GetString());
        Assert.False(value.RiskAvailable);
    }

    [Fact]
    public void Integrated_MissingAvrCannotCarryStaleNumericMetric()
    {
        var value = IntegratedResponse();
        value.Metrics.AvRatio = 0.6;
        Assert.False(AiResultValidator.Validate(value).IsValid);
    }

    [Fact]
    public void Integrated_ResearchCannotClaimLowClinicalRisk()
    {
        var value = IntegratedResponse();
        value.RiskAvailable = true;
        value.RiskAssessment = new AiRiskAssessment { RiskScore = 0.1, RiskLevel = "LOW" };
        Assert.Equal("ERR_AI_OUTPUT_INVALID", AiResultValidator.Validate(value).ErrorCode);
    }

    [Fact]
    public void Integrated_ExplicitNullHashIsInvalidRatherThanCrashing()
    {
        var value = IntegratedResponse();
        value.Research!.Dr!.CheckpointSha256 = null!;
        Assert.Equal("ERR_AI_OUTPUT_INVALID", AiResultValidator.Validate(value).ErrorCode);
    }
}
