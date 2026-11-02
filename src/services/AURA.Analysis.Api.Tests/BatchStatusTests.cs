using System.Text.Json;
using AURA.Common.Cache;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class BatchStatusTests
{
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly BatchIdentity Identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static BatchStatusRow Row(string status, bool retryable = false, string? jobStatus = null, int attempt = 1) =>
        new(Guid.NewGuid(), "synthetic.png", status, "NOT_REQUIRED", status == "DUPLICATE" ? null :
            new(Guid.NewGuid(), jobStatus ?? (status switch
            {
                "COMPLETED" => "SUCCEEDED", "PROCESSING" => "RUNNING", "RETRY_READY" => "RETRY_READY",
                "FAILED" or "LOW_QUALITY" or "REJECTED" => "FAILED", _ => "QUEUED"
            }), attempt - 1, attempt), retryable,
            "ERR_TIMEOUT", null, status == "DUPLICATE" ? Guid.NewGuid() : null);
    private static BatchStatusSnapshot Snapshot(params BatchStatusRow[] rows) => new(Identity.BatchId, DateTime.UtcNow, rows);

    [Theory]
    [InlineData("PENDING", "IN_PROGRESS", 1, 0, 0, 0)]
    [InlineData("QUEUED", "IN_PROGRESS", 1, 0, 0, 0)]
    [InlineData("PROCESSING", "IN_PROGRESS", 1, 0, 0, 0)]
    [InlineData("RETRY_READY", "IN_PROGRESS", 1, 0, 0, 0)]
    [InlineData("COMPLETED", "COMPLETED", 0, 1, 0, 0)]
    [InlineData("DUPLICATE", "COMPLETED", 0, 0, 0, 1)]
    [InlineData("FAILED", "FAILED", 0, 0, 1, 0)]
    [InlineData("LOW_QUALITY", "FAILED", 0, 0, 1, 0)]
    [InlineData("REJECTED", "FAILED", 0, 0, 1, 0)]
    public void All_processing_statuses_follow_the_batch_contract(string state, string batch, int active, int succeeded, int failed, int duplicate)
    {
        var result = BatchStatusProjection.Create(Snapshot(Row(state)));
        Assert.Equal(batch, result.BatchStatus);
        Assert.Equal((1, active, succeeded, failed, duplicate),
            (result.Summary.Total, result.Summary.Active, result.Summary.Succeeded, result.Summary.Failed, result.Summary.Duplicate));
        Assert.Equal(1, result.Summary.ByStatus[state]);
        Assert.Equal(1, result.Summary.ByStatus.Values.Sum());
        Assert.Equal(9, result.Summary.ByStatus.Count);
    }

    [Fact]
    public void Retryable_failed_attempt_is_active_and_retry_count_is_attempt_minus_one()
    {
        var result = BatchStatusProjection.Create(Snapshot(Row("FAILED", true, "FAILED", 4)));
        Assert.Equal("IN_PROGRESS", result.BatchStatus);
        Assert.Equal(1, result.Summary.Active);
        Assert.Equal(0, result.Summary.Failed);
        Assert.Equal(1, result.Summary.ByStatus["FAILED"]);
        Assert.True(result.Items[0].Error!.Retryable);
        Assert.Equal(3, result.Items[0].Error!.RetryCount);
        Assert.Equal(3, result.Items[0].Job!.RetryCount);
    }

    [Fact]
    public void Dead_letter_is_terminal_even_if_a_stale_retryable_flag_is_true()
    {
        var result = BatchStatusProjection.Create(Snapshot(Row("FAILED", true, "DEAD_LETTER", 3)));
        Assert.Equal("FAILED", result.BatchStatus);
        Assert.Equal(1, result.Summary.Failed);
        Assert.False(result.Items[0].Error!.Retryable);
    }

    [Fact]
    public void Mixed_terminal_outcomes_are_partial_failed_and_duplicates_are_not_new_successes()
    {
        var result = BatchStatusProjection.Create(Snapshot(Row("COMPLETED"), Row("FAILED"), Row("LOW_QUALITY"), Row("DUPLICATE")));
        Assert.Equal("PARTIAL_FAILED", result.BatchStatus);
        Assert.Equal((4, 0, 1, 2, 1),
            (result.Summary.Total, result.Summary.Active, result.Summary.Succeeded, result.Summary.Failed, result.Summary.Duplicate));
        Assert.Equal("ERR_DUPLICATE_REQUEST", result.Items[3].Error!.Code);
        Assert.False(result.Items[3].Error!.Retryable);
    }

    [Fact]
    public void Active_work_keeps_a_mixed_batch_in_progress()
    {
        var result = BatchStatusProjection.Create(Snapshot(Row("FAILED"), Row("COMPLETED"), Row("QUEUED")));
        Assert.Equal("IN_PROGRESS", result.BatchStatus);
        Assert.Equal(3, result.Summary.Total);
    }

    [Fact]
    public void Unknown_internal_error_codes_are_not_exposed()
    {
        var row = Row("FAILED") with { ErrorCode = "SQL_secret_do_not_publish" };
        var result = BatchStatusProjection.Create(Snapshot(row));
        Assert.Equal("ERR_AI_FAILED", result.Items[0].Error!.Code);
        Assert.DoesNotContain("SQL_secret", JsonSerializer.Serialize(result, UploadJson.Options));
    }

    [Fact]
    public void Corrupt_snapshot_or_job_metadata_is_a_dependency_error()
    {
        foreach (var snapshot in new[] { Snapshot(), Snapshot(Row("UNKNOWN")), Snapshot(Row("QUEUED") with { Job = new(Guid.NewGuid(), "QUEUED", 0, 2) }) })
        {
            var error = Assert.Throws<UploadFailure>(() => BatchStatusProjection.Create(snapshot));
            Assert.Equal(503, error.Status);
        }
    }

    [Fact]
    public void Serialization_has_contract_fields_and_preserves_uppercase_status_count_keys()
    {
        var result = BatchStatusProjection.Create(Snapshot(Row("QUEUED")));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, UploadJson.Options));
        Assert.Equal(new[] { "batchId", "batchStatus", "createdAt", "items", "summary" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var counts = json.RootElement.GetProperty("summary").GetProperty("byStatus");
        Assert.Equal(1, counts.GetProperty("QUEUED").GetInt32());
        Assert.Equal(9, counts.EnumerateObject().Count());
        Assert.False(json.RootElement.TryGetProperty("tenantId", out _));
        Assert.False(json.RootElement.GetProperty("items")[0].TryGetProperty("jobRetryable", out _));
        Assert.False(json.RootElement.GetProperty("items")[0].TryGetProperty("existingAnalysisId", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Denied_tenant_or_owner_scope_does_not_load_items_or_counts(int mode)
    {
        var repository = new Repository(); var access = new Access();
        if (mode == 0) access.Allowed = false;
        if (mode == 1) access.Tenant = Guid.NewGuid();
        if (mode == 2) access.Owner = Guid.NewGuid();
        var service = new BatchStatusService(repository, access);
        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.GetAsync(Actor, null, Identity.BatchId, default));
        Assert.Equal((404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên."), (error.Status, error.Code, error.Message));
        Assert.Equal(0, repository.ReadCalls);
        Assert.Equal("ANALYSIS_READ", access.Last!.Action);
        Assert.Equal(Identity.PatientId, access.Last.PatientId);
    }

    [Fact]
    public async Task Unknown_batch_and_denied_batch_have_the_same_public_error()
    {
        var repository = new Repository { Found = false }; var access = new Access();
        var service = new BatchStatusService(repository, access);
        var unknown = await Assert.ThrowsAsync<UploadFailure>(() => service.GetAsync(Actor, null, Identity.BatchId, default));
        Assert.Equal(0, access.Calls);
        repository.Found = true; access.Allowed = false;
        var denied = await Assert.ThrowsAsync<UploadFailure>(() => service.GetAsync(Actor, null, Identity.BatchId, default));
        Assert.Equal((unknown.Status, unknown.Code, unknown.Message), (denied.Status, denied.Code, denied.Message));
        Assert.Equal(0, repository.ReadCalls);
    }

    [Fact]
    public async Task Permission_is_rechecked_on_each_poll_and_correlation_is_forwarded()
    {
        var repository = new Repository(); var access = new Access();
        var service = new BatchStatusService(repository, access);
        var clinic = Guid.NewGuid();
        var result = await service.GetAsync(Actor, clinic, Identity.BatchId, default, "batch-correlation");
        Assert.Equal(Identity.BatchId, result.BatchId);
        Assert.Equal(Actor, access.Last!.RequesterId);
        Assert.Equal(clinic, access.Last.ClinicId);
        Assert.Equal("batch-correlation", access.Last.CorrelationId);
        access.Allowed = false;
        await Assert.ThrowsAsync<UploadFailure>(() => service.GetAsync(Actor, clinic, Identity.BatchId, default));
        Assert.Equal(2, access.Calls);
        Assert.Equal(1, repository.ReadCalls);
    }

    [Fact]
    public async Task Missing_redis_progress_is_rebuilt_from_database_terminal_items()
    {
        var repository = new Repository { ProcessingStatus = "COMPLETED" };
        var progress = new ProgressStub();
        var service = new BatchStatusService(repository, new Access(), progress);

        var response = await service.GetAsync(Actor, null, Identity.BatchId, default);

        Assert.Equal("COMPLETED", response.BatchStatus);
        Assert.Equal(1, response.Summary.Succeeded);
        Assert.True(progress.RebuildCalled);
        Assert.Equal(1, progress.Total);
        Assert.Equal(new BatchImageOutcome(response.Items[0].AnalysisId, true), Assert.Single(progress.Outcomes));
    }

    [Fact]
    public async Task Redis_rebuild_does_not_count_retryable_failed_attempt_as_terminal()
    {
        var repository = new Repository { ProcessingStatus = "FAILED", Retryable = true };
        var progress = new ProgressStub();
        var response = await new BatchStatusService(repository, new Access(), progress)
            .GetAsync(Actor, null, Identity.BatchId, default);

        Assert.Equal("IN_PROGRESS", response.BatchStatus);
        Assert.Empty(progress.Outcomes);
    }

    [Fact]
    public async Task Redis_outage_does_not_hide_durable_postgres_batch_status()
    {
        var response = await new BatchStatusService(new Repository(), new Access(), new ProgressStub { ThrowOnRead = true })
            .GetAsync(Actor, null, Identity.BatchId, default);

        Assert.Equal("IN_PROGRESS", response.BatchStatus);
        Assert.Equal(1, response.Summary.Active);
    }

    [Fact]
    public async Task Resource_moved_after_permission_check_returns_404()
    {
        var repository = new Repository { ReadMissing = true };
        var service = new BatchStatusService(repository, new Access());
        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.GetAsync(Actor, null, Identity.BatchId, default));
        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Dependency_errors_are_503_and_do_not_expose_the_cause()
    {
        var access = new Access { Fail = true };
        var service = new BatchStatusService(new Repository(), access);
        var error = await Assert.ThrowsAsync<UploadFailure>(() => service.GetAsync(Actor, null, Identity.BatchId, default));
        Assert.Equal((503, "ERR_NETWORK", true), (error.Status, error.Code, error.Retryable));
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task Cancelled_caller_never_touches_dependencies()
    {
        var repository = new Repository(); var access = new Access();
        var service = new BatchStatusService(repository, access);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(Actor, null, Identity.BatchId, cancelled.Token));
        Assert.Equal(0, repository.IdentityCalls);
        Assert.Equal(0, access.Calls);
    }

    private sealed class Repository : IBatchStatusRepository
    {
        public bool Found = true, ReadMissing;
        public string ProcessingStatus = "QUEUED";
        public bool Retryable;
        public int IdentityCalls, ReadCalls;
        public Task<BatchIdentity?> FindIdentityAsync(Guid id, CancellationToken ct)
        { IdentityCalls++; return Task.FromResult<BatchIdentity?>(Found ? Identity : null); }
        public Task<BatchStatusSnapshot?> ReadAsync(BatchIdentity identity, CancellationToken ct)
        { ReadCalls++; Assert.Equal(Identity, identity); return Task.FromResult<BatchStatusSnapshot?>(ReadMissing ? null : Snapshot(Row(ProcessingStatus, Retryable))); }
    }
    private sealed class Access : IPatientAccessResolver
    {
        public bool Allowed = true, Fail;
        public Guid Tenant = Identity.TenantId, Owner = Identity.OwnerId;
        public int Calls;
        public PatientAccessRequest? Last;
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct)
        {
            Calls++; Last = request;
            if (Fail) throw new HttpRequestException("private dependency diagnostic");
            return Task.FromResult(new AccessDecision(Allowed, Tenant, "CLINIC", Owner, "CLINIC:" + Owner, 1));
        }
    }

    private sealed class ProgressStub : IBatchRedisProgressService
    {
        public bool ThrowOnRead;
        public bool RebuildCalled;
        public int Total;
        public IReadOnlyList<BatchImageOutcome> Outcomes = Array.Empty<BatchImageOutcome>();
        public Task InitializeBatchAsync(Guid batchId, int totalImages, TimeSpan? expiry = null) => Task.CompletedTask;
        public Task<BatchProgressSnapshot?> GetProgressAsync(Guid batchId) => ThrowOnRead
            ? Task.FromException<BatchProgressSnapshot?>(new HttpRequestException("redis unavailable"))
            : Task.FromResult<BatchProgressSnapshot?>(null);
        public Task<BatchImageCompletionResult> RecordImageOutcomeAsync(Guid batchId, Guid imageId, bool isSuccess) =>
            Task.FromResult(new BatchImageCompletionResult(batchId, 1, isSuccess ? 1 : 0, isSuccess ? 0 : 1, 0, true,
                isSuccess ? "COMPLETED" : "FAILED"));
        public Task RebuildBatchAsync(Guid batchId, int totalImages, IReadOnlyList<BatchImageOutcome> outcomes,
            string status, TimeSpan? expiry = null)
        {
            RebuildCalled = true; Total = totalImages; Outcomes = outcomes;
            return Task.CompletedTask;
        }
    }
}
