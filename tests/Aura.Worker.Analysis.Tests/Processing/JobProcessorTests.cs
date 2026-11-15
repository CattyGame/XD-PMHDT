using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Data.Repositories;
using Aura.Worker.Analysis.Domain;
using Aura.Worker.Analysis.Messaging;
using Aura.Worker.Analysis.Processing;
using Aura.Worker.Analysis.Services;
using Xunit;
using AURA.Common.Cache;

namespace Aura.Worker.Analysis.Tests.Processing;

public class JobProcessorTests
{
    private readonly Mock<IJobRepository> _jobRepoMock = new();
    private readonly Mock<IAnalysisRepository> _analysisRepoMock = new();
    private readonly Mock<IBlobStorageService> _blobMock = new();
    private readonly Mock<IAiHttpClient> _aiMock = new();
    private readonly Mock<IProfileResolveClient> _profileMock = new();
    private readonly Mock<IBatchRedisProgressService> _batchProgressMock = new();
    private readonly Mock<IBatchCompletedEventPublisher> _batchEventsMock = new();
    private readonly Mock<ILogger<JobProcessor>> _loggerMock = new();
    private readonly IOptions<WorkerOptions> _options;

    public JobProcessorTests()
    {
        _jobRepoMock.Setup(r => r.RenewLeaseAsync(It.IsAny<JobLease>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((JobLease lease, TimeSpan duration, CancellationToken ct) => Task.FromResult<JobLease?>(lease));
        _options = Options.Create(new WorkerOptions
        {
            MaxAttempts = 3,
            RetryDelaysSeconds = [5, 15],
            LeaseSeconds = 90,
            DurableCallIntent = true
        });

        _batchProgressMock
            .Setup(p => p.RecordImageOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<bool>()))
            .Returns((Guid batchId, Guid imageId, bool isSuccess) =>
                Task.FromResult(new BatchImageCompletionResult(
                    batchId,
                    2,
                    isSuccess ? 1 : 0,
                    isSuccess ? 0 : 1,
                    1,
                    false,
                    "IN_PROGRESS")));

        _batchEventsMock
            .Setup(p => p.PublishAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<BatchImageCompletionResult>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private JobProcessor CreateProcessor()
    {
        return new JobProcessor(
            _jobRepoMock.Object,
            _analysisRepoMock.Object,
            _blobMock.Object,
            _aiMock.Object,
            _profileMock.Object,
            _batchProgressMock.Object,
            _options,
            _loggerMock.Object);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(0.672, false, false)]
    [InlineData(null, true, false)]
    [InlineData(0.672, true, false)]
    [InlineData(null, true, true)]
    public async Task AC1_Annotations_AreValidatedBeforeCanonicalCommit(double? avRatio, bool integrated, bool invalidCrop)
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var patientId = Guid.NewGuid();

        var message = new JobRunMessage
        {
            JobId = jobId,
            AnalysisId = analysisId,
            TenantId = tenantId,
            BatchId = batchId,
            Attempt = 1,
            CorrelationId = "test-corr-001"
        };

        var job = new JobRecord
        {
            JobId = jobId,
            AnalysisId = analysisId,
            TenantId = tenantId,
            AttemptNo = 1,
            Status = JobStatus.Queued
        };

        var analysis = new AnalysisRecord
        {
            AnalysisId = analysisId,
            TenantId = tenantId,
            PatientId = patientId,
            BatchId = batchId,
            ProcessingStatus = AnalysisStatus.Queued,
            OriginalBlobKey = "original.png"
        };

        _jobRepoMock.Setup(r => r.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _analysisRepoMock.Setup(r => r.GetAnalysisAsync(analysisId, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        _jobRepoMock.Setup(r => r.TryAcquireLeaseAsync(jobId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns((Guid id, string owner, TimeSpan duration, CancellationToken ct) => Task.FromResult<JobLease?>(new(id,owner,1,DateTime.UtcNow.Add(duration))));

        _profileMock.Setup(p => p.RecheckAccessAsync(It.Is<WorkerAccessContext>(x => x.TenantId == tenantId && x.PatientId == patientId), "ANALYSIS_READ", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileResolveResult { Allowed = true });

        _blobMock.Setup(b => b.DownloadOriginalImageAsync("original.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });

        var validAiResponse = new AiAnalyzeResponse
        {
            RequestId = analysisId,
            Status = "WARNING",
            IsMock = false,
            ModelVersion = "frangi-v0.2",
            ConfigVersion = "s3-frangi-1.0.0",
            ThresholdVersion = "frangi-threshold-0.02-v1",
            ImageInfo = new AiImageInfo { Width = 1, Height = 1, Channels = 3, EyeSide = "left", Modality = "FUNDUS" },
            Metrics = new AiMetrics { VesselDensity = 0.05, AvRatio = avRatio },
            RiskAvailable = false,
            RiskAssessment = null,
            Warnings = new List<AiWarning> { new() { Code = "RISK_UNAVAILABLE", Message = "Frangi" } },
            Segmentation = new AiSegmentation
            {
                MaskFormat = "png_base64",
                MaskBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jkioAAAAASUVORK5CYII=",
                OverlayBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jkioAAAAASUVORK5CYII="
            }
        };

        if (avRatio is null)
            validAiResponse.Warnings.Add(new() { Code = "AVR_UNAVAILABLE", Message = "Insufficient vessel sets" });
        if (integrated)
        {
            validAiResponse.Research = new AiResearchResults {
                Avr = new AiAvrResearch { MeasurementAvailable = avRatio is not null, Avr = avRatio,
                    CraePx = avRatio * 20, CrvePx = avRatio is null ? null : 20,
                    UnavailableReason = avRatio is null ? "INSUFFICIENT_SIX_VESSEL_SETS" : null,
                    ModelVersion = "avr-v8", ModelSha256 = new string('a',64) },
                Dr = new AiDrResearch { ProbabilityDr = 0.61, ModelVersion = "resnet34", CheckpointSha256 = new string('b',64),
                    GradcamMethod = "GRAD_CAM", GradcamGeometry = "IMAGENET_CENTER_CROP_224" }
            };
            validAiResponse.Segmentation.AvOverlayBase64 = validAiResponse.Segmentation.OverlayBase64;
            validAiResponse.Segmentation.GradcamBase64 = "iVBORw0KGgoAAAANSUhEUgAAAOAAAADgCAAAAAA/RjU9AAAASElEQVR42u3BMQEAAADCoPVPbQo/oAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAICXAcTgAAGGKhurAAAAAElFTkSuQmCC";
            if (invalidCrop) validAiResponse.Segmentation.GradcamBase64 = validAiResponse.Segmentation.OverlayBase64;
        }

        _aiMock.Setup(a => a.AnalyzeAsync(It.IsAny<AiAnalyzeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCallResult { IsSuccess = true, Response = validAiResponse });

        string? committedJson = null;
        _analysisRepoMock.Setup(r => r.CommitSuccessAsync(analysisId, It.IsAny<JobLease>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AnnotationDescriptor>>(), It.IsAny<OutboxRecord>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, JobLease, string, IReadOnlyList<AnnotationDescriptor>, OutboxRecord, CancellationToken>(
                (_, _, json, _, _, _) => committedJson = json)
            .Returns(Task.CompletedTask);

        var processor = CreateProcessor();

        // Act
        var result = await processor.ProcessAsync(message, CancellationToken.None);

        // Assert
        if (invalidCrop)
        {
            Assert.Equal(JobProcessResult.AckFailure, result);
            Assert.Null(committedJson);
            _blobMock.Verify(b => b.UploadAnnotationAsync(It.IsAny<string>(),It.IsAny<byte[]>(),It.IsAny<string>(),It.IsAny<CancellationToken>()),Times.Never);
            _analysisRepoMock.Verify(r => r.CommitFailureAsync(analysisId,It.IsAny<JobLease>(),AnalysisStatus.Failed,
                It.IsAny<string>(),It.IsAny<OutboxRecord>(),It.IsAny<CancellationToken>()),Times.Once);
            return;
        }
        Assert.Equal(JobProcessResult.AckSuccess, result);
        using var persisted = JsonDocument.Parse(Assert.IsType<string>(committedJson));
        var root = persisted.RootElement;
        var storedAvr = root.GetProperty("metrics").GetProperty("avRatio");
        if (avRatio is null) Assert.Equal(JsonValueKind.Null, storedAvr.ValueKind);
        else Assert.Equal(avRatio.Value, storedAvr.GetDouble());
        Assert.False(root.GetProperty("riskAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("riskLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("riskScore").ValueKind);
        if (avRatio is null)
            Assert.Contains(root.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "AVR_UNAVAILABLE");
        if (integrated)
        {
            Assert.True(root.GetProperty("metrics").TryGetProperty("research", out var research));
            Assert.Equal(0.61, research.GetProperty("dr").GetProperty("probability_dr").GetDouble());
            Assert.Equal(4, root.GetProperty("annotations").GetArrayLength());
        }
        _blobMock.Verify(b => b.UploadAnnotationAsync(It.Is<string>(s => s.Contains("mask")), It.IsAny<byte[]>(), "image/png", It.IsAny<CancellationToken>()), Times.Once);
        _blobMock.Verify(b => b.UploadAnnotationAsync(It.Is<string>(s => s.Contains("overlay")), It.IsAny<byte[]>(), "image/png", It.IsAny<CancellationToken>()), Times.Once);
        _analysisRepoMock.Verify(r => r.CommitSuccessAsync(analysisId, It.Is<JobLease>(l => l.JobId == jobId), It.IsAny<string>(), It.Is<IReadOnlyList<AnnotationDescriptor>>(a => a.Count == (integrated ? 4 : 2)
            && a.All(x => x.SizeBytes != 2048 && x.Width == (x.Type == "HEATMAP" ? 224 : 1))), It.Is<OutboxRecord>(o => o.EventType == "analysis.completed"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AC2_DuplicateJob_AlreadySucceeded_AcksWithoutCallingAi()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var message = new JobRunMessage
        {
            JobId = jobId,
            AnalysisId = analysisId,
            TenantId = tenantId,
            Attempt = 1
        };

        var job = new JobRecord
        {
            JobId = jobId,
            AnalysisId = analysisId,
            TenantId = tenantId,
            AttemptNo = 1,
            Status = JobStatus.Succeeded // already finished
        };

        _jobRepoMock.Setup(r => r.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var processor = CreateProcessor();

        // Act
        var result = await processor.ProcessAsync(message, CancellationToken.None);

        // Assert
        Assert.Equal(JobProcessResult.AckDuplicate, result);
        _aiMock.Verify(a => a.AnalyzeAsync(It.IsAny<AiAnalyzeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AC3_LowQualityImage_CommitsTerminalLowQualityStatus()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var batchId = Guid.NewGuid();

        var message = new JobRunMessage { JobId = jobId, AnalysisId = analysisId, TenantId = tenantId, BatchId = batchId, Attempt = 1 };
        var job = new JobRecord { JobId = jobId, AnalysisId = analysisId, TenantId = tenantId, AttemptNo = 1, Status = JobStatus.Queued };
        var analysis = new AnalysisRecord { AnalysisId = analysisId, TenantId = tenantId, PatientId = Guid.NewGuid(), BatchId = batchId, ProcessingStatus = AnalysisStatus.Queued, OriginalBlobKey = "original.png" };

        _jobRepoMock.Setup(r => r.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        _analysisRepoMock.Setup(r => r.GetAnalysisAsync(analysisId, It.IsAny<CancellationToken>())).ReturnsAsync(analysis);
        _jobRepoMock.Setup(r => r.TryAcquireLeaseAsync(jobId, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).Returns((Guid id, string owner, TimeSpan duration, CancellationToken ct) => Task.FromResult<JobLease?>(new(id,owner,1,DateTime.UtcNow.Add(duration))));
        _profileMock.Setup(p => p.RecheckAccessAsync(It.IsAny<WorkerAccessContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileResolveResult { Allowed = true });
        _blobMock.Setup(b => b.DownloadOriginalImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new byte[] { 1 });

        // AI returns 422 Low Quality
        _aiMock.Setup(a => a.AnalyzeAsync(It.IsAny<AiAnalyzeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiCallResult { IsSuccess = false, IsLowQuality = true, ErrorCode = ErrorCodes.ErrImageLowQuality, ErrorMessage = "Low contrast" });

        var processor = CreateProcessor();

        // Act
        var result = await processor.ProcessAsync(message, CancellationToken.None);

        // Assert
        Assert.Equal(JobProcessResult.AckFailure, result);
        _analysisRepoMock.Verify(r => r.CommitFailureAsync(
            analysisId,
            It.Is<JobLease>(l => l.JobId == jobId),
            AnalysisStatus.LowQuality,
            It.IsAny<string>(),
            It.Is<OutboxRecord>(o => o.EventType == "analysis.failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
