# SCRUM-410 · Browser regression

**TC-W7-M5-03 · 25/11/2026 (UTC+7).** Code candidate `beca9c627556b15357c74032781e2a937685fac0`, tree `bd69ddec1d4efcaeb7b3028826b1962191bf033b`. Base `develop@e48b1b0dd38e67db6f2d05c78a3caac5f0553645`. [Draft PR #114](https://github.com/CattyGame/XD-PMHDT/pull/114).

## Required regression

| Gate | Kết quả | Chi tiết |
| --- | --- | --- |
| `npm test -- --maxWorkers=2` | PASS | 55 files, 540 test, 0 skip |
| `npm run lint` | PASS | Exit 0, 14 cảnh báo. Một cảnh báo `react(set-state-in-effect)` tại phần tiếp nhận file của `ClinicUploadPage` |
| `npm run build` | PASS | TypeScript, Firebase SW và Vite production |
| `npx playwright test --config playwright.m5-mock.config.ts` | PASS | 11 Chromium, 0 skip |
| `npx playwright test --config playwright.m5-browsers.config.ts` | PASS | 33 ca, 0 skip |
| CI tại SHA code | PASS | 8/8 checks GitHub, gồm React lint/test/build, M5 protected UI contract journeys, .NET, Python, repository, contracts, Postgres và Docker/Gateway |
| Tích hợp live đầy đủ theo SCRUM-397 | BLOCKED | Thiếu manifest xác nhận deployment trùng SHA/tree. Các smoke live thực thi được ghi riêng bên dưới |

Cấu hình mock chạy Playwright 1.65.0 một worker trên Windows x64, Node 24.21.0, npm 11.19.0. Production build dùng `VITE_ENABLE_MOCK=false` với Firebase fixture. Hai suite dùng `AURA_BATCH_MOCK_BASE_URL=http://127.0.0.1:4180` trỏ Vite preview của đúng build và chặn service workers để cô lập Firebase/API fixture. Firefox chưa có lúc bắt đầu và được cài riêng. Lượt khởi động Firefox trong sandbox treo. Lượt regression chính thức của ma trận chạy Firefox trong phiên Windows đang hoạt động và qua đầy đủ. [CI của candidate](https://github.com/CattyGame/XD-PMHDT/commit/beca9c627556b15357c74032781e2a937685fac0/checks).

| Project | Binary hoặc kênh xác minh | PASS | FAIL | SKIP |
| --- | --- | ---: | ---: | ---: |
| Chromium | Playwright Chromium revision 1254 | 11 | 0 | 0 |
| Google Chrome | Channel `chrome`, 158.0.8127.88 | 11 | 0 | 0 |
| Microsoft Edge | Channel `msedge`, 158.0.4370.33 | 11 | 0 | 0 |
| Firefox | Playwright Firefox 159.0, revision 1572 | 11 | 0 | 0 |

Mỗi project kiểm Doctor ở 390×844, 768×1024, 1024×900, 1366×768 và 1920×1080. Các ca reflow Login, Personal Upload, Batch lookup và Admin chạy ở 390×844 và 768×1024.

## Phạm vi đã kiểm

| Nhóm | Bằng chứng hiện tại |
| --- | --- |
| Login/logout, đổi tài khoản và Back | PASS mock Playwright và RTL. Tái sử dụng auth flow và session restore của SCRUM-311/312, cùng ca reset phiên từ SCRUM-376 |
| Upload, batch, F5, offline/reconnect | PASS mock. Tái sử dụng batch 100 ảnh, retry từng ảnh, lost ACK, hồi phục qua GET và assertion tránh submit lại từ SCRUM-332/334/374/376 |
| Analysis Result và null | PASS mock. Luồng clinician mở kết quả. F5 hiển thị lý do AVR null và ảnh riêng tư decode từ Blob. Trở lại khi quyền đổi sang deny xóa ảnh và nội dung. Back sau logout giữ ở Login. RTL kiểm giới hạn theo dõi trạng thái 5 phút và retry bằng GET |
| Doctor Review và Billing Sandbox | PASS mock. Tái sử dụng ca review, offline draft, 409 và quyền bị thu hồi từ SCRUM-354/355/356/357, cùng Sandbox thanh toán có idempotency |
| Export và tải file | PASS mock. Batch E2E bắt event download PDF với tên từ backend fixture. Tái sử dụng `ReportDownloadButton.test.tsx` và `reportDownload.test.ts` cho scope GET/POST, lỗi và quyền |
| Đổi clinic | PASS RTL mới. Clinic Upload xóa bệnh nhân cũ khi đổi clinic, yêu cầu lại quyền khi trở về, và không dùng lại tên/ID khi backend trả HTTP 403 |
| FCM receipt/click và current authorization | PASS tự động mức fixture. Tái sử dụng `useFcmNotification.test.tsx`, `FcmToastHost.test.tsx`, `firebaseMessagingWorker.test.ts`, `notificationNavigation.test.ts`, `notificationDevices.session.test.ts`. Gate real push/click trên deployment cần M2/M6 |
| Consultation Chat, revoke socket, nhiều tab, token expiry | BLOCKED cho candidate. Frontend Chat còn ở draft #104, backend/bản kiểm tổng hợp phụ thuộc SCRUM-391 và SCRUM-396 |

Hai nhóm thay đổi riêng được kiểm trước khi chạy full. `196a4c5` chặn dùng lại bệnh nhân sau đổi clinic với 6 targeted Vitest PASS. `beca9c6` bổ sung runner ba browser và nối ca clinician hiện có với assertion current authorization, kết quả sau F5, Blob URL và logout. Kịch bản mock còn tái sử dụng fixture không chứa ảnh bệnh nhân thật.

## Evidence Sprint 6 đã đối chiếu

| Bằng chứng | Trạng thái và phạm vi |
| --- | --- |
| SCRUM-389 `9e15fc8` | REUSED làm tham chiếu cho live private image, feedback/review version, simulated-source label và revoke trên checkout Sprint 6. Research layer, crop, geometry và provenance trên mẫu research live vẫn cần M4/M5 |
| SCRUM-393 `cad9ce0` | REUSED làm tham chiếu cho analytics, 409 version và audit trên checkout Sprint 6. Catalog, config, template và usage vẫn phụ thuộc M1 SCRUM-383 và M4 SCRUM-392 |
| SCRUM-394 `d744b6d` | REUSED làm tham chiếu cho hai tài khoản chat, nhận tin, read cursor và dọn UI khi revoke trên checkout Sprint 6. Live lost ACK, offline/reconnect gap, nhiều tab, token expiry và thời gian revoke socket cần M2 SCRUM-391, M6 SCRUM-396 và M5 |

[Evidence SCRUM-389](https://github.com/CattyGame/XD-PMHDT/blob/9e15fc8/docs/testing/SCRUM-389-local-integration-2026-11-20.md) · [Evidence SCRUM-393](https://github.com/CattyGame/XD-PMHDT/blob/cad9ce0/docs/testing/SCRUM-393-local-integration-2026-11-20.md) · [Evidence SCRUM-394](https://github.com/CattyGame/XD-PMHDT/blob/d744b6d/docs/testing/SCRUM-394-local-integration-2026-11-20.md).

## Live TEST cục bộ

Frontend production build `beca9c6` tại Vite preview 4181, Firebase TEST thực và Gateway loopback 5000. Dùng ba tài khoản TEST đã cấp và ca tổng hợp hiện có của SCRUM-389. Các phép đo chi tiết ở báo cáo SCRUM-409. [Web Vitals](SCRUM-409-web-vitals.md).

| Luồng | Trạng thái quan sát |
| --- | --- |
| Chrome Doctor, SuperAdmin, Patient | PASS đăng nhập. Firebase, bootstrap và profile đều HTTP 200 |
| Admin Portal và quyền | PASS Chrome SuperAdmin mở portal và API danh sách clinic HTTP 200. Patient bị từ chối trên Admin, đăng xuất và kiểm điều hướng yêu cầu đăng nhập lại |
| Analysis Result và ảnh riêng tư | PASS Chrome với dữ liệu tổng hợp đã seed. GET Result, API cấp quyền ảnh và bytes đều HTTP 200, ảnh Blob decode được. Edge và Firefox cũng đăng nhập Doctor, mở đúng Result, xác nhận ảnh decode và đăng xuất |
| Doctor Worklist và Review | FAIL live smoke hiện tại. Worklist trả HTTP 200 nhưng thiếu ca tổng hợp được chỉ định trong `items`. Chưa xác nhận Review từ Worklist trên deployment này. M5/M2 kiểm assignment và danh sách sau khi ghép SCRUM-389 với candidate |
| Tải PDF | FAIL live. Bấm Tải PDF trên ca tổng hợp, API report HTTP 503. Bản hợp đồng mock vẫn có assertion event tải file. M2 đối chiếu dịch vụ export với deployment mục tiêu |
| Upload mới đến AI COMPLETED | BLOCKED. Smoke Result dùng ca tổng hợp đã seed, danh sách service local chưa có worker hoàn tất upload mới. M1 xác minh pipeline trên candidate |
| Push và click thật | FAIL receipt trong smoke dev loopback 4183. Notification có bật cờ test push, nhưng luồng gửi/nhận hết thời gian chờ và chưa quan sát receipt. SKIPPED click thông báo hệ điều hành, BLOCKED gate tích hợp FCM. Các kiểm worker/click tự động vẫn PASS ở mức fixture. M2/M6 xác minh đăng ký thiết bị, nhận, click và quyền hiện hành |
| Consultation Chat | BLOCKED. PR #104 còn dạng draft. Giữ bằng chứng Sprint 6 riêng theo SHA gốc |
| Nghiệm thu theo SCRUM-397 | BLOCKED. Entry verifier trên code candidate kiểm 6 contracts, 42 migrations, danh tính SHA/tree sạch và 5 blocker còn mở. Docker chưa cung cấp revision đối chiếu cho các image đang chạy, trong đó Analysis thiếu image có thể inspect theo ID đang chạy |

Timeout do locator Result và nút PDF trong các lần thử đầu đã được xác minh lại với selector khớp giao diện. Bảng dùng kết quả của lần xác minh sau. Phạm vi smoke live hiện tại gồm ba tài khoản TEST. Nghiệm thu bốn vai trò, thay đổi quyền đang mở socket và luồng thông báo trên deployment cần candidate và fixture SCRUM-397/SCRUM-396.
