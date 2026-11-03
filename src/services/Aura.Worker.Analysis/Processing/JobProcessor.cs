using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Data.Repositories;
using Aura.Worker.Analysis.Domain;
using Aura.Worker.Analysis.Messaging;
using Aura.Worker.Analysis.Services;
using AURA.Common.Cache;

namespace Aura.Worker.Analysis.Processing;

public enum JobProcessResult
{
    AckSuccess,
    AckFailure,
    AckDuplicate,
    HoldInvestigate,
    Requeue
}

public interface IJobProcessor
{
    Task<JobProcessResult> ProcessAsync(JobRunMessage message, CancellationToken cancellationToken);
}

public class JobProcessor : IJobProcessor
{
    private readonly IJobRepository _jobRepo;
    private readonly IAnalysisRepository _analysisRepo;
    private readonly IBlobStorageService _blobService;
    private readonly IAiHttpClient _aiClient;
    private readonly IProfileResolveClient _profileClient;
    private readonly WorkerOptions _workerOptions;
    private readonly ILogger<JobProcessor> _logger;
    private readonly IBatchRedisProgressService _batchProgress;
    private readonly IBatchCompletedEventPublisher _batchEvents;
    private readonly string _workerInstanceId;

    public JobProcessor(
        IJobRepository jobRepo,
        IAnalysisRepository analysisRepo,
        IBlobStorageService blobService,
        IAiHttpClient aiClient,
        IProfileResolveClient profileClient,
        IBatchRedisProgressService batchProgress,
        IBatchCompletedEventPublisher batchEvents,
        IOptions<WorkerOptions> workerOptions,
        ILogger<JobProcessor> logger)
    {
        _jobRepo = jobRepo;
        _analysisRepo = analysisRepo;
        _blobService = blobService;
        _aiClient = aiClient;
        _profileClient = profileClient;
        _batchProgress = batchProgress;
        _batchEvents = batchEvents;
        _workerOptions = workerOptions.Value;
        _logger = logger;
        _workerInstanceId = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    public async Task<JobProcessResult> ProcessAsync(JobRunMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing job {JobId} for analysis {AnalysisId}, attempt {Attempt}",
            message.JobId, message.AnalysisId, message.Attempt);

        // 1. Verify Job and matching context in DB
        var job = await _jobRepo.GetJobAsync(message.JobId, cancellationToken);
        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found in database; acknowledging message to avoid poison loop", message.JobId);
            return JobProcessResult.AckFailure;
        }

        if (job.TenantId != message.TenantId || job.AnalysisId != message.AnalysisId || job.AttemptNo != message.Attempt)
        {
            _logger.LogError("Job context mismatch: expected tenant {MsgTenant}, analysis {MsgAnalysis}, attempt {MsgAttempt}; found in DB: tenant {DbTenant}, analysis {DbAnalysis}, attempt {DbAttempt}",
                message.TenantId, message.AnalysisId, message.Attempt, job.TenantId, job.AnalysisId, job.AttemptNo);
            return JobProcessResult.AckFailure;
        }

        // 2. Duplicate Check: if job already SUCCEEDED or FAILED terminal, ACK without re-running AI
        if (job.Status == JobStatus.Succeeded || job.Status == JobStatus.Failed)
        {
            _logger.LogInformation("Job {JobId} already reached terminal state {Status}. Idempotent ACK.", message.JobId, job.Status);
            var terminalAnalysis = await _analysisRepo.GetAnalysisAsync(message.AnalysisId, cancellationToken);
            if (terminalAnalysis is not null)
                await RecordBatchOutcomeAsync(message, terminalAnalysis, job.Status == JobStatus.Succeeded, cancellationToken);
            return JobProcessResult.AckDuplicate;
        }

        var analysis = await _analysisRepo.GetAnalysisAsync(message.AnalysisId, cancellationToken);
        if (analysis == null)
        {
            _logger.LogError("Analysis {AnalysisId} not found for job {JobId}", message.AnalysisId, message.JobId);
            return JobProcessResult.AckFailure;
        }

        if (analysis.ProcessingStatus == AnalysisStatus.Completed ||
            analysis.ProcessingStatus == AnalysisStatus.Failed ||
            analysis.ProcessingStatus == AnalysisStatus.LowQuality)
        {
            _logger.LogInformation("Analysis {AnalysisId} already terminal ({Status}). Acknowledging duplicate job.",
                analysis.AnalysisId, analysis.ProcessingStatus);
            await RecordBatchOutcomeAsync(message, analysis, analysis.ProcessingStatus == AnalysisStatus.Completed, cancellationToken);
            return JobProcessResult.AckDuplicate;
        }

        // 3. Acquire lease
        var leaseAcquired = await _jobRepo.TryAcquireLeaseAsync(
            message.JobId,
            _workerInstanceId,
            TimeSpan.FromSeconds(_workerOptions.LeaseSeconds),
            cancellationToken);

        if (!leaseAcquired)
        {
            _logger.LogWarning("Could not acquire lease for job {JobId}. Requeuing message.", message.JobId);
            return JobProcessResult.Requeue;
        }

        // Refresh job to get latest fencing_token
        job = await _jobRepo.GetJobAsync(message.JobId, cancellationToken) ?? job;

        // 4. Recheck Profile access BEFORE downloading original image
        var initialAccess = await _profileClient.RecheckAccessAsync(
            analysis.TenantId,
            analysis.PatientId,
            "ANALYSIS_READ",
            cancellationToken);

        if (!initialAccess.Allowed)
        {
            if (initialAccess.IsTransientError)
            {
                _logger.LogWarning("Profile service transiently unavailable during pre-check. Requeuing job {JobId}.", message.JobId);
                return JobProcessResult.Requeue;
            }

            _logger.LogWarning("Authority revoked or denied during pre-check for patient {PatientId}. Terminal abort.", analysis.PatientId);
            var abortEvt = OutboxEventFactory.CreateFailedEvent(
                analysis.TenantId,
                analysis.AnalysisId,
                analysis.PatientId,
                message.CorrelationId,
                ErrorCodes.ErrUnauthorized,
                AnalysisStatus.Rejected);

            await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.Rejected,
                JsonSerializer.Serialize(new { error = "ANALYSIS_ABORT", reason = initialAccess.Reason }), abortEvt, cancellationToken);
            await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
            return JobProcessResult.AckFailure;
        }

        // 5. Download original image blob
        byte[] imageBytes;
        try
        {
            imageBytes = await _blobService.DownloadOriginalImageAsync(analysis.OriginalBlobKey, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "Original image missing for analysis {AnalysisId}", analysis.AnalysisId);
            var missingEvt = OutboxEventFactory.CreateFailedEvent(
                analysis.TenantId,
                analysis.AnalysisId,
                analysis.PatientId,
                message.CorrelationId,
                ErrorCodes.ErrImageMissing,
                AnalysisStatus.Failed);

            await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.Failed,
                JsonSerializer.Serialize(new { error = ErrorCodes.ErrImageMissing, message = ex.Message }), missingEvt, cancellationToken);
            await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
            return JobProcessResult.AckFailure;
        }

        // 6. Record durable AI-call intent BEFORE HTTP invocation
        if (_workerOptions.DurableCallIntent)
        {
            await _jobRepo.RecordCallIntentAsync(job.JobId, cancellationToken);
        }

        // 7. Invoke AI REST API
        var aiRequest = new AiAnalyzeRequest
        {
            RequestId = analysis.AnalysisId,
            PatientId = null,
            EyeSide = analysis.EyeSide,
            Mode = "retina_vessels",
            Modality = "FUNDUS",
            ImageBase64 = Convert.ToBase64String(imageBytes),
            IncludeMask = true,
            IncludeOverlay = true
        };

        var aiResult = await _aiClient.AnalyzeAsync(aiRequest, cancellationToken);

        // 8. Handle AI Errors and Outcomes
        if (!aiResult.IsSuccess)
        {
            // Case 8a: 422 Low Quality Image -> Terminal LOW_QUALITY
            if (aiResult.IsLowQuality)
            {
                _logger.LogWarning("Analysis {AnalysisId} failed due to LOW_QUALITY image", analysis.AnalysisId);
                var lqEvt = OutboxEventFactory.CreateFailedEvent(
                    analysis.TenantId,
                    analysis.AnalysisId,
                    analysis.PatientId,
                    message.CorrelationId,
                    ErrorCodes.ErrImageLowQuality,
                    AnalysisStatus.LowQuality);

                await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.LowQuality,
                    JsonSerializer.Serialize(new { errorCode = ErrorCodes.ErrImageLowQuality, message = aiResult.ErrorMessage }), lqEvt, cancellationToken);
                await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
                return JobProcessResult.AckFailure;
            }

            // Case 8b: Transient AI Error (timeout, 503, network)
            if (aiResult.IsTransientError)
            {
                if (message.Attempt < _workerOptions.MaxAttempts)
                {
                    int nextAttempt = message.Attempt + 1;
                    int delayIndex = Math.Min(message.Attempt - 1, _workerOptions.RetryDelaysSeconds.Length - 1);
                    int delaySeconds = _workerOptions.RetryDelaysSeconds[delayIndex];

                    _logger.LogInformation("Transient error calling AI. Scheduling retry attempt {NextAttempt} after {Delay}s",
                        nextAttempt, delaySeconds);

                    var nextJob = await _jobRepo.CreateNextAttemptJobAsync(job, nextAttempt, cancellationToken);
                    var retryMsg = OutboxEventFactory.CreateRetryJobMessage(
                        analysis.TenantId,
                        analysis.BatchId,
                        analysis.AnalysisId,
                        nextJob.JobId,
                        message.ImageFileId,
                        nextAttempt,
                        message.CorrelationId);

                    await _analysisRepo.ScheduleRetryAsync(analysis.AnalysisId, job.JobId, nextAttempt,
                        TimeSpan.FromSeconds(delaySeconds), retryMsg, cancellationToken);
                    return JobProcessResult.AckSuccess;
                }
                else
                {
                    _logger.LogError("AI calls exhausted after {Attempts} attempts. Moving to terminal FAILED.", message.Attempt);
                    var failEvt = OutboxEventFactory.CreateFailedEvent(
                        analysis.TenantId,
                        analysis.AnalysisId,
                        analysis.PatientId,
                        message.CorrelationId,
                        aiResult.ErrorCode ?? ErrorCodes.ErrAiFailed,
                        AnalysisStatus.Failed);

                    await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.Failed,
                        JsonSerializer.Serialize(new { errorCode = aiResult.ErrorCode ?? ErrorCodes.ErrAiFailed, message = aiResult.ErrorMessage }), failEvt, cancellationToken);
                    await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
                    return JobProcessResult.AckFailure;
                }
            }

            // Case 8c: Non-transient AI Error (400, 401, 403)
            var termEvt = OutboxEventFactory.CreateFailedEvent(
                analysis.TenantId,
                analysis.AnalysisId,
                analysis.PatientId,
                message.CorrelationId,
                aiResult.ErrorCode ?? ErrorCodes.ErrAiFailed,
                AnalysisStatus.Failed);

            await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.Failed,
                JsonSerializer.Serialize(new { errorCode = aiResult.ErrorCode ?? ErrorCodes.ErrAiFailed, message = aiResult.ErrorMessage }), termEvt, cancellationToken);
            await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
            return JobProcessResult.AckFailure;
        }

        // 9. Validate AI Response Structure and Invariants
        var aiResponse = aiResult.Response!;
        var validation = AiResultValidator.Validate(aiResponse);

        if (validation.IsMockHold)
        {
            _logger.LogCritical("HOLD: Mock AI detected in real namespace for analysis {AnalysisId}! Holding for operator investigation.", analysis.AnalysisId);
            return JobProcessResult.HoldInvestigate;
        }

        if (!validation.IsValid)
        {
            _logger.LogError("AI output invalid for analysis {AnalysisId}: {Msg}", analysis.AnalysisId, validation.ErrorMessage);
            var invalidEvt = OutboxEventFactory.CreateFailedEvent(
                analysis.TenantId,
                analysis.AnalysisId,
                analysis.PatientId,
                message.CorrelationId,
                validation.ErrorCode ?? ErrorCodes.ErrAiOutputInvalid,
                AnalysisStatus.Failed);

            await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.Failed,
                JsonSerializer.Serialize(new { errorCode = validation.ErrorCode, message = validation.ErrorMessage }), invalidEvt, cancellationToken);
            await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
            return JobProcessResult.AckFailure;
        }

        // 10. Recheck Profile access BEFORE committing result
        var preCommitAccess = await _profileClient.RecheckAccessAsync(
            analysis.TenantId,
            analysis.PatientId,
            "ANALYSIS_READ",
            cancellationToken);

        if (!preCommitAccess.Allowed)
        {
            _logger.LogWarning("Access revoked right before commit for patient {PatientId}. Aborting commit.", analysis.PatientId);
            var abortEvt = OutboxEventFactory.CreateFailedEvent(
                analysis.TenantId,
                analysis.AnalysisId,
                analysis.PatientId,
                message.CorrelationId,
                ErrorCodes.ErrUnauthorized,
                AnalysisStatus.Rejected);

            await _analysisRepo.CommitFailureAsync(analysis.AnalysisId, job.JobId, AnalysisStatus.Rejected,
                JsonSerializer.Serialize(new { error = "ANALYSIS_ABORT", reason = preCommitAccess.Reason }), abortEvt, cancellationToken);
            await RecordBatchOutcomeAsync(message, analysis, isSuccess: false, cancellationToken);
            return JobProcessResult.AckFailure;
        }

        // 11. Upload mask and overlay to private blob storage
        var maskBytes = Convert.FromBase64String(aiResponse.Segmentation.MaskBase64);
        var overlayBytes = Convert.FromBase64String(aiResponse.Segmentation.OverlayBase64);

        var maskFileId = Guid.NewGuid();
        var overlayFileId = Guid.NewGuid();

        var maskKey = $"{analysis.TenantId}/{analysis.BatchId}/{analysis.AnalysisId}/mask-{maskFileId}.png";
        var overlayKey = $"{analysis.TenantId}/{analysis.BatchId}/{analysis.AnalysisId}/overlay-{overlayFileId}.png";

        await _blobService.UploadAnnotationAsync(maskKey, maskBytes, "image/png", cancellationToken);
        await _blobService.UploadAnnotationAsync(overlayKey, overlayBytes, "image/png", cancellationToken);

        // 12. Build public AnalysisResult shape (camelCase per OpenAPI 0.7.0)
        var resultDto = new
        {
            riskAvailable = false,
            riskLevel = (string?)null,
            riskScore = (double?)null,
            confidenceScore = (double?)null,
            isMock = false,
            modelVersion = aiResponse.ModelVersion,
            configVersion = aiResponse.ConfigVersion,
            thresholdVersion = aiResponse.ThresholdVersion,
            disclaimer = "Kết quả phân vùng nghiên cứu, không phải chẩn đoán.",
            version = 1,
            metrics = new
            {
                vesselDensity = aiResponse.Metrics.VesselDensity,
                tortuosityIndex = aiResponse.Metrics.TortuosityIndex,
                avRatio = aiResponse.Metrics.AvRatio,
                fractalDimension = aiResponse.Metrics.FractalDimension,
                branchingPoints = aiResponse.Metrics.BranchingPoints
            },
            indicators = new string[0],
            annotations = new[]
            {
                new { fileId = maskFileId, kind = "mask" },
                new { fileId = overlayFileId, kind = "overlay" }
            },
            warnings = aiResponse.Warnings.Select(w => new { code = w.Code, message = w.Message }).ToArray(),
            processingTimeMs = aiResponse.ProcessingTimeMs,
            limitations = aiResponse.Limitations
        };

        var resultJson = JsonSerializer.Serialize(resultDto);

        // 13. Create outbox domain event 0.7 (analysis.completed)
        var completedEvt = OutboxEventFactory.CreateCompletedEvent(
            analysis.TenantId,
            analysis.AnalysisId,
            analysis.PatientId,
            message.CorrelationId,
            aiResponse.ModelVersion,
            aiResponse.ConfigVersion,
            aiResponse.ThresholdVersion);

        // 14. Commit Result + Analysis status + Job status + Outbox in SINGLE transaction
        await _analysisRepo.CommitSuccessAsync(
            analysis.AnalysisId,
            job.JobId,
            resultJson,
            completedEvt,
            cancellationToken);

        await RecordBatchOutcomeAsync(message, analysis, isSuccess: true, cancellationToken);
        _logger.LogInformation("Successfully processed and committed analysis {AnalysisId}", analysis.AnalysisId);

        return JobProcessResult.AckSuccess;
    }

    private async Task RecordBatchOutcomeAsync(JobRunMessage message, AnalysisRecord analysis, bool isSuccess, CancellationToken cancellationToken)
    {
        BatchImageCompletionResult? result = null;
        try
        {
            result = await _batchProgress.RecordImageOutcomeAsync(analysis.BatchId, analysis.AnalysisId, isSuccess);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis batch progress update failed for batch {BatchId}, falling back to DB batch completion check", analysis.BatchId);
        }

        bool isBatchFinished = result?.IsBatchFinished ?? false;
        string batchStatus = result?.BatchStatus ?? "IN_PROGRESS";
        int completed = result?.Completed ?? 0;
        int failed = result?.Failed ?? 0;
        int total = result?.Total ?? 0;

        if (!isBatchFinished)
        {
            var dbBatchStatus = await _analysisRepo.CheckBatchCompletionAsync(analysis.BatchId, cancellationToken);
            if (dbBatchStatus is { IsFinished: true })
            {
                isBatchFinished = true;
                batchStatus = dbBatchStatus.BatchStatus;
                completed = dbBatchStatus.Completed;
                failed = dbBatchStatus.Failed;
                total = dbBatchStatus.Total;
            }
        }

        if (isBatchFinished)
        {
            _logger.LogInformation("Batch {BatchId} finished: status={Status}, completed={Completed}, failed={Failed}",
                analysis.BatchId, batchStatus, completed, failed);
            var finalResult = new BatchImageCompletionResult(analysis.BatchId, total, completed, failed, 0, true, batchStatus);
            await _batchEvents.PublishAsync(analysis.TenantId, analysis.BatchId, analysis.PatientId,
                message.CorrelationId, finalResult, cancellationToken);
        }
    }
}
