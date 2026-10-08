# HƯỚNG DẪN VẬN HÀNH & KIỂM THỬ APACHE NIFI (RUNBOOK)
> **Dự án**: AURA - Phân tích Mạch máu Võng mạc Hỗ trợ Sàng lọc Sức khỏe
> **Module**: M4 (AI Engineer & NiFi Camera Ingestion Pipeline)
> **Mã công việc Jira**: [SCRUM-63] PoC NiFi nhận ảnh giả lập camera | [SCRUM-290] Version NiFi và dữ liệu huấn luyện
> **Người thực hiện**: Đào Duy Quân (M4 Lead)
> **Phiên bản**: 1.0.0

---

## 1. Tổng quan kiến trúc & Ranh giới NiFi trong hệ thống AURA

Trong quy trình khám mắt thực tế, các camera chụp đáy mắt (Fundus Camera) tại phòng khám tự động kết xuất ảnh cùng tệp metadata JSON chứa thông tin ca khám. **Apache NiFi** đóng vai trò là tầng tiếp nhận biên (Edge Ingestion Pipeline):
* Lắng nghe và tiếp nhận tệp ảnh từ camera chụp đáy mắt giả lập qua cổng HTTP nội bộ.
* Trích xuất các trường thông tin nhận dạng (`requestId`, `patientId`, `modality`, `eyeSide`).
* Xác thực sơ bộ (Validation): Chỉ cho phép modality `FUNDUS`, loại bỏ payload rác.
* Chống gửi trùng lặp (Deduplication) dựa trên `requestId` hoặc mã băm tệp ảnh.
* Đẩy dữ liệu vào hệ thống qua **API Gateway (M1)** tại endpoint `POST /api/v1/analyses`.
* Cơ chế tự động thử lại (Retry) khi Gateway bận (HTTP 429/503) và cách ly lỗi (Quarantine).

```
[ Camera chụp đáy mắt giả lập ]
                │ (HTTP POST JSON + Image Base64)
                ▼
  [ Apache NiFi (ListenHTTP :8081) ]
                │
                ├──▶ [ RouteOnAttribute ] ──(OCT / Lỗi)──▶ [ Quarantine Queue ]
                │
                ├──▶ [ DetectDuplicate ]  ──(Trùng ID)──▶ [ Quarantine Queue ]
                │
                ▼ (Hợp lệ)
      [ InvokeHTTP :8080 ] ──▶ [ API Gateway M1 ] ──▶ [ Analysis API M2 (202 Accepted) ]
                │
                └──(503/429)──▶ [ Retry Queue (Max 3 lần) ]
```

---

## 2. Thông số mạng & Cổng dịch vụ

| Dịch vụ / Endpoint | Giao thức | Cổng Host | Cổng Container | Mục đích |
| :--- | :--- | :--- | :--- | :--- |
| **NiFi Web UI** | HTTPS | `8443` | `8443` | Giao diện kéo thả và giám sát luồng NiFi |
| **Camera Ingestion** | HTTP | `8081` | `8081` | Điểm tiếp nhận ảnh từ camera (`/ingest/camera`) |
| **API Gateway Target**| HTTP | `5000` | `8080` | Đích chuyển tiếp dữ liệu (`http://api-gateway:8080/api/v1/analyses`) |

---

## 3. Hướng dẫn Khởi chạy dịch vụ NiFi

### Cách 1: Khởi chạy bằng Docker Compose chuyên dụng
Từ thư mục gốc dự án:
```bash
docker compose -f infra/nifi/docker-compose.nifi.yml up -d
```

Kiểm tra trạng thái container:
```bash
docker compose -f infra/nifi/docker-compose.nifi.yml ps
docker logs -f aura-nifi
```

Truy cập giao diện Web UI:
* Địa chỉ: `https://localhost:8443/nifi`
* Tài khoản mặc định: `admin`
* Mật khẩu mặc định: Được cấu hình trong `.env` (`NIFI_ADMIN_PASSWORD`) hoặc biến môi trường `AuraNiFi2026SecurePass`.

---

## 4. Quản lý & Version hóa Flow Definition (SCRUM-290)

Tệp định nghĩa luồng chuẩn được lưu trữ và version hóa trong Git tại:
`infra/nifi/camera_ingestion_flow.json`

> **Quy tắc bảo mật quan trọng (Tech Lead Requirement)**:
> Tuyệt đối không lưu mật khẩu, API token hoặc credential y tế vào tệp flow JSON export. Mọi kết nối nhạy cảm phải được truyền qua Parameter Context hoặc biến môi trường của container.

### Quy trình nạp luồng (Import Flow):
1. Mở NiFi Web UI (`https://localhost:8443/nifi`).
2. Kéo biểu tượng **Process Group** từ thanh công cụ vào vùng làm việc.
3. Nhấp chọn biểu tượng **Upload** (Browse) và chọn tệp `infra/nifi/camera_ingestion_flow.json`.
4. Nhấp **Add** để tạo nhóm `AURA-Camera-Ingestion`.
5. Bật Start nhóm xử lý (Click chuột phải $\rightarrow$ Start).

---

## 5. Kịch bản kiểm thử (Test Scenarios)

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
* **Kỳ vọng**: NiFi tiếp nhận HTTP 200 OK, trích xuất metadata, kiểm tra trùng lặp thành công, và chuyển tiếp thành công đến API Gateway. FlowFile đi tới `LogAttribute-SuccessAuditing`.

---

### Kịch bản 2: Gửi trùng lặp Request ID (Duplicate Protection)
Gửi lại chính xác payload trên một lần nữa với cùng `request_id`:
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
* **Kỳ vọng**: Processor `DetectDuplicate-RequestId` phát hiện trùng lặp trong vòng 24h, định tuyến sang `LogAttribute-Quarantine` với cờ `duplicate`. Hệ thống backend không bị xử lý lặp lại.

---

### Kịch bản 3: Gửi modality không hỗ trợ - OCT (Quarantine Path)
Gửi payload chứa modality `OCT`:
```bash
curl -X POST http://localhost:8081/ingest/camera \
  -H "Content-Type: application/json" \
  -d '{
    "request_id": "cam_req_20261008_002",
    "patient_id": "PAT-99121",
    "device_id": "ZEISS-CIRRUS-OCT",
    "modality": "OCT",
    "eye_side": "left",
    "image_base64": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
  }'
```
* **Kỳ vọng**: Processor `RouteOnAttribute-Validation` phát hiện `modality != FUNDUS`, định tuyến trực tiếp vào `LogAttribute-Quarantine`, ghi cảnh báo log hệ thống mà không gửi yêu cầu lỗi tới Gateway.

---

### Kịch bản 4: Gateway quá tải & Tự động thử lại (Retry with Backoff)
Khi API Gateway hoặc Analysis Service tạm thời bận hoặc quá tải (HTTP 503 / 429):
* FlowFile được đưa vào `RetryFlowFile-GatewayBackoff`.
* Tự động thử lại tối đa 3 lần với khoảng cách thời gian giãn cách theo cấp số nhân (Exponential Backoff).
* Nếu vượt quá 3 lần vẫn lỗi, FlowFile chuyển sang `retries_exceeded` đưa vào hàng đợi Quarantine để kỹ sư vận hành xử lý sự cố thủ công.
