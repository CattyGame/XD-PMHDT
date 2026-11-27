# Model Card — AURA Retinal Vessel Segmentation & Heuristic Risk Pipeline (`v1.3.0`)

> **Dự án**: AURA — Hệ thống Phân tích Mạch máu Võng mạc Hỗ trợ Sàng lọc Sức khỏe (`SP26SE025`)
> **Mã Jira**: `SCRUM-351` (`W45-M4-02`), `SCRUM-352` (`W45-M4-03`), `SCRUM-353` (`W45-M4-04`)
> **Tác giả**: Đào Duy Quân (`M4 — AI Core Lead / AI Engineer`)
> **Phiên bản Model Card**: `1.3.0` · **Phiên bản Hợp đồng API**: `1.3.0` (`contracts/ai-openapi-1.3.yaml`)
> **Ngày cập nhật**: `2026-10-30`
> **Chuẩn trình bày**: Theo khung *Model Cards for Model Reporting* (Mitchell et al., FAT* 2019) và quy tắc trung thực học thuật của `docs/data_cards/dataset_card.md`.

---

## 1. Thông tin Tổng quan Mô hình (Model Details)

| Thuộc tính | Giá trị định danh chính thức |
|---|---|
| **Tên dịch vụ** | `AURA.AiCore` (`POST /api/v1/analyze`) |
| **Mã phiên bản mô hình (`model_version`)** | `frangi-v0.2` (giữ nguyên toán học phân vùng đã khóa trên tập validation Sprint 1–3) |
| **Mã tối ưu hóa thực thi (`optimization`)** | `separable-1d-hessian-vectorized-6pass-inplace-v1.3` |
| **Mã cấu hình (`config_version`)** | `s45-frangi-1.3.0` (`src/ai-core/config/ai-frangi-v1.3.yaml`) |
| **Mã ngưỡng nhị phân hóa (`threshold_version`)** | `frangi-threshold-0.02-v1` (`threshold = 0.02`) |
| **Bản chất thuật toán** | Xử lý ảnh cổ điển đa tỷ lệ (Multi-scale 2D Hessian Vesselness Filter) kết hợp trích xuất khung xương hình thái học (Morphological Skeletonization) và hàm đánh giá nguy cơ sơ bộ dựa trên quy tắc giải tích (**Heuristic Risk Model** — không phải mạng học sâu chẩn đoán lâm sàng). |
| **Định dạng đầu vào hỗ trợ** | Ảnh màu đáy mắt 2D (`modality = "FUNDUS"`, `mode = "retina_vessels"`, `PNG / JPEG / WEBP` mã hóa Base64), bảo toàn nguyên vẹn kích thước gốc (ví dụ `999 x 960` pixel, không resize/crop). |
| **Cơ chế xác thực & truy vết** | RS256 Service JWT fail-closed (`iss="aura-identity-s3-v1"`, `aud="aura-ai"`, `scope="ai:analyze"`, `TTL <= 300s`) và phản hồi bắt buộc header `X-Correlation-Id` trên mọi mã trạng thái HTTP (`200`, `400`, `401`, `403`, `422`, `500`, `503`). |

---

## 2. Mục đích Sử dụng & Ranh giới An toàn Y tế (Intended Use & Out-of-Scope)

### 2.1. Phạm vi sử dụng hợp lệ (Primary Intended Uses)
1. **Phân vùng cấu trúc mạch máu võng mạc 2D**: Sinh ảnh nhị phân `mask` (`PNG 8-bit Grayscale L`, giá trị `0/255`) và ảnh chồng lớp trực quan `overlay` (`PNG RGB`) khớp chính xác hệ trục tọa độ gốc của ảnh chụp đáy mắt để phục vụ lưu trữ `aura-annotations` và hiển thị trên **Doctor Workspace UI** (`M5`).
2. **Định lượng đặc trưng hình thái mạch máu**: Cung cấp chỉ số mật độ mạch máu (`vessel_density`), chỉ số độ cong queo trung bình có trọng số (`tortuosity_index >= 1.0`), và số điểm phân nhánh khung xương (`branching_points`).
3. **Sàng lọc ưu tiên sơ bộ cho quy trình Bác sĩ duyệt (`Doctor Review Worklist`)**: Cung cấp điểm nguy cơ hình thái sơ bộ `risk_score in [0.0, 1.0]` và phân tầng `LOW | MODERATE | HIGH` đi kèm danh sách chỉ báo giải thích được (`indicators`) và nhãn miễn trừ trách nhiệm y khoa bắt buộc (`disclaimer`).

### 2.2. Các trường hợp ngoài phạm vi / Bị cấm sử dụng (Out-of-Scope & Prohibited Uses)
* **Không dùng để tự động chẩn đoán, loại trừ bệnh lý tim mạch/đột quỵ/võng mạc tiểu đường hoặc kê đơn điều trị** khi chưa có đánh giá xác nhận (`Doctor Revision`) của bác sĩ chuyên khoa mắt.
* **Không chấp nhận dữ liệu định danh bệnh nhân (`PHI`)**: Trường `patient_id` trong request sang AI Core bắt buộc là `null` (nếu khác `null` sẽ bị từ chối với `HTTP 422 SCHEMA_VALIDATION_ERROR`).
* **Không áp dụng cho ảnh cắt lớp quang học võng mạc (`OCT`) ở Sprint 4–5**: Mọi request có `modality="OCT"` tiếp tục bị từ chối an toàn với `HTTP 422 UNSUPPORTED_MODALITY` theo đúng lộ trình khảo sát tại `docs/ai/oct_modality_feasibility_study_v1.0.md`.

---

## 3. Kiến trúc Toán học & Tối ưu hóa Thực thi (Algorithmic Architecture)

### 3.1. Chốt chặn Chất lượng Tiền Phân vùng (Pre-Frangi Quality Gate)
Trước khi cấp phát bộ nhớ phân vùng hoặc tính điểm rủi ro, ảnh RGB gốc được ước lượng nội vùng quan sát (`max(R,G,B) > 20` -> `binary_fill_holes` -> `minimum_filter` tách chiều tương đương `12` vòng co hình thái `3x3`). Trên vùng nội tiếp `Omega`, hệ thống tính:
* `regionRatio = |Omega| / (W * H)` (ngưỡng tối thiểu `>= 0.15`)
* `luminance = mean(Y[Omega])` với `Y = 0.299*R + 0.587*G + 0.114*B` (khoảng hợp lệ `[15.0, 240.0]`)
* `contrast = std(Y[Omega], ddof=0)` (ngưỡng tối thiểu `>= 8.0`)
* `sharpness = var(Laplacian_3x3(Y)[Omega], ddof=0)` (ngưỡng tối thiểu `>= 3.5`)

> **Quy tắc nhất quán (`W45-M4-02`)**: Nếu bất kỳ điều kiện nào vi phạm, pipeline **ngắt ngay lập tức** và trả về `HTTP 422 LOW_QUALITY_IMAGE`, tuyệt đối không sinh mask nhiễu và không tính toán `risk_assessment` (giúp Worker `M2` kích hoạt hoàn trả credit tự động chính xác).

### 3.2. Vector hóa Ma trận Hessian 6-Pass Tách chiều trên CPU (`fast_vectorized_frangi_2d`)
Trong phiên bản Sprint 3 (`v1.2`), `skimage.filters.frangi` gọi tuần tự gradient bậc 1 rồi gradient bậc 2 qua `10` lượt tích chập 1D cho mỗi mức tỷ lệ `sigma in {1.0, 2.0, 3.0, 4.0}` (`40` lượt tổng cộng) và cấp phát hàng chục mảng trung gian `float64`, khiến độ trễ trung vị trên ảnh `999 x 960` mất `2516.41 ms`.

Trong phiên bản `v1.3` (`src/ai-core/app/services/frangi_service.py`), phép tính Hessian 2D được tái cấu trúc toán học tương đương tuyệt đối:
1. **Tiền tích chập cặp hạt nhân đạo hàm Gaussian 1D**: Với mỗi `sigma`, xây dựng 3 hạt nhân 1D hợp nhất `k00 = G * G`, `k01 = G * G'`, `k11 = G' * G'` (bán kính cắt cụt `5*sigma + 0.5`), giảm từ **10 lượt xuống đúng 6 lượt `scipy.ndimage.correlate1d`** cho mỗi `sigma` (`24` lượt trên toàn bộ 4 tỷ lệ).
2. **Giải nghiệm trị riêng ma trận đối xứng `2x2` tại chỗ (In-place Analytical Eigenvalues)** trên bộ đệm `float64` tái sử dụng (`buf_tmp`, `h_rr`, `h_rc`, `h_cc`, `vessel_max`), triệt tiêu `~385 MB` cấp phát mảng tạm mỗi lượt suy luận:
   $$\lambda_{1,2} = \frac{(H_{rr} + H_{cc}) \pm \sqrt{(H_{rr} - H_{cc})^2 + 4 H_{rc}^2}}{2}, \quad |\lambda_1| \le |\lambda_2|$$
3. **Hàm đáp ứng Frangi (`black_ridges=True`, chỉ giữ điểm có `lambda_2 > 0`)**:
   $$V(\sigma) = \exp\left(-\frac{(\lambda_1 / \lambda_2)^2}{2\beta^2}\right) \cdot \left(1 - \exp\left(-\frac{\lambda_1^2 + \lambda_2^2}{2\gamma^2}\right)\right)$$
   với `beta = 0.5` và quy tắc lưu giữ `gamma` chuẩn của `skimage` qua các tỷ lệ.
4. **Bảo toàn 100% độ chính xác**: Kiểm chứng đối chiếu trực tiếp trên toàn bộ `6` ảnh test `999 x 960` (`5,754,240` pixel) cho thấy **`0` pixel sai khác (`Dice vs Sprint 3 = 1.000000`)**.

### 3.3. Đo Độ cong queo Mạch máu & Mô hình Rủi ro Sơ bộ (`src/ai-core/app/services/tortuosity.py`)
1. **Trích xuất khung xương & điểm phân nhánh**:
   * Làm mảnh mặt nạ nhị phân về khung xương rộng 1 pixel bằng `skimage.morphology.skeletonize`.
   * Tính bậc kết nối 8 lân cận `deg(p)` tại mọi điểm khung xương; điểm có `deg(p) >= 3` được ghi nhận là **điểm phân nhánh (`branching_points`)** và được tách ra để phân rã mạng mạch thành các đoạn nhánh đơn liên thông (`>= 8` pixel, loại bỏ gai nhiễu ngắn).
2. **Chỉ số độ cong queo có trọng số khoảng cách (`tortuosity_index`)**:
   * Trên từng đoạn nhánh thứ `k` nối hai đầu mút `(p_start, p_end)`, tính chiều dài cung đa giác 8-lân cận $L_{\text{arc}, k} = n_{\text{orth}} \cdot 1.0 + n_{\text{diag}} \cdot \sqrt{2}$ (vector hóa $O(N)$ qua `np.bincount`) và khoảng cách dây cung Euclid $L_{\text{chord}, k} = \|p_{\text{end}} - p_{\text{start}}\|_2$:
   $$\text{TI} = \frac{\sum_{k} L_{\text{chord}, k} \cdot \max\left(1.0, \frac{L_{\text{arc}, k}}{L_{\text{chord}, k}}\right)}{\sum_{k} L_{\text{chord}, k}} \ge 1.0$$
3. **Hàm tính điểm nguy cơ sơ bộ (`evaluate_heuristic_risk`)**:
   * Chuẩn hóa thành phần cong queo `r_tort = clip((TI - 1.08) / (1.35 - 1.08), 0, 1)`.
   * Chuẩn hóa độ lệch mật độ mạch máu `r_dens` khi `vessel_density` thấp bất thường (`< 0.045`, nghi ngờ thưa mạch) hoặc cao bất thường (`> 0.085`, nghi ngờ tăng sinh/nhiễu phân nhánh).
   * Hợp nhất có trọng số: `risk_score = clip(0.60 * r_tort + 0.40 * r_dens, 0.0, 1.0)`.
   * Ngưỡng phân tầng: `LOW` (`risk_score < 0.35`), `MODERATE` (`0.35 <= risk_score < 0.70`), `HIGH` (`risk_score >= 0.70`), luôn đính kèm hằng số `HEURISTIC_DISCLAIMER`.

---

## 4. Tập Dữ liệu Đánh giá & Khả năng Tái lập (Evaluation Dataset)

* **Tập dữ liệu**: `CHASE_DB1` (* Kingston University & St. George's, University of London*, giấy phép **CC BY 4.0**, `28` ảnh `999 x 960 RGB` từ `14` đối tượng).
* **Quy ước chia tập không rò rỉ đối tượng (`docs/datasets/chase_db1/manifest.csv`, `seed = 42`)**:
  * `Train`: `16` ảnh (`8` đối tượng: `03, 06, 07, 08, 09, 12, 13, 14`).
  * `Validation`: `6` ảnh (`3` đối tượng: `04, 05, 10`) — dùng để chọn cấu hình và chốt ngưỡng `0.02`.
  * `Test`: `6` ảnh (`3` đối tượng: `01, 02, 11`) — **chỉ dùng để đo kiểm độc lập**, nhãn tham chiếu chính `_1stHO.png`, đánh giá trên toàn ảnh `999 x 960`.

---

## 5. Kết quả Đo lường Thực nghiệm Chính thức (`frangi_runtime_api_v1.3.json`)

> **Nguồn dữ liệu đo thực tế**: `docs/benchmarks/frangi_runtime_api_v1.3.json` (chạy ngày `2026-10-30T10:23:18Z` trên `Windows-11-10.0.26300-SP0`, `8` logical CPU cores, `Python 3.12.14`, `NumPy 2.5.3`, `SciPy 1.18.1`, `scikit-image 0.26.0`, `Pillow 12.3.0`).

### 5.1. Độ chính xác Phân vùng & Tương đương Baseline (`CHASE_DB1 Test Split — 6 ảnh`)

| Chỉ số đánh giá | Sprint 3 (`v1.2.0`) | Sprint 4–5 (`v1.3.0`) | Nhận xét kiểm chứng |
|---|---:|---:|---|
| **Mean Dice** (so với `_1stHO.png`) | `0.420051` | **`0.420051`** | Bảo toàn nguyên vẹn 100% |
| **Mean IoU** (so với `_1stHO.png`) | `0.266240` | **`0.266240`** | Bảo toàn nguyên vẹn 100% |
| **Mean Sensitivity** | `0.419782` | **`0.419782`** | Bảo toàn nguyên vẹn 100% |
| **Mean Specificity** | `0.956500` | **`0.956500`** | Bảo toàn nguyên vẹn 100% |
| **Mean Dice vs Sprint 3 Mask** | `1.000000` (tham chiếu) | **`1.000000`** | Khớp tuyệt đối từng điểm ảnh |
| **Tổng số pixel lệch (`6 x 999 x 960`)** | `0` | **`0 / 5,754,240`** | Không sai lệch dù chỉ 1 pixel |

### 5.2. Chi tiết Chỉ số Hình thái & Rủi ro Sơ bộ theo từng ảnh Test (`Contract 1.3.0`)

| Ảnh Test | Đối tượng / Mắt | `vessel_density` | `tortuosity_index` | `branching_points` | `risk_score` | `risk_level` | `dice` | `processing_time_ms` |
|---|---|---:|---:|---:|---:|---|---:|---:|
| `Image_01L.jpg` | `01` / `left` | `0.056918` | `1.1207` | `1399` | `0.0632` | `LOW` | `0.424641` | `1092.90 ms` |
| `Image_01R.jpg` | `01` / `right` | `0.051492` | `1.1149` | `1317` | `0.1171` | `LOW` | `0.398377` | `1061.94 ms` |
| `Image_02L.jpg` | `02` / `left` | `0.084140` | `1.1195` | `2950` | `0.3735` | `MODERATE` | `0.404570` | `1101.60 ms` |
| `Image_02R.jpg` | `02` / `right` | `0.111617` | `1.1377` | `5265` | `0.4905` | `MODERATE` | `0.383643` | `1062.72 ms` |
| `Image_11L.jpg` | `11` / `left` | `0.061945` | `1.1033` | `2082` | `0.0572` | `LOW` | `0.454170` | `1121.54 ms` |
| `Image_11R.jpg` | `11` / `right` | `0.047436` | `1.1025` | `1275` | `0.1380` | `LOW` | `0.454909` | `1130.50 ms` |

### 5.3. Độ trễ Suy luận Đơn luồng qua HTTP Uvicorn (bao gồm JWT + Quality + Frangi + Tortuosity + Risk + PNG Encode)

| Thước đo độ trễ | Sprint 3 (`v1.2.0` — chưa có Tortuosity/Risk) | Sprint 4–5 (`v1.3.0` — đầy đủ Tortuosity + Risk) | Mục tiêu SLA (`W45-M4-02`) | Mức cải thiện |
|---|---:|---:|---:|---:|
| **`processing_time_mean_ms`** | `2528.39 ms` | **`1095.20 ms`** | `< 1800.00 ms` | **ĐẠT (`-56.68%`)** |
| **`processing_time_median_ms`** | `2516.41 ms` | **`1097.25 ms`** | `< 1800.00 ms` | **ĐẠT (`-56.40%` / nhanh gấp `2.29x`)** |
| **`processing_time_p95_ms`** | `2595.46 ms` | **`1128.26 ms`** | `< 1800.00 ms` | **ĐẠT (`-56.53%`)** |
| **`processing_time_max_ms`** | `2600.48 ms` | **`1130.50 ms`** | `< 1800.00 ms` | **ĐẠT (`-56.53%`)** |
| **`http_roundtrip_median_ms`** | `2526.11 ms` | **`1112.62 ms`** | `< 1800.00 ms` | **ĐẠT (`-55.96%`)** |

### 5.4. Kết quả Kiểm thử Tải Đồng thời & Kiểm toán Bộ nhớ (`W45-M4-04`)

| Hạng mục đo kiểm trên tiến trình Uvicorn độc lập | Giá trị đo thực tế | Đánh giá DoD |
|---|---:|---|
| **Mức đồng thời kiểm thử (`concurrency_levels_tested`)** | `5` và `10` luồng đồng thời | Đúng yêu cầu `5..10` concurrent requests |
| **Tổng số lượt suy luận ảnh `999 x 960` hoàn tất** | **`100 / 100`** (`0` thất bại) | **Tỷ lệ lỗi `0.0%`**, không crash Uvicorn |
| **Tổng thời gian thực thi `100` ca (`wall_clock_seconds`)** | `39.82 s` (`2.511 req/s`) | Thông lượng batch `~150 ảnh/phút` trên 1 node CPU |
| **Mức sử dụng CPU của tiến trình Uvicorn** | `166.98 CPU-s` (`4.19` nhân thực / `52.42%` toàn hệ thống) | Tận dụng song song đa nhân hiệu quả qua C-kernels của SciPy/NumPy |
| **RSS RAM nền sau khởi động & warm-up** | `95.30 MiB` | Gọn nhẹ khi rỗi |
| **RSS RAM đỉnh khi dồn tải `10` ảnh `999 x 960` cùng lúc** | `1038.38 MiB` (`~1.01 GiB`) | Tương ứng `~95 MiB` nền + `10 x ~94 MiB` bộ đệm luồng song song |
| **RSS RAM trạng thái ổn định ngay sau khi xong `100` ảnh** | **`100.72 MiB`** (chênh lệch ròng `+5.42 MiB`) | **Thu hồi bộ nhớ ngay lập tức**, không phình RAM |
| **Kiểm toán `tracemalloc` (`20` lượt liên tiếp + `gc.collect()`)** | Đỉnh đơn luồng `96.244 MiB`, **giữ lại ròng `+0.0067 MiB` (`~6.9 KiB`)** | **`memory_leak_detected = false`**, `0` đối tượng rác không thu hồi được |

---

## 6. Giới hạn Kỹ thuật, Đạo đức AI & Khuyến nghị Vận hành (Limitations & Ethics)

1. **Giới hạn của bộ lọc Frangi không giám sát (`Mean Dice = 0.4201`)**: Bộ lọc Hessian nhạy với rìa đĩa thị sáng và nhiễu xuất tiết, đồng thời dễ bỏ sót vi mạch cực mảnh độ tương phản thấp. Trong trường hợp ảnh có nhiều nhiễu rìa làm tăng mật độ mask (ví dụ `Image_02R.jpg`, `vessel_density = 0.1116`), điểm `risk_score` có thể tăng lên mức `MODERATE` do mật độ phân nhánh giả — điều này đã được giải thích minh bạch ngay trong chuỗi `indicators` trả về cho Bác sĩ.
2. **Giới hạn đại diện quần thể của `CHASE_DB1`**: Tập dữ liệu chỉ gồm `28` ảnh của `14` trẻ em đa sắc tộc tại Anh và không chứa nhãn bệnh lý tim mạch hay nhãn phân loại động mạch/tĩnh mạch (`av_ratio` và `fractal_dimension` tiếp tục được giữ `null` trung thực, không sinh số giả).
3. **Khuyến nghị cấu hình Container cho `M1` & `M2` khi chạy Batch `>= 100` ảnh**:
   * Đặt `AURA_FRANGI_CONFIG_PATH=/app/config/ai-frangi-v1.3.yaml` (hoặc `AURA_RISK_AVAILABLE=true`) khi kích hoạt luồng Contract `1.3.0` cho Sprint 4–5; giữ nguyên `/app/config/ai-frangi.yaml` khi cần tương thích ngược Contract `1.2.0` của Sprint 3.
   * Với `10` luồng gọi đồng thời vào cùng 1 container `ai-core`, đỉnh RSS đạt `~1.04 GiB`; khuyến nghị `M1` cấp `mem_limit: 1536m` (hoặc `2g`) cho container `ai-core` và `M2` giữ `HttpClient.Timeout >= 15s` để bao phủ cả phân vị `P95 = 6.27s` khi dồn `10` request cạnh tranh CPU đồng thời.

---

## 7. Model Card V2 Addendum — Pipeline Nghiên cứu Tích hợp & Versioned Manifest Rollout (Sprint 6: `W6-M4-01`, `W6-M4-02`, `W6-M4-03`)

Bổ sung thông số cho phiên bản nghiên cứu tích hợp **V2** (`AURA_INTEGRATED_RESEARCH_ENABLED=true`) và cơ chế nâng cấp/rollback manifest an toàn theo `docs/benchmarks/model_card_v2_registry.json`.

### 7.1. Chi tiết Mô hình Nghiên cứu Tích hợp (`integrated_inference.py`)

| Thành phần | Thuật toán & Kiến trúc | Kiểm soát rò rỉ dữ liệu & Tọa độ | Hash / Phiên bản & Lệnh tái lập |
|---|---|---|---|
| **Frangi Baseline / HRF Proxy** | Separable 6-pass 2D Hessian Filter | Không resize/crop trên baseline; giữ nguyên geometry ảnh gốc | `ai-frangi.yaml` (`sha256: 0b0451ca...a0ee`), `ai-frangi-v1.3.yaml` (`dd444ce8...5087`), `ai-frangi-hrf.yaml` (`8af374dc...9aca`) |
| **Binary DR Screening (`SCRUM-372` V2)** | `ResNet34` (`IMAGENET1K_V1` frozen backbone) + Linear head + autograd `Grad-CAM` (`layer4`) | Patient-level split HRF (`18 train / 6 val / 6 test`); `15 Glaucoma` tách riêng OOD; chọn epoch bằng Val AUC; `gradcam_geometry = IMAGENET_CENTER_CROP_224` (`Resize 256 -> Crop 224x224`) | Lệnh: `python src/ai-core/scripts/train_dr_cnn.py --output model-artifacts/dr-resnet34-hrf-v1/model.pt --epochs 100` (`val_auc=0.8889`, `test_auc=1.0`, `sha256` với `torch 2.14.1+cpu`: `8c9114c5...f8f7`) |
| **AVR V8 & OD Localization (`SCRUM-373`)** | `RRWNET_RITE` (`CC-BY-NC-4.0`) hoặc `PIXEL_MLP_RGB_DOG` + `lunetv2_odc.onnx` + Zone B (`2R..3R`) Knudtson (`0.88`/`0.95`) | Threshold hiệu chỉnh chỉ trên 15 ca val của `AVRDB`; `predict` chỉ dùng RGB; không đủ `6A+6V` trả `avr=null + unavailable_reason`, tính vào mẫu số `coverage` | Lệnh tải: `python src/ai-core/scripts/fetch_avr_rrwnet.py` (`model.safetensors`: `32d93fa7...4485`, `lunetv2_odc.onnx`: `f72f6c9e...b5f`) |

### 7.2. Ranh giới An toàn, Rollout Manifest & Gate Khoa học

1. **Tách bạch rõ `is_mock` / `research` / `clinical`:** Mọi response chứa `research` (DR + AVR V8) bắt buộc giữ `is_mock = false` và `risk_available = false`, `risk_assessment = null`. Xác suất DR nhị phân trên HRF **không phải** phân độ ICDR lâm sàng.
2. **Versioned Config & Safe Rollout (`W6-M4-02`):** Quản trị viên `SYSTEM_ADMIN` chỉ được kích hoạt `artifactManifestId` + `thresholdConfigSha256` nằm trong registry phía server (`src/ai-core/config/artifact-manifests.json`) thông qua `/api/v1/admin/ai/config` (ghi durable command journal `analysis.ai_config_commands` + deterministic owner audit trên PostgreSQL, ủy quyền sang internal AI Core `/internal/v1/ai/config` qua RS256 scope `ai:config`, tự reconcile các lệnh `PENDING` khi restart hoặc audit DB phục hồi); từ chối mọi path/URL tùy ý, chạy smoke verification thật trước atomic switch (kể cả load/infer AVR+DR thật khi bật `AURA_INTEGRATED_RESEARCH_ENABLED=true`, và fail-closed `/ready` 503 nếu artifact integrated hỏng), trả đúng receipt gốc cho replay idempotent, lưu bền vững state/history qua `AURA_RUNTIME_STATE_PATH` trên volume (`ai_core_state`), pin snapshot bất biến cho các request đang chạy và cho phép rollback về manifest liền trước. AI Core không mở public reload endpoint.
3. **Gate Khoa học `SCRUM-373` (`OPEN_NOT_PASSED`):** Giá trị baseline trên `29` ca INSPIRE-AVR cho thấy `Mean AVR MAE = 0.2335`, `Mean AVR Bias = -0.2136` (mục tiêu `|bias| <= 0.02` trên bảng 2 chuyên gia Iowa 40 ca vẫn mở). Gate khoa học này được giữ tách biệt và không đánh dấu `PASS` giả bằng script.

---

## 8. Sprint 8 Artifact Registry & Reproducibility Handover (`W8-M4-01` / `SCRUM-423`, `W8-M4-02` / `SCRUM-424`)

- **Registry có kiểm chứng SHA-256:** Toàn bộ cấu hình Frangi YAML, trọng số JSON 13 đặc trưng DR (`dr_screening_weights.json`), thông số tái huấn luyện PyTorch `ResNet34` (`8c9114c54f1b06300081bb82b80a0ea3a10e3d035232e1c5f3570b2db42df8f7`), `RRWNet` SafeTensors (`32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485`, `CC-BY-NC-4.0`) và `lunetv2_odc.onnx` (`f72f6c9ea2ade798b80539de77fa610bb6553bb0fd7fcb78d1e841982cae0b5f`, **artifact ONNX duy nhất hiện hữu**) được pin tập trung trong [`docs/benchmarks/w8_artifact_registry_v1.0.json`](../benchmarks/w8_artifact_registry_v1.0.json) và hướng dẫn [`docs/data_cards/w8_artifact_reproducibility_guide.md`](w8_artifact_reproducibility_guide.md).
- **Nghiệm thu chức năng vs. Kiểm định khoa học tách biệt:** Biên bản nghiệm thu V2 và đối chiếu RTM được công bố tại [`docs/ai/w8_m4_v2_software_and_scientific_acceptance.md`](../ai/w8_m4_v2_software_and_scientific_acceptance.md) và [`docs/benchmarks/w8_v2_acceptance_summary.json`](../benchmarks/w8_v2_acceptance_summary.json), bảo đảm `clinicalValidation = false`, không sinh `expert_confirmed` từ `AUTO` và giữ `SCRUM-373 = OPEN_NOT_PASSED`.
