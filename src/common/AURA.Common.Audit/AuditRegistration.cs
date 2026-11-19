using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace AURA.Common.Audit;

public static class AuditRegistration
{
    public static IServiceCollection AddOwnerAudit(this IServiceCollection services, IConfiguration config, AuditSource source)
    {
        services.AddSingleton(sp => new AuditStore(sp.GetRequiredService<NpgsqlDataSource>(), source));
        services.AddSingleton<IAuditWriter>(sp => sp.GetRequiredService<AuditStore>());
        if (source == AuditSource.Profile)
        {
            services.AddSingleton<AuditProjection>();
            services.AddSingleton<IAuditTransport, LocalAuditTransport>();
            services.AddHostedService<AuditProjectionRetention>();
        }
        else
        {
            AddClient("AuditIdentity", "Identity", 5004);
            AddClient("AuditProfile", "Profile", 5005);
            services.AddSingleton<IAuditTransport>(sp => new AuditHttpTransport(sp.GetRequiredService<IHttpClientFactory>(), config, source));
        }
        services.AddHostedService<AuditPublisher>();
        return services;

        void AddClient(string name, string target, int localPort) => services.AddHttpClient(name, client =>
        {
            var raw = config[$"Services:{target}:BaseUrl"] ?? (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
                ? $"http://{target.ToLowerInvariant()}-api:8080" : $"http://127.0.0.1:{localPort}");
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
                || origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0)
                throw new InvalidOperationException("Audit dependency origin is invalid.");
            client.BaseAddress = origin; client.Timeout = TimeSpan.FromSeconds(5);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(["Authorization", "X-User-Authorization"]);
    }
}

internal sealed class AuditProjectionRetention(AuditProjection projection) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await projection.PurgeExpiredAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (NpgsqlException) { /* Retry retention without deleting any pending source obligation. */ }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
