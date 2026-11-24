using Azure.Storage.Blobs;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aura.Worker.Analysis.Tests.Services;

public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_TEST_AZURITE_CONNECTION")))
            Skip = "Requires the explicitly isolated Azurite fixture.";
    }
}

public class BlobStorageCompatibilityTests
{
    [AzuriteFact]
    public async Task Worker_download_and_private_annotation_upload_use_the_supported_storage_protocol()
    {
        var connection = Environment.GetEnvironmentVariable("AURA_TEST_AZURITE_CONNECTION")!;
        var suffix = Guid.NewGuid().ToString("N");
        var source = "s7-source-" + suffix;
        var annotations = "s7-annotations-" + suffix;
        var storage = new BlobServiceClient(connection, new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03));
        var input = storage.GetBlobContainerClient(source);
        var output = storage.GetBlobContainerClient(annotations);
        try
        {
            await input.CreateAsync();
            byte[] bytes = [1, 2, 3, 4];
            await input.GetBlobClient("original").UploadAsync(new BinaryData(bytes));
            var worker = new BlobStorageService(Options.Create(new BlobStorageOptions {
                ConnectionString = connection, OriginalsContainer = source, AnnotationsContainer = annotations }),
                NullLogger<BlobStorageService>.Instance);
            Assert.Equal(bytes, await worker.DownloadOriginalImageAsync("original", default));
            await worker.UploadAnnotationAsync("mask", bytes, "image/png", default);
            Assert.Equal(bytes, (await output.GetBlobClient("mask").DownloadContentAsync()).Value.Content.ToArray());
            Assert.Equal(Azure.Storage.Blobs.Models.PublicAccessType.None, (await output.GetAccessPolicyAsync()).Value.BlobPublicAccess);
        }
        finally
        {
            await input.DeleteIfExistsAsync();
            await output.DeleteIfExistsAsync();
        }
    }
}
