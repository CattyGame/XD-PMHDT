using System.Data.Common;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace AURA.Analysis.Api.Upload;

public sealed class BlobStorageOptions
{
    public string Provider { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string PublicBlobEndpoint { get; set; } = "";
    public string OriginalContainer { get; set; } = "aura-originals";
    public string AnnotationContainer { get; set; } = "aura-annotations";
    public string ReportContainer { get; set; } = "aura-reports";
}

/// <summary>Shared-key adapter for the configured Azure account or Development Azurite.
/// Containers must already exist with private ACLs. This adapter never changes their ACLs.</summary>
public sealed class AzureBlobPrivateFileStorage : IPrivateFileStorage, IPrivateStorageReadiness
{
    private readonly BlobServiceClient _service;
    private readonly BlobServiceClient _publicService;
    private readonly HashSet<string> _containers;
    private readonly TimeProvider _clock;
    private readonly bool _emulator;

    public AzureBlobPrivateFileStorage(BlobStorageOptions options, bool development,
        TimeProvider? clock = null)
    {
        // Never include connection strings, endpoint queries or SDK exceptions in configuration errors.
        try
        {
            if (options.Provider != "AzureBlob") throw new ArgumentException();
            var fields = new DbConnectionStringBuilder { ConnectionString = options.ConnectionString };
            var names = fields.Keys.Cast<string>().ToArray();
            var allowed = new[] { "DefaultEndpointsProtocol", "AccountName", "AccountKey", "BlobEndpoint" };
            if (names.Length != 4 || names.Any(n => !allowed.Contains(n, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException();
            var account = (string)fields["AccountName"];
            var key = (string)fields["AccountKey"];
            if (!Regex.IsMatch(account, "^[a-z0-9]{3,24}$") || Convert.FromBase64String(key).Length != 64)
                throw new ArgumentException();
            var endpoint = new Uri((string)fields["BlobEndpoint"], UriKind.Absolute);
            var browser = new Uri(options.PublicBlobEndpoint, UriKind.Absolute);
            _emulator = account == "devstoreaccount1";
            ValidateEndpoint(endpoint, account, development, _emulator, browserEndpoint: false);
            ValidateEndpoint(browser, account, development, _emulator, browserEndpoint: true);
            if ((string)fields["DefaultEndpointsProtocol"] != endpoint.Scheme) throw new ArgumentException();
            _containers = new[] { options.OriginalContainer, options.AnnotationContainer, options.ReportContainer }
                .ToHashSet(StringComparer.Ordinal);
            if (_containers.Count != 3 || _containers.Any(c => !Regex.IsMatch(c, "^[a-z0-9]+(?:-[a-z0-9]+)*$")
                    || c.Length < 3 || c.Length > 63)) throw new ArgumentException();
            var credential = new StorageSharedKeyCredential(account, key);
            _service = new BlobServiceClient(endpoint, credential, ClientOptions());
            _publicService = new BlobServiceClient(browser, credential, ClientOptions());
            _clock = clock ?? TimeProvider.System;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidCastException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Invalid private Blob storage configuration.");
        }
    }

    private static BlobClientOptions ClientOptions()
    {
        // Explicit service version also works with the pinned Azurite 3.33.0 emulator.
        var result = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2023_11_03);
        result.Retry.MaxRetries = 0; // Reconciliation belongs to the caller, not an SDK retry loop.
        result.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        result.Diagnostics.IsLoggingEnabled = false;
        result.Diagnostics.IsLoggingContentEnabled = false;
        result.Diagnostics.IsDistributedTracingEnabled = false;
        return result;
    }

    private static void ValidateEndpoint(Uri endpoint, string account, bool development, bool emulator,
        bool browserEndpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)) throw new ArgumentException();
        if (emulator)
        {
            if (!development || endpoint.Scheme is not ("http" or "https") || endpoint.Port != 10000
                || endpoint.AbsolutePath.TrimEnd('/') != "/devstoreaccount1"
                || !(endpoint.Host == "127.0.0.1" || (!browserEndpoint && endpoint.Host == "azurite")))
                throw new ArgumentException();
        }
        else if (endpoint.Scheme != "https" || endpoint.Port != 443
            || endpoint.Host != account + ".blob.core.windows.net" || endpoint.AbsolutePath != "/")
            throw new ArgumentException();
    }

    private BlobContainerClient Container(string container, string key)
    {
        if (!_containers.Contains(container)) throw new ArgumentException("Unconfigured storage container.");
        // Accept only server-generated UUID keys, upload staging, and the Worker's typed PNG names.
        var segments = key.Split('/');
        static bool Id(string s) => (Guid.TryParseExact(s, "D", out var id) || Guid.TryParseExact(s, "N", out id)) && id != Guid.Empty;
        var staging = segments.Length == 3 && segments[0] == "staging" && segments.Skip(1).All(Id);
        var annotation = Regex.Match(segments[^1], @"\A(?:mask|overlay|annotated|heatmap)-(?<id>[0-9a-fA-F-]{36})\.png\z");
        var typedPng = segments.Length == 4 && segments.Take(3).All(Id)
            && annotation.Success && Id(annotation.Groups["id"].Value);
        if (!staging && !typedPng && (segments.Length is < 3 or > 6 || !segments.All(Id)))
            throw new ArgumentException("Invalid Analysis object key.");
        return _service.GetBlobContainerClient(container);
    }

    private static async Task RequirePrivateAsync(BlobContainerClient container, CancellationToken ct)
    {
        var acl = await container.GetAccessPolicyAsync(cancellationToken: ct);
        if (acl.Value.BlobPublicAccess != PublicAccessType.None)
            throw new InvalidOperationException("Storage container must be private.");
    }

    public async Task<bool> CheckPrivateContainersAsync(CancellationToken ct)
    {
        foreach(var name in _containers) await RequirePrivateAsync(_service.GetBlobContainerClient(name),ct);
        return true;
    }

    public async Task PutAsync(string container, string key, Stream content, string contentType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = Container(container, key);
        if (contentType is not ("image/png" or "image/jpeg" or "application/pdf" or "text/csv" or "application/zip")
            || !content.CanRead || !content.CanSeek || content.Length <= content.Position)
            throw new ArgumentException("Invalid storage content.");
        var position = content.Position;
        var length = content.Length - position;
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, ct));
        content.Position = position;
        await RequirePrivateAsync(target, ct);
        var blob = target.GetBlobClient(key);
        try
        {
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType, CacheControl = "no-store" },
                Metadata = new Dictionary<string, string> { ["sha256"] = sha }
            }, ct);
        }
        catch (RequestFailedException e) when ((e.Status == 409 && e.ErrorCode == "BlobAlreadyExists")
            || (e.Status == 412 && e.ErrorCode == "ConditionNotMet"))
        {
            // Same deterministic key may be retried after a lost acknowledgement. Never overwrite it.
            var existing = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            if (existing.ContentLength != length || existing.ContentType != contentType
                || !existing.Metadata.TryGetValue("sha256", out var oldSha) || oldSha != sha)
                throw new InvalidOperationException("Object key already contains different content.");
        }
    }

    public async Task<Stream> OpenReadAsync(string container, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = Container(container, key);
        await RequirePrivateAsync(target, ct);
        return (await target.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: ct)).Value.Content;
    }

    public async Task DeleteAsync(string container, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = Container(container, key);
        await RequirePrivateAsync(target, ct);
        await target.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task<FileReadAccess> CreateReadAccessAsync(string container, string key, TimeSpan ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromMinutes(15) || ttl.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Read TTL must be 1..900 whole seconds.");
        var target = Container(container, key);
        await RequirePrivateAsync(target, ct);
        await target.GetBlobClient(key).GetPropertiesAsync(cancellationToken: ct);
        var now = DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().ToUnixTimeSeconds());
        var expires = now.Add(ttl);
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container, BlobName = key, Resource = "b",
            StartsOn = now.AddMinutes(-5), ExpiresOn = expires,
            Protocol = _publicService.Uri.Scheme == "https" ? SasProtocol.Https : SasProtocol.HttpsAndHttp,
            CacheControl = "no-store"
        };
        sas.SetPermissions(BlobSasPermissions.Read);
        var url = _publicService.GetBlobContainerClient(container).GetBlobClient(key).GenerateSasUri(sas);
        return new FileReadAccess(url, expires);
    }
}

public static class AnalysisPrivateStorageRegistration
{
    public static IServiceCollection AddAnalysisPrivateStorage(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(StorageOptions.SectionName).Get<BlobStorageOptions>()
            ?? throw new InvalidOperationException("Private Blob storage configuration is required.");
        // Validate immediately; fail startup if credentials/endpoints are missing or unsafe.
        services.AddSingleton<IPrivateFileStorage>(new AzureBlobPrivateFileStorage(options, environment.IsDevelopment()));
        return services;
    }
}
