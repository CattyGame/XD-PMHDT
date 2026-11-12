# Nghiệm thu SCRUM-375

Ngày 12/11/2026. Nhánh `feature/SCRUM-375-batch-progress-reports`, base `origin/develop` tại `e0471af`. Tái sử dụng phần theo dõi và tải báo cáo từ `1a04148`. PR #94 của SCRUM-374 còn mở khi kiểm tra. Trang dùng route `/batches/:batchId`, bộ tính tiến độ và bộ nhớ tạm thumbnail đã có trong develop. [Jira SCRUM-375](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-375) · [PR #94](https://github.com/CattyGame/XD-PMHDT/pull/94)

## Kết quả nghiệm thu

| Tiêu chí | Kết quả | Bằng chứng |
| --- | --- | --- |
| AC1. Tiến trình được cập nhật liên tục với tần suất phù hợp | **PASS** | Trình duyệt đọc batch TEST qua Gateway, hiển thị 1/2 và 50%, khôi phục sau F5, tự cập nhật thành 2/2 và 100% sau khi trạng thái fixture thay đổi. RTL xác nhận tối đa một GET đang chờ, nhịp 3 giây, backoff, `Retry-After` và dừng khi hoàn tất. |
| AC2. Tải được PDF chuẩn cho đúng ca và tài khoản | **BLOCKED** | Trình duyệt gửi POST cho đúng ca đã hoàn tất. Lần gọi thực tế thất bại với HTTP 503 `ERR_NETWORK`, nên không có PDF qua Gateway để đối chiếu. Tích hợp Analysis/PostgreSQL cô lập xác nhận đúng ca, cấu trúc PDF và từ chối tài khoản khác. |

## Phạm vi và lỗi đã sửa

- Hiển thị tổng ảnh, số ảnh có kết quả cuối, phần trăm và trạng thái từng ảnh. F5 khôi phục qua Batch ID trên URL. Thumbnail cục bộ chỉ nằm trong bộ nhớ tab.
- Mỗi batch có tối đa một GET đang chạy. Khi hoạt động bình thường, đợi 3 giây sau mỗi lần đọc. Với lỗi tạm thời, thử lại tối đa 5 lần, chờ 2–30 giây hoặc lâu hơn theo `Retry-After`. Dừng timer khi tab ẩn hoặc batch đã kết thúc, hủy GET khi rời trang hoặc đổi ngữ cảnh.
- Khi quay lại tab hoặc có kết nối, đọc lại nếu đã hết thời gian chờ. Tích hợp listener FCM ở foreground cho tín hiệu đúng Batch ID. Kiểm thử listener bằng mock. Chưa gửi FCM thật.
- Giữ nút PDF theo từng ca ở trạng thái `COMPLETED`. Các nút PDF tổng hợp, CSV và ZIP xuất hiện khi batch kết thúc và đã xử lý xong tiếp nhận. Sửa điều kiện kết thúc khi còn mục upload hoặc đối soát `UNKNOWN`.
- Tải báo cáo bằng POST có xác thực rồi GET nội dung qua Gateway. Kiểm tra report ID, origin, phạm vi phòng khám, thời hạn, MIME và phần đầu PDF/CSV/ZIP. Chặn nhấp đúp trong khi tải, hủy request khi rời trang và thu hồi Blob URL. Hiển thị lỗi 401, 403, 404, 429 và lỗi máy chủ.
- Trang chỉ gọi API đọc tiến độ và xuất báo cáo. Mục `PROCESSING` và `UNKNOWN` không có hành động retry phân tích. Thao tác “Tải lại trạng thái” chỉ gửi GET. Không lưu dữ liệu bệnh nhân vào LocalStorage.

## Kiểm thử qua Gateway

- Trong phiên tích hợp, Gateway health, Analysis readiness, Billing health và Notification readiness trả HTTP 200. Các route batch, tạo báo cáo và đọc nội dung yêu cầu xác thực, trả 401 khi không có bearer.
- Firebase `aura-dev-97716` xác thực thành công. Identity và Profile qua Gateway đều trả HTTP 200, role `PATIENT`.
- Truy vấn chỉ đọc môi trường local xác nhận tài khoản chưa có ví credit `TEST`. Đường cấp credit có trong mã Billing là tạo order cho gói TEST rồi thanh toán qua `/api/v1/billing/sandbox/pay` trên stack sandbox có cấu hình hợp lệ. Kiểm thử upload qua pipeline thật và xác nhận đối soát credit chống trừ hai lần còn **BLOCKED**.
- Batch tổng hợp `0958a867-613d-4940-a0b2-262820a6f712` được tạo riêng cho phiên TEST trong PostgreSQL local với PNG sinh tự động ở Azurite private. Một ca hoàn tất, một ca chuyển từ đang xử lý sang thất bại. Cập nhật dữ liệu fixture để kiểm tra luồng đọc và polling qua Gateway. Phiên kiểm thử này không chạy AI worker hay đặt/trừ credit.
- Ca hoàn tất `4472cd99-ffde-473d-8cbd-6920aee41214` trả 200 ở GET chi tiết. POST `/api/v1/analyses/{caseId}/reports` qua Gateway trả 503 và giao diện hiện lỗi tải. Correlation ID `4e66d7984d2044619103d86e88d5b188`. Chưa có file hoặc snapshot báo cáo để xác minh PDF thật, quyền của tài khoản khác và nội dung CSV/ZIP thật. CSV và ZIP được hỗ trợ trong mã Analysis, nghiệm thu qua Gateway còn **BLOCKED**.
- Khi thử lại, Gateway cổng 5000, PostgreSQL cổng 5432 và Azurite cổng 10000 đều từ chối kết nối. Sandbox không truy cập được Docker engine hoặc WSL. Chưa xác minh image/container mới.

## Hồi quy

| Kiểm tra | Kết quả | Môi trường |
| --- | --- | --- |
| Vitest / React Testing Library | **426 PASS**, 0 FAIL, 0 SKIP | 37 file, jsdom và MSW |
| Vitest tập trung batch, analysis adapter, report và component | **57 PASS**, 0 FAIL, 0 SKIP | 5 file, nằm trong 426 ca ở trên |
| Playwright `scrum357.e2e.ts` | **5 PASS**, 0 FAIL, 6 SKIP | Chromium và Vite local. Sáu ca được bảo vệ bỏ qua theo điều kiện sẵn có của suite. Phiên Firebase được kiểm tra bằng đăng nhập trực tiếp trong trình duyệt. |
| Analysis .NET, lọc nhóm batch/report | **33 PASS**, 0 FAIL, 0 SKIP | PostgreSQL 17.11 cô lập trên Windows, migration thật, storage giả lập cho bài test report. Bao gồm snapshot đúng ca, quyền tải, PDF và 20 lượt đọc batch đồng thời. |
| Oxlint theo `npm run lint` | **PASS** | 0 lỗi, 3 cảnh báo sẵn có ở CampaignManagementPage |
| Typecheck và production build | **PASS** | `npm run build` chạy `tsc -b` trước Vite. Có cảnh báo kích thước chunk. |
| Kiểm tra cú pháp kịch bản local | **PASS** | `node --check e2e/scrum375-live.mjs` |
| `git diff --check` | **PASS** | Diff nhánh SCRUM-375 |

RTL kiểm tra backoff, làm mới sau mount/F5, batch thành công và lỗi, trạng thái chưa đối soát, chống GET chồng lấn, chặn retry phân tích và bỏ dữ liệu đã hiển thị khi mất quyền. MSW kiểm tra đường dẫn PDF theo ca, 401/403/404/429/500, MIME, định dạng nội dung và từ chối URL download ngoài Gateway. Kiểm tra snapshot PostgreSQL bao gồm liên kết ca/tài khoản, cấu trúc PDF và từ chối tải từ tài khoản khác.

## Tái kiểm thử trên Windows

1. Khởi động stack AURA TEST hiện có và xác nhận Gateway, Analysis và lưu trữ private đều sẵn sàng. Tra log Analysis/Gateway theo correlation ID ở trên nếu lỗi 503 lặp lại.
2. Từ thư mục `src/web/aura-web`, cấu hình Firebase Web TEST cho Vite, đặt `VITE_ENABLE_MOCK=false` và chạy `npm run dev -- --host 127.0.0.1 --port 5173`.
3. Chạy kịch bản bên dưới với batch tổng hợp thuộc tài khoản TEST. Nhập mật khẩu trực tiếp trong Chromium khi cửa sổ mở. Thay Batch ID nếu fixture cũ không còn trên stack. Kịch bản kiểm tra GET sau F5, route tạo PDF theo ID của ca, GET đúng report ID, MIME, cấu trúc PDF, header CSV và liên kết ca cùng SHA-256 trong manifest ZIP.

```powershell
node e2e/scrum375-live.mjs --email '<email Firebase TEST>' --batch '0958a867-613d-4940-a0b2-262820a6f712'
```

4. Sau khi có credit TEST hợp lệ, gửi ảnh tổng hợp qua luồng upload được hỗ trợ và kiểm tra reservation/settlement đúng một lần. Dùng một tài khoản TEST khác để xác nhận không đọc được báo cáo. Đối chiếu nội dung PDF với ca đã chọn trước khi chốt AC2.

## Tệp thay đổi

| Tệp | Phạm vi |
| --- | --- |
| `src/web/aura-web/src/pages/BatchProgressPage.tsx` | Trang batch, polling, FCM và hành động báo cáo |
| `src/web/aura-web/src/pages/BatchProgressPage.test.tsx` | Hồi quy trạng thái, polling, F5 và quyền |
| `src/web/aura-web/src/api/batchProgress.ts` | GET có hủy, backoff và nhận diện trạng thái cuối |
| `src/web/aura-web/src/api/analysisResult.ts` | Tái sử dụng helper gọi và tải nội dung xác thực |
| `src/web/aura-web/src/api/reportDownload.ts` | Xác minh report ID, URL và định dạng |
| `src/web/aura-web/src/api/reportDownload.test.ts` | Mock quyền truy cập và hợp đồng tải báo cáo |
| `src/web/aura-web/src/components/ReportDownloadButton.tsx` | Trạng thái tải, hủy, lỗi và cleanup Blob URL |
| `src/web/aura-web/src/components/ReportDownloadButton.test.tsx` | Kiểm thử click đồng thời, abort và cleanup |
| `src/web/aura-web/e2e/scrum375-live.mjs` | Kịch bản xác minh qua Gateway với đăng nhập thủ công |
| `src/services/AURA.Analysis.Api.Tests/ReportSnapshotPostgresTests.cs` | Kiểm thử binding snapshot, PDF và tài khoản khác |
| `docs/testing/SCRUM-375-batch-progress-reports.md` | Báo cáo nghiệm thu |
