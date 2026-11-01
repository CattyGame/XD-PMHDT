> **Dự án**: AURA — Phân tích mạch máu võng mạc hỗ trợ đánh giá sức khỏe
> **Mã Jira**: SCRUM-59, SCRUM-290; SCRUM-308 / SCRUM-310 (Sprint 2 — Tiền xử lý chuẩn hóa & Contract Worker–AI)
> **Người biên soạn ban đầu**: Đào Duy Quân (M4 — AI Engineer)
> **Phiên bản tài liệu**: 1.3.1
> **Ngày cập nhật**: 2026-10-24
> **Trạng thái**: Dataset/benchmark offline và module preprocessing đã được mô tả riêng; AI endpoint vẫn mock. 7A preprocessing tests và 7B AI suite/OpenAPI check được người chạy xác nhận PASS; chưa chứng nhận Worker hoặc inference thật.

---

## 1. Dataset thực tế và phạm vi sử dụng

Dataset thực tế đã tải và kiểm tra là CHASE_DB1.

| Dataset | Trạng thái trong dự án | Mục đích |
|---|---|---|
| CHASE_DB1 | Đã tải và kiểm tra ảnh–nhãn | Baseline phân vùng mạch máu và kiểm thử xử lý ảnh |
| HRF (High-Resolution Fundus) | Đã lưu trữ trong data-assets/HRF | Sàng lọc & Phân vùng tổn thương Bệnh Võng Mạc Đái Tháo Đường (DR) |
| INSPIRE_AVR | Đã lưu trữ trong data-assets/INSPIRE_AVR | Đo tỷ lệ Arteriovenous Ratio (AVR), Bệnh Võng Mạc Tăng Huyết Áp & Nguy cơ Tim mạch / Đột quỵ |
| DRIVE | Chưa tải, chưa kiểm tra trực tiếp | Nguồn tham khảo; cân nhắc đánh giá bổ sung |
| APTOS 2019 | Chưa tải, chưa kiểm tra trực tiếp | Nguồn tham khảo cho phân loại bệnh võng mạc tiểu đường |
| STARE | Chưa xác nhận dữ liệu local và kết quả kiểm tra | Dataset ứng viên để đánh giá bổ sung |

Các thông tin tổng hợp từ tài liệu công bố của DRIVE, APTOS hoặc STARE là thông tin tham khảo, không phải kết quả EDA thực nghiệm của nhóm.

CHASE_DB1 cung cấp nhãn phân vùng mạch máu. Các file trong ZIP đã kiểm tra không cung cấp nhãn chẩn đoán đột quỵ, cao huyết áp, nguy cơ tim mạch hoặc phân loại động mạch–tĩnh mạch.

---

## 2. Nguồn dữ liệu, giấy phép và trích dẫn

### 2.1. Nguồn CHASE_DB1

- Đơn vị công bố: Kingston University, phối hợp với St. George’s, University of London.
- Trang dataset:
  https://researchinnovation.kingston.ac.uk/en/datasets/chasedb1-retinal-vessel-reference-dataset-4/
- Link ZIP trên trang chính thức:
  https://researchinnovation.kingston.ac.uk/files/40659508/CHASEDB1.zip
- Link README trên trang chính thức:
  https://researchinnovation.kingston.ac.uk/files/40659510/readme.txt
- Tên archive đã kiểm tra: `CHASEDB1.zip`.

### 2.2. Giấy phép

Trang chính thức ghi giấy phép của ZIP và README là Creative Commons Attribution 4.0 (CC BY 4.0).

Điều khoản:
https://creativecommons.org/licenses/by/4.0/

Khi sử dụng hoặc phân phối dữ liệu:
- Ghi nhận tác giả và nguồn dataset.
- Cung cấp liên kết giấy phép.
- Ghi rõ nếu đã thay đổi dữ liệu.
- Không trình bày việc sử dụng dữ liệu như sự bảo trợ của tác giả hoặc đơn vị công bố.

Dự án lưu archive gốc tại `data-assets/CHASE_DB1/CHASEDB1.zip`
và hướng dẫn sử dụng tại `data-assets/CHASE_DB1/README.md`.

Archive được quản lý trong repository để các thành viên sử dụng
cùng nguồn dữ liệu. Khi chia sẻ phải giữ thông tin nguồn,
ghi nhận tác giả và liên kết giấy phép.

Ảnh và mask sau giải nén nằm tại `datasets/CHASE_DB1/raw/`.
Thư mục dữ liệu giải nén và các prediction sinh ra được Git bỏ qua;
không commit thêm bản sao của các file này.

### 2.3. Trích dẫn

- Dataset: CHASE_DB1 retinal vessel reference dataset, Kingston University, 2012.
- Công trình liên quan được trang dataset dẫn tới:
  https://doi.org/10.1109/TBME.2012.2205687

ZIP đã kiểm tra chỉ chứa ảnh và mask. README được cung cấp riêng trên trang chính thức.

---

## 3. Nội dung và kết quả kiểm tra archive

Kết quả dưới đây được đo trực tiếp từ `CHASEDB1.zip` đã tải, không suy ra từ mô tả dataset.

| Hạng mục | Kết quả |
|---|---|
| Tổng số file | 84 |
| Ảnh đầu vào | 28 JPG |
| Mask người đánh dấu thứ nhất | 28 PNG |
| Mask người đánh dấu thứ hai | 28 PNG |
| Nhóm mã đối tượng | 14, từ 01 đến 14 |
| Ảnh mỗi nhóm | 2 ảnh: mắt trái L và mắt phải R |
| Kích thước thực tế của ảnh và mask | 999 × 960 pixel |
| Chế độ ảnh đầu vào | RGB |
| Chế độ lưu mask | PNG nhị phân 1-bit |
| Giá trị pixel mask khi giải mã bằng Pillow | 0 và 255 |
| Kiểm tra CRC của ZIP | Không phát hiện lỗi |
| Giải mã ảnh | 84/84 file thành công |
| Cặp ảnh–nhãn | Đầy đủ |
| Kích thước ảnh–nhãn | Khớp toàn bộ |
| File trùng nội dung pixel | Không phát hiện |
| Mask rỗng hoặc toàn foreground | Không phát hiện |

Đây là 28 ảnh đầu vào độc lập theo file, đi kèm 56 mask. Không tính mask thành ảnh đầu vào bổ sung.

Các kiểm tra trên xác nhận tính toàn vẹn và cấu trúc dữ liệu; không thay thế đánh giá chất lượng chú giải của chuyên gia.

---

## 4. Cấu trúc lưu trữ và ghép ảnh–nhãn

Đường dẫn tương đối từ thư mục gốc repository:

`datasets/CHASE_DB1/raw/`

Ví dụ:

- `Image_01L.jpg`: ảnh mắt trái của đối tượng 01.
- `Image_01L_1stHO.png`: mask của người đánh dấu thứ nhất.
- `Image_01L_2ndHO.png`: mask của người đánh dấu thứ hai.
- `Image_01R.jpg`: ảnh mắt phải của đối tượng 01.

Quy tắc:
- Giữ nguyên tên file và dữ liệu gốc trong thư mục `raw`.
- Ghép ảnh và mask theo cùng phần tên `Image_<subject_id><eye_side>`.
- Dùng `subject_id` để nhóm hai mắt của cùng đối tượng.
- Không dùng mask làm ảnh đầu vào cho thuật toán dự đoán.

Quy ước đánh giá hiện tại:
- Dùng `_1stHO.png` làm nhãn tham chiếu chính.
- `_2ndHO.png` được giữ để có thể đánh giá khác biệt giữa hai người đánh dấu; baseline Frangi v0.2 hiện chưa báo cáo phép so sánh này.
- Không coi mask của người đánh dấu thứ hai là kết quả dự đoán của AI.
- Chuẩn hóa mask thành nhãn 0/1 trước khi tính metric.

Archive gốc trong `data-assets/CHASE_DB1/` được quản lý phiên bản.
Dữ liệu giải nén trong `datasets/` được Git bỏ qua.

Tài liệu, script kiểm tra, manifest và báo cáo JSON trong
`docs/datasets/chase_db1/` được quản lý phiên bản trong repository.

---

## 5. Phân chia dữ liệu và khả năng tái lập

Đã tạo manifest bằng script:

`src/ai-core/scripts/prepare_chase_db1.py`

Cấu hình:
- Seed: 42.
- Chia theo mã đối tượng.
- Hai mắt của cùng đối tượng nằm trong cùng một tập.
- Đây là split của dự án, không phải official split của dataset.

| Tập | Mã đối tượng | Số đối tượng | Số ảnh |
|---|---|---|---|
| Train | 03, 06, 07, 08, 09, 12, 13, 14 | 8 | 16 |
| Validation | 04, 05, 10 | 3 | 6 |
| Test | 01, 02, 11 | 3 | 6 |
| Tổng | 01–14 | 14 | 28 |

File quản lý phiên bản:
- `docs/datasets/chase_db1/manifest.csv`
- `docs/datasets/chase_db1/validation_report.json`

Manifest ghi đường dẫn ảnh, hai mask, mã đối tượng, mắt trái/phải,
split và SHA-256 của từng file.

Quy tắc sử dụng:
- Train dùng để huấn luyện hoặc xây dựng thuật toán.
- Validation dùng để chọn ngưỡng và tham số.
- Test chỉ dùng để đánh giá sau khi đã chốt thuật toán và tham số.
- Không dùng test để điều chỉnh thuật toán.
- Ảnh augmentation phải nằm cùng tập với ảnh gốc.
- Không thay đổi split để chọn kết quả đẹp hơn.

Lệnh chạy lại từ thư mục gốc repository trên Windows:

`.\.venv-dataset\Scripts\python.exe src/ai-core/scripts/prepare_chase_db1.py`

Môi trường kiểm tra cần Python và Pillow.
Để đối chiếu dữ liệu giữa các máy, so sánh SHA-256 trong manifest.

Kết quả đã chạy:
- 28 ảnh, 56 mask, 14 đối tượng.
- 84 file giải mã thành công.
- Không có file thừa.
- Không có mã đối tượng xuất hiện ở nhiều tập.
- Script `prepare_chase_db1.py` chỉ kiểm tra dữ liệu và tạo manifest,
  không chạy benchmark. Vì vậy `benchmark_performed: false` trong
  báo cáo chuẩn bị dữ liệu chỉ phản ánh phạm vi của script này.
- Benchmark Frangi được chạy riêng và được trình bày tại mục 8.
## 6. Tiền xử lý và vùng đánh giá
- Lấy kênh xanh lá và chia giá trị pixel cho 255.
- Làm mượt Gaussian.
- Ước lượng vùng dự đoán từ ảnh, sau đó áp dụng các bước xử lý vùng
  theo script.
- Tính phản hồi Frangi.
- Triệt phản hồi ngoài vùng dự đoán và chuẩn hóa theo phản hồi lớn nhất.
- Áp dụng threshold đã chọn trên validation.
- Không resize ảnh hoặc mask.
- Tính metric trên toàn ảnh với nhãn tham chiếu `_1stHO.png`.

Vùng dự đoán do thuật toán ước lượng không phải FOV mask được
dataset cung cấp. Việc triệt phản hồi ngoài vùng dự đoán không
đồng nghĩa metric chỉ được tính trong FOV.

Cấu hình chi tiết được lưu trong script và báo cáo benchmark;
khi tái lập phải sử dụng cấu hình đã chốt.
Quy tắc:
- Không ghi đè ảnh và mask trong `raw`.
- Ghi rõ kênh ảnh sử dụng, kích thước đầu vào và các phép biến đổi.
- Áp dụng cùng biến đổi hình học cho ảnh và mask.
- Nếu resize mask, dùng nearest-neighbor để giữ nhãn rời rạc.
- Không mô tả chuẩn hóa `(x - mean) / std` là đưa dữ liệu về khoảng [0, 1].
- Nếu chia pixel ảnh 8-bit cho 255, ghi rõ đó là phép đưa giá trị về [0, 1].

ZIP đã kiểm tra không có file FOV mask riêng. Hai file `1stHO` và `2ndHO` là mask mạch máu, không phải mask vùng quan sát.

Trước khi báo cáo Dice/IoU phải ghi rõ:
- Đánh giá trên toàn ảnh hay chỉ trong vùng quan sát.
- Nếu tạo FOV mask, cách tạo và kiểm tra mask.
- Nhãn tham chiếu được sử dụng.
- Kích thước đánh giá và cách quy đổi kết quả dự đoán.

Không tuyên bố mọi metric đã được tính trong FOV khi chưa có triển khai và kết quả kiểm tra.

---

## 7. Giới hạn dữ liệu và mục đích đánh giá

Theo trang công bố, CHASE_DB1 gồm ảnh của hai mắt từ 14 trẻ em trong nghiên cứu CHASE tại Anh.

Giới hạn:
- Số đối tượng nhỏ; kết quả có thể thay đổi đáng kể theo cách chia tập.
- Không đủ để khẳng định khả năng tổng quát trên người trưởng thành hoặc bệnh nhân Việt Nam.
- Nhãn mạch máu không cung cấp trực tiếp kết luận nguy cơ tim mạch.
- Không có nhãn động mạch–tĩnh mạch trong ZIP đã kiểm tra để đánh giá AVR có giám sát.
- Việc giải mã ảnh thành công không chứng minh ảnh đã được khử định danh hoàn toàn hoặc đạt một chuẩn pháp lý y tế cụ thể.

Dự án sử dụng dữ liệu cho nghiên cứu và demo kỹ thuật trong phạm vi đồ án.

---

## 8. Trạng thái benchmark

Đã chạy baseline Frangi v0.2 trên dữ liệu CHASE_DB1 thực tế.

- Nhãn tham chiếu: `_1stHO.png`.
- Đánh giá trên toàn ảnh, không resize.
- Chọn cấu hình và ngưỡng trên validation.
- Ngưỡng đã chốt: 0.02.
- Test sử dụng cấu hình đã chốt, không tìm lại ngưỡng.

| Chỉ số trung bình theo ảnh | Validation — 6 ảnh | Test — 6 ảnh |
|---|---:|---:|
| Dice | 0.3892 | 0.4201 |
| IoU | 0.2423 | 0.2662 |
| Sensitivity | 0.4140 | 0.4198 |
| Specificity | 0.9437 | 0.9565 |

Thời gian xử lý thuật toán local trong báo cáo test gốc
`docs/benchmarks/frangi_test_v0.2.json`:
- Trung vị: 1729.30 ms/ảnh.
- p95: 1765.00 ms/ảnh.
- Lớn nhất: 1768.73 ms/ảnh.
Các lần tái lập có thể cho thời gian khác do máy và tải hệ thống.
Không thay số liệu báo cáo gốc bằng thời gian của lần chạy mới.
Báo cáo tái lập được lưu riêng để đối chiếu.

Thời gian chưa gồm đọc/ghi file, tính metric, API, hàng đợi
và database. Chỉ có 6 mẫu đo từ 3 đối tượng; chưa xác nhận
hiệu năng ổn định hoặc đạt NFR toàn luồng.
Baseline còn nhiều nhiễu và bỏ sót mạch.
Chưa xác nhận đạt ngưỡng chất lượng mục tiêu của dự án.

Báo cáo chi tiết:
`docs/benchmarks/frangi_baseline_v0.2.md`

Báo cáo mô phỏng cũ được giữ với trạng thái SIMULATION_ONLY;
không dùng làm bằng chứng về độ chính xác hoặc hiệu năng thật.

## 9. Giới hạn sử dụng kết quả AURA

AURA đang trong giai đoạn nghiên cứu và demo kỹ thuật.

Các chỉ số hình học mạch máu là kết quả kỹ thuật cần được kiểm chứng. Dataset và baseline phân vùng này chưa đủ để xác nhận hiệu quả hỗ trợ quyết định lâm sàng.

Không sử dụng kết quả demo để tự động chẩn đoán, loại trừ bệnh hoặc quyết định điều trị.

---

## 10. Điều kiện hoàn thành SCRUM-59

- [x] Đã tải và kiểm tra archive CHASE_DB1.
- [x] Đã xác nhận số ảnh, mask, kích thước và cặp ảnh–nhãn.
- [x] Đã xác minh giấy phép trên trang chính thức.
- [x] Đã phân biệt dataset thực tế với nguồn tham khảo.
- [x] Đã lưu thông tin nguồn, giấy phép và trích dẫn trong repository.
- [x] Đã có script kiểm tra dữ liệu chạy lại được trên máy nhóm.
- [x] Đã tạo và kiểm tra manifest ảnh–nhãn cùng split theo đối tượng.
- [ ] Đã rà soát các tài liệu liên quan để thống nhất dataset.
- [ ] Đã review và merge thay đổi tài liệu/script.

Việc hoàn thành SCRUM-59 không đồng nghĩa thuật toán đã đạt độ chính xác hoặc hiệu năng mục tiêu.

---

## 11. Module preprocessing Sprint 2 và ranh giới runtime

Module `src/ai-core/app/services/preprocessing.py` cung cấp
`preprocess_fundus_image` dùng chung. Nó nhận PIL Image, bytes hoặc Base64,
chuẩn hóa RGB rồi trả `PreprocessResult`. Module chưa được gọi trong
`app/main.py`: endpoint hiện gọi `process_mock_analysis`.

| Bước module | Thuật toán / cấu hình hiện có |
|---|---|
| FOV heuristic | Ngưỡng độ sáng cấu hình `fov_threshold=15` và MaxFilter/MinFilter; không phải bộ phát hiện võng mạc đã được xác thực. |
| Quality trên ảnh gốc | Mean luminance, độ lệch chuẩn intensity (contrast), điểm biên từ kernel high-pass và FOV ratio. |
| Crop/pad | Bounding box của FOV và padding, tạo vùng crop vuông khi bật crop. |
| Resize | Ảnh Lanczos, mask nearest-neighbor; mặc định 512×512, cấu hình được. |
| Green / enhancement | Kênh xanh lá; `ImageOps.autocontrast` có cutoff/mask, rồi UnsharpMask. Không triển khai CLAHE hoặc local adaptive histogram equalization. |
| Kết quả | Enhanced image, green channel, FOV mask, kích thước, quality, `is_acceptable` và `rejection_reason`. |

Ngưỡng default: luminance 15..240, contrast >=8, sharpness >=3.5, FOV ratio
>=0.15. Đây là heuristic kỹ thuật, chưa hiệu chuẩn trên bộ ảnh có nhãn chất
lượng. `is_valid_fundus` là tên cờ nội bộ của heuristic, không xác nhận chẩn
đoán hoặc nguồn ảnh. Empty FOV luôn bị từ chối từ bản sửa 7A, kể cả các min
thresholds bằng 0; config sai miền/kiểu/NaN/Infinity bị ValueError. Config
mutable được kiểm tra lại tại pipeline và direct quality entry point.

Caller phải đọc `is_acceptable`; module vẫn có thể trả ảnh đã biến đổi khi
cờ false. Module không tự phát HTTP 422, không gửi Billing release và không
phát event. LOW_QUALITY_IMAGE của endpoint hiện là mock scenario khi được bật
rõ ràng, có `quality_assessment_performed: false`; không xem đó là kết quả
bộ lọc chất lượng này.

Module resize/crop này khác pipeline Frangi offline ở mục 6/8 (không resize,
metric toàn ảnh). Thay preprocessing cần báo cáo mới với config/version và
phép đối chiếu nhãn/hình học; không gán số benchmark cũ cho pipeline mới.
Chưa chứng minh mask inference thật được biến đổi ngược về tọa độ ảnh gốc.

7A: người chạy xác nhận 58 passed in 2.41s (14 cũ +44 regression).
7B: người chạy xác nhận toàn AI suite, OpenAPI sync --check và whitespace
checks PASS; log tổng hợp không cho số test. Đây không phải clinical
validation, benchmark dữ liệu thật mới, Docker deployment hoặc Worker flow.

Kế hoạch đánh giá: [M4 evaluation plan](../testing/m4_evaluation_plan.md).
Mapping/bằng chứng: [Worker–AI contract](../api/worker_ai_contract_integration_v1.0.md)
và [M4 integration status](../integration/M4_Sprint2_integration_status.md).


---

## 12. Tích hợp Frangi v0.2 Runtime, Quality Gate và Smoke-Training Sprint 3

> **Mã Jira**: SCRUM-329 (`W3-M4-01`), SCRUM-330 (`W3-M4-02`), SCRUM-331 (`W3-M4-03`) · **Cập nhật**: 2026-10-28

### 12.1. Đồng nhất thuật toán Frangi v0.2 ở Runtime (`src/ai-core/app/services/frangi_service.py`)
- Runtime mặc định của `ai-core` trong Sprint 3 (`AURA_AI_ENGINE=frangi`, `allowMock=false`) thực thi đúng thuật toán Frangi v0.2 đã chốt tại mục 6 & 8 theo cấu hình `src/ai-core/config/ai-frangi.yaml`:
  - Giữ nguyên kích thước gốc `999 × 960` (không gọi `preprocess_fundus_image` resize 512×512).
  - Kiểm tra chất lượng ảnh trước khi chạy Frangi trên vùng ước lượng nội vùng (`max(R,G,B) > 20` → `binary_fill_holes` → `binary_erosion` 3×3 lặp 12 lần):
    - `grayscale = 0.299*R + 0.587*G + 0.114*B` (`float64`)
    - `luminance = mean(grayscale[estimatedRegion])`
    - `contrast = std(grayscale[estimatedRegion], ddof=0)`
    - `sharpness = var(convolve(grayscale, [[0,1,0],[1,-4,1],[0,1,0]], mode='reflect')[estimatedRegion], ddof=0)`
    - `regionRatio = estimatedRegionPixels / (width * height)`
    - Từ chối bằng HTTP `422 LOW_QUALITY_IMAGE` khi vùng ước lượng rỗng hoặc `regionRatio < 0.15`, `luminance < 15`, `luminance > 240`, `contrast < 8`, `sharpness < 3.5` (trường hợp bằng đúng ngưỡng biên vẫn được chấp nhận).
  - Kết quả phân vùng trả về qua API `POST /api/v1/analyze` trên 6 ảnh tập `test` (`docs/benchmarks/frangi_runtime_api_v1.2.json`) khớp 100% với báo cáo offline `docs/benchmarks/frangi_test_v0.2.json` (`Mean Dice = 0.4201`, `Mean IoU = 0.2662`, `Mean Sensitivity = 0.4198`, `Mean Specificity = 0.9565`).
  - Trả về `is_mock: false`, `risk_available: false`, `risk_assessment: null`, `status: "WARNING"` kèm `RISK_UNAVAILABLE`.

### 12.2. Kiểm thử khói (Smoke-Run) Pipeline Huấn luyện Phân vùng (`src/ai-core/scripts/run_training_smoke.py`)
- Cấu hình: `src/ai-core/config/segmentation-training-smoke.yaml`
- Báo cáo đầu ra: `docs/benchmarks/segmentation_training_smoke_v0.1.json`
- Đặc tính kỹ thuật và bảo toàn tập dữ liệu:
  - Chỉ nạp `16` ảnh tập `train` (8 đối tượng: `03, 06, 07, 08, 09, 12, 13, 14`) và `6` ảnh tập `validation` (3 đối tượng: `04, 05, 10`) theo `manifest.csv` (`seed=42`).
  - Tuyệt đối không nạp hoặc chạm tới tập `test` (`test_images_loaded: 0`, `test_split_touched: false`, `subject_leakage_detected: false`).
  - Hàm mất mát `train_weighted_bce_loss` giảm đều qua 5 epoch (`0.653302` → `0.598443`).
  - Không tổng hợp hoặc suy diễn nhãn rủi ro lâm sàng (`risk_labels_used: false`, `replaces_runtime_frangi: false`).


---

## 13. Tối ưu hóa Thuật toán Frangi 2D v0.2 & Benchmark Đa độ phân giải trên Tập HRF (Sprint 5)

> **Mã Jira**: SCRUM-371 (`W5-M4-01`) · **Tác giả**: Đào Duy Quân (M4 — AI Engineer) · **Cập nhật**: 2026-11-01

### 13.1. Đặc tính Kỹ thuật & Tập dữ liệu HRF
- Tập dữ liệu: High-Resolution Fundus (HRF) Image Database gồm 45 ảnh chụp võng mạc siêu phân giải ($3504 \times 2336$ pixel), bao gồm 15 ca Bệnh võng mạc đái tháo đường (`dr`), 15 ca Mắt khỏe mạnh (`h`), và 15 ca Glôcôm (`g`).
- Lưu trữ cục bộ: `datasets/HRF` (được giải nén an toàn từ `data-assets/HRF/HRF.zip`, tuân thủ `.gitignore`).
- Phân chia tập dữ liệu: Xác định bằng giải thuật giả ngẫu nhiên cố định (`seed=42`) ở cấp độ bệnh nhân:
  - `train`: 27 ảnh (9 dr, 9 h, 9 g)
  - `validation`: 9 ảnh (3 dr, 3 h, 3 g)
  - `test`: 9 ảnh (3 dr, 3 h, 3 g)
- Chi tiết manifest và báo cáo xác thực:
  - `docs/datasets/hrf/manifest.csv`
  - `docs/datasets/hrf/validation_report.json`

### 13.2. Pipeline Tối ưu hóa Tiền xử lý & Đo lường
- File triển khai: `src/ai-core/app/services/image_preprocessor.py`
- Bộ nâng cao độ tương phản: CLAHE (`clipLimit=0.02`, `tileGridSize=(8,8)`).
- Trích xuất vùng hữu dụng (FOV mask): Thuật toán tối ưu hóa đa tỷ lệ bằng morphological hole-filling và separable boundary erosion (< 60ms).
- Xử lý đa độ phân giải (Smart Multi-Resolution Processing):
  - Kích thước xử lý giới hạn ở chiều rộng 1200 pixel để kiểm soát bộ nhớ RAM < 200 MiB và độ trễ CPU.
  - Phân vùng mạch máu bằng bộ lọc Frangi Hessian vector hóa đa tỷ lệ (`sigmas=[1.0, 2.0, 3.0]`).
  - Phục hồi hình học gốc: Mặt nạ phân vùng nhị phân được nội suy (Nearest-Neighbor) trở lại đúng kích thước gốc $3504 \times 2336$ pixel.
- Báo cáo Benchmark: `docs/benchmarks/frangi_hrf_benchmark_v1.0.json` (kiểm thử độc lập trên 9 ảnh tập `test`):
  - **Độ trễ trung vị (p50)**: `1343.95 ms` (< 1.5s SLA — **ĐẠT**)
  - **Độ trễ phân vị 95 (p95)**: `1407.72 ms` (< 1.5s SLA — **ĐẠT**)
  - **Độ đặc hiệu trung bình (Mean Specificity)**: `0.9867` (98.67%)
  - **Mean Dice**: `0.4010`

---

## 14. Mô hình Sàng lọc Bệnh Võng Mạc Đái Tháo Đường (DR Screening) & Explainable AI Grad-CAM (Sprint 5)

> **Mã Jira**: SCRUM-372 (`W5-M4-02`) · **Tác giả**: Đào Duy Quân (M4 — AI Engineer) · **Cập nhật**: 2026-11-01

### 14.1. Cam kết Tuân thủ Master Audit & Giới hạn Lâm sàng
- **Cam kết phạm vi chẩn đoán**: Mô hình được thiết kế thuần túy cho mục đích **Sàng lọc Nhị phân (Binary DR Screening — Referable DR vs Low Risk)** giữa ca bệnh đái tháo đường (`dr`) và ca bình thường (`h`).
- **Tuyệt đối KHÔNG giả lập phân loại 5 giai đoạn ICDR**: Do tập HRF chỉ cung cấp nhãn danh mục nhị phân và không có nhãn giai đoạn lâm sàng (ICDR Severity Levels 0–4), hệ thống không tạo lập hoặc suy diễn các số liệu chẩn đoán giai đoạn thiếu cơ sở khoa học.
- Mọi kết quả phân tích đều đính kèm thông cáo miễn trừ trách nhiệm y khoa:
  > *"DISCLAIMER: Binary Diabetic Retinopathy screening tool (Referable DR vs Low Risk). NOT an ICDR 5-stage clinical diagnosis. HRF dataset lacks multi-stage ground truth. Clinical diagnosis must be confirmed by an ophthalmologist."*

### 14.2. Huấn luyện Mô hình & Đánh giá Tiêu chí Nghiệm thu (AC)
- File huấn luyện: `src/ai-core/scripts/train_dr_screening.py`
- Trọng số mô hình: `src/ai-core/config/dr_screening_weights.json`
- Báo cáo Benchmark: `docs/benchmarks/dr_screening_hrf_benchmark_v1.0.json`
- **Kết quả nghiệm thu AC1**:
  - **AUC-ROC trên tập Validation**: `0.8889` (Mục tiêu AC1: $\ge 0.88$ — **ĐẠT**)
  - **AUC-ROC trên tập Test**: `1.0000`
  - **Độ đặc hiệu (Specificity)**: `1.0000` (100% không báo động nhầm trên mắt khỏe mạnh)
  - **Độ chính xác tổng thể (Accuracy)**: `83.33%` trên validation, `100%` trên test

### 14.3. Trích xuất Bản đồ Nhiệt Giải thích Grad-CAM (AC2 & NFR-22)
- File dịch vụ: `src/ai-core/app/services/dr_screening_service.py`
- Thuật toán Grad-CAM / Retinal Saliency làm nổi bật trực tiếp các tổn thương vi phình mạch (microaneurysms), xuất huyết điểm (hemorrhages) và xuất tiết cứng (hard exudates).
- Bản đồ nhiệt được phối màu chuẩn JET/Warm và hòa trộn (alpha blending) với ảnh gốc, xuất ra định dạng chuỗi Base64 PNG kèm tọa độ tâm cụm tổn thương phục vụ hiển thị trực quan trên Doctor Workspace.

---

## 15. Pipeline Phân loại Động/Tĩnh Mạch & Đo Tỷ Số AVR trên Tập INSPIRE-AVR (Sprint 5)

> **Mã Jira**: SCRUM-373 (`W5-M4-03`) · **Tác giả**: Đào Duy Quân (M4 — AI Engineer) · **Cập nhật**: 2026-11-01

### 15.1. Nguồn dữ liệu & Cấu trúc Phân đoạn
- Tập dữ liệu: INSPIRE-AVR (100 ca chụp đáy mắt chuẩn y khoa tại `datasets/INSPIRE_AVR`), cung cấp cặp mặt nạ chuẩn vàng Động mạch (`--arteries.jpg` / `--artry.jpg`) và Tĩnh mạch (`--veins.jpg`).
- Phân chia tập dữ liệu (`docs/datasets/inspire_avr/manifest.csv`):
  - `train`: 70 ca (trong đó 65 ca có đầy đủ cặp nhãn AV)
  - `validation`: 15 ca (14 ca có nhãn AV)
  - `test`: 14 ca (đầy đủ nhãn AV)

### 15.2. Thuật toán Đo Khẩu kính Mạch máu & Tỷ số AVR Knudtson
- File dịch vụ: `src/ai-core/app/services/avr_service.py`
- Định vị Đĩa thị (Optic Disc Landmark): Xác định tâm $(cx, cy)$ và bán kính $R_{\text{OD}}$ bằng Gaussian contrast-centroid.
- Vùng khảo sát chuẩn lâm sàng (Zone B Annulus ROI): Vành khuyên đồng tâm từ $2.0 \cdot R_{\text{OD}}$ đến $3.0 \cdot R_{\text{OD}}$ (Knudtson et al., 2003).
- Công thức phối hợp khẩu kính:
  - Khẩu kính Động mạch tương đương (CRAE):
    $$CRAE = \sqrt{0.87 \cdot w_1^2 + 1.01 \cdot w_2^2 - 0.22 \cdot w_1 \cdot w_2}$$
  - Khẩu kính Tĩnh mạch tương đương (CRVE):
    $$CRVE = \sqrt{0.72 \cdot w_1^2 + 0.91 \cdot w_2^2}$$
  - Tỷ số Động/Tĩnh mạch ($AVR$):
    $$AVR = \frac{CRAE}{CRVE}$$
- Phân tầng nguy cơ bệnh võng mạc tăng huyết áp:
  - `NORMAL`: $AVR \ge 0.65$
  - `BORDERLINE`: $0.60 \le AVR < 0.65$
  - `HYPERTENSION_RISK`: $AVR < 0.60$ (co hẹp động mạch lan tỏa — Generalized Arteriolar Narrowing)

### 15.3. Xóa bỏ Giới hạn Cứng & Cơ chế Báo động Graceful Degraded Fallback
- **Tuân thủ Master Audit**: Hệ thống **loại bỏ hoàn toàn ràng buộc gán ép nhân tạo $AVR \in [0.40, 0.85]$**.
- Khi ảnh bị suy giảm chất lượng nghiêm trọng, không thể định vị đĩa thị, hoặc số lượng mạch máu trong Zone B không đủ điều kiện đo lường tin cậy:
  - Trả về `status: "WARNING"`
  - `risk_available: false`
  - `avr: null`, `crae_px: null`, `crve_px: null`
  - `risk_level: "RISK_UNAVAILABLE"`
  - `warning_code: "RISK_UNAVAILABLE"`
  - Tuyệt đối không sinh số liệu sinh học giả tạo để vượt qua kiểm thử.
- Báo cáo Benchmark: `docs/benchmarks/inspire_avr_benchmark_v1.0.json`:
  - Tổng số mẫu kiểm thử: 29 ca (Validation + Test)
  - Tỷ lệ đo đạc thành công: 100% (Mean AVR = `0.9658`, p50 Latency = `638.74 ms`)
  - Phát hiện chính xác ca lâm sàng có biểu hiện co thắt động mạch thực tế (`IM003537`: $AVR = 0.5294 < 0.60 \implies \text{HYPERTENSION\_RISK}$)
- Ảnh trực quan hóa lâm sàng: Động mạch phân màu Đỏ, Tĩnh mạch phân màu Xanh, Vành khuyên Zone B viền Xanh lá và Tâm đĩa thị chấm Vàng, trả về qua Base64 PNG.
