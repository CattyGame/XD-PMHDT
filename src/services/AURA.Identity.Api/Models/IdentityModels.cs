using System.Text.Json.Serialization;

namespace AURA.Identity.Api.Models;

public sealed record ErrorDetails(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("retryable")] bool Retryable
);

public sealed record ErrorEnvelope(
    [property: JsonPropertyName("error")] ErrorDetails Error
);

public sealed record BootstrapResponse(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("firebaseUid")] string FirebaseUid,
    [property: JsonPropertyName("accountStatus")] string AccountStatus,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("profileProvisioningStatus")] string ProfileProvisioningStatus
);

public sealed record ServiceTokenRequest(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientSecret")] string ClientSecret,
    [property: JsonPropertyName("audience")] string? Audience,
    [property: JsonPropertyName("scope")] string? Scope
);

public sealed record ServiceTokenResponse(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("tokenType")] string TokenType,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn
);

public sealed record IdentityContextResponse(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("accountStatus")] string AccountStatus,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("email")] string? Email = null,
    [property: JsonPropertyName("emailVerified")] bool EmailVerified = false
);

public sealed record ProvisionProfileRequest(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("firebaseUid")] string FirebaseUid,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("email")] string? Email
);
