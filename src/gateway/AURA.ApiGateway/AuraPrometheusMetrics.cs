using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AURA.ApiGateway;

public static class AuraPrometheusMetrics
{
    private static readonly DateTime StartTime = DateTime.UtcNow;
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    // Key: $"{method}|{status}|{sanitizedPath}"
    private static readonly ConcurrentDictionary<string, long> RequestCounts = new();

    // Standard Prometheus histogram exponential buckets (in seconds)
    public static readonly double[] HistogramBuckets =
    [
        0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0
    ];
    private static readonly long[] BucketCounts = new long[HistogramBuckets.Length];
    private static long TotalRecordedRequests = 0;
    private static long TotalDurationTicks = 0;

    // Fixed-size thread-safe circular reservoir buffer for sliding-window quantile estimation
    public const int ReservoirCapacity = 4096;
    private static readonly double[] LatencyReservoir = new double[ReservoirCapacity];
    private static long ReservoirSequence = 0;

    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public static IApplicationBuilder UseAuraPrometheusMetrics(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await next(context);
            }
            finally
            {
                sw.Stop();
                var durationSeconds = sw.Elapsed.TotalSeconds;
                RecordRequest(context.Request.Method, context.Response.StatusCode, context.Request.Path.Value ?? "/", durationSeconds, sw.ElapsedTicks);
            }
        });
    }

    public static void RecordRequest(string method, int statusCode, string rawPath, double durationSeconds, long elapsedTicks = 0)
    {
        var sanitizedPath = SanitizePath(rawPath);
        var key = $"{method}|{statusCode}|{sanitizedPath}";
        RequestCounts.AddOrUpdate(key, 1, (_, count) => count + 1);

        Interlocked.Increment(ref TotalRecordedRequests);
        if (elapsedTicks > 0)
        {
            Interlocked.Add(ref TotalDurationTicks, elapsedTicks);
        }
        else
        {
            var ticks = (long)(durationSeconds * Stopwatch.Frequency);
            Interlocked.Add(ref TotalDurationTicks, ticks);
        }

        // Bounded Histogram bucket increments (O(B), thread-safe)
        for (int i = 0; i < HistogramBuckets.Length; i++)
        {
            if (durationSeconds <= HistogramBuckets[i])
            {
                Interlocked.Increment(ref BucketCounts[i]);
            }
        }

        // Rolling circular reservoir (O(1), non-blocking, zero memory leak, never freezes)
        long seq = Interlocked.Increment(ref ReservoirSequence);
        int slot = (int)((seq - 1) % ReservoirCapacity);
        LatencyReservoir[slot] = durationSeconds;
    }

    public static IEndpointRouteBuilder MapAuraMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metrics", async context =>
        {
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            var text = GenerateMetricsText();
            await context.Response.WriteAsync(text);
        });

        return endpoints;
    }

    public static string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var sanitized = GuidRegex.Replace(path, "{id}");
        // Remove trailing slash if not root
        if (sanitized.Length > 1 && sanitized.EndsWith('/'))
        {
            sanitized = sanitized[..^1];
        }
        return sanitized;
    }

    public static long GetTotalRecordedRequests() => Interlocked.Read(ref TotalRecordedRequests);

    public static string GenerateMetricsText()
    {
        var sb = new StringBuilder();
        var uptimeSeconds = (DateTime.UtcNow - StartTime).TotalSeconds;

        CurrentProcess.Refresh();
        var memBytes = CurrentProcess.WorkingSet64;
        var cpuSeconds = CurrentProcess.TotalProcessorTime.TotalSeconds;

        sb.AppendLine("# HELP http_requests_total Total number of HTTP requests processed");
        sb.AppendLine("# TYPE http_requests_total counter");

        if (RequestCounts.IsEmpty)
        {
            sb.AppendLine("http_requests_total{method=\"GET\",status=\"200\",path=\"/health\"} 0");
        }
        else
        {
            foreach (var kvp in RequestCounts)
            {
                var parts = kvp.Key.Split('|');
                var method = parts[0];
                var status = parts[1];
                var path = parts[2];
                sb.AppendLine($"http_requests_total{{method=\"{method}\",status=\"{status}\",path=\"{path}\"}} {kvp.Value}");
            }
        }

        // Bounded Rolling Reservoir quantiles
        long totalCount = Interlocked.Read(ref TotalRecordedRequests);
        int sampleCount = (int)Math.Min(totalCount, ReservoirCapacity);

        double p50 = 0.001, p95 = 0.005, p99 = 0.015;
        if (sampleCount > 0)
        {
            var snapshot = new double[sampleCount];
            Array.Copy(LatencyReservoir, snapshot, sampleCount);
            Array.Sort(snapshot);
            p50 = snapshot[(int)(sampleCount * 0.50)];
            p95 = snapshot[(int)(sampleCount * 0.95)];
            p99 = snapshot[(int)(sampleCount * 0.99)];
        }

        double sumSeconds = Stopwatch.Frequency > 0
            ? (double)Interlocked.Read(ref TotalDurationTicks) / Stopwatch.Frequency
            : 0;

        // Histogram Buckets
        sb.AppendLine("# HELP http_request_duration_seconds_bucket HTTP request latency histogram buckets in seconds");
        sb.AppendLine("# TYPE http_request_duration_seconds_bucket counter");
        for (int i = 0; i < HistogramBuckets.Length; i++)
        {
            sb.AppendLine($"http_request_duration_seconds_bucket{{le=\"{HistogramBuckets[i]}\"}} {Interlocked.Read(ref BucketCounts[i])}");
        }
        sb.AppendLine($"http_request_duration_seconds_bucket{{le=\"+Inf\"}} {totalCount}");

        // Summary Quantiles
        sb.AppendLine("# HELP http_request_duration_seconds HTTP request latencies in seconds");
        sb.AppendLine("# TYPE http_request_duration_seconds summary");
        sb.AppendLine($"http_request_duration_seconds{{quantile=\"0.5\"}} {p50:F5}");
        sb.AppendLine($"http_request_duration_seconds{{quantile=\"0.95\"}} {p95:F5}");
        sb.AppendLine($"http_request_duration_seconds{{quantile=\"0.99\"}} {p99:F5}");
        sb.AppendLine($"http_request_duration_seconds_sum {sumSeconds:F5}");
        sb.AppendLine($"http_request_duration_seconds_count {totalCount}");

        // Process metrics
        sb.AppendLine("# HELP process_resident_memory_bytes Resident memory size in bytes");
        sb.AppendLine("# TYPE process_resident_memory_bytes gauge");
        sb.AppendLine($"process_resident_memory_bytes {memBytes}");

        sb.AppendLine("# HELP process_cpu_seconds_total Total user and system CPU time spent in seconds");
        sb.AppendLine("# TYPE process_cpu_seconds_total counter");
        sb.AppendLine($"process_cpu_seconds_total {cpuSeconds:F2}");

        sb.AppendLine("# HELP process_uptime_seconds The uptime of the process in seconds");
        sb.AppendLine("# TYPE process_uptime_seconds gauge");
        sb.AppendLine($"process_uptime_seconds {uptimeSeconds:F1}");

        // .NET Runtime
        sb.AppendLine("# HELP dotnet_total_memory_bytes Total memory allocated by GC in bytes");
        sb.AppendLine("# TYPE dotnet_total_memory_bytes gauge");
        sb.AppendLine($"dotnet_total_memory_bytes {GC.GetTotalMemory(false)}");

        sb.AppendLine("# HELP dotnet_gc_collections_total Number of GC collections");
        sb.AppendLine("# TYPE dotnet_gc_collections_total counter");
        sb.AppendLine($"dotnet_gc_collections_total{{generation=\"0\"}} {GC.CollectionCount(0)}");
        sb.AppendLine($"dotnet_gc_collections_total{{generation=\"1\"}} {GC.CollectionCount(1)}");
        sb.AppendLine($"dotnet_gc_collections_total{{generation=\"2\"}} {GC.CollectionCount(2)}");

        return sb.ToString();
    }
}
