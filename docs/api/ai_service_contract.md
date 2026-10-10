# HỢP ĐỒNG GIAO TIẾP AI NỘI BỘ — MODULE M4

> **Dự án**: AURA
> **Jira**: SCRUM-61 — OpenAPI AI và schema output; SCRUM-62 — FastAPI Docker và mock có nhãn
> **Người biên soạn ban đầu**: Đào Duy Quân — M4
> **Bên sử dụng chính**: M2 — Analysis Worker
> **Phiên bản API AI nội bộ**: 1.1.0
> **Ngày cập nhật**: 2026-10-10
> **Trạng thái**: Runtime mock và bộ test Python đã kiểm tra đạt; chưa nghiệm thu tích hợp Worker hoặc toàn luồng.

## 1. Phạm vi và trạng thái triển khai

AI Core là API nội bộ dành cho Analysis Worker.

Kiến trúc dự kiến:

- Web/NiFi gửi yêu cầu qua Gateway đến Analysis API.
- Analysis API tạo yêu cầu phân tích và job.
- Analysis Worker nhận job, lấy ảnh và gọi AI Core.
- Worker xử lý kết quả, lưu dữ liệu và cập nhật trạng thái phân tích.

Các bước tạo job, xử lý hàng đợi, lưu kết quả, retry và cập nhật trạng thái
là trách nhiệm tích hợp của M2. Bộ test AI hiện tại không xác nhận các
bước đó đã được triển khai hoặc chạy đúng.

Runtime AI hiện tại:

- Kiểm tra Base64, định dạng, tính toàn vẹn và giới hạn ảnh.
- Trả chỉ số cố định phục vụ kiểm thử.
- Tạo mask và overlay tổng hợp cùng kích thước ảnh đầu vào.
- Hỗ trợ các kịch bản giả lập SUCCESS, WARNING, LOW_QUALITY và INFERENCE_ERROR.
- Chưa tích hợp Frangi hoặc U-Net vào endpoint phân tích.
- Chưa đánh giá chất lượng ảnh thật hoặc hiệu quả lâm sàng.

Benchmark Frangi chạy bằng script riêng không phải thuật toán đang
được endpoint này sử dụng.

## 2. Nguồn contract và địa chỉ truy cập

| Nội dung | Địa chỉ hoặc file |
|---|---|
| OpenAPI YAML | `contracts/ai_service_openapi.yaml` |
| OpenAPI JSON sinh từ YAML | `contracts/ai_service_openapi.json` |
| Schema Python | `src/ai-core/app/schemas/analysis.py` |
| Runtime API | `src/ai-core/app/main.py` |
| Mock service | `src/ai-core/app/services/mock_service.py` |
| Test kịch bản | `src/ai-core/tests/test_mock_scenarios.py` |
| AI trong mạng Docker | `http://ai-core:8000` |
| AI trên máy phát triển | `http://127.0.0.1:8000` |
| Swagger UI của runtime | `http://127.0.0.1:8000/docs` |
| OpenAPI do runtime sinh | `http://127.0.0.1:8000/openapi.json` |

YAML là nguồn để sinh file JSON trong repository.
OpenAPI do FastAPI sinh tại `/openapi.json` được kiểm tra riêng với contract.

API AI nội bộ phiên bản 1.1.0 khác contract Analysis công khai phiên bản 0.6.0.

Frontend dùng contract Analysis công khai.
Worker dùng contract AI nội bộ; kiến trúc không yêu cầu Frontend gọi trực tiếp AI Core.

## 3. Endpoint nền tảng

### GET /health

Trả trạng thái service:

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

`model_loaded=false` phản ánh runtime chưa nạp mô hình thật.

`version=0.1.0` trong health là phiên bản service hiện tại,
không phải phiên bản contract AI 1.1.0.

### GET /api/v1/platform/ping

Kiểm tra kết nối:

```json
{
  "service": "AURA.AiCore",
  "message": "AI Core template is running",
  "timestamp": "2026-10-10T03:00:00Z"
}
```

Health và ping hoạt động không chứng minh mô hình hoặc toàn luồng hoạt động.

## 4. POST /api/v1/analyze

Endpoint nhận JSON và trả response đồng bộ cho bên gọi.

Headers:

```http
Content-Type: application/json
Accept: application/json
```

### 4.1. Request

| Trường | Kiểu | Bắt buộc | Mặc định | Quy ước |
|---|---|---|---|---|
| `request_id` | string | Có | — | ID do bên gọi cung cấp; được trả lại trong response thành công |
| `patient_id` | string hoặc null | Không | null | ID bệnh nhân nếu có |
| `eye_side` | string | Không | unknown | left, right hoặc unknown |
| `mode` | string | Không | retina_vessels | Chỉ hỗ trợ retina_vessels |
| `modality` | string | Không | FUNDUS | FUNDUS; OCT bị từ chối |
| `image_base64` | string | Có | — | Base64 của ảnh hợp lệ |
| `include_mask` | boolean | Không | true | Yêu cầu mask giả lập |
| `include_overlay` | boolean | Không | true | Yêu cầu overlay giả lập |

Giới hạn ảnh nội bộ:

- Định dạng: PNG, JPEG, WEBP.
- Dung lượng dữ liệu giải mã tối đa: 10 MiB.
- Số pixel tối đa: 16.000.000.
- Kiểm tra kích thước và số pixel trước khi `load()` dữ liệu ảnh.
- Kiểm tra tính toàn vẹn và giải mã ảnh trước khi áp dụng kịch bản mock.
- Không thay ảnh hỏng bằng ảnh giả để trả thành công.

Public Analysis API vẫn áp dụng định dạng và giới hạn của contract công khai.
Việc AI nội bộ hỗ trợ WEBP không tự mở rộng định dạng public API.

`request_id` và `patient_id` hiện là string trong schema AI.
Không coi đây là bằng chứng runtime đã kiểm tra UUID, quyền truy cập
hoặc quan hệ giữa bệnh nhân và job.

M2 phải xác định và kiểm tra ánh xạ ID từ job sang request AI.
Không dùng `request_id` để chọn kịch bản mock hoặc thay cơ chế idempotency.

### 4.2. Request example

Ảnh trong ví dụ là PNG RGB tổng hợp 64 × 48, không phải ảnh đáy mắt.
Hai tùy chọn ảnh đầu ra được tắt để ví dụ response không cần chứa Base64.

```json
{
  "request_id": "demo-ai-001",
  "patient_id": null,
  "eye_side": "unknown",
  "mode": "retina_vessels",
  "modality": "FUNDUS",
  "image_base64": "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAAZUlEQVR4nO3PUQkAIBTAwBfCEMaxfxpD+HEIgwW4zdnr64YLGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEtaEALGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEteOwCadmQWyWfhSMAAAAASUVORK5CYII=",
  "include_mask": false,
  "include_overlay": false
}
```

## 5. Response thành công

HTTP 200 hiện trả `SUCCESS` hoặc `WARNING`.

Enum schema vẫn chứa `FAILED`, nhưng runtime hiện trả lỗi bằng
`ErrorResponse` với HTTP 4xx/5xx, không dùng `200 / FAILED`.

| Trường | Ý nghĩa hiện tại |
|---|---|
| `request_id`, `patient_id` | ID từ request |
| `timestamp` | Thời điểm UTC |
| `status` | SUCCESS hoặc WARNING |
| `warnings` | Danh sách object có `code` và `message`; SUCCESS mặc định rỗng |
| `is_mock` | true |
| `model_version` | mock-v0.1 |
| `threshold_version` | v0.1 |
| `config_version` | v0.1 |
| `limitations` | Giới hạn của runtime giả lập |
| `processing_time_ms` | Thời gian xử lý trong hàm mock, gồm kiểm tra ảnh và tạo kết quả |
| `image_info` | Kích thước và số kênh của ảnh giải mã |
| `metrics` | Chỉ số cố định, không đo từ ảnh |
| `risk_assessment` | Giá trị giả lập, không phải rủi ro lâm sàng đã kiểm chứng |
| `segmentation` | Kết quả ảnh giả lập hoặc null |

Không dùng `processing_time_ms` để kết luận đạt NFR toàn luồng.
Không có cam kết runtime xử lý mỗi ảnh trong khoảng 45 ms.

### 5.1. SUCCESS example

Thời gian trong ví dụ chỉ minh họa cấu trúc.

```json
{
  "request_id": "demo-ai-001",
  "patient_id": null,
  "timestamp": "2026-10-10T03:00:00Z",
  "status": "SUCCESS",
  "warnings": [],
  "is_mock": true,
  "model_version": "mock-v0.1",
  "threshold_version": "v0.1",
  "config_version": "v0.1",
  "limitations": "Kết quả mô phỏng (Mock Engine) phục vụ kiểm thử tích hợp; chưa tích hợp thuật toán phân vùng thật. Mask và overlay là hình vẽ tổng hợp, không phải kết quả từ ảnh.",
  "processing_time_ms": 10.0,
  "image_info": {
    "width": 64,
    "height": 48,
    "channels": 3,
    "eye_side": "unknown",
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
      "[MOCK] Mật độ mạch giả lập: vessel_density=0.1485",
      "[MOCK] Chỉ số uốn lượn giả lập: tortuosity_index=1.165",
      "[MOCK] Tỷ lệ động/tĩnh mạch giả lập: av_ratio=0.672"
    ],
    "disclaimer": "Kết quả hoàn toàn là dữ liệu giả lập phục vụ kiểm thử tích hợp. Không phải kết quả chẩn đoán và không thay thế đánh giá của bác sĩ."
  },
  "segmentation": null
}
```

### 5.2. WARNING

WARNING vẫn có đầy đủ `metrics`, `risk_assessment` và ảnh đầu ra
nếu bên gọi yêu cầu.

Cảnh báo được trả trong:

```json
[
  {
    "code": "AI_WARNING",
    "message": "[MOCK] Cảnh báo kỹ thuật phục vụ kiểm thử. Kết quả giả lập vẫn được trả đầy đủ; cảnh báo này không xác nhận ảnh kém chất lượng."
  }
]
```

WARNING không mặc định đồng nghĩa LOW_QUALITY.
Không tự loại bỏ kết quả chỉ vì `status=WARNING`.

### 5.3. Mask và overlay

- `mask_format=png_base64`.
- Mask là PNG mode L, chỉ chứa pixel 0 hoặc 255.
- Overlay là PNG mode RGB.
- Cả hai giữ đúng chiều rộng và chiều cao ảnh đầu vào.
- Mask là hình vẽ tổng hợp, không phải phân vùng mạch từ ảnh.
- Overlay hiện không phủ trên ảnh gốc.
- Tùy chọn nào tắt thì trường Base64 tương ứng là null.
- Nếu cả hai tùy chọn tắt thì `segmentation=null`.

Theo thiết kế tích hợp, Worker lưu ảnh đầu ra vào storage riêng tư
và trả tham chiếu `fileId` theo contract công khai.
Không đưa Base64 hoặc object key nội bộ trực tiếp vào public response.

Việc lưu file, tạo fileId và kiểm tra quyền truy cập chưa được
bộ test AI hiện tại xác nhận.

## 6. ErrorResponse

| HTTP | error_code | Trường hợp |
|---|---|---|
| 400 | INVALID_IMAGE_PAYLOAD | Base64 sai, ảnh hỏng, định dạng không hỗ trợ hoặc vượt giới hạn |
| 422 | UNSUPPORTED_MODALITY | OCT |
| 422 | INVALID_MODALITY | Modality khác FUNDUS/OCT |
| 422 | UNSUPPORTED_MODE | Mode khác retina_vessels |
| 422 | SCHEMA_VALIDATION_ERROR | Sai cấu trúc request hoặc thiếu trường bắt buộc |
| 422 | LOW_QUALITY_IMAGE | Kịch bản từ chối chất lượng giả lập đã bật |
| 500 | INFERENCE_RUNTIME_ERROR | Lỗi kỹ thuật hoặc cấu hình mock không hợp lệ |

ErrorResponse có `error_code`, `message`, `details` và `timestamp`.
Lỗi không trả `metrics` hoặc `segmentation`.

### 6.1. Chất lượng ảnh giả lập

```json
{
  "error_code": "LOW_QUALITY_IMAGE",
  "message": "[MOCK] Giả lập từ chối ảnh vì chất lượng không đủ. Chưa thực hiện đánh giá chất lượng ảnh thật.",
  "details": {
    "is_mock": true,
    "reason": "MOCK_LOW_QUALITY",
    "quality_assessment_performed": false
  },
  "timestamp": "2026-10-10T03:00:00Z"
}
```

Đây là kết quả giả lập có chủ đích.
Không dùng nó làm bằng chứng đã triển khai thuật toán đánh giá chất lượng.

### 6.2. Lỗi kỹ thuật

```json
{
  "error_code": "INFERENCE_RUNTIME_ERROR",
  "message": "Lỗi nội bộ khi xử lý AI.",
  "details": null,
  "timestamp": "2026-10-10T03:00:00Z"
}
```

Exception và traceback được ghi trong log server,
không trả chi tiết đó cho bên gọi.

## 7. Cấu hình kịch bản mock

Kịch bản được chọn bằng biến môi trường phía server.

| AURA_MOCK_SCENARIO | AURA_MOCK_SCENARIOS_ENABLED | Kết quả |
|---|---|---|
| SUCCESS hoặc không đặt | Không cần bật | 200 / SUCCESS |
| WARNING | true | 200 / WARNING, có kết quả |
| LOW_QUALITY | true | 422 / LOW_QUALITY_IMAGE |
| INFERENCE_ERROR | true | 500 / INFERENCE_RUNTIME_ERROR |
| WARNING, LOW_QUALITY hoặc INFERENCE_ERROR | Không phải true | 500 / INFERENCE_RUNTIME_ERROR |
| Giá trị scenario không hỗ trợ | Bất kỳ | 500 / INFERENCE_RUNTIME_ERROR |

Các kịch bản bổ sung phục vụ kiểm thử ở môi trường phát triển.
Cấu hình áp dụng cho tiến trình AI, không phải tham số chọn theo từng request.

Ảnh phải qua kiểm tra trước khi áp dụng scenario.
Ví dụ, Base64 hỏng trong scenario LOW_QUALITY vẫn trả
400 / INVALID_IMAGE_PAYLOAD.

## 8. Hướng dẫn tích hợp cho M2

Đây là yêu cầu tích hợp; chưa phải kết quả nghiệm thu Worker.

| Kết quả AI | Cách xử lý cần triển khai |
|---|---|
| 200 / SUCCESS | Kiểm tra response, lưu kết quả và cập nhật trạng thái theo contract công khai |
| 200 / WARNING | Giữ kết quả và cảnh báo; không mặc định chuyển thành lỗi chất lượng |
| 422 / LOW_QUALITY_IMAGE | Xử lý nhánh chất lượng không đạt theo state machine; không coi là thành công |
| 400 / INVALID_IMAGE_PAYLOAD | Xử lý lỗi dữ liệu; không retry lặp lại cùng payload hỏng |
| Các lỗi 422 khác | Xử lý lỗi schema hoặc cấu hình nghiệp vụ; không retry mù |
| 500 / INFERENCE_RUNTIME_ERROR | Phân loại nguyên nhân và áp dụng chính sách retry hữu hạn của Worker |
| Timeout hoặc mất kết nối | Xử lý lỗi giao tiếp theo chính sách Worker |

Không coi mọi lỗi 500 đều có thể khắc phục bằng retry:
cấu hình mock sai cần sửa cấu hình.

M2 cần kiểm chứng riêng:

- Ánh xạ ID giữa analysis, job, request AI và kết quả.
- Truy vết correlation ID xuyên suốt.
- Ánh xạ mã lỗi nội bộ sang mã lỗi và trạng thái công khai.
- Idempotency và xử lý job trùng.
- Giới hạn số lần retry và kết thúc job khi hết retry.
- Lưu mask/overlay riêng tư và tạo fileId.
- Không làm mất nhãn mock khi lưu hoặc hiển thị kết quả.

## 9. Kiểm chứng

Chạy từ thư mục gốc repository:

```powershell
$env:PYTHONPATH = (Join-Path (Get-Location) "src/ai-core")

.\.venv-dataset\Scripts\python.exe src/ai-core/scripts/sync_openapi_contracts.py --check

.\.venv-dataset\Scripts\python.exe -m pytest src/ai-core/tests -q

.\.venv-contract\Scripts\python.exe scripts/validate_analysis_contract.py
```

Kết quả đã kiểm tra ngày 2026-10-10:

- YAML và JSON OpenAPI AI đồng bộ.
- Bộ test AI: 29 passed.
- Contract Analysis công khai: OpenAPI 0.6.0, 31 hash đạt.
- Các kịch bản SUCCESS, WARNING, LOW_QUALITY và INFERENCE_ERROR đã được kiểm tra.
- Ảnh hỏng bị từ chối trước kịch bản chất lượng giả lập.

Các test được chạy trên môi trường Python local.
Chưa xác nhận container Docker đã build lại với phiên bản này.

Các kết quả trên không chứng minh đã hoàn thành:

- Worker và hàng đợi.
- Phân quyền hoặc service token.
- NiFi gửi thành công tới backend.
- Dedup/idempotency và retry toàn luồng.
- Hiệu năng toàn hệ thống.
- Phân vùng thật hoặc đánh giá rủi ro lâm sàng.

## 10. Giới hạn y tế

Chỉ số, mức rủi ro, confidence, mask và overlay hiện là dữ liệu giả lập.

Không dùng các giá trị này để đánh giá bệnh nhân hoặc kết luận
nguy cơ tim mạch. `risk_assessment.disclaimer` và nhãn `is_mock`
phải được giữ rõ khi tích hợp, lưu trữ và trình bày kết quả.