namespace Aura.Worker.Analysis.Data.Repositories;

/// <summary>The immutable identity returned by the successful claim, never reconstructed from a later row.</summary>
public sealed record JobLease(Guid JobId, string Owner, long FencingToken, DateTime ExpiresAt);

public sealed class JobLeaseLostException(Guid jobId) : InvalidOperationException($"Job lease lost: {jobId:D}");

public sealed record AnnotationDescriptor(Guid FileId, string Type, string ObjectKey, string MimeType,
    long SizeBytes, int Width, int Height, string Sha256);
