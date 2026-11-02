using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace AURA.Analysis.Api.Upload;

/// <summary>Persists accepted uploads atomically. Does not run migrations or create/alter roles.</summary>
public sealed class PostgresUploadRepository(NpgsqlDataSource dataSource) : IUploadRepository
{
    private const string FingerprintVersion = RequestFingerprint.Version;

    public async Task<IUploadSession> BeginAsync(IdempotencyScope scope, string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (scope.TenantId == Guid.Empty || scope.RequesterId == Guid.Empty || scope.Key == Guid.Empty || !ValidHash(fingerprint))
            throw new ArgumentException("Invalid upload idempotency scope or fingerprint.");
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await dataSource.OpenConnectionAsync(ct);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await TryLockAsync(connection, transaction,
                $"aura-upload-v1:{scope.TenantId:D}:{scope.RequesterId:D}:{scope.Key:D}", ct);
            await using var command = new NpgsqlCommand("""
                SELECT id, request_fingerprint, accepted_response::text, request_schema_version
                FROM analysis.analysis_requests
                WHERE tenant_id=$1 AND requester_id=$2 AND idempotency_key=$3;
                """, connection, transaction);
            command.Parameters.AddWithValue(scope.TenantId);
            command.Parameters.AddWithValue(scope.RequesterId);
            command.Parameters.AddWithValue(scope.Key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var state = IdempotencyState.New;
            AcceptedResponse? stored = null;
            if (await reader.ReadAsync(ct))
            {
                state = reader.GetString(1) == fingerprint ? IdempotencyState.Replay : IdempotencyState.Conflict;
                if (state == IdempotencyState.Replay)
                {
                    if (reader.GetString(3) != FingerprintVersion) throw UploadFailure.Dependency("Analysis database");
                    try { stored = JsonSerializer.Deserialize<AcceptedResponse>(reader.GetString(2), UploadJson.Options); }
                    catch (JsonException) { throw UploadFailure.Dependency("Analysis database"); }
                    if (stored is null || stored.BatchId != reader.GetGuid(0) || stored.Items is null || stored.Items.Count == 0
                        || stored.StatusUrl != $"/api/v1/batches/{stored.BatchId:D}" || stored.CreatedAt == default
                        || stored.Items.Any(i => i is null || i.AnalysisId == Guid.Empty))
                        throw UploadFailure.Dependency("Analysis database");
                }
            }
            return new Session(connection, transaction, scope, fingerprint, state, stored);
        }
        catch
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { if (connection is not null) await connection.DisposeAsync(); }
            throw;
        }
    }

    private static bool ValidHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task TryLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope, CancellationToken ct)
    {
        // Namespace and full scope are hashed consistently across processes; a collision only adds contention.
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1);", connection, transaction);
        command.Parameters.AddWithValue(key);
        if (await command.ExecuteScalarAsync(ct) is not true)
            throw new UploadFailure(409, "ERR_REQUEST_IN_PROGRESS", "Yêu cầu hoặc ảnh đang được xử lý. Gửi lại cùng key.", true);
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction, IdempotencyScope scope,
        string fingerprint, IdempotencyState state, AcceptedResponse? stored) : IUploadSession
    {
        private bool _disposed;
        private bool _commitAttempted;
        public IdempotencyState State { get; } = state;
        public AcceptedResponse? StoredResponse { get; } = stored;

        private void RequireNew()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != IdempotencyState.New || _commitAttempted)
                throw new InvalidOperationException("This upload session cannot perform another write operation.");
        }

        public async Task<Guid?> FindExistingAnalysisAsync(Guid patientId, string modality, string eyeSide, string sha256, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequireNew();
            if (patientId == Guid.Empty || modality != "FUNDUS" || eyeSide is not ("left" or "right" or "unknown") || !ValidHash(sha256))
                throw new ArgumentException("Invalid dedupe scope.");
            // Nonblocking locks avoid deadlocks when two batches list the same images in a different order.
            await TryLockAsync(connection, transaction,
                $"aura-dedupe-v1:{scope.TenantId:D}:{patientId:D}:{modality}:{eyeSide}:{sha256}", ct);
            await using var command = new NpgsqlCommand("""
                SELECT i.id
                FROM analysis.images i
                JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
                WHERE i.tenant_id=$1 AND r.patient_id=$2 AND i.modality=$3 AND i.eye_side=$4
                  AND i.sha256=$5 AND i.processing_status <> 'DUPLICATE'
                ORDER BY i.created_at, i.id
                LIMIT 1;
                """, connection, transaction);
            command.Parameters.AddWithValue(scope.TenantId);
            command.Parameters.AddWithValue(patientId);
            command.Parameters.AddWithValue(modality);
            command.Parameters.AddWithValue(eyeSide);
            command.Parameters.AddWithValue(sha256);
            return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
        }

        public async Task CommitAsync(BatchPlan plan, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequireNew();
            if (plan.TenantId != scope.TenantId || plan.RequesterId != scope.RequesterId || plan.IdempotencyKey != scope.Key
                || plan.Fingerprint != fingerprint || plan.FingerprintVersion != RequestFingerprint.Version
                || plan.BatchId == Guid.Empty || plan.PatientId == Guid.Empty || plan.OwnerId == Guid.Empty
                || plan.Response is null || plan.Response.BatchId != plan.BatchId || plan.Images is null || plan.Images.Count == 0
                || plan.Response.Items is null || plan.Images.Count != plan.Response.Items.Count)
                throw new ArgumentException("Batch plan does not match its locked upload scope.");
            for (var i = 0; i < plan.Images.Count; i++)
                if (plan.Images[i].AnalysisId != plan.Response.Items[i].AnalysisId)
                    throw new ArgumentException("Batch response does not match its image plan.");
            _commitAttempted = true;
            if (plan.RecoveryAttemptId is not null)
                await PostgresUploadRecoveryRepository.FenceAcceptanceAsync(connection, transaction, plan, ct);
            else
                await PostgresUploadRecoveryRepository.RejectUnjournaledCommitAsync(connection, transaction, plan, ct);
            await using (var command = new NpgsqlCommand("""
                INSERT INTO analysis.batches
                    (id,tenant_id,owner_id,requester_id,patient_id,clinic_id,total_images,completed_images,
                     failed_images,status,created_at,updated_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$11);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(plan.BatchId);
                command.Parameters.AddWithValue(plan.TenantId);
                command.Parameters.AddWithValue(plan.OwnerId);
                command.Parameters.AddWithValue(plan.RequesterId);
                command.Parameters.AddWithValue(plan.PatientId);
                command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)plan.ClinicId ?? DBNull.Value);
                var initiallyCompleted = plan.Images.Count(i => i.ProcessingStatus is "COMPLETED" or "DUPLICATE");
                var initiallyFailed = plan.Images.Count(i => i.ProcessingStatus is "FAILED" or "LOW_QUALITY" or "REJECTED");
                var initiallyActive = plan.Images.Count - initiallyCompleted - initiallyFailed;
                command.Parameters.AddWithValue(plan.Images.Count);
                command.Parameters.AddWithValue(initiallyCompleted);
                command.Parameters.AddWithValue(initiallyFailed);
                command.Parameters.AddWithValue(initiallyActive > 0 ? "IN_PROGRESS"
                    : initiallyFailed == 0 ? "COMPLETED"
                    : initiallyCompleted == 0 ? "FAILED" : "PARTIAL_COMPLETED");
                command.Parameters.AddWithValue(plan.CreatedAt.UtcDateTime);
                await command.ExecuteNonQueryAsync(ct);
            }
            await using (var command = new NpgsqlCommand("""
                INSERT INTO analysis.analysis_requests
                    (id,tenant_id,owner_id,requester_id,patient_id,idempotency_key,request_fingerprint,
                     request_schema_version,accepted_response,correlation_id,created_at,updated_at)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$11);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue(plan.BatchId);
                command.Parameters.AddWithValue(plan.TenantId);
                command.Parameters.AddWithValue(plan.OwnerId);
                command.Parameters.AddWithValue(plan.RequesterId);
                command.Parameters.AddWithValue(plan.PatientId);
                command.Parameters.AddWithValue(plan.IdempotencyKey);
                command.Parameters.AddWithValue(plan.Fingerprint);
                command.Parameters.AddWithValue(plan.FingerprintVersion);
                command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(plan.Response, UploadJson.Options));
                command.Parameters.AddWithValue(plan.CorrelationId);
                command.Parameters.AddWithValue(plan.CreatedAt.UtcDateTime);
                await command.ExecuteNonQueryAsync(ct);
            }
            foreach (var image in plan.Images)
            {
                await using (var command = new NpgsqlCommand("""
                    INSERT INTO analysis.images
                      (id,tenant_id,analysis_request_id,batch_id,file_id,modality,eye_side,object_key,mime_type,
                       size_bytes,width,height,sha256,processing_status,existing_analysis_id,created_at,updated_at)
                    VALUES ($1,$2,$3,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$15);
                    """, connection, transaction))
                {
                    command.Parameters.AddWithValue(image.AnalysisId);
                    command.Parameters.AddWithValue(plan.TenantId);
                    command.Parameters.AddWithValue(plan.BatchId);
                    command.Parameters.AddWithValue(image.FileId);
                    command.Parameters.AddWithValue(image.Modality);
                    command.Parameters.AddWithValue(image.EyeSide);
                    command.Parameters.AddWithValue(image.ObjectKey);
                    command.Parameters.AddWithValue(image.MimeType);
                    command.Parameters.AddWithValue(image.SizeBytes);
                    command.Parameters.AddWithValue(image.Width);
                    command.Parameters.AddWithValue(image.Height);
                    command.Parameters.AddWithValue(image.Sha256);
                    command.Parameters.AddWithValue(image.ProcessingStatus);
                    command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)image.ExistingAnalysisId ?? DBNull.Value);
                    command.Parameters.AddWithValue(plan.CreatedAt.UtcDateTime);
                    await command.ExecuteNonQueryAsync(ct);
                }
                if (image.Job is { } job)
                {
                    await using var command = new NpgsqlCommand("""
                        INSERT INTO analysis.jobs (id,tenant_id,image_id,message_id,attempt_no,status,correlation_id,created_at,updated_at)
                        VALUES ($1,$2,$3,$4,1,'QUEUED',$5,$6,$6);
                        """, connection, transaction);
                    command.Parameters.AddWithValue(job.JobId);
                    command.Parameters.AddWithValue(plan.TenantId);
                    command.Parameters.AddWithValue(image.AnalysisId);
                    command.Parameters.AddWithValue(job.MessageId);
                    command.Parameters.AddWithValue(job.CorrelationId);
                    command.Parameters.AddWithValue(plan.CreatedAt.UtcDateTime);
                    await command.ExecuteNonQueryAsync(ct);
                }
            }
            foreach (var item in plan.Outbox)
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO analysis.outbox_events
                       (id,tenant_id,analysis_request_id,image_id,event_type,schema_version,payload,occurred_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8);
                    """, connection, transaction);
                command.Parameters.AddWithValue(item.EventId);
                command.Parameters.AddWithValue(plan.TenantId);
                command.Parameters.AddWithValue(plan.BatchId);
                command.Parameters.AddWithValue(item.ImageId);
                command.Parameters.AddWithValue(item.EventType);
                command.Parameters.AddWithValue(item.SchemaVersion);
                command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, item.PayloadJson);
                command.Parameters.AddWithValue(item.OccurredAt.UtcDateTime);
                await command.ExecuteNonQueryAsync(ct);
            }
            // No external side effect is compensated here: a failed/lost COMMIT acknowledgement may be ambiguous.
            await transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await transaction.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }
}
