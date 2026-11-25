# Biên bản Nghiệm thu Phần mềm AI V2 & Báo cáo Khoa học Trung thực (`W8-M4-02` / `SCRUM-424`)

**Owner:** Đào Duy Quân (Module M4 — AI Core)
**Truy vết RTM:** `FR-3`, `FR-4`, `NFR-15`, `NFR-22`, `NFR-23` · **Test ID:** `TC-W8-M4-02`
**Dữ liệu đối chiếu máy đọc được:** [`docs/benchmarks/w8_v2_acceptance_summary.json`](../benchmarks/w8_v2_acceptance_summary.json)

---

## PHẦN I — Biên bản Nghiệm thu Phần mềm AI V2 (Functional Software Handoff)

Phần này nghiệm thu **năng lực vận hành kỹ thuật của phần mềm AI nghiên cứu V2** theo phạm vi đồ án (không đánh đồng với chứng nhận y khoa lâm sàng).

### 1. Tổng hợp Hạng mục Chức năng đã Xác minh

| Hạng mục phần mềm V2 | Bằng chứng thực thi & Cấu hình | Trạng thái chức năng | Giới hạn bắt buộc |
|---|---|---|---|
| **Hợp đồng AI REST 1.3.0 & RS256 Service Auth** | `contracts/ai-openapi-1.3.yaml`, `src/ai-core/app/schemas/analysis.py`, `src/ai-core/app/auth/service_jwt.py` | **VERIFIED** | Nội bộ RS256 fail-closed (`TTL <= 300s`), `patient_id = null`, từ chối `OCT`/`XRAY` bằng `HTTP 422 UNSUPPORTED_MODALITY` (**OCT DEFERRED**). |
| **Cổng chất lượng ảnh khó & Fail-Closed** | `src/ai-core/tests/test_w7_m4_sprint7.py` (`TC-W7-M4-03`) | **VERIFIED** | Positive control pass `200 OK`; ảnh tối, lóa, mờ, bytes hỏng trả `422 LOW_QUALITY_IMAGE` hoặc `400 INVALID_IMAGE_PAYLOAD`. |
| **Frangi v0.2 Golden Parity & Ngân sách CPU/RAM** | `docs/benchmarks/w7_latency_ram_profile_v1.0.json` (`1c8049f5...4e65`) | **VERIFIED** | Golden Parity `Dice = 1.000000` (`0` pixel lệch trên `999×960`); p95 warm `1653.1 ms < 20,000 ms` SRS SLA; peak RSS `157.51 MB < 2048 MiB`. |
| **Chặn quá tải đồng thời (Backpressure)** | `src/ai-core/app/main.py` (`CNN_SLOT = BoundedSemaphore(1)`) | **VERIFIED** | Trả ngay `HTTP 429 RESEARCH_MODEL_BUSY` + header `Retry-After: 1`, không gây OOMKill container `2g`. |
| **Manifest Rollout & Hash Fail-Closed** | `src/ai-core/scripts/manage_w8_artifact_registry.py`, `src/ai-core/app/services/runtime_registry.py` | **VERIFIED** | Pin SHA-256 toàn bộ cấu hình/weights (`ce448cf7...acd`), atomic switch + rollback, sai hash fail-closed (`409` / `503`). |
| **Tách biệt Kết quả AI `AUTO` vs `Doctor Review`** | `AnalysisResponse` (`risk_available=false`, `research`), `MedicalReportPdfRenderer.cs` | **VERIFIED** | **Tuyệt đối không sinh nhãn bác sĩ (`expert_confirmed` / `DoctorApproved = true`) từ đầu ra `AUTO`.** |

### 2. Thể hiện Phạm vi V2 trên Giao diện Web UI, PDF & Demo

1. **Web UI (`src/web/aura-web/src/pages/AnalysisResultPage.tsx`):**
   - Hiển thị rõ mục **Kết quả AI nghiên cứu**, ghi nhận đúng `Grad-CAM DR — crop 224×224` (`IMAGENET_CENTER_CROP_224`) và câu cảnh báo *"Kết quả này không phân độ ICDR và không thay thế kết luận lâm sàng."*
   - Khi `avRatio === null` hoặc `measurement_available === false`: hiển thị trạng thái **Chưa có kết quả AVR**, nêu `unavailable_reason` và khẳng định *"Thiếu AVR không có nghĩa nguy cơ thấp."*
   - Khi trạng thái cuối là `LOW_QUALITY` / `FAILED` / `REJECTED`: hiển thị `error.code` + `error.message`, dừng polling, không tự đoán chỉ số.
2. **Báo cáo PDF đơn ca (`src/services/AURA.Analysis.Api/Upload/MedicalReportPdfRenderer.cs`):**
   - In tiêu đề riêng `RESEARCH OUTPUTS - NOT A CLINICAL DIAGNOSIS`, hiển thị rõ `Grad-CAM uses an ImageNet center crop (Resize 256 -> Crop 224x224), not full-image lesion localization or ICDR grading`, hiển thị `AVR unavailable: <reason>` khi thiếu AVR và pin `Research artifact hashes: AVR=... | DR=...`.
   - `IcdrLevel` chỉ xuất hiện khi có bản ghi `DoctorReportReview` được bác sĩ phê duyệt (`review?.IsApproved == true`).
3. **Báo cáo hàng loạt & Export nghiên cứu (`AnalysisReportService.cs`, `ExportService.cs`):**
   - Pin đủ `ResultVersion`, `ModelVersion`, `ConfigVersion`, `ThresholdVersion`, `ReviewVersion`, giữ nguyên `MeasurementAvailable` và `Avr = null` khi không đo được.

---

## PHẦN II — Báo cáo Kiểm định Khoa học Độc lập & Trung thực (Scientific Validation)

**Trạng thái khoa học tổng quát:** `RESEARCH_ONLY_PARTIAL_GATE_OPEN` (`clinicalValidation = false`, `icdrGradingSupported = false`).

### 1. Bảng Số liệu Khoá Từng Tập Độc lập (Không gộp Cohort, Không giấu `unavailable`)

| Tập dữ liệu | Manifest SHA-256 (`LF`) | Split & Mẫu số (`Denominator`) | Độ phủ (`Coverage`) | Số đo thực tế (`Metric` / `Bias`) | Nguồn Nhãn Tham chiếu (`Annotation Source`) |
|---|---|---|---|---|---|
| **CHASE_DB1** (`CC BY 4.0`, 28 ảnh / 14 người) | `b22bb7f635aad4c1ef670abd15a68e038d9c5d9a1383a01976f8ae7664a6455a` | `16 train / 6 val / 6 test`<br>Mẫu số test: `6` | `6 / 6` (`1.0`) | Test 6 `Mean Dice = 0.420052`, `Mean IoU = 0.266240`<br>(Full 28: `Mean Dice = 0.401484`; `bias: null`) | Mask phân vùng mạch máu người đánh dấu thứ nhất (`_1stHO.png`; giữ `_2ndHO.png` đối chiếu). Không có nhãn bệnh lý DR/AVR. |
| **HRF45** (`FAU Academic`, 45 ảnh) | `a6d0d719066d01e113ca2e1ade0e89324e99ce36ef5772afd1742a55973a11ce` | Nhị phân `DR vs H`: `18 train / 6 val / 6 test`<br>`15 glaucoma_ood` tách riêng<br>Mẫu số test: `6` | `6 / 6` (`1.0`) | `Val AUC = 0.888889`, `Test AUC = 1.0`<br>`glaucoma_ood = 15` tách riêng khỏi AUC<br>Frangi HRF test 9 `Mean Dice = 0.483415` | Nhãn phân nhóm lâm sàng gốc của Đại học Erlangen-Nuremberg (15 DR, 15 Healthy, 15 Glaucoma) + mask mạch chuyên gia FAU. **Không phải nhãn ICDR 5 mức.** |
| **AVRDB100** (`CC BY 4.0`, 99 ca hợp lệ, bỏ `IM000189`) | `2ae7cba1d278178fc337dd622a7fba12609e2afbba0d9af1eefc980c89ac9211` | `70 train / 15 val / 14 test`<br>Mẫu số toàn bộ: `99` | Runtime CI (chưa bundle trọng số CC-BY-NC-4.0): `0 / 99` (`0.0`) | Tham chiếu `29` ca val+test (`inspire_avr_benchmark_v1.0.json`, SHA `e8cbdf68...9120`): `Mean AVR MAE = 0.2335`, **`Mean AVR Bias = -0.2136`**, `Mean Vessel Dice = 0.3494` | Mask động/tĩnh mạch vector/ảnh của bộ công bố Mendeley DOI `10.17632/3csr652p9y.2` (**chỉ là mask surrogate**, không thay bảng đo 2 chuyên gia Iowa). |
| **Iowa40** (Ngoài repo & ZIP) | `null` (Không lưu trong repo/ZIP) | Tập kiểm định độc lập `40` ca (`calibrationAllowed = false`) | `0 / 40` (`0.0`) | **`NOT_TESTED_MISSING_EXTERNAL_DATASET`**<br>Ngưỡng mục tiêu: `abs(bias) <= 0.02` | Bảng đối chiếu AVR độc lập của 2 chuyên gia Đại học Iowa (`GT/GT.txt`). |

### 2. Trạng thái Gate Khoa học `SCRUM-373` & Cam kết Trung thực

- **`SCRUM-373` giữ nguyên `OPEN_NOT_PASSED`:** Độ lệch baseline surrogate trên 29 ca INSPIRE-AVR đang là `bias = -0.2136` (`MAE = 0.2335`), chưa đạt ngưỡng khoa học `|bias| <= 0.02` và bộ chuẩn `Iowa40` chưa có mặt trong workspace/CI. Không được chuyển `FAIL` / `OPEN_NOT_PASSED` thành `PASS`.
- **Không dùng ground-truth lúc dự đoán:** `audit_rgb_only_inference()` xác nhận mọi hàm predict công khai và nội bộ chỉ nhận RGB; `OCT` tiếp tục bị từ chối ở hợp đồng `1.3.0`.
- **Liên kết kiểm định tự động:** Chạy `python src/ai-core/scripts/verify_w8_v2_acceptance.py --verify` để đối chiếu toàn bộ SHA-256, số liệu cohort và bất biến trên cả JSON, Markdown, Web UI và PDF renderer.
