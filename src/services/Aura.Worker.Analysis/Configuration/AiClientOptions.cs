namespace Aura.Worker.Analysis.Configuration;

public class AiClientOptions
{
    public const string SectionName = "AiClient";

    public string BaseUrl { get; set; } = "http://ai-core:8000";
    public string Audience { get; set; } = "aura-ai";
    public string Scope { get; set; } = "ai:analyze";
    public int TimeoutSeconds { get; set; } = 30;
}

public class ProfileClientOptions
{
    public const string SectionName = "ProfileClient";

    public string BaseUrl { get; set; } = "http://profile-api:8080";
    public string Audience { get; set; } = "aura-profile";
    public string Scope { get; set; } = "profile:resolve";
}

public class ServiceAuthOptions
{
    public const string SectionName = "ServiceAuth";

    public string IdentityBaseUrl { get; set; } = "http://identity-api:8080";
    public string ClientSecret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "aura-identity-local";
    public string ClientId { get; set; } = "analysis-worker";
    public Guid PrincipalId { get; set; } = Guid.Parse("7ab5c4dc-147d-555f-ab6a-18d354c42439");
    public string PrivateKeyPemPath { get; set; } = string.Empty;
}
