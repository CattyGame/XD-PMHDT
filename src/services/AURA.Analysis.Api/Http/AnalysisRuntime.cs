using AURA.Analysis.Api.Upload;
using AURA.Common.Cache;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StackExchange.Redis;

namespace AURA.Analysis.Api.Http;

public static class AnalysisRuntime
{
    public static IServiceCollection AddAnalysisRuntime(this IServiceCollection services,IConfiguration configuration,IHostEnvironment environment)
    {
        var raw=configuration.GetConnectionString("Postgres");
        var redisConnection=configuration.GetConnectionString("Redis");
        if(string.IsNullOrWhiteSpace(redisConnection))throw new InvalidOperationException("Analysis Redis configuration is required.");
        if(string.IsNullOrWhiteSpace(raw))throw new InvalidOperationException("Analysis PostgreSQL configuration is required.");
        NpgsqlConnectionStringBuilder db;
        try{db=new NpgsqlConnectionStringBuilder(raw);}
        catch(ArgumentException){throw new InvalidOperationException("Invalid Analysis PostgreSQL configuration.");}
        var capacity=configuration.GetValue("Analysis:ConcurrentUploads",4);
        if(db.Username!="aura_analysis_app"||string.IsNullOrWhiteSpace(db.Password)||string.IsNullOrWhiteSpace(db.Host)
            ||string.IsNullOrWhiteSpace(db.Database)||!db.Pooling||db.NoResetOnClose||db.Multiplexing||capacity is <1 or >8||db.MaxPoolSize<capacity*2+4)
            throw new InvalidOperationException("Invalid Analysis database role/pool or upload concurrency configuration.");
        if(configuration["ServiceAuth:ClientId"]!="analysis-api"||string.IsNullOrWhiteSpace(configuration["ServiceAuth:ClientSecret"]))
            throw new InvalidOperationException("Analysis own service credentials are required.");
        if(configuration.GetValue("Services:DependencyTimeoutSeconds",5) is <1 or >30)
            throw new InvalidOperationException("Dependency timeout must be between 1 and 30 seconds.");
        foreach(var service in new[]{"Identity","Profile","Billing"})
        {
            if(!Uri.TryCreate(configuration[$"Services:{service}:BaseUrl"],UriKind.Absolute,out var uri)
                ||uri.Scheme is not ("http" or "https")||uri.AbsolutePath!="/"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)
                throw new InvalidOperationException("Explicit dependency origins are required.");
        }
        var options=configuration.GetSection("Analysis").Get<UploadOptions>()??new();
        if(options.MaxImageBytes is <1 or >10485760||options.MaxImagePixels is <1 or >16000000||options.MaxImagesPerRequest is <1 or >100
            ||options.MaxRequestBytes<options.MaxImageBytes||options.MaxRequestBytes>1101004800||options.OriginalsContainer!="aura-originals")
            throw new InvalidOperationException("Invalid Analysis upload limits.");
        var reportOptions=configuration.GetSection("Analysis:Report").Get<AnalysisReportOptions>()??new();
        if(!Uri.TryCreate(reportOptions.PublicApiBaseUrl,UriKind.Absolute,out var publicApi)
            ||publicApi.Scheme is not ("http" or "https")||publicApi.AbsolutePath!="/"
            ||publicApi.UserInfo.Length>0||publicApi.Query.Length>0||publicApi.Fragment.Length>0
            ||(!environment.IsDevelopment()&&publicApi.Scheme!="https"))
            throw new InvalidOperationException("A safe public Analysis API origin is required for authenticated report QR links.");
        services.AddSingleton(options);services.AddSingleton(configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()??new());
        services.AddSingleton(reportOptions);
        services.AddSingleton(new AnalysisUploadGate(capacity));services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_=>NpgsqlDataSource.Create(db.ConnectionString));
        services.AddSingleton<IConnectionMultiplexer>(_=>ConnectionMultiplexer.Connect(redisConnection));
        services.AddSingleton<IBatchRedisProgressService,BatchRedisProgressService>();
        services.AddAnalysisServiceClients(configuration);services.AddAnalysisPrivateStorage(configuration,environment);
        services.AddTransient<IUploadRepository,PostgresUploadRepository>();services.AddTransient<IUploadRecoveryJournal,PostgresUploadRecoveryRepository>();
        services.AddTransient<IFileAccessRepository,PostgresFileAccessRepository>();services.AddTransient<IBatchStatusRepository,PostgresBatchStatusRepository>();
        services.AddTransient<IAnalysisQueryRepository,PostgresAnalysisQueryRepository>();
        services.AddTransient<IDoctorWorklistRepository,PostgresAnalysisQueryRepository>();
        services.AddTransient<IDoctorReviewRepository,PostgresDoctorReviewRepository>();
        services.AddTransient<IDoctorAssignmentClient,ProfileDoctorAssignmentClient>();
        services.AddTransient<IInternalResolversRepository,PostgresInternalResolversRepository>();
        services.AddTransient<UploadPlanningService>();services.AddTransient<IAnalysisUploadHandler,DurableUploadService>();
        services.AddTransient<IAnalysisReadinessChecks,AnalysisReadinessChecks>();
        services.AddTransient<BatchStatusService>();services.AddTransient<FileAccessService>();
        services.AddTransient<AnalysisReportService>();
        services.AddTransient<AnalysisQueryService>();services.AddTransient<InternalResolversService>();
        services.AddTransient<DoctorReviewService>();services.AddTransient<DoctorWorklistService>();services.AddTransient<AURA.Analysis.Api.Campaign.CampaignRepository>();
        return services;
    }
}
