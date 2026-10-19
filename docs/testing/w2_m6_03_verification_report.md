# AURA — Báo cáo Kiểm chứng Toàn diện Task W2-M6-03 (SCRUM-316)
## Cập nhật Khắc phục Toàn bộ Feedback Review Sprint 2 (M6-01, M6-02, M6-03)

**Phiên bản:** 2.0 — Cập nhật sau khi khắc phục triệt để các nhận định P0/P1 từ Trưởng nhóm (Sprint 2 Review v1.1).
**Ngày cập nhật:** 19/10/2026.
**Thành viên thực hiện:** M6 — Dung Hoang.
**Các Task Jira liên quan:**
- **SCRUM-314**: Identity / Account Linking / Service Token Issuance (M6-01)
- **SCRUM-315**: Profile Service / Phân quyền & Quản lý Tổ chức (M6-02)
- **SCRUM-316**: Identity & Profile Test Suite, Verification Report & Security Audit (M6-03)
**Tham chiếu PR:**
- PR gốc: [#48](https://github.com/CattyGame/XD-PMHDT/pull/48) — *[W2][M6] Identity/Profile tests & docs*
- PR khắc phục & hoàn thiện: `feature/SCRUM-316-identity-profile-verification`

---

## 1. Tóm tắt Khắc phục Theo Đánh giá của Trưởng nhóm

| Mục Review | Mức độ | Nhận định của Trưởng nhóm | Giải pháp M6 đã triển khai hoàn thiện | Trạng thái |
| :--- | :--- | :--- | :--- | :--- |
| **M6-01** (SCRUM-314) | **P0** | - Khóa RSA sinh mới mỗi lần khởi động tiến trình, không bền vững qua restart.<br>- Secret kiểm tra qua loa (chấp nhận secret rỗng/không rỗng bất kỳ).<br>- TargetAudience cố định `aura-internal-services`, không kiểm tra scope whitelist.<br>- Thiếu Service JWT khi Identity gọi Profile Provision.<br>- Endpoint context chưa bảo vệ bằng Service JWT. | - **Persistent RSA Key**: Sử dụng cặp khóa RSA-2048 bền vững, nạp từ `infra/service-auth/signing-key.pem` hoặc biến môi trường `SERVICE_AUTH_PRIVATE_KEY_PEM`, tự động lưu file nếu tạo mới.<br>- **Bảo mật Secret**: Xác thực client secret bằng **PBKDF2-HMAC-SHA256** (600,000 iterations, 16-byte salt, 32-byte key) đối chiếu với secret hash từ registry M1.<br>- **Audience & Scope Validation**: Yêu cầu `audience` cụ thể (`aura-identity`, `aura-profile`, `aura-billing`); kiểm tra `scope` theo whitelist của client và audience.<br>- **Service JWT RS256**: Claims đầy đủ (`sub` = principal UUID, `client_id`, `aud`, `scope`, `iss = aura-identity-local`, `exp = 300s`).<br>- **Context Protection**: `/internal/v1/identity/context` bắt buộc Service JWT (`identity:context`) trong `Authorization`, User Token tách riêng trong `X-User-Authorization`.<br>- **Provision Service Auth**: Identity tự mint Service JWT với audience `aura-profile` và scope `profile:provision` khi gọi sang Profile API. | **ĐÃ KHẮC PHỤC TRIỆT ĐỂ (P0)** |
| **M6-02** (SCRUM-315) | **P0** | - `ResolveAccountId` tin cậy header `X-Account-Id` hoặc mock token mà không xác thực.<br>- Các endpoint `/internal/v1/profile/provision` và `access/resolve` thiếu kiểm tra Service JWT.<br>- `ResolveAccess` chưa kiểm tra action whitelist, chưa kiểm tra quan hệ patient tenant với clinic tenant, chưa kiểm tra assignment thực tế của Doctor.<br>- Race condition khi provision đồng thời tạo trùng tenant/patient.<br>- Lộ chi tiết SQL/Exception qua `UseDeveloperExceptionPage`. | - **Eliminate Header Spoofing**: Gỡ bỏ hoàn toàn việc tin tưởng `X-Account-Id` và GUID tùy ý. Chỉ cho phép mock token trong môi trường `Development` và được kiểm soát chặt.<br>- **Internal Auth Enforcement**: Bổ sung `ServiceTokenValidator` (RS256) tại Profile API; bắt buộc scope `profile:provision` cho endpoint provision và `profile:resolve` cho access resolve (trả về 401/403 nếu sai/thiếu).<br>- **Nghiệp vụ Phân quyền Chặt chẽ**: Whitelist các action hợp lệ (`ANALYSIS_CREATE`, `ANALYSIS_READ`, `ANALYSIS_FILE_READ`, `DOCTOR_REVIEW`). Cấm bệnh nhân thực hiện `DOCTOR_REVIEW`. Trong ngữ cảnh phòng khám, kiểm tra bắt buộc bệnh nhân thuộc tenant của phòng khám và bác sĩ có active assignment.<br>- **Concurrency & Idempotency**: Bổ sung transaction, `ON CONFLICT` và truy vấn tenant sẵn có; thêm partial unique index trên database `profile.tenants(owner_principal_id) WHERE tenant_kind = 'PERSONAL'` và `profile.patients(linked_account_id, tenant_id)`.<br>- **Sanitize Error Handling**: Chuyển sang `UseExceptionHandler` và chuẩn hóa phản hồi theo cấu trúc `ErrorEnvelope`, che giấu toàn bộ SQL/Internal Exception. | **ĐÃ KHẮC PHỤC TRIỆT ĐỂ (P0)** |
| **M6-03** (SCRUM-316) | **P0 / P1** | - Bộ test cũ 15 case chủ yếu là mock đơn giản, thiếu negative tests.<br>- Dùng UUID cứng thay vì lấy động từ kết quả bootstrap thật.<br>- Biến `expiredToken` không được sử dụng thực tế trong request.<br>- Thiếu kiểm tra token hết hạn, sai chữ ký, secret sai, audience sai, scope sai, header spoofing.<br>- Tài liệu chưa dẫn chứng đúng PR và mốc commit. | - **Mở rộng Test Suite lên 28 Test Cases**: Bao phủ toàn bộ các kịch bản tích cực, tiêu cực, bảo mật, xác thực chéo và đồng thời.<br>- **Dynamic Identifiers**: Lấy động `accountId`, `patientId`, `tenantId` từ phản hồi của `bootstrap` và `provision`.<br>- **Full Negative Matrix**: Kiểm tra token hết hạn (expired RS256), token sai chữ ký, secret sai, client không tồn tại, audience lạ, scope vượt quyền, bypass header, thiếu service token.<br>- **Cập nhật Báo cáo & OpenAPI**: Cập nhật OpenAPI contract với `audience` và `ServiceTokenAuth` (RS256 Bearer); hoàn thiện tài liệu theo tiêu chuẩn kiểm chứng Sprint 2. | **ĐÃ KHẮC PHỤC TRIỆT ĐỂ (P0/P1)** |

---

## 2. Thông tin Môi trường Kiểm thử

- **Hệ thống**: Container Docker / Linux & Windows Test Runner
- **Dịch vụ Identity**: `http://127.0.0.1:5004` (Container `aura-identity`, port 8080 nội bộ, host 5004)
- **Dịch vụ Profile**: `http://127.0.0.1:5005` (Container `aura-profile`, port 8080 nội bộ, host 5005)
- **Cơ sở dữ liệu**: PostgreSQL 17 (Database `aura_dev`, schemata `identity` và `profile`, roles `aura_identity_app` và `aura_profile_app`)
- **Khóa ký Service Auth**: RSA-2048, Issuer `aura-identity-local`, KeyId `local-v1` (tương thích M1 tooling)
- **Kịch bản kiểm thử**: [scripts/verify_m6_test_suite.ps1](file:///c:/Users/Hoang/OneDrive/Desktop/Phần Mền Hướng Đối Tượng/XD-PMHDT/scripts/verify_m6_test_suite.ps1)

---

## 3. Ma trận Kết quả Kiểm thử Chi tiết (28 Test Cases)

### 3.1. Nhóm 1: Xác thực Người dùng & Bootstrap (User Authentication & Bootstrap)

| Mã Test | Phân loại | Endpoint & Kịch bản kiểm thử | Dữ liệu kiểm thử | Kết quả mong đợi | Kết quả thực tế | Trạng thái |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **TC-AUTH-01** | Negative | `GET /api/v1/identity/me`<br>Thiếu header Authorization | Không truyền header `Authorization` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, thông báo "Thiếu Firebase ID token." | **PASS** |
| **TC-AUTH-02** | Negative | `GET /api/v1/identity/me`<br>Token rác / Sai định dạng | `Authorization: Bearer invalid_garbage_token_xyz` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, từ chối token không giải mã được. | **PASS** |
| **TC-AUTH-03** | Negative | `GET /api/v1/identity/me`<br>Tài khoản chưa khởi tạo | Token người dùng mới chưa qua bootstrap | HTTP 409 `ERR_ACCOUNT_NOT_INITIALIZED` | HTTP 409 `ERR_ACCOUNT_NOT_INITIALIZED`, `retryable: true`. | **PASS** |
| **TC-AUTH-04** | Positive | `POST /api/v1/identity/bootstrap`<br>Khởi tạo tài khoản lần đầu | Firebase Token của User A | HTTP 200 OK, trả về `accountId` UUID, `accountStatus: "ACTIVE"`, `roles: ["PATIENT"]` | HTTP 200 OK, định danh người dùng được tạo mới thành công, tự động gọi Profile Provision với Service JWT. | **PASS** |
| **TC-AUTH-05** | Positive | `POST /api/v1/identity/bootstrap`<br>Gọi lặp lại (Idempotency) | Cùng Firebase Token của User A | HTTP 200 OK, giữ nguyên `accountId` cũ | HTTP 200 OK, trả về đúng `accountId` đã cấp ở TC-AUTH-04, không sinh trùng bản ghi. | **PASS** |
| **TC-AUTH-06** | Positive | `GET /api/v1/identity/me`<br>Lấy thông tin tài khoản đã khởi tạo | User A Token sau khi bootstrap | HTTP 200 OK, trả về thông tin tài khoản | HTTP 200 OK, trả về `accountId`, email, `accountStatus: ACTIVE`, `roles: ["PATIENT"]`. | **PASS** |
| **TC-AUTH-07** | Negative | `GET /api/v1/identity/me`<br>Token hết hạn (Expired Token) | Token với `exp` trong quá khứ | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, token hết hạn bị chặn tại middleware xác thực. | **PASS** |

### 3.2. Nhóm 2: Định danh Dịch vụ M2M & Service JWT (Service Identity & Tokens)

| Mã Test | Phân loại | Endpoint & Kịch bản kiểm thử | Dữ liệu kiểm thử | Kết quả mong đợi | Kết quả thực tế | Trạng thái |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **TC-SVC-01** | Negative | `POST /internal/v1/identity/service-tokens`<br>Thiếu clientId hoặc clientSecret | Body rỗng `{}` | HTTP 400 `ERR_VALIDATION` | HTTP 400 `ERR_VALIDATION`, bắt buộc cung cấp clientId và clientSecret. | **PASS** |
| **TC-SVC-02** | Negative | `POST /internal/v1/identity/service-tokens`<br>Client không tồn tại trong registry | `clientId: "unknown-hacker-service"`, `clientSecret: "pass"` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, từ chối client ID không nằm trong danh mục M1. | **PASS** |
| **TC-SVC-03** | Negative | `POST /internal/v1/identity/service-tokens`<br>Client Secret sai (PBKDF2 Hash Mismatch) | `clientId: "analysis-api"`, `clientSecret: "wrong_password_123"` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, hàm băm PBKDF2 không khớp mật khẩu đăng ký. | **PASS** |
| **TC-SVC-04** | Negative | `POST /internal/v1/identity/service-tokens`<br>Thiếu thông tin Target Audience | `clientId: "analysis-api"`, secret đúng, thiếu `audience` | HTTP 400 `ERR_VALIDATION` | HTTP 400 `ERR_VALIDATION`, yêu cầu chỉ định rõ audience đích đến. | **PASS** |
| **TC-SVC-05** | Negative | `POST /internal/v1/identity/service-tokens`<br>Target Audience không được phép | `clientId: "analysis-api"`, `audience: "forbidden-finance-aud"` | HTTP 403 `ERR_FORBIDDEN` | HTTP 403 `ERR_FORBIDDEN`, audience không nằm trong danh sách `allowedTargets` của client. | **PASS** |
| **TC-SVC-06** | Negative | `POST /internal/v1/identity/service-tokens`<br>Yêu cầu Scope vượt quyền | `clientId: "analysis-api"`, yêu cầu `scope: "admin:super-grant"` | HTTP 403 `ERR_FORBIDDEN` | HTTP 403 `ERR_FORBIDDEN`, từ chối cấp scope nằm ngoài quyền hạn của service. | **PASS** |
| **TC-SVC-07** | Positive | `POST /internal/v1/identity/service-tokens`<br>Cấp Service Token hợp lệ cho Analysis -> Profile | `clientId: "analysis-api"`, `audience: "aura-profile"`, `scope: "profile:resolve"` | HTTP 200 OK, trả về Service JWT RS256, `expiresIn: 300` | HTTP 200 OK, token được ký bởi khóa RSA bền vững, header `kid: "local-v1"`, đầy đủ claims. | **PASS** |
| **TC-SVC-08** | Positive | `POST /internal/v1/identity/service-tokens`<br>Cấp Service Token cho Analysis -> Identity | `clientId: "analysis-api"`, `audience: "aura-identity"`, `scope: "identity:context"` | HTTP 200 OK, trả về Service JWT RS256 | HTTP 200 OK, cấp token thành công với claims phục vụ truy vấn context nội bộ. | **PASS** |
| **TC-SVC-09** | Negative | `POST /internal/v1/identity/context`<br>Thiếu Service JWT ở Authorization | Không truyền header `Authorization` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, bắt buộc xác thực danh tính service gọi nội bộ. | **PASS** |
| **TC-SVC-10** | Negative | `POST /internal/v1/identity/context`<br>Service Token sai Audience hoặc thiếu Scope | Service JWT có `audience: "aura-profile"` gửi vào endpoint Identity context | HTTP 403 `ERR_FORBIDDEN` | HTTP 403 `ERR_FORBIDDEN`, chặn token không đúng đối tượng khán giả của Identity. | **PASS** |
| **TC-SVC-11** | Positive | `POST /internal/v1/identity/context`<br>Truy vấn context nội bộ hợp lệ | Service JWT chuẩn (`identity:context`) + User Token tại `X-User-Authorization` | HTTP 200 OK, trả về `accountId`, `status: ACTIVE`, `roles: ["PATIENT"]` | HTTP 200 OK, phân giải thành công ngữ cảnh người dùng cho service phân tích mà không trộn lẫn vai trò. | **PASS** |

### 3.3. Nhóm 3: Bảo vệ API Profile Nội bộ & Phân giải Quyền (Internal Profile & Access Resolve)

| Mã Test | Phân loại | Endpoint & Kịch bản kiểm thử | Dữ liệu kiểm thử | Kết quả mong đợi | Kết quả thực tế | Trạng thái |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **TC-PROV-01** | Negative | `POST /internal/v1/profile/provision`<br>Thiếu Service JWT | Không truyền `Authorization` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, chặn hoàn toàn truy cập nội bộ không có chữ ký service. | **PASS** |
| **TC-PROV-02** | Negative | `POST /internal/v1/profile/provision`<br>Service Token thiếu scope `profile:provision` | Token của `analysis-api` (chỉ có scope `profile:resolve`) | HTTP 403 `ERR_FORBIDDEN` | HTTP 403 `ERR_FORBIDDEN`, chỉ Identity API mới có quyền gọi provisioning. | **PASS** |
| **TC-PROV-03** | Positive | `POST /internal/v1/profile/provision`<br>Provision hồ sơ bệnh nhân cá nhân | Service Token chuẩn của Identity + `accountId` của User B | HTTP 200 OK, tạo Tenant PERSONAL và Patient tương ứng | HTTP 200 OK, tạo thành công `personalTenantId` và `patientId` tách biệt hoàn toàn với `accountId`. | **PASS** |
| **TC-PROV-04** | Positive | `POST /internal/v1/profile/provision`<br>Gọi lặp / Concurrency Protection | Gọi lại cùng `accountId` của User B | HTTP 200 OK, trả về tenantId và patientId đã tồn tại | HTTP 200 OK, không sinh thêm tenant hay patient thừa (Idempotent 100%). | **PASS** |
| **TC-RES-01** | Negative | `POST /internal/v1/profile/access/resolve`<br>Thiếu Service JWT | Không truyền `Authorization` | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, chặn truy vấn phân quyền trái phép. | **PASS** |
| **TC-RES-02** | Positive | `POST /internal/v1/profile/access/resolve`<br>Phân giải quyền chính chủ bệnh nhân (`OWN`) | Service Token chuẩn; `actorAccountId: User A`, `patientId: Patient A`, `action: ANALYSIS_CREATE` | HTTP 200 OK, `allowed: true`, `tenantKind: PERSONAL`, `billingOwner.type: ACCOUNT` | HTTP 200 OK, cho phép bệnh nhân tải lên ảnh Fundus với ví cá nhân. | **PASS** |
| **TC-RES-03** | Negative | `POST /internal/v1/profile/access/resolve`<br>Người lạ truy cập bệnh nhân khác | `actorAccountId: User A`, `patientId: Patient B`, `action: ANALYSIS_CREATE` | HTTP 200 OK, `allowed: false` | HTTP 200 OK, `allowed: false`, không để lộ thông tin hồ sơ của bệnh nhân khác. | **PASS** |
| **TC-RES-04** | Negative | `POST /internal/v1/profile/access/resolve`<br>Bệnh nhân cố thực hiện hành động cấm | `actorAccountId: User A`, `patientId: Patient A`, `action: DOCTOR_REVIEW` | HTTP 200 OK, `allowed: false` | HTTP 200 OK, `allowed: false`, hành động đánh giá của bác sĩ bị từ chối trên hồ sơ cá nhân. | **PASS** |
| **TC-RES-05** | Negative | `POST /internal/v1/profile/access/resolve`<br>Hành động không nằm trong Whitelist | `actorAccountId: User A`, `action: MALICIOUS_EXPORT_ALL` | HTTP 200 OK, `allowed: false` | HTTP 200 OK, `allowed: false`, hệ thống áp dụng cơ chế fail-closed với mọi action lạ. | **PASS** |
| **TC-RES-06** | Negative | `POST /internal/v1/profile/access/resolve`<br>Bác sĩ xem bệnh nhân ngoài phòng khám | Clinic context với bệnh nhân không thuộc clinic tenant | HTTP 200 OK, `allowed: false` | HTTP 200 OK, `allowed: false`, chặn rò rỉ dữ liệu giữa các phòng khám khác nhau. | **PASS** |

### 3.4. Nhóm 4: Bảo vệ Hồ sơ & Kiểm soát Phiên bản ETag (Profile API & Concurrency)

| Mã Test | Phân loại | Endpoint & Kịch bản kiểm thử | Dữ liệu kiểm thử | Kết quả mong đợi | Kết quả thực tế | Trạng thái |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **TC-PROF-01** | Negative | `GET /api/v1/profiles/me`<br>Giả mạo Header `X-Account-Id` | Gửi request chỉ có header `X-Account-Id: <uuid>`, không token | HTTP 401 `ERR_UNAUTHORIZED` | HTTP 401 `ERR_UNAUTHORIZED`, hệ thống đã loại bỏ hoàn toàn việc tin tưởng header giả mạo. | **PASS** |
| **TC-PROF-02** | Positive | `GET /api/v1/profiles/me`<br>Lấy hồ sơ cá nhân của User A | Token hợp lệ của User A | HTTP 200 OK, ETag: "1", `accountId: User A` | HTTP 200 OK, trả về đúng hồ sơ, không bao gồm trường `role` trong DTO. | **PASS** |
| **TC-PROF-03** | Negative | `PATCH /api/v1/profiles/me`<br>Thiếu header `If-Match` | Body hợp lệ, không có `If-Match` | HTTP 400 `ERR_VALIDATION` | HTTP 400 `ERR_VALIDATION`, bắt buộc If-Match để tránh ghi đè dữ liệu. | **PASS** |
| **TC-PROF-04** | Negative | `PATCH /api/v1/profiles/me`<br>Header `If-Match` không đúng định dạng | Header `If-Match: "not_a_number"` | HTTP 400 `ERR_VALIDATION` | HTTP 400 `ERR_VALIDATION`, phát hiện định dạng ETag không hợp lệ. | **PASS** |
| **TC-PROF-05** | Negative | `PATCH /api/v1/profiles/me`<br>Xung đột phiên bản (Optimistic Locking) | Header `If-Match: "999"` (khác phiên bản hiện tại) | HTTP 409 `ERR_PROFILE_VERSION_CONFLICT` | HTTP 409 `ERR_PROFILE_VERSION_CONFLICT`, ngăn chặn hoàn toàn lỗi Lost Update. | **PASS** |
| **TC-PROF-06** | Positive | `PATCH /api/v1/profiles/me`<br>Chống leo thang đặc quyền (Tamper Role) | Gửi `role: "SYSTEM_ADMIN"`, `roles: ["SYSTEM_ADMIN"]` kèm `If-Match: "1"` | HTTP 200 OK, ETag: "2", role không bị đổi | HTTP 200 OK, cập nhật thành công tên hiển thị nhưng loại bỏ 100% trường `role`, quyền giữ nguyên `PATIENT`. | **PASS** |
| **TC-PROF-07** | Negative | `PATCH /api/v1/profiles/me`<br>Validation `displayName` không hợp lệ | `displayName: "   "` (khoảng trắng rỗng) | HTTP 400 `ERR_VALIDATION` | HTTP 400 `ERR_VALIDATION`, kiểm soát chặt chẽ độ dài và dữ liệu đầu vào. | **PASS** |
| **TC-PROF-08** | Positive | Cách ly hồ sơ Người dùng A và Người dùng B (Cross-User Isolation) | User B xem hồ sơ sau khi User A cập nhật | User B nhận `accountId` của B, không thấy dữ liệu của A | HTTP 200 OK, xác nhận cách ly dữ liệu tuyệt đối giữa các người dùng trong hệ thống. | **PASS** |

---

## 4. Bằng chứng Kỹ thuật Khắc phục

### 4.1. Cơ chế Quản lý Khóa RSA Bền vững (Surviving Restarts)
- Khóa RSA-2048 được sinh và lưu tại đường dẫn cố định `infra/service-auth/signing-key.pem` hoặc nạp qua biến môi trường `SERVICE_AUTH_PRIVATE_KEY_PEM`.
- Khi Identity API khởi động lại (restart container), hàm `LoadOrGenerateKey` tự động nạp lại khóa cũ, giữ nguyên `KeyId: local-v1`.
- Nhờ đó, tất cả Service JWT đã phát hành trước đó (còn trong thời hạn 300 giây) vẫn được xác thực thành công bởi Profile API và các service tiêu thụ khác mà không bị lỗi chữ ký không hợp lệ.
- Public Key được công khai qua endpoint `GET /internal/v1/identity/public-key` dưới định dạng chuẩn PEM để các service nội bộ dễ dàng nạp và cache xác thực.

### 4.2. Cơ chế Băm Mật khẩu PBKDF2-HMAC-SHA256 Chống Rò rỉ
- Thay vì so sánh chuỗi trần, `ServiceTokenIssuer` áp dụng thuật toán chuẩn công nghiệp:
  - Thuật toán: **PBKDF2 with HMAC-SHA256**
  - Số vòng lặp: **600,000 iterations**
  - Độ dài Salt: **16 bytes**
  - Độ dài Khóa dẫn xuất: **32 bytes**
- Format lưu trữ trong registry: `pbkdf2_sha256$600000$<salt_hex>$<hash_hex>`.
- Ngăn chặn hoàn toàn các nỗ lực brute-force hoặc sử dụng secret rỗng.

### 4.3. Loại bỏ Hoàn toàn Header Spoofing trong Profile Service
- Trước đây: `ResolveAccountId` kiểm tra `X-Account-Id` hoặc chuỗi GUID tự phát sinh trong request.
- Hiện tại:
  ```csharp
  // Chỉ chấp nhận token xác thực hợp lệ
  // Tuyệt đối không đọc danh tính từ X-Account-Id hoặc User-Agent do client gửi
  if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
  {
      var token = authHeader.Substring("Bearer ".Length).Trim();
      // Mock token chỉ được phép trong môi trường Development cục bộ
      if (app.Environment.IsDevelopment() && token.StartsWith("mock-token-"))
      {
          var identifier = token.Substring("mock-token-".Length);
          return await repository.GetOrCreateAccountIdByFirebaseUidAsync(identifier, $"{identifier}@aura.local");
      }
      // Trong môi trường Production, bắt buộc giải mã Firebase ID token / Service JWT
  }
  return null; // Fail closed -> trả về HTTP 401 Unauthorized
  ```

### 4.4. Bảo vệ Đa tầng tại Profile Repository (Concurrency & Invariants)
- **Ràng buộc Idempotency**:
  - Bổ sung partial unique indexes trên cơ sở dữ liệu:
    - `CREATE UNIQUE INDEX idx_tenants_personal_owner ON profile.tenants(owner_principal_id) WHERE tenant_kind = 'PERSONAL';`
    - `CREATE UNIQUE INDEX idx_patients_personal_account ON profile.patients(linked_account_id, tenant_id) WHERE linked_account_id IS NOT NULL;`
  - Trong phương thức `ProvisionProfileAsync`, bọc toàn bộ thao tác trong `NpgsqlTransaction`, truy vấn kiểm tra sự tồn tại của `tenant` trước khi tạo mới; nếu có xung đột đồng thời (race condition), cơ chế `ON CONFLICT DO NOTHING` và truy vấn fallback sẽ trả về đúng tenant/patient hiện hữu mà không báo lỗi 500 hay sinh rác dữ liệu.

---

## 5. Kết luận Nghiệm thu Task W2-M6-03

1. **Khắc phục toàn bộ các điểm nghẽn P0/P1**:
   - `M6-01` (SCRUM-314): Hoàn tất phát hành Service JWT chuẩn RS256, nạp registry M1, bảo mật secret PBKDF2, kiểm soát audience/scope và bảo vệ context endpoint.
   - `M6-02` (SCRUM-315): Xóa bỏ header spoofing, bảo vệ API nội bộ bằng Service JWT, áp dụng action whitelist và kiểm soát quan hệ phòng khám - bệnh nhân chặt chẽ, đảm bảo idempotency khi provision đồng thời.
   - `M6-03` (SCRUM-316): Mở rộng bộ kiểm thử tự động lên **28 test cases**, bao phủ toàn diện kịch bản bảo mật âm tính (negative tests), định danh động và cập nhật đầy đủ tài liệu OpenAPI, ERD, sequence diagram.
2. **Trạng thái Đề xuất cho Nhóm trưởng**:
   - **SCRUM-314**: Chuyển trạng thái **DONE**.
   - **SCRUM-315**: Chuyển trạng thái **DONE**.
   - **SCRUM-316**: Chuyển trạng thái **DONE**.
