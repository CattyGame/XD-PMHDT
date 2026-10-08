# HỢP ĐỒNG GIAO TIẾP API - MODULE M4 (AI & COMPUTER VISION)
> **Dự án**: AURA (Phần mềm Phân tích Mạch máu Võng mạc Hỗ trợ Đánh giá Sức khỏe)
> **Mã công việc Jira**: [SCRUM-61] OpenAPI AI và schema output | [SCRUM-62] FastAPI Docker và mock có nhãn
> **Người thực hiện**: Đào Duy Quân (M4 Lead - AI & NiFi)
> **Các bên liên quan thống nhất**: Nguyễn Trương Hậu (M1 - Gateway/Architecture), Võ Hữu Duy (M2 - Backend Analysis), Lâm Trần Nguyên Hoàng (M5 - Frontend)
> **Phiên bản**: 1.1.0 (Cập nhật đồng bộ kiến trúc bất đồng bộ & Worker nội bộ)

---

## 1. Tổng quan kiến trúc & Ranh giới Module M4

Theo kiến trúc hệ thống chuẩn hóa của dự án AURA:
* **Web Client (M5) / NiFi Camera Ingestion (M4)**: Gửi request phân tích ảnh qua **API Gateway (M1)**.
* **M1 (API Gateway)**: Tiếp nhận request từ client/NiFi, xác thực JWT/Token, rate limit và định tuyến đến **Analysis API (M2)**.
* **M2 (Analysis Service & Worker)**:
  - **Analysis API**: Tiếp nhận yêu cầu, lưu trữ metadata ban đầu, trả về **`202 Accepted`** cùng `analysisId`/`batchId`, và đẩy job vào hàng đợi **RabbitMQ**.
  - **Analysis Worker**: Tiêu thụ job từ RabbitMQ, tải dữ liệu ảnh từ Object Storage/Payload và gọi trực tiếp **AI Core (M4)** qua mạng nội bộ Docker (`http://ai-core:8000/api/v1/analyze`).
* **M4 (AI Core Microservice - Module hiện tại)**: Là **API nội bộ** (Internal Microservice) chạy trong Docker container cô lập (`ai-core:8000`). Nhiệm vụ duy nhất là tiếp nhận ảnh từ Worker, thực thi suy luận mô hình thị giác máy tính (Segmentation & Metrics Heuristics), và trả kết quả JSON đồng bộ cho Worker. **Gateway và Web không gọi trực tiếp AI Core.**

```
[ Web Client M5 / NiFi M4 ]
           │
           ▼
   [ API Gateway M1 ]
           │
           ▼
[ Analysis API (M2) ] ──(202 Accepted)──▶ [ Client Polling / Event ]
           │
           ▼ (Publish Job)
     [ RabbitMQ ]
           │
           ▼ (Consume Job)
[ Analysis Worker (M2) ] ──(HTTP POST nội bộ)──▶ [ AI Core (M4: ai-core:8000) ]
           │                                                │
           │◀────────────────(Kết quả JSON + Masks)─────────┘
           │
           ├──▶ Lưu kết quả vào DB Analysis
           └──▶ Phát sự kiện Notification / FCM
```

---

## 2. Đường dẫn Hợp đồng OpenAPI
* **YAML File**: `contracts/ai_service_openapi.yaml` (Chuẩn OpenAPI 3.0.3)
* **JSON File**: `contracts/ai_service_openapi.json` (Dành cho Swagger UI, Postman Import, Insomnia)

---

## 3. Chi tiết các Endpoint

### 3.1. `GET /health`
Kiểm tra sức khỏe dịch vụ, tình trạng nạp mô hình AI và thiết bị thực thi.
*Quy tắc*: Ở giai đoạn Mock Engine tuần 1 (chưa nạp trọng số mô hình thực), `model_loaded` bắt buộc giữ giá trị `false`.

* **Response 200 OK**:
```json
{
  "status": "ok",
  "version": "0.1.0",
  "service": "AURA.AiCore",
  "device": "cpu",
  "model_loaded": false,
  "uptime_seconds": 124.8
}
```

---

### 3.2. `POST /api/v1/analyze`
Tiếp nhận ảnh chụp võng mạc từ Analysis Worker, xác thực payload ảnh, phân vùng hệ thống mạch máu, trích xuất chỉ số hình học và ước lượng điểm rủi ro tim mạch.

#### Request Headers:
```http
Content-Type: application/json
Accept: application/json
```

#### Request Body (`AnalysisRequest`):
| Trường | Kiểu dữ liệu | Bắt buộc | Mặc định | Mô tả & Ràng buộc |
| :--- | :--- | :--- | :--- | :--- |
| `request_id` | string | **Có** | - | Mã định danh duy nhất của yêu cầu do Worker/Analysis sinh ra |
| `patient_id` | string | Không | `null` | Mã định danh bệnh nhân (nếu có) |
| `eye_side` | string | Không | `"unknown"` | Mắt chụp: `"left"`, `"right"`, `"unknown"` |
| `mode` | string | Không | `"retina_vessels"` | Chế độ phân tích: **Chỉ hỗ trợ `"retina_vessels"`** (các mode ngoài phạm vi bị từ chối với lỗi 422 `UNSUPPORTED_MODE`) |
| `modality` | string | Không | `"FUNDUS"` | Loại ảnh y tế: **Chỉ hỗ trợ `"FUNDUS"`** (`"OCT"` bị từ chối với mã máy đọc được 422 `UNSUPPORTED_MODALITY`) |
| `image_base64` | string | **Có** | - | Chuỗi Base64 hợp lệ của ảnh PNG, JPEG, WebP. **Bắt buộc từ chối chuỗi rác/hỏng hoặc không phải ảnh** với lỗi 400 `INVALID_IMAGE_PAYLOAD` |
| `include_mask` | boolean | Không | `true` | Yêu cầu trả về mặt nạ nhị phân phân vùng mạch máu |
| `include_overlay`| boolean | Không | `true` | Yêu cầu trả về ảnh gốc phủ viền mạch màu |

* **Request Example**:
```json
{
  "request_id": "req_aura_20261005_001",
  "patient_id": "PAT-88231",
  "eye_side": "right",
  "mode": "retina_vessels",
  "modality": "FUNDUS",
  "image_base64": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
  "include_mask": true,
  "include_overlay": true
}
```

#### Response Body (`AnalysisResponse` - 200 OK):
| Cụm thông tin | Trường | Kiểu dữ liệu | Ý nghĩa kỹ thuật |
| :--- | :--- | :--- | :--- |
| **Header** | `request_id` | string | Trùng với `request_id` gửi lên |
| | `status` | string | `"SUCCESS"` \| `"WARNING"` \| `"FAILED"` |
| | `timestamp` | string (ISO) | Thời điểm xử lý (UTC) |
| | `is_mock` | boolean | `true` (xác nhận kết quả từ Mock Engine tuần 1) |
| | `model_version` | string | Phiên bản mô hình suy luận (`"mock-v0.1"`) |
| | `threshold_version`| string | Phiên bản ngưỡng đánh giá rủi ro (`"v0.1"`) |
| | `config_version` | string | Phiên bản cấu hình thuật toán (`"v0.1"`) |
| | `limitations` | string | Mô tả giới hạn kỹ thuật của phiên bản hiện tại |
| | `processing_time_ms`| float | Thời gian suy luận mô hình (Inference latency ~45ms) |
| **Image Info** | `width`, `height` | int | Kích thước phân giải thực tế của ảnh giải mã |
| | `channels` | int | Số kênh màu (mặc định 3: RGB) |
| | `eye_side` | string | Mắt chụp đã phân tích |
| | `modality` | string | `"FUNDUS"` |
| **Vessel Metrics**| `vessel_density` | float | Mật độ diện tích mạch máu trên diện tích võng mạc (0.0 - 1.0) |
| | `tortuosity_index` | float | Chỉ số xoắn mạch (Arc-length / Chord-length) |
| | `av_ratio` | float | Tỷ lệ đường kính tiểu động mạch / tiểu tĩnh mạch (AVR) |
| | `fractal_dimension`| float | Độ phức tạp phân nhánh phân hình (Fractal Dimension) |
| | `branching_points` | int | Tổng số điểm ngã ba / phân nhánh phát hiện được |
| **Risk Assessment**| `risk_score` | float | Điểm rủi ro tổng hợp chuẩn hóa (0.0: rất thấp ➔ 1.0: rất cao) |
| | `risk_level` | string | Phân hạng: `"LOW"`, `"MODERATE"`, `"HIGH"` |
| | `confidence_score` | float | Độ tin cậy thuật toán (0.0 - 1.0) |
| | `indicators` | array[string]| Các nhận định cụ thể dựa trên chỉ số |
| | `disclaimer` | string | Tuyên bố miễn trừ trách nhiệm y tế bắt buộc (NFR-21) |
| **Segmentation** | `mask_base64` | string (opt) | Ảnh nhị phân mạch máu trắng-đen Base64 |
| | `overlay_base64` | string (opt) | Ảnh gốc phủ viền mạch màu Base64 |

* **Response Example**:
```json
{
  "request_id": "req_aura_20261005_001",
  "patient_id": "PAT-88231",
  "timestamp": "2026-10-08T03:15:05Z",
  "status": "SUCCESS",
  "is_mock": true,
  "model_version": "mock-v0.1",
  "threshold_version": "v0.1",
  "config_version": "v0.1",
  "limitations": "Kết quả mô phỏng (Mock Engine) phục vụ tích hợp giao diện M5 và Backend M2.",
  "processing_time_ms": 46.2,
  "image_info": {
    "width": 512,
    "height": 512,
    "channels": 3,
    "eye_side": "right",
    "modality": "FUNDUS"
  },
  "metrics": {
    "vessel_density": 0.1485,
    "tortuosity_index": 1.165,
    "av_ratio": 0.672,
    "fractal_dimension": 1.475,
    "branching_points": 62
  },
  "risk_assessment": {
    "risk_score": 0.32,
    "risk_level": "LOW",
    "confidence_score": 0.91,
    "indicators": [
      "Mật độ mạch máu võng mạc trong giới hạn bình thường (14.85%)",
      "Chỉ số xoắn mạch (Tortuosity index 1.165) nằm trong khoảng an toàn (<1.25)",
      "Tỷ lệ động mạch/tĩnh mạch AVR (0.672) phù hợp tiêu chuẩn tham chiếu (0.65 - 0.70)"
    ],
    "disclaimer": "Kết quả ước lượng dựa trên phân tích hình thái học võng mạc (Heuristic). Mang tính chất tham khảo kỹ thuật, không thay thế chẩn đoán y khoa chính thức từ bác sĩ chuyên khoa."
  },
  "segmentation": {
    "mask_format": "png_base64",
    "mask_base64": "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAIElEQVR42mP8z8AARAwMDDAGAwg4eP8hH4A8jE+DkS4AAN1dD2N31pX2AAAAAElFTkSuQmCC",
    "overlay_base64": "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAALUlEQVR42mNk+M9AwMDEgAQYGBgYgBwDAyNMMVge5AAc7vj//z8jTBzMAOUiGAB2tBDYn7bX+wAAAABJRU5ErkJggg=="
  }
}
```

---

## 4. Xử lý Lỗi & Mã trạng thái HTTP

Mọi lỗi trả về đều có mã lỗi máy đọc được (`error_code`), thông điệp rõ ràng và thời điểm xảy ra:

| HTTP Code | Mã lỗi nội bộ (`error_code`) | Mô tả hành vi & Quy tắc từ chối |
| :--- | :--- | :--- |
| **400 Bad Request** | `INVALID_IMAGE_PAYLOAD` | Chuỗi Base64 rỗng, sai định dạng giải mã, hoặc dữ liệu nhị phân không phải ảnh hợp lệ (PNG, JPEG, WebP). **Tuyệt đối không fallback tạo ảnh giả.** |
| **422 Unprocessable**| `UNSUPPORTED_MODALITY` | Ảnh gửi lên có modality là `OCT` (chưa hỗ trợ trong v0.1). |
| **422 Unprocessable**| `INVALID_MODALITY` | Modality không hợp lệ (ngoài `FUNDUS`). |
| **422 Unprocessable**| `UNSUPPORTED_MODE` | Mode khác `retina_vessels` (ví dụ `iris_biometrics` hoặc `hybrid` nằm ngoài phạm vi v0.1). |
| **422 Unprocessable**| `SCHEMA_VALIDATION_ERROR` | Thiếu các trường bắt buộc (`request_id`, `image_base64`) hoặc định dạng JSON sai. |
| **500 Server Error** | `INFERENCE_RUNTIME_ERROR` | Lỗi ngoại lệ trong quá trình thực thi suy luận nội bộ. |

* **Cấu trúc Error Response**:
```json
{
  "error_code": "UNSUPPORTED_MODALITY",
  "message": "Định dạng ảnh OCT chưa được hỗ trợ trong phiên bản hiện tại (chỉ hỗ trợ FUNDUS).",
  "details": {
    "supported_modalities": ["FUNDUS"],
    "received_modality": "OCT"
  },
  "timestamp": "2026-10-08T03:15:06Z"
}
```

---

## 5. Quy chuẩn Tuyên bố Miễn trừ Y tế (Medical Disclaimer - NFR-21)
Theo yêu cầu phi chức năng y tế:
> Mọi kết quả trả về từ `AI Core` bắt buộc phải kèm trường `disclaimer` với nội dung khẳng định: Các chỉ số hình học mạch máu và điểm rủi ro là giá trị ước lượng kỹ thuật (Heuristic), phục vụ sàng lọc sơ bộ hỗ trợ quyết định lâm sàng, không thay thế chẩn đoán y khoa chính thức của bác sĩ chuyên khoa mắt/tim mạch.
