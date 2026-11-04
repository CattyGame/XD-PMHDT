using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AURA.Analysis.Api.Outbox;

// Exact supported branches of the pinned Sprint3 event0.7/job0.6 schemas.
// No dynamic schema fetch, coercion, default insertion or payload rewriting.
internal static class OutboxContractValidator
{
    private static readonly Regex Correlation = new("^[A-Za-z0-9._-]{1,64}$");
    private static readonly Regex Timestamp = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$");
    private static bool Keys(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject()) if (!seen.Add(p.Name)) return false;
        return seen.SetEquals(names);
    }
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Uuid(JsonElement e, string n) => Str(e,n) is { } s && Guid.TryParseExact(s,"D",out var g) && g!=Guid.Empty && g.ToString("D")==s;
    private static bool Integer(JsonElement e, string n) => e.TryGetProperty(n,out var p) && p.ValueKind==JsonValueKind.Number && p.TryGetInt32(out var v) && v>=1;
    private static bool NonNegativeInteger(JsonElement e, string n) => e.TryGetProperty(n,out var p) && p.ValueKind==JsonValueKind.Number && p.TryGetInt32(out var v) && v>=0;
    private static bool Boolean(JsonElement e, string n) => e.TryGetProperty(n,out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False;
    public static bool IsValid(JsonElement e, bool job)
    {
        if (!Keys(e,job ? new[]{"messageId","messageType","schemaVersion","occurredAt","tenantId","correlationId","jobId","analysisId","batchId","imageFileId","attempt"}
                        : new[]{"eventId","eventType","schemaVersion","occurredAt","tenantId","correlationId","analysisId","data"})) return false;
        if (Str(e,"schemaVersion")!=(job?"0.6":"0.7") || !Uuid(e,job?"messageId":"eventId") || !Uuid(e,"tenantId") || !Uuid(e,"analysisId")) return false;
        var stamp=Str(e,"occurredAt"); var corr=Str(e,"correlationId");
        if (stamp is null || !Timestamp.IsMatch(stamp) || !DateTimeOffset.TryParse(stamp,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out _) || corr is null || !Correlation.IsMatch(corr)) return false;
        if (job) return Str(e,"messageType")=="analysis.job.run" && Uuid(e,"jobId") && Uuid(e,"batchId") && Uuid(e,"imageFileId") && Integer(e,"attempt");
        if (!e.TryGetProperty("data",out var d) || d.ValueKind!=JsonValueKind.Object) return false;
        switch (Str(e,"eventType"))
        {
            case "analysis.requested":
                return Keys(d,"patientId","batchId") && Uuid(d,"patientId") && Uuid(d,"batchId");
            case "analysis.completed":
                if (!Keys(d,"patientId","riskLevel","modelVersion","isMock","configVersion","thresholdVersion","resultVersion","riskAvailable") || !Uuid(d,"patientId") || !Boolean(d,"isMock") || !Boolean(d,"riskAvailable") || !Integer(d,"resultVersion") || Str(d,"modelVersion") is null || Str(d,"configVersion") is null || Str(d,"thresholdVersion") is null) return false;
                return d.GetProperty("riskAvailable").GetBoolean() ? Str(d,"riskLevel") is "LOW" or "MODERATE" or "HIGH" : d.GetProperty("riskLevel").ValueKind==JsonValueKind.Null;
            case "analysis.failed":
                return Keys(d,"patientId","errorCode","retryable","terminalStatus") && Uuid(d,"patientId") && d.GetProperty("retryable").ValueKind==JsonValueKind.False
                    && (Str(d,"terminalStatus") is "FAILED" or "LOW_QUALITY" or "REJECTED")
                    && (Str(d,"errorCode") is "ERR_AI_FAILED" or "ERR_TIMEOUT" or "ERR_NETWORK" or "ERR_IMAGE_LOW_QUALITY" or "ERR_IMAGE_MISSING" or "ERR_UNAUTHORIZED" or "ERR_VALIDATION" or "ERR_AI_OUTPUT_INVALID");
            case "batch.completed":
                if (!Keys(d,"batchId","patientId","batchStatus","totalImages","completedImages","failedImages")
                    || !Uuid(d,"batchId") || !Uuid(d,"patientId")
                    || Str(d,"batchStatus") is not ("COMPLETED" or "PARTIAL_COMPLETED" or "FAILED")
                    || !NonNegativeInteger(d,"completedImages") || !NonNegativeInteger(d,"failedImages")
                    || !Integer(d,"totalImages") || d.GetProperty("totalImages").GetInt32() > 100) return false;
                return d.GetProperty("completedImages").GetInt32() + d.GetProperty("failedImages").GetInt32()
                    == d.GetProperty("totalImages").GetInt32()
                    && Uuid(e,"analysisId") && (Str(d,"batchStatus") switch
                    {
                        "COMPLETED" => d.GetProperty("failedImages").GetInt32() == 0,
                        "PARTIAL_COMPLETED" => d.GetProperty("completedImages").GetInt32() > 0 && d.GetProperty("failedImages").GetInt32() > 0,
                        "FAILED" => d.GetProperty("completedImages").GetInt32() == 0 && d.GetProperty("failedImages").GetInt32() > 0,
                        _ => false
                    });
            default: return false;
        }
    }
}
