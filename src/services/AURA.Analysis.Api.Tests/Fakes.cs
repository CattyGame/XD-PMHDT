using System.Collections.Concurrent;
using AURA.Analysis.Api.Upload;

namespace AURA.Analysis.Api.Tests;

internal sealed class MemFile : IUploadedFile
{
    private readonly byte[] _bytes;
    public MemFile(string name, byte[] bytes, string? type = null) { FileName = name; _bytes = bytes; ContentType = type; }
    public string FileName { get; }
    public string? ContentType { get; }
    public long Length => _bytes.Length;
    public Stream OpenReadStream() => new MemoryStream(_bytes);
}

internal sealed class FakeAccess : IPatientAccessResolver
{
    public static readonly Guid Tenant = Guid.Parse("7a000000-0000-4000-8000-000000000001");
    public bool Allowed = true;
    public bool Down;
    public int Calls;
    public Guid TenantOverride = Guid.Empty;
    public string? LastAction;
    public PatientAccessRequest? LastRequest;
    public Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct)
    {
        Calls++;
        LastAction = request.Action;
        LastRequest = request;
        if (Down) throw new HttpRequestException("profile down");
        var tenant = TenantOverride == Guid.Empty ? Tenant : TenantOverride;
        return Task.FromResult(new AccessDecision(Allowed, tenant, "CLINIC", request.RequesterId, "CLINIC:" + tenant, 1));
    }
}

internal sealed class FakeBilling : ICreditReservations
{
    private readonly object _lock = new();
    public readonly Dictionary<Guid, Guid> ByKey = new();       // idempotency key -> reservationId
    public readonly List<Guid> Released = new();
    public readonly List<ReserveCredit> ReserveRequests = new();
    public readonly List<ReleaseCredit> ReleaseRequests = new();
    public readonly List<bool> ReleaseWasCancelled = new();
    public int ReserveCalls;
    public int InsufficientAtCall = -1;                          // lần gọi thứ n (từ 1) trả thiếu credit
    public bool Down;

    public Task<ReservationOutcome> ReserveAsync(ReserveCredit request, CancellationToken ct)
    {
        lock (_lock)
        {
            ReserveCalls++;
            ReserveRequests.Add(request);
            if (Down) throw new HttpRequestException("billing down");
            if (ReserveCalls == InsufficientAtCall) return Task.FromResult(new ReservationOutcome(ReservationStatus.InsufficientCredit, null));
            if (!ByKey.TryGetValue(request.IdempotencyKey, out var id)) ByKey[request.IdempotencyKey] = id = Guid.NewGuid();
            return Task.FromResult(new ReservationOutcome(ReservationStatus.Reserved, id));
        }
    }

    public Task ReleaseAsync(ReleaseCredit request, CancellationToken ct)
    {
        lock (_lock)
        {
            Released.Add(request.ReservationId);
            ReleaseRequests.Add(request);
            ReleaseWasCancelled.Add(ct.IsCancellationRequested);
        }
        return Task.CompletedTask;
    }
}

internal sealed class FakeStorage : IPrivateFileStorage
{
    public readonly ConcurrentDictionary<string, byte[]> Files = new();
    public readonly List<string> Deleted = new();
    public int PutCalls;
    public int FailOnPut = -1;                                    // lần Put thứ n (từ 1) ném lỗi
    public Func<Task>? BeforePut;                                 // để dựng tình huống đồng thời

    public async Task PutAsync(string container, string key, Stream content, string contentType, CancellationToken ct)
    {
        int n = Interlocked.Increment(ref PutCalls);
        if (BeforePut is not null) await BeforePut();
        if (n == FailOnPut) throw new IOException("disk full");
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Files[container + "/" + key] = ms.ToArray();
    }
    public Task<Stream> OpenReadAsync(string container, string key, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Files[container + "/" + key]));
    public Task DeleteAsync(string container, string key, CancellationToken ct)
    {
        Files.TryRemove(container + "/" + key, out _);
        lock (Deleted) Deleted.Add(key);
        return Task.CompletedTask;
    }
    public (string Container, string Key, TimeSpan Ttl)? LastReadAccess;
    public bool ReadAccessDown;
    public Task<FileReadAccess> CreateReadAccessAsync(string container, string key, TimeSpan ttl, CancellationToken ct)
    {
        if (ReadAccessDown) throw new IOException("storage down");
        LastReadAccess = (container, key, ttl);
        return Task.FromResult(new FileReadAccess(new Uri("http://127.0.0.1:10000/devstoreaccount1/" + container + "/" + key + "?sig=demo"),
            DateTimeOffset.UtcNow.Add(ttl)));
    }
}

internal sealed class FakeFileRepo : IFileAccessRepository
{
    public readonly List<StoredFileRecord> Records = new();
    public bool Down;
    public Task<StoredFileRecord?> FindAsync(Guid analysisId, Guid fileId, CancellationToken ct)
    {
        if (Down) throw new InvalidOperationException("db down");
        return Task.FromResult(Records.FirstOrDefault(r => r.AnalysisId == analysisId && r.FileId == fileId));
    }
}

internal sealed class FakeRepo : IUploadRepository
{
    private readonly object _lock = new();
    private readonly HashSet<IdempotencyScope> _locked = new();
    public readonly Dictionary<IdempotencyScope, (string Fingerprint, AcceptedResponse Response)> Committed = new();
    public readonly Dictionary<string, Guid> Existing = new();    // sha256 -> analysisId đã có
    public readonly List<BatchPlan> Commits = new();
    public bool FailCommit;
    public bool DbDown;

    public Task<IUploadSession> BeginAsync(IdempotencyScope scope, string fingerprint, CancellationToken ct)
    {
        if (DbDown) throw new InvalidOperationException("db down");
        lock (_lock)
        {
            if (!_locked.Add(scope))
                throw new UploadFailure(409, "ERR_REQUEST_IN_PROGRESS", "Request đang được xử lý.", true);
            if (Committed.TryGetValue(scope, out var c))
                return Task.FromResult<IUploadSession>(new Session(this, scope, c.Fingerprint == fingerprint ? IdempotencyState.Replay : IdempotencyState.Conflict, c.Response, fingerprint));
            return Task.FromResult<IUploadSession>(new Session(this, scope, IdempotencyState.New, null, fingerprint));
        }
    }

    private sealed class Session : IUploadSession
    {
        private readonly FakeRepo _r; private readonly IdempotencyScope _scope;
        public Session(FakeRepo r, IdempotencyScope scope, IdempotencyState state, AcceptedResponse? stored, string fp)
        { _r = r; _scope = scope; State = state; StoredResponse = stored; _ = fp; }
        public IdempotencyState State { get; }
        public AcceptedResponse? StoredResponse { get; }
        public Task<Guid?> FindExistingAnalysisAsync(Guid patientId, string modality, string eyeSide, string sha256, CancellationToken ct) =>
            Task.FromResult<Guid?>(_r.Existing.TryGetValue(sha256, out var id) ? id : null);
        public Task CommitAsync(BatchPlan plan, CancellationToken ct)
        {
            if (_r.FailCommit) throw new InvalidOperationException("commit failed");
            lock (_r._lock)
            {
                _r.Commits.Add(plan);
                _r.Committed[_scope] = (plan.Fingerprint, plan.Response);
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { lock (_r._lock) _r._locked.Remove(_scope); return ValueTask.CompletedTask; }
    }
}
