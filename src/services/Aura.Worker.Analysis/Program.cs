using Microsoft.EntityFrameworkCore;
using Aura.Worker.Analysis.Configuration;
using Aura.Worker.Analysis.Data;
using Aura.Worker.Analysis.Data.Repositories;
using Aura.Worker.Analysis.Messaging;
using Aura.Worker.Analysis.Processing;
using Aura.Worker.Analysis.Services;
using AURA.Common.Cache;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// 1. Configuration sections
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection(BlobStorageOptions.SectionName));
builder.Services.Configure<AiClientOptions>(builder.Configuration.GetSection(AiClientOptions.SectionName));
builder.Services.Configure<ProfileClientOptions>(builder.Configuration.GetSection(ProfileClientOptions.SectionName));
builder.Services.Configure<ServiceAuthOptions>(builder.Configuration.GetSection(ServiceAuthOptions.SectionName));

// 2. Database
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=postgres;Port=5432;Database=aura_dev;Username=aura_analysis_app;Password=secret";
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
builder.Services.AddSingleton<IBatchRedisProgressService, BatchRedisProgressService>();
builder.Services.AddSingleton<RabbitMqConnectionProvider>();
builder.Services.AddSingleton<IBatchCompletedEventPublisher, BatchCompletedEventPublisher>();
builder.Services.AddDbContext<WorkerDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

// 3. Infrastructure & HTTP Clients
builder.Services.AddSingleton<IServiceTokenProvider, ServiceTokenProvider>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

builder.Services.AddHttpClient<IAiHttpClient, AiHttpClient>(client =>
{
    var aiOptions = builder.Configuration.GetSection(AiClientOptions.SectionName).Get<AiClientOptions>() ?? new();
    client.BaseAddress = new Uri(aiOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(aiOptions.TimeoutSeconds + 5);
});

builder.Services.AddHttpClient<IProfileResolveClient, ProfileResolveClient>(client =>
{
    var profileOptions = builder.Configuration.GetSection(ProfileClientOptions.SectionName).Get<ProfileClientOptions>() ?? new();
    client.BaseAddress = new Uri(profileOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// 4. Repositories & Processor
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddScoped<IAnalysisRepository, AnalysisRepository>();
builder.Services.AddScoped<IJobProcessor, JobProcessor>();

// 5. Background Consumer
builder.Services.AddHostedService<JobRunConsumer>();

var host = builder.Build();
host.Run();
