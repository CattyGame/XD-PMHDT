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

public static class AllowedAccountStatuses
{
    public const string Active = "ACTIVE";
    public const string Disabled = "DISABLED";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Active,
        Disabled
    };
}

public static class AllowedGlobalRoles
{
    public const string Patient = "PATIENT";
    public const string Doctor = "DOCTOR";
    public const string SystemAdmin = "SYSTEM_ADMIN";

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Patient,
        Doctor,
        SystemAdmin
    };
}

public sealed record AdminAccountDto(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("firebaseUid")] string FirebaseUid,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("accountStatus")] string AccountStatus,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("profileProvisioningStatus")] string ProfileProvisioningStatus,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);

public sealed record AdminAccountPageResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<AdminAccountDto> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor
);

public sealed record UpdateAccountStatusRequest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expectedVersion")] int ExpectedVersion,
    [property: JsonPropertyName("reason")] string Reason
);

public sealed record UpdateAccountRolesRequest(
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("expectedVersion")] int ExpectedVersion,
    [property: JsonPropertyName("reason")] string Reason
);
