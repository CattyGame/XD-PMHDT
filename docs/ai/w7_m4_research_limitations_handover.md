# Bàn giao Giới hạn Nghiên cứu & Regression Ảnh khó — Module M4 (Sprint 7 / W7-M4-03 / SCRUM-407)

**Phạm vi tài liệu:** Bàn giao kỹ thuật chính thức cho nhánh AI Core (`src/ai-core`), hợp đồng API `1.3.0`, giao diện hiển thị kết quả (`AnalysisResultPage.tsx`), và bộ kiểm thử ảnh khó / fail-closed của Module M4.
**Trạng thái lâm sàng:** `clinicalValidation = false`, `icdrGradingSupported = false` — **chỉ phục vụ nghiên cứu / hỗ trợ tham khảo kỹ thuật, không phải thiết bị y tế hay công cụ chẩn đoán lâm sàng.**

---

## 1. Phạm vi chức năng đã xác minh vs. Giới hạn khoa học hiện hữu

| Thành phần | Phạm vi đã xác minh (Functional / Engineering) | Giới hạn khoa học bắt buộc nêu rõ |
| :--- | :--- | :--- |
| **Frangi v0.2 Vessel Segmentation** | Chạy CPU ổn định trên ảnh gốc (`RGB -> Green -> Gaussian -> 6-pass separable Hessian`), giữ nguyên kích thước ảnh, Golden Parity `Dice = 1.000000` (`0` pixel lệch so với `skimage.filters.frangi`). | Thuật toán lọc giải tích không học sâu: `CHASE_DB1` Mean Dice `~0.40`, `HRF` Mean Dice `~0.48`. Không trả nguy cơ lâm sàng trong cấu hình mặc định (`risk_available=false`, `risk_assessment=null`). |
| **HRF Binary DR CNN (`ResNet34`)** | Phân loại nhị phân nghiên cứu (`DR` vs `Healthy`) trên HRF45 với chia tách theo bệnh nhân, kiểm tra SHA-256 checkpoint fail-closed. | **Không phải phân độ 5 mức ICDR** (`notIcdrGrading=true`). Cỡ mẫu nhỏ (HRF45: 18 train / 6 val / 6 test; 15 ca Glaucoma được tách riêng thành `glaucoma_ood`, không gộp vào AUC DR vs Healthy). |
| **Grad-CAM Explainability** | Tạo bản đồ giải thích gradient thật (`method=GRAD_CAM`) từ lớp tích chập cuối `layer4`. | **Giới hạn hình học (`IMAGENET_CENTER_CROP_224`):** ảnh đầu vào được `Resize(shorter_edge=256) -> CenterCrop(224x224)` trước khi tính Grad-CAM. Bản đồ nhiệt **chỉ phản ánh vùng crop trung tâm 224×224**, không phải mask tổn thương trên toàn bộ ảnh fundus gốc. |
| **AVR V8 (Zone B / Knudtson)** | Đo đường kính mạch native-pixel trong Zone B (`2R–3R` từ tâm đĩa thị) và tính CRAE/CRVE/AVR theo công thức Knudtson cải tiến (6 động mạch + 6 tĩnh mạch). | **Chính sách Abstention bắt buộc:** khi không định vị được đĩa thị (`OPTIC_DISC_NOT_LOCALIZED`), Zone B bị cắt biên (`INCOMPLETE_ZONE_B`), hoặc không đủ 6 nhánh độc lập (`INSUFFICIENT_SIX_VESSEL_SETS`), hệ thống **bắt buộc trả `avr = null` + `unavailable_reason`** — tuyệt đối không thay bằng `0` hay `LOW risk`. Ca `unavailable` luôn được giữ trong mẫu số `coverage = measured / denominator`. |

---

## 2. Gate khoa học mở riêng (`SCRUM-373`)

- **Trạng thái:** `OPEN_NOT_PASSED` (`autoCloseAllowed = false`, `iowaGatePassed = false`).
- **Tiêu chí khoa học:** `|bias| <= 0.02` trên bảng đối chiếu 40 ca Iowa two-observer (`Iowa40`).
- **Số liệu baseline đã ghi nhận:** `bias = -0.2136`, `MAE = 0.2335`.
- **Nguyên tắc bàn giao:** Việc hoàn tất các hạng mục kỹ thuật Sprint 7 (`W7-M4-01`, `W7-M4-02`, `W7-M4-03`) **không đóng tự động `SCRUM-373`**. Bảng `Iowa40` là dữ liệu ngoài không đóng gói trong Git/ZIP và luôn được báo cáo trung thực là `NOT_TESTED_MISSING_EXTERNAL_DATASET` trong báo cáo tự động (`docs/benchmarks/v2_evaluation_report_s7.json`).

---

## 3. Ma trận kiểm thử Ảnh khó & Fail-Closed (`TC-W7-M4-03`)

Tất cả các ca ảnh khó dưới đây đã được khóa bằng regression tests tự động trong `src/ai-core/tests/test_w7_m4_sprint7.py` và UI tests trong `src/web/aura-web/src/pages/AnalysisResultPage.test.tsx`:

| Nhóm ca kiểm thử | Đầu vào mô phỏng / thực tế | Hành vi fail-closed kỳ vọng | Mã lỗi / Trạng thái | Không được phép xảy ra |
| :--- | :--- | :--- | :--- | :--- |
| **Positive Control** | Ảnh fundus tổng hợp đạt chuẩn độ sáng, tương phản, sắc nét | Trả `HTTP 200`, `is_mock=false`, `risk_available=false`, `risk_assessment=null`, `status=WARNING` kèm `RISK_UNAVAILABLE` | `200 OK` | Không được từ chối nhầm ảnh hợp lệ |
| **Ảnh quá tối / pitch-black** | Ảnh đen hoàn toàn hoặc tối dưới ngưỡng `min_luminance` | Từ chối trước bước Frangi với chi tiết `failed_checks` | `422 LOW_QUALITY_IMAGE` | Không trả `200`, không đoán `LOW risk` |
| **Ảnh cháy sáng / lóa (glare)** | Ảnh bão hòa trắng vượt ngưỡng `max_luminance` / mất tương phản | Từ chối trước bước Frangi | `422 LOW_QUALITY_IMAGE` | Không trả mask rỗng giả thành công |
| **Ảnh mờ / out-of-focus** | Ảnh phẳng / độ sắc nét Laplacian thấp hơn `min_sharpness` | Từ chối với `sharpness` trong `failed_checks` | `422 LOW_QUALITY_IMAGE` | Không tạo kết quả mạch nhiễu |
| **Sai modality** | `modality="OCT"` hoặc `modality="XRAY"` | Từ chối rõ ràng theo hợp đồng 1.3 | `422 UNSUPPORTED_MODALITY` / `INVALID_MODALITY` | Không tự ép sang `FUNDUS` |
| **Payload hỏng / sai định dạng** | Base64 hỏng, bytes PNG bị cắt cụt, data-URL prefix hoặc WebP khi Frangi chỉ hỗ trợ PNG/JPEG | Từ chối fail-closed ở bước giải mã | `400 INVALID_IMAGE_PAYLOAD` | Không gây `500` crash |
| **AVR thiếu điều kiện đo** | Thiếu đĩa thị / Zone B cắt biên / `< 6` mạch mỗi loại | `measurement_available=false`, `avr=null`, có `unavailable_reason`; DR độc lập (nếu bật integrated) vẫn giữ nguyên | `200` + `AVR_UNAVAILABLE` | Không điền `avr=0`, không suy ra `LOW risk` |
| **Artifact hỏng / sai SHA-256** | Checkpoint bị chỉnh byte hoặc thiếu biến môi trường SHA-256 | Từ chối phục vụ / từ chối kích hoạt manifest | `503 RESEARCH_MODEL_UNAVAILABLE` / `409 ARTIFACT_HASH_MISMATCH` | Không fallback sang mock hoặc weights không xác thực |
| **Quá tải đồng thời** | Slot suy luận (`CNN_SLOT`) đang bận trên container 1 worker | Áp dụng backpressure ngay lập tức | `429 RESEARCH_MODEL_BUSY` + `Retry-After: 1` | Không spawn quá bộ nhớ `2g` gây OOMKill |
| **Hiển thị UI ở trạng thái lỗi cuối** | `processingStatus` là `LOW_QUALITY` / `FAILED` / `REJECTED` kèm `error: { code, message }` | Hiển thị rõ mã lỗi & thông điệp lỗi từ API, dừng polling | Terminal UI state | Không hiển thị AVR `0` hay `Đánh giá nguy cơ: LOW` |

---

## 4. Lộ trình cải tiến kỹ thuật tiếp theo (Không tạo nhãn bác sĩ giả)

1. **Đóng gate `SCRUM-373` bằng dữ liệu thật:**
   - Xin cấp quyền sử dụng và chạy đánh giá ngoài trên bộ 40 ca **University of Iowa INSPIRE-AVR** có nhãn đối chiếu 2 bác sĩ thật (`GT/GT.txt`) ở môi trường bảo mật không phân phối lại dữ liệu.
   - Tách rõ lỗi do định vị đĩa thị (`OPTIC_DISC_NOT_LOCALIZED`) vs. lỗi phân loại A/V (`RRWNet` calibration) vs. sai lệch đo caliper đường kính native-pixel để hiệu chỉnh từng khâu có kiểm chứng.
2. **Nâng cấp bài toán DR & Explainability:**
   - Thu thập / liên kết bộ dữ liệu đa trung tâm có phân độ ICDR 5 mức do bác sĩ nhãn khoa thực tế gắn nhãn (tuyệt đối **không sinh nhãn bác sĩ giả hoặc nội suy từ HRF45**).
   - Chuyển kiến trúc phân loại & giải thích sang ảnh độ phân giải cao toàn khung (`full-FOV` thay vì `CenterCrop 224x224`) để bản đồ giải thích khớp không gian toàn ảnh fundus gốc.
3. **Tối ưu suy luận an toàn theo Golden Parity Gate:**
   - Mọi tối ưu hóa tương lai (ONNX Runtime CPU / INT8 / FP16 cho mô hình neural) chỉ được kích hoạt opt-in khi vượt qua Golden Parity Gate (`max_abs_prob_diff <= 1e-4`, `gradcam_cosine >= 0.99`, không làm giảm độ phủ AVR).
4. **Liên kết bằng chứng Sprint 7:**
   - Báo cáo đánh giá V2: `docs/benchmarks/v2_evaluation_report_s7.json` (sinh/xác minh bởi `src/ai-core/scripts/evaluate_v2_protocol.py`).
   - Hồ sơ latency, RAM & Golden Parity: `docs/benchmarks/w7_latency_ram_profile_v1.0.json` (sinh/xác minh bởi `src/ai-core/scripts/benchmark_w7_latency_ram.py`).
   - Bộ kiểm thử tự động: `src/ai-core/tests/test_w7_m4_sprint7.py`, `src/web/aura-web/src/pages/AnalysisResultPage.test.tsx`, `tests/research/m4_compose_checks.cjs`.
