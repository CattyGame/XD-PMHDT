using System.Buffers.Binary;
using System.Security.Cryptography;
using Aura.Worker.Analysis.Data.Repositories;

namespace Aura.Worker.Analysis.Processing;

public static class AnnotationMetadata
{
    public static AnnotationDescriptor FromPng(Guid fileId, string type, string objectKey, byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137,80,78,71,13,10,26,10];
        if (bytes.Length < 33 || !bytes.AsSpan(0,8).SequenceEqual(signature)
            || !bytes.AsSpan(12,4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("AI annotation is not a PNG.");
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4));
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000)
            throw new InvalidDataException("Invalid annotation geometry.");
        return new(fileId,type,objectKey,"image/png",bytes.LongLength,width,height,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
