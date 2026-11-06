# THIẾT KẾ NGHIỆP VỤ & PHÂN QUYỀN ĐA KHÁCH THUÊ PHÂN HỆ CHIẾN DỊCH TẦM SOÁT (SCREENING CAMPAIGN)

> **Mã task**: `W5-M6-03` · **Jira**: `SCRUM-379`
> **Người thực hiện**: M6 (Security, Auth, Identity & QA Lead)
> **Sprint**: Sprint 5 (02/11/2026 – 08/11/2026) · **Dự án**: AURA (`SP26SE025`)
> **Chuẩn kiến trúc**: Microservices, Multi-tenant Isolation, ADR-005 D2 Authoritative Resolution, Zero Trust.

---

## 1. Tổng Quan Nghiệp Vụ & Mô Hình Thực Thể Đa Khách Thuê

Trong các đợt khám sàng lọc sức khỏe mắt cộng đồng (ví dụ: tầm soát bệnh võng mạc đái tháo đường, tăng huyết áp võng mạc cho người cao tuổi tại địa phương), các phòng khám hoặc bệnh viện đối tác (Clinics) tổ chức các **Chiến dịch Tầm soát (Screening Campaigns)**. Mỗi chiến dịch đại diện cho một đợt khám lưu động có mục tiêu, bác sĩ phụ trách, địa điểm, thời gian và chỉ tiêu số lượng bệnh nhân.

### 1.1. Mối Quan Hệ Giữa Các Thực Thể Nghiệp Vụ

```mermaid
erDiagram
    CLINIC ||--o{ MEMBERSHIP : "quản lý"
    CLINIC ||--o{ CAMPAIGN : "sở hữu (tenant_id)"
    CLINIC ||--o{ PATIENT : "đăng ký khám"
    CLINIC ||--o{ BATCH : "tiếp nhận"

    ACCOUNT ||--o| MEMBERSHIP : "liên kết"
    MEMBERSHIP ||--o{ DOCTOR_ASSIGNMENT : "bác sĩ thực hiện"

    CAMPAIGN ||--o{ BATCH : "tập hợp các đợt chụp"
    CAMPAIGN }o--|| ACCOUNT : "bác sĩ phụ trách (doctor_in_charge_id)"

    BATCH ||--o{ ANALYSIS : "chứa các ca chụp"
    PATIENT ||--o{ ANALYSIS : "hồ sơ mắt bệnh nhân"

    CLINIC {
        uuid clinic_id PK
        uuid tenant_id UK
        string name
        string status
    }

    CAMPAIGN {
        uuid id PK
        uuid tenant_id FK
        string name
        string location
        uuid doctor_in_charge_id FK
        int target_patient_count
        string status "PLANNED | IN_PROGRESS | COMPLETED | CLOSED"
        timestamp start_date
        timestamp end_date
    }

    BATCH {
        uuid id PK
        uuid tenant_id FK
        uuid campaign_id FK "nullable"
        int total_images
        int completed_images
        int failed_images
        string status
    }

    PATIENT {
        uuid patient_id PK
        uuid tenant_id FK
        string patient_type "CLINIC_MANAGED | PERSONAL"
    }
```

* **Clinic (Phòng khám / Tổ chức Y tế)**: Khách thuê độc lập (**Tenant**). Sở hữu dữ liệu chiến dịch, danh sách bệnh nhân và hạn ngạch tín dụng (Credit Wallet).
* **Doctor / Staff (Bác sĩ / Nhân viên)**: Là tài khoản người dùng (`identity.accounts`) được cấp quyền thông qua liên kết thành viên (`profile.memberships`). Bác sĩ chỉ có quyền truy cập dữ liệu của phòng khám mà họ đang có tư cách thành viên `ACTIVE`.
* **Campaign (Chiến dịch Tầm soát)**: Thuộc về **duy nhất một phòng khám** (`tenant_id = clinic.tenant_id`). Mọi truy vấn đọc, sửa, thêm batch, xuất báo cáo bắt buộc phải kiểm tra ranh giới `tenant_id`.
* **Batch (Đợt ảnh tải lên)**: Một batch gồm nhiều ảnh ($\ge 100$ ảnh) được liên kết vào một Campaign nếu cùng thuộc một Clinic tenant. Cấm tuyệt đối liên kết chéo batch của phòng khám này vào chiến dịch của phòng khám khác.
* **Patient & Analysis (Bệnh nhân & Ca phân tích)**: Bệnh nhân tham gia chiến dịch được phân loại là `CLINIC_MANAGED` dưới sự quản lý của phòng khám đó.

---

## 2. Nguyên Tắc Bảo Mật & Ranh Giới Đa Khách Thuê (Tenant Boundary)

### 2.1. Quyết Định Kiến Trúc ADR-005 D2 (Authoritative Context Resolution)

* **Cấm tin cậy thông tin do Client tự khai báo**: API Gateway và Analysis API **tuyệt đối không tin cậy** các header như `X-Clinic-Context-Id` hoặc claim tự tạo trong client request.
* **Phân giải thẩm quyền qua Profile Service**:
  Mọi request tới Campaign API bắt buộc truyền query param `clinicContextId={clinicId}`. Analysis API trích xuất tài khoản từ Bearer token, sau đó gọi sang dịch vụ Profile Service qua endpoint nội bộ an toàn:
  `POST /internal/v1/profile/clinic-access/resolve`
  kèm chữ ký Service JWT (scope `profile:resolve`).
  Profile Service sẽ truy vấn database trung tâm kiểm tra:
  1. Tài khoản có trạng thái `ACTIVE` trong `identity.accounts` không?
  2. Tài khoản có tư cách thành viên `ACTIVE` tại phòng khám `clinicId` không?
  3. Phòng khám có trạng thái `ACTIVE` không?
  4. Vai trò của tài khoản có đủ thẩm quyền thực hiện thao tác tương ứng (`CAMPAIGN_MANAGE`, `CAMPAIGN_READ`, `CAMPAIGN_EXPORT`) không?
* **Fail-Closed**: Nếu Profile Service gặp sự cố hoặc token không thể xác minh, hệ thống trả về ngay `HTTP 503` hoặc `HTTP 401/403`, tuyệt đối không có cơ chế bypass hoặc cho phép mặc định.

### 2.2. Nguyên Tắc Chống Dò Quét Tài Nguyên (Anti-Probing Principle)

* Khi người dùng thuộc Clinic B gửi request truy cập chi tiết chiến dịch, gắn batch, xem thống kê hoặc xuất báo cáo của Campaign thuộc Clinic A (bằng cách truyền đúng `clinicContextId` của Clinic B nhưng nhắm vào UUID của Campaign Clinic A):
  Analysis Repository thực hiện truy vấn:

  ```sql
  SELECT ... FROM analysis.campaigns WHERE tenant_id = @TenantClinicB AND id = @CampaignClinicA;
  ```

* Do không tồn tại bản ghi khớp cả `tenant_id` và `id`, hệ thống phản hồi:
  `HTTP 404 Not Found (ERR_NOT_FOUND)`
* **Lý do y tế & bảo mật**: Trả về `404 Not Found` thay vì `403 Forbidden` giúp ngăn chặn kẻ tấn công biết được UUID của Campaign phòng khám khác có thực sự tồn tại trong hệ thống hay không (Anti-Enumeration & Anti-Probing).

### 2.3. Ràng Buộc Vòng Đời Chiến Dịch (Campaign Lifecycle & Closed Protection)

* Chiến dịch có 4 trạng thái tuần tự: `PLANNED` $\rightarrow$ `IN_PROGRESS` $\rightarrow$ `COMPLETED` $\rightarrow$ `CLOSED`.
* **Khóa bất biến khi CLOSED**: Khi chiến dịch đã được chốt sổ (`CLOSED`), cấm tuyệt đối việc thêm ảnh hoặc liên kết thêm batch mới. Bất kỳ nỗ lực nào đều nhận lỗi:
  `HTTP 409 Conflict (ERR_CAMPAIGN_CLOSED)`.
* Dữ liệu chiến dịch đã CLOSED vẫn cho phép đọc (`GET`) và xuất CSV (`GET /export`) phục vụ lưu trữ y khoa và thanh tra kiểm toán.

---

## 3. Ma Trận Phân Quyền (RBAC & Multi-Tenant Authority Matrix)

| Vai trò người gọi | Ngữ cảnh phòng khám | Thao tác (`Action`) | Endpoint | Quyết định | HTTP Response |
| --- | --- | --- | --- | --- | --- |
| **CLINIC_ADMIN** | Clinic của mình (Active) | Tạo chiến dịch mới (`CAMPAIGN_MANAGE`) | `POST /api/v1/campaigns` | **ALLOW** | `201 Created` |
| **CLINIC_ADMIN** | Clinic của mình (Active) | Đổi trạng thái (`CAMPAIGN_MANAGE`) | `PATCH /api/v1/campaigns/{id}/status` | **ALLOW** | `200 OK` |
| **CLINIC_ADMIN** | Clinic của mình (Active) | Gắn batch hợp lệ (`CAMPAIGN_MANAGE`) | `POST /api/v1/campaigns/{id}/batches` | **ALLOW** | `200 OK` |
| **CLINIC_ADMIN** | Clinic của mình (Active) | Xuất báo cáo CSV (`CAMPAIGN_EXPORT`) | `GET /api/v1/campaigns/{id}/export` | **ALLOW** | `200 OK (CSV)` |
| **DOCTOR** | Clinic của mình (Active) | Xem danh sách / chi tiết (`CAMPAIGN_READ`) | `GET /api/v1/campaigns/{id}` | **ALLOW** | `200 OK` |
| **DOCTOR** | Clinic của mình (Active) | Xem tóm tắt thống kê (`CAMPAIGN_READ`) | `GET /api/v1/campaigns/{id}/summary` | **ALLOW** | `200 OK` |
| **DOCTOR** | Clinic của mình (Active) | Cố tình tạo / đóng campaign (`CAMPAIGN_MANAGE`) | `POST /api/v1/campaigns` | **DENY** (ROLE_DENIED) | `403 Forbidden` |
| **DOCTOR** | Clinic của mình (Active) | Cố tình xuất báo cáo CSV (`CAMPAIGN_EXPORT`) | `GET /api/v1/campaigns/{id}/export` | **DENY** (ROLE_DENIED) | `403 Forbidden` |
| **Bất kỳ vai trò Clinic B** | Khai báo `clinicContextId` Clinic A | Mọi thao tác | Bất kỳ endpoint nào | **DENY** (NOT_A_MEMBER) | `403 Forbidden` |
| **Bất kỳ vai trò Clinic B** | Khai báo `clinicContextId` Clinic B | Nhắm vào `campaignId` của Clinic A | Bất kỳ endpoint nào | **DENY** (WHERE tenant_id) | `404 Not Found` |
| **CLINIC_ADMIN Clinic A** | Clinic A | Gắn Batch của Clinic B | `POST /api/v1/campaigns/{id}/batches` | **DENY** (WHERE tenant_id) | `404 Not Found` |
| **CLINIC_ADMIN Clinic A** | Clinic A | Gắn Batch vào Campaign đã CLOSED | `POST /api/v1/campaigns/{id}/batches` | **DENY** (STATUS_CLOSED) | `409 Conflict` |
| **Bệnh nhân (PATIENT)** | Bất kỳ | Mọi thao tác Campaign | Bất kỳ endpoint nào | **DENY** (NOT_A_MEMBER) | `403 Forbidden` |
| **Chưa xác thực** | Không có token | Mọi thao tác | Bất kỳ endpoint nào | **DENY** (UNAUTHORIZED) | `401 Unauthorized` |

---

## 4. Các Sơ Đồ Tuần Tự Chi Tiết (Sequence Diagrams)

### 4.1. Sequence 1: Luồng Hợp Lệ (Positive Control - Clinic Admin & Doctor)

```mermaid
sequenceDiagram
    autonumber
    actor AdminA as Admin Clinic A
    actor DoctorA as Bác sĩ Clinic A
    participant Gateway as API Gateway (YARP)
    participant Analysis as Analysis API
    participant Profile as Profile API
    participant DB as PostgreSQL (analysis schema)

    Note over AdminA, DB: 1. Tạo chiến dịch tầm soát mới (Positive Control)
    AdminA->>Gateway: POST /api/v1/campaigns?clinicContextId={ClinicA}<br/>Authorization: Bearer token_admin_a
    Gateway->>Analysis: Reverse Proxy chuyển tiếp request
    Analysis->>Profile: POST /internal/v1/profile/clinic-access/resolve<br/>(Actor: AdminA, Clinic: ClinicA, Action: CAMPAIGN_MANAGE)
    Profile-->>Analysis: Decision: { Allowed: true, TenantId: TenantA, Role: "CLINIC_ADMIN" }
    Analysis->>DB: INSERT INTO analysis.campaigns (tenant_id = TenantA, status = 'PLANNED')
    DB-->>Analysis: Record Created (ID: CampaignA1)
    Analysis-->>Gateway: HTTP 201 Created { id: CampaignA1, status: "PLANNED" }
    Gateway-->>AdminA: HTTP 201 Created

    Note over AdminA, DB: 2. Kích hoạt chiến dịch & Gắn batch ảnh hợp lệ của Clinic A
    AdminA->>Gateway: PATCH /api/v1/campaigns/{CampaignA1}/status?clinicContextId={ClinicA} (status = IN_PROGRESS)
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Profile: Resolve Clinic Access -> Allowed: true, TenantId: TenantA
    Analysis->>DB: UPDATE analysis.campaigns SET status = 'IN_PROGRESS' WHERE tenant_id = TenantA AND id = CampaignA1
    DB-->>Analysis: OK
    Analysis-->>Gateway: HTTP 200 OK { status: "IN_PROGRESS" }
    Gateway-->>AdminA: HTTP 200 OK

    AdminA->>Gateway: POST /api/v1/campaigns/{CampaignA1}/batches?clinicContextId={ClinicA} { batchId: BatchA1 }
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Profile: Resolve Clinic Access -> Allowed: true
    Analysis->>DB: Check Campaign status != 'CLOSED' & Check Batch belongs to TenantA
    Analysis->>DB: UPDATE analysis.batches SET campaign_id = CampaignA1 WHERE tenant_id = TenantA AND id = BatchA1
    DB-->>Analysis: Updated 1 row
    Analysis-->>Gateway: HTTP 200 OK { message: "Liên kết batch thành công." }
    Gateway-->>AdminA: HTTP 200 OK

    Note over DoctorA, DB: 3. Bác sĩ Clinic A theo dõi tiến độ & số liệu lâm sàng
    DoctorA->>Gateway: GET /api/v1/campaigns/{CampaignA1}/summary?clinicContextId={ClinicA}<br/>Authorization: Bearer token_doctor_a
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Profile: POST /internal/v1/profile/clinic-access/resolve (Action: CAMPAIGN_READ)
    Profile-->>Analysis: Decision: { Allowed: true, TenantId: TenantA, Role: "DOCTOR" }
    Analysis->>DB: Aggregate batches & analyses (Total, Completed, Normal, HighRisk)
    DB-->>Analysis: Aggregated statistics
    Analysis-->>Gateway: HTTP 200 OK { completionRatePercent: 100%, highRiskCases: 4 }
    Gateway-->>DoctorA: HTTP 200 OK
```

---

### 4.2. Sequence 2: Chặn Giả Mạo Ngữ Cảnh Chéo Tenant (Negative Control - HTTP 403 Forbidden)

```mermaid
sequenceDiagram
    autonumber
    actor AdminB as Admin Clinic B (Attacker)
    participant Gateway as API Gateway (YARP)
    participant Analysis as Analysis API
    participant Profile as Profile API

    Note over AdminB, Profile: Admin Clinic B cố tình dùng token của mình nhưng khai báo clinicContextId của Clinic A
    AdminB->>Gateway: GET /api/v1/campaigns?clinicContextId={ClinicA_Id}<br/>Authorization: Bearer token_admin_b
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Analysis: Trích xuất AccountId từ Bearer Token -> AccountB
    Analysis->>Profile: POST /internal/v1/profile/clinic-access/resolve<br/>(Actor: AccountB, Clinic: ClinicA_Id, Action: CAMPAIGN_READ)
    Profile->>Profile: Kiểm tra bảng profile.memberships cho ClinicA_Id
    Profile-->>Analysis: Decision: { Allowed: false, Reason: "NOT_A_MEMBER" }
    Analysis->>Analysis: Fail-closed: từ chối ngay lập tức
    Analysis-->>Gateway: HTTP 403 Forbidden { code: "ERR_FORBIDDEN", message: "Không có quyền truy cập vào phòng khám này." }
    Gateway-->>AdminB: HTTP 403 Forbidden (Bảo toàn ranh giới Clinic A)
```

---

### 4.3. Sequence 3: Chặn Dò Quét Tài Nguyên Chéo Tenant (Negative Control - HTTP 404 Not Found)

```mermaid
sequenceDiagram
    autonumber
    actor DoctorB as Bác sĩ Clinic B (Attacker)
    participant Gateway as API Gateway (YARP)
    participant Analysis as Analysis API
    participant Profile as Profile API
    participant DB as PostgreSQL (analysis schema)

    Note over DoctorB, DB: Bác sĩ Clinic B dùng ngữ cảnh hợp lệ của Clinic B nhưng nhắm vào Campaign UUID của Clinic A
    DoctorB->>Gateway: GET /api/v1/campaigns/{CampaignA_Id}?clinicContextId={ClinicB_Id}<br/>Authorization: Bearer token_doctor_b
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Profile: POST /internal/v1/profile/clinic-access/resolve<br/>(Actor: DoctorB, Clinic: ClinicB_Id, Action: CAMPAIGN_READ)
    Profile-->>Analysis: Decision: { Allowed: true, TenantId: TenantB, Role: "DOCTOR" }
    Analysis->>DB: SELECT * FROM analysis.campaigns WHERE tenant_id = TenantB AND id = CampaignA_Id
    DB-->>Analysis: 0 rows returned (Campaign thuộc TenantA)
    Analysis->>Analysis: Áp dụng Anti-probing: không tìm thấy trong TenantB
    Analysis-->>Gateway: HTTP 404 Not Found { code: "ERR_NOT_FOUND", message: "Không tìm thấy chiến dịch tầm soát." }
    Gateway-->>DoctorB: HTTP 404 Not Found (Ngăn chặn đoán biết UUID của phòng khám khác)
```

---

### 4.4. Sequence 4: Chặn Gắn Batch Ngoại Lai (Negative Control - HTTP 404 Not Found)

```mermaid
sequenceDiagram
    autonumber
    actor AdminA as Admin Clinic A
    participant Gateway as API Gateway (YARP)
    participant Analysis as Analysis API
    participant Profile as Profile API
    participant DB as PostgreSQL (analysis schema)

    Note over AdminA, DB: Admin Clinic A cố tình gắn BatchB (thuộc sở hữu Clinic B) vào CampaignA
    AdminA->>Gateway: POST /api/v1/campaigns/{CampaignA_Id}/batches?clinicContextId={ClinicA_Id}<br/>Body: { batchId: BatchB_Id }
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Profile: POST /internal/v1/profile/clinic-access/resolve<br/>(Actor: AdminA, Clinic: ClinicA_Id, Action: CAMPAIGN_MANAGE)
    Profile-->>Analysis: Decision: { Allowed: true, TenantId: TenantA, Role: "CLINIC_ADMIN" }
    Analysis->>DB: SELECT id, status FROM analysis.campaigns WHERE tenant_id = TenantA AND id = CampaignA_Id
    DB-->>Analysis: 1 row (Campaign tồn tại và chưa CLOSED)
    Analysis->>DB: SELECT id FROM analysis.batches WHERE tenant_id = TenantA AND id = BatchB_Id
    DB-->>Analysis: 0 rows returned (BatchB thuộc về TenantB!)
    Analysis->>Analysis: Phát hiện Batch không thuộc quyền sở hữu của phòng khám
    Analysis-->>Gateway: HTTP 404 Not Found { code: "ERR_NOT_FOUND", message: "Không tìm thấy batch ảnh trong phòng khám này." }
    Gateway-->>AdminA: HTTP 404 Not Found (Bảo vệ toàn vẹn dữ liệu ca chụp)
```

---

### 4.5. Sequence 5: Chặn Thao Tác Khi Chiến Dịch Đã Khóa (Negative Control - HTTP 409 Conflict)

```mermaid
sequenceDiagram
    autonumber
    actor AdminA as Admin Clinic A
    participant Gateway as API Gateway (YARP)
    participant Analysis as Analysis API
    participant Profile as Profile API
    participant DB as PostgreSQL (analysis schema)

    Note over AdminA, DB: Admin Clinic A cố gắn thêm batch mới vào Campaign đã có trạng thái CLOSED
    AdminA->>Gateway: POST /api/v1/campaigns/{CampaignA_Closed}/batches?clinicContextId={ClinicA_Id}<br/>Body: { batchId: BatchA_New }
    Gateway->>Analysis: Reverse Proxy
    Analysis->>Profile: Resolve Clinic Access -> Allowed: true, TenantId: TenantA
    Analysis->>DB: SELECT id, status FROM analysis.campaigns WHERE tenant_id = TenantA AND id = CampaignA_Closed
    DB-->>Analysis: 1 row with status = 'CLOSED'
    Analysis->>Analysis: Kiểm tra nghiệp vụ: Chiến dịch đã kết thúc (CLOSED)
    Analysis-->>Gateway: HTTP 409 Conflict { code: "ERR_CAMPAIGN_CLOSED", message: "Chiến dịch đã kết thúc (CLOSED), không cho phép nạp thêm ảnh chụp mới." }
    Gateway-->>AdminA: HTTP 409 Conflict (Bảo đảm tính bất biến của hồ sơ kết thúc)
```

---

## 5. Bảng Đối Soát 10+ Ca Kiểm Thử Chéo Tenant (Test Verification Matrix)

Bảng dưới đây quy định tập hợp các ca kiểm thử bảo mật tự động trong bộ kiểm thử `CampaignMultiTenantHttpTests`:

| Mã Ca Test | Phân loại | Mô tả kịch bản kiểm thử | Token Người Gọi | Clinic Context | Tài nguyên mục tiêu | Kết quả kỳ vọng (Status & Code) |
| --- | --- | --- | --- | --- | --- | --- |
| **TC-POS-01** | Positive Control | Clinic Admin tạo chiến dịch mới đầy đủ thông tin | `token_admin_a` | `ClinicA` | N/A | **201 Created** |
| **TC-POS-02** | Positive Control | Clinic Admin đọc chi tiết chiến dịch vừa tạo | `token_admin_a` | `ClinicA` | `CampaignA` | **200 OK** |
| **TC-POS-03** | Positive Control | Clinic Admin cập nhật trạng thái sang `IN_PROGRESS` | `token_admin_a` | `ClinicA` | `CampaignA` | **200 OK** |
| **TC-POS-04** | Positive Control | Bác sĩ Clinic A xem tóm tắt lâm sàng chiến dịch | `token_doctor_a` | `ClinicA` | `CampaignA` | **200 OK** |
| **TC-POS-05** | Positive Control | Clinic Admin gắn batch 50 ảnh hợp lệ của Clinic A | `token_admin_a` | `ClinicA` | `CampaignA` + `BatchA` | **200 OK** |
| **TC-POS-06** | Positive Control | Clinic Admin xuất báo cáo y tế CSV tổng kết | `token_admin_a` | `ClinicA` | `CampaignA` | **200 OK** (`text/csv`) |
| **TC-NEG-01** | Negative Control | Admin Clinic B gọi API với `clinicContextId` Clinic A | `token_admin_b` | `ClinicA` | Danh sách Campaign A | **403 Forbidden** (`ERR_FORBIDDEN`) |
| **TC-NEG-02** | Negative Control | Bác sĩ Clinic B gọi API với `clinicContextId` Clinic A | `token_doctor_b` | `ClinicA` | Chi tiết Campaign A | **403 Forbidden** (`ERR_FORBIDDEN`) |
| **TC-NEG-03** | Negative Control | Admin Clinic B dùng context Clinic B xem Campaign A | `token_admin_b` | `ClinicB` | `CampaignA` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-04** | Negative Control | Bác sĩ Clinic B dùng context Clinic B xem Campaign A | `token_doctor_b` | `ClinicB` | `CampaignA` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-05** | Negative Control | Admin Clinic B cố sửa trạng thái Campaign Clinic A | `token_admin_b` | `ClinicB` | `CampaignA` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-06** | Negative Control | Bác sĩ Clinic B cố xem thống kê (summary) Campaign A | `token_doctor_b` | `ClinicB` | `CampaignA` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-07** | Negative Control | Admin Clinic B cố xuất CSV báo cáo của Campaign A | `token_admin_b` | `ClinicB` | `CampaignA` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-08** | Negative Control | Admin Clinic A cố gắn Batch của Clinic B vào Campaign A | `token_admin_a` | `ClinicA` | `CampaignA` + `BatchB` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-09** | Negative Control | Admin Clinic B cố gắn Batch của Clinic B vào Campaign A | `token_admin_b` | `ClinicB` | `CampaignA` + `BatchB` | **404 Not Found** (`ERR_NOT_FOUND`) |
| **TC-NEG-10** | Negative Control | Cố gắn batch vào Campaign đã khóa (`CLOSED`) | `token_admin_a` | `ClinicA` | `CampaignA_Closed` | **409 Conflict** (`ERR_CAMPAIGN_CLOSED`) |
| **TC-NEG-11** | Negative Control | Bác sĩ Clinic A cố tạo chiến dịch (vượt quyền Admin) | `token_doctor_a` | `ClinicA` | N/A | **403 Forbidden** (`ERR_FORBIDDEN`) |
| **TC-NEG-12** | Negative Control | Bệnh nhân cá nhân (Patient) cố truy cập Campaign | `token_patient` | `ClinicA` | N/A | **403 Forbidden** (`ERR_FORBIDDEN`) |
| **TC-NEG-13** | Negative Control | Request không có Bearer token hợp lệ | Không có | `ClinicA` | N/A | **401 Unauthorized** (`ERR_UNAUTHORIZED`) |
| **TC-NEG-14** | Negative Control | Bỏ qua query param, chỉ gửi header `X-Clinic-Context-Id` | `token_admin_a` | Header only | `CampaignA` | **400 Bad Request** (`clinicContextId`) |

---
*Tài liệu được ban hành chính thức cho Sprint 5 — Đề tài AURA SP26SE025.*
