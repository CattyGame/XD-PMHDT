using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aura.Worker.Analysis.Data.Entities;

[Table("jobs", Schema = "analysis")]
public class JobRecord
{
    [Key]
    [Column("id")]
    public Guid JobId { get; set; }

    [Column("image_id")]
    public Guid AnalysisId { get; set; }

    [NotMapped]
    public Guid BatchId { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [NotMapped]
    public Guid ImageFileId { get; set; }

    [Column("message_id")]
    public Guid MessageId { get; set; } = Guid.NewGuid();

    [Column("attempt_no")]
    public int AttemptNo { get; set; }

    [Column("status")]
    public string Status { get; set; } = "QUEUED";

    [Column("lease_owner")]
    public string? LeaseOwner { get; set; }

    [Column("lease_expires_at")]
    public DateTime? LeaseExpiresAt { get; set; }

    [Column("fencing_token")]
    public long FencingToken { get; set; } = 0;

    [Column("call_intent_recorded")]
    public bool IntentRecorded { get; set; } = false;

    [Column("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;

    [Column("started_at")]
    public DateTime? StartedAt { get; set; }

    [Column("finished_at")]
    public DateTime? FinishedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("images", Schema = "analysis")]
public class AnalysisRecord
{
    [Key]
    [Column("id")]
    public Guid AnalysisId { get; set; }

    [Column("analysis_request_id")]
    public Guid BatchId { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("file_id")]
    public Guid FileId { get; set; }

    [NotMapped]
    public Guid PatientId { get; set; }

    [Column("eye_side")]
    public string EyeSide { get; set; } = "unknown";

    [Column("processing_status")]
    public string ProcessingStatus { get; set; } = "QUEUED";

    [Column("review_status")]
    public string ReviewStatus { get; set; } = "NOT_REQUIRED";

    [Column("object_key")]
    public string OriginalBlobKey { get; set; } = string.Empty;

    [Column("width")]
    public int Width { get; set; } = 512;

    [Column("height")]
    public int Height { get; set; } = 512;

    [NotMapped]
    public string? ResultJson { get; set; }

    [NotMapped]
    public string? ErrorJson { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Table("analysis_requests", Schema = "analysis")]
public class AnalysisRequestRecord
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("patient_id")]
    public Guid PatientId { get; set; }
}

[Table("outbox_events", Schema = "analysis")]
public class OutboxRecord
{
    [Key]
    [Column("id")]
    public Guid OutboxId { get; set; } = Guid.NewGuid();

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    [Column("analysis_request_id")]
    public Guid BatchId { get; set; }

    [Column("image_id")]
    public Guid AnalysisId { get; set; }

    [NotMapped]
    public Guid EventId { get; set; }

    [Column("event_type")]
    public string EventType { get; set; } = string.Empty;

    [Column("schema_version")]
    public string SchemaVersion { get; set; } = "0.6.0";

    [Column("payload", TypeName = "jsonb")]
    public string PayloadJson { get; set; } = string.Empty;

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [Column("published_at")]
    public DateTime? PublishedAt { get; set; }

    [Column("delivery_attempts")]
    public int AttemptCount { get; set; } = 0;

    [NotMapped]
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public string Status { get; set; } = "PENDING";

    [Column("claim_generation")]
    public long ClaimGeneration { get; set; } = 0;
}
