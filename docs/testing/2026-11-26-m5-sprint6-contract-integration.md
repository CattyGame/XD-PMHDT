# Sprint 6 M5 · Kiểm thử tích hợp hợp đồng

## Cập nhật ngày 27/11/2026

- **Candidate M5** `456734d164f686f97d0238a99da1ec8fa4f486d7`, tree `ce50963905aad8ca92ba1244894cf1400583ece3`. Ứng dụng được build và chạy Vitest ở `8476057`. Hai commit có cùng tree mã ứng dụng `c4aafffaa72b2bb04491d97fbd425b2105071904`. Commit sau thay fixture Playwright và tăng assertion giải mã ảnh. [Draft PR #115](https://github.com/CattyGame/XD-PMHDT/pull/115) · [CI candidate](https://github.com/CattyGame/XD-PMHDT/commit/456734d164f686f97d0238a99da1ec8fa4f486d7/checks).
- **SCRUM-393 · PASS frontend.** Đã cập nhật từ `develop@4fcc312` và giải quyết xung đột portal trong `e6baafe`. Giá công bố xác nhận receipt `ACTIVE`. Lệnh ngừng bán gửi UUID, kiểm đúng ID và số phiên bản của giá hiện tại và thử lại cùng key/body sau mất phản hồi. 29 Vitest Billing/Admin mục tiêu qua. Hai endpoint catalog đã có trong mã sau khi merge [PR #117 của M1](https://github.com/CattyGame/XD-PMHDT/pull/117).
- **SCRUM-389 · PASS frontend.** `8476057` giải mã ảnh PNG/JPEG có xác thực trước khi tạo BlobURL, đóng bitmap tạm và kiểm lại abort. Ca lớp PNG hỏng xác nhận chỉ ảnh hợp lệ được cấp BlobURL. 31 Vitest Doctor/lịch sử mục tiêu qua. Lượt Playwright đầu phát hiện fixture PNG cũ sai CRC ở chunk IDAT. `456734d` thay bằng PNG tổng hợp hợp lệ và kiểm `naturalWidth > 0`. Sau sửa, 8 ca liên quan qua ở Chromium. Bộ mục tiêu cho Doctor, Billing và Chat qua đủ 33/33 ở Chrome, Edge và Firefox với production preview loopback.
- **SCRUM-394 · PASS mock ở bản ghép tạm.** Kiểm trực tiếp 3/3 ca Chat/FCM trên source tree `11a8b5a520f94f26457a85ae015645d7ef3515c2`, ghép candidate M5 với nhánh M6 `d1616a96` trong thư mục tạm. Luồng mở hội thoại từ thông báo kiểm quyền hiện tại và chặn quyền đã bị thu hồi. API/Firebase được mô phỏng. Bản ghép phục vụ kiểm thử, PR #115 giữ lịch sử riêng.
- **Gate nội bộ.** 566/566 Vitest, lint và production build qua. Chạy lại `Run-Checks.ps1` đạt 65/65 bài kiểm cấu hình, gồm kiểm plan đủ 43 migration. CI .NET, React lint/test/build và M5 protected UI contract journeys trên candidate qua. Bốn bài kiểm công cụ entry gate qua. Mốc lỗi migration ở lượt 26/11 thuộc lịch sử.
- **REUSED · M1 và M4.** Báo cáo M1 xác nhận Billing trên DB fixture và HTTP TestServer, gồm checkout snapshot và validation schema phản hồi. M4 đã công bố smoke Uvicorn với promotion, lượt inference thực và rollback tại `d0c8a95`, nay có trong develop. Hai bằng chứng này có phạm vi dịch vụ và môi trường của từng tác giả. [M1](./m1_sprint6_completion_20261127.md) · [M4](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-392?focusedCommentId=10228).
- **REUSED · M6.** Trên nhánh M6 `d1616a96`, báo cáo kết quả 17/17 ca cross-service với DB fixture. Socket đóng sau 428 ms khi thu hồi assignment, 846 ms khi thu hồi membership và 836 ms khi vô hiệu account. FCM upstream dùng test double. [Kết quả SCRUM-396](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-396?focusedCommentId=10235).
- **BLOCKED · nghiệm thu trên TEST tích hợp.** Chưa có URL và manifest deployment SCRUM-397 cho các ca M5. Cần kiểm doctor research và revoke, frontend catalog/history/usage/config, và Chat/FCM trên cùng candidate. FCM nhận và mở thông báo trên thiết bị thật còn cần xác minh. [SCRUM-397](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-397).

## Lượt ngày 26/11/2026

26/11/2026 (UTC+7) · Nhánh `fix/m5-sprint6-contract-integration` · [Draft PR #115](https://github.com/CattyGame/XD-PMHDT/pull/115)

- Baseline triển khai `develop@223646866601fe487c9c93bf802b163ad85753f3`. Đã cập nhật nhánh từ `develop@c70113a6917be44093816334f978707c320910b8` trước lượt Playwright cuối.
- Candidate ứng dụng `7c49c7b3b0e3a4f0f2c43688b828c54215c842f5`, tree `19b84a289d4528da5a6213f3ff44b635055310fa`. Vitest, lint và build chạy ở `992ce40f1b2307193928d04fb5e6b3b97988b6c5`. Hai commit có cùng frontend tree `74e262d51333d187eaa59eb0f3284aaf848dda02`.
- Candidate sau khi chỉnh fixture `149e2fc65ee5188bf99904cffa0c5060a4efce94`, tree `092a1790be6a28c89965a15e6f795a92776bc376`. Chỉ thay đổi fixture E2E batch. `src/web/aura-web/src` ở cả hai candidate có cùng tree `c16cf7f7baa9f7ef69fa84b1c9ae1ebe6b8327b6`. Commit báo cáo được tạo sau candidate.

## SCRUM-393 · Billing, quản trị và mẫu FCM

- `063da03` sửa `src/api/sprint6Portal.ts`, các trang trong `src/pages/portal/`, adapter test và assertion catalog E2E. Lịch sử ví cá nhân gửi `ACCOUNT` và `accountId`. Đơn hàng, ledger và tổng thanh toán theo DTO backend. Đơn hàng và ledger phân trang độc lập. Tổng tiền theo nhóm tiền tệ và môi trường, dùng đơn vị chính. Owner change huỷ yêu cầu và bỏ phản hồi đến muộn.
- Lệnh giá gửi `expectedVersion` và UUID `Idempotency-Key`. Retry sau mất ACK dùng cùng key và body. `e571448` lưu bản nháp trong phiên khi gặp 409, tải version hiện hành rồi đợi người dùng bấm lưu để tạo lệnh mới. Mẫu thông báo đọc `id`, dùng event và lựa chọn nội dung trong allowlist backend.
- **FAIL trước sửa** · Sáu adapter test tái hiện lệch hợp đồng. Test mở rộng cho lỗi 409 tái hiện mất bản nháp sau khi tải lại.
- **PASS sau sửa** · 27 Vitest mục tiêu. Đối chiếu 9 trường hợp hợp lệ hoặc sai kiểu từ fixture tổng hợp với schema OpenAPI Billing và Notification bằng Swagger Parser và Ajv. Playwright kiểm version, UUID và tải lại danh mục sau 409.
- **BLOCKED live** · M1 cần cung cấp `GET /api/v1/admin/billing/packages` và `PATCH /api/v1/admin/billing/packages/{packageId}/status`. Handler hiện có của Billing cung cấp hai POST giá, GET summary, orders và usage. Cần TEST có ví và ledger tổng hợp, kiểm đọc/ghi danh mục và phiên bản, ngừng bán, mẫu bật/tắt tới lần gửi FCM thật và xác minh kích hoạt cấu hình trên inference cùng M4.

## SCRUM-389 · Doctor Workspace

- `e6c08b0` sửa `src/pages/doctor/DoctorWorkspacePage.tsx` và test liên quan. Tải worklist huỷ yêu cầu chi tiết đang chạy và xóa trạng thái tải cùng lỗi của ca trước.
- **FAIL trước sửa** · Test hồi quy xác nhận thông báo “Đang tải chi tiết ca…” vẫn xuất hiện sau khi signal bị abort và worklist mới đã tải xong.
- **PASS sau sửa** · 30 Vitest mục tiêu cho Doctor Workspace, xem lớp ảnh, protected file access và lịch sử bệnh nhân. Suite hiện có kiểm phân vùng Grad-CAM crop, provenance, feedback theo version, ảnh BlobURL, 409/revoke và AVR null kèm lý do.
- **BLOCKED live** · Cần case research tổng hợp có ảnh gốc và các layer đúng geometry/version, thông tin provenance và quyền có thể thu hồi trên candidate SCRUM-397. M4 cung cấp case research. M6 xác nhận revision triển khai và scope cho bác sĩ kiểm thử.

## SCRUM-394 · Consultation Chat

- `992ce40` sửa `src/hooks/useConsultationThread.ts` và mở rộng luồng Chat sẵn có tại `e2e/m5-workflows.mock.e2e.ts`. Tin nhắn đến trong lúc ngắt kết nối được lấy đủ trang ngay cả khi lần tải trước xác nhận thread trống. Lần mở thread có lịch sử vẫn phân trang bình thường.
- **FAIL trước sửa** · Với 23 tin trong lúc mất kết nối, Playwright thấy tin thuộc trang mới nhất và thiếu tin đầu ở trang cũ.
- **PASS sau sửa** · Hai luồng Chat Playwright mục tiêu. Kịch bản reconnect kiểm thread trống và thread có lịch sử với hơn 20 tin mỗi lần, gửi lại cùng `clientMessageId`, dedup message, read cursor, draft offline và xóa thread khi nhận `AccessRevoked`. Đã đối chiếu `JoinConversation(conversationId)` với Hub. Gửi và đánh dấu đọc tiếp tục dùng REST có xác thực.
- **BLOCKED live** · M2 và M6 cần chốt TEST có hai tài khoản và đủ quyền. Cần đo tiếp nhận tin, mất ACK, hai tab, token hết hạn, backend deny và đóng socket trong ≤5 giây khi thu hồi quyền. Kết quả mock xác minh dọn giao diện khi nhận event.

## Gate và cấu hình

| Gate | Trạng thái | Bằng chứng |
| --- | --- | --- |
| `npm test -- --maxWorkers=2 --exclude 'test-results/**'` | PASS | 563/563, 57 files. Exclude thư mục artifact local có bản baseline lưu cùng test, CI dùng checkout sạch |
| `npm run lint` | PASS | Exit 0, có cảnh báo React của linter |
| `npm run build` | PASS | TypeScript, Firebase service worker và Vite production |
| `npx playwright test --config playwright.m5-mock.config.ts` | PASS | Chromium 15/15 |
| `npx playwright test --config playwright.m5-browsers.config.ts` | PASS sau kiểm lại ca bị ảnh hưởng | Lượt đầy đủ Chrome 15/15, Edge 15/15, Firefox 14/15. Ca batch đạt 4/4 khi chạy lại riêng trên Chromium, Chrome, Edge và Firefox sau chỉnh fixture |
| GitHub Actions tại candidate ứng dụng | PASS frontend, FAIL .NET | React lint/test/build và M5 protected UI contract journeys đều qua. `.NET build` lỗi ở `Check development configuration scripts`, dẫn đến Docker and Gateway checks bị SKIPPED. [Checks candidate ứng dụng](https://github.com/CattyGame/XD-PMHDT/commit/7c49c7b3b0e3a4f0f2c43688b828c54215c842f5/checks) · [Checks sau chỉnh fixture](https://github.com/CattyGame/XD-PMHDT/commit/149e2fc65ee5188bf99904cffa0c5060a4efce94/checks) |
| HTTP integration và browser live theo SCRUM-397 | BLOCKED | Chưa có URL và manifest TEST xác nhận candidate này. Docker Desktop Linux engine cục bộ chưa chạy |

Windows x64, Node 24.21.0, npm 11.19.0 và Playwright 1.65.0 với một worker. Chromium revision 1254, Chrome channel 158.0.8127.88, Edge channel 158.0.4370.33 và Playwright Firefox 159.0 revision 1572. Playwright dùng production preview loopback, `VITE_ENABLE_MOCK=false`, cấu hình Firebase fixture, route HTTP/WebSocket tổng hợp và chặn service worker. Doctor chạy ở 390×844, 768×1024, 1024×900, 1366×768 và 1920×1080. Catalog và Chat có luồng mobile. Không dùng dữ liệu bệnh nhân thật.

`149e2fc` ổn định ca hồi quy batch của SCRUM-410. Fixture giữ một PUT chưa được lưu trong lúc PUT thứ năm được lưu nhưng mất ACK. Lượt Firefox ban đầu chỉ có PUT mất ACK thất bại, khiến locator cho PUT chưa lưu hết thời gian chờ. Lượt kiểm lại vẫn xác nhận phục hồi cùng lượt gửi, một PUT cho file đã lưu mất ACK, không gửi lại sau F5 và giới hạn 4 request đồng thời.

Lỗi CI .NET xuất hiện trên cả PR và `develop@c70113a`. `Initialize-AuraDevDatabase.ps1 -PlanOnly` trả 43 mục, có cả migration catalog/FCM 021 và migration audit 022 mới của SCRUM-418. `Run-Checks.ps1:120` còn đòi đúng 42 mục. Chuyển lỗi sang người phụ trách SCRUM-418 để đồng bộ assertion migration và chạy lại Docker/Gateway. [Job PR](https://github.com/CattyGame/XD-PMHDT/actions/runs/45841648011/job/139984730147) · [Job develop](https://github.com/CattyGame/XD-PMHDT/actions/runs/45839042188/job/139975496805).

**REUSED, bối cảnh lịch sử.** Các báo cáo tích hợp [SCRUM-389](./SCRUM-389-local-integration-2026-11-20.md), [SCRUM-393](./SCRUM-393-local-integration-2026-11-20.md) và [SCRUM-394](./SCRUM-394-local-integration-2026-11-20.md) ghi các lần chạy trước cùng SHA của chúng. Dùng để chọn ca hồi quy và đối chiếu điều kiện TEST.

**Bàn giao.** M6 cung cấp URL TEST và manifest có SHA, image và profile. M1 xác nhận hai route catalog. M4 chuẩn bị ca research và bằng chứng cấu hình làm đổi inference. M2/M6 chuẩn bị luồng FCM và hai tài khoản Chat. M5 chạy lại đúng các case live tương ứng rồi đưa từng Jira ra xét Done.
