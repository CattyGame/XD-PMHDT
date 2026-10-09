# HƯỚNG DẪN VẬN HÀNH & KIỂM THỬ APACHE NIFI (RUNBOOK)
> **Dự án**: AURA - Phân tích Mạch máu Võng mạc Hỗ trợ Sàng lọc Sức khỏe
> **Module**: M4 (AI Engineer & NiFi Camera Ingestion Pipeline)
> **Mã công việc Jira**: [SCRUM-63] PoC NiFi nhận ảnh giả lập camera | [SCRUM-290] Version NiFi và dữ liệu huấn luyện
> **Người thực hiện**: Đào Duy Quân (M4 Lead)
> **Người duyệt**: Nguyễn Trương Hậu (M1 - Tech Lead), Võ Hữu Duy (M2 - Backend Lead)
> **Phiên bản**: 1.1.0 (Cập nhật controller cache, sửa routing InvalidPayload, trace headers và quarantine persistent)

---

## 1. Tổng quan kiến trúc & Ranh giới NiFi trong hệ thống AURA

Trong quy trình khám mắt thực tế, các camera chụp đáy mắt (Fundus Camera) tại phòng khám tự động kết xuất ảnh cùng tệp metadata JSON chứa thông tin ca khám. **Apache NiFi** đóng vai trò là tầng tiếp nhận biên (Edge Ingestion Pipeline):
* Lắng nghe và tiếp nhận tệp ảnh từ camera chụp đáy mắt giả lập qua cổng HTTP nội bộ (`/ingest/camera`).
* Trích xuất các trường thông tin nhận dạng (`requestId`, `patientId`, `modality`, `eyeSide`, `deviceId`).
* Xác thực sơ bộ (Validation): Chỉ cho phép modality `FUNDUS`, loại bỏ payload rác và `OCT`. Đã sửa biểu thức định tuyến để payload hợp lệ không đồng thời đi vào nhánh lỗi (`InvalidPayload = false` khi là Fundus hợp lệ).
* Chống gửi trùng lặp (Deduplication) dựa trên `requestId` sử dụng Controller Service `DistributedMapCacheServer` & `DistributedMapCacheClientService`.
* Bổ sung Header định danh & Truy vết: Tạo `X-Correlation-Id`, `Idempotency-Key` và `X-Device-Id` qua processor `UpdateAttribute` trước khi gửi HTTP.
* Chuyển tiếp dữ liệu vào hệ thống qua **API Gateway (M1)** tại endpoint `POST /api/v1/analyses`.
* Cơ chế tự động thử lại (Retry with Backoff): Thử lại tối đa 3 lần khi Gateway bận (HTTP 429/503) hoặc lỗi kết nối tạm thời.
* Cách ly lỗi và Lưu trữ bền vững (Persistent Quarantine): Lưu tệp lỗi và tệp hết hạn thử lại vào volume `/opt/nifi/quarantine/` kèm đầy đủ lý do và mã truy vết.

```
[ Camera chụp đáy mắt giả lập ]
                │ (HTTP POST JSON + Image Base64)
                ▼
  [ Apache NiFi (ListenHTTP :8081) ]
                │
                ▼
   [ EvaluateJsonPath-Metadata ]
                │
                ├──▶ [ RouteOnAttribute ] ──(OCT / Sai Schema)──▶ [ Quarantine Queue & PutFile ]
                │
                ▼ (FUNDUS hợp lệ)
   [ DetectDuplicate-RequestId ] ──(Trùng ID)───────────────────▶ [ Quarantine Queue & PutFile ]
         (DistributedCache)
                │ (Không trùng lặp)
                ▼
  [ UpdateAttribute-AddTraceHeaders ] (Tạo X-Correlation-Id, Idempotency-Key)
                │
                ▼
       [ InvokeHTTP :8080 ] ──▶ [ API Gateway M1 ] ──▶ [ Analysis API M2 (202 Accepted) ]
                │
                ├──(200/202 Response)──▶ [ LogAttribute-SuccessAuditing ]
                │
                └──(503/429/Timeout)──▶ [ RetryFlowFile (Max 3 lần) ]
                                                │
                                                └──(Hết 3 lần)──▶ [ Quarantine Queue & PutFile ]
```

---

## 2. Thông số mạng & Cổng dịch vụ

| Dịch vụ / Endpoint | Giao thức | Cổng Host | Cổng Container | Mục đích |
| :--- | :--- | :--- | :--- | :--- |
| **NiFi Web UI** | HTTPS | `8443` | `8443` | Giao diện kéo thả và giám sát luồng NiFi |
| **Camera Ingestion** | HTTP | `8081` | `8081` | Điểm tiếp nhận ảnh từ camera (`/ingest/camera`) |
| **NiFi Cache Server**| TCP | Nội bộ | `4557` | DistributedMapCacheServer cho deduplication |
| **API Gateway Target**| HTTP | `5000` | `8080` | Đích chuyển tiếp dữ liệu (`http://api-gateway:8080/api/v1/analyses`) |

Mạng Docker kết nối: `${AURA_DOCKER_NETWORK:-aura_network}` (dùng biến môi trường, không hardcode tên thư mục).

---

## 3. Cấu hình Môi trường & Khởi chạy NiFi

Tệp cấu hình: `infra/nifi/docker-compose.nifi.yml`

### Biến môi trường:
Các biến môi trường được cấu hình trong `.env` (hoặc xem mẫu tại `.env.example`):
* `AURA_DOCKER_NETWORK`: Tên mạng Docker dùng chung giữa Gateway, Analysis và NiFi (mặc định: `aura_network`).
* `NIFI_ADMIN_USER`: Tài khoản quản trị NiFi UI (mặc định: `admin`).
* `NIFI_ADMIN_PASSWORD`: Mật khẩu quản trị NiFi UI (tối thiểu 12 ký tự, ví dụ: `AuraNiFi2026SecurePass`).

### Khởi chạy container:
```bash
docker compose -f infra/nifi/docker-compose.nifi.yml up -d
```

Kiểm tra trạng thái container và log:
```bash
docker compose -f infra/nifi/docker-compose.nifi.yml ps
docker logs -f aura-nifi
```

Truy cập giao diện Web UI:
* Địa chỉ: `https://localhost:8443/nifi`
* Tài khoản: `${NIFI_ADMIN_USER}`
* Mật khẩu: `${NIFI_ADMIN_PASSWORD}`

---

## 4. Quản lý & Version hóa Flow Definition (SCRUM-290)

Tệp định nghĩa luồng chuẩn được lưu trữ và version hóa trong Git tại:
`infra/nifi/camera_ingestion_flow.json`

Phiên bản Apache NiFi chuẩn: **1.27.0**

### Các thành phần chính trong flow:
1. **Controller Services**:
   - `DistributedMapCacheServer`: Quản lý bộ nhớ đệm cache trên cổng 4557 để lưu vết các `request_id` đã xử lý.
   - `DistributedMapCacheClientService`: Client service kết nối tới cache server, cung cấp dịch vụ tra cứu cho processor `DetectDuplicate`.
2. **Processors**:
   - `ListenHTTP-CameraSimulator`: Tiếp nhận payload JSON Base64 trên cổng 8081.
   - `EvaluateJsonPath-Metadata`: Trích xuất các thuộc tính định tuyến từ payload.
   - `RouteOnAttribute-Validation`:
     + `ValidFundus`: `${camera.modality:toUpper():equals('FUNDUS'):and(${camera.request_id:isEmpty():not()})}`
     + `UnsupportedModality`: `${camera.modality:toUpper():equals('OCT')}`
     + `InvalidPayload`: `${camera.modality:toUpper():equals('FUNDUS'):not():and(${camera.modality:toUpper():equals('OCT'):not()}):or(${camera.request_id:isEmpty()})}`
   - `DetectDuplicate-RequestId`: Chặn request trùng lặp trong 24 giờ.
   - `UpdateAttribute-AddTraceHeaders`: Gán header truy vết `X-Correlation-Id`, `Idempotency-Key`, `X-Device-Id`.
   - `InvokeHTTP-ForwardToGateway`: Gửi HTTP POST tới Gateway với các header truy vết.
   - `RetryFlowFile-GatewayBackoff`: Thử lại tối đa 3 lần khi Gateway bận hoặc timeout.
   - `LogAttribute-Quarantine`: Ghi log cảnh báo các FlowFile bị từ chối/cách ly.
   - `PutFile-QuarantinePersistence`: Lưu trữ FlowFile lỗi vào volume `/opt/nifi/quarantine/` để phục vụ điều tra sự cố.

### Quy trình nạp luồng (Import Flow):
1. Đăng nhập NiFi Web UI (`https://localhost:8443/nifi`).
2. Kéo biểu tượng **Process Group** từ thanh công cụ vào vùng làm việc.
3. Nhấp chọn biểu tượng **Upload** (Browse) và chọn tệp `infra/nifi/camera_ingestion_flow.json`.
4. Nhấp **Add** để tạo nhóm `AURA-Camera-Ingestion`.
5. Bật Controller Services trong cấu hình Process Group (Configuration $\rightarrow$ Controller Services $\rightarrow$ Enable).
6. Bật Start nhóm xử lý (Click chuột phải $\rightarrow$ Start).

---

## 5. Kịch bản kiểm thử (Test Scenarios)

Hệ thống hỗ trợ 2 hình thức kiểm thử:
* **Kiểm thử logic mô phỏng (Local Unit Simulation)**: Chạy script `src/ai-core/scripts/simulate_camera_nifi.py` (không yêu cầu container NiFi đang chạy).
* **Kiểm thử trên dịch vụ thật (Live Container Execution)**: Chạy script với cờ `--live` khi container NiFi đang hoạt động.

### Kịch bản 1: Gửi ảnh FUNDUS hợp lệ (Success Path)
Gửi payload hợp lệ giả lập từ camera:
```bash
curl -X POST http://localhost:8081/ingest/camera \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "cam_req_20261008_001",
    "patient_id": "PAT-99120",
    "device_id": "TOPCON-TRC-NW400",
    "modality": "FUNDUS",
    "eye_side": "right",
    "image_base64": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
  }'
```
* **Kỳ vọng**: NiFi tiếp nhận HTTP 200 OK, trích xuất metadata, gán `X-Correlation-Id: cam_req_20261008_001` và `Idempotency-Key: idemp-cam_req_20261008_001`, chuyển tiếp thành công đến API Gateway. FlowFile đi tới `LogAttribute-SuccessAuditing`.

### Kịch bản 2: Chống gửi trùng lặp Request ID (Duplicate Protection)
Gửi lại chính xác payload trên một lần nữa với cùng `request_id`:
* **Kỳ vọng**: Processor `DetectDuplicate-RequestId` phát hiện trùng lặp trong vòng 24h, định tuyến sang `LogAttribute-Quarantine` và lưu vào thư mục quarantine với lý do `DUPLICATE_DETECTED`. Gateway không bị gọi lặp lại.

### Kịch bản 3: Gửi modality không hỗ trợ - OCT (Quarantine Path)
Gửi payload chứa modality `OCT`:
* **Kỳ vọng**: Processor `RouteOnAttribute-Validation` phát hiện `modality == OCT`, định tuyến trực tiếp vào quarantine với lý do `UNSUPPORTED_MODALITY`, không gửi tới Gateway.

### Kịch bản 4: Payload thiếu Request ID hoặc ảnh hỏng
Gửi payload thiếu `request_id` hoặc chuỗi base64 rỗng:
* **Kỳ vọng**: Processor `RouteOnAttribute-Validation` định tuyến vào quarantine với lý do `INVALID_PAYLOAD`, không đi vào nhánh xử lý hợp lệ.

### Kịch bản 5: Gateway quá tải & Tự động thử lại (Retry with Backoff)
Khi Gateway trả về HTTP 429, 503 hoặc timeout:
* **Kỳ vọng**: FlowFile được đưa vào `RetryFlowFile-GatewayBackoff`, thử lại tối đa 3 lần với khoảng cách thời gian giãn cách.

### Kịch bản 6: Hết số lần thử lại & Lưu Quarantine bền vững
Nếu Gateway vẫn lỗi sau 3 lần thử:
* **Kỳ vọng**: FlowFile chuyển sang mối quan hệ `retries_exceeded`, được ghi log và lưu trữ bền vững tại `/opt/nifi/quarantine/` kèm mã `X-Correlation-Id` phục vụ truy vết.

---

## 6. Lệnh kiểm thử tự động

Chạy bộ kiểm thử logic 6 kịch bản trên môi trường phát triển:
```bash
python src/ai-core/scripts/simulate_camera_nifi.py
```

Chạy kiểm thử trực tiếp tới container NiFi thật (khi container đang chạy):
```bash
python src/ai-core/scripts/simulate_camera_nifi.py --live --url http://localhost:8081/ingest/camera
```
