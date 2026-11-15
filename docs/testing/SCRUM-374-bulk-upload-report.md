# SCRUM-374 · Nghiệm thu Bulk Upload UI

Ngày 11/11/2026

Nhánh `feature/SCRUM-374-bulk-upload-ui` được tạo từ `origin/develop` tại `e0471af952cde6c97ad55a34eac892025af7d267`. Các thay đổi upload được tách theo tệp từ nhánh tổng hợp tại `1a04148`. Checkout gốc và nhánh tổng hợp vẫn lưu đầy đủ công việc trước đó.

## Phạm vi

| Vị trí dưới `src/web/aura-web/` | Tệp thay đổi |
|---|---|
| API và workspace | `src/api/analysisUpload.ts`, `src/api/batchLocalPreviews.ts`, `src/api/bulkUploadUi.ts`, `src/api/itemUpload.ts`, `src/api/personalUploadWorkspace.ts` |
| Dọn dữ liệu theo phiên | `src/auth/backendSessionStore.ts` |
| Giao diện | `src/components/ItemUploadProgress.tsx`, `src/pages/ClinicUploadPage.tsx`, `src/pages/UploadPage.tsx` |
| Unit và component tests | `src/api/bulkUploadUi.test.ts`, `src/api/itemUpload.test.ts`, `src/pages/ClinicUploadPage.test.tsx`, `src/pages/UploadPage.protocol.test.tsx`, `src/pages/UploadPage.test.tsx` |
| Playwright | `e2e/bulk-upload.mock.e2e.ts`, `playwright.bulk-upload-mock.config.ts` |

Tệp bàn giao `docs/testing/SCRUM-374-bulk-upload-report.md`.

## Chức năng và lỗi đã sửa

- Hai trang upload hỗ trợ kéo thả hoặc chọn tối đa 100 ảnh. Giao diện xem trước ảnh, hiện tên và dung lượng, cho phép xóa trước khi gửi và chọn OD/OS riêng từng ảnh. Client đối chiếu đuôi JPG/JPEG/PNG với MIME nếu trình duyệt cung cấp, giới hạn 10 MiB/ảnh và phát hiện mục trùng trong lượt chọn. Máy chủ vẫn kiểm tra nội dung ảnh.
- Mỗi PUT mang một ảnh. Adapter giới hạn cứng tối đa **4** PUT đồng thời, kể cả khi truyền cấu hình cao hơn. Giao diện hiển thị từng ảnh đang chờ, đang tải, đã lưu hoặc lỗi. Thanh tổng đếm ảnh được máy chủ xác nhận. Trong lúc truyền một ảnh, thanh của ảnh đó dùng trạng thái không xác định.
- Lỗi đơn lẻ của một PUT cho phép các ảnh còn lại tiếp tục. Queue ngừng cấp việc mới khi mất mạng toàn trình duyệt, request bị hủy, lỗi quyền hoặc rate limit. Retry từng ảnh đọc inventory trước và dùng lại item ID làm idempotency key. Ảnh đã staged hoặc accepted được bỏ qua. Một lỗi vĩnh viễn ở ảnh khác vẫn cho phép retry ảnh có thể phục hồi.
- Khôi phục upload sau tải lại trang dựa trên batch ID và kiểm SHA-256 của các ảnh chọn lại. Lượt đang đối soát credit chỉ cho đọc trạng thái. Chốt lượt tải bằng định danh ổn định sau khi đã xử lý đủ inventory. Kiểm thử kiểm tra gửi một manifest, một finalize và retry đúng item.
- File và thông tin upload nằm trong bộ nhớ. Object URL được thu hồi khi xóa ảnh, tháo preview hoặc kết thúc phiên. Thumbnail chuyển tiếp sang trang theo dõi hiện có được thu nhỏ tuần tự. Bài kiểm thử theo dõi thao tác ghi browser storage và kiểm tra URL đã được giải phóng.

## Hợp đồng API và giới hạn tích hợp

Luồng sử dụng các route Analysis hiện có qua Gateway. Tạo manifest bằng `POST /api/v1/analysis-upload-batches`, đọc inventory bằng `GET /api/v1/analysis-upload-batches/{batchId}`, gửi từng ảnh qua `PUT /api/v1/analysis-upload-batches/{batchId}/items/{itemId}` và chốt bằng `POST /api/v1/analysis-upload-batches/{batchId}/finalize`. PUT dùng multipart với một trường `image` và `Idempotency-Key` bằng item ID.

Gateway và tuyến health của Analysis trên local trả 200. Manifest, PUT và finalize thiếu Firebase bearer đều trả 401. Docker CLI trong sandbox bị từ chối truy cập named pipe. Các phép đo trên stack local là kiểm tra HTTP, qua các container đang chạy.

## Kiểm thử

| Bài chạy | PASS | FAIL | SKIPPED | Phạm vi |
|---|---:|---:|---:|---|
| Vitest toàn frontend | 421 | 0 | 0 | 36 tệp, gồm regression của `develop`. |
| Upload component tests | 43 | 0 | 0 | 3 tệp React Testing Library, là tập con Vitest. |
| Playwright Bulk Upload | 1 | 0 | 0 | Chromium tạo 100 PNG 256×256 và mô phỏng API/Firebase. Queue đi tới 99 ảnh khi một PUT bị ngắt. Retry gửi đúng ảnh còn thiếu. |
| Playwright hiện có | 5 | 0 | 6 | Các bài công khai đạt. Sáu ca cần tài khoản hoặc fixture TEST. |
| `ItemUploadPostgresTests` | 10 | 0 | 0 | PostgreSQL 17 cô lập. Fixture giả lập Billing và Blob, xác minh mất phản hồi vẫn dùng cùng analysis/reservation, thu hồi quyền, batch hết hạn và replay với byte khác. |

`npm run lint` (Oxlint theo cấu hình repo), `npm exec -- tsc -b --pretty false`, `npm run build` và `git diff --check` đạt. Lint còn 3 cảnh báo có sẵn ở `CampaignManagementPage.tsx`. Vite cảnh báo bundle chính 596,55 kB.

Để chạy lại bài Chromium từ `src/web/aura-web`, khởi động Vite trên `127.0.0.1:5175` với cấu hình Firebase công khai của fixture và `VITE_ENABLE_MOCK=false`. Playwright chặn toàn bộ request Firebase và API liên quan.

```powershell
$env:VITE_FIREBASE_API_KEY = 'aura-playwright-mock'
$env:VITE_FIREBASE_AUTH_DOMAIN = 'aura-playwright.invalid'
$env:VITE_FIREBASE_PROJECT_ID = 'aura-playwright-mock'
$env:VITE_FIREBASE_MESSAGING_SENDER_ID = '123456789012'
$env:VITE_FIREBASE_APP_ID = '1:123456789012:web:0000000000000000000000'
$env:VITE_ENABLE_MOCK = 'false'
npm exec -- vite --host 127.0.0.1 --port 5175 --strictPort
```

Terminal thứ hai chạy `npm exec -- playwright test --config playwright.bulk-upload-mock.config.ts --workers=1`.

## Acceptance Criteria

| Tiêu chí | Kết quả | Bằng chứng |
|---|---|---|
| AC1 · Kéo thả và xem trước 100 ảnh | PASS | Chromium render đủ 100 thumbnail, tải được preview và thao tác OD/OS trước khi gửi. Component test kiểm tra ranh giới 101 và thu hồi URL. |
| AC2 · Retry/resume từng ảnh | PASS | Chromium và RTL mô phỏng PUT lỗi, xác minh GET inventory rồi retry một ảnh. Unit tests kiểm timeout, đối soát trạng thái chưa rõ và F5 theo checksum. |
| Upload có xác thực và Billing qua Gateway | BLOCKED | Cần tài khoản Firebase TEST có credit thử nghiệm và ảnh tổng hợp. |

Phép kiểm hiệu năng dùng ảnh PNG tổng hợp nhỏ. Chưa đo bộ ảnh gần 1 GiB hoặc tải thật qua Gateway. Các biện pháp idempotency đã kiểm ở frontend và tầng service, còn xác minh giao dịch Billing qua Gateway cần ví TEST. Jira và thao tác merge thuộc quy trình review của nhóm.
