# HỢP ĐỒNG GIAO TIẾP API - MODULE M4 (AI & COMPUTER VISION)
> **Dự án**: AURA (Phần mềm Phân tích Mạch máu Võng mạc Hỗ trợ Đánh giá Sức khỏe)  
> **Mã công việc Jira**: [SCRUM-61] OpenAPI AI và schema output  
> **Người thực hiện**: Đào Duy Quân (M4 Lead)  
> **Các bên liên quan duyệt**: Nguyễn Trương Hậu (M1 - Gateway/Architecture), Võ Hữu Duy (M2 - Backend Analysis), Lâm Trần Nguyên Hoàng (M5 - Frontend)  
> **Phiên bản**: 1.0.0  

---

## 1. Tổng quan kiến trúc & Ranh giới Module M4

Trong kiến trúc hệ thống AURA:
* **M1 (API Gateway)**: Tiếp nhận request từ client, xác thực JWT, định tuyến và chuyển tiếp request đến `ai-service:8000`.
* **M2 (Backend Analysis)**: Quản lý luồng nghiệp vụ khám bệnh, hồ sơ bệnh nhân, lưu trữ lịch sử kết quả phân tích vào Database.
* **M4 (AI Service - Module hiện tại)**: Độc lập trong Docker Container, cung cấp suy luận phân vùng mạch máu (Segmentation) và tính toán chỉ số hình học mạch máu (Heuristic Risk Assessment).
* **M5 (Frontend React)**: Hiển thị giao diện phân tích, ảnh gốc, mặt nạ nhị phân mạch máu (Mask) và lớp phủ viền mạch máu (Overlay).

```
[ Frontend M5 ] 
      │ (Upload ảnh)
      ▼
[ API Gateway M1 ] ──(Reverse Proxy)──▶ [ AI Service M4 (FastAPI Docker) ]
      ▲                                         │ (Output JSON + Masks)
      │                                         ▼
[ Backend M2 (Analysis Service) ] ◀─────────────┘
```

---

## 2. Đường dẫn Hợp đồng OpenAPI
* **YAML File**: `contracts/ai_service_openapi.yaml` (Chuẩn OpenAPI 3.0.3)
* **JSON File**: `contracts/ai_service_openapi.json` (Dành cho Swagger UI, Postman Import, Insomnia)

---

## 3. Chi tiết các Endpoint

### 3.1. `GET /health`
Kiểm tra sức khỏe dịch vụ, tình trạng nạp mô hình AI và thiết bị thực thi.

* **Response 200 OK**:
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "service": "aura-ai-service",
  "device": "cpu",
  "model_loaded": true,
  "uptime_seconds": 3600.5
}
```

---

### 3.2. `POST /api/v1/analyze`
Tiếp nhận ảnh chụp võng mạc, phân vùng hệ thống mạch máu, trích xuất chỉ số hình học và tính toán điểm rủi ro.

#### Request Headers:
```http
Content-Type: application/json
Accept: application/json
```

#### Request Body (`AnalysisRequest`):
| Trường | Kiểu dữ liệu | Bắt buộc | Mô tả |
| :--- | :--- | :--- | :--- |
| `request_id` | string | **Có** | Mã UUID/Request ID duy nhất do M1/M2 sinh ra |
| `patient_id` | string | Không | Mã bệnh nhân (nếu có) |
| `eye_side` | string | Không | Mắt chụp: `"left"`, `"right"`, `"unknown"` (mặc định: `"unknown"`) |
| `mode` | string | Không | Chế độ phân tích: `"retina_vessels"`, `"iris_biometrics"`, `"hybrid"` |
| `image_base64` | string | **Có** | Chuỗi Base64 của ảnh chụp đáy mắt (PNG/JPG) |
| `include_mask` | boolean | Không | Có trả về ảnh nhị phân phân vùng mạch máu không (mặc định: `true`) |
| `include_overlay`| boolean | Không | Có trả về ảnh phủ viền mạch lên ảnh gốc không (mặc định: `true`) |

* **Request Example**:
```json
{
  "request_id": "req_aura_20261005_001",
  "patient_id": "PAT-88231",
  "eye_side": "right",
  "mode": "retina_vessels",
  "image_base64": "iVBORw0KGgoAAAANSUhEUgAA...",
  "include_mask": true,
  "include_overlay": true
}
```

#### Response Body (`AnalysisResponse` - 200 OK):
| Cụm thông tin | Trường | Kiểu dữ liệu | Ý nghĩa kỹ thuật |
| :--- | :--- | :--- | :--- |
| **Header** | `request_id` | string | Trùng với request_id gửi lên |
| | `status` | string | `"SUCCESS"` \| `"WARNING"` \| `"FAILED"` |
| | `processing_time_ms`| float | Thời gian suy luận mô hình (milliseconds) |
| **Image Info** | `width`, `height` | int | Kích thước phân giải ảnh xử lý |
| | `channels` | int | Số kênh màu (mặc định 3: RGB) |
| **Vessel Metrics**| `vessel_density` | float | Mật độ diện tích mạch máu trên diện tích võng mạc (0.0 - 1.0) |
| | `tortuosity_index` | float | Chỉ số xoắn mạch (Arc-length / Chord-length) |
| | `av_ratio` | float | Tỷ lệ đường kính tiểu động mạch / tiểu tĩnh mạch (AVR) |
| | `fractal_dimension`| float | Độ phức tạp phân nhánh phân hình (Fractal Dimension) |
| | `branching_points` | int | Tổng số điểm ngã ba / phân nhánh phát hiện được |
| **Risk Assessment**| `risk_score` | float | Điểm rủi ro tổng hợp chuẩn hóa (0.0: rất thấp ➔ 1.0: rất cao) |
| | `risk_level` | string | Phân hạng: `"LOW"`, `"MODERATE"`, `"HIGH"` |
| | `confidence_score` | float | Độ tin cậy thuật toán (0.0 - 1.0) |
| | `indicators` | array[string]| Các nhận định cụ thể dựa trên chỉ số |
| | `disclaimer` | string | Tuyên bố miễn trừ trách nhiệm y tế bắt buộc |
| **Segmentation** | `mask_base64` | string (opt) | Ảnh nhị phân mạch máu trắng-đen Base64 |
| | `overlay_base64` | string (opt) | Ảnh gốc phủ viền mạch màu Base64 |

* **Response Example**:
```json
{
  "request_id": "req_aura_20261005_001",
  "patient_id": "PAT-88231",
  "timestamp": "2026-10-05T17:59:00Z",
  "status": "SUCCESS",
  "processing_time_ms": 142.5,
  "image_info": {
    "width": 512,
    "height": 512,
    "channels": 3,
    "eye_side": "right"
  },
  "metrics": {
    "vessel_density": 0.1428,
    "tortuosity_index": 1.185,
    "av_ratio": 0.672,
    "fractal_dimension": 1.482,
    "branching_points": 64
  },
  "risk_assessment": {
    "risk_score": 0.35,
    "risk_level": "LOW",
    "confidence_score": 0.88,
    "indicators": [
      "Mật độ mạch máu trong giới hạn bình thường (14.28%)",
      "Chỉ số xoắn mạch (Tortuosity) ổn định"
    ],
    "disclaimer": "Kết quả ước lượng dựa trên hình học mạch máu võng mạc (Heuristic). Không thay thế chẩn đoán y khoa chính thức."
  },
  "segmentation": {
    "mask_format": "png_base64",
    "mask_base64": "iVBORw0KGgoAAAANSUhEUg...",
    "overlay_base64": "iVBORw0KGgoAAAANSUhEUg..."
  }
}
```

---

## 4. Xử lý Lỗi & Mã trạng thái HTTP

| HTTP Code | Mã lỗi nội bộ | Mô tả |
| :--- | :--- | :--- |
| **400 Bad Request** | `INVALID_IMAGE_PAYLOAD` | Base64 bị lỗi hoặc dữ liệu không phải ảnh hợp lệ |
| **422 Unprocessable**| `SCHEMA_VALIDATION_ERROR` | Thiếu các trường bắt buộc (`request_id`, `image_base64`) |
| **500 Server Error** | `INFERENCE_RUNTIME_ERROR` | Lỗi xảy ra trong quá trình chạy mô hình AI |

* **Cấu trúc Error Response**:
```json
{
  "error_code": "INVALID_IMAGE_PAYLOAD",
  "message": "Không thể giải mã dữ liệu ảnh Base64 được gửi lên.",
  "details": {
    "supported_formats": ["JPEG", "PNG", "WebP"]
  },
  "timestamp": "2026-10-05T18:00:00Z"
}
```

---

## 5. Quy chuẩn Tuyên bố Miễn trừ Y tế (Medical Disclaimer)
Theo yêu cầu kỹ thuật phi chức năng **NFR-21**:
> Mọi kết quả trả về từ `AI Service` bắt buộc phải kèm trường `disclaimer` với nội dung rõ ràng rằng: Các chỉ số và điểm rủi ro là giá trị ước lượng kỹ thuật (Heuristic), được tính toán dựa trên hình thái học võng mạc, phục vụ nghiên cứu và hỗ trợ sàng lọc sơ bộ, không phải kết luận chẩn đoán bệnh học.
