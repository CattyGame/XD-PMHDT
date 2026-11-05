using System.Diagnostics;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Xunit;

namespace AURA.M1.RegressionTests;

public sealed class M1StorageFactAttribute : FactAttribute
{
    public M1StorageFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AURA_TEST_AZURITE_CONNECTION")))
            Skip="Start isolated Azurite on loopback and set AURA_TEST_AZURITE_CONNECTION.";
    }
}

public class PrivateStorageTests
{
    [M1StorageFact]
    public async Task Actual_private_blob_bytes_roundtrip_and_tampering_or_public_access_is_rejected()
    {
        var connection=Environment.GetEnvironmentVariable("AURA_TEST_AZURITE_CONNECTION")!;
        var service=new BlobServiceClient(connection);
        if(!service.Uri.IsLoopback)throw new InvalidOperationException("Storage tests require isolated loopback Azurite.");
        var id=Guid.NewGuid().ToString("N");
        var containers=new[]{"originals","masks","reports"}.Select(n=>service.GetBlobContainerClient($"m1-test-{id}-{n}")).ToArray();
        var root=Path.Combine(Path.GetTempPath(),"aura-m1-storage-"+id);
        var assets=Path.Combine(root,"assets");
        var expected=new Dictionary<string,byte[]>();
        try
        {
            foreach(var c in containers)
            {
                await c.CreateAsync(PublicAccessType.None);
                var bytes=RandomNumberGenerator.GetBytes(8192);expected[c.Name]=bytes;
                await c.GetBlobClient("tenant/private/object.bin").UploadAsync(BinaryData.FromBytes(bytes));
            }
            Assert.Equal(0,await RunAsync("export",assets));
            Assert.Equal(3,Directory.GetFiles(assets,"*.blob").Length);
            foreach(var c in containers)await c.DeleteAsync();
            Assert.Equal(0,await RunAsync("restore",assets,service.Uri.AbsoluteUri));
            foreach(var c in containers)
            {
                Assert.Equal(PublicAccessType.None,(await c.GetAccessPolicyAsync()).Value.BlobPublicAccess);
                Assert.Equal(expected[c.Name],(await c.GetBlobClient("tenant/private/object.bin").DownloadContentAsync()).Value.Content.ToArray());
            }
            Assert.NotEqual(0,await RunAsync("verify",assets,"http://127.0.0.1:1/wrong-account"));
            var file=Directory.GetFiles(assets,"*.blob")[0];var original=await File.ReadAllBytesAsync(file);
            var tampered=(byte[])original.Clone();tampered[10]^=1;await File.WriteAllBytesAsync(file,tampered);
            Assert.NotEqual(0,await RunAsync("verify",assets,service.Uri.AbsoluteUri));
            await File.WriteAllBytesAsync(file,original);
            await containers[0].SetAccessPolicyAsync(PublicAccessType.Blob);
            Assert.NotEqual(0,await RunAsync("export",Path.Combine(root,"public-export")));
        }
        finally
        {
            foreach(var c in containers)await c.DeleteIfExistsAsync();
            if(Directory.Exists(root))Directory.Delete(root,true);
        }

        async Task<int> RunAsync(params string[] args)
        {
            var repo=new DirectoryInfo(AppContext.BaseDirectory);
            while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"AURA.sln")))repo=repo.Parent;
            Assert.NotNull(repo);
            var start=new ProcessStartInfo("dotnet") {RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            foreach(var arg in new[]{"run","--project",Path.Combine(repo.FullName,"tools/AURA.PrivateStorage.Backup"),"--no-launch-profile","--"}.Concat(args))start.ArgumentList.Add(arg);
            start.Environment["AURA_STORAGE_CONNECTION_STRING"]=connection;
            start.Environment["AURA_STORAGE_CONTAINERS"]=string.Join(',',containers.Select(c=>c.Name));
            using var process=Process.Start(start)!;
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try {await process.WaitForExitAsync(timeout.Token);}
            catch {process.Kill(entireProcessTree:true);throw;}
            await Task.WhenAll(stdout,stderr);
            return process.ExitCode;
        }
    }
}
