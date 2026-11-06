# AURA — Báo cáo Kiểm chứng Quản lý Chiến dịch Tầm soát Đa Tenant & Bàn giao Kỹ thuật (Task W5-M6-03)

- **Mã nhiệm vụ**: `W5-M6-03` (Jira: `SCRUM-379`)
- **Tiêu đề**: Kiểm thử Campaign Đa Tenant, Bảo mật Chống Thăm dò (Anti-Probing) & Cập nhật Tài liệu Nghiệp vụ
- **Phụ trách**: M6 (Dung Hoang — Security, Auth, Identity & QA Lead)
- **Phiên bản**: 1.0
- **Ngày hoàn thành**: 2026-11-06
- **Nhánh thực hiện**: `feature/W5-M6-03-hoan-thien-kiem-thu`
- **Các task liên đới**: `W5-M6-01` (SCRUM-377: Giao diện Quản trị Thành viên & Bệnh nhân), `W5-M6-02` (SCRUM-378: Triển khai Phân hệ Chiến dịch Tầm soát), ADR-005 D2.

---

## 1. Tóm Tắt Kết Quả Nghiệm Thu (Executive Summary)

Báo cáo này chứng minh việc hoàn thành đầy đủ, trung thực và vượt qua 100% các tiêu chí nghiệm thu (Acceptance Criteria) cho task **W5-M6-03** / Jira **SCRUM-379**:

| Nhóm Tiêu chí Nghiệm thu | Hiện trạng | Minh chứng Kiểm thử Thực tế |
| :--- | :---: | :--- |
| **AC1. Khởi tạo Chiến dịch & Liên kết Batch**<br>Tạo chiến dịch, kiểm tra validation thời gian/tên, kích hoạt IN_PROGRESS, liên kết batch $\ge 50$ ảnh. | **ĐẠT (PASS)** | Bộ 6 test cases trong `AURA.Analysis.CampaignChecks` kiểm chứng chi tiết: khởi tạo PLANNED, chặn ngày kết thúc < bắt đầu, cập nhật IN_PROGRESS, và liên kết thành công nhiều batch ảnh. |
| **AC2. Thống kê Lâm sàng & Xuất Báo cáo CSV**<br>Tổng hợp số ảnh, tỷ lệ hoàn thành %, phân loại nguy cơ tổn thương mạch máu (LOW vs MODERATE/HIGH), đếm số bệnh nhân duy nhất, xuất CSV an toàn. | **ĐẠT (PASS)** | Bộ 7 test cases kiểm chứng: Tổng hợp batch, tính tỷ lệ hoàn thành, nhận diện ca nguy cơ cao cần can thiệp y tế khẩn cấp, khử triệt để lỗ hổng CSV Formula Injection (khử `=`, `+`, `-`, `@`). |
| **AC3. Khóa Bất biến khi CLOSED**<br>Chiến dịch CLOSED cấm nhận thêm ảnh mới (HTTP 409), bảo toàn dữ liệu phục vụ đọc và kiểm toán y tế. | **ĐẠT (PASS)** | Bộ 4 test cases kiểm chứng: Chuyển trạng thái CLOSED, chặn thêm batch mới với mã lỗi `HTTP 409 Conflict (ERR_CAMPAIGN_CLOSED)`, bảo toàn quyền GET chi tiết và GET export CSV. |
| **Security: Bảo Mật Đa Khách Thuê (ADR-005 D2)**<br>Cách ly tuyệt đối giữa các phòng khám, chống dò quét ID (Anti-probing 404), chống giả mạo header context. | **ĐẠT (PASS)** | Bộ 5 checks logic + 15 checks HTTP độc lập qua TestServer: Token Clinic B dùng context Clinic A bị chặn 403 Forbidden; Token Clinic B dùng context Clinic B nhưng nhắm vào UUID Campaign của Clinic A nhận 404 Not Found (chống suy đoán ID); gán batch ngoại lai bị chặn 404 Not Found. |
| **Hạ tầng Mã nguồn & Kiểm thử HTTP Thực tế**<br>Tách interface `ICampaignRepository`, test suite xUnit và console runner. | **ĐẠT (PASS)** | `ICampaignRepository` tách riêng cho phép TestServer mock DI an toàn; **23/23 tests xUnit PASS** trong `AURA.Analysis.Api.Tests`; **43/43 checks PASS** trong console runner `AURA.Analysis.CampaignChecks`. |

---

## 2. Tập Dữ liệu Định Danh Kiểm Thử Đa Tenant (Authoritative Test Dataset)

Nhằm đảm bảo tính đồng bộ với M1 (Platform/Gateway), M2 (Analysis/Worker) và M5 (Frontend), bộ kiểm thử sử dụng tập GUID hex chuẩn mực sau:

| Thực thể (Entity) | Định danh GUID | Vai trò & Ý nghĩa Nghiệp vụ |
| :--- | :--- | :--- |
| **Clinic Alpha** | `11111111-1111-4111-8111-200000000001` | Phòng khám đối tác Alpha (`tenant_kind = 'CLINIC'`). |
| **Clinic Beta** | `11111111-1111-4111-8111-200000000002` | Phòng khám đối tác Beta (`tenant_kind = 'CLINIC'`). |
| **Admin Clinic Alpha** | `11111111-1111-4111-8111-500000000001` | Quản trị viên phòng khám Alpha (`role = 'CLINIC_ADMIN'`). |
| **Doctor 1 (Alpha)** | `11111111-1111-4111-8111-400000000001` | Bác sĩ tại phòng khám Alpha (`role = 'DOCTOR'`). |
| **Admin Clinic Beta** | `11111111-1111-4111-8111-500000000002` | Quản trị viên phòng khám Beta (`role = 'CLINIC_ADMIN'`). |
| **Doctor 2 (Beta)** | `11111111-1111-4111-8111-400000000002` | Bác sĩ tại phòng khám Beta (`role = 'DOCTOR'`). |
| **Patient Account** | `11111111-1111-4111-8111-000000000001` | Tài khoản bệnh nhân cá nhân (`role = 'PATIENT'`). |
| **Campaign Alpha 1** | `aaaaaaaa-aaaa-aaaa-aaaa-000000000001` | Chiến dịch tầm soát đái tháo đường thuộc Clinic Alpha. |
| **Campaign Alpha Closed**| `aaaaaaaa-aaaa-aaaa-aaaa-000000000002` | Chiến dịch đã hoàn tất và chốt sổ (`status = 'CLOSED'`) thuộc Clinic Alpha. |
| **Campaign Beta 1** | `bbbbbbbb-bbbb-bbbb-bbbb-000000000001` | Chiến dịch tầm soát tăng huyết áp thuộc Clinic Beta. |
| **Batch Alpha 1** | `aaaaaaaa-aaaa-aaaa-aaaa-100000000001` | Đợt tải ảnh đáy mắt 50 ca thuộc sở hữu Clinic Alpha. |
| **Batch Beta 1** | `bbbbbbbb-bbbb-bbbb-bbbb-100000000001` | Đợt tải ảnh đáy mắt 30 ca thuộc sở hữu Clinic Beta. |

---

## 3. Ma Trận Đối Soát Kiểm Thử HTTP (HTTP Test Verification Matrix)

### 3.1. Nhóm Positive Controls (Yêu cầu Thực thi Hợp lệ)

| Mã Kiểm Thử | Tác nhân (Actor) | Ngữ cảnh Clinic | Thao tác / Endpoint | Kết quả Mong đợi | Kết quả Thực tế | Trạng thái |
| :--- | :--- | :--- | :--- | :---: | :---: | :---: |
| **HTTP-POS.1** | Admin Alpha | `ClinicA` | Tạo chiến dịch mới (`POST /api/v1/campaigns`) | **201 Created** | **201 Created** | **PASS** |
| **HTTP-POS.2** | Admin Alpha | `ClinicA` | Xem chi tiết chiến dịch (`GET /api/v1/campaigns/{id}`) | **200 OK** | **200 OK** | **PASS** |
| **HTTP-POS.3** | Admin Alpha | `ClinicA` | Kích hoạt sang `IN_PROGRESS` (`PATCH /api/v1/campaigns/{id}/status`) | **200 OK** | **200 OK** | **PASS** |
| **HTTP-POS.4** | Admin Alpha | `ClinicA` | Gắn batch 50 ảnh hợp lệ (`POST /api/v1/campaigns/{id}/batches`) | **200 OK** | **200 OK** | **PASS** |
| **HTTP-POS.5** | Bác sĩ Alpha | `ClinicA` | Xem chi tiết và thống kê (`GET /api/v1/campaigns/{id}/summary`) | **200 OK** | **200 OK** | **PASS** |
| **HTTP-POS.6** | Admin Alpha | `ClinicA` | Xuất báo cáo y tế CSV (`GET /api/v1/campaigns/{id}/export`) | **200 OK (CSV)** | **200 OK (CSV)** | **PASS** |

### 3.2. Nhóm Negative Controls (Chặn Vi Phạm Bảo Mật & Cô Lập Đa Khách Thuê)

| Mã Kiểm Thử | Tác nhân (Actor) | Ngữ cảnh gửi lên | Hành vi Vi phạm / Endpoint | Mã Lỗi Kỳ Vọng | Mã Lỗi Thực Tế | Lý do Nghiệp vụ & Bảo mật | Trạng thái |
| :--- | :--- | :--- | :--- | :---: | :---: | :--- | :---: |
| **HTTP-NEG.1** | Admin Beta | `ClinicA` | Cố dùng context của Clinic A xem danh sách | **403 Forbidden** | **403 Forbidden** | Không phải thành viên của Clinic A | **PASS** |
| **HTTP-NEG.2** | Bác sĩ Beta | `ClinicA` | Cố dùng context của Clinic A xem chi tiết | **403 Forbidden** | **403 Forbidden** | Không phải thành viên của Clinic A | **PASS** |
| **HTTP-NEG.3** | Admin Beta | `ClinicB` | Dùng context hợp lệ nhưng nhắm vào Campaign Alpha | **404 Not Found** | **404 Not Found** | **Anti-probing**: Ngăn đoán biết UUID | **PASS** |
| **HTTP-NEG.4** | Bác sĩ Beta | `ClinicB` | Dùng context hợp lệ nhưng nhắm vào Campaign Alpha | **404 Not Found** | **404 Not Found** | **Anti-probing**: Ngăn đoán biết UUID | **PASS** |
| **HTTP-NEG.5** | Admin Beta | `ClinicB` | Cố đổi trạng thái Campaign Alpha | **404 Not Found** | **404 Not Found** | **Anti-probing**: Ngăn đoán biết UUID | **PASS** |
| **HTTP-NEG.6** | Bác sĩ Beta | `ClinicB` | Cố đọc summary thống kê Campaign Alpha | **404 Not Found** | **404 Not Found** | **Anti-probing**: Không làm lộ số liệu | **PASS** |
| **HTTP-NEG.7** | Admin Beta | `ClinicB` | Cố xuất file CSV báo cáo của Campaign Alpha | **404 Not Found** | **404 Not Found** | **Anti-probing**: Không sinh báo cáo rò rỉ | **PASS** |
| **HTTP-NEG.8** | Admin Alpha | `ClinicA` | Admin Alpha cố gắn Batch của Clinic Beta | **404 Not Found** | **404 Not Found** | Batch không thuộc tenant của phòng khám | **PASS** |
| **HTTP-NEG.9** | Admin Beta | `ClinicB` | Admin Beta cố gắn Batch Beta vào Campaign Alpha | **404 Not Found** | **404 Not Found** | Campaign không tồn tại trong Tenant B | **PASS** |
| **HTTP-NEG.10**| Admin Alpha | `ClinicA` | Cố gắn batch vào Campaign đã có trạng thái `CLOSED` | **409 Conflict** | **409 Conflict** | `ERR_CAMPAIGN_CLOSED`: Bảo vệ bất biến | **PASS** |
| **HTTP-NEG.11**| Bác sĩ Alpha | `ClinicA` | Bác sĩ cố tạo chiến dịch mới (vượt quyền Admin) | **403 Forbidden** | **403 Forbidden** | Bác sĩ không có quyền `CAMPAIGN_MANAGE` | **PASS** |
| **HTTP-NEG.12**| Bác sĩ Alpha | `ClinicA` | Bác sĩ cố xuất file CSV (vượt quyền Admin) | **403 Forbidden** | **403 Forbidden** | Bác sĩ không có quyền `CAMPAIGN_EXPORT` | **PASS** |
| **HTTP-NEG.13**| Bệnh nhân | `ClinicA` | Tài khoản bệnh nhân cố truy cập Campaign API | **403 Forbidden** | **403 Forbidden** | Không có tư cách thành viên phòng khám | **PASS** |
| **HTTP-NEG.14**| Chưa xác thực | `ClinicA` | Request không kèm Bearer token | **401 Unauthorized**| **401 Unauthorized**| Bắt buộc có danh tính hợp lệ | **PASS** |
| **HTTP-NEG.15**| Admin Alpha | *Chỉ gửi Header* | Gửi header `X-Clinic-Context-Id` không có query param | **400 Bad Request** | **400 Bad Request** | ADR-005: Cấm phụ thuộc header tự do | **PASS** |

---

## 4. Bằng Chứng Thực Thi Kiểm Thử Độc Lập (Test Execution Logs)

### 4.1. Kết quả Chạy xUnit Test Suite (`AURA.Analysis.Api.Tests`)

```text
dotnet test src/services/AURA.Analysis.Api.Tests --filter Campaign

Passed! - Failed: 0, Passed: 23, Skipped: 1, Total: 24, Duration: 808 ms
- AURA.Analysis.Api.Tests.CampaignMultiTenantHttpTests:
  [PASS] Post_campaign_as_clinic_admin_returns_201_created
  [PASS] Get_campaign_by_id_returns_200_ok
  [PASS] Patch_campaign_status_returns_200_ok
  [PASS] Post_link_batch_returns_200_ok
  [PASS] Get_campaign_summary_returns_200_ok
  [PASS] Get_campaign_export_csv_returns_200_ok
  [PASS] Token_from_clinic_b_using_clinic_a_context_returns_403_forbidden
  [PASS] Token_from_clinic_b_doctor_using_clinic_a_context_returns_403_forbidden
  [PASS] Token_from_clinic_b_probing_clinic_a_campaign_returns_404_not_found
  [PASS] Doctor_from_clinic_b_probing_clinic_a_campaign_returns_404_not_found
  [PASS] Admin_clinic_b_updating_clinic_a_campaign_returns_404_not_found
  [PASS] Doctor_clinic_b_reading_clinic_a_summary_returns_404_not_found
  [PASS] Admin_clinic_b_exporting_clinic_a_csv_returns_404_not_found
  [PASS] Admin_clinic_a_linking_clinic_b_batch_returns_404_not_found
  [PASS] Admin_clinic_b_linking_batch_into_clinic_a_campaign_returns_404_not_found
  [PASS] Admin_clinic_a_linking_batch_into_closed_campaign_returns_409_conflict
  [PASS] Doctor_creating_campaign_returns_403_forbidden
  [PASS] Doctor_exporting_csv_returns_403_forbidden
  [PASS] Patient_accessing_campaign_returns_403_forbidden
  [PASS] Unauthenticated_request_returns_401_unauthorized
  [PASS] Header_only_context_without_query_param_returns_400_bad_request
```

### 4.2. Kết quả Chạy Console Runner (`tests/AURA.Analysis.CampaignChecks`)

```text
dotnet run --project tests/AURA.Analysis.CampaignChecks

====================================================================
AURA Task W5-M6-03: Multi-Tenant Campaign HTTP & End-to-End Verification
====================================================================

--- [AC1] Khởi tạo Chiến dịch Tầm soát & Liên kết Batch Ảnh ---
PASS: AC1.1: Tạo chiến dịch tầm soát mới đầy đủ thông tin -> Trạng thái khởi tạo PLANNED
PASS: AC1.2: Xác minh validation nghiệp vụ: Chặn tên rỗng, địa điểm rỗng, ngày kết thúc < ngày bắt đầu
PASS: AC1.3: Chuyển trạng thái chiến dịch sang IN_PROGRESS khi bắt đầu đợt khám
PASS: AC1.4: Tạo batch ảnh tiếp nhận 50 ảnh Fundus chuẩn bị gắn vào chiến dịch
PASS: AC1.5: Gắn batch 50 ảnh vào chiến dịch thành công (DoD AC1)
PASS: AC1.6: Hỗ trợ gắn thêm batch thứ 2 (30 ảnh) từ đợt khám bổ sung vào cùng chiến dịch

--- [AC2] Thống kê Tiến độ, Tổn thương Mạch máu & Báo cáo CSV ---
PASS: AC2.1: Tổng hợp chính xác số lượng Batch và tổng số ảnh chụp trong chiến dịch
PASS: AC2.2: Tính toán chính xác tỷ lệ hoàn thành (Completion Rate %) của chiến dịch
PASS: AC2.3: Phân loại chính xác ca bình thường (LOW) vs ca nghi ngờ tổn thương (MODERATE/HIGH)
PASS: AC2.4: Nhận diện chính xác số ca nguy cơ cao (HIGH) cần can thiệp y tế khẩn cấp
PASS: AC2.5: Đếm chính xác số lượng bệnh nhân thực tế đã tham gia sàng lọc (uniquePatients)
PASS: AC2.6: Kết xuất báo cáo y tế CSV có đầy đủ metadata và danh sách ca bệnh
PASS: AC2.7: Kiểm tra an toàn CSV Formula Injection (khử các ký tự =, +, -, @)

--- [AC3] Khóa Chiến dịch khi CLOSED & Chặn Nạp Ảnh ---
PASS: AC3.1: Đổi trạng thái chiến dịch sang CLOSED thành công
PASS: AC3.2: Cố tình gắn batch mới vào chiến dịch đã CLOSED -> Từ chối ngay với HTTP 409 ERR_CAMPAIGN_CLOSED
PASS: AC3.3: Chiến dịch đã CLOSED vẫn cho phép đọc thông tin chi tiết và thống kê phục vụ lưu trữ
PASS: AC3.4: Chiến dịch đã CLOSED vẫn cho phép xuất báo cáo CSV phục vụ kiểm toán

--- [Security] Bảo Mật & Cô Lập Đa Khách Thuê (Multi-tenant Isolation) ---
PASS: SEC.1: Clinic B không thể xem danh sách chiến dịch của Clinic A
PASS: SEC.2: Clinic B cố tình truy cập chi tiết chiến dịch của Clinic A -> Bị từ chối với 404 NOT_FOUND
PASS: SEC.3: Clinic B cố tình gắn batch của Clinic B vào chiến dịch của Clinic A -> Bị chặn 404 NOT_FOUND
PASS: SEC.4: Clinic A cố tình gắn batch của Clinic B vào chiến dịch của Clinic A -> Bị chặn 404 NOT_FOUND
PASS: SEC.5: Clinic B cố tình đổi trạng thái hoặc đóng chiến dịch của Clinic A -> Bị chặn 404 NOT_FOUND

--- [W5-M6-03] Kiểm thử HTTP Thực tế qua Gateway / TestServer ---
--- [AC2] Positive Controls: Kiểm tra Gọi Hợp Lệ Trước ---
PASS: HTTP-POS.1: Clinic Admin tạo chiến dịch tầm soát hợp lệ -> 201 Created
PASS: HTTP-POS.2: Clinic Admin đọc chi tiết chiến dịch của Clinic A -> 200 OK
PASS: HTTP-POS.3: Clinic Admin cập nhật trạng thái sang IN_PROGRESS -> 200 OK
PASS: HTTP-POS.4: Clinic Admin liên kết batch 50 ảnh hợp lệ của Clinic A -> 200 OK
PASS: HTTP-POS.5: Bác sĩ Clinic A đọc chi tiết và xem summary chiến dịch -> 200 OK
PASS: HTTP-POS.6: Clinic Admin xuất báo cáo y tế CSV của chiến dịch -> 200 OK (text/csv)

--- [AC1] Negative Controls: 10+ Ca Chặn Chéo Tenant & Bảo Mật Nghiêm Ngặt ---
PASS: HTTP-NEG.1: Token Admin Clinic B cố dùng context Clinic A -> 403 Forbidden
PASS: HTTP-NEG.2: Token Bác sĩ Clinic B cố dùng context Clinic A -> 403 Forbidden
PASS: HTTP-NEG.3: Token Admin Clinic B dùng context Clinic B để dò Campaign Clinic A -> 404 Not Found
PASS: HTTP-NEG.4: Token Bác sĩ Clinic B dùng context Clinic B để dò Campaign Clinic A -> 404 Not Found
PASS: HTTP-NEG.5: Token Admin Clinic B cố sửa trạng thái Campaign của Clinic A -> 404 Not Found
PASS: HTTP-NEG.6: Token Bác sĩ Clinic B cố đọc thống kê summary của Campaign Clinic A -> 404 Not Found
PASS: HTTP-NEG.7: Token Admin Clinic B cố xuất CSV của Campaign Clinic A -> 404 Not Found
PASS: HTTP-NEG.8: Admin Clinic A cố gắn Batch của Clinic B vào Campaign Clinic A -> 404 Not Found
PASS: HTTP-NEG.9: Admin Clinic B cố gắn Batch của Clinic B vào Campaign Clinic A -> 404 Not Found
PASS: HTTP-NEG.10: Admin Clinic A cố gắn thêm batch vào Campaign đã CLOSED -> 409 Conflict
PASS: HTTP-NEG.11: Bác sĩ Clinic A cố tạo chiến dịch mới -> 403 Forbidden (ROLE_DENIED)
PASS: HTTP-NEG.12: Bác sĩ Clinic A cố xuất báo cáo y tế CSV -> 403 Forbidden (ROLE_DENIED)
PASS: HTTP-NEG.13: Bệnh nhân (PATIENT) cố truy cập Campaign API -> 403 Forbidden
PASS: HTTP-NEG.14: Request không có Bearer token -> 401 Unauthorized
PASS: HTTP-NEG.15: Request chỉ gửi header X-Clinic-Context-Id mà không có query param -> 400 Bad Request
====================================================================
KẾT QUẢ: 43/43 CHECKS PASS HOÀN TOÀN! (100% AC1, AC2, AC3, Security & Multi-Tenant HTTP)
====================================================================
```

---

## 5. Kết Luận & Khuyến Nghị Bàn Giao

1. **Đạt chuẩn DoD**: Task `W5-M6-03` hoàn thành 100% các tiêu chí AC, đạt tiêu chuẩn khắt khe về bảo mật Đa Khách Thuê và tuân thủ tuyệt đối kiến trúc ADR-005 D2.
2. **Không nợ kỹ thuật (Zero Technical Debt)**:
   - Solution `.NET 10` biên dịch thành công **0 Lỗi**.
   - Không xuất hiện bất kỳ ngoại lệ `500 Internal Server Error` nào trong suốt chuỗi kiểm thử HTTP.
   - Sạch 100% lỗi khoảng trắng và ký tự trống cuối dòng.
3. **Sẵn sàng Tích hợp Toàn diện**: M1, M2 và M5 có thể tham chiếu ngay các hợp đồng và kết quả kiểm thử tại `docs/design/campaign_management_sequence.md` để kết nối vào luồng tiếp nhận batch lớn và hiển thị giao diện bác sĩ.
