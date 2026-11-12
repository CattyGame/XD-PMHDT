# Nghiệm thu SCRUM-376

Ngày 12/11/2026 trên Windows, Node.js 24.21.0, npm 11.19.0, Vitest 5.0.3 và Playwright Chromium. Nhánh `feature/SCRUM-376-batch-frontend-tests` dựa trên `origin/develop` tại `e0471af`. Suite Chromium tái sử dụng từ `d520275` và bổ sung mất mạng, đối soát inventory, F5 và kiểm tra dữ liệu trình duyệt. [SCRUM-376 trên Jira](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-376)

| Tiêu chí Jira | Kết quả | Bằng chứng |
| --- | --- | --- |
| AC1 · F5 khôi phục tiến độ đúng batchId trên URL | **PASS** | Chromium mô phỏng khôi phục 68/100 rồi polling tới 100/100. Phiên Firebase TEST qua Gateway thực khôi phục 1/2 rồi tự cập nhật lên 2/2. |
| AC2 · 100% component tests module Upload đạt | **PASS** | 45/45 ca RTL Upload cá nhân, staged protocol, hồi quy và Clinic Upload đạt trong worktree tích hợp. |

## Component và mất mạng

- RTL và MSW kiểm tra file picker với JPG, từ chối GIF và tệp vượt 10 MiB, kéo thả 100 ảnh, preview, tối đa bốn request đang chạy, một ảnh trong mỗi PUT, một manifest, một finalize và thu hồi Object URL. Bộ test hiện có của hai PR bao phủ OD/OS theo ảnh, khóa nhấp lặp, retry riêng, hủy khi unmount và trạng thái chưa rõ cần đối soát.
- RTL Batch Progress chạy API client thật với MSW để đọc 0/100, 68/100 và 100/100 từ URL, kiểm tra Pending/Processing/Success/Failed, các lỗi 401/403/404/429/500/503 và timeout. Suite tích hợp bao phủ backoff, `Retry-After`, hủy và tránh GET chồng lấn, dừng ở trạng thái cuối, chặn retry ca đang xử lý hoặc chưa đối soát và lỗi tải báo cáo.
- Trong Playwright mock, Chromium kéo thả 100 PNG sinh tại chỗ và chọn OD/OS theo ảnh. Trình duyệt đi offline sau khi mock server đã nhận 5 ảnh, trong đó phản hồi của ảnh thứ năm bị mất. Lúc đó giao diện ghi nhận 4 ảnh, số PUT đã quan sát bị giới hạn và finalize chưa chạy.
- Sau khi có mạng, một nút retry theo ảnh gửi đúng chuỗi GET inventory → PUT ảnh chưa lưu → GET inventory. Ảnh thứ năm được nhận diện đã lưu và chỉ có đúng một PUT. Sau khi tiếp tục, mock server có 100 item ID duy nhất, một manifest và một finalize. F5 giữ URL, đọc lại GET và không phát sinh upload.
- Analysis với PostgreSQL cô lập kiểm chứng cùng ca con và reservation được khôi phục khi mất phản hồi tiếp nhận. Luồng Analysis dùng Billing giả lập. Bài Billing riêng trên database fixture có 80 credit cho 100 lượt reserve. Kết quả có 55 dòng capture, 25 dòng release và số dư cuối 25.

## Bảo mật trình duyệt

- Phát hiện đăng nhập AURA lưu phiên Firebase lâu dài. Đổi sang persistence theo tab để F5 tiếp tục được và dùng Firebase SDK chuyển phiên cũ sang phạm vi phiên hiện tại. Email và token Firebase của phiên đang mở nằm trong SessionStorage do SDK quản lý. Tab mở độc lập cần đăng nhập lại. Sau logout, bài Chromium xác nhận SDK dọn phiên và giữ nguyên khóa của tính năng khác.
- Playwright kiểm tra LocalStorage, SessionStorage, IndexedDB, Cache Storage và console với dữ liệu tổng hợp. Không thấy tên hồ sơ, ID bệnh nhân, thumbnail hay tên ảnh kiểm thử trong storage lúc có bản nháp, khi tải lại và sau logout. Không thấy thông tin xác thực trong LocalStorage hoặc IndexedDB, API trong Cache Storage, hoặc marker nhạy cảm trong console. RTL xác nhận GET tiến độ và POST manifest dùng `cache: 'no-store'`. Ảnh nháp và thumbnail được giữ trong bộ nhớ tab.

## Gateway và Docker

- Gateway `GET /health`, Identity `GET /health` và Analysis `GET /health/ready` trả HTTP 200. PostgreSQL 17.11 trả kết quả truy vấn và có schema staged upload, báo cáo và credit. Redis trả PONG. Gateway từ chối `GET /api/v1/identity/me` không xác thực với HTTP 401.
- Trình duyệt tái sử dụng phiên Firebase TEST đã xác thực. Batch tổng hợp `504e3229-9cc4-46ec-931b-ffc8ee7a2954` khôi phục 50% từ `GET /api/v1/batches/{batchId}` sau F5, rồi tự cập nhật 100% và dừng polling khi fixture chuyển trạng thái. PostgreSQL local có đúng một request và hai ảnh của fixture. Đây là kiểm thử đọc qua Gateway đang chạy, với trạng thái được điều khiển trong fixture.
- Tài khoản TEST có 0 ví TEST. Upload có tính credit và kiểm tra ledger qua Gateway thật **BLOCKED**. Sandbox bị từ chối truy cập Docker engine, nên việc build hoặc khởi động lại container từ sandbox cũng **BLOCKED**. Lỗi PDF 503 qua Gateway đã được ghi trong SCRUM-375.

## Các lượt kiểm thử

| Suite | PASS | FAIL | SKIPPED | Phạm vi |
| --- | ---: | ---: | ---: | --- |
| Vitest/RTL trên nhánh SCRUM-376 | 420 | 0 | 0 | 38 tệp |
| Vitest/RTL với hai dependency | 451 | 0 | 0 | 39 tệp |
| Playwright SCRUM-376 | 1 | 0 | 0 | Chromium, mạng mô phỏng |
| Playwright SCRUM-357 | 5 | 0 | 6 | Sáu bài có điều kiện tài khoản/batch TEST sẵn có của suite |
| Analysis staged upload | 10 | 0 | 0 | PostgreSQL cô lập, migration thật |
| Billing reconciliation | 1 | 0 | 0 | PostgreSQL cô lập, ví TEST của fixture, kiểm tra settlement cho 100 lượt reserve |

Oxlint, `tsc -b` trong production build, Vite build, `node --check` cho kịch bản Gateway và `git diff --check` đạt. Oxlint còn ba cảnh báo sẵn có ở `CampaignManagementPage`. Vite có cảnh báo kích thước chunk.

Lượt Billing đầu hết connection pool 16 kết nối của fixture khi gửi 100 lượt reserve đồng thời. Bài test đạt sau khi chạy lại với pool 64 và timeout kết nối 45 giây trong database cô lập.

## Dependency và chạy lại trên Windows

PR #94 cung cấp retry từng ảnh, tiến độ tiếp nhận và cleanup bộ nhớ upload. PR #95 cung cấp polling và tải báo cáo. Hai PR đang mở khi kiểm tra. Các bài RTL mới chạy được trên nhánh SCRUM-376. Kịch bản Chromium 100 ảnh cần cả hai PR, đã chạy trên worktree tích hợp riêng với `d69d9e1` và `af0af45`. [PR #94](https://github.com/CattyGame/XD-PMHDT/pull/94) · [PR #95](https://github.com/CattyGame/XD-PMHDT/pull/95)

Sau khi có hai dependency, chạy từ thư mục `src/web/aura-web`.

```powershell
npm ci
npm test
npm exec -- playwright test --config playwright.batch-mock.config.ts --workers=1
npm run lint
npm run build
```

Để chạy lại qua Gateway, khởi động stack TEST và Vite với cấu hình Firebase Web TEST và `VITE_ENABLE_MOCK=false`. Chọn batch tổng hợp đang chạy thuộc tài khoản TEST, rồi đăng nhập trực tiếp trong Chromium khi kịch bản mở cửa sổ. Kịch bản xác nhận GET 200 với `Cache-Control: no-store`, tiến độ theo URL trước và sau F5, trạng thái cuối và không phát sinh thao tác ghi.

```powershell
node e2e/scrum376-live.mjs --batch '<UUID batch tổng hợp đang xử lý>'
```

## Tệp thay đổi

| Tệp | Nội dung |
| --- | --- |
| `src/web/aura-web/src/pages/UploadPage.regression.test.tsx` | File picker, kéo thả, 1/100 ảnh, validate, request và Object URL |
| `src/web/aura-web/src/pages/BatchProgressPage.regression.test.tsx` | Tiến độ từ URL, trạng thái theo ảnh, lỗi HTTP và timeout |
| `src/web/aura-web/e2e/batch-frontend.mock.e2e.ts` | Mất mạng, lost response, inventory, F5 và browser storage |
| `src/web/aura-web/playwright.batch-mock.config.ts` | Chromium, Vite loopback và Firebase fixture |
| `src/web/aura-web/e2e/scrum376-live.mjs` | Chạy lại F5 với Gateway và đăng nhập trực tiếp |
| `src/web/aura-web/src/firebase/config.ts` | Chuyển phiên Firebase cũ sang persistence theo tab |
| `src/web/aura-web/src/pages/LoginPage.tsx` | Persistence theo tab cho phiên mới |
| `src/web/aura-web/src/pages/LoginPage.test.tsx` | Kiểm tra persistence đăng nhập |
| `docs/testing/SCRUM-376-batch-frontend-tests-report.md` | Báo cáo nghiệm thu |
