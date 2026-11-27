using System.Security.Cryptography;
using System.Text;
using AURA.Common.Audit;
using Npgsql;

namespace AURA.Analysis.Api.Upload;

public enum AiConfigCommandStatus
{
    Pending,
    Committed,
    Failed
}

public sealed record AiConfigCommandRequest(
    Guid ActorAccountId,
    string Action,
    int ExpectedVersion,
    Guid? ArtifactManifestId,
    string? ThresholdConfigSha256,
    string Reason,
    string IdempotencyKey,
    string PayloadFingerprint,
    string CorrelationId);

public sealed record AiConfigCommandRecord(
    Guid CommandId,
    string IdempotencyKey,
    string Action,
    Guid ActorAccountId,
    int ExpectedVersion,
    Guid? ArtifactManifestId,
    string? ThresholdConfigSha256,
    string Reason,
    string PayloadFingerprint,
    string CorrelationId,
    AiConfigCommandStatus Status,
    AiConfigView? Receipt,
    string? FailureCode,
    DateTimeOffset CreatedAt = default);

public interface IAiConfigCommandJournal
{
    Task<AiConfigCommandRecord> BeginOrLoadAsync(AiConfigCommandRequest request, CancellationToken ct);
    Task CommitWithAuditAsync(AiConfigCommandRecord command, AiConfigView receipt, CancellationToken ct);
    Task MarkFailedAsync(Guid commandId, string failureCode, CancellationToken ct);
    Task<IReadOnlyList<AiConfigCommandRecord>> ListPendingAsync(int limit, CancellationToken ct);
}

public static class AiConfigCommandJournalHelpers
{
    public static string ComputeFingerprint(
        Guid actorAccountId,
        string action,
        int expectedVersion,
        Guid? artifactManifestId,
        string? sha256,
        string reason)
    {
        var canonical = $"{actorAccountId:D}|{action}|{expectedVersion}|{artifactManifestId?.ToString("D") ?? ""}|{sha256 ?? ""}|{reason.Trim()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string DeriveStableIdempotencyKey(string payloadFingerprint) =>
        $"auto-{payloadFingerprint[..32]}";

    public static AuditEvent BuildDeterministicAuditEvent(AiConfigCommandRecord command, AiConfigView receipt)
    {
        var ev = new AuditEvent(
            EventId: command.CommandId,
            SchemaVersion: 1,
            Source: AuditSource.Analysis,
            ActorAccountId: command.ActorAccountId,
            TenantId: null,
            Action: AuditAction.AiConfigChanged,
            ResourceType: AuditResource.AiConfig,
            ResourceId: receipt.ArtifactManifestId.ToString("D"),
            ResourceVersion: receipt.Version,
            CorrelationId: AuditEvent.Correlation(command.CorrelationId),
            Outcome: AuditOutcome.Succeeded,
            OccurredAt: receipt.ActivatedAt.ToUniversalTime());
        AuditEvent.Validate(ev);
        return ev;
    }

    public static string FormatStatus(AiConfigCommandStatus status) => status switch
    {
        AiConfigCommandStatus.Pending => "PENDING",
        AiConfigCommandStatus.Committed => "COMMITTED",
        AiConfigCommandStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static AiConfigCommandStatus ParseStatus(string value) => value switch
    {
        "PENDING" => AiConfigCommandStatus.Pending,
        "COMMITTED" => AiConfigCommandStatus.Committed,
        "FAILED" => AiConfigCommandStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown command status: {value}")
    };
}

public sealed class PostgresAiConfigCommandJournal(NpgsqlDataSource db) : IAiConfigCommandJournal
{
    public async Task<AiConfigCommandRecord> BeginOrLoadAsync(AiConfigCommandRequest request, CancellationToken ct)
    {
        var commandId = Guid.NewGuid();
        var correlation = string.IsNullOrWhiteSpace(request.CorrelationId) ? Guid.NewGuid().ToString("D") : request.CorrelationId.Trim();
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO analysis.ai_config_commands (
                command_id, idempotency_key, action, actor_account_id, expected_version,
                artifact_manifest_id, threshold_config_sha256, reason, payload_fingerprint,
                correlation_id, status
            )
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'PENDING')
            ON CONFLICT (idempotency_key) DO NOTHING;
            """, conn, tx))
        {
            insert.Parameters.AddWithValue(commandId);
            insert.Parameters.AddWithValue(request.IdempotencyKey);
            insert.Parameters.AddWithValue(request.Action);
            insert.Parameters.AddWithValue(request.ActorAccountId);
            insert.Parameters.AddWithValue(request.ExpectedVersion);
            insert.Parameters.AddWithValue((object?)request.ArtifactManifestId ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)request.ThresholdConfigSha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue(request.Reason);
            insert.Parameters.AddWithValue(request.PayloadFingerprint);
            insert.Parameters.AddWithValue(correlation);
            await insert.ExecuteNonQueryAsync(ct);
        }

        AiConfigCommandRecord existing;
        await using (var select = new NpgsqlCommand("""
            SELECT command_id, idempotency_key, action, actor_account_id, expected_version,
                   artifact_manifest_id, threshold_config_sha256, reason, payload_fingerprint,
                   correlation_id, status, committed_version, committed_manifest_id,
                   committed_sha256, committed_activated_at, failure_code, created_at
            FROM analysis.ai_config_commands
            WHERE idempotency_key = $1
            FOR UPDATE;
            """, conn, tx))
        {
            select.Parameters.AddWithValue(request.IdempotencyKey);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Failed to load inserted/existing AI config command.");
            existing = ReadRecord(reader);
        }

        if (!string.Equals(existing.PayloadFingerprint, request.PayloadFingerprint, StringComparison.Ordinal))
        {
            throw new UploadFailure(409, "ERR_IDEMPOTENCY_CONFLICT",
                "Idempotency-Key đã được sử dụng cho payload cấu hình AI khác.");
        }

        if (existing.Status == AiConfigCommandStatus.Failed)
        {
            await using var reopen = new NpgsqlCommand("""
                UPDATE analysis.ai_config_commands
                SET status = 'PENDING', failure_code = NULL, updated_at = now()
                WHERE command_id = $1 AND status = 'FAILED';
                """, conn, tx);
            reopen.Parameters.AddWithValue(existing.CommandId);
            await reopen.ExecuteNonQueryAsync(ct);
            existing = existing with { Status = AiConfigCommandStatus.Pending, FailureCode = null };
        }

        await tx.CommitAsync(ct);
        return existing;
    }

    public async Task CommitWithAuditAsync(AiConfigCommandRecord command, AiConfigView receipt, CancellationToken ct)
    {
        var audit = AiConfigCommandJournalHelpers.BuildDeterministicAuditEvent(command, receipt);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var update = new NpgsqlCommand("""
            UPDATE analysis.ai_config_commands
            SET status = 'COMMITTED',
                committed_version = $2,
                committed_manifest_id = $3,
                committed_sha256 = $4,
                committed_activated_at = $5,
                failure_code = NULL,
                updated_at = now()
            WHERE command_id = $1;
            """, conn, tx))
        {
            update.Parameters.AddWithValue(command.CommandId);
            update.Parameters.AddWithValue(receipt.Version);
            update.Parameters.AddWithValue(receipt.ArtifactManifestId);
            update.Parameters.AddWithValue(receipt.ThresholdConfigSha256);
            update.Parameters.AddWithValue(receipt.ActivatedAt.ToUniversalTime());
            await update.ExecuteNonQueryAsync(ct);
        }

        await AuditStore.AppendAsync(conn, tx, audit, ct);
        await tx.CommitAsync(ct);
    }

    public async Task MarkFailedAsync(Guid commandId, string failureCode, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE analysis.ai_config_commands
            SET status = 'FAILED',
                failure_code = $2,
                updated_at = now()
            WHERE command_id = $1 AND status = 'PENDING';
            """);
        cmd.Parameters.AddWithValue(commandId);
        cmd.Parameters.AddWithValue(failureCode);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AiConfigCommandRecord>> ListPendingAsync(int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT command_id, idempotency_key, action, actor_account_id, expected_version,
                   artifact_manifest_id, threshold_config_sha256, reason, payload_fingerprint,
                   correlation_id, status, committed_version, committed_manifest_id,
                   committed_sha256, committed_activated_at, failure_code, created_at
            FROM analysis.ai_config_commands
            WHERE status = 'PENDING'
            ORDER BY created_at ASC
            LIMIT $1;
            """);
        cmd.Parameters.AddWithValue(Math.Clamp(limit, 1, 200));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<AiConfigCommandRecord>();
        while (await reader.ReadAsync(ct))
            items.Add(ReadRecord(reader));
        return items;
    }

    private static AiConfigCommandRecord ReadRecord(NpgsqlDataReader r)
    {
        var status = AiConfigCommandJournalHelpers.ParseStatus(r.GetString(10));
        AiConfigView? receipt = null;
        if (status == AiConfigCommandStatus.Committed && !r.IsDBNull(11) && !r.IsDBNull(12) && !r.IsDBNull(13) && !r.IsDBNull(14))
        {
            receipt = new AiConfigView(
                r.GetGuid(12),
                r.GetString(13),
                r.GetInt32(11),
                new DateTimeOffset(r.GetDateTime(14), TimeSpan.Zero));
        }

        return new AiConfigCommandRecord(
            r.GetGuid(0),
            r.GetString(1),
            r.GetString(2),
            r.GetGuid(3),
            r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetGuid(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            status,
            receipt,
            r.IsDBNull(15) ? null : r.GetString(15),
            new DateTimeOffset(r.GetDateTime(16), TimeSpan.Zero));
    }
}
