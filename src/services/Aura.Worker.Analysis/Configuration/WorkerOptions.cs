namespace Aura.Worker.Analysis.Configuration;

public class WorkerOptions
{
    public const string SectionName = "WorkerOptions";

    public int MaxAttempts { get; set; } = 3;
    public int[] RetryDelaysSeconds { get; set; } = [5, 15];
    public int Concurrency { get; set; } = 1;
    public ushort Prefetch { get; set; } = 1;
    public int LeaseSeconds { get; set; } = 90;
    public int HeartbeatSeconds { get; set; } = 15;
    public int AiTimeoutSeconds { get; set; } = 30;
    public bool DurableCallIntent { get; set; } = true;
}

public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string HostName { get; set; } = "rabbitmq";
    public int Port { get; set; } = 5672;
    public bool UseTls { get; set; }
    public string VirtualHost { get; set; } = "/aura-s3";
    public string UserName { get; set; } = "aura_worker";
    public string Password { get; set; } = string.Empty;
    public string QueueName { get; set; } = "aura.s3.analysis.jobs";
    public string RetryExchange { get; set; } = "aura.s3.retry.jobs";
    public string DeadExchange { get; set; } = "aura.s3.dead.jobs";
    public string EventsExchange { get; set; } = "aura.s3.events";
}

public class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    public string ConnectionString { get; set; } = string.Empty;
    public string OriginalsContainer { get; set; } = "aura-originals";
    public string AnnotationsContainer { get; set; } = "aura-annotations";
}
