using System.Text.Json.Serialization;

namespace AURA.Profile.Api.Models;

public sealed record ErrorDetails(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("retryable")] bool Retryable
);

public sealed record ErrorEnvelope(
    [property: JsonPropertyName("error")] ErrorDetails Error
);

public sealed record UserProfileResponse(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("patientId")] Guid? PatientId,
    [property: JsonPropertyName("personalTenantId")] Guid? PersonalTenantId,
    [property: JsonPropertyName("profileVersion")] int ProfileVersion,
    [property: JsonPropertyName("email")] string? Email = null
);

// CHÚ Ý AN TOÀN: Request cập nhật hồ sơ TUYỆT ĐỐI KHÔNG chứa role, accountId, tenantId, patientId.
// Chống sửa role qua Profile theo chuẩn bảo mật Sprint 2.
public sealed record UpdateProfileRequest(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("phone")] string? Phone
);

public sealed record ProvisionProfileRequest(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("firebaseUid")] string FirebaseUid,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("email")] string? Email
);

public sealed record AccessResolveRequest(
    [property: JsonPropertyName("actorAccountId")] Guid ActorAccountId,
    [property: JsonPropertyName("patientId")] Guid PatientId,
    [property: JsonPropertyName("clinicContextId")] Guid? ClinicContextId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("analysisId")] Guid? AnalysisId = null
);

public sealed record BillingOwnerDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] Guid Id
);

public sealed record AccessResolveResponse(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("accountStatus")] string? AccountStatus,
    [property: JsonPropertyName("patientId")] Guid? PatientId,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("tenantKind")] string? TenantKind,
    [property: JsonPropertyName("ownerPrincipalId")] Guid? OwnerPrincipalId,
    [property: JsonPropertyName("billingOwner")] BillingOwnerDto? BillingOwner,
    [property: JsonPropertyName("permissionVersion")] int PermissionVersion,
    [property: JsonPropertyName("reason")] string? Reason = null
);

// --- Models cho Clinic Lifecycle & Memberships (Task W4-M6-01 / SCRUM-358) ---

public static class ClinicStatuses
{
    public const string Pending = "PENDING";
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Rejected = "REJECTED";

    public static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        Pending, Active, Suspended, Rejected
    };
}

public static class ClinicMemberRoles
{
    public const string ClinicAdmin = "CLINIC_ADMIN";
    public const string Doctor = "DOCTOR";
    public const string Staff = "STAFF";

    public static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        ClinicAdmin, Doctor, Staff
    };

    public static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var trimmed = role.Trim();
        if (string.Equals(trimmed, "ClinicManager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, ClinicAdmin, StringComparison.OrdinalIgnoreCase))
            return ClinicAdmin;
        if (string.Equals(trimmed, "ClinicDoctor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Doctor, StringComparison.OrdinalIgnoreCase))
            return Doctor;
        if (string.Equals(trimmed, "ClinicStaff", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Staff, StringComparison.OrdinalIgnoreCase))
            return Staff;
        return null;
    }
}

public static class MembershipStatuses
{
    public const string Active = "ACTIVE";
    public const string Revoked = "REVOKED";
    public const string Invited = "INVITED";
}

public sealed record RegisterClinicRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("taxId")] string? TaxId,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("representativeName")] string? RepresentativeName,
    [property: JsonPropertyName("phone")] string? Phone
);

public sealed record UpdateClinicStatusRequest(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason
);

public sealed record InviteClinicMemberRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role
);

public sealed record ClinicDto(
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("taxId")] string? TaxId,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("representativeName")] string? RepresentativeName,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("auditReason")] string? AuditReason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);

public sealed record AddClinicMemberRequest(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("role")] string Role
);

public sealed record ClinicMembershipDto(
    [property: JsonPropertyName("membershipId")] Guid MembershipId,
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("accountId")] Guid? AccountId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("invitedEmail")] string? InvitedEmail = null,
    [property: JsonPropertyName("invitationToken")] string? InvitationToken = null
);

public sealed record ClinicMemberListResponse(
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("items")] IReadOnlyList<ClinicMembershipDto> Items
);

// --- Models cho Camera Delegation & Doctor Assignments (Sprint 3 - Task W3-M6-02) ---

public static class DoctorAssignmentStatuses
{
    public const string Active = "ACTIVE";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";
}

public sealed record CameraDelegationResolveRequest(
    [property: JsonPropertyName("actorAccountId")] Guid ActorAccountId,
    [property: JsonPropertyName("patientId")] Guid PatientId,
    [property: JsonPropertyName("servicePrincipalId")] Guid ServicePrincipalId,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("expectedTenantId")] Guid ExpectedTenantId,
    [property: JsonPropertyName("action")] string Action
);

public sealed record CameraDelegationResolveResponse(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("patientId")] Guid? PatientId,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("tenantKind")] string? TenantKind,
    [property: JsonPropertyName("ownerPrincipalId")] Guid? OwnerPrincipalId,
    [property: JsonPropertyName("billingOwner")] BillingOwnerDto? BillingOwner,
    [property: JsonPropertyName("grantId")] Guid? GrantId,
    [property: JsonPropertyName("reason")] string? Reason
);

public sealed record CreateDoctorAssignmentRequest(
    [property: JsonPropertyName("doctorId")] Guid DoctorId,
    [property: JsonPropertyName("patientId")] Guid PatientId,
    [property: JsonPropertyName("analysisId")] Guid? AnalysisId = null,
    [property: JsonPropertyName("notes")] string? Notes = null
);

public sealed record DoctorAssignmentDto(
    [property: JsonPropertyName("assignmentId")] Guid AssignmentId,
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("doctorId")] Guid DoctorId,
    [property: JsonPropertyName("doctorName")] string DoctorName,
    [property: JsonPropertyName("patientId")] Guid PatientId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("assignedAt")] DateTimeOffset AssignedAt,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("analysisId")] Guid? AnalysisId = null,
    [property: JsonPropertyName("transferredFromAssignmentId")] Guid? TransferredFromAssignmentId = null,
    [property: JsonPropertyName("notes")] string? Notes = null
);

public sealed record DoctorAssignmentListResponse(
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("items")] IReadOnlyList<DoctorAssignmentDto> Items
);

// --- Models cho Clinic Patients Registry & Doctor Assignment Engine (Task W4-M6-02) ---

public static class PatientTypes
{
    public const string Personal = "PERSONAL";
    public const string ClinicManaged = "CLINIC_MANAGED";
}

public sealed record ClinicPatientMedicalHistoryDto(
    [property: JsonPropertyName("hasDiabetes")] bool? HasDiabetes = null,
    [property: JsonPropertyName("hasHypertension")] bool? HasHypertension = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("otherConditions")] string? OtherConditions = null
);

public sealed record RegisterClinicPatientRequest(
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("dateOfBirth")] DateOnly? DateOfBirth = null,
    [property: JsonPropertyName("gender")] string? Gender = null,
    [property: JsonPropertyName("phone")] string? Phone = null,
    [property: JsonPropertyName("healthInsuranceNumber")] string? HealthInsuranceNumber = null,
    [property: JsonPropertyName("medicalHistory")] ClinicPatientMedicalHistoryDto? MedicalHistory = null
);

public sealed record UpdateClinicPatientRequest(
    [property: JsonPropertyName("fullName")] string? FullName = null,
    [property: JsonPropertyName("dateOfBirth")] DateOnly? DateOfBirth = null,
    [property: JsonPropertyName("gender")] string? Gender = null,
    [property: JsonPropertyName("phone")] string? Phone = null,
    [property: JsonPropertyName("healthInsuranceNumber")] string? HealthInsuranceNumber = null,
    [property: JsonPropertyName("medicalHistory")] ClinicPatientMedicalHistoryDto? MedicalHistory = null
);

public sealed record ClinicPatientDto(
    [property: JsonPropertyName("patientId")] Guid PatientId,
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("patientType")] string PatientType,
    [property: JsonPropertyName("fullName")] string? FullName,
    [property: JsonPropertyName("dateOfBirth")] DateOnly? DateOfBirth,
    [property: JsonPropertyName("gender")] string? Gender,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("healthInsuranceNumber")] string? HealthInsuranceNumber,
    [property: JsonPropertyName("medicalHistory")] ClinicPatientMedicalHistoryDto? MedicalHistory,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);

public sealed record ClinicPatientListResponse(
    [property: JsonPropertyName("clinicId")] Guid ClinicId,
    [property: JsonPropertyName("items")] IReadOnlyList<ClinicPatientDto> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount
);

public sealed record BatchAssignDoctorRequest(
    [property: JsonPropertyName("doctorId")] Guid DoctorId,
    [property: JsonPropertyName("patientIds")] IReadOnlyList<Guid> PatientIds,
    [property: JsonPropertyName("notes")] string? Notes = null
);

public sealed record TransferDoctorAssignmentRequest(
    [property: JsonPropertyName("newDoctorId")] Guid NewDoctorId,
    [property: JsonPropertyName("notes")] string? Notes = null
);

public sealed record AcceptClinicInvitationRequest(string Token);
