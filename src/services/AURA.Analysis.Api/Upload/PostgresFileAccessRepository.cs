using Npgsql;

namespace AURA.Analysis.Api.Upload;

/// <summary>Returns only metadata. FileAccessService must resolve current Profile permission before issuing a URL.</summary>
public sealed class PostgresFileAccessRepository(NpgsqlDataSource dataSource) : IFileAccessRepository
{
    public async Task<StoredFileRecord?> FindAsync(Guid analysisId, Guid fileId, CancellationToken ct)
    {
        if (analysisId == Guid.Empty || fileId == Guid.Empty) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT i.id,i.file_id,i.tenant_id,r.patient_id,r.owner_id,0 AS file_kind,i.object_key,i.sha256,i.mime_type
            FROM analysis.images i
            JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
            WHERE i.id=$1 AND i.file_id=$2
            UNION ALL
            SELECT i.id,a.file_id,i.tenant_id,r.patient_id,r.owner_id,1 AS file_kind,a.object_key,a.sha256,a.mime_type
            FROM analysis.annotation_refs a
            JOIN analysis.images i ON i.tenant_id=a.tenant_id AND i.id=a.image_id
            JOIN analysis.analysis_requests r ON r.tenant_id=i.tenant_id AND r.id=i.analysis_request_id
            WHERE i.id=$1 AND a.file_id=$2
            LIMIT 2;
            """);
        command.Parameters.AddWithValue(analysisId);
        command.Parameters.AddWithValue(fileId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var result = new StoredFileRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            reader.GetGuid(4), reader.GetInt32(5) == 0 ? StoredFileKind.Original : StoredFileKind.Annotation, reader.GetString(6)){Sha256=reader.GetString(7),MimeType=reader.GetString(8)};
        if (await reader.ReadAsync(ct)) throw UploadFailure.Dependency("Analysis database");
        return result;
    }
}
