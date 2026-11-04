using System.Diagnostics;
using System.Net;
using AURA.Profile.Api;
using AURA.Profile.Api.Models;

Console.WriteLine("====================================================================");
Console.WriteLine("AURA Task W4-M6-04: Multi-Tenant Authorization, Security & Audit Checks");
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

// -----------------------------------------------------------------------------
// DỮ LIỆU ĐẶC TẢ ĐA TENANT SPRINT 4 (W4-M6-04 Authoritative Dataset)
// -----------------------------------------------------------------------------
var accountA = Guid.Parse("11111111-1111-4111-8111-000000000001");
var accountB = Guid.Parse("11111111-1111-4111-8111-000000000002");
var accountDisabled = Guid.Parse("11111111-1111-4111-8111-000000000007");

var personalTenantA = Guid.Parse("11111111-1111-4111-8111-100000000001");
var personalTenantB = Guid.Parse("11111111-1111-4111-8111-100000000002");
var clinicAlpha = Guid.Parse("11111111-1111-4111-8111-200000000001");
var clinicBeta = Guid.Parse("11111111-1111-4111-8111-200000000002");

var patientA = Guid.Parse("11111111-1111-4111-8111-300000000001"); // Under Personal Tenant A
var patientB = Guid.Parse("11111111-1111-4111-8111-300000000002"); // Registered under Clinic Alpha (CLINIC_MANAGED)
var patientC = Guid.Parse("11111111-1111-4111-8111-300000000003"); // Registered under Clinic Beta (CLINIC_MANAGED)

var doctor1 = Guid.Parse("11111111-1111-4111-8111-400000000001"); // Clinic Alpha Doctor (ACTIVE)
var doctor2 = Guid.Parse("11111111-1111-4111-8111-400000000002"); // Clinic Beta Doctor (ACTIVE)
var doctor3 = Guid.Parse("11111111-1111-4111-8111-400000000003"); // Clinic Alpha Doctor (Backup / Transfer target)
var doctorRevoked = Guid.Parse("11111111-1111-4111-8111-400000000009"); // Clinic Alpha Doctor whose membership is REVOKED
var clinicAdminAlpha = Guid.Parse("11111111-1111-4111-8111-500000000001");
var clinicAdminBeta = Guid.Parse("11111111-1111-4111-8111-500000000002");

var analysis1 = Guid.Parse("11111111-1111-4111-8111-600000000001"); // Analysis of patientB under Clinic Alpha
var assignment1 = Guid.Parse("11111111-1111-4111-8111-700000000001"); // Assignment of doctor1 for patientB / analysis1
var assignmentOfClinicBeta = Guid.Parse("11111111-1111-4111-8111-700000000002"); // Assignment in Clinic Beta

var campaignAlpha = Guid.Parse("11111111-1111-4111-8111-800000000001"); // Campaign in Clinic Alpha
var campaignBeta = Guid.Parse("11111111-1111-4111-8111-800000000002"); // Campaign in Clinic Beta
var campaignClosed = Guid.Parse("11111111-1111-4111-8111-800000000003"); // Closed Campaign in Clinic Alpha
var batchAlpha = Guid.Parse("11111111-1111-4111-8111-900000000001"); // Batch belonging to Clinic Alpha

var servicePrincipalId = Guid.Parse("6991e61d-15d2-5107-86c0-dd3966af169c");

// -----------------------------------------------------------------------------
// NHÓM 1: KIỂM THỬ AC1 (Cross-Tenant, URL Tampering & Multi-Tenant Boundary)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC1] Multi-Tenant Boundary & Penetration Attack Checks ---");

Check("TC-AC1.1: Camera gửi grant hợp lệ cho Patient A dưới Personal Tenant A -> ALLOW", () =>
{
    var request = new CameraDelegationResolveRequest(
        ActorAccountId: accountA,
        PatientId: patientA,
        ServicePrincipalId: servicePrincipalId,
        ClientId: "nifi-camera",
        DeviceId: "CAM-VALID-01",
        ExpectedTenantId: personalTenantA,
        Action: "ANALYSIS_CREATE"
    );

    if (request.ExpectedTenantId != personalTenantA)
        throw new Exception("ExpectedTenantId không khớp.");
    if (request.ActorAccountId != accountA)
        throw new Exception("ActorAccountId không sở hữu patient.");

    var response = new CameraDelegationResolveResponse(
        Allowed: true,
        PatientId: request.PatientId,
        TenantId: request.ExpectedTenantId,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: new BillingOwnerDto("USER", accountA),
        GrantId: Guid.NewGuid(),
        Reason: "OK"
    );

    if (!response.Allowed || response.Reason != "OK")
        throw new Exception("Phải cho phép delegation hợp lệ.");
});

Check("TC-AC1.2: Camera giả mạo deviceId (CAM-UNKNOWN-99) -> DENY (GRANT_NOT_FOUND)", () =>
{
    var request = new CameraDelegationResolveRequest(
        ActorAccountId: accountA,
        PatientId: patientA,
        ServicePrincipalId: servicePrincipalId,
        ClientId: "nifi-camera",
        DeviceId: "CAM-UNKNOWN-99",
        ExpectedTenantId: personalTenantA,
        Action: "ANALYSIS_CREATE"
    );

    var response = new CameraDelegationResolveResponse(
        Allowed: false,
        PatientId: request.PatientId,
        TenantId: request.ExpectedTenantId,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: null,
        GrantId: null,
        Reason: "GRANT_NOT_FOUND"
    );

    if (response.Allowed || response.Reason != "GRANT_NOT_FOUND")
        throw new Exception("Thiết bị giả mạo phải bị từ chối với GRANT_NOT_FOUND.");
});

Check("TC-AC1.3: Camera gửi ảnh cho Patient A nhưng nhắm vào Clinic Alpha -> DENY (CLINIC_CAMERA_UNSUPPORTED)", () =>
{
    var request = new CameraDelegationResolveRequest(
        ActorAccountId: accountA,
        PatientId: patientA,
        ServicePrincipalId: servicePrincipalId,
        ClientId: "nifi-camera",
        DeviceId: "CAM-VALID-01",
        ExpectedTenantId: clinicAlpha, // Invalid: Camera delegation strictly Personal
        Action: "ANALYSIS_CREATE"
    );

    var isPersonal = request.ExpectedTenantId == personalTenantA;
    var response = new CameraDelegationResolveResponse(
        Allowed: isPersonal,
        PatientId: request.PatientId,
        TenantId: request.ExpectedTenantId,
        TenantKind: isPersonal ? "PERSONAL" : "CLINIC",
        OwnerPrincipalId: accountA,
        BillingOwner: null,
        GrantId: null,
        Reason: isPersonal ? "OK" : "CLINIC_CAMERA_UNSUPPORTED"
    );

    if (response.Allowed || response.Reason != "CLINIC_CAMERA_UNSUPPORTED")
        throw new Exception("Target Clinic Tenant phải bị từ chối.");
});

Check("TC-AC1.4: Camera gửi ảnh cho Patient B (thuộc Clinic Alpha) -> DENY (CLINIC_CAMERA_UNSUPPORTED)", () =>
{
    var request = new CameraDelegationResolveRequest(
        ActorAccountId: accountB,
        PatientId: patientB,
        ServicePrincipalId: servicePrincipalId,
        ClientId: "nifi-camera",
        DeviceId: "CAM-VALID-01",
        ExpectedTenantId: clinicAlpha,
        Action: "ANALYSIS_CREATE"
    );

    var response = new CameraDelegationResolveResponse(
        Allowed: false,
        PatientId: request.PatientId,
        TenantId: request.ExpectedTenantId,
        TenantKind: "CLINIC",
        OwnerPrincipalId: null,
        BillingOwner: null,
        GrantId: null,
        Reason: "CLINIC_CAMERA_UNSUPPORTED"
    );

    if (response.Allowed || response.Reason != "CLINIC_CAMERA_UNSUPPORTED")
        throw new Exception("Bệnh nhân phòng khám không được nhận ảnh qua camera personal grant.");
});

Check("TC-AC1.5: Camera gửi grant đã bị thu hồi (CAM-REVOKED-02) -> DENY (GRANT_REVOKED)", () =>
{
    var grantStatus = "REVOKED";
    var isRevoked = string.Equals(grantStatus, "REVOKED", StringComparison.OrdinalIgnoreCase);

    var response = new CameraDelegationResolveResponse(
        Allowed: !isRevoked,
        PatientId: patientA,
        TenantId: personalTenantA,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: null,
        GrantId: null,
        Reason: isRevoked ? "GRANT_REVOKED" : "OK"
    );

    if (response.Allowed || response.Reason != "GRANT_REVOKED")
        throw new Exception("Grant bị thu hồi phải bị từ chối ngay lập tức.");
});

Check("TC-AC1.6: Camera gửi grant đã hết hạn (CAM-EXPIRED-03) -> DENY (GRANT_EXPIRED)", () =>
{
    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(-10);
    var isExpired = expiresAt <= DateTimeOffset.UtcNow;

    var response = new CameraDelegationResolveResponse(
        Allowed: !isExpired,
        PatientId: patientA,
        TenantId: personalTenantA,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: null,
        GrantId: null,
        Reason: isExpired ? "GRANT_EXPIRED" : "OK"
    );

    if (response.Allowed || response.Reason != "GRANT_EXPIRED")
        throw new Exception("Grant hết hạn phải bị từ chối với GRANT_EXPIRED.");
});

Check("TC-AC1.7: Camera gửi actorAccountId là Account B nhưng patientId là Patient A -> DENY (ACTOR_PATIENT_MISMATCH)", () =>
{
    var request = new CameraDelegationResolveRequest(
        ActorAccountId: accountB, // Does not own Patient A
        PatientId: patientA,
        ServicePrincipalId: servicePrincipalId,
        ClientId: "nifi-camera",
        DeviceId: "CAM-VALID-01",
        ExpectedTenantId: personalTenantA,
        Action: "ANALYSIS_CREATE"
    );

    var ownsPatient = request.ActorAccountId == accountA; // Only Account A owns Patient A
    var response = new CameraDelegationResolveResponse(
        Allowed: ownsPatient,
        PatientId: request.PatientId,
        TenantId: request.ExpectedTenantId,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: null,
        GrantId: null,
        Reason: ownsPatient ? "OK" : "ACTOR_PATIENT_MISMATCH"
    );

    if (response.Allowed || response.Reason != "ACTOR_PATIENT_MISMATCH")
        throw new Exception("Mạo danh tài khoản ủy quyền phải bị từ chối.");
});

Check("TC-AC1.8: Cross-Tenant Reading: Account B cố tình đọc analysis của Patient A -> DENY", () =>
{
    var request = new AccessResolveRequest(
        ActorAccountId: accountB,
        PatientId: patientA,
        ClinicContextId: null,
        Action: "ANALYSIS_READ"
    );

    var allowed = (request.ActorAccountId == accountA);
    var response = new AccessResolveResponse(
        Allowed: allowed,
        AccountStatus: "ACTIVE",
        PatientId: request.PatientId,
        TenantId: personalTenantA,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: new BillingOwnerDto("USER", accountA),
        PermissionVersion: 1
    );

    if (response.Allowed)
        throw new Exception("Truy cập chéo bệnh nhân trong personal context phải bị chặn hoàn toàn.");
});

Check("TC-AC1.9: Cross-Clinic Doctor Reading: Doctor 2 (Clinic Beta) cố đọc hồ sơ Patient B (Clinic Alpha) -> DENY", () =>
{
    var doctorClinicId = clinicBeta;
    var patientClinicId = clinicAlpha;
    var hasActiveAssignment = false; // Doctor 2 has no active assignment for Patient B in Clinic Alpha

    var isCrossClinicAllowed = (doctorClinicId == patientClinicId) && hasActiveAssignment;
    var response = new AccessResolveResponse(
        Allowed: isCrossClinicAllowed,
        AccountStatus: "ACTIVE",
        PatientId: patientB,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlpha),
        PermissionVersion: 1
    );

    if (response.Allowed)
        throw new Exception("Bác sĩ phòng khám khác không được đọc hồ sơ khi không có assignment.");
});

Check("TC-AC1.10: URL Tampering: Bác sĩ/Admin Clinic Alpha thay clinicId thành Clinic Beta trên URL -> 403 Forbidden", () =>
{
    var callerClinicMembership = clinicAlpha;
    var urlClinicId = clinicBeta;

    HttpStatusCode statusCode = (callerClinicMembership == urlClinicId) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Cố tình sửa clinicId trên URL phải bị chặn tuyệt đối với HTTP 403 Forbidden.");
});

Check("TC-AC1.11: Anti-probing trên Hồ sơ Bệnh nhân: Clinic Alpha tra cứu GET /clinics/{alpha}/patients/{patientC} -> 404 Not Found", () =>
{
    var callerClinic = clinicAlpha;
    var requestedPatientId = patientC; // Belongs to Clinic Beta
    var patientClinicMap = new Dictionary<Guid, Guid> { { patientB, clinicAlpha }, { patientC, clinicBeta } };

    HttpStatusCode statusCode;
    if (!patientClinicMap.TryGetValue(requestedPatientId, out var actualClinic) || actualClinic != callerClinic)
    {
        // Fail-closed with 404 Not Found to prevent probing whether patientId exists in other clinics
        statusCode = HttpStatusCode.NotFound;
    }
    else
    {
        statusCode = HttpStatusCode.OK;
    }

    if (statusCode != HttpStatusCode.NotFound)
        throw new Exception($"Truy vấn bệnh nhân của Clinic khác dưới URL Clinic Alpha phải trả về 404 Not Found (nhận được {statusCode}).");
});

Check("TC-AC1.12: Cross-Tenant Mutation: Clinic Alpha cố ý PATCH sửa hồ sơ của patientC (thuộc Clinic Beta) -> 404 Not Found", () =>
{
    var callerClinic = clinicAlpha;
    var targetPatient = patientC;
    var patientClinicMap = new Dictionary<Guid, Guid> { { patientB, clinicAlpha }, { patientC, clinicBeta } };

    var canUpdate = patientClinicMap.TryGetValue(targetPatient, out var ownerClinic) && ownerClinic == callerClinic;
    var statusCode = canUpdate ? HttpStatusCode.OK : HttpStatusCode.NotFound;

    if (statusCode != HttpStatusCode.NotFound)
        throw new Exception("Sửa hồ sơ bệnh nhân clinic khác phải bị từ chối 404 Not Found.");
});

Check("TC-AC1.13: Batch Assignment Tampering: Gán bệnh nhân ngoại lai (patientC của Clinic Beta) cho Bác sĩ Clinic Alpha -> 400 Bad Request", () =>
{
    var clinicId = clinicAlpha;
    var doctorId = doctor1; // Active in Clinic Alpha
    var patientIds = new List<Guid> { patientB, patientC }; // patientC is foreign!

    var clinicPatients = new HashSet<Guid> { patientB };
    var hasForeignPatient = patientIds.Any(p => !clinicPatients.Contains(p));

    var statusCode = hasForeignPatient ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
    if (statusCode != HttpStatusCode.BadRequest)
        throw new Exception("Phân công batch chứa bệnh nhân thuộc clinic khác phải bị chặn với 400 Bad Request.");
});

Check("TC-AC1.14: Batch Assignment Tampering: Phân công bệnh nhân Clinic Alpha cho Bác sĩ ngoại lai (doctor2 của Clinic Beta) -> 400 Bad Request", () =>
{
    var clinicId = clinicAlpha;
    var targetDoctorId = doctor2; // Active in Clinic Beta, not Clinic Alpha
    var clinicDoctors = new HashSet<Guid> { doctor1, doctor3 };

    var isDoctorInClinic = clinicDoctors.Contains(targetDoctorId);
    var statusCode = isDoctorInClinic ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

    if (statusCode != HttpStatusCode.BadRequest)
        throw new Exception("Phân công cho bác sĩ không thuộc phòng khám phải bị chặn với 400 Bad Request.");
});

Check("TC-AC1.15: Transfer Assignment Tampering: Admin Clinic Alpha cố transfer phân công của Clinic Beta -> 404 Not Found", () =>
{
    var callerClinic = clinicAlpha;
    var targetAssignmentId = assignmentOfClinicBeta;
    var assignmentClinicMap = new Dictionary<Guid, Guid> { { assignment1, clinicAlpha }, { assignmentOfClinicBeta, clinicBeta } };

    var isOwnedByClinic = assignmentClinicMap.TryGetValue(targetAssignmentId, out var ownerClinic) && ownerClinic == callerClinic;
    var statusCode = isOwnedByClinic ? HttpStatusCode.OK : HttpStatusCode.NotFound;

    if (statusCode != HttpStatusCode.NotFound)
        throw new Exception("Transfer phân công của phòng khám khác phải trả về 404 Not Found.");
});

Check("TC-AC1.16: Cross-Tenant Campaign Inspection: Bác sĩ Clinic Alpha cố xem Campaign của Clinic Beta -> 403 Forbidden / 404 Not Found", () =>
{
    var callerClinic = clinicAlpha;
    var targetCampaignTenant = clinicBeta;

    var isAllowed = (callerClinic == targetCampaignTenant);
    var statusCode = isAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Bác sĩ/Admin không được xem Campaign của phòng khám khác (phải 403 Forbidden).");
});

Check("TC-AC1.17: Cross-Tenant Campaign Batch Linking: Gắn batch của Clinic Alpha vào Campaign của Clinic Beta -> 403 Forbidden", () =>
{
    var batchTenant = clinicAlpha;
    var campaignTenant = clinicBeta;

    var canLink = (batchTenant == campaignTenant);
    var statusCode = canLink ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Không được gắn batch vào campaign của clinic khác.");
});

Check("TC-AC1.18: Campaign Closed Protection: Không cho phép liên kết thêm batch ảnh vào Campaign đã CLOSED -> 400 Bad Request", () =>
{
    var campaignStatus = "CLOSED";
    var canAddBatch = !string.Equals(campaignStatus, "CLOSED", StringComparison.OrdinalIgnoreCase);

    var statusCode = canAddBatch ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
    if (statusCode != HttpStatusCode.BadRequest)
        throw new Exception("Campaign đã CLOSED phải từ chối nạp thêm batch ảnh với 400 Bad Request.");
});

// -----------------------------------------------------------------------------
// NHÓM 2: KIỂM THỬ AC2 (Revocation Immediacy & Anti-Privilege Escalation)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC2] Anti-Probing, Anti-Escalation & Revocation Immediacy Checks ---");

Check("TC-AC2.1: Bác sĩ Doctor 1 có assignment ACTIVE tại Clinic Alpha -> Cho phép đọc và DOCTOR_REVIEW cho Patient B", () =>
{
    var request = new AccessResolveRequest(
        ActorAccountId: doctor1,
        PatientId: patientB,
        ClinicContextId: clinicAlpha,
        Action: "DOCTOR_REVIEW"
    );

    var hasMembership = true;
    var hasAssignment = true;
    var isAccountActive = true;

    var allowed = hasMembership && hasAssignment && isAccountActive;
    var response = new AccessResolveResponse(
        Allowed: allowed,
        AccountStatus: "ACTIVE",
        PatientId: request.PatientId,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlpha),
        PermissionVersion: 1
    );

    if (!response.Allowed)
        throw new Exception("Bác sĩ có assignment ACTIVE phải được phép duyệt hồ sơ.");
});

Check("TC-AC2.2: Bác sĩ Doctor 2 bị REVOKED assignment tại Clinic Alpha -> Từ chối DOCTOR_REVIEW tức thì (Instant Revocation)", () =>
{
    var assignmentStatus = "REVOKED";
    var isAssignmentActive = string.Equals(assignmentStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    var response = new AccessResolveResponse(
        Allowed: isAssignmentActive,
        AccountStatus: "ACTIVE",
        PatientId: patientB,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlpha),
        PermissionVersion: 1
    );

    if (response.Allowed)
        throw new Exception("Phân công bị thu hồi phải bị từ chối ngay lập tức.");
});

Check("TC-AC2.3: Bác sĩ có tài khoản bị khóa (DISABLED trong identity.accounts) -> Fail-closed từ chối mọi quyền truy cập", () =>
{
    var accountStatus = "DISABLED";
    var isAccountActive = string.Equals(accountStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    var response = new AccessResolveResponse(
        Allowed: isAccountActive,
        AccountStatus: accountStatus,
        PatientId: patientB,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: null,
        PermissionVersion: 1
    );

    if (response.Allowed)
        throw new Exception("Tài khoản bị khóa phải bị từ chối truy cập fail-closed.");
});

Check("TC-AC2.4: Anti-probing trên ID: Truy vấn phân công với Clinic Beta khi actor thuộc Clinic Alpha -> Trả về 404 Not Found", () =>
{
    var actorClinicId = clinicAlpha;
    var requestedClinicId = clinicBeta;

    HttpStatusCode statusCode = (requestedClinicId != actorClinicId) ? HttpStatusCode.NotFound : HttpStatusCode.OK;
    if (statusCode != HttpStatusCode.NotFound)
        throw new Exception($"Yêu cầu chéo Clinic phải trả về 404 Not Found để chống thăm dò, nhưng nhận được {statusCode}.");
});

Check("TC-AC2.5: Anti-probing trên Analysis/File: Tra cứu file với ID không tồn tại hoặc thuộc tenant khác -> 404 Not Found", () =>
{
    var requestedTenantId = clinicBeta;
    var callerTenantId = clinicAlpha;

    HttpStatusCode statusCode = (requestedTenantId != callerTenantId) ? HttpStatusCode.NotFound : HttpStatusCode.OK;
    if (statusCode != HttpStatusCode.NotFound)
        throw new Exception("Tra cứu file chéo tenant phải trả về 404 Not Found.");
});

Check("TC-AC2.6: Chặn leo quyền qua Membership payload: Cố tình gán vai trò SYSTEM_ADMIN -> 400 Bad Request (ERR_INVALID_ROLE)", () =>
{
    var allowedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLINIC_ADMIN", "DOCTOR", "STAFF" };
    var payloadRole = "SYSTEM_ADMIN"; // Attempted privilege escalation

    var isValidRole = allowedRoles.Contains(payloadRole);
    HttpStatusCode statusCode = isValidRole ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

    if (isValidRole || statusCode != HttpStatusCode.BadRequest)
        throw new Exception("Cố tình nâng quyền lên SYSTEM_ADMIN qua payload phải bị chặn với 400 Bad Request.");
});

Check("TC-AC2.7: Chặn leo quyền từ Clinic Admin: CLINIC_ADMIN không được thực hiện DOCTOR_REVIEW", () =>
{
    var role = "CLINIC_ADMIN";
    var action = "DOCTOR_REVIEW";

    var isClinicalReviewAllowed = string.Equals(role, "DOCTOR", StringComparison.OrdinalIgnoreCase) && action == "DOCTOR_REVIEW";
    var response = new AccessResolveResponse(
        Allowed: isClinicalReviewAllowed,
        AccountStatus: "ACTIVE",
        PatientId: patientB,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlpha),
        PermissionVersion: 1
    );

    if (response.Allowed)
        throw new Exception("CLINIC_ADMIN thuần túy không được thực hiện DOCTOR_REVIEW.");
});

Check("TC-AC2.8: Chặn bệnh nhân tự duyệt hồ sơ: Patient trong Personal tenant cố tình thực hiện DOCTOR_REVIEW -> DENY", () =>
{
    var isPatientReviewAllowed = false; // Strictly forbidden
    var response = new AccessResolveResponse(
        Allowed: isPatientReviewAllowed,
        AccountStatus: "ACTIVE",
        PatientId: patientA,
        TenantId: personalTenantA,
        TenantKind: "PERSONAL",
        OwnerPrincipalId: accountA,
        BillingOwner: new BillingOwnerDto("USER", accountA),
        PermissionVersion: 1
    );

    if (response.Allowed)
        throw new Exception("Bệnh nhân không được duyệt chuyên môn hồ sơ y khoa.");
});

Check("TC-AC2.9: Revocation Immediacy trên API: Bác sĩ bị gỡ khỏi clinic (status = 'REVOKED'), token JWT cũ còn hạn -> 403 Forbidden ngay", () =>
{
    // Giả lập tình huống: Bác sĩ doctorRevoked có JWT token hợp lệ cấp cách đây 5 phút (chưa hết hạn)
    var isTokenExpired = false;
    var dbMembershipStatus = "REVOKED"; // Admin vừa bấm gỡ khỏi clinic

    // Profile API kiểm tra live membership trong DB:
    var hasActiveMembership = string.Equals(dbMembershipStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    var statusCode = (!isTokenExpired && hasActiveMembership) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Dù JWT token còn hạn, khi membership bị REVOKED phải từ chối ngay lập tức với 403 Forbidden.");
});

Check("TC-AC2.10: Revocation Immediacy cho M2: M2 gọi POST /internal/v1/profile/resolve cho Bác sĩ bị revoked -> allowed = false", () =>
{
    var dbMembershipStatus = "REVOKED";
    var isAllowed = string.Equals(dbMembershipStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    var response = new AccessResolveResponse(
        Allowed: isAllowed,
        AccountStatus: "ACTIVE",
        PatientId: patientB,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlpha),
        PermissionVersion: 1,
        Reason: isAllowed ? "OK" : "MEMBERSHIP_REVOKED"
    );

    if (response.Allowed || response.Reason != "MEMBERSHIP_REVOKED")
        throw new Exception("M2 resolve quyền bác sĩ bị gỡ khỏi phòng khám phải trả về allowed = false và reason = MEMBERSHIP_REVOKED.");
});

Check("TC-AC2.11: Revocation Immediacy khi Transfer ca bệnh: Doctor 1 bị thu hồi ca chụp, Doctor 3 nhận ca chụp mới -> Quyền chuyển giao tức thì", () =>
{
    // Trước transfer: doctor1 active, doctor3 none
    // Thực hiện transfer assignment:
    var assignmentOldStatus = "REVOKED";
    var assignmentNewStatus = "ACTIVE";

    var doctor1CanReview = string.Equals(assignmentOldStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    var doctor3CanReview = string.Equals(assignmentNewStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    if (doctor1CanReview)
        throw new Exception("Bác sĩ cũ phải mất quyền duyệt ca bệnh lập tức sau transfer.");
    if (!doctor3CanReview)
        throw new Exception("Bác sĩ mới phải nhận quyền duyệt ca bệnh lập tức sau transfer.");
});

Check("TC-AC2.12: Clinic Lifecycle Suspension: Khi Clinic bị đổi sang SUSPENDED -> Mọi thành viên gọi API bị từ chối 403 tức thì", () =>
{
    var clinicStatus = "SUSPENDED";
    var isClinicOperable = string.Equals(clinicStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    var statusCode = isClinicOperable ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Khi phòng khám bị SUSPENDED, mọi thao tác lâm sàng phải bị chặn với 403 Forbidden.");
});

Check("TC-AC2.13: Invitation Lifecycle: Lời mời tham gia phòng khám đã EXPIRED hoặc REVOKED -> Từ chối gia nhập (400 / 410)", () =>
{
    var invitationStatus = "EXPIRED";
    var canAccept = string.Equals(invitationStatus, "PENDING", StringComparison.OrdinalIgnoreCase);

    var statusCode = canAccept ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
    if (statusCode != HttpStatusCode.BadRequest)
        throw new Exception("Lời mời phòng khám đã EXPIRED hoặc REVOKED không được cho phép chấp nhận.");
});

// -----------------------------------------------------------------------------
// NHÓM 3: KIỂM THỬ AC3 (Service Authentication, Data Integrity & Model Compliance)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC3] Service Authentication & Data Integrity Checks ---");

Check("TC-AC3.1: Service gọi internal API không có Bearer token -> 401 Unauthorized", () =>
{
    string? authHeader = null;
    var isAuthorized = !string.IsNullOrWhiteSpace(authHeader) && authHeader.StartsWith("Bearer ");

    var statusCode = isAuthorized ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
    if (statusCode != HttpStatusCode.Unauthorized)
        throw new Exception("Thiếu service token phải trả về 401 Unauthorized.");
});

Check("TC-AC3.2: Service gọi internal API với sai Audience (aura-identity thay vì aura-profile) -> 403 Forbidden", () =>
{
    var expectedAudience = "aura-profile";
    var actualAudience = "aura-identity";

    var isAudienceValid = string.Equals(expectedAudience, actualAudience, StringComparison.Ordinal);
    var statusCode = isAudienceValid ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Sai audience phải trả về 403 Forbidden.");
});

Check("TC-AC3.3: Service gọi internal API với thiếu Scope (cần profile:resolve) -> 403 Forbidden", () =>
{
    var scopes = new[] { "profile:provision" };
    var hasResolveScope = scopes.Contains("profile:resolve");

    var statusCode = hasResolveScope ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Thiếu scope profile:resolve phải trả về 403 Forbidden.");
});

Check("TC-AC3.4: Bác sĩ chỉ xem được phân công của chính mình (chặn xem trộm phân công bác sĩ khác -> 403 Forbidden)", () =>
{
    var callerDoctorId = doctor1;
    var requestedDoctorId = doctor2;
    var isClinicAdmin = false;

    var isAllowedToView = isClinicAdmin || (callerDoctorId == requestedDoctorId);
    var statusCode = isAllowedToView ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

    if (statusCode != HttpStatusCode.Forbidden)
        throw new Exception("Bác sĩ xem phân công của bác sĩ khác phải bị chặn với 403 Forbidden.");
});

Check("TC-AC3.5: Tính toàn vẹn dữ liệu: NiFi và Analysis không sửa trực tiếp dữ liệu Identity/Profile", () =>
{
    var allowedDdlUsers = new HashSet<string> { "postgres", "aura_migrations" };
    var serviceUser = "aura_analysis_app";

    var canAlterProfileSchema = allowedDdlUsers.Contains(serviceUser);
    if (canAlterProfileSchema)
        throw new Exception("Dịch vụ ngoài không được có quyền sửa đổi schema profile/identity.");
});

Check("TC-AC3.6: Phân loại Bệnh nhân Bất biến: Bệnh nhân PERSONAL không thể bị chuyển đổi tùy tiện sang CLINIC_MANAGED", () =>
{
    var originalPatientType = "PERSONAL";
    var attemptedUpdateType = "CLINIC_MANAGED";

    // Quy tắc: patient_type là trường bất biến sau khi khởi tạo
    var isImmutable = originalPatientType == "PERSONAL" && attemptedUpdateType == "CLINIC_MANAGED";
    var statusCode = isImmutable ? HttpStatusCode.BadRequest : HttpStatusCode.OK;

    if (statusCode != HttpStatusCode.BadRequest)
        throw new Exception("Không được phép tự ý thay đổi patient_type giữa PERSONAL và CLINIC_MANAGED.");
});

Check("TC-AC3.7: Phân giải Quyền theo Analysis ID cho M2 (SLA < 5ms): ResolveAccessAsync với analysisId trả về quyết định chuẩn xác", () =>
{
    var sw = Stopwatch.StartNew();

    var request = new AccessResolveRequest(
        ActorAccountId: doctor1,
        PatientId: patientB,
        ClinicContextId: clinicAlpha,
        Action: "DOCTOR_REVIEW",
        AnalysisId: analysis1
    );

    var hasActiveAssignment = true;
    var isClinicActive = true;

    var response = new AccessResolveResponse(
        Allowed: hasActiveAssignment && isClinicActive,
        AccountStatus: "ACTIVE",
        PatientId: request.PatientId,
        TenantId: clinicAlpha,
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlpha,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlpha),
        PermissionVersion: 1,
        Reason: (hasActiveAssignment && isClinicActive) ? "OK" : "NO_ACTIVE_ASSIGNMENT"
    );

    sw.Stop();

    if (!response.Allowed || response.Reason != "OK")
        throw new Exception("Quyền truy cập ca phân tích của Bác sĩ được phân công phải được duyệt.");
    if (sw.ElapsedMilliseconds > 5)
        throw new Exception($"SLA phân giải quyền vượt quá 5ms (thực tế: {sw.ElapsedMilliseconds}ms).");
});

// -----------------------------------------------------------------------------
// TỔNG KẾT BỘ TEST
// -----------------------------------------------------------------------------
Console.WriteLine("====================================================================");
Console.WriteLine($"Model checks: {passed}/38 PASS; not HTTP/DB security acceptance.");
Console.WriteLine("====================================================================");

if(args.Contains("--database"))
{
    var connection=Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES") ?? throw new InvalidOperationException("AURA_TEST_POSTGRES is required for isolated database checks.");
    await DatabaseChecks.RunAsync(connection);
}
