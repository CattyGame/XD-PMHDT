# SCRUM-357 – Báo cáo kiểm thử UI Doctor / Payment / Batch

**Ngày lập**: 08/11/2026 · **Trạng thái**: Test suite và test plan đã chuẩn bị; **chưa xác nhận E2E PASS trên CI**.

## 1. Phạm vi và bằng chứng hiện có

- Theo Jira SCRUM-357: responsive ≥1024px; tự reconnect Batch sau lỗi mạng; Doctor Review + duyệt ảnh E2E PASS trên CI; tài liệu `docs/ux/user_guide_sprint4_5.md`.
- SCRUM-354 (Doctor Workspace), SCRUM-355 (Billing + FCM), SCRUM-356 (Bulk Upload + Batch) đã được đặt **In Review**; các phần tính năng vẫn trên PR riêng, **chưa merge chung**.
- SCRUM-354 PR #82 đã sửa conflict, commit d0f6f78; CI commit này cần theo dõi. SCRUM-356 PR #84 có 7/7 CI checks PASS trên commit bbd3beb. Không quy đổi CI unit/lint/build thành E2E PASS.
- Local hiện chỉ có tài khoản **Bệnh nhân**, chưa có tài khoản phòng khám DOCTOR/CLINIC_ADMIN/STAFF; Backend Analysis Worker chưa chạy và Notification Consumer trước đó `RabbitMQ__AutoStart=false`.

## 2. Kịch bản và trạng thái (không bịa kết quả)

| AC | Kiểm tra | Phương pháp | Trạng thái hiện tại |
|---|---|---|---|
| AC1 | Login + trang lỗi tại 1024/1366/1920, không tràn ngang | Playwright `scrum357.spec.ts`, tự chụp ảnh | Chưa thực thi trên CI |
| AC1 | Doctor Workspace, Billing, Bulk Upload / Dashboard ≥1024px | Playwright + tài khoản TEST theo quyền; chụp ảnh 1024/1366/1920 | Blocked: chưa có tài khoản/phần tính năng hợp nhất |
| AC2 | Batch đang xử lý, rớt mạng rồi kết nối lại không F5 | Playwright browser offline/online, GET polling | Blocked: cần batch có quyền và backend Worker |
| AC2 | Upload mất mạng, inventory/idempotency, không nhân bản lượt tải | Test backend + trình duyệt với ảnh không định danh | Blocked: không có tài khoản Clinic |
| AC2 | Doctor Review lưu nháp khi mất mạng/reload | Cần thiết kế lưu nháp an toàn cho PHI trước khi thử | Chưa triển khai/không PASS |
| AC3 | Bác sĩ xem ca, xem ảnh, review, xử lý 409 không ghi đè | Playwright role DOCTOR + phân công ca COMPLETED | Blocked: không có DOCTOR test account |
| AC3 | Chạy browser E2E trên CI | Cần Playwright browsers và test tenant/credentials ở CI secrets | Chưa cấu hình CI / chưa PASS |

## 3. Bộ kiểm thử tự động cung cấp

- `src/web/aura-web/playwright.config.ts`: Chromium, trace/video/screenshot khi lỗi.
- `src/web/aura-web/e2e/scrum357.spec.ts`: kiểm tra responsive công khai; tùy chọn Doctor Workspace, batch reconnect và 409 ở trình duyệt (409 được mock và **không** phải xung đột backend thật).
- Tài liệu người dùng `docs/ux/user_guide_sprint4_5.md`.

**Chạy tại máy dev (chỉ trên nhánh có code Doctor/Billing/Batch, khi đã có test environment):**

```powershell
cd src/web/aura-web
npm install --save-dev @playwright/test
npx playwright install chromium
$env:SCRUM357_BASE_URL = 'http://127.0.0.1:5175'
npx playwright test --config playwright.config.ts
```

Để bật kịch bản role-based, set **biến môi trường local hoặc CI Secrets**, không commit thông tin đăng nhập:

```text
SCRUM357_TEST_EMAIL            tài khoản TEST, không phải tài khoản thật
SCRUM357_TEST_PASSWORD         mật khẩu TEST
SCRUM357_DOCTOR_CLINIC_ID      UUID cơ sở mà DOCTOR được cấp quyền
SCRUM357_BATCH_ID              UUID batch hợp lệ, user có quyền đọc
```

**Lưu ý về độ tin cậy:** Playwright có thể `skip` các bài yêu cầu quyền nếu chưa cung cấp credentials. CI xanh cho các bài public không có nghĩa là AC3 PASS. Reviewer phải xác nhận số test `passed/skipped`, screenshots và test report của bộ E2E đầy đủ.

## 4. Kế hoạch E2E đầy đủ sau khi M2/M3 bàn giao

1. Cấp tài khoản test DOCTOR và CLINIC_ADMIN/STAFF với membership ACTIVE, gắn đúng Clinic/Patient/Analysis dữ liệu giả lập.
2. Bật Analysis Worker và các dependencies, tạo batch 50 ảnh giả lập đủ quyền; kiểm tra Progress UI và lặp thử offline/online không reload.
3. Dùng test tenant thanh toán Sandbox; đối chiếu đơn COMPLETED + Credit đúng một lần, không tạo giao dịch thật.
4. Review ca COMPLETED chưa được đánh giá, kiểm tra lưu vào API và trạng thái REVIEWED; dùng 2 phiên tạo 409 thực, kiểm tra popup và không ghi đè.
5. Chạy Playwright CI sau khi hợp nhất các nhánh, chụp 1024/1366/1920 cho Doctor, Billing, Bulk, Batch và kiểm tra không tràn layout. Lưu ảnh/video report không chứa PHI.

## 5. Tiêu chí bàn giao

- Mã kiểm thử và tài liệu: **đã soạn**.
- Chạy tự động và ảnh chuẩn: **chưa chạy / chưa có bằng chứng**.
- AC1/AC2/AC3: **pending / blocked**, tuyệt đối chưa đánh dấu Done.
