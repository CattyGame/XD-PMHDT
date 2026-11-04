using System.Text.Json;
using Aura.Worker.Analysis.Data.Entities;
using Aura.Worker.Analysis.Messaging;

namespace Aura.Worker.Analysis.Processing;

public static class OutboxEventFactory
{
    public static OutboxRecord CreateCompletedEvent(
        Guid tenantId,
        Guid analysisId,
        Guid patientId,
        string correlationId,
        string modelVersion,
        string configVersion,
        string thresholdVersion)
    {
        var evt = new DomainEventEnvelope<AnalysisCompletedData>
        {
            EventId = Guid.NewGuid(),
            EventType = "analysis.completed",
            SchemaVersion = "0.7",
            OccurredAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            TenantId = tenantId,
            CorrelationId = correlationId,
            AnalysisId = analysisId,
            Data = new AnalysisCompletedData
            {
                PatientId = patientId,
                RiskLevel = null,
                RiskAvailable = false,
                ModelVersion = modelVersion,
                IsMock = false,
                ConfigVersion = configVersion,
                ThresholdVersion = thresholdVersion,
                ResultVersion = 1
            }
        };

        return new OutboxRecord
        {
            OutboxId = evt.EventId,
            EventId = evt.EventId,
            TenantId = tenantId,
            SchemaVersion = evt.SchemaVersion,
            OccurredAt = DateTime.Parse(evt.OccurredAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            EventType = evt.EventType,
            PayloadJson = JsonSerializer.Serialize(evt),
            AvailableAt = DateTime.UtcNow,
            Status = "PENDING"
        };
    }

    public static OutboxRecord CreateFailedEvent(
        Guid tenantId,
        Guid analysisId,
        Guid patientId,
        string correlationId,
        string errorCode,
        string terminalStatus)
    {
        var evt = new DomainEventEnvelope<AnalysisFailedData>
        {
            EventId = Guid.NewGuid(),
            EventType = "analysis.failed",
            SchemaVersion = "0.7",
            OccurredAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            TenantId = tenantId,
            CorrelationId = correlationId,
            AnalysisId = analysisId,
            Data = new AnalysisFailedData
            {
                PatientId = patientId,
                ErrorCode = errorCode,
                Retryable = false,
                TerminalStatus = terminalStatus
            }
        };

        return new OutboxRecord
        {
            OutboxId = evt.EventId,
            EventId = evt.EventId,
            TenantId = tenantId,
            SchemaVersion = evt.SchemaVersion,
            OccurredAt = DateTime.Parse(evt.OccurredAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            EventType = evt.EventType,
            PayloadJson = JsonSerializer.Serialize(evt),
            AvailableAt = DateTime.UtcNow,
            Status = "PENDING"
        };
    }

    public static OutboxRecord CreateBatchCompletedEvent(
        Guid tenantId,
        Guid batchId,
        Guid patientId,
        string correlationId,
        string batchStatus,
        int totalImages,
        int completedImages,
        int failedImages)
    {
        var evt = new DomainEventEnvelope<BatchCompletedData>
        {
            EventId = batchId,
            EventType = "batch.completed",
            SchemaVersion = "0.7",
            OccurredAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            TenantId = tenantId,
            CorrelationId = correlationId,
            AnalysisId = batchId,
            Data = new BatchCompletedData
            {
                BatchId = batchId,
                PatientId = patientId,
                BatchStatus = batchStatus,
                TotalImages = totalImages,
                CompletedImages = completedImages,
                FailedImages = failedImages
            }
        };

        return new OutboxRecord
        {
            OutboxId = evt.EventId,
            EventId = evt.EventId,
            TenantId = tenantId,
            SchemaVersion = evt.SchemaVersion,
            OccurredAt = DateTime.Parse(evt.OccurredAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            EventType = evt.EventType,
            PayloadJson = JsonSerializer.Serialize(evt),
            AvailableAt = DateTime.UtcNow,
            Status = "PENDING"
        };
    }

    public static OutboxRecord CreateRetryJobMessage(
        Guid tenantId,
        Guid batchId,
        Guid analysisId,
        Guid newJobId,
        Guid imageFileId,
        int attempt,
        string correlationId,
        Guid? messageId = null)
    {
        var msg = new JobRunMessage
        {
            MessageId = messageId ?? Guid.NewGuid(),
            MessageType = "analysis.job.run",
            SchemaVersion = "0.6",
            OccurredAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            TenantId = tenantId,
            CorrelationId = correlationId,
            JobId = newJobId,
            AnalysisId = analysisId,
            BatchId = batchId,
            ImageFileId = imageFileId,
            Attempt = attempt
        };

        return new OutboxRecord
        {
            OutboxId = msg.MessageId,
            EventId = msg.MessageId,
            TenantId = tenantId,
            SchemaVersion = msg.SchemaVersion,
            EventType = msg.MessageType,
            PayloadJson = JsonSerializer.Serialize(msg),
            Status = "PENDING"
        };
    }
}
