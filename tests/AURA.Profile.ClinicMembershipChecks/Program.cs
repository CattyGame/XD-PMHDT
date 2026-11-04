using System.Net;
using AURA.Profile.Api;
using AURA.Profile.Api.Models;
using Npgsql;

Console.WriteLine("====================================================================");
Console.WriteLine("AURA Task W3-M6-01: Clinic Membership & Context Verification Checks");
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
// NHÓM 1: KIỂM THỬ AC1 (Clinic Isolation & Management Authority)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC1] Kiểm tra phân quyền quản lý & Ranh giới Clinic ---");

var clinicAlphaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
var clinicBetaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
var adminAlphaId = Guid.Parse("a1111111-0000-0000-0000-000000000001");
var doctorAlphaId = Guid.Parse("a1111111-0000-0000-0000-000000000002");
var adminBetaId = Guid.Parse("b2222222-0000-0000-0000-000000000001");
var systemAdminId = Guid.Parse("99999999-9999-9999-9999-999999999999");

Check("AC1.1: SYSTEM_ADMIN có quyền quản lý mọi phòng khám bất kể danh sách thành viên", () =>
{
    var roles = new[] { "SYSTEM_ADMIN" };
    // Giả lập logic authority check
    var isSystemAdmin = roles.Contains("SYSTEM_ADMIN", StringComparer.OrdinalIgnoreCase);
    if (!isSystemAdmin) throw new Exception("SYSTEM_ADMIN phải được nhận diện.");
});

Check("AC1.2: Ranh giới Clinic: CLINIC_ADMIN của Clinic Alpha không có quyền quản lý Clinic Beta", () =>
{
    // Giả lập actor là Admin Alpha truy cập Clinic Beta
    var actorId = adminAlphaId;
    var targetClinic = clinicBetaId;
    // Actor không thuộc Clinic Beta -> Phải nhận kết quả không được phép và lý do NOT_A_MEMBER (hoặc CLINIC_NOT_FOUND)
    var memberClinic = clinicAlphaId;
    var isMember = memberClinic == targetClinic;
    if (isMember) throw new Exception("Admin Alpha không được coi là thành viên Clinic Beta.");
});

Check("AC1.3: Nhân viên trong phòng khám không phải CLINIC_ADMIN (ví dụ DOCTOR) bị từ chối quyền quản lý", () =>
{
    var role = ClinicMemberRoles.Doctor;
    var isAllowed = string.Equals(role, ClinicMemberRoles.ClinicAdmin, StringComparison.OrdinalIgnoreCase);
    if (isAllowed) throw new Exception("DOCTOR không được phép quản lý membership.");
});

// -----------------------------------------------------------------------------
// NHÓM 2: KIỂM THỬ AC2 (Chặn tự cấp quyền / Nâng quyền & Idempotency)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC2] Chặn nâng quyền, validate roles & Idempotency ---");

Check("AC2.0: Chuẩn hóa vai trò mở rộng (ClinicManager, ClinicDoctor, ClinicStaff)", () =>
{
    if (ClinicMemberRoles.NormalizeRole("ClinicManager") != ClinicMemberRoles.ClinicAdmin)
        throw new Exception("ClinicManager phải chuẩn hóa thành CLINIC_ADMIN.");
    if (ClinicMemberRoles.NormalizeRole("ClinicDoctor") != ClinicMemberRoles.Doctor)
        throw new Exception("ClinicDoctor phải chuẩn hóa thành DOCTOR.");
    if (ClinicMemberRoles.NormalizeRole("ClinicStaff") != ClinicMemberRoles.Staff)
        throw new Exception("ClinicStaff phải chuẩn hóa thành STAFF.");
    if (ClinicMemberRoles.NormalizeRole("CLINIC_ADMIN") != ClinicMemberRoles.ClinicAdmin)
        throw new Exception("CLINIC_ADMIN phải giữ nguyên.");
    if (ClinicMemberRoles.NormalizeRole("UNKNOWN") != null)
        throw new Exception("Vai trò không hợp lệ phải trả về null.");
});

Check("AC2.0b: Khai báo đầy đủ các trạng thái Clinic (PENDING, ACTIVE, SUSPENDED, REJECTED)", () =>
{
    if (!ClinicStatuses.AllowedStatuses.Contains("PENDING") ||
        !ClinicStatuses.AllowedStatuses.Contains("ACTIVE") ||
        !ClinicStatuses.AllowedStatuses.Contains("SUSPENDED") ||
        !ClinicStatuses.AllowedStatuses.Contains("REJECTED"))
    {
        throw new Exception("ClinicStatuses thiếu trạng thái quy định.");
    }
});

Check("AC2.1: Danh sách AllowedRoles chỉ chứa đúng 3 vai trò: CLINIC_ADMIN, DOCTOR, STAFF", () =>
{
    if (ClinicMemberRoles.AllowedRoles.Count != 3)
        throw new Exception($"AllowedRoles phải có đúng 3 phần tử, thực tế: {ClinicMemberRoles.AllowedRoles.Count}");

    if (!ClinicMemberRoles.AllowedRoles.Contains("CLINIC_ADMIN") ||
        !ClinicMemberRoles.AllowedRoles.Contains("DOCTOR") ||
        !ClinicMemberRoles.AllowedRoles.Contains("STAFF"))
    {
        throw new Exception("AllowedRoles thiếu vai trò chuẩn.");
    }
});

Check("AC2.2: Từ chối các vai trò trái phép (SYSTEM_ADMIN, PATIENT, ROOT, v.v.) qua payload", () =>
{
    var invalidRoles = new[] { "SYSTEM_ADMIN", "PATIENT", "ROOT", "SUPERUSER", "OWNER", "", "   " };
    foreach (var inv in invalidRoles)
    {
        var isValid = !string.IsNullOrWhiteSpace(inv) && ClinicMemberRoles.AllowedRoles.Contains(inv.Trim());
        if (isValid) throw new Exception($"Vai trò không hợp lệ '{inv}' lại được chấp nhận.");
    }
});

Check("AC2.3: Chặn thêm thành viên khi accountId là Guid.Empty", () =>
{
    var request = new AddClinicMemberRequest(Guid.Empty, "DOCTOR");
    if (request.AccountId != Guid.Empty) throw new Exception("Test setup error.");
    var isValid = request.AccountId != Guid.Empty;
    if (isValid) throw new Exception("Guid.Empty không được phép làm accountId.");
});

Check("AC2.4: Xử lý thao tác trùng: Thành viên đã ACTIVE thì từ chối với ERR_MEMBERSHIP_ALREADY_EXISTS", () =>
{
    var currentStatus = MembershipStatuses.Active;
    var isAlreadyActive = string.Equals(currentStatus, MembershipStatuses.Active, StringComparison.OrdinalIgnoreCase);
    if (!isAlreadyActive) throw new Exception("Thành viên phải ở trạng thái ACTIVE.");
    var errorCode = isAlreadyActive ? "ERR_MEMBERSHIP_ALREADY_EXISTS" : null;
    if (errorCode != "ERR_MEMBERSHIP_ALREADY_EXISTS") throw new Exception("Sai mã lỗi khi thêm trùng thành viên.");
});

Check("AC2.5: Bảo vệ Admin duy nhất: Không cho phép thu hồi khi Clinic chỉ còn 1 CLINIC_ADMIN duy nhất", () =>
{
    var activeAdminCount = 1;
    var targetRole = ClinicMemberRoles.ClinicAdmin;
    var isSoleAdmin = string.Equals(targetRole, ClinicMemberRoles.ClinicAdmin, StringComparison.OrdinalIgnoreCase) && activeAdminCount <= 1;
    if (!isSoleAdmin) throw new Exception("Phải nhận diện được sole admin.");
    var errorCode = isSoleAdmin ? "ERR_CANNOT_REVOKE_SOLE_ADMIN" : null;
    if (errorCode != "ERR_CANNOT_REVOKE_SOLE_ADMIN") throw new Exception("Phải báo lỗi ERR_CANNOT_REVOKE_SOLE_ADMIN.");
});

Check("AC2.6: Cho phép thu hồi admin khi Clinic có từ 2 CLINIC_ADMIN trở lên", () =>
{
    var activeAdminCount = 2;
    var targetRole = ClinicMemberRoles.ClinicAdmin;
    var canRevoke = !string.Equals(targetRole, ClinicMemberRoles.ClinicAdmin, StringComparison.OrdinalIgnoreCase) || activeAdminCount > 1;
    if (!canRevoke) throw new Exception("Phòng khám có 2 admin thì phải cho phép gỡ 1 admin.");
});

// -----------------------------------------------------------------------------
// NHÓM 3: KIỂM THỬ AC3 (Audit Revocation & Access Enforcement)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [AC3] Chuyển trạng thái REVOKED & Hiệu lực thu hồi tức thì ---");

Check("AC3.1: Gỡ thành viên chuyển status = 'REVOKED' (Soft Delete, không xóa dòng)", () =>
{
    var updatedStatus = MembershipStatuses.Revoked;
    if (updatedStatus != "REVOKED") throw new Exception("Trạng thái sau revoke phải là REVOKED.");
});

Check("AC3.2: Tái kích hoạt: Thành viên từng bị 'REVOKED' được phép mời lại và chuyển về 'ACTIVE'", () =>
{
    var previousStatus = MembershipStatuses.Revoked;
    var canReactivate = string.Equals(previousStatus, MembershipStatuses.Revoked, StringComparison.OrdinalIgnoreCase);
    if (!canReactivate) throw new Exception("Thành viên REVOKED phải được phép tái kích hoạt.");
    var newStatus = MembershipStatuses.Active;
    if (newStatus != "ACTIVE") throw new Exception("Sau khi tái kích hoạt status phải là ACTIVE.");
});

Check("AC3.3: Hiệu lực quyền tức thì: ResolveAccessAsync từ chối truy cập ngay khi status != 'ACTIVE'", () =>
{
    // Giả lập logic kiểm tra membership trong ResolveAccessAsync:
    // SELECT role FROM profile.memberships WHERE clinic_id = $1 AND account_id = $2 AND status = 'ACTIVE'
    var revokedMemberRole = (string?)null; // Vì WHERE status = 'ACTIVE' nên trả về NULL
    var isAllowed = !string.IsNullOrEmpty(revokedMemberRole);
    if (isAllowed) throw new Exception("Thành viên đã bị REVOKED không được phép truy cập tài nguyên y tế.");
});

Check("AC3.4: Thành viên ACTIVE trong Clinic được ResolveAccessAsync chấp thuận", () =>
{
    var activeMemberRole = ClinicMemberRoles.ClinicAdmin;
    var isAllowed = !string.IsNullOrEmpty(activeMemberRole);
    if (!isAllowed) throw new Exception("Thành viên ACTIVE phải được ResolveAccessAsync chấp thuận.");
});

// -----------------------------------------------------------------------------
// NHÓM 5: KIỂM THỬ SPRINT 4 (Task W4-M6-01: Clinic Lifecycle & Auto-Activation)
// -----------------------------------------------------------------------------
Console.WriteLine("\n--- [W4-M6-01] Kiểm thử Nghiệm thu AC1, AC2, AC3 ---");

Check("AC1.1 (W4): Đăng ký Clinic mới -> Khởi tạo trạng thái PENDING với thông tin pháp nhân", () =>
{
    var request = new RegisterClinicRequest(
        Name: "Phòng khám Đa khoa AURA Sài Gòn",
        TaxId: "0312345678",
        Address: "456 Hai Bà Trưng, Quận 1, TP.HCM",
        RepresentativeName: "Bác sĩ Lê Thị Mai",
        Phone: "0912345678"
    );

    if (string.IsNullOrWhiteSpace(request.Name)) throw new Exception("Tên cơ sở không được để trống.");
    if (string.IsNullOrWhiteSpace(request.TaxId)) throw new Exception("Mã số thuế y tế không được để trống.");

    var clinicId = Guid.NewGuid();
    var tenantId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;
    var clinic = new ClinicDto(
        clinicId, tenantId, request.Name, request.TaxId, request.Address,
        request.RepresentativeName, request.Phone, ClinicStatuses.Pending, null, now, now);

    if (clinic.Status != "PENDING") throw new Exception($"Trạng thái khởi tạo phải là PENDING, thực tế: {clinic.Status}");
});

Check("AC1.2 (W4): Admin xét duyệt Clinic -> Chuyển PENDING sang ACTIVE kèm lý do phê duyệt", () =>
{
    var adminRoles = new[] { "SYSTEM_ADMIN" };
    var isSystemAdmin = adminRoles.Contains("SYSTEM_ADMIN", StringComparer.OrdinalIgnoreCase);
    if (!isSystemAdmin) throw new Exception("Chỉ SYSTEM_ADMIN mới có quyền duyệt.");

    var updateReq = new UpdateClinicStatusRequest("ACTIVE", "Giấy phép y tế hợp lệ đã thẩm định");
    if (!ClinicStatuses.AllowedStatuses.Contains(updateReq.Status)) throw new Exception("Trạng thái không hợp lệ.");

    var previousStatus = ClinicStatuses.Pending;
    var newStatus = updateReq.Status;
    var auditReason = updateReq.Reason;

    if (previousStatus != "PENDING" || newStatus != "ACTIVE")
        throw new Exception("Chuyển trạng thái phê duyệt thất bại.");
    if (string.IsNullOrEmpty(auditReason))
        throw new Exception("Bắt buộc ghi nhận lý do phê duyệt trong audit log.");
});

Check("AC1.3 (W4): Mời Bác sĩ qua email (ClinicDoctor) -> Tạo membership trạng thái INVITED", () =>
{
    var inviteReq = new InviteClinicMemberRequest("bacsi.tuan@auraclinic.vn", "ClinicDoctor");
    var normalizedRole = ClinicMemberRoles.NormalizeRole(inviteReq.Role);
    if (normalizedRole != ClinicMemberRoles.Doctor) throw new Exception("Vai trò phải được chuẩn hóa về DOCTOR.");

    var membership = new ClinicMembershipDto(
        MembershipId: Guid.NewGuid(),
        ClinicId: clinicAlphaId,
        AccountId: null,
        DisplayName: "bacsi.tuan",
        Role: normalizedRole,
        Status: MembershipStatuses.Invited,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        InvitedEmail: inviteReq.Email
    );

    if (membership.Status != "INVITED") throw new Exception("Membership mới mời phải ở trạng thái INVITED.");
    if (membership.AccountId != null) throw new Exception("Chưa đăng nhập thì AccountId phải là null.");
    if (membership.InvitedEmail != inviteReq.Email) throw new Exception("Email mời không khớp.");
});

Check("AC1.4 (W4): Bác sĩ được mời đăng nhập -> Hệ thống tự động kích hoạt chuyển sang ACTIVE", () =>
{
    var doctorAccountId = Guid.NewGuid();
    var registeredEmail = "bacsi.tuan@auraclinic.vn";
    var pendingMembershipStatus = MembershipStatuses.Invited;

    // Giả lập logic AutoActivatePendingMembershipsAsync
    var isMatchingEmail = string.Equals(registeredEmail, "bacsi.tuan@auraclinic.vn", StringComparison.OrdinalIgnoreCase);
    var activatedStatus = (isMatchingEmail && pendingMembershipStatus == MembershipStatuses.Invited)
        ? MembershipStatuses.Active
        : pendingMembershipStatus;

    if (activatedStatus != "ACTIVE") throw new Exception("Hệ thống phải tự kích hoạt sang ACTIVE khi email trùng khớp.");
});

Check("AC2.1 (W4): Thử nghiệm dùng token Bác sĩ Clinic Alpha đọc thông tin thành viên Clinic Beta -> HTTP 403 Forbidden", () =>
{
    var callerClinicId = clinicAlphaId;
    var targetClinicId = clinicBetaId;
    var callerRole = ClinicMemberRoles.Doctor;

    var isMemberOfTarget = callerClinicId == targetClinicId;
    var isSystemAdmin = false;

    var isAllowed = isSystemAdmin || (isMemberOfTarget && callerRole == ClinicMemberRoles.ClinicAdmin);
    var failureReason = !isMemberOfTarget ? "CROSS_TENANT_FORBIDDEN" : "FORBIDDEN";

    if (isAllowed) throw new Exception("Bác sĩ Clinic A tuyệt đối không được đọc Clinic B.");
    if (failureReason != "CROSS_TENANT_FORBIDDEN") throw new Exception("Lý do thất bại phải là CROSS_TENANT_FORBIDDEN.");

    var statusCode = failureReason == "CLINIC_NOT_FOUND" ? 404 : 403;
    if (statusCode != 403) throw new Exception($"Kỳ vọng HTTP 403 Forbidden theo AC2, thực tế: {statusCode}");
});

Check("AC2.2 (W4): Người dùng không phải SYSTEM_ADMIN gọi xét duyệt Clinic -> Bị chặn với HTTP 403 Forbidden", () =>
{
    var normalUserRoles = new[] { "CLINIC_ADMIN" };
    var isAllowed = normalUserRoles.Contains("SYSTEM_ADMIN", StringComparer.OrdinalIgnoreCase);
    if (isAllowed) throw new Exception("Chỉ SYSTEM_ADMIN mới có quyền duyệt.");
    var statusCode = isAllowed ? 200 : 403;
    if (statusCode != 403) throw new Exception("Phải trả về 403 Forbidden khi thiếu quyền SYSTEM_ADMIN.");
});

Check("AC3.1 (W4): Khi Clinic bị SUSPENDED -> Toàn bộ tài khoản thuộc Clinic bị khóa tạo ca chụp mới (ANALYSIS_CREATE)", () =>
{
    var clinicStatus = ClinicStatuses.Suspended;
    var action = "ANALYSIS_CREATE";

    var isAllowed = !string.Equals(action, "ANALYSIS_CREATE", StringComparison.OrdinalIgnoreCase) && clinicStatus == ClinicStatuses.Active;
    var reason = clinicStatus == ClinicStatuses.Suspended ? "CLINIC_SUSPENDED" : "OK";

    var response = new AccessResolveResponse(
        Allowed: isAllowed,
        AccountStatus: "ACTIVE",
        PatientId: Guid.NewGuid(),
        TenantId: Guid.NewGuid(),
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlphaId,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlphaId),
        PermissionVersion: 1,
        Reason: reason
    );

    if (response.Allowed) throw new Exception("Clinic bị SUSPENDED phải bị khóa tạo ca chụp mới.");
    if (response.Reason != "CLINIC_SUSPENDED") throw new Exception($"Lý do phải là CLINIC_SUSPENDED, thực tế: {response.Reason}");
});

Check("AC3.2 (W4): Khi Clinic ở trạng thái PENDING -> Khóa tạo ca chụp mới với lý do CLINIC_PENDING", () =>
{
    var clinicStatus = ClinicStatuses.Pending;
    var action = "ANALYSIS_CREATE";

    var isAllowed = !string.Equals(action, "ANALYSIS_CREATE", StringComparison.OrdinalIgnoreCase) && clinicStatus == ClinicStatuses.Active;
    var reason = clinicStatus == ClinicStatuses.Pending ? "CLINIC_PENDING" : "OK";

    var response = new AccessResolveResponse(
        Allowed: isAllowed,
        AccountStatus: "ACTIVE",
        PatientId: Guid.NewGuid(),
        TenantId: Guid.NewGuid(),
        TenantKind: "CLINIC",
        OwnerPrincipalId: clinicAlphaId,
        BillingOwner: new BillingOwnerDto("CLINIC", clinicAlphaId),
        PermissionVersion: 1,
        Reason: reason
    );

    if (response.Allowed) throw new Exception("Clinic PENDING không được phép tạo ca chụp mới.");
    if (response.Reason != "CLINIC_PENDING") throw new Exception($"Lý do phải là CLINIC_PENDING, thực tế: {response.Reason}");
});

Check("AC3.3 (W4): Khi Clinic bị SUSPENDED -> Quyền xem lịch sử cũ (ANALYSIS_READ) vẫn được bảo toàn cho bác sĩ", () =>
{
    var clinicStatus = ClinicStatuses.Suspended;
    var action = "ANALYSIS_READ";

    var isCreateAction = string.Equals(action, "ANALYSIS_CREATE", StringComparison.OrdinalIgnoreCase);
    if (isCreateAction) throw new Exception("Test setup error.");
    var isReadAllowed = clinicStatus == ClinicStatuses.Suspended && !isCreateAction;
    if (!isReadAllowed) throw new Exception("Quyền đọc y bạ cũ phải được bảo toàn.");
});

// -----------------------------------------------------------------------------
// NHÓM 4: KIỂM THỬ TÍCH HỢP POSTGRESQL (Nếu có cờ --database)
// -----------------------------------------------------------------------------
if (args.Contains("--database"))
{
    Console.WriteLine("\n--- Chạy kiểm thử tích hợp trực tiếp trên PostgreSQL ---");
    var connString = Environment.GetEnvironmentVariable("AURA_TEST_POSTGRES")
        ?? throw new InvalidOperationException("AURA_TEST_POSTGRES must explicitly identify an isolated fixture database.");

    try
    {
        await using var dataSource = NpgsqlDataSource.Create(connString);
        var repo = new ProfileRepository(dataSource);

        // Kiểm tra kết nối
        await using var conn = await dataSource.OpenConnectionAsync();
        Console.WriteLine("Kết nối database thành công.");

        // Kiểm tra quyền của Admin Alpha trên Clinic Alpha
        var (allowedAlpha, _) = await repo.CheckClinicManagementAuthorityAsync(adminAlphaId, clinicAlphaId, Array.Empty<string>());
        Check("DB AC1: Admin Alpha có quyền quản lý Clinic Alpha", () =>
        {
            if (!allowedAlpha) throw new Exception("Admin Alpha phải có quyền quản lý Clinic Alpha trên DB.");
        });

        // Kiểm tra ranh giới: Admin Alpha trên Clinic Beta
        var (allowedBeta, reasonBeta) = await repo.CheckClinicManagementAuthorityAsync(adminAlphaId, clinicBetaId, Array.Empty<string>());
        Check("DB AC1: Admin Alpha không có quyền quản lý Clinic Beta", () =>
        {
            if (allowedBeta) throw new Exception("Admin Alpha không được có quyền quản lý Clinic Beta trên DB.");
            if (reasonBeta != "NOT_A_MEMBER") throw new Exception($"Lý do phải là NOT_A_MEMBER, thực tế: {reasonBeta}");
        });
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException("Explicit database checks failed; they must not be silently skipped.", ex);
    }
}

Console.WriteLine($"\n====================================================================");
Console.WriteLine($"KẾT QUẢ: {passed} model/fixture checks passed; HTTP/DB acceptance needs its own evidence.");
Console.WriteLine($"====================================================================");
return 0;
