# Đối chiếu RTM & Bằng chứng Nghiệm thu Module M4 (Sprint 6–8)

**Tài liệu gốc RTM:** `requirements/requirements_traceability_matrix_v0.2.md` (trong gói chốt `AURA_SPRINT6_READY_2026-11-16.zip`)
**Báo cáo tóm tắt JSON:** [`docs/benchmarks/w8_v2_acceptance_summary.json`](../benchmarks/w8_v2_acceptance_summary.json)

---

## 1. Bảng Truy vết Yêu cầu Thuộc Phạm vi M4

> Nguyên tắc: Giữ trung thực trạng thái `PARTIAL` đối với các yêu cầu liên module hoặc chưa có chứng nhận lâm sàng/đóng gate khoa học; **không tự ép `100% VERIFIED/PASS`**.

| ID | Yêu cầu (tóm tắt) | Trạng thái RTM | Bằng chứng & Source Module M4 | Test ID đã thực thi / liên quan | Phần ngoài M4 / Giới hạn còn mở |
|---|---|---|---|---|---|
| **FR-3** | Xem kết quả AI, mức nguy cơ hợp lệ; tách biệt AI và đánh giá bác sĩ | `PARTIAL` | `src/ai-core/app/schemas/analysis.py`, `AnalysisResultPage.tsx`, `MedicalReportPdfRenderer.cs`, `docs/ai/w8_m4_v2_software_and_scientific_acceptance.md` | `TC-FR-3`, `TC-W7-M4-03`, `TC-W8-M4-02`, `TC-W8-M4-03` | `clinicalValidation = false`; chỉ hỗ trợ nghiên cứu V2, bác sĩ chịu trách nhiệm xác nhận lâm sàng. |
| **FR-4** | Xem ảnh chú thích vùng mạch & yếu tố giải thích được hỗ trợ | `PARTIAL` | `frangi_service.py` (mask/overlay gốc), `dr_cnn_service.py` (`GRAD_CAM` trên `IMAGENET_CENTER_CROP_224`), `avr_service.py` (`av_overlay`) | `TC-FR-4`, `TC-W7-M4-03`, `TC-W8-M4-02`, `TC-W8-M4-03` | Grad-CAM phản ánh vùng crop trung tâm `224×224`, không phải mask tổn thương toàn khung ảnh. |
| **NFR-15** | Annotation & giải thích nguy cơ rõ ràng, đúng thuật toán & giới hạn | `PARTIAL` | `docs/data_cards/model_card_v1.3.md`, `docs/ai/w7_m4_research_limitations_handover.md`, `docs/ai/w8_m4_v2_software_and_scientific_acceptance.md` | `TC-NFR-15`, `TC-W8-M4-02` | Cần review lâm sàng thực tế của chuyên khoa; `SCRUM-373` giữ `OPEN_NOT_PASSED`. |
| **NFR-16** | Cập nhật model/threshold/config không downtime & rollback | `PARTIAL` | `src/ai-core/app/services/runtime_registry.py`, `src/ai-core/config/artifact-manifests.json`, `manage_w8_artifact_registry.py` | `TC-NFR-16`, `TC-W6-M4-02`, `TC-W8-M4-01` | Rollout ở mức M4 AI Core đã xác minh; triển khai hạ tầng blue-green sản phẩm do M1 quản lý. |
| **NFR-21** | AI Engine giao tiếp bằng RESTful API theo contract | `PARTIAL` | `contracts/ai-openapi-1.3.yaml`, `src/ai-core/app/main.py`, `src/ai-core/app/auth/service_jwt.py` | `TC-NFR-21`, `test_frangi_sprint3.py`, `TC-W8-M4-01` | Contract + RS256 internal scope pass ở M4; tích hợp đầu cuối đa dịch vụ do QA tổng hợp. |
| **NFR-22** | AI output có yếu tố giải thích phù hợp thuật toán | `PARTIAL` | `dr_cnn_service.py` (autograd `layer4` Grad-CAM), `avr_service.py` (Zone B 2R–3R overlay), `frangi_service.py` | `TC-NFR-22`, `test_dr_screening_gradcam.py`, `TC-W8-M4-02` | Không tạo CNN/Grad-CAM giả; `OCT` bị từ chối tường minh (`DEFERRED`). |
| **NFR-23** | Truy vết được AI version, config, threshold, hash & review version | `PARTIAL` | `docs/benchmarks/w8_artifact_registry_v1.0.json`, `docs/benchmarks/w8_v2_acceptance_summary.json`, `MedicalReportPdfRenderer.cs`, `AnalysisReportService.cs` | `TC-NFR-23`, `TC-W8-M4-01`, `TC-W8-M4-02` | Phần AI Core & PDF/snapshot đã pin đủ; lưu trữ DB liên dịch vụ đồng sở hữu với M2/M1. |

---

## 2. Danh mục Kiểm thử Tương ứng Sprint 8 (`TC-W8-M4-01..03`)

- **`TC-W8-M4-01` (`SCRUM-423`)**: `src/ai-core/tests/test_w8_m4_sprint8.py` + `python src/ai-core/scripts/manage_w8_artifact_registry.py --verify`.
  - Positive: Kiểm định đủ 7 artifact, 4 cohort, 3 manifest SHA-256, `git ls-files` sạch, đóng gói repro ZIP hợp lệ với `PACK_MANIFEST.json`, atomic rollout & rollback qua `RuntimeManifestRegistry`.
  - Negative: Đổi 1 byte file bị từ chối fail-closed; `--require-local-weights` thiếu file `.pt`/`.safetensors`/`.onnx` báo FAIL; thử đóng gói file chứa `iowa` hoặc `.pem` vào ZIP bị chặn tuyệt đối.
- **`TC-W8-M4-02` (`SCRUM-424`)**: `src/ai-core/tests/test_w8_m4_sprint8.py` + `python src/ai-core/scripts/verify_w8_v2_acceptance.py --verify`.
  - Positive: Khớp 100% SHA-256, số liệu cohort (`0.420052`, `0.888889`, `1.0`, `-0.2136`, `0.2335`), `annotationSource`, trạng thái UI (`AnalysisResultPage.tsx`) và PDF (`MedicalReportPdfRenderer.cs`).
  - Negative: Sửa lệch metric/hash, đặt `SCRUM-373 = PASS`, đặt `expertConfirmedGeneratedFromAuto = true` hoặc bỏ `annotationSource` đều bị từ chối ngay.
- **`TC-W8-M4-03` (`SCRUM-425`)**: `src/ai-core/tests/test_w8_m4_sprint8.py` + `python src/ai-core/scripts/verify_w8_defense_script.py --verify`.
  - Positive: Thực thi trực tiếp `measure_avr_prediction` trên ca đo được và ca unavailable; kiểm tra thời lượng chữ bài nói 3 phút, thuật ngữ khớp mã nguồn và kiểm định 2 JSON mẫu qua `AnalysisResponse.model_validate(...)`.
  - Negative: Thay `avr: null` bằng `0`, bật `risk_available: true` hoặc thêm nhận định ICDR giả đều làm verifier báo lỗi.
