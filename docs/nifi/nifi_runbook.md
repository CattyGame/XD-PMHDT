# Runbook NiFi Camera Ingestion — AURA

- Công việc: SCRUM-63, SCRUM-290.
- Phiên bản tài liệu: 1.2.0.
- Apache NiFi: 1.27.0.
- Contract đích: `contracts/analysis-openapi.yaml`, phiên bản 0.6.0.
- Flow: `infra/nifi/camera_ingestion_flow.json`.
- Trạng thái: đã kiểm tra nhận dữ liệu, validation, quarantine và tạo multipart; chưa hoàn tất tích hợp backend.

## 1. Phạm vi hiện tại

Luồng hiện tại thực hiện:

1. Nhận HTTP POST JSON tại `/ingest/camera`.
2. Trích xuất metadata.
3. Kiểm tra UUID, modality FUNDUS, eye_side và device_id.
4. Kiểm tra Base64, định dạng JPEG/PNG, dung lượng và số pixel.
5. Tính SHA-256 của ảnh.
6. Chuyển JSON thành multipart/form-data.
7. Giữ multipart trong hàng đợi trước port `PreparedMultipart-PendingGateway`.
8. Lưu payload lỗi vào volume quarantine.

Chưa triển khai hoặc chưa xác minh:

- Gửi multipart đến Gateway bằng InvokeHTTP.
- Service token có scope `analysis:ingest`.
- Endpoint backend `POST /api/v1/analyses`.
- Kiểm tra quyền bệnh nhân trong tenant.
- Idempotency và chống trùng tại backend.
- Retry/backoff khi HTTP 429, 503 hoặc timeout.
- Xử lý phản hồi backend và lưu lỗi sau khi gửi.
- Kiểm thử toàn luồng và NFR end-to-end.
- Triển khai flow trên instance NiFi hoàn toàn mới và kiểm tra đầy đủ
  các tình huống; import nhóm mới trong cùng instance đã đạt hai ca
  ngày 2026-10-10, xem mục 8.

HTTP 200 từ ListenHTTP chỉ xác nhận tiếp nhận dữ liệu vào NiFi;
không có nghĩa backend đã tạo analysis.

## 2. Thành phần và đường đi

Flow có 7 processor:

| Processor | Chức năng |
|---|---|
| ListenHTTP-CameraSimulator | Nhận JSON trên cổng 8081 |
| EvaluateJsonPath-Metadata | Trích xuất các thuộc tính camera |
| RouteOnAttribute-Validation | Phân nhánh metadata hợp lệ và lỗi |
| ExecuteScript-ValidateImage | Giải mã, kiểm tra ảnh và tính checksum |
| ExecuteScript-PrepareMultipart | Tạo body multipart và thuộc tính header |
| UpdateAttribute-Quarantine | Giữ lý do lỗi, đặt filename theo UUID |
| PutFile-QuarantinePersistence | Ghi JSON lỗi vào volume |

Hai Output Port:

- `PreparedMultipart-PendingGateway`: điểm giữ payload đã chuẩn bị.
- `QuarantineWriteFailure`: điểm giữ FlowFile khi ghi quarantine thất bại.

Các đường nối chính:

| Nguồn | Relationship | Đích |
|---|---|---|
| ListenHTTP | success | EvaluateJsonPath |
| EvaluateJsonPath | matched | RouteOnAttribute |
| EvaluateJsonPath | failure | UpdateAttribute-Quarantine |
| RouteOnAttribute | ValidFundus | ValidateImage |
| RouteOnAttribute | UnsupportedModality, unmatched | UpdateAttribute-Quarantine |
| ValidateImage | success | PrepareMultipart |
| ValidateImage | failure | UpdateAttribute-Quarantine |
| PrepareMultipart | success | PreparedMultipart-PendingGateway |
| PrepareMultipart | failure | UpdateAttribute-Quarantine |
| UpdateAttribute-Quarantine | success | PutFile |
| PutFile | failure | QuarantineWriteFailure |

EvaluateJsonPath auto-terminate `unmatched`.
PutFile auto-terminate `success` sau khi ghi thành công.
Các nhánh success/failure của hai ExecuteScript không auto-terminate
và không bật Retry.

Flow hiện tại không có DistributedMapCache, DetectDuplicate,
InvokeHTTP hoặc RetryFlowFile.

## 3. Mạng và khởi chạy

| Dịch vụ | Địa chỉ trên máy Windows |
|---|---|
| NiFi UI | https://localhost:8443/nifi |
| Camera ingress | http://127.0.0.1:8081/ingest/camera |
| Gateway | http://127.0.0.1:5000 |

Địa chỉ Gateway dự kiến từ container NiFi:
`http://api-gateway:8080/api/v1/analyses`.

NiFi và Gateway phải cùng mạng Docker.
Tên mạng đã kiểm tra trên máy thực hiện: `aura_aura_network`.
Máy khác phải kiểm tra tên mạng thực tế, không mặc định dùng tên này.

PowerShell:

```powershell
$gatewayInfo = docker inspect aura-api-gateway-1 | ConvertFrom-Json
$gatewayInfo[0].NetworkSettings.Networks.PSObject.Properties.Name
```

Cấu hình trong `.env`:

- `AURA_DOCKER_NETWORK`: tên mạng thực tế.
- `NIFI_ADMIN_USER`: tài khoản NiFi.
- `NIFI_ADMIN_PASSWORD`: mật khẩu riêng, ít nhất 12 ký tự.

Không commit `.env`, mật khẩu hoặc service token.

Từ thư mục gốc dự án:

```powershell
docker compose --env-file .env -p aura-nifi -f infra/nifi/docker-compose.nifi.yml config --quiet
docker compose --env-file .env -p aura-nifi -f infra/nifi/docker-compose.nifi.yml up -d
docker logs --tail 100 aura-nifi
```

Các lệnh sau này phải dùng cùng project `aura-nifi`, compose file
và env file để sử dụng đúng container/volume.

## 4. Quyền ghi quarantine

Thư mục trong container: `/opt/nifi/quarantine`.

Kiểm tra:

```powershell
docker exec aura-nifi sh -c 'if test -w /opt/nifi/quarantine; then echo WRITABLE; else echo NOT_WRITABLE; fi'
```

Nếu trả NOT_WRITABLE:

```powershell
$nifiUid = (docker exec aura-nifi id -u).Trim()
$nifiGid = (docker exec aura-nifi id -g).Trim()

docker exec --user 0 aura-nifi chown "${nifiUid}:${nifiGid}" /opt/nifi/quarantine
docker exec --user 0 aura-nifi chmod 750 /opt/nifi/quarantine
```

Chạy lại kiểm tra và xác nhận WRITABLE.

PutFile được cấu hình:

- Directory: `/opt/nifi/quarantine/${quarantine.reason}`
- Create Missing Directories: true.
- Conflict Resolution Strategy: fail.
- Filename: `${uuid}.json`.

Quarantine lưu nguyên JSON lỗi.
Lý do nằm trong tên thư mục; toàn bộ FlowFile Attributes
không tự động được ghi thành sidecar.

Nếu ghi thất bại, FlowFile được giữ trong hàng đợi trước
`QuarantineWriteFailure`; không auto-terminate nhánh này.

## 5. Định dạng đầu vào

Camera gửi JSON với các trường:

| Trường | Yêu cầu |
|---|---|
| request_id | UUID; giữ nguyên cho cùng logical request khi gửi lại |
| patient_id | UUID |
| modality | FUNDUS |
| eye_side | left, right hoặc unknown |
| device_id | Chuỗi không rỗng |
| image_base64 | Base64 chuẩn của ảnh JPEG/PNG |

Không dùng data URI, Base64 có khoảng trắng hoặc alphabet URL-safe.

Giới hạn trong script:

- JSON: tối đa 20 MiB tại bước ValidateImage.
- Ảnh giải mã: tối đa 10 MiB.
- Số pixel: tối đa 16.000.000.

EvaluateJsonPath chạy trước ValidateImage.
Giới hạn JSON trong script không phải giới hạn tiếp nhận HTTP
và không ngăn việc EvaluateJsonPath đọc payload trước đó.

Kiểm tra kỹ thuật ảnh không xác nhận đây là ảnh võng mạc,
không đánh giá chất lượng lâm sàng và không thay thế validation backend.

## 6. Payload chuẩn bị gửi API

PrepareMultipart tạo:

| Nguồn | Đích |
|---|---|
| patient_id | Multipart patientId |
| modality | Multipart modality |
| eye_side | Multipart eyeSide |
| image_base64 | Binary trong multipart images |
| request_id | Thuộc tính gateway.idempotency_key |
| request_id | Thuộc tính gateway.correlation_id |

Các thuộc tính sau khi thành công:

- `gateway.payload.ready = true`
- `gateway.payload.version = analysis-multipart-v0.1`
- `gateway.retry_count = 0`
- `mime.type = multipart/form-data; boundary=aura-...`
- `filename` kết thúc bằng `.multipart`

Đây mới là thuộc tính FlowFile, chưa phải header đã gửi qua HTTP.

Bước gửi sau này phải dùng đúng Content-Type có boundary,
Idempotency-Key và X-Correlation-Id đã chuẩn bị.
Retry phải giữ nguyên key và body; không chạy lại PrepareMultipart
trên body đã chuyển thành multipart.

## 7. Kiểm thử đã thực hiện

Kiểm thử được thực hiện trên container NiFi 1.27.0 chạy trên Windows Docker.

| Trường hợp | Kết quả quan sát |
|---|---|
| JSON sai cú pháp | File trong INVALID_JSON |
| Metadata thiếu bắt buộc | File trong unmatched |
| Modality OCT | File trong UnsupportedModality |
| Thiếu image_base64 | File trong MISSING_IMAGE_BASE64 |
| Base64 sai | File trong INVALID_BASE64 |
| Base64 của văn bản | File trong INVALID_OR_UNSUPPORTED_IMAGE |
| Request ID sai tại PrepareMultipart | File trong INVALID_REQUEST_ID |
| JPEG CHASE_DB1 Image_03L.jpg | Qua ValidateImage và tạo multipart |

Ảnh JPEG đã kiểm tra:

- Kích thước: 999 × 960.
- Dung lượng ảnh binary: 71.630 byte.
- SHA-256:
  `db4bc8514041302ad66a283d83a331e88b2a7ffb156ae5ed4de84ad3aa0660c8`.

File multipart đã tải xuống được phân tích độc lập:
boundary hợp lệ, có đủ patientId/modality/eyeSide/images,
JPEG giải mã được và checksum khớp ảnh trước chuyển đổi.

Patient UUID trong mẫu thử là dữ liệu giả lập;
chưa xác nhận tồn tại hoặc được cấp quyền trong backend.

Chưa thực hiện đủ các trường hợp PNG, ảnh quá dung lượng,
quá số pixel, ảnh hỏng và checksum mismatch.

Hai request khác UUID dùng cùng ảnh không chứng minh idempotency.
`gateway.retry_count = 0` không chứng minh đã triển khai retry.

Xem file quarantine:

```powershell
docker exec aura-nifi find /opt/nifi/quarantine -maxdepth 2 -type f
```

Script `src/ai-core/scripts/simulate_camera_nifi.py` chuẩn bị 7 mẫu thử.
Mặc định chạy dry-run, không gửi HTTP.

Thêm `--live` để gửi đến ListenHTTP khi các processor đã chạy
và hai Output Port vẫn Stopped. Mỗi lượt live tạo thêm payload
trong hàng đợi và quarantine.

HTTP 200 chỉ xác nhận ingress nhận request.
Script không tự kiểm tra routing, Gateway, service token, dedup hoặc retry.

## 8. Start, Stop và lưu cấu hình

Giữ hai Output Port Stopped trong giai đoạn hiện tại.

Start processor theo thứ tự:

1. PutFile-QuarantinePersistence.
2. UpdateAttribute-Quarantine.
3. ExecuteScript-PrepareMultipart.
4. ExecuteScript-ValidateImage.
5. RouteOnAttribute-Validation.
6. EvaluateJsonPath-Metadata.
7. ListenHTTP-CameraSimulator.

Không Start toàn nhóm vì hai Output Port đang là điểm giữ tạm
và chưa được nối tiếp ở nhóm cha.

Khi sửa cấu hình, Stop ListenHTTP trước,
đợi dữ liệu ổn định rồi Stop các processor còn lại.
Không Empty queue nếu cần giữ payload để kiểm tra.

Export từ nhóm cha:

1. Nhấp chuột phải AURA-Camera-Ingestion.
2. Download flow definition.
3. Without external services.
4. Lưu thành `infra/nifi/camera_ingestion_flow.json`.

Export hiện có 7 processor, 11 kết nối và 2 Output Port.
Các ID tham chiếu đã được kiểm tra khớp.

Flow definition chứa cấu hình và script, không chứa FlowFile đang chờ.
Trạng thái ENABLED trong export không chứng minh processor đang chạy.
Sau import phải kiểm tra trạng thái trước khi Start.
### Kiểm tra import flow — 2026-10-10

Đã import flow definition vào nhóm mới
`AURA-Camera-Ingestion-ImportCheck` trong cùng instance NiFi 1.27.0.

Nhóm import có 7 processor, 11 connection và 2 output port.
Dùng listener cổng 8081 với Base Path `ingest/import-check`;
listener của nhóm cũ được giữ Stopped.

Hai tình huống đã kiểm tra:

| Trường hợp | Request ID | Kết quả |
|---|---|---|
| RealJPEG | a9423b30-dc93-4cb1-8f89-97154bf0c162 | Đến queue trước PreparedMultipart-PendingGateway |
| InvalidBase64 | 81a21792-8df1-42e0-850f-689c79d063e4 | Lưu trong quarantine/INVALID_BASE64 |

RealJPEG:
- image.validation.status: VALID.
- gateway.payload.ready: true.
- image.format: JPEG.
- Kích thước: 999×960.
- image.size_bytes: 71630.
- gateway.idempotency_key và gateway.correlation_id khớp request ID.
- mime.type chứa multipart/form-data và boundary theo UUID FlowFile.

File quarantine của InvalidBase64:
`/opt/nifi/quarantine/INVALID_BASE64/c25beab1-21cf-40f7-b865-e7e4656948d5.json`.

Kết quả xác nhận flow import hoạt động cho hai tình huống trên
trong cùng instance NiFi. Chưa kiểm tra triển khai trên instance
hoàn toàn mới, chưa kiểm tra lại toàn bộ tình huống và chưa nghiệm thu
Gateway forwarding, service token, dedup hoặc retry.

Sau kiểm tra, dừng các processor và giữ hai output port Stopped.

Dừng container nhưng giữ dữ liệu:

```powershell
docker compose --env-file .env -p aura-nifi -f infra/nifi/docker-compose.nifi.yml stop
```

Khởi chạy lại dùng lệnh up ở mục 3.
Không xóa volume nếu muốn giữ cấu hình, hàng đợi và quarantine.

## 9. Điều kiện hoàn tất tích hợp

M2 cần triển khai endpoint nhận multipart, service authentication,
quyền bệnh nhân/tenant, idempotency và transaction lưu analysis/job/outbox
theo contract.

M1 cần kiểm tra route Gateway và kết nối đến Analysis API.

M4 cần bổ sung InvokeHTTP, ánh xạ phản hồi,
retry/backoff có giới hạn và xử lý lỗi sau gửi.

Chỉ kết luận tích hợp hoàn tất sau khi kiểm thử:
request hợp lệ, lỗi quyền, replay cùng key, key bị dùng với nội dung khác,
429/503/timeout, hết retry và khôi phục sau restart.