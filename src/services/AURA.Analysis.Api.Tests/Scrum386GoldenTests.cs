using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AURA.Analysis.Api.Alerts;
using AURA.Analysis.Api.Analytics;
using AURA.Analysis.Api.Exports;
using AURA.Analysis.Api.Feedback;
using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class Scrum386GoldenTests
{
    private static readonly JsonDocument Fixture = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "scrum386-golden.json")));

    [Fact]
    [Trait("TestKind", "ContractMock")]
    public void Alert_source_ids_and_kinds_match_versioned_golden_cases()
    {
        foreach (var item in Fixture.RootElement.GetProperty("alerts").EnumerateArray())
        {
            var facts = new AlertSourceFacts(
                item.GetProperty("tenantId").GetGuid(),
                item.GetProperty("analysisId").GetGuid(),
                item.GetProperty("resultId").GetGuid(),
                item.GetProperty("reviewVersion").GetInt32(),
                item.GetProperty("processingStatus").GetString()!,
                item.GetProperty("resultRiskLevel").GetString(),
                item.GetProperty("correctedRiskLevel").GetString(),
                item.GetProperty("hasCurrentReview").GetBoolean(),
                item.GetProperty("measurementStatus").GetString());

            var actual = AlertSourceRules.Derive(facts)
                .Select(source => new { kind = source.Kind, alertId = AlertSourceRules.ToAlertId(source) })
                .ToArray();
            var expected = item.GetProperty("expected").EnumerateArray()
                .Select(source => new { kind = source.GetProperty("kind").GetString(), alertId = source.GetProperty("alertId").GetGuid() })
                .ToArray();

            Assert.Equal(expected.Select(x => x.kind), actual.Select(x => x.kind));
            Assert.Equal(expected.Select(x => x.alertId), actual.Select(x => x.alertId));
        }
    }

    [Fact]
    [Trait("TestKind", "ContractMock")]
    public async Task Alert_pages_use_stable_cursor_without_gaps_or_duplicates()
    {
        var clinic = Guid.Parse("66666666-6666-4666-8666-666666666666");
        var tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var candidates = new[]
        {
            new AlertCandidate(tenant, clinic, Guid.NewGuid(), Guid.Parse("22222222-2222-4222-8222-222222222222"),
                Guid.Parse("33333333-3333-4333-8333-333333333333"), 1, 4, "COMPLETED", "LOW", "HIGH", true, "AVAILABLE", DateTimeOffset.UnixEpoch),
            new AlertCandidate(tenant, clinic, Guid.NewGuid(), Guid.Parse("44444444-4444-4444-8444-444444444444"),
                Guid.Parse("55555555-5555-4555-8555-555555555555"), 1, 2, "COMPLETED", "LOW", "HIGH", true, "UNKNOWN", DateTimeOffset.UnixEpoch),
        };
        var access = new GoldenAnalyticsAccess(new AnalyticsAccessGrant(tenant, clinic, null, null, 1));
        var repository = new GoldenAlertRepository(candidates);
        var service = new AlertService(repository, access);

        var page1 = await service.ListAsync(Guid.NewGuid(), clinic, 1, null, "golden-page", default);
        var page2 = await service.ListAsync(Guid.NewGuid(), clinic, 1, page1.NextCursor, "golden-page", default);
        var page3 = await service.ListAsync(Guid.NewGuid(), clinic, 1, page2.NextCursor, "golden-page", default);
        var actual = page1.Items.Concat(page2.Items).Concat(page3.Items).Select(x => x.AlertId).ToArray();
        var expected = candidates.SelectMany(x => AlertSourceRules.Derive(x.Facts))
            .Select(AlertSourceRules.ToAlertId).Order().ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.Null(page3.NextCursor);
    }

    [Fact]
    [Trait("TestKind", "ContractMock")]
    public void Golden_export_csv_has_fixed_bytes_checksum_and_formula_neutralization()
    {
        var export = Fixture.RootElement.GetProperty("export");
        var rows = export.GetProperty("rows").EnumerateArray().Select(row => new ResearchExportRow(
            row.GetProperty("caseCode").GetString()!,
            row.GetProperty("riskLevel").ValueKind == JsonValueKind.Null ? null : row.GetProperty("riskLevel").GetString(),
            row.GetProperty("measurementAvailable").GetBoolean(),
            row.GetProperty("avr").ValueKind == JsonValueKind.Null ? null : row.GetProperty("avr").GetDecimal()))
            .ToArray();

        var csv = ResearchExportWriter.WriteCsv(rows);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))).ToLowerInvariant();

        Assert.Equal(export.GetProperty("expectedCsv").GetString(), csv);
        Assert.Equal(export.GetProperty("expectedSha256").GetString(), checksum);
        Assert.DoesNotContain("patientId", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resultId", csv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("TestKind", "ContractMock")]
    public async Task Feedback_golden_case_binds_to_current_result_model_and_review_revision()
    {
        var item = Fixture.RootElement.GetProperty("feedback");
        var identity = new AnalysisIdentity(item.GetProperty("analysisId").GetGuid(), item.GetProperty("batchId").GetGuid(),
            item.GetProperty("tenantId").GetGuid(), item.GetProperty("patientId").GetGuid(), item.GetProperty("clinicId").GetGuid());
        var current = new AnalysisDetailResponse(identity.AnalysisId, identity.BatchId, identity.PatientId,
            "COMPLETED", "REVIEWED", DateTime.UnixEpoch, DateTime.UnixEpoch, identity.OwnerId, Guid.NewGuid(),
            new AnalysisImageRef(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")), null,
            new AnalysisResultDetail(true, "HIGH", .91, .88, false, item.GetProperty("modelVersion").GetString()!,
                "golden-config-v1", "golden-threshold-v1", "Synthetic test", 2,
                new AnalysisMetricsDetail(.42, .21, .7, .3, 8), [], [], [], 10, "Synthetic test")
            { ResultId = item.GetProperty("resultId").GetGuid() }, null, ReviewVersion: item.GetProperty("reviewVersion").GetInt32());
        var queries = new GoldenAnalysisQuery(identity, current);
        var access = new GoldenPatientAccess(new AccessDecision(true, identity.TenantId, "CLINIC", identity.OwnerId, "clinic", 1));
        var feedback = new GoldenFeedbackRepository();
        var request = new FeedbackCreateRequest(item.GetProperty("resultId").GetGuid(),
            item.GetProperty("modelVersion").GetString()!, item.GetProperty("reviewVersion").GetInt32(),
            item.GetProperty("feedbackType").GetString()!, item.GetProperty("comment").GetString()!);

        var receipt = await new FeedbackService(queries, access, feedback).CreateAsync(
            item.GetProperty("doctorId").GetGuid(), identity.OwnerId, identity.AnalysisId, request,
            "scrum386-golden", CancellationToken.None);

        Assert.Equal("ACCEPTED", receipt.Status);
        Assert.Equal(item.GetProperty("resultId").GetGuid(), feedback.Saved!.ResultId);
        Assert.Equal(item.GetProperty("modelVersion").GetString(), feedback.Saved.ModelVersion);
        Assert.Equal(item.GetProperty("reviewVersion").GetInt32(), feedback.Saved.ReviewVersion);
        Assert.Same(current.Result, (await queries.ReadAsync(identity, null, default))!.Result);
    }

    [Fact]
    [Trait("TestKind", "ContractMock")]
    public async Task Feedback_with_superseded_review_revision_conflicts_without_append()
    {
        var item = Fixture.RootElement.GetProperty("feedback");
        var identity = Identity(item);
        var current = Current(identity, item);
        var repository = new GoldenFeedbackRepository();
        var stale = new FeedbackCreateRequest(item.GetProperty("resultId").GetGuid(),
            item.GetProperty("modelVersion").GetString()!, item.GetProperty("reviewVersion").GetInt32() - 1,
            item.GetProperty("feedbackType").GetString()!, item.GetProperty("comment").GetString()!);

        var failure = await Assert.ThrowsAsync<UploadFailure>(() => new FeedbackService(
            new GoldenAnalysisQuery(identity, current),
            new GoldenPatientAccess(new AccessDecision(true, identity.TenantId, "CLINIC", identity.OwnerId, "clinic", 1)),
            repository).CreateAsync(item.GetProperty("doctorId").GetGuid(), identity.OwnerId, identity.AnalysisId,
                stale, "scrum386-stale", CancellationToken.None));

        Assert.Equal(409, failure.Status);
        Assert.Null(repository.Saved);
    }

    private static AnalysisIdentity Identity(JsonElement item) => new(item.GetProperty("analysisId").GetGuid(),
        item.GetProperty("batchId").GetGuid(), item.GetProperty("tenantId").GetGuid(),
        item.GetProperty("patientId").GetGuid(), item.GetProperty("clinicId").GetGuid());

    private static AnalysisDetailResponse Current(AnalysisIdentity identity, JsonElement item) => new(
        identity.AnalysisId, identity.BatchId, identity.PatientId, "COMPLETED", "REVIEWED", DateTime.UnixEpoch,
        DateTime.UnixEpoch, identity.OwnerId, Guid.NewGuid(), new AnalysisImageRef(Guid.NewGuid()), null,
        new AnalysisResultDetail(true, "HIGH", .91, .88, false, item.GetProperty("modelVersion").GetString()!,
            "golden-config-v1", "golden-threshold-v1", "Synthetic test", 2,
            new AnalysisMetricsDetail(.42, .21, .7, .3, 8), [], [], [], 10, "Synthetic test")
        { ResultId = item.GetProperty("resultId").GetGuid() }, null, ReviewVersion: item.GetProperty("reviewVersion").GetInt32());

    private sealed class GoldenAnalysisQuery(AnalysisIdentity identity, AnalysisDetailResponse current) : IAnalysisQueryRepository
    {
        public Task<AnalysisIdentity?> FindIdentityAsync(Guid analysisId, CancellationToken ct) =>
            Task.FromResult<AnalysisIdentity?>(analysisId == identity.AnalysisId ? identity : null);
        public Task<AnalysisDetailResponse?> ReadAsync(AnalysisIdentity request, string? correlationId, CancellationToken ct) =>
            Task.FromResult<AnalysisDetailResponse?>(request.AnalysisId == identity.AnalysisId ? current : null);
    }

    private sealed class GoldenPatientAccess(AccessDecision decision) : IPatientAccessResolver
    {
        public Task<AccessDecision> ResolveAsync(PatientAccessRequest request, CancellationToken ct) => Task.FromResult(decision);
    }

    private sealed class GoldenFeedbackRepository : IFeedbackRepository
    {
        public FeedbackRecord? Saved { get; private set; }
        public Task<FeedbackReceipt> InsertAsync(FeedbackRecord feedback, CancellationToken ct)
        {
            Saved = feedback;
            return Task.FromResult(new FeedbackReceipt(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "ACCEPTED", feedback.ReviewVersion));
        }
    }

    private sealed class GoldenAnalyticsAccess(AnalyticsAccessGrant grant) : IAnalyticsAccessResolver
    {
        public Task<AnalyticsAccessGrant> ResolveAsync(AnalyticsAccessRequest request, CancellationToken ct) => Task.FromResult(grant);
    }

    private sealed class GoldenAlertRepository(IReadOnlyList<AlertCandidate> candidates) : IAlertRepository
    {
        public Task<IReadOnlyList<AlertCandidate>> ReadCandidatesAsync(Guid tenantId, Guid clinicId,
            IReadOnlyCollection<Guid>? patientIds, int limit, CancellationToken ct) => Task.FromResult(candidates);
        public Task MaterializeSourcesAsync(Guid clinicId, Guid actorId, IReadOnlyCollection<AlertSource> sources,
            string correlationId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AlertStateRecord>> ReadStatesAsync(Guid tenantId, Guid clinicId,
            IReadOnlyCollection<Guid> analysisIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<AlertStateRecord>>([]);
        public Task<AlertStateRecord> ChangeStateAsync(Guid clinicId, Guid actorId, AlertSource source,
            int expectedVersion, string targetStatus, string reason, string correlationId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
