using Microsoft.Extensions.Logging;

namespace AURA.Analysis.Api.Upload;

/// <summary>Cấu hình storage dùng chung với M1 (biến môi trường Storage__*, xem infra/storage/*.env.example).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string OriginalContainer { get; set; } = "aura-originals";
    public string AnnotationContainer { get; set; } = "aura-annotations";
    public string ReportContainer { get; set; } = "aura-reports";
    public int ReadUrlTtlSeconds { get; set; } = 120;
}

public enum StoredFileKind { Original, Annotation }

/// <param name="PatientId">Bệnh nhân của batch chứa ảnh; dùng để hỏi quyền với Profile.</param>
public sealed record StoredFileRecord(Guid AnalysisId, Guid FileId, Guid TenantId, Guid PatientId, Guid OwnerId,
    StoredFileKind Kind, string ObjectKey);

public interface IFileAccessRepository
{
    /// <summary>Tìm file theo (analysisId, fileId). Trả null nếu không có hoặc file không thuộc analysis đó.</summary>
    Task<StoredFileRecord?> FindAsync(Guid analysisId, Guid fileId, CancellationToken ct);
}

/// <summary>Chỉ gồm url và expiresAt theo schema FileAccess; không bao giờ có object key.</summary>
public sealed record FileAccessResult(string Url, DateTime ExpiresAt);

/// <summary>
/// GET /api/v1/analyses/{analysisId}/files/{fileId}/access: kiểm tra tồn tại và quyền (action FILE_READ),
/// rồi mới xin URL ký ngắn hạn từ storage. Không tồn tại, ngoài phạm vi hoặc khác tenant đều trả cùng một lỗi 404.
/// Endpoint phải đặt Cache-Control: no-store cho phản hồi này.
/// </summary>
public sealed class FileAccessService
{
    private readonly StorageOptions _storageOptions;
    private readonly IFileAccessRepository _files;
    private readonly IPatientAccessResolver _access;
    private readonly IPrivateFileStorage _storage;

    public FileAccessService(StorageOptions storageOptions, IFileAccessRepository files,
        IPatientAccessResolver access, IPrivateFileStorage storage, ILogger<FileAccessService> log)
    {
        _storageOptions = storageOptions; _files = files; _access = access; _storage = storage; _ = log;
    }

    private static UploadFailure NotFound() => new(404, "ERR_NOT_FOUND", "Không tìm thấy tài nguyên.");

    public async Task<FileAccessResult> GetAsync(Guid requesterId, Guid? clinicId, Guid analysisId, Guid fileId,
        CancellationToken ct)
    {
        var record = await Guard("Analysis database", () => _files.FindAsync(analysisId, fileId, ct));
        if (record is null) throw NotFound();

        var decision = await Guard("Profile", () => _access.ResolveAsync(
            new PatientAccessRequest(requesterId, clinicId, record.PatientId, "FILE_READ", analysisId), ct));
        if (!decision.Allowed || decision.TenantId != record.TenantId || decision.OwnerPrincipalId != record.OwnerId) throw NotFound();

        var container = record.Kind == StoredFileKind.Original
            ? _storageOptions.OriginalContainer
            : _storageOptions.AnnotationContainer;
        var ttl = TimeSpan.FromSeconds(_storageOptions.ReadUrlTtlSeconds);

        var access = await Guard("private storage", () => _storage.CreateReadAccessAsync(container, record.ObjectKey, ttl, ct));
        return new FileAccessResult(access.Url.ToString(), access.ExpiresAt.UtcDateTime);
    }

    private static async Task<T> Guard<T>(string what, Func<Task<T>> action)
    {
        try { return await action(); }
        catch (UploadFailure) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw UploadFailure.Dependency(what); }
    }
}
