using System.Text.Json;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

/// <summary>4 test M1 yêu cầu trong review SCRUM-325 + test mapper đọc đúng cột thật của 001.</summary>
public sealed class AnalysisResultM1Tests
{
    private sealed class Repo : IAnalysisQueryRepository
    {
        public AnalysisIdentity? Identity; public AnalysisDetailResponse? Response;
        public Task<AnalysisIdentity?> FindIdentityAsync(Guid id, CancellationToken ct) => Task.FromResult(Identity);
        public Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity i, string? c, CancellationToken ct) => Task.FromResult(Response);
    }

    private static readonly Guid Owner = Guid.Parse("9b2e5c10-1111-4a2b-8c3d-000000000001");

    private static AnalysisResultDetail FrangiResult(Guid mask, Guid overlay) => AnalysisResultMapper.Map(
        1, "null", """{"vesselDensity":0.1428,"tortuosityIndex":null,"avRatio":null,"fractalDimension":1.48,"branchingPoints":64}""",
        """[{"code":"RISK_UNAVAILABLE","message":"Chưa cung cấp đánh giá rủi ro."}]""",
        false, "frangi-v0.2", "s3-frangi-1.0.0", "frangi-threshold-0.02-v1", 120.5, "Segmentation research baseline.",
        new[] { new AnalysisResultMapper.AnnotationRow(overlay, "OVERLAY"), new AnalysisResultMapper.AnnotationRow(mask, "MASK") });

    private static (Repo repo, Guid id, Guid batch) Setup(string status, AnalysisResultDetail? result, AnalysisErrorDetail? error)
    {
        var id = Guid.NewGuid(); var batch = Guid.NewGuid(); var patient = Guid.NewGuid();
        var repo = new Repo
        {
            Identity = new AnalysisIdentity(id, batch, FakeAccess.Tenant, patient, Owner),
            Response = new AnalysisDetailResponse(id, batch, patient, status, "NOT_REQUIRED",
                DateTime.UtcNow, DateTime.UtcNow, Owner, Owner, new AnalysisImageRef(Guid.NewGuid()),
                new AnalysisJobDetail(Guid.NewGuid(), status == "COMPLETED" ? "SUCCEEDED" : "RUNNING", 0, 1), result, error)
        };
        return (repo, id, batch);
    }

    [Fact]
    public async Task Get_WithPermission_ReturnsCompletedResult_WithAnnotationsAndCamelCase()
    {
        var mask = Guid.NewGuid(); var overlay = Guid.NewGuid();
        var (repo, id, _) = Setup("COMPLETED", FrangiResult(mask, overlay), null);
        var resp = await new AnalysisQueryService(repo, new FakeAccess()).GetAsync(Owner, null, id, CancellationToken.None);

        var json = JsonSerializer.Serialize(resp, UploadJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("COMPLETED", root.GetProperty("processingStatus").GetString());
        var r = root.GetProperty("result");
        Assert.False(r.GetProperty("riskAvailable").GetBoolean());
        // Trường bắt buộc nullable của OpenAPI 0.7 phải có mặt dù null
        Assert.Equal(JsonValueKind.Null, r.GetProperty("riskLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("riskScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("confidenceScore").ValueKind);
        Assert.Equal("frangi-v0.2", r.GetProperty("modelVersion").GetString());
        Assert.Equal(0.1428, r.GetProperty("metrics").GetProperty("vesselDensity").GetDouble());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("metrics").GetProperty("avRatio").ValueKind);
        var ann = r.GetProperty("annotations");
        Assert.Equal(2, ann.GetArrayLength());
        Assert.Equal("mask", ann[0].GetProperty("kind").GetString());
        Assert.Equal(mask, ann[0].GetProperty("fileId").GetGuid());
        Assert.Equal("overlay", ann[1].GetProperty("kind").GetString());
        Assert.Equal("RISK_UNAVAILABLE", r.GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("ProcessingStatus", out _)); // không PascalCase
    }

    [Fact]
    public async Task Get_DeniedOrCrossTenant_Returns404NotFound()
    {
        var (repo, id, _) = Setup("COMPLETED", FrangiResult(Guid.NewGuid(), Guid.NewGuid()), null);
        var denied = await Assert.ThrowsAsync<UploadFailure>(() =>
            new AnalysisQueryService(repo, new FakeAccess { Allowed = false }).GetAsync(Owner, null, id, CancellationToken.None));
        Assert.Equal(404, denied.Status);

        var cross = await Assert.ThrowsAsync<UploadFailure>(() =>
            new AnalysisQueryService(repo, new FakeAccess { TenantOverride = Guid.NewGuid() }).GetAsync(Owner, null, id, CancellationToken.None));
        Assert.Equal(404, cross.Status);

        var otherOwner = await Assert.ThrowsAsync<UploadFailure>(() =>
            new AnalysisQueryService(repo, new FakeAccess()).GetAsync(Guid.NewGuid(), null, id, CancellationToken.None));
        Assert.Equal(404, otherOwner.Status);

        repo.Identity = null; // không tồn tại cũng 404, không phân biệt với không có quyền
        var missing = await Assert.ThrowsAsync<UploadFailure>(() =>
            new AnalysisQueryService(repo, new FakeAccess()).GetAsync(Owner, null, id, CancellationToken.None));
        Assert.Equal("ERR_NOT_FOUND", missing.Code);
    }

    [Theory]
    [InlineData("PROCESSING")]
    [InlineData("QUEUED")]
    public async Task Get_ProcessingStatusNotCompleted_ReturnsNullResult(string status)
    {
        var (repo, id, _) = Setup(status, null, null);
        var resp = await new AnalysisQueryService(repo, new FakeAccess()).GetAsync(Owner, null, id, CancellationToken.None);
        var json = JsonSerializer.Serialize(resp, UploadJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(status, doc.RootElement.GetProperty("processingStatus").GetString());
        Assert.False(doc.RootElement.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("FAILED", "ERR_AI_FAILED", true)]
    [InlineData("LOW_QUALITY", "ERR_IMAGE_LOW_QUALITY", false)]
    public async Task Get_FailedOrLowQuality_ReturnsErrorPayload(string status, string code, bool retryable)
    {
        var (repo, id, _) = Setup(status, null, new AnalysisErrorDetail(code, "msg", "corr-1", retryable));
        var resp = await new AnalysisQueryService(repo, new FakeAccess()).GetAsync(Owner, null, id, CancellationToken.None);
        var json = JsonSerializer.Serialize(resp, UploadJson.Options);
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement.GetProperty("error");
        Assert.Equal(code, e.GetProperty("code").GetString());
        Assert.Equal("corr-1", e.GetProperty("correlationId").GetString());
        Assert.Equal(retryable, e.GetProperty("retryable").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public void Mapper_ReadsRealColumns_RiskNullMeansUnavailable_AndOrdersAnnotations()
    {
        var mask = Guid.NewGuid(); var overlay = Guid.NewGuid();
        var r = FrangiResult(mask, overlay);
        Assert.False(r.RiskAvailable);
        Assert.Null(r.RiskLevel);
        Assert.Equal(1, r.Version);
        Assert.Equal(64, r.Metrics.BranchingPoints);
        Assert.Null(r.Metrics.TortuosityIndex);
        Assert.Equal(new[] { "mask", "overlay" }, r.Annotations.Select(a => a.Kind));
        Assert.Equal(120.5, r.ProcessingTimeMs);
        Assert.Empty(r.Indicators);
    }

    [Fact]
    public void Mapper_RiskObject_SetsRiskAvailable_AndAcceptsSnakeCaseMetrics()
    {
        var r = AnalysisResultMapper.Map(2, """{"riskLevel":"MODERATE","riskScore":0.57,"confidenceScore":0.88}""",
            """{"vessel_density":0.2,"branching_points":10}""", "[]", true, "m", "c", "t", 1, "l",
            Array.Empty<AnalysisResultMapper.AnnotationRow>());
        Assert.True(r.RiskAvailable);
        Assert.Equal("MODERATE", r.RiskLevel);
        Assert.Equal(0.57, r.RiskScore);
        Assert.Equal(0.2, r.Metrics.VesselDensity);
        Assert.Equal(10, r.Metrics.BranchingPoints);
        Assert.Empty(r.Annotations);
    }

    [Theory]
    [InlineData("{\"risk_level\":\"RISK_UNAVAILABLE\",\"risk_score\":0,\"confidence_score\":0}")]
    [InlineData("{\"riskLevel\":\"LOW\",\"riskScore\":null,\"confidenceScore\":null}")]
    public void Mapper_UnavailableOrIncompleteRisk_KeepsNullAvrAndNoRisk(string riskJson)
    {
        var r = AnalysisResultMapper.Map(1, riskJson, "{\"av_ratio\":null}",
            "[{\"code\":\"AVR_UNAVAILABLE\",\"message\":\"Insufficient vessels\"}]",
            false, "m", "c", "t", 1, "research", Array.Empty<AnalysisResultMapper.AnnotationRow>());
        Assert.False(r.RiskAvailable);
        Assert.Null(r.RiskLevel);
        Assert.Null(r.RiskScore);
        Assert.Null(r.ConfidenceScore);
        Assert.Null(r.Metrics.AvRatio);
        Assert.Equal("AVR_UNAVAILABLE", Assert.Single(r.Warnings).Code);
    }

    [Fact]
    public void Mapper_PreservesCanonicalResearchMetadataFromMetricsJsonb()
    {
        var r = AnalysisResultMapper.Map(1, "null", """
            {"avRatio":null,"research":{"avr":{"measurement_available":false,
            "avr":null,"unavailable_reason":"INCOMPLETE_ZONE_B","model_version":"avr-v8"},
            "dr":{"probability_dr":0.61,"model_version":"resnet34",
            "gradcam_geometry":"IMAGENET_CENTER_CROP_224"}}}
            """, "[]", false, "frangi-v0.2", "c", "t", 1, "research", Array.Empty<AnalysisResultMapper.AnnotationRow>());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(r, UploadJson.Options));
        Assert.True(json.RootElement.TryGetProperty("research", out var research));
        Assert.Equal(0.61, research.GetProperty("dr").GetProperty("probability_dr").GetDouble());
        Assert.Equal("INCOMPLETE_ZONE_B", research.GetProperty("avr").GetProperty("unavailable_reason").GetString());
        Assert.False(r.RiskAvailable);
    }
}
