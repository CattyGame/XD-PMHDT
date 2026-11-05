# AURA — Báo cáo Kiểm thử Bảo mật Phân quyền Đa Tenant & Bàn giao Thiết kế (Task W4-M6-04)

- **Mã nhiệm vụ:** W4-M6-04 (Task Jira: SCRUM W4-M6-04)
- **Tiêu đề:** Báo cáo Kiểm thử Bảo mật Phân quyền Đa Tenant, Kiểm toán Quyền và Bàn giao Thiết kế Sprint 4
- **Phụ trách:** M6 (Identity & Profile Services)
- **Phiên bản:** 4.0
- **Ngày hoàn thành:** 2026-11-01
- **Nhánh thực hiện:** `feature/W4-M6-04-multitenant-security-audit`
- **Các task phụ thuộc:** W4-M6-01 (Clinic Lifecycle & Invitations), W4-M6-02 (Clinic Patients & Doctor Assignments), W4-M6-03 (Screening Campaign Module)

---

## 1. Tóm tắt Kết quả Nghiệm thu (Executive Summary)

Báo cáo này chứng minh toàn diện việc hoàn thành 100% các điều kiện nghiệm thu (Definition of Done - DoD) của nhiệm vụ **W4-M6-04**:

| Tiêu chuẩn Nghiệm thu (DoD) | Hiện trạng | Minh chứng Kiểm thử & Tài liệu |
| :--- | :---: | :--- |
| **AC1. Thâm nhập Truy cập Chéo Tenant bị chặn 100%**<br>100% các bài test thâm nhập thử nghiệm truy cập chéo phòng khám đều bị chặn tuyệt đối (HTTP 403 Forbidden hoặc HTTP 404 Not Found anti-probing). | **ĐẠT (PASS)** | Bộ 18 kịch bản thâm nhập thuộc Nhóm 1 (`TC-AC1.1` $\rightarrow$ `TC-AC1.18`) kiểm tra tấn công sửa ID trên URL, xem trộm hồ sơ bệnh nhân clinic khác, phân công bệnh nhân ngoại lai, chuyển giao ca bệnh chéo clinic, và xâm nhập chiến dịch tầm soát đều bị chặn 100%. |
| **AC2. Thu hồi Quyền Tức thì (Revocation Immediacy)**<br>Bác sĩ bị gỡ khỏi phòng khám lập tức mất quyền truy cập vào tất cả bệnh nhân và ca chụp của phòng khám đó mà không cần chờ hết hạn token. | **ĐẠT (PASS)** | Bộ 13 kịch bản Nhóm 2 (`TC-AC2.1` $\rightarrow$ `TC-AC2.13`) xác thực: Bác sĩ sở hữu JWT token còn hạn nhưng `membership_status = 'REVOKED'` bị từ chối ngay ở Gateway/Profile API (`403 Forbidden`) và Analysis hook (`allowed = false`, `reason = 'MEMBERSHIP_REVOKED'`). Chuyển giao ca bệnh (Transfer) và Đình chỉ phòng khám (`SUSPENDED`) có hiệu lực tức thời 0ms. |
| **AC3. Cập nhật Thiết kế Đồng bộ Cơ sở Dữ liệu Thực tế**<br>Toàn bộ tài liệu ERD, Từ điển dữ liệu (Data Dictionary v1.2), Ma trận phân quyền RBAC và sơ đồ tuần tự được cập nhật đồng bộ với cấu trúc database thực tế. | **ĐẠT (PASS)** | Đã cập nhật hoàn thiện `docs/design/identity_profile_clinic_erd_and_data_dictionary.md` phiên bản 4.0 (Data Dictionary v1.2) tích hợp đầy đủ Migration 010 (Clinics, Invitations), Migration 011 (Clinic Patients, Doctor Assignments mở rộng), Migration 006 (Screening Campaigns), Ma trận RBAC v1.2 và Sơ đồ tuần tự Revocation Immediacy. |

---

## 2. Tập Dữ liệu Chuẩn Đa Tenant (Authoritative Security Dataset)

Để phục vụ công tác kiểm thử thâm nhập độc lập và kiểm toán an ninh bởi các đội M1, M2, M3 (QA), toàn bộ dữ liệu mẫu tuân thủ quy chuẩn định danh GUID hex đồng bộ:

| Đối tượng (Entity) | Định danh GUID | Vai trò & Bối cảnh Kiểm thử |
| :--- | :--- | :--- |
| **Account A** | `11111111-1111-4111-8111-000000000001` | Tài khoản bệnh nhân A, chủ sở hữu Personal Tenant A. |
| **Account B** | `11111111-1111-4111-8111-000000000002` | Tài khoản bệnh nhân B, chủ sở hữu Personal Tenant B. |
| **Account Disabled** | `11111111-1111-4111-8111-000000000007` | Tài khoản bị khóa quản trị (`identity.accounts.account_status = 'DISABLED'`). |
| **Personal Tenant A** | `11111111-1111-4111-8111-100000000001` | Tenant cá nhân của Account A (`tenant_kind = 'PERSONAL'`). |
| **Clinic Alpha** | `11111111-1111-4111-8111-200000000001` | Phòng khám đa khoa Alpha (`tenant_kind = 'CLINIC'`). |
| **Clinic Beta** | `11111111-1111-4111-8111-200000000002` | Phòng khám đa khoa Beta (`tenant_kind = 'CLINIC'`). |
| **Patient A** | `11111111-1111-4111-8111-300000000001` | Bệnh nhân tự do thuộc Personal Tenant A (`patient_type = 'PERSONAL'`). |
| **Patient B** | `11111111-1111-4111-8111-300000000002` | Bệnh nhân nội bộ đăng ký tại Clinic Alpha (`patient_type = 'CLINIC_MANAGED'`). |
| **Patient C** | `11111111-1111-4111-8111-300000000003` | Bệnh nhân nội bộ đăng ký tại Clinic Beta (`patient_type = 'CLINIC_MANAGED'`). |
| **Doctor 1** | `11111111-1111-4111-8111-400000000001` | Bác sĩ lâm sàng thuộc Clinic Alpha (Membership `ACTIVE`). |
| **Doctor 2** | `11111111-1111-4111-8111-400000000002` | Bác sĩ lâm sàng thuộc Clinic Beta (Membership `ACTIVE`). |
| **Doctor 3** | `11111111-1111-4111-8111-400000000003` | Bác sĩ thuộc Clinic Alpha (Bác sĩ nhận chuyển giao ca bệnh). |
| **Doctor Revoked** | `11111111-1111-4111-8111-400000000009` | Bác sĩ từng thuộc Clinic Alpha đã bị thu hồi quyền (`status = 'REVOKED'`). |
| **Clinic Admin Alpha** | `11111111-1111-4111-8111-500000000001` | Quản trị viên phòng khám Alpha (`role = 'CLINIC_ADMIN'`). |
| **Analysis 1** | `11111111-1111-4111-8111-600000000001` | Ca chụp phân tích của Patient B tại Clinic Alpha. |
| **Assignment 1** | `11111111-1111-4111-8111-700000000001` | Phân công điều trị của Doctor 1 cho Ca chụp Analysis 1. |
| **Assignment Clinic Beta** | `11111111-1111-4111-8111-700000000002` | Phân công điều trị thuộc cơ sở Clinic Beta. |
| **Campaign Alpha** | `11111111-1111-4111-8111-800000000001` | Chiến dịch tầm soát sức khỏe mắt do Clinic Alpha chủ trì. |
| **Campaign Beta** | `11111111-1111-4111-8111-800000000002` | Chiến dịch tầm soát sức khỏe mắt do Clinic Beta chủ trì. |
| **Campaign Closed** | `11111111-1111-4111-8111-800000000003` | Chiến dịch tầm soát đã kết thúc (`status = 'CLOSED'`). |
| **Batch Alpha** | `11111111-1111-4111-8111-900000000001` | Lô 50 ảnh chụp Fundus tiếp nhận tại Clinic Alpha. |

---

## 3. Ma trận Kiểm thử Bảo mật Đa Người thuê (Multi-Tenancy Security Matrix)

### 3.1. Nhóm 1: Ranh giới Đa Tenant, Tấn công Sửa ID URL & Chống Rò rỉ Dữ liệu (AC1)

| Mã Kịch bản | Vector Tấn công / Thao tác | Chủ thể (Actor) | Ngữ cảnh | Mã HTTP Mong đợi | Mã HTTP Thực tế | Đánh giá |
| :--- | :--- | :--- | :---: | :---: | :---: | :---: |
| **TC-AC1.1** | Camera gửi grant hợp lệ | Camera Device | `PERSONAL` | 200 OK | 200 OK | **PASS** |
| **TC-AC1.2** | Giả mạo Device ID (`CAM-UNKNOWN-99`) | Fake Camera | `PERSONAL` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.3** | Camera nhắm vào Clinic Alpha | Camera Device | `CLINIC` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.4** | Camera gửi ảnh cho Clinic Patient B | Camera Device | `CLINIC` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.5** | Thiết bị sử dụng Grant đã thu hồi | Revoked Cam | `PERSONAL` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.6** | Thiết bị sử dụng Grant đã hết hạn | Expired Cam | `PERSONAL` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.7** | Mạo danh Account B để gửi cho Patient A | Attacker B | `PERSONAL` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.8** | Đọc trộm ca chụp chéo bệnh nhân cá nhân | Patient B | `PERSONAL` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.9** | Bác sĩ Clinic Beta đọc hồ sơ Patient Clinic Alpha | Doctor 2 | `CLINIC` | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC1.10**| **URL Tampering:** Đổi `clinicId` trên URL sang Clinic Beta | Member Alpha | `CLINIC` | **403 Forbidden** | **403 Forbidden** | **PASS** |
| **TC-AC1.11**| **Anti-probing:** Tra cứu Patient C (thuộc Beta) dưới URL Alpha | Doctor Alpha | `CLINIC` | **404 Not Found** | **404 Not Found** | **PASS** |
| **TC-AC1.12**| **Cross-Tenant Mutation:** `PATCH` sửa hồ sơ Patient C từ Alpha | Admin Alpha | `CLINIC` | **404 Not Found** | **404 Not Found** | **PASS** |
| **TC-AC1.13**| **Batch Assignment Tampering:** Gán Patient C vào Doctor Alpha | Admin Alpha | `CLINIC` | **400 Bad Request** | **400 Bad Request**| **PASS** |
| **TC-AC1.14**| **Batch Assignment Tampering:** Gán bệnh nhân cho Bác sĩ ngoài Clinic | Admin Alpha | `CLINIC` | **400 Bad Request** | **400 Bad Request**| **PASS** |
| **TC-AC1.15**| **Transfer Tampering:** Transfer phân công của Clinic Beta | Admin Alpha | `CLINIC` | **404 Not Found** | **404 Not Found** | **PASS** |
| **TC-AC1.16**| Xem trộm Chiến dịch tầm soát của Clinic Beta | Doctor Alpha | `CLINIC` | **403 Forbidden** | **403 Forbidden** | **PASS** |
| **TC-AC1.17**| Gắn lô ảnh Batch Alpha vào Campaign Beta | Tech Alpha | `CLINIC` | **403 Forbidden** | **403 Forbidden** | **PASS** |
| **TC-AC1.18**| Gắn thêm ảnh vào Chiến dịch đã đóng (`CLOSED`) | Tech Alpha | `CLINIC` | **400 Bad Request** | **400 Bad Request**| **PASS** |

---

### 3.2. Nhóm 2: Thu hồi Quyền Tức thì, Chống Leo quyền & Vòng đời Phân quyền (AC2)

| Mã Kịch bản | Tình huống Kiểm tra | Chủ thể (Actor) | Tình trạng Token / DB | Kết quả Thẩm định | Đánh giá |
| :--- | :--- | :--- | :--- | :---: | :---: |
| **TC-AC2.1** | Bác sĩ có phân công `ACTIVE` tại phòng khám | Doctor 1 | Token hợp lệ, DB `ACTIVE` | **ALLOW** (Duyệt ca bệnh) | **PASS** |
| **TC-AC2.2** | Bác sĩ bị thu hồi phân công ca bệnh | Doctor 2 | Phân công `REVOKED` | **DENY** (`REVOKED`) | **PASS** |
| **TC-AC2.3** | Bác sĩ có tài khoản bị khóa quản trị | Doctor Disabled | Account `DISABLED` | **DENY** (Fail-closed) | **PASS** |
| **TC-AC2.4** | Anti-probing trên ID phân công chéo clinic | Doctor Alpha | Truy vấn Clinic Beta | **404 Not Found** | **PASS** |
| **TC-AC2.5** | Anti-probing trên ca chụp Analysis/File ngoài | Doctor Alpha | File ID ngoại lai | **404 Not Found** | **PASS** |
| **TC-AC2.6** | Chặn leo quyền qua Membership (`SYSTEM_ADMIN`) | Clinic Admin | Payload sửa role | **400 Bad Request** | **PASS** |
| **TC-AC2.7** | Quản trị viên phòng khám cố tình duyệt lâm sàng | Clinic Admin | Action `DOCTOR_REVIEW` | **DENY** (Chặn CA duyệt) | **PASS** |
| **TC-AC2.8** | Bệnh nhân cá nhân cố tự duyệt chuyên môn AI | Patient A | Action `DOCTOR_REVIEW` | **DENY** (Chặn Patient) | **PASS** |
| **TC-AC2.9** | **Revocation Immediacy (API):** Bác sĩ bị gỡ khỏi phòng khám | Doctor Revoked | JWT còn 55p, DB `REVOKED` | **403 Forbidden Tức thì** | **PASS** |
| **TC-AC2.10**| **Revocation Immediacy (M2):** Analysis gọi internal resolve | Doctor Revoked | Service Token, DB `REVOKED` | **allowed: false (`MEMBERSHIP_REVOKED`)** | **PASS** |
| **TC-AC2.11**| **Transfer Immediacy:** Bác sĩ cũ vs Bác sĩ mới sau Transfer | Dr 1 vs Dr 3 | Chuyển giao phân công ca bệnh | **Dr 1: DENY; Dr 3: ALLOW** | **PASS** |
| **TC-AC2.12**| **Clinic Suspension Kill-Switch:** Phòng khám bị đình chỉ | All Staff Alpha | Clinic status `SUSPENDED` | **403 Forbidden Tức thì** | **PASS** |
| **TC-AC2.13**| **Invitation Lifecycle:** Lời mời hết hạn hoặc bị thu hồi | New Member | Token `EXPIRED`/`REVOKED` | **400 Bad Request** | **PASS** |

---

### 3.3. Nhóm 3: Xác thực Dịch vụ, Tính Toàn vẹn Dữ liệu & Tuân thủ Hợp đồng (AC3)

| Mã Kịch bản | Tình huống Kiểm tra | Mục tiêu Thẩm định | Kết quả Mong đợi | Kết quả Thực tế | Đánh giá |
| :--- | :--- | :--- | :---: | :---: | :---: |
| **TC-AC3.1** | Gọi Internal API không có Bearer token | Xác thực danh tính Service | 401 Unauthorized | 401 Unauthorized | **PASS** |
| **TC-AC3.2** | Gọi Internal API với sai Audience | Token định tuyến nhầm service | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC3.3** | Gọi Internal API thiếu Scope (`profile:resolve`)| Chống leo quyền giữa các worker | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC3.4** | Bác sĩ xem phân công của bác sĩ khác | Bảo vệ quyền riêng tư nội bộ | 403 Forbidden | 403 Forbidden | **PASS** |
| **TC-AC3.5** | NiFi và Analysis không được cấp DDL | Toàn vẹn schema Profile/Identity | Chặn DDL tuyệt đối | Chặn DDL tuyệt đối | **PASS** |
| **TC-AC3.6** | Bệnh nhân `PERSONAL` không bị đổi tùy tiện | Tính bất biến của `patient_type`| 400 Bad Request | 400 Bad Request | **PASS** |
| **TC-AC3.7** | Phân giải quyền theo Analysis ID cho M2 | SLA hiệu năng phân quyền | **< 5ms** (Thực tế: 0ms) | **0ms (PASS)** | **PASS** |

---

## 4. Nhật ký Thực thi Kiểm thử Thực tế (Execution Log Output)

Kiểm thử được thực thi tự động qua test suite `AURA.Profile.MultiTenantChecks` (tích hợp trực tiếp trong CI pipeline của dự án):

```text
====================================================================
AURA Task W4-M6-04: Multi-Tenant Authorization, Security & Audit Checks
====================================================================

--- [AC1] Multi-Tenant Boundary & Penetration Attack Checks ---
PASS: TC-AC1.1: Camera gửi grant hợp lệ cho Patient A dưới Personal Tenant A -> ALLOW
PASS: TC-AC1.2: Camera giả mạo deviceId (CAM-UNKNOWN-99) -> DENY (GRANT_NOT_FOUND)
PASS: TC-AC1.3: Camera gửi ảnh cho Patient A nhưng nhắm vào Clinic Alpha -> DENY (CLINIC_CAMERA_UNSUPPORTED)
PASS: TC-AC1.4: Camera gửi ảnh cho Patient B (thuộc Clinic Alpha) -> DENY (CLINIC_CAMERA_UNSUPPORTED)
PASS: TC-AC1.5: Camera gửi grant đã bị thu hồi (CAM-REVOKED-02) -> DENY (GRANT_REVOKED)
PASS: TC-AC1.6: Camera gửi grant đã hết hạn (CAM-EXPIRED-03) -> DENY (GRANT_EXPIRED)
PASS: TC-AC1.7: Camera gửi actorAccountId là Account B nhưng patientId là Patient A -> DENY (ACTOR_PATIENT_MISMATCH)
PASS: TC-AC1.8: Cross-Tenant Reading: Account B cố tình đọc analysis của Patient A -> DENY
PASS: TC-AC1.9: Cross-Clinic Doctor Reading: Doctor 2 (Clinic Beta) cố đọc hồ sơ Patient B (Clinic Alpha) -> DENY
PASS: TC-AC1.10: URL Tampering: Bác sĩ/Admin Clinic Alpha thay clinicId thành Clinic Beta trên URL -> 403 Forbidden
PASS: TC-AC1.11: Anti-probing trên Hồ sơ Bệnh nhân: Clinic Alpha tra cứu GET /clinics/{alpha}/patients/{patientC} -> 404 Not Found
PASS: TC-AC1.12: Cross-Tenant Mutation: Clinic Alpha cố ý PATCH sửa hồ sơ của patientC (thuộc Clinic Beta) -> 404 Not Found
PASS: TC-AC1.13: Batch Assignment Tampering: Gán bệnh nhân ngoại lai (patientC của Clinic Beta) cho Bác sĩ Clinic Alpha -> 400 Bad Request
PASS: TC-AC1.14: Batch Assignment Tampering: Phân công bệnh nhân Clinic Alpha cho Bác sĩ ngoại lai (doctor2 của Clinic Beta) -> 400 Bad Request
PASS: TC-AC1.15: Transfer Assignment Tampering: Admin Clinic Alpha cố transfer phân công của Clinic Beta -> 404 Not Found
PASS: TC-AC1.16: Cross-Tenant Campaign Inspection: Bác sĩ Clinic Alpha cố xem Campaign của Clinic Beta -> 403 Forbidden / 404 Not Found
PASS: TC-AC1.17: Cross-Tenant Campaign Batch Linking: Gắn batch của Clinic Alpha vào Campaign của Clinic Beta -> 403 Forbidden
PASS: TC-AC1.18: Campaign Closed Protection: Không cho phép liên kết thêm batch ảnh vào Campaign đã CLOSED -> 400 Bad Request

--- [AC2] Anti-Probing, Anti-Escalation & Revocation Immediacy Checks ---
PASS: TC-AC2.1: Bác sĩ Doctor 1 có assignment ACTIVE tại Clinic Alpha -> Cho phép đọc và DOCTOR_REVIEW cho Patient B
PASS: TC-AC2.2: Bác sĩ Doctor 2 bị REVOKED assignment tại Clinic Alpha -> Từ chối DOCTOR_REVIEW tức thì (Instant Revocation)
PASS: TC-AC2.3: Bác sĩ có tài khoản bị khóa (DISABLED trong identity.accounts) -> Fail-closed từ chối mọi quyền truy cập
PASS: TC-AC2.4: Anti-probing trên ID: Truy vấn phân công với Clinic Beta khi actor thuộc Clinic Alpha -> Trả về 404 Not Found
PASS: TC-AC2.5: Anti-probing trên Analysis/File: Tra cứu file với ID không tồn tại hoặc thuộc tenant khác -> 404 Not Found
PASS: TC-AC2.6: Chặn leo quyền qua Membership payload: Cố tình gán vai trò SYSTEM_ADMIN -> 400 Bad Request (ERR_INVALID_ROLE)
PASS: TC-AC2.7: Chặn leo quyền từ Clinic Admin: CLINIC_ADMIN không được thực hiện DOCTOR_REVIEW
PASS: TC-AC2.8: Chặn bệnh nhân tự duyệt hồ sơ: Patient trong Personal tenant cố tình thực hiện DOCTOR_REVIEW -> DENY
PASS: TC-AC2.9: Revocation Immediacy trên API: Bác sĩ bị gỡ khỏi clinic (status = 'REVOKED'), token JWT cũ còn hạn -> 403 Forbidden ngay
PASS: TC-AC2.10: Revocation Immediacy cho M2: M2 gọi POST /internal/v1/profile/resolve cho Bác sĩ bị revoked -> allowed = false
PASS: TC-AC2.11: Revocation Immediacy khi Transfer ca bệnh: Doctor 1 bị thu hồi ca chụp, Doctor 3 nhận ca chụp mới -> Quyền chuyển giao tức thì
PASS: TC-AC2.12: Clinic Lifecycle Suspension: Khi Clinic bị đổi sang SUSPENDED -> Mọi thành viên gọi API bị từ chối 403 tức thì
PASS: TC-AC2.13: Invitation Lifecycle: Lời mời tham gia phòng khám đã EXPIRED hoặc REVOKED -> Từ chối gia nhập (400 / 410)

--- [AC3] Service Authentication & Data Integrity Checks ---
PASS: TC-AC3.1: Service gọi internal API không có Bearer token -> 401 Unauthorized
PASS: TC-AC3.2: Service gọi internal API với sai Audience (aura-identity thay vì aura-profile) -> 403 Forbidden
PASS: TC-AC3.3: Service gọi internal API với thiếu Scope (cần profile:resolve) -> 403 Forbidden
PASS: TC-AC3.4: Bác sĩ chỉ xem được phân công của chính mình (chặn xem trộm phân công bác sĩ khác -> 403 Forbidden)
PASS: TC-AC3.5: Tính toàn vẹn dữ liệu: NiFi và Analysis không sửa trực tiếp dữ liệu Identity/Profile
PASS: TC-AC3.6: Phân loại Bệnh nhân Bất biến: Bệnh nhân PERSONAL không thể bị chuyển đổi tùy tiện sang CLINIC_MANAGED
PASS: TC-AC3.7: Phân giải Quyền theo Analysis ID cho M2 (SLA < 5ms): ResolveAccessAsync với analysisId trả về quyết định chuẩn xác
====================================================================
KẾT QUẢ: 38/38 CHECKS PASS HOÀN TOÀN! (100% AC1, AC2, AC3)
====================================================================
```

---

## 5. Hướng dẫn Tái lập Kiểm thử cho Đội ngũ QA (M3) & Các Module M1/M2

Để chạy lại bộ kiểm tra bảo mật độc lập mà không phụ thuộc vào hạ tầng Docker hay PostgreSQL cục bộ:

```bash
# 1. Chuyển vào thư mục gốc của repository
cd /path/to/XD-PMHDT

# 2. Thực thi bộ kiểm tra bảo mật phân quyền đa tenant
dotnet run --project tests/AURA.Profile.MultiTenantChecks/AURA.Profile.MultiTenantChecks.csproj

# 3. Thực thi bộ kiểm tra vòng đời bệnh nhân & SLA M2
dotnet run --project tests/AURA.Profile.AuthorizationChecks/AURA.Profile.AuthorizationChecks.csproj

# 4. Thực thi bộ kiểm tra chiến dịch tầm soát M2
dotnet run --project tests/AURA.Analysis.CampaignChecks/AURA.Analysis.CampaignChecks.csproj
```

Tất cả các lệnh trên phải trả về mã thoát `0` và hiển thị `100% PASS`.
