namespace AURA.Analysis.Api.Upload;

// ---- Đầu vào ----
public interface IUploadedFile
{
    string FileName { get; }
    string? ContentType { get; }
    long Length { get; }
    /// <summary>Stream phải hỗ trợ seek (IFormFile đã bộ đệm thỏa điều kiện).</summary>
    Stream OpenReadStream();
}

/// <param name="RequesterId">Principal từ token đã xác thực; không lấy từ body.</param>
public sealed record UploadCommand(
    Guid RequesterId,
    Guid? ClinicId,
    Guid IdempotencyKey,
    string? CorrelationId,
    string? PatientId,
    string? Modality,
    string? EyeSide,
    IReadOnlyList<IUploadedFile> Files);

// ---- Profile (M6): POST /internal/v1/profile/access/resolve ----
public sealed record PatientAccessRequest(
    Guid RequesterId,
    Guid? ClinicId,
    Guid PatientId,
    string Action,
    Guid? AnalysisId = null)
{
    public string? CorrelationId { get; init; }
}

public sealed record AccessDecision(bool Allowed, Guid TenantId, string TenantKind, Guid OwnerPrincipalId,
    string BillingOwner, long PermissionVersion);

public interface IPatientAccessResolver
{
    Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct);
}

// ---- Billing (M3): /internal/v1/billing/reservations ----
public sealed record ReserveCredit(Guid TenantId, string BillingOwner, Guid AnalysisId, Guid IdempotencyKey)
{
    public Guid ActorAccountId { get; init; }
    public Guid PatientId { get; init; }
    public int Generation { get; init; } = 1;
    public string? CorrelationId { get; init; }
}
public sealed record ReleaseCredit(Guid TenantId, Guid ReservationId, Guid IdempotencyKey)
{
    public string Reason { get; init; } = "ANALYSIS_ABORT";
    public string? CorrelationId { get; init; }
}
public enum ReservationStatus { Reserved, InsufficientCredit }
public sealed record ReservationOutcome(ReservationStatus Status, Guid? ReservationId);

public interface ICreditReservations
{
    Task<ReservationOutcome> ReserveAsync(ReserveCredit request, CancellationToken ct);
    Task ReleaseAsync(ReleaseCredit request, CancellationToken ct);
}

// ---- Private storage (M1): Azurite/Azure Blob ----
public sealed record FileReadAccess(Uri Url, DateTimeOffset ExpiresAt);

public interface IPrivateFileStorage
{
    Task PutAsync(string container, string key, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string container, string key, CancellationToken ct);
    Task DeleteAsync(string container, string key, CancellationToken ct);
    Task<FileReadAccess> CreateReadAccessAsync(string container, string key, TimeSpan ttl, CancellationToken ct);
}

// ---- Persistence (Analysis DB, schema analysis) ----
public sealed record IdempotencyScope(Guid TenantId, Guid RequesterId, Guid Key);

public enum IdempotencyState { New, Replay, Conflict }

public sealed record AcceptedItem(Guid AnalysisId, string FileName, string ProcessingStatus, ItemError? Error,
    Guid? ExistingAnalysisId = null);
public sealed record ItemError(string Code, string Message, bool Retryable);
public sealed record AcceptedResponse(Guid BatchId, IReadOnlyList<AcceptedItem> Items, DateTime CreatedAt, string StatusUrl);

public sealed record JobPlan(Guid JobId, Guid MessageId, string CorrelationId);

public sealed record ImagePlan(
    Guid AnalysisId, Guid FileId, string ObjectKey, string FileName, string MimeType, long SizeBytes,
    int Width, int Height, string Sha256, string Modality, string EyeSide,
    string ProcessingStatus, Guid? ExistingAnalysisId, JobPlan? Job);

public sealed record OutboxPlan(Guid EventId, Guid ImageId, string EventType, string SchemaVersion,
    string PayloadJson, DateTimeOffset OccurredAt);

public sealed record BatchPlan(
    Guid BatchId, Guid TenantId, Guid OwnerId, Guid RequesterId, Guid PatientId, Guid IdempotencyKey,
    string Fingerprint, string FingerprintVersion, string CorrelationId, DateTimeOffset CreatedAt,
    AcceptedResponse Response, IReadOnlyList<ImagePlan> Images, IReadOnlyList<OutboxPlan> Outbox)
{
    /// <summary>Internal durable-upload fence; absent only for legacy/domain-only callers.</summary>
    public Guid? RecoveryAttemptId { get; init; }
}

/// <summary>Một phiên giữ khóa idempotency (vd. pg advisory lock) cho tới khi Dispose.</summary>
public interface IUploadSession : IAsyncDisposable
{
    IdempotencyState State { get; }
    AcceptedResponse? StoredResponse { get; }
    /// <summary>Ảnh cùng tenant + patient + modality + eyeSide + checksum đã tồn tại.</summary>
    Task<Guid?> FindExistingAnalysisAsync(Guid patientId, string modality, string eyeSide, string sha256, CancellationToken ct);
    /// <summary>Ghi batch, image, job, outbox và phản hồi idempotency trong MỘT transaction.</summary>
    Task CommitAsync(BatchPlan plan, CancellationToken ct);
}

public interface IUploadRepository
{
    /// <summary>Ném UploadFailure 409 ERR_REQUEST_IN_PROGRESS nếu cùng khóa đang được xử lý.</summary>
    Task<IUploadSession> BeginAsync(IdempotencyScope scope, string fingerprint, CancellationToken ct);
}

public sealed record UploadResult(AcceptedResponse Response, bool Replayed);
