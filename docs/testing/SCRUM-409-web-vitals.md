# SCRUM-409 · Web Vitals và hành trình ba click

**TC-W7-M5-02 · 25/11/2026 (UTC+7).** [Draft PR #114](https://github.com/CattyGame/XD-PMHDT/pull/114).

## Hành trình clinician

**PASS (Playwright mock).** Bắt đầu khi tài khoản DOCTOR đã đăng nhập và hồ sơ bệnh nhân đã mở tại `/clinics/patients`. Luồng trước đây điều hướng qua trang upload, bỏ qua `patientId` đã được truyền, và cần thêm click qua batch để mở kết quả. Luồng hiện tại có ba thao tác.

1. Click **Chụp / Gửi ảnh phân tích** để mở hộp chọn tệp. Playwright mô phỏng thao tác chọn PNG trong hộp hệ điều hành.
2. Click **Gửi ảnh phòng khám** sau khi trang gán bệnh nhân đã được kiểm tra quyền lại ở API.
3. Click **Xem kết quả phân tích**. Với một ảnh do backend tiếp nhận ở trạng thái QUEUED, link mở đúng analysis và clinic. Trang đọc trạng thái từ API và tự theo dõi tới khi có kết quả hoặc hết thời hạn theo dõi.

Test xác nhận bearer và clinic, patient ID trong manifest, bytes PNG trong PUT, một lần finalize, và trạng thái chưa có kết quả AI. Quy trình chọn bệnh nhân, xác nhận gửi, xử lý retry/restore và điều hướng batch vẫn dùng cơ chế hiện có. Handoff tệp nằm trong React state của hai route bảo vệ và được hủy theo phiên. Quyền truy cập cuối vẫn do backend kiểm tra.

## Production bundle

Windows x64, Intel Core i7-1065G7, 8 luồng, RAM 8 GiB. Node 24.21.0, npm 11.19.0, Vite 8.3.3. Một build production mỗi SHA bằng `npm run build`, `VITE_ENABLE_MOCK=false`, Firebase fixture. Đo file minified trong `dist/assets`. Tải ban đầu gồm JS/CSS được liên kết trong `dist/index.html`, kể cả modulepreload. Cột gzip cộng các asset đã nén riêng. Mạng/cache không tham gia phép đo byte.

| Chỉ số | Trước · `547f355` | Sau · `26cbed1` |
| --- | ---: | ---: |
| JS ban đầu, kB | 668,66 | 535,38 |
| JS ban đầu gzip, kB | 191,63 | 159,34 |
| CSS ban đầu, kB | 58,25 | 22,14 |
| CSS ban đầu gzip, kB | 12,19 | 4,74 |
| Toàn bộ JS, kB | 668,66 | 678,25 |
| Toàn bộ JS gzip, kB | 191,63 | 208,64 |
| Chunk JS lớn nhất, kB | 668,66 | 381,27 |
| Chunk riêng Result, kB | Gộp trong entry | 12,33 (4,39 gzip) |

Baseline `547f35592f1255c6bd034b056129eed212e0a6a6`, tree `d516826260b78d84b3461328f298cf70e9c089f9`. Code sau tối ưu `26cbed196f5f42790db67802eed92aa2bf95ae1d`, tree `77ee9ef5026b51327bbd131f04f56bd0cbb72636`. JS tải ban đầu giảm khoảng **20%**. Tổng gzip tăng vì chia chunk và thêm mã xử lý luồng. Login và trang chủ tải sẵn. Route lớn tải theo nhu cầu, có trạng thái chờ và nút tải lại khi import thất bại.

## Ảnh và phiên

- **PASS (RTL)** — Kết quả tải preview thứ nhất trước. Request cấp quyền và bytes cho các ảnh phụ chỉ bắt đầu khi preview cách viewport tối đa 200 px (hoặc tải ngay nếu thiếu IntersectionObserver). Giữ nguyên original, mask, hình học và hash. Tái dùng chống trùng theo file ID và GET có `cache: 'no-store'`.
- **PASS (RTL)** — Blob URL bị thu hồi tại hạn quyền thực tế, kể cả khi tải bytes tốn thời gian. Khi viewer unmount hoặc đổi scope, yêu cầu đang chạy bị abort và Blob URL được thu hồi. Viewer gắn key với UID, account, profile version, clinic và analysis.
- **PASS (targeted)** — `ApplicationRoutes.test.tsx` 17 ca. Sau cập nhật luồng clinician, 25 ca ở `ApplicationRoutes`, `ClinicPatientPage` và `ClinicUploadPage`. `AnalysisResultPage` và `analysisResult` 25 ca. Playwright chromium luồng ba click 1 ca.

## Đo trên Gateway TEST cục bộ

| Chỉ số | Trước · `547f355` | Sau · `beca9c6` |
| --- | --- | --- |
| Dashboard p95 < 3 s | 3.490 ms · FAIL mục tiêu cục bộ | 2.970 ms · PASS mục tiêu cục bộ |
| Dashboard LCP p95 < 1,8 s (stretch) | 3.076 ms · FAIL | 2.704 ms · FAIL |
| Result p95 < 3 s | 5.346 ms · FAIL | 5.207 ms · FAIL |
| Result LCP p95 < 1,8 s (stretch) | 2.860 ms · FAIL | 2.612 ms · FAIL |

Build production trước và sau dùng cùng Firebase TEST và Gateway `127.0.0.1:5000` qua Vite preview loopback. Google Chrome 158.0.8127.88 headless, viewport 1366×768, cùng máy Windows ghi ở trên, mạng máy hiện hành, không throttle mạng/CPU. Khởi tạo phiên DOCTOR trước phép đo, warm-up mỗi route một lượt rồi giữ cache HTTP ấm. Mỗi mẫu bắt đầu từ full navigation. Dashboard kết thúc khi heading danh tính được cấp quyền xuất hiện. Result kết thúc khi UI hiển thị trạng thái COMPLETED của ca tổng hợp cố định và ảnh gốc riêng tư decode từ Blob. LCP lấy từ PerformanceObserver kể từ navigation, đọc ở 250 ms sau assertion readiness. p95 là mẫu thứ 29 của 30 giá trị đã sắp tăng. Các GET bảo vệ vẫn dùng `no-store`. Tổng cộng 120 mẫu readiness. Chuỗi đo nằm trong `test-results/scrum7-live-evidence.json` và `test-results/scrum7-live-result-evidence.json` của frontend checkout.

**Nghiệm thu performance live Sprint 7 · BLOCKED.** Các image backend hiện chạy thiếu thông tin revision để ghép với SCRUM-397. Phép đo cục bộ dùng Gateway thật, ca TEST đã seed từ Sprint 6 và kết quả có nhãn `isMock=true`. Dashboard đạt mục tiêu cục bộ với biên 30 ms. Result vẫn vượt mục tiêu 2.207 ms khi tính đến lúc ảnh gốc sẵn sàng. Để nghiệm thu, dùng deployment HTTPS đã chốt manifest, lấy timing riêng từ navigation → kết quả → cấp quyền ảnh → bytes/decode và chạy lại cả hai build trên profile lạnh/giới hạn như 150 ms RTT, 1,6 Mbps tải xuống và 0,75 Mbps tải lên. Ghi các image/model/config digest. Candidate đo sau là `beca9c627556b15357c74032781e2a937685fac0`, tree `bd69ddec1d4efcaeb7b3028826b1962191bf033b`.
