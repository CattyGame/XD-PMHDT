using System.Security.Cryptography;
using System.Text.Json;

namespace AURA.Analysis.Api.Upload;

/// <summary>Version 1: ordinal object keys, original array order, Int64 or finite IEEE754 JSON numbers.</summary>
public static class ReportSnapshotDigest
{
    public static string Compute(string json)
    {
        using var document=JsonDocument.Parse(json);
        using var buffer=new MemoryStream();
        using(var writer=new Utf8JsonWriter(buffer))Write(writer,document.RootElement);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }
    private static void Write(Utf8JsonWriter writer,JsonElement element)
    {
        switch(element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach(var p in element.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)){writer.WritePropertyName(p.Name);Write(writer,p.Value);}
                writer.WriteEndObject();break;
            case JsonValueKind.Array:
                writer.WriteStartArray();foreach(var value in element.EnumerateArray())Write(writer,value);writer.WriteEndArray();break;
            case JsonValueKind.Number:
                if(element.TryGetInt64(out var integer))writer.WriteNumberValue(integer);else writer.WriteNumberValue(element.GetDouble());break;
            default:element.WriteTo(writer);break;
        }
    }
}
