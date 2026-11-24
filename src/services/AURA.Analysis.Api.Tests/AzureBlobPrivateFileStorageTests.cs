using AURA.Analysis.Api.Upload;
using Xunit;

namespace AURA.Analysis.Api.Tests;

public sealed class AzureBlobPrivateFileStorageTests
{
    private const string Key = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private static BlobStorageOptions Options(string endpoint = "http://127.0.0.1:10000/devstoreaccount1",
        string browser = "http://127.0.0.1:10000/devstoreaccount1") => new()
    {
        Provider = "AzureBlob",
        ConnectionString = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={Key};BlobEndpoint={endpoint};",
        PublicBlobEndpoint = browser
    };
    private static string ObjectKey => $"{Guid.NewGuid():D}/{Guid.NewGuid():D}/{Guid.NewGuid():D}";

    [Theory]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1")]
    [InlineData("http://azurite:10000/devstoreaccount1")]
    public void Explicit_local_config_is_accepted_in_development(string endpoint)
    {
        Assert.NotNull(new AzureBlobPrivateFileStorage(Options(endpoint), true));
    }

    [Fact]
    public void Explicit_local_TLS_is_accepted_without_allowing_external_or_production_emulators()
    {
        foreach (var endpoint in new[] { "https://127.0.0.1:10000/devstoreaccount1", "https://azurite:10000/devstoreaccount1" })
        {
            var options = Options(endpoint, "https://127.0.0.1:10000/devstoreaccount1");
            options.ConnectionString = options.ConnectionString.Replace("Protocol=http;", "Protocol=https;");
            Assert.NotNull(new AzureBlobPrivateFileStorage(options, true));
            Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(options, false));
        }
        var external = Options("https://external.invalid:10000/devstoreaccount1");
        external.ConnectionString = external.ConnectionString.Replace("Protocol=http;", "Protocol=https;");
        Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(external, true));
    }

    [Fact]
    public void Emulator_cannot_be_enabled_in_production()
    {
        Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(Options(), false));
    }

    [Theory]
    [InlineData("http://remote.example:10000/devstoreaccount1")]
    [InlineData("http://localhost:10000/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10001/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/anotheraccount")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1?sig=do-not-log")]
    [InlineData("http://user:password@127.0.0.1:10000/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10000/devstoreaccount1#fragment")]
    public void Unsafe_endpoints_fail_without_leaking_the_input(string endpoint)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(Options(endpoint), true));
        Assert.Equal("Invalid private Blob storage configuration.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Browser_endpoint_cannot_point_at_docker_dns()
    {
        Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(
            Options(browser: "http://azurite:10000/devstoreaccount1"), true));
    }

    [Fact]
    public void Credentials_are_required_and_have_no_fallback()
    {
        Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(new BlobStorageOptions(), true));
        var options = Options(); options.ConnectionString = options.ConnectionString.Replace(Key, "bad-key");
        Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(options, true));
        options = Options(); options.AnnotationContainer = options.OriginalContainer;
        Assert.Throws<InvalidOperationException>(() => new AzureBlobPrivateFileStorage(options, true));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("patient-name/photo.png")]
    [InlineData("staging/not-a-uuid/11111111-1111-4111-8111-111111111111")]
    [InlineData("staging/11111111-1111-4111-8111-111111111111/../../escape")]
    [InlineData("staging/11111111-1111-4111-8111-111111111111/00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-1111-4111-8111-111111111111/11111111-1111-4111-8111-111111111111/11111111-1111-4111-8111-111111111111/patient-photo.png")]
    [InlineData("11111111-1111-4111-8111-111111111111/11111111-1111-4111-8111-111111111111/11111111-1111-4111-8111-111111111111/mask-00000000-0000-0000-0000-000000000000.png")]
    [InlineData("00000000-0000-0000-0000-000000000000/00000000-0000-0000-0000-000000000000/00000000-0000-0000-0000-000000000000")]
    public async Task Unsafe_object_names_are_rejected_before_any_request(string key)
    {
        var storage = new AzureBlobPrivateFileStorage(Options(), true);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync("aura-originals", key, default));
    }

    [Fact]
    public async Task Staging_keys_reach_content_validation_without_relaxing_payload_checks()
    {
        var storage=new AzureBlobPrivateFileStorage(Options(),true);
        var annotations=new[]{"mask","overlay","annotated","heatmap"}.Select(kind=>$"{ObjectKey}/{kind}-{Guid.NewGuid():D}.png");
        foreach(var key in new[]{ObjectKey,$"staging/{Guid.NewGuid():D}/{Guid.NewGuid():D}"}.Concat(annotations))
        {
            using var empty=new MemoryStream();
            var error=await Assert.ThrowsAsync<ArgumentException>(()=>storage.PutAsync("aura-originals",key,empty,"image/png",default));
            Assert.Equal("Invalid storage content.",error.Message);
        }
    }

    [Fact]
    public async Task Unconfigured_container_is_rejected_before_any_request()
    {
        var storage = new AzureBlobPrivateFileStorage(Options(), true);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync("other-container", ObjectKey, default));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(901)]
    public async Task Out_of_range_TTL_is_rejected_before_any_request(int seconds)
    {
        var storage = new AzureBlobPrivateFileStorage(Options(), true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            storage.CreateReadAccessAsync("aura-originals", ObjectKey, TimeSpan.FromSeconds(seconds), default));
    }

    [Fact]
    public async Task Cancelled_caller_stops_all_operations_before_IO()
    {
        var storage = new AzureBlobPrivateFileStorage(Options(), true);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var input = new MemoryStream([1, 2, 3]);
        var key = ObjectKey;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.PutAsync("aura-originals", key, input, "image/png", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.OpenReadAsync("aura-originals", key, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.DeleteAsync("aura-originals", key, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.CreateReadAccessAsync("aura-originals", key, TimeSpan.FromSeconds(120), cancelled.Token));
    }
}
