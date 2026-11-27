# M1 Sprint 6 — bản sửa và kết quả kiểm tra ngày 27/11/2026

## Phạm vi bàn giao

- Nhánh: `fix/m1-sprint6-integration`.
- Nền: `develop@4621a53fe7763945f43fba543742e58261e3d7c8`, đã có PR #116 của M4; bản sửa này không thay đổi AI, dataset hoặc nhãn chuyên gia.
- Yêu cầu Jira: [SCRUM-380](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-380), [SCRUM-381](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-381), [SCRUM-382](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-382), [SCRUM-383](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-383).
- Tự review và thực thi tuần tự, không dùng subagent. Bàn giao mã nguồn để review; chưa merge `develop` hoặc chuyển Jira Done.

## Đối chiếu theo task

| Task | Kết quả trong bản sửa này | Bằng chứng và giới hạn |
| --- | --- | --- |
| 380 — Audit dùng chung | Giữ append-only, phân quyền theo service, outbox/projection; ngừng gói phải commit cùng audit hoặc rollback toàn bộ | Suite M1 chạy thật DB: audit contract/persistence/delivery/HTTP/mutation/upgrade đều đạt; test mới thu hồi quyền INSERT audit và kiểm ngừng gói không được commit |
| 381 — Camera/NiFi | Không thay flow hoặc registry/grant; giữ signed principal, clinic binding, revoke, audience/scope và key rotation | CameraGrantTests, GatewayHeaderTests và 3 kiểm tra flow đạt; drill NiFi phục hồi queue dùng bằng chứng 22/11, không gọi là drill mới ngày 27/11 |
| 382 — TLS camera | Giữ nguyên profile TLS và runbook đã bàn giao | Các file liên quan không đổi so với `d2cff5a`; kế thừa drill 22/11 với CA/hostname/plaintext negative controls. Chưa chạy lại toàn bộ stack NiFi/TLS trong phiên sửa này |
| 383 — Billing/FCM | Bổ sung danh mục quản trị, ngừng gói, summary theo owner; sửa caller UI theo DTO thực; giữ template generic và delivery có retry | 48 test M1, 31 test Billing, schema phản hồi thực, frontend và browser mock đều đạt; không gửi FCM/provider thanh toán thật trong phiên này |

AES-256 và độ trễ resolver của Sprint 7 không được dùng làm tiêu chí mới để đánh giá Sprint 6.

## Hành vi đã hoàn thiện

### Danh mục và giá

- `GET /api/v1/admin/billing/packages`: phân trang keyset, chỉ current SYSTEM_ADMIN, trả phiên bản mới nhất của mỗi gói, kể cả gói ngừng bán; `Cache-Control: no-store`.
- `PATCH /api/v1/admin/billing/packages/{packageId}/status`: chỉ nhận `RETIRED`, bắt buộc `expectedVersion`, lý do và `Idempotency-Key` UUID. Sai phiên bản hoặc dùng lại key với payload khác trả 409.
- Ngừng bán giữ nguyên ID/giá/credit của price version; không tăng số phiên bản giá. Muốn bán lại phải công bố price version mới. Chỉ số `version` trong receipt ngừng bán là phiên bản giá vừa ngừng, không phải revision chung của mọi thay đổi catalog.
- Dùng chung advisory transaction lock và bảng `billing.catalog_publications` với lệnh công bố giá. Phát lại lệnh ngừng trả receipt ban đầu, không ngừng nhầm phiên bản mới và không ghi audit lần hai. Key cũng không được tái sử dụng giữa công bố giá và ngừng bán.
- Gói ngừng bán không nhận đơn mới; đơn đã tạo vẫn thanh toán theo snapshot cũ, kể cả callback phát lại. Race công bố giá/ngừng bán được kiểm tra bằng PostgreSQL.
- Receipt công bố giá bổ sung `status: ACTIVE`; các trường cũ được giữ.

### Lịch sử, usage và báo cáo

- Caller cá nhân dùng `ownerType=ACCOUNT`; phòng khám dùng `CLINIC`. Không tự thêm environment hoặc quyền owner từ giao diện.
- Orders đọc `id`, `amount`, `currency`, `priceVersionId` thực. Tiền từ history/summary là đơn vị tiền chính; `amountMinor` của quản trị catalog là đơn vị nhỏ nhất.
- Usage hiển thị các dòng ledger, delta có dấu, balanceAfter, reservationUnits và cursor. Không suy diễn tổng số dư hoặc tổng tiêu thụ toàn kỳ từ một trang.
- `GET /api/v1/billing/summary` kiểm current `BILLING_READ` và tenant của owner; runtime environment do server quyết định. Chỉ đếm một lần đơn COMPLETED có payment SUCCESS khớp amount/currency trong khoảng `[from, to)`.
- Summary tách currency/environment; UI không dựng doanh thu ròng/hoàn tiền khi producer không có trường tương ứng.

### Lưu thay đổi và template

- Tạo gói mới gửi `expectedVersion=0`; sửa giá gửi version hiện tại. Form giữ một key theo payload để gửi lại sau lỗi mất phản hồi/503; không tự tạo gói thứ hai bằng key mới khi người dùng chỉ thử lại.
- Khi 401/403/409 hoặc response sai hợp đồng, dữ liệu/form bị chặn và yêu cầu tải lại; lỗi tạm thời giữ form để thử lại cùng intent. Unmount vẫn abort request.
- Template đọc trường `id` và eventType thực từ Notification. UI chỉ cho chọn title/body trong allowlist generic hiện có; không nhập dữ liệu bệnh nhân vào push. Suite kiểm tra template cuối dùng khi delivery và khả năng tắt gửi.

Không cần migration mới: bảng idempotency hiện có đủ lưu command hash/price version; không sửa migration cũ hoặc snapshot/ledger đã ghi.

## Kết quả chạy thực tế

Fixture riêng, loopback, dữ liệu tổng hợp: PostgreSQL 17, RabbitMQ 4, Azurite 3.33.0. Không dùng DB dự án đang chạy để test. Các kết quả HTTP dùng production endpoint/repository với TestServer; authority upstream là fixture kiểm soát được quyền, không phải phiên Google thật.

| Kiểm tra | Kết quả ngày 27/11 |
| --- | --- |
| `dotnet test tests/AURA.M1.RegressionTests/AURA.M1.RegressionTests.csproj` với đủ PG/Rabbit/Blob | 48 passed, 0 failed, 0 skipped |
| `dotnet test src/services/AURA.Billing.Api.Tests/AURA.Billing.Api.Tests.csproj` với DB test riêng | 31 passed, 0 failed, 0 skipped |
| `create_candidate_manifest.py --verify-trx` trên hai TRX cuối | PASS, không tính các lần RED hoặc fixture lỗi trước đó là bằng chứng đạt |
| `node scripts/validate_billing_responses.cjs <AURA_M1_ARTIFACTS>` | 8 response thật khớp schema Billing/Notification; negative control xóa trường bắt buộc bị từ chối |
| `npm test -- --maxWorkers=2` | 57 file, 563 test passed |
| `npx playwright test --config playwright.m5-mock.config.ts` | 15 passed; mock HTTP, không phải nghiệm thu full stack hoặc FCM thật |
| `npm run build` | PASS |
| `npm run lint` | Exit 0; còn warning trong frontend, không gọi là lint sạch warning |
| `dotnet build AURA.sln --configuration Release --no-restore` | 0 error, 27 warning |
| Initializer fresh và reapply | 43 migration mỗi lần, PASS |
| `test_camera_flow.py`, `test_audit_registry.py`, `test_m1_harnesses.py` | Lần lượt 3, 2, 8 test passed |
| `python scripts/validate_openapi.py` | PASS 42 route Analysis; Billing được kiểm riêng bằng response validator nêu trên |
| YAML parse workflow/contract, `git diff --check` | PASS |

Workflow bắt buộc `adr005-postgres.yml` đã thêm xuất `AURA_M1_ARTIFACTS` và chạy response validator. Thiếu sample/schema hoặc sai DTO phải fail, không dùng file response tự điền để làm CI xanh.

Billing OpenAPI v0.5.0 SHA-256: `f3d40ee424ff05653333edabcf6c157abeb9350d574614728132b417200db5e3`.

Các log local nằm trong `.superpowers/m1-sprint6-fix-20261127/` (ignored), gồm RED/GREEN và kết quả cuối; không commit cấu hình fixture chứa mật khẩu. Bốn tài liệu đang có thay đổi của người dùng đã kiểm SHA-256 không đổi, không đưa vào bản sửa M1.

## Bằng chứng camera được kế thừa

Drill ngày 22/11 tại `D:/XD-PMHDT/.review-evidence/m1-sprint6-integration-20261122/live-camera-drill.json`: real NiFi/Identity/Gateway/Analysis/Profile/Billing/Blob/Reporting, ảnh synthetic và TEST credit; không AI worker/payment provider.

Đã chứng minh cùng FlowFile UUID/hash sau restart, replay không thêm job/reservation/receipt, Reporting outage giữ nghĩa vụ rồi phục hồi, retry budget đưa HOLD, replay có xác thực, revoke không tạo receipt, direct DB write bị chặn và TLS CA/hostname/plaintext negative controls.

Flow SHA-256: `d7243d100c588be6bfa71e714ea6586fbdba4e7f740909cd5c5d6ad1718f682f`. `infra/nifi`, `compose.camera-tls.yaml`, runbook TLS và `CameraDelegationEndpoints.cs` không đổi giữa source drill `d2cff5a` và nền bản sửa này.

Nếu đổi certificate/hostname/flow/profile triển khai, cần chạy lại drill tương ứng; kết quả kế thừa không xác nhận cấu hình máy triển khai khác.

## Kết luận bàn giao

Các lỗi thuộc phạm vi M1 Sprint 6 đã được sửa và kiểm tra trong môi trường fixture. Bản sửa sẵn sàng review cùng caller M5; chưa có kết quả CI remote cho commit bàn giao, chưa merge và chưa tự thay trạng thái Jira. Ngày test và phạm vi mock/native/historical được giữ riêng để reviewer quyết định nghiệm thu chính xác.
