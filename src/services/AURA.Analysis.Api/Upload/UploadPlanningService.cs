using System.Text.RegularExpressions;
using System.Text.Json;
namespace AURA.Analysis.Api.Upload;

internal sealed record PreparedUpload(IUploadSession Session, AccessDecision Decision, BatchPlan? Plan, AcceptedResponse? StoredResponse) : IAsyncDisposable
{ public ValueTask DisposeAsync() => Session.DisposeAsync(); }

public sealed partial class UploadPlanningService(UploadOptions options,IPatientAccessResolver access,IUploadRepository repository,TimeProvider clock)
{
    private const string EventSchemaVersion = "0.7";
    private const string JobSchemaVersion = "0.6";
    private static readonly string[] EyeSides={"left","right","unknown"};
    private readonly UploadOptions _options=options;
    private readonly IPatientAccessResolver _access=access;
    private readonly IUploadRepository _repository=repository;
    private readonly TimeProvider _clock=clock;
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex CorrelationPattern();
    internal async Task<PreparedUpload> PrepareAsync(UploadCommand cmd,CancellationToken ct)
    {
        var (patientId, modality, eyeSide) = ValidateForm(cmd);
        var correlationId = cmd.CorrelationId is { } c && CorrelationPattern().IsMatch(c)
            ? c : Guid.NewGuid().ToString();

        var inspections = await InspectAllAsync(cmd.Files, ct);

        var decision = await Call("Profile", () => _access.ResolveAsync(
            new PatientAccessRequest(cmd.RequesterId, cmd.ClinicId, patientId, "ANALYSIS_CREATE") { CorrelationId = correlationId }, ct));
        if (!decision.Allowed)
            throw new UploadFailure(404, "ERR_NOT_FOUND", "Không tìm thấy bệnh nhân.");   // ngoài phạm vi = không tồn tại

        var fingerprint = RequestFingerprint.Compute(patientId, modality, eyeSide, inspections.Select(i => i.Sha256));
        var scope = new IdempotencyScope(decision.TenantId, cmd.RequesterId, cmd.IdempotencyKey);

        var session = await Call("Analysis database", () => _repository.BeginAsync(scope, fingerprint, ct));
        try
        {
        if (session.State == IdempotencyState.Conflict)
            throw new UploadFailure(409, "ERR_IDEMPOTENCY_KEY_REUSED", "Idempotency-Key đã dùng với nội dung khác.");
        if (session.State == IdempotencyState.Replay && session.StoredResponse is { } stored)
            return new PreparedUpload(session, decision, null, stored);

        var now = _clock.GetUtcNow();
        var plan = await BuildPlanAsync(cmd, decision, patientId, modality, eyeSide, correlationId, fingerprint,
            inspections, session, now, ct);

            return new PreparedUpload(session,decision,plan,null);
        }
        catch { await session.DisposeAsync(); throw; }
    }
    // ---- Kiểm tra form ----
    private (Guid patientId, string modality, string eyeSide) ValidateForm(UploadCommand cmd)
    {
        var errors = new List<ErrorDetail>();

        Guid patientId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(cmd.PatientId)) errors.Add(new("patientId", "required"));
        else if (!Guid.TryParse(cmd.PatientId, out patientId) || patientId == Guid.Empty) errors.Add(new("patientId", "invalid_uuid"));

        var modality = string.IsNullOrWhiteSpace(cmd.Modality) ? "FUNDUS" : cmd.Modality;
        if (modality != "FUNDUS") errors.Add(new("modality", "unsupported"));   // OCT hoặc khác: 400 trước mọi bước còn lại

        var eyeSide = string.IsNullOrWhiteSpace(cmd.EyeSide) ? "unknown" : cmd.EyeSide;
        if (!EyeSides.Contains(eyeSide)) errors.Add(new("eyeSide", "invalid"));

        if (cmd.Files.Count == 0) errors.Add(new("images", "required"));
        else if (cmd.Files.Count > _options.MaxImagesPerRequest) errors.Add(new("images", "too_many"));

        if (errors.Count > 0) throw UploadFailure.Validation(errors.ToArray());

        long total = cmd.Files.Sum(f => f.Length);
        if (total > _options.MaxRequestBytes)
            throw new UploadFailure(413, "ERR_PAYLOAD_TOO_LARGE", "Request vượt giới hạn kích thước.",
                false, new[] { new ErrorDetail("images", "request_too_large") });

        return (patientId, modality, eyeSide);
    }

    // ---- Kiểm tra từng ảnh: lỗi bất kỳ thì từ chối cả request trước khi reserve ----
    private async Task<List<ImageInspection>> InspectAllAsync(IReadOnlyList<IUploadedFile> files, CancellationToken ct)
    {
        var ok = new List<ImageInspection>(files.Count);
        var problems = new List<(int Index, ImageProblem Problem)>();
        for (int i = 0; i < files.Count; i++)
        {
            await using var s = files[i].OpenReadStream();
            var outcome = await ImageInspector.InspectAsync(s, files[i].ContentType, _options, ct);
            if (outcome.Problem is { } p) problems.Add((i, p));
            else ok.Add(outcome.Image!);
        }
        if (problems.Count > 0)
        {
            var first = problems[0].Problem;
            throw new UploadFailure(first.Status, first.Code, MessageFor(first),
                false, problems.Select(p => new ErrorDetail($"images[{p.Index}]", p.Problem.Issue)).ToArray());
        }
        return ok;
    }

    private static string MessageFor(ImageProblem p) => p.Status switch
    {
        413 => "File vượt giới hạn kích thước theo policy.",
        415 => "Chỉ nhận ảnh JPEG hoặc PNG.",
        _ => "Dữ liệu không hợp lệ."
    };

    // ---- Dựng kế hoạch ghi: ID xác định theo (tenant, requester, key) nên retry tạo lại đúng ID cũ ----
    private async Task<BatchPlan> BuildPlanAsync(UploadCommand cmd, AccessDecision d, Guid patientId, string modality,
        string eyeSide, string correlationId, string fingerprint, List<ImageInspection> inspections,
        IUploadSession session, DateTimeOffset now, CancellationToken ct)
    {
        string baseKey = string.Join("|", d.TenantId, cmd.RequesterId, cmd.IdempotencyKey);
        var batchId = DeterministicIds.Create("batch", baseKey);
        var images = new List<ImagePlan>();
        var outbox = new List<OutboxPlan>();
        var items = new List<AcceptedItem>();
        var firstInRequest = new Dictionary<string, Guid>();

        for (int i = 0; i < inspections.Count; i++)
        {
            var insp = inspections[i];
            var analysisId = DeterministicIds.Create("analysis", baseKey, i.ToString());
            var fileId = DeterministicIds.Create("file", baseKey, i.ToString());
            var objectKey = $"{d.TenantId:D}/{batchId:D}/{analysisId:D}";   // chỉ UUID; không có tên file/tên bệnh nhân

            Guid? existing = firstInRequest.TryGetValue(insp.Sha256, out var sameBatch)
                ? sameBatch
                : await Call("Analysis database", () => session.FindExistingAnalysisAsync(patientId, modality, eyeSide, insp.Sha256, ct));

            string fileName = SafeFileName(cmd.Files[i].FileName);

            if (existing is { } existingId)
            {
                images.Add(new ImagePlan(analysisId, fileId, objectKey, fileName, insp.MimeType, insp.SizeBytes,
                    insp.Width, insp.Height, insp.Sha256, modality, eyeSide, "DUPLICATE", existingId, null));
                items.Add(new AcceptedItem(analysisId, fileName, "DUPLICATE",
                    new ItemError("ERR_DUPLICATE_REQUEST", "Ảnh đã được gửi trước đó.", false), existingId));
                continue;
            }

            firstInRequest[insp.Sha256] = analysisId;
            var jobId = DeterministicIds.Create("job", analysisId.ToString(), "1");
            var messageId = DeterministicIds.Create("job-message", jobId.ToString());
            images.Add(new ImagePlan(analysisId, fileId, objectKey, fileName, insp.MimeType, insp.SizeBytes,
                insp.Width, insp.Height, insp.Sha256, modality, eyeSide, "QUEUED", null,
                new JobPlan(jobId, messageId, correlationId)));
            items.Add(new AcceptedItem(analysisId, fileName, "QUEUED", null));

            string occurred = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var requestedEventId = DeterministicIds.Create("event-requested", analysisId.ToString());
            outbox.Add(new OutboxPlan(requestedEventId, analysisId, "analysis.requested", EventSchemaVersion,
                JsonSerializer.Serialize(new
                {
                    eventId = requestedEventId,
                    eventType = "analysis.requested",
                    schemaVersion = EventSchemaVersion,
                    occurredAt = occurred,
                    tenantId = d.TenantId,
                    correlationId,
                    analysisId,
                    data = new { patientId, batchId }
                }, UploadJson.Options), now));
            outbox.Add(new OutboxPlan(messageId, analysisId, "analysis.job.run", JobSchemaVersion,
                JsonSerializer.Serialize(new
                {
                    messageId,
                    messageType = "analysis.job.run",
                    schemaVersion = JobSchemaVersion,
                    occurredAt = occurred,
                    tenantId = d.TenantId,
                    correlationId,
                    jobId,
                    analysisId,
                    batchId,
                    imageFileId = fileId,
                    attempt = 1
                }, UploadJson.Options), now));
        }

        var response = new AcceptedResponse(batchId, items, now.UtcDateTime, $"/api/v1/batches/{batchId:D}");
        return new BatchPlan(batchId, d.TenantId, d.OwnerPrincipalId, cmd.RequesterId, patientId, cmd.IdempotencyKey,
            fingerprint, RequestFingerprint.Version, correlationId, now, response, images, outbox)
        { ClinicId = cmd.ClinicId };
    }

    private static string SafeFileName(string name)
    {
        var n = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(n)) n = "image";
        return n.Length > 255 ? n[..255] : n;
    }

    private static async Task<T> Call<T>(string what, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (UploadFailure) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw UploadFailure.Dependency(what); }
    }

    private static async Task Call(string what, Func<Task> action)
    {
        try { await action(); }
        catch (UploadFailure) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw UploadFailure.Dependency(what); }
    }
}
