using System.Text.Json.Serialization;

namespace Aura.Worker.Analysis.Messaging;

public class DomainEventEnvelope<TData>
{
    [JsonPropertyName("eventId")]
    public Guid EventId { get; set; }

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "0.8";

    [JsonPropertyName("occurredAt")]
    public string OccurredAt { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("analysisId")]
    public Guid AnalysisId { get; set; }

    [JsonPropertyName("data")]
    public TData Data { get; set; } = default!;
}

public class AnalysisCompletedData
{
    [JsonPropertyName("patientId")]
    public Guid PatientId { get; set; }

    [JsonPropertyName("riskLevel")]
    public string? RiskLevel { get; set; }

    [JsonPropertyName("modelVersion")]
    public string ModelVersion { get; set; } = "frangi-v0.2";

    [JsonPropertyName("isMock")]
    public bool IsMock { get; set; } = false;

    [JsonPropertyName("configVersion")]
    public string ConfigVersion { get; set; } = "s3-frangi-1.0.0";

    [JsonPropertyName("thresholdVersion")]
    public string ThresholdVersion { get; set; } = "frangi-threshold-0.02-v1";

    [JsonPropertyName("resultVersion")]
    public int ResultVersion { get; set; } = 1;

    [JsonPropertyName("riskAvailable")]
    public bool RiskAvailable { get; set; } = false;
}

public class AnalysisFailedData
{
    [JsonPropertyName("patientId")]
    public Guid PatientId { get; set; }

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; } = false;

    [JsonPropertyName("terminalStatus")]
    public string TerminalStatus { get; set; } = "FAILED";
}

public class BatchCompletedData
{
    [JsonPropertyName("batchId")]
    public Guid BatchId { get; set; }

    [JsonPropertyName("patientId")]
    public Guid PatientId { get; set; }

    [JsonPropertyName("batchStatus")]
    public string BatchStatus { get; set; } = "COMPLETED";

    [JsonPropertyName("totalImages")]
    public int TotalImages { get; set; }

    [JsonPropertyName("completedImages")]
    public int CompletedImages { get; set; }

    [JsonPropertyName("failedImages")]
    public int FailedImages { get; set; }
}
