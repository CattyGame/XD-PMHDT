using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    if (args.Length < 2 || args[0] is not ("export" or "restore" or "verify")) throw new ArgumentException("export|restore|verify directory [expected endpoint]");
    var service=new BlobServiceClient(Environment.GetEnvironmentVariable("AURA_STORAGE_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Storage connection required."));
    var directory=Path.GetFullPath(args[1]);
    var manifestFile=Path.Combine(Directory.GetParent(directory)!.FullName,"assets.manifest.json");
    var json=new JsonSerializerOptions(JsonSerializerDefaults.Web) {WriteIndented=true};
    if (args[0]=="export")
    {
        var names=(Environment.GetEnvironmentVariable("AURA_STORAGE_CONTAINERS")
            ?? "aura-originals,aura-annotations,aura-reports").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if (names.Length==0 || names.Distinct().Count()!=names.Length) throw new ArgumentException("Invalid container inventory.");
        Directory.CreateDirectory(directory);
        var items=new List<StoredObject>();
        foreach(var name in names)
        {
            var container=service.GetBlobContainerClient(name);
            var acl=await container.GetAccessPolicyAsync();
            if (acl.Value.BlobPublicAccess!=PublicAccessType.None) throw new InvalidOperationException("Backup requires private storage.");
            await foreach(var blob in container.GetBlobsAsync())
            {
                var file=Guid.NewGuid().ToString("N")+".blob";
                var source=await container.GetBlobClient(blob.Name).DownloadStreamingAsync(new BlobDownloadOptions {
                    Conditions=new BlobRequestConditions {IfMatch=blob.Properties.ETag!.Value}
                });
                await using var content=source.Value.Content;
                await using (var output=File.Create(Path.Combine(directory,file))) await content.CopyToAsync(output);
                var info=new FileInfo(Path.Combine(directory,file));
                using var input=info.OpenRead();
                items.Add(new(name,blob.Name,file,info.Length,Convert.ToHexString(SHA256.HashData(input)),source.Value.Details.ContentType));
            }
        }
        await File.WriteAllTextAsync(manifestFile,JsonSerializer.Serialize(new StorageManifest("AzureBlob",names,items),json));
        Console.WriteLine($"Exported {items.Count} objects from {names.Length} private containers.");
    }
    else
    {
        if (args.Length!=3 || service.Uri.AbsoluteUri.TrimEnd('/')!=args[2].TrimEnd('/')) throw new InvalidOperationException("Restore endpoint not confirmed.");
        var manifest=JsonSerializer.Deserialize<StorageManifest>(await File.ReadAllTextAsync(manifestFile),json)
            ?? throw new InvalidDataException("Missing storage manifest.");
        if (manifest.Provider!="AzureBlob") throw new InvalidDataException("Wrong provider.");
        if (manifest.Containers.Length==0 || manifest.Containers.Distinct().Count()!=manifest.Containers.Length)
            throw new InvalidDataException("Invalid container inventory.");
        foreach(var item in manifest.Objects)
        {
            if (!manifest.Containers.Contains(item.Container)) throw new InvalidDataException("Object outside declared container inventory.");
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(item.File),"N",out _) || item.File!=Path.GetFileName(item.File) || !item.File.EndsWith(".blob"))
                throw new InvalidDataException("Invalid object file path.");
            var path=Path.Combine(directory,item.File);
            using var check=File.OpenRead(path);
            if (check.Length!=item.Length || Convert.ToHexString(SHA256.HashData(check))!=item.Sha256) throw new InvalidDataException("Object checksum mismatch.");
        }
        if (args[0]=="verify")
        {
            foreach(var name in manifest.Containers)
            {
                var c=service.GetBlobContainerClient(name);
                if ((await c.ExistsAsync()).Value && (await c.GetAccessPolicyAsync()).Value.BlobPublicAccess!=PublicAccessType.None)
                    throw new InvalidOperationException("Restore target is public.");
            }
            return 0;
        }
        foreach(var name in manifest.Containers)
        {
            var c=service.GetBlobContainerClient(name);
            await c.CreateIfNotExistsAsync(PublicAccessType.None);
            if ((await c.GetAccessPolicyAsync()).Value.BlobPublicAccess!=PublicAccessType.None) throw new InvalidOperationException("Restore target is public.");
        }
        foreach(var item in manifest.Objects)
        {
            if (!manifest.Containers.Contains(item.Container)) throw new InvalidDataException("Object outside declared container inventory.");
            var blob=service.GetBlobContainerClient(item.Container).GetBlobClient(item.Name);
            await using(var input=File.OpenRead(Path.Combine(directory,item.File)))
                await blob.UploadAsync(input,new BlobUploadOptions {HttpHeaders=new BlobHttpHeaders {ContentType=item.ContentType}});
            var downloaded=await blob.DownloadStreamingAsync();
            await using var contents=downloaded.Value.Content;
            if (Convert.ToHexString(await SHA256.HashDataAsync(contents))!=item.Sha256) throw new InvalidDataException("Restored object checksum mismatch.");
        }
        Console.WriteLine($"Restored and verified {manifest.Objects.Count} private objects.");
    }
    return 0;
}
catch(Exception ex)
{
    Console.Error.WriteLine($"Private storage backup/restore failed ({ex.GetType().Name}).");
    return 1;
}

record StoredObject(string Container,string Name,string File,long Length,string Sha256,string? ContentType);
record StorageManifest(string Provider,string[] Containers,List<StoredObject> Objects);
