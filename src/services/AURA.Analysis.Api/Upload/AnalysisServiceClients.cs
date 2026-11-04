using AURA.Analysis.Api.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AURA.Analysis.Api.Upload;

/// <summary>Registered by the HTTP composition root when the persistence/storage adapters are ready.</summary>
public static class AnalysisServiceClients
{
    public static IServiceCollection AddAnalysisServiceClients(this IServiceCollection services, IConfiguration configuration)
    {
        void Configure(HttpClient client, string service)
        {
            var value = configuration[$"Services:{service}:BaseUrl"];
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
                throw new InvalidOperationException($"Services:{service}:BaseUrl must be an HTTP(S) origin.");
            var timeout = configuration.GetValue("Services:DependencyTimeoutSeconds", 5);
            if (timeout is < 1 or > 30) throw new InvalidOperationException("Dependency timeout must be between 1 and 30 seconds.");
            client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(timeout);
        }
        services.AddSingleton<IServiceTokenProvider, ServiceTokenProvider>();
        services.AddHttpClient<IClinicAccessResolver,ProfileClinicAccessClient>(client=>Configure(client,"Profile"))
            .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false})
            .RedactLoggedHeaders(new[]{"Authorization"});
        services.AddTransient<IAnalysisUserIdentityResolver, AnalysisUserIdentityResolver>();
        services.AddHttpClient("AnalysisIdentity", client => Configure(client, "Identity"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(new[] { "Authorization", "X-User-Authorization" });
        services.AddHttpClient<IPatientAccessResolver, ProfileAccessClient>(client => Configure(client, "Profile"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(new[] { "Authorization", "X-User-Authorization" });
        services.AddHttpClient("ProfileAssignments", client => Configure(client, "Profile"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(new[] { "Authorization", "X-User-Authorization" });
        services.AddHttpClient<ICreditReservations, BillingReservationsClient>(client => Configure(client, "Billing"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(new[] { "Authorization", "X-User-Authorization" });
        return services;
    }
}
