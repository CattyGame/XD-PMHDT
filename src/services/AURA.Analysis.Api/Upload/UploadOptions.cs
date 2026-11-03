namespace AURA.Analysis.Api.Upload;

/// <summary>Giới hạn upload. Cấu hình qua biến môi trường Analysis__MaxImageBytes, ... (Sprint 2 contract mục 7).</summary>
public sealed class UploadOptions
{
    public const string SectionName = "Analysis";

    public long MaxImageBytes { get; set; } = 10_485_760;          // 10 MiB mỗi ảnh
    public long MaxImagePixels { get; set; } = 16_000_000;         // 16 triệu pixel
    public int MaxImagesPerRequest { get; set; } = 100;
    public long MaxRequestBytes { get; set; } = 104_857_600;       // 100 MiB (Gateway boundary)

    public string OriginalsContainer { get; set; } = "aura-originals";
}
