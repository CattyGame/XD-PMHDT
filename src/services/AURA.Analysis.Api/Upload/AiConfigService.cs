using System.Text.Json;
using System.Text.RegularExpressions;

namespace AURA.Analysis.Api.Upload;

/// <summary>Public admin view matching sprint6-planned-openapi-v1.yaml AiConfigView.</summary>
public sealed record AiConfigView(
    Guid ArtifactManifestId,
    string ThresholdConfigSha256,
    int Version,
    DateTimeOffset ActivatedAt);

/// <summary>Admin change request matching sprint6-planned-openapi-v1.yaml AiConfigChange.</summary>
public sealed record AiConfigChange(
    int ExpectedVersion,
    Guid ArtifactManifestId,
    string ThresholdConfigSha256,
    string Reason);

public sealed record AiManifestDescriptor(
    Guid ArtifactManifestId,
    string ConfigFile,
    string ThresholdConfigSha256,
    string ModelVersion,
    string ConfigVersion,
    string ThresholdVersion,
    bool RiskAvailable);

public sealed record AiConfigAuditEntry(
    Guid ActorAccountId,
    Guid ArtifactManifestId,
    string ThresholdConfigSha256,
    int FromVersion,
    int ToVersion,
    string Action,
    string Reason,
    DateTimeOffset OccurredAt);

public interface IAiManifestCatalog
{
    Guid DefaultManifestId { get; }
    AiManifestDescriptor? Find(Guid artifactManifestId);
    IReadOnlyList<AiManifestDescriptor> List();
}

public interface IAiCandidateSmokeVerifier
{
    Task<bool> VerifyCandidateAsync(AiManifestDescriptor candidate, CancellationToken ct);
}

public sealed class VerifiedServerAiManifestCatalog : IAiManifestCatalog
{
    private static readonly AiManifestDescriptor[] BuiltInManifests =
    [
        new(
            Guid.Parse("11111111-1111-4111-8111-111111110301"),
            "ai-frangi.yaml",
            "0b0451cad981b69471920b374c19042f169b991a26c49e2c3e29395db7c6a0ee",
            "frangi-v0.2",
            "s3-frangi-1.0.0",
            "frangi-threshold-0.02-v1",
            false),
        new(
            Guid.Parse("11111111-1111-4111-8111-111111110405"),
            "ai-frangi-v1.3.yaml",
            "dd444ce85afdcda830cdc825ace92d34d13e476739bdd2575890be13e8305087",
            "frangi-v0.2",
            "s45-frangi-1.3.0",
            "frangi-threshold-0.02-v1",
            true),
        new(
            Guid.Parse("11111111-1111-4111-8111-111111110501"),
            "ai-frangi-hrf.yaml",
            "8af374dc5dc1e70df09e333730e46a27c40e51a9f76e554498d3fa8e27119aca",
            "frangi-hrf-proxy-v1",
            "s5-hrf-opencv-clahe-1280-v1",
            "hrf-normalized-frangi-0.04-v1",
            false),
    ];

    private readonly Dictionary<Guid, AiManifestDescriptor> _byId;
    public Guid DefaultManifestId { get; }

    public VerifiedServerAiManifestCatalog(IReadOnlyList<AiManifestDescriptor>? manifests = null, Guid? defaultManifestId = null)
    {
        var source = manifests is { Count: > 0 } ? manifests : BuiltInManifests;
        _byId = source.ToDictionary(m => m.ArtifactManifestId);
        DefaultManifestId = defaultManifestId is { } d && _byId.ContainsKey(d) ? d : source[0].ArtifactManifestId;
    }

    public AiManifestDescriptor? Find(Guid artifactManifestId) =>
        _byId.TryGetValue(artifactManifestId, out var entry) ? entry : null;

    public IReadOnlyList<AiManifestDescriptor> List() => _byId.Values.ToArray();
}

public sealed class MetadataAiCandidateSmokeVerifier : IAiCandidateSmokeVerifier
{
    public Task<bool> VerifyCandidateAsync(AiManifestDescriptor candidate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var valid = candidate.ArtifactManifestId != Guid.Empty
            && !string.IsNullOrWhiteSpace(candidate.ConfigFile)
            && !candidate.ConfigFile.Contains("..", StringComparison.Ordinal)
            && !candidate.ConfigFile.Contains("://", StringComparison.Ordinal)
            && Regex.IsMatch(candidate.ThresholdConfigSha256, @"\A[a-f0-9]{64}\z")
            && !string.IsNullOrWhiteSpace(candidate.ModelVersion)
            && !string.IsNullOrWhiteSpace(candidate.ConfigVersion)
            && !string.IsNullOrWhiteSpace(candidate.ThresholdVersion);
        return Task.FromResult(valid);
    }
}

/// <summary>
/// Manages versioned AI manifest activation with strict payload validation, candidate smoke checks,
/// atomic active pointer switches, and rollback without exposing raw weights paths or AI Core publicly.
/// </summary>
public sealed class AiConfigService(
    IAiManifestCatalog catalog,
    IAiCandidateSmokeVerifier smokeVerifier,
    TimeProvider clock)
{
    private static readonly HashSet<string> AllowedChangeFields =
        new(StringComparer.Ordinal) { "expectedVersion", "artifactManifestId", "thresholdConfigSha256", "reason" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<AiConfigView> _history = new();
    private readonly List<AiConfigAuditEntry> _audit = new();
    private AiConfigView _current = CreateInitialView(catalog, clock);

    private static AiConfigView CreateInitialView(IAiManifestCatalog catalog, TimeProvider clock)
    {
        var initial = catalog.Find(catalog.DefaultManifestId)
            ?? throw new InvalidOperationException("Verified default AI manifest is missing.");
        return new AiConfigView(initial.ArtifactManifestId, initial.ThresholdConfigSha256, 1, clock.GetUtcNow());
    }

    public Task<AiConfigView> GetActiveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_history)
        {
            return Task.FromResult(_current);
        }
    }

    public IReadOnlyList<AiConfigAuditEntry> AuditTrail()
    {
        lock (_history)
        {
            return _audit.ToArray();
        }
    }

    public static AiConfigChange ParseStrictChange(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw UploadFailure.Validation(new ErrorDetail("body", "must_be_json_object"));

        foreach (var prop in body.EnumerateObject())
        {
            if (!AllowedChangeFields.Contains(prop.Name))
                throw UploadFailure.Validation(new ErrorDetail(prop.Name, "unexpected_field_forbidden"));
        }

        if (!body.TryGetProperty("expectedVersion", out var versionEl)
            || versionEl.ValueKind != JsonValueKind.Number
            || !versionEl.TryGetInt32(out var expectedVersion)
            || expectedVersion < 1)
            throw UploadFailure.Validation(new ErrorDetail("expectedVersion", "must_be_positive_integer"));

        if (!body.TryGetProperty("artifactManifestId", out var manifestEl)
            || manifestEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(manifestEl.GetString(), out var artifactManifestId)
            || artifactManifestId == Guid.Empty)
            throw UploadFailure.Validation(new ErrorDetail("artifactManifestId", "invalid_uuid"));

        if (!body.TryGetProperty("thresholdConfigSha256", out var shaEl)
            || shaEl.ValueKind != JsonValueKind.String
            || shaEl.GetString() is not { } sha
            || !Regex.IsMatch(sha, @"\A[a-f0-9]{64}\z"))
            throw UploadFailure.Validation(new ErrorDetail("thresholdConfigSha256", "invalid_sha256_hex"));

        if (!body.TryGetProperty("reason", out var reasonEl)
            || reasonEl.ValueKind != JsonValueKind.String
            || reasonEl.GetString() is not { } reason
            || string.IsNullOrWhiteSpace(reason)
            || reason.Length > 1000)
            throw UploadFailure.Validation(new ErrorDetail("reason", "required_1_to_1000_chars"));

        return new AiConfigChange(expectedVersion, artifactManifestId, sha, reason.Trim());
    }

    public async Task<AiConfigView> ActivateAsync(Guid actorAccountId, AiConfigChange change, CancellationToken ct = default)
    {
        if (actorAccountId == Guid.Empty)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần danh tính SYSTEM_ADMIN hợp lệ.");

        await _gate.WaitAsync(ct);
        try
        {
            var manifest = catalog.Find(change.ArtifactManifestId)
                ?? throw new UploadFailure(404, "ERR_MANIFEST_NOT_FOUND", "Không tìm thấy artifactManifestId trong registry phía server.");

            AiConfigView snapshot;
            lock (_history)
            {
                snapshot = _current;
            }

            if (change.ExpectedVersion != snapshot.Version)
                throw new UploadFailure(409, "ERR_VERSION_CONFLICT",
                    $"Phiên bản hiện tại là {snapshot.Version}, không khớp expectedVersion={change.ExpectedVersion}.");

            if (!string.Equals(manifest.ThresholdConfigSha256, change.ThresholdConfigSha256, StringComparison.Ordinal))
                throw new UploadFailure(409, "ERR_HASH_MISMATCH",
                    "Mã băm thresholdConfigSha256 không khớp manifest đã xác minh.");

            bool smokePassed;
            try
            {
                smokePassed = await smokeVerifier.VerifyCandidateAsync(manifest, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                smokePassed = false;
            }

            if (!smokePassed)
                throw new UploadFailure(503, "ERR_SMOKE_CHECK_FAILED",
                    "Kiểm tra smoke của candidate AI manifest thất bại; giữ nguyên phiên bản đang chạy.", retryable: true);

            var now = clock.GetUtcNow();
            var next = new AiConfigView(manifest.ArtifactManifestId, manifest.ThresholdConfigSha256, snapshot.Version + 1, now);
            lock (_history)
            {
                _history.Push(snapshot);
                _current = next;
                _audit.Add(new(actorAccountId, manifest.ArtifactManifestId, manifest.ThresholdConfigSha256,
                    snapshot.Version, next.Version, "ACTIVATE_MANIFEST", change.Reason, now));
            }
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AiConfigView> RollbackAsync(Guid actorAccountId, int expectedVersion, string reason, CancellationToken ct = default)
    {
        if (actorAccountId == Guid.Empty)
            throw new UploadFailure(401, "ERR_UNAUTHORIZED", "Cần danh tính SYSTEM_ADMIN hợp lệ.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw UploadFailure.Validation(new ErrorDetail("reason", "required_1_to_1000_chars"));

        await _gate.WaitAsync(ct);
        try
        {
            AiConfigView currentSnapshot;
            AiConfigView previousSnapshot;
            lock (_history)
            {
                currentSnapshot = _current;
                if (expectedVersion != currentSnapshot.Version)
                    throw new UploadFailure(409, "ERR_VERSION_CONFLICT",
                        $"Phiên bản hiện tại là {currentSnapshot.Version}, không khớp expectedVersion={expectedVersion}.");
                if (_history.Count == 0)
                    throw new UploadFailure(409, "ERR_NO_PREVIOUS_VERSION", "Chưa có phiên bản trước để rollback.");
                previousSnapshot = _history.Peek();
            }

            var targetManifest = catalog.Find(previousSnapshot.ArtifactManifestId)
                ?? throw new UploadFailure(503, "ERR_MANIFEST_NOT_FOUND", "Manifest trước không còn trong catalog.");

            if (!await smokeVerifier.VerifyCandidateAsync(targetManifest, ct))
                throw new UploadFailure(503, "ERR_SMOKE_CHECK_FAILED",
                    "Rollback target smoke verification failed; keeping active version.", retryable: true);

            var now = clock.GetUtcNow();
            var next = new AiConfigView(previousSnapshot.ArtifactManifestId, previousSnapshot.ThresholdConfigSha256, currentSnapshot.Version + 1, now);
            lock (_history)
            {
                _history.Pop();
                _current = next;
                _audit.Add(new(actorAccountId, next.ArtifactManifestId, next.ThresholdConfigSha256,
                    currentSnapshot.Version, next.Version, "ROLLBACK_MANIFEST", reason.Trim(), now));
            }
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }
}
