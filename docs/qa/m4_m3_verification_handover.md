# KIỂM CHỨNG KỸ THUẬT VÀ BÀN GIAO QA — M4

> **Dự án**: AURA
> **Mã Jira**: SCRUM-293
> **Người biên soạn ban đầu**: Đào Duy Quân — M4
> **Người phối hợp review**: M3 — QA; M1 — Tech Lead
> **Phiên bản tài liệu**: 1.1.0
> **Ngày cập nhật**: 2026-10-09
> **Trạng thái**: Đã kiểm tra một số thành phần cục bộ; còn công việc và kiểm thử tích hợp. Chưa ghi nhận ký duyệt của M3/M1.

## 1. Phạm vi và nguồn kiểm chứng

Tài liệu ghi nhận kết quả kiểm tra mã nguồn, dataset, contract,
AI mock, benchmark offline và NiFi ingestion.

Nguồn đã rà soát:
- Repository: https://github.com/CattyGame/XD-PMHDT
- PR #22: https://github.com/CattyGame/XD-PMHDT/pull/22
- Commit nguồn: `e434dd344ae40651bf689b1a3e8233f2150f9402`.

Các sửa đổi sau PR #22 được thực hiện trên nhánh sửa tiếp theo.
Người review cần ghi SHA thực tế của nhánh khi nghiệm thu.

Không coi PR đã merge, tài liệu đã lưu hoặc dry-run thành công
là bằng chứng toàn bộ tính năng đã được kiểm thử.

## 2. Kết quả đã kiểm chứng

| Thành phần | Kết quả | Giới hạn |
|---|---|---|
| Public Analysis contract | Validator PASS: REST 0.6.0; REST examples 20, event 4, job 1; embedded checks 54; negative checks PASS; 31 hash PASS | Chưa kiểm chứng backend hoặc phân quyền |
| AI test suite | Bộ 20 test hiện có chạy PASS | Không chứng minh mọi input hoặc luồng tích hợp đều đúng |
| Giới hạn ảnh | Ảnh vượt giới hạn bị từ chối trước load(); giới hạn runtime vẫn là 16 triệu pixel | Chưa kiểm thử tải lớn hoặc đồng thời |
| Kích thước đầu ra mock | Input/mask/overlay khớp tại 3000×100, 100×3000 và 999×960; mask PNG nhị phân L, overlay PNG RGB | Đầu ra giả lập, không phải phân vùng thật |
| Dataset CHASE_DB1 | ZIP kiểm tra được; 84 file khớp SHA-256 trong manifest; 28 ảnh, 56 mask, 14 đối tượng | Không xác nhận hiệu quả lâm sàng |
| Split | Train 16, validation 6, test 6; chia theo đối tượng | Dataset nhỏ |
| Frangi v0.2 | Đánh giá test với threshold 0.02; bốn metric trung bình tái lập trong sai số tuyệt đối < 0.0001 | Chưa đạt mục tiêu chất lượng; chưa tích hợp API |
| NiFi ingestion | Đã tiếp nhận JPEG thật, xác thực ảnh, lưu một số trường hợp sai vào quarantine và chuẩn bị multipart | Chưa gửi Gateway, chưa dedup/retry |
| Camera client dry-run | Chuẩn bị 7 tình huống, không gửi request | Không phải 7 test tích hợp PASS |

### 2.1. Kết quả Frangi test

| Metric trung bình theo ảnh | Giá trị |
|---|---:|
| Dice | 0.4200514032963332 |
| IoU | 0.26624005991059213 |
| Sensitivity | 0.4197821648456912 |
| Specificity | 0.9564996977653181 |

Kết quả chỉ áp dụng cho 6 ảnh test của 3 đối tượng:
01, 02 và 11.

Latency không nằm trong điều kiện so sánh tái lập.
Thời gian thuật toán cục bộ không chứng minh NFR toàn luồng.

### 2.2. Phân biệt contract

- Public Analysis REST: `contracts/analysis-openapi.yaml`, phiên bản 0.6.0.
- AI nội bộ: `contracts/ai_service_openapi.yaml` và bản JSON tương ứng.
- Event/job: schemaVersion 0.6.

Script sync OpenAPI kiểm tra sự thống nhất giữa file AI YAML và JSON.
Không dùng kết quả này để tuyên bố mọi schema runtime và nghiệp vụ
đã đồng bộ tuyệt đối nếu chưa có kiểm tra tương ứng.

## 3. Hiện trạng NiFi

Flow hiện có:
- ListenHTTP-CameraSimulator.
- EvaluateJsonPath-Metadata.
- RouteOnAttribute-Validation.
- ExecuteScript-ValidateImage.
- ExecuteScript-PrepareMultipart.
- UpdateAttribute-Quarantine.
- PutFile-QuarantinePersistence.
- Port PreparedMultipart-PendingGateway.
- Port QuarantineWriteFailure.

Multipart đã chuẩn bị có:
- patientId.
- modality.
- eyeSide.
- images.

Checksum và kích thước ảnh được giữ nguyên trong kiểm tra đã chạy.

Các attribute:
- gateway.idempotency_key.
- gateway.correlation_id.
- gateway.payload.ready.

Các attribute trên chưa chứng minh HTTP headers đã được gửi.

Chưa có hoặc chưa kiểm chứng:
- InvokeHTTP gửi Gateway.
- DistributedMapCache phục vụ dedup.
- Retry có giới hạn.
- Service token và quyền analysis:ingest.
- Backend tạo analysis từ multipart.
- Triển khai flow trên instance NiFi hoàn toàn mới.

HTTP 200 của ListenHTTP chỉ xác nhận tiếp nhận request,
không xác nhận xử lý thành công tại Gateway/backend.

### Kiểm tra import bổ sung — 2026-10-10

Đã import vào nhóm mới trong cùng instance NiFi và kiểm tra:
- JPEG thật đi đến queue multipart với trạng thái VALID/ready=true.
- Base64 sai được ghi vào quarantine/INVALID_BASE64,
  đối chiếu đúng request ID.

Đạt cho hai tình huống trên. Chưa kiểm chứng instance sạch,
Gateway forwarding, service token, dedup hoặc retry.
Chi tiết nằm trong docs/nifi/nifi_runbook.md.

## 4. Hướng dẫn kiểm tra lại

Chạy từ thư mục gốc repository.
Chuẩn bị virtual environment và dependencies trước khi chạy.

### 4.1. Public Analysis contract

```powershell
.\.venv-contract\Scripts\python.exe scripts/validate_analysis_contract.py
```

Tiêu chí:
- Validator exit code 0.
- Các nhóm kiểm tra PASS.
- 31 hash chuẩn hóa LF khớp baseline.

### 4.2. AI test suite

```powershell
$env:PYTHONPATH = (Join-Path (Get-Location) "src/ai-core")

.\.venv-dataset\Scripts\python.exe -m pytest src/ai-core/tests -q
```

Tiêu chí: toàn bộ test hiện có PASS.
Ghi số lượng test thực tế, commit và môi trường chạy.

Dependencies test cần có PyYAML để import module yaml.

### 4.3. AI YAML/JSON

```powershell
.\.venv-dataset\Scripts\python.exe src/ai-core/scripts/sync_openapi_contracts.py --check
```

Tiêu chí: script exit code 0 và xác nhận hai file thống nhất.
Kiểm tra này không thay thế kiểm thử runtime hoặc Worker.

### 4.4. Frangi test

Giải nén CHASE_DB1 theo README trước khi chạy:

```powershell
.\.venv-dataset\Scripts\python.exe src/ai-core/scripts/evaluate_frangi_test.py
```

Tiêu chí:
- Đọc 6 ảnh test theo manifest.
- Dùng threshold đã chốt 0.02.
- Không chọn lại threshold trên test.
- Bốn metric trung bình khớp báo cáo gốc trong sai số < 0.0001.
- Giữ nguyên báo cáo gốc; lưu báo cáo tái lập riêng.
- Không yêu cầu latency các lần chạy phải giống nhau.

### 4.5. NiFi

Thực hiện theo `docs/nifi/nifi_runbook.md`.

Tách riêng:
- Dry-run client.
- Kiểm thử tiếp nhận và quarantine.
- Kiểm thử multipart.
- Kiểm thử import.
- Kiểm thử gửi Gateway và backend.

Chỉ ghi PASS cho bước đã thực hiện và đối chiếu kết quả.
Không ghi dedup/retry PASS từ dry-run.

## 5. Đối chiếu các task M4

Bảng này là nhận định kỹ thuật theo phạm vi đã kiểm tra,
không tự thay thế acceptance criteria hiện hành trên Jira.

| Task | Phần đã có | Phần cần xác nhận |
|---|---|---|
| SCRUM-59 | CHASE_DB1, manifest, split, thông tin nguồn và giấy phép | Review bản data card mới và tài liệu liên quan |
| SCRUM-60 | Frangi chạy offline trên validation/test | Nếu AC yêu cầu ≥20 ảnh thật: hiện các báo cáo validation/test chỉ chứng minh 12 ảnh; cần bổ sung kiểm tra đúng phạm vi, không dùng 20 mẫu mô phỏng thay thế |
| SCRUM-61 | AI contract YAML/JSON và test schema | Review của M1/M2; mapping với Worker |
| SCRUM-62 | FastAPI mock, xác thực ảnh, mask/overlay đúng kích thước, test PASS | Build và smoke test container tại commit sửa mới nếu AC yêu cầu |
| SCRUM-63 | NiFi UI và ingestion đến multipart/quarantine | Gateway forwarding, dedup và retry nếu thuộc AC |
| SCRUM-64 | Metric Frangi thật và kiểm tra tái lập | Review báo cáo; NFR toàn luồng chưa kiểm chứng |
| SCRUM-290 | Cấu hình mạng, volume và flow ingestion | Import sạch, HTTP headers và kết nối backend thực tế |
| SCRUM-291 | Tài liệu review/risk đã chỉnh theo phạm vi thực tế | M1/M3 review bản mới |
| SCRUM-293 | Checklist và kết quả kiểm tra trong tài liệu này | M3 kiểm chứng độc lập; kết luận và xác nhận thực tế |

Không kết luận mọi task ĐẠT khi còn thiếu điều kiện hoặc xác nhận.

## 6. Công việc còn lại

- Chốt và kiểm thử mapping WARNING, LOW_QUALITY và lỗi AI–Worker.
- Kiểm tra lưu mask/overlay thành file riêng tư và trả fileId/kind.
- Sửa workflow CI và kiểm tra pipeline.
- Bổ sung route Gateway cho batches theo contract.
- Backend triển khai endpoint và service token cần thiết.
- Kiểm thử import NiFi.
- Triển khai và kiểm thử Gateway forwarding, idempotency, retry.
- Đo hiệu năng toàn luồng sau tích hợp.
- Hoàn thành review tài liệu và acceptance criteria cùng M1/M3.

Mỗi mục phải có người phụ trách và task/PR liên quan.
Không mặc định toàn bộ công việc backend, Gateway hoặc CI thuộc M4.

## 7. Ghi nhận review và nghiệm thu

Khi review thực tế, ghi:
- Người kiểm tra và ngày kiểm tra.
- Commit/PR hoặc phiên bản tài liệu.
- Môi trường và lệnh kiểm tra.
- Kết quả đạt, không đạt hoặc bị chặn.
- Phần được nghiệm thu và phần còn lại.

Trạng thái xác nhận:
- M4: phụ trách bàn giao kỹ thuật; chờ xác nhận trên bản cập nhật.
- M3: chờ kiểm chứng độc lập và kết luận QA.
- M1: chờ review phạm vi và quyết định nghiệm thu.

Tài liệu này không ghi nhận chữ ký hoặc phê duyệt thay cho các thành viên.