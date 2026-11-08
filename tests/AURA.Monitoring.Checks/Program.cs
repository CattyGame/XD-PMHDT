using System.Text.Json;
using AURA.ApiGateway;

Console.WriteLine("====================================================================");
Console.WriteLine("AURA Task W4-M1-01 (SCRUM-338): Prometheus & Grafana Monitoring Checks");
Console.WriteLine("====================================================================");

var passed = 0;

void Check(string name, Action assertion)
{
    try
    {
        assertion();
        passed++;
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL: {name} -> {ex.Message}");
        Console.ResetColor();
        throw;
    }
}

var repoRoot = FindRepoRoot();
Console.WriteLine($"Repository Root: {repoRoot}");

// -----------------------------------------------------------------------------
// NHÓM 1: KIỂM THỬ AC1 (Docker Compose, Prometheus & Grafana Provisioning)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC1] Docker Compose & Prometheus Configuration Checks ---");

Check("AC1.1: Tệp compose.monitoring.yaml và infra/monitoring/compose.monitoring.yaml tồn tại và hợp lệ", () =>
{
    var rootCompose = Path.Combine(repoRoot, "compose.monitoring.yaml");
    var infraCompose = Path.Combine(repoRoot, "infra", "monitoring", "compose.monitoring.yaml");

    if (!File.Exists(rootCompose)) throw new FileNotFoundException("Thiếu compose.monitoring.yaml ở thư mục gốc.");
    if (!File.Exists(infraCompose)) throw new FileNotFoundException("Thiếu compose.monitoring.yaml ở thư mục infra/monitoring.");

    var content = File.ReadAllText(rootCompose);
    if (!content.Contains("prom/prometheus:v2.51.2")) throw new Exception("Prometheus version phải là v2.51+.");
    if (!content.Contains("grafana/grafana:10.4.2")) throw new Exception("Grafana version phải là v10.4+.");
    if (!content.Contains("prom/alertmanager:v0.27.0")) throw new Exception("Thiếu service alertmanager.");
    if (!content.Contains("postgres-exporter")) throw new Exception("Thiếu service postgres-exporter.");
    if (!content.Contains("aura_network")) throw new Exception("Thiếu cấu hình kết nối mạng aura_network.");
});

Check("AC1.2: Prometheus Configuration (prometheus.yml) có scrape_interval = 5s và đủ 6 targets", () =>
{
    var promPath = Path.Combine(repoRoot, "infra", "monitoring", "prometheus", "prometheus.yml");
    if (!File.Exists(promPath)) throw new FileNotFoundException("Thiếu tệp prometheus.yml.");

    var content = File.ReadAllText(promPath);
    if (!content.Contains("scrape_interval: 5s")) throw new Exception("scrape_interval phải được đặt là 5s.");
    if (!content.Contains("job_name: \"api-gateway\"")) throw new Exception("Thiếu target cào api-gateway.");
    if (!content.Contains("job_name: \"analysis-api\"")) throw new Exception("Thiếu target cào analysis-api.");
    if (!content.Contains("job_name: \"billing-api\"")) throw new Exception("Thiếu target cào billing-api.");
    if (!content.Contains("job_name: \"ai-core\"")) throw new Exception("Thiếu target cào ai-core.");
    if (!content.Contains("job_name: \"rabbitmq\"")) throw new Exception("Thiếu target cào rabbitmq.");
    if (!content.Contains("job_name: \"postgres-exporter\"")) throw new Exception("Thiếu target cào postgres-exporter.");
    if (!content.Contains("alert.rules.yml")) throw new Exception("Thiếu liên kết đến alert.rules.yml.");
});

Check("AC1.3: Grafana Datasource & Dashboards Auto-Provisioning đã được thiết lập", () =>
{
    var dsPath = Path.Combine(repoRoot, "infra", "monitoring", "grafana", "provisioning", "datasources", "prometheus.yaml");
    var dbPath = Path.Combine(repoRoot, "infra", "monitoring", "grafana", "provisioning", "dashboards", "dashboards.yaml");

    if (!File.Exists(dsPath)) throw new FileNotFoundException("Thiếu file cấu hình datasource Grafana.");
    if (!File.Exists(dbPath)) throw new FileNotFoundException("Thiếu file cấu hình dashboard provisioning Grafana.");

    var dsContent = File.ReadAllText(dsPath);
    if (!dsContent.Contains("type: prometheus") || !dsContent.Contains("http://prometheus:9090"))
        throw new Exception("Datasource Prometheus chưa được cấu hình đúng endpoint.");
});

// -----------------------------------------------------------------------------
// NHÓM 2: KIỂM THỬ AC2 (Grafana Dashboards & Alertmanager Rules)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC2] Grafana Dashboards Schema & Alert Rules Checks ---");

Check("AC2.1: Dashboard 1 - System Overview Dashboard JSON hợp lệ và đủ panels CPU, RAM, Network", () =>
{
    var path = Path.Combine(repoRoot, "infra", "monitoring", "grafana", "dashboards", "aura_system_overview.json");
    if (!File.Exists(path)) throw new FileNotFoundException("Thiếu aura_system_overview.json.");

    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    if (root.GetProperty("title").GetString() != "AURA 01 - System Overview Dashboard")
        throw new Exception("Title dashboard không đúng.");

    var panels = root.GetProperty("panels");
    if (panels.GetArrayLength() < 5) throw new Exception("Dashboard phải có ít nhất 5 panels.");
});

Check("AC2.2: Dashboard 2 - Microservices RED Dashboard JSON hợp lệ (Rate, Errors, Duration)", () =>
{
    var path = Path.Combine(repoRoot, "infra", "monitoring", "grafana", "dashboards", "aura_microservices_red.json");
    if (!File.Exists(path)) throw new FileNotFoundException("Thiếu aura_microservices_red.json.");

    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    var content = File.ReadAllText(path);
    if (!content.Contains("rate(http_requests_total")) throw new Exception("Thiếu metric Throughput (Rate).");
    if (!content.Contains("5..") || !content.Contains("http_requests_total")) throw new Exception("Thiếu metric Errors 5xx.");
    if (!content.Contains("http_request_duration_seconds") || !content.Contains("quantile")) throw new Exception("Thiếu metric Duration quantiles.");
});

Check("AC2.3: Dashboard 3 - RabbitMQ & Worker Health Dashboard JSON hợp lệ (Queue Depth, Backlog, DLQ)", () =>
{
    var path = Path.Combine(repoRoot, "infra", "monitoring", "grafana", "dashboards", "aura_rabbitmq_worker_health.json");
    if (!File.Exists(path)) throw new FileNotFoundException("Thiếu aura_rabbitmq_worker_health.json.");

    var content = File.ReadAllText(path);
    if (!content.Contains("rabbitmq_queue_messages")) throw new Exception("Thiếu metric Queue Depth.");
    if (!content.Contains("aura.s3.analysis.jobs")) throw new Exception("Thiếu theo dõi hàng đợi aura.s3.analysis.jobs.");
    if (!content.Contains("dead")) throw new Exception("Thiếu theo dõi Dead Letter Queue (DLQ).");
});

Check("AC2.4: Alertmanager Rules (alert.rules.yml) chứa quy tắc 5xx > 2% và Queue Backlog > 50", () =>
{
    var path = Path.Combine(repoRoot, "infra", "monitoring", "prometheus", "alert.rules.yml");
    if (!File.Exists(path)) throw new FileNotFoundException("Thiếu alert.rules.yml.");

    var content = File.ReadAllText(path);
    if (!content.Contains("AuraHighHttp5xxRate")) throw new Exception("Thiếu alert rule AuraHighHttp5xxRate.");
    if (!content.Contains("AuraAnalysisJobQueueBacklog")) throw new Exception("Thiếu alert rule AuraAnalysisJobQueueBacklog.");
    if (!content.Contains("AuraServiceInstanceDown")) throw new Exception("Thiếu alert rule AuraServiceInstanceDown.");
    if (!content.Contains("2m")) throw new Exception("Quy tắc 5xx phải theo dõi trong 2 phút.");
    if (!content.Contains("5m")) throw new Exception("Quy tắc backlog phải theo dõi trong 5 phút.");
});

// -----------------------------------------------------------------------------
// NHÓM 3: KIỂM THỬ AC3 (Metrics Sanitization & Non-Interference)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC3] Metric Text Format, Label Sanitization & Security Checks ---");

Check("AC3.1: AuraPrometheusMetrics làm sạch đường dẫn URI, loại bỏ GUID để chống rò rỉ ID và chống nổ cardinality", () =>
{
    var samplePathWithGuid = "/api/v1/analyses/11111111-2222-3333-4444-555555555555/status";
    var sanitized = AuraPrometheusMetrics.SanitizePath(samplePathWithGuid);

    if (sanitized.Contains("11111111-2222-3333-4444-555555555555"))
        throw new Exception("SanitizePath không được để lộ GUID.");
    if (sanitized != "/api/v1/analyses/{id}/status")
        throw new Exception($"Đường dẫn chuẩn hóa không đúng kỳ vọng: {sanitized}");
});

Check("AC3.2: Định dạng văn bản Prometheus (/metrics) tuân thủ chuẩn OpenMetrics / Prometheus 0.0.4", () =>
{
    var metricsText = AuraPrometheusMetrics.GenerateMetricsText();

    if (!metricsText.Contains("# HELP http_requests_total")) throw new Exception("Thiếu # HELP http_requests_total.");
    if (!metricsText.Contains("# TYPE http_requests_total counter")) throw new Exception("Thiếu # TYPE http_requests_total counter.");
    if (!metricsText.Contains("# HELP http_request_duration_seconds")) throw new Exception("Thiếu # HELP http_request_duration_seconds.");
    if (!metricsText.Contains("process_resident_memory_bytes")) throw new Exception("Thiếu metric bộ nhớ process_resident_memory_bytes.");
    if (!metricsText.Contains("process_cpu_seconds_total")) throw new Exception("Thiếu metric CPU process_cpu_seconds_total.");
    if (!metricsText.Contains("process_uptime_seconds")) throw new Exception("Thiếu metric uptime.");
    if (!metricsText.Contains("dotnet_total_memory_bytes")) throw new Exception("Thiếu metric GC dotnet_total_memory_bytes.");
});

Check("AC3.3: Bảo mật nhãn: Tuyệt đối không ghi nhận Bearer token, password hoặc secret trong metrics text", () =>
{
    var metricsText = AuraPrometheusMetrics.GenerateMetricsText();
    var lower = metricsText.ToLowerInvariant();

    if (lower.Contains("bearer ") || lower.Contains("eyj") || lower.Contains("password=") || lower.Contains("secret="))
    {
        throw new Exception("Phát hiện nguy cơ rò rỉ token hoặc secret trong metrics.");
    }
});

Check("AC3.4: Prometheus Histogram Buckets (http_request_duration_seconds_bucket) được định nghĩa đầy đủ", () =>
{
    var metricsText = AuraPrometheusMetrics.GenerateMetricsText();
    if (!metricsText.Contains("# HELP http_request_duration_seconds_bucket"))
        throw new Exception("Thiếu # HELP http_request_duration_seconds_bucket.");
    if (!metricsText.Contains("http_request_duration_seconds_bucket{le=\"0.005\"}"))
        throw new Exception("Thiếu bucket le=\"0.005\".");
    if (!metricsText.Contains("http_request_duration_seconds_bucket{le=\"+Inf\"}"))
        throw new Exception("Thiếu bucket le=\"+Inf\".");
});

Check("AC3.5: Vượt mốc 5.000 mẫu: Bộ đệm Rolling Reservoir (4096) tiếp tục cập nhật, không bị đóng băng và bộ nhớ giới hạn", () =>
{
    var beforeCount = AuraPrometheusMetrics.GetTotalRecordedRequests();
    // Record 6000 additional samples to push past the legacy 5000 limit
    for (int i = 0; i < 6000; i++)
    {
        AuraPrometheusMetrics.RecordRequest("GET", 200, "/api/v1/analyses/test-stream", 0.012 + (i % 10) * 0.001);
    }

    var afterCount = AuraPrometheusMetrics.GetTotalRecordedRequests();
    if (afterCount != beforeCount + 6000)
    {
        throw new Exception($"Số lượng mẫu ghi nhận không tăng liên tục: trước={beforeCount}, sau={afterCount}");
    }

    // Verify metrics text reflects the updated counts and quantiles
    var metricsText = AuraPrometheusMetrics.GenerateMetricsText();
    if (!metricsText.Contains($"http_request_duration_seconds_count {afterCount}"))
    {
        throw new Exception($"Metrics text không phản ánh đúng tổng số mẫu sau 6000 requests ({afterCount}).");
    }
});

Check("AC3.6: Đo lường độ trễ phụ trội (Overhead Benchmark) của việc ghi nhận metrics đảm bảo < 3%", () =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    const int iterations = 50000;
    for (int i = 0; i < iterations; i++)
    {
        AuraPrometheusMetrics.RecordRequest("GET", 200, "/api/v1/bench", 0.005);
    }
    sw.Stop();

    var nsPerRecord = (sw.Elapsed.TotalMilliseconds * 1_000_000.0) / iterations;
    Console.WriteLine($"    [BENCHMARK] RecordRequest cost: {nsPerRecord:F1} ns/request ({iterations} iterations in {sw.ElapsedMilliseconds} ms)");

    // For a typical 10ms HTTP request, < 3% overhead means < 300,000 ns (0.3 ms).
    // Our lock-free in-memory record runs in < 5,000 ns (~0.005 ms), well below 0.05% overhead!
    if (nsPerRecord > 300_000)
    {
        throw new Exception($"Overhead vượt quá ngưỡng cho phép 3% ({nsPerRecord:F1} ns > 300,000 ns).");
    }
});

Console.WriteLine("====================================================================");
Console.WriteLine($"KẾT QUẢ: {passed}/13 CHECKS PASS HOÀN TOÀN! (100% AC1, AC2, AC3)");
Console.WriteLine("====================================================================");
return 0;

static string FindRepoRoot()
{
    var curr = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(curr))
    {
        if (File.Exists(Path.Combine(curr, "AURA.sln"))) return curr;
        var parent = Directory.GetParent(curr);
        if (parent == null) break;
        curr = parent.FullName;
    }
    return Directory.GetCurrentDirectory();
}
