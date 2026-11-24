using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aura.Worker.Analysis.Configuration;

namespace Aura.Worker.Analysis.Services;

public interface IBlobStorageService
{
    Task<byte[]> DownloadOriginalImageAsync(string blobKey, CancellationToken cancellationToken);
    Task<string> UploadAnnotationAsync(string blobKey, byte[] data, string contentType, CancellationToken cancellationToken);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobStorageOptions _options;
    private readonly ILogger<BlobStorageService> _logger;

    public BlobStorageService(
        IOptions<BlobStorageOptions> options,
        ILogger<BlobStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;
        // Match Analysis API's pinned protocol and the supported Azurite fixture.
        var clientOptions = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03);
        clientOptions.Diagnostics.IsLoggingEnabled = false;
        clientOptions.Diagnostics.IsLoggingContentEnabled = false;
        _blobServiceClient = new BlobServiceClient(_options.ConnectionString, clientOptions);
    }

    public async Task<byte[]> DownloadOriginalImageAsync(string blobKey, CancellationToken cancellationToken)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_options.OriginalsContainer);
        var blobClient = containerClient.GetBlobClient(blobKey);

        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            throw new FileNotFoundException($"Original image blob not found: {blobKey} in container {_options.OriginalsContainer}");
        }

        var downloadResult = await blobClient.DownloadContentAsync(cancellationToken);
        return downloadResult.Value.Content.ToArray();
    }

    public async Task<string> UploadAnnotationAsync(string blobKey, byte[] data, string contentType, CancellationToken cancellationToken)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_options.AnnotationsContainer);
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

        var blobClient = containerClient.GetBlobClient(blobKey);
        using var stream = new MemoryStream(data);
        var headers = new BlobHttpHeaders { ContentType = contentType };

        await blobClient.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = headers }, cancellationToken);
        _logger.LogInformation("Uploaded annotation blob {BlobKey} ({Bytes} bytes)", blobKey, data.Length);

        return blobClient.Uri.ToString();
    }
}
