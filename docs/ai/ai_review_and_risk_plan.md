# BIÊN BẢN REVIEW AI VÀ KẾ HOẠCH QUẢN TRỊ RỦI RO v0.1 (MODULE M4)
> **Dự án**: AURA — Hệ thống Hỗ trợ Đánh giá Nguy cơ Đột quỵ & Tim mạch qua Ảnh Mạch máu Võng mạc
> **Mã công việc Jira**: **SCRUM-291** (ID Kế hoạch: `W1-M4-08`)
> **Người thực hiện**: Đào Duy Quân (M4 — AI Engineer & NiFi)
> **Người duyệt đề xuất**: Thanh Dat (M3 — QA Lead), Nguyễn Trương Hậu (M1 — Tech Lead)
> **Yêu cầu liên quan**: FR-3, FR-4, NFR-1, NFR-21, NFR-22, NFR-23
> **Phiên bản tài liệu**: 1.1.0 (Sprint 1 Baseline - Đã cập nhật đối soát kỹ thuật thực tế)

---

## 1. Mục tiêu và Bối cảnh Thẩm định
Biên bản này tổng kết quá trình rà soát, đánh giá tính khả thi và xác lập ranh giới y tế cho phân hệ AI Core (M4) trong giai đoạn Sprint 1. Mục tiêu trọng tâm:
1. Xác định ranh giới chức năng giữa kết quả xử lý thuật toán và bằng chứng lâm sàng y tế thực tế.
2. Đưa ra quyết định **GO / NO-GO** cho từng đầu ra kỹ thuật và mô hình AI.
3. Thiết lập ma trận rủi ro (Risk Matrix) và biện pháp giảm thiểu tương ứng với các yêu cầu phi chức năng (NFR-1, NFR-21, NFR-22, NFR-23).
4. Xác lập Model Card v0.1 và lộ trình lấp khoảng trống (Gap-closing Backlog) cho các Sprint tiếp theo.

---

## 2. Thẩm định Tính khả thi AI & Khoảng trống Chức năng (Gap Analysis)

### 2.1. Đánh giá tính khả thi theo yêu cầu chức năng
* **FR-3 (Phân tích mạch máu võng mạc - Retinal Vessel Analysis)**:
  * *Hiện trạng*: Đã triển khai script baseline Frangi v0.2 trên tập dữ liệu chuẩn CHASE_DB1 (chia 16 train / 6 val / 6 test theo đối tượng). Ngưỡng tối ưu 0.02 được chọn trên tập validation. Trên 6 ảnh test của 3 đối tượng độc lập, thuật toán đạt mean Dice = 0.4201, mean IoU = 0.2662, sensitivity = 0.4198 và specificity = 0.9565. Thời gian đo đạc thuật toán cục bộ trên CPU máy trạm ghi nhận trung vị 1729.30 ms - 3000.87 ms và p95 1765.00 ms - 3241.07 ms (tùy cấu hình CPU và môi trường runtime; chưa bao gồm disk I/O, hash SHA-256, encode PNG). Baseline còn nhiều nhiễu, bỏ sót các nhánh vi mạch nhỏ và chưa đạt ngưỡng chất lượng lâm sàng hoặc NFR toàn luồng. API hiện vẫn là Mock Service độc lập; chưa tích hợp Frangi baseline vào API hay Worker và chưa triển khai mô hình học sâu U-Net.
  * *Khoảng trống (Gap)*: Các tham số hình thái học vi mạch (`vessel_density`, `mean_diameter_px`, `tortuosity_index`, `avr_ratio`) hiện tại trong API chỉ là các hằng số mock định trước (`0.18`, `4.2`, `1.15`, `0.67`) phục vụ tích hợp giao diện và luồng dữ liệu, chưa thực hiện tính toán hình thái học thật từ ảnh hay từ mô hình học sâu. Việc trích xuất và đo đạc hình thái học vi mạch thật cần được phát triển và hiệu chuẩn lâm sàng ở các Sprint tiếp theo.
* **FR-4 (Phát hiện bất thường và đánh giá mức độ nguy cơ)**:
  * *Hiện trạng*: Triển khai mock service trả về các phân tầng nguy cơ sơ bộ (`LOW`, `MODERATE`, `HIGH`) phục vụ tích hợp luồng toàn hệ thống. Enum `RiskLevel` đã được đồng bộ chuẩn hóa giữa hợp đồng OpenAPI và Pydantic schema runtime.
  * *Khoảng trống (Gap)*: **CHASE_DB1 đang sử dụng cung cấp nhãn phân vùng mạch máu, không cung cấp nhãn chẩn đoán huyết áp toàn thân hoặc biến cố đột quỵ. DRIVE và APTOS hiện là nguồn tham khảo, chưa được tải và kiểm tra trực tiếp trong dự án.**
    * APTOS 2019 chỉ gắn nhãn mức độ tổn thương võng mạc tiểu đường (DR Grades 0–4).
    * Do đó, hệ thống hiện tại **CHƯA ĐỦ BẰNG CHỨNG LÂM SÀNG** để đưa ra khẳng định bệnh lý tim mạch/đột quỵ hệ thống. Mọi phân tầng nguy cơ ở giai đoạn này chỉ là chỉ số nghiên cứu giả lập (Research Heuristic).

### 2.2. Khẳng định giới hạn đạo đức và y sinh
> [!IMPORTANT]
> Không sử dụng CHASE_DB1 hoặc thông tin tham khảo từ DRIVE/APTOS làm bằng chứng xác nhận khả năng chẩn đoán tăng huyết áp hoặc dự đoán đột quỵ. AI Core hiện phục vụ nghiên cứu và demo kỹ thuật; khả năng hỗ trợ sàng lọc lâm sàng chưa được kiểm chứng.

---

## 3. Quyết định GO / NO-GO (Gating Decision)

Dựa trên kết quả thực nghiệm Sprint 1 và tài liệu chỉ đạo kiến trúc từ Tech Lead:

| Hạng mục / Đầu ra | Quyết định | Lý do & Điều kiện ràng buộc |
| :--- | :---: | :--- |
| **Hợp đồng tích hợp API Worker (`/api/v1/analyze`)** | **GO** | OpenAPI spec v0.1 đã chuẩn hóa (YAML và JSON), Pydantic schema đồng bộ, hỗ trợ xử lý bất đồng bộ qua Analysis Worker (RabbitMQ), đạt 20/20 unit tests. |
| **Kiểm tra tính hợp lệ của ảnh đầu vào** | **GO** | Xác thực Base64 nghiêm ngặt và giải mã toàn bộ pixel qua Pillow `verify()` & `load()`; từ chối ảnh rác/chuỗi giả mạo/ảnh chỉ có header/ảnh cắt cụt CRC với mã HTTP 400 `INVALID_IMAGE_PAYLOAD`, không fallback binary. |
| **Nghiên cứu Baseline phân vùng mạch (Frangi v0.2)** | **GO (Nghiên cứu offline)** | Báo cáo benchmark cũ (`baseline_benchmark_v0.1.json`) chỉ là dữ liệu mô phỏng kỹ thuật (`SIMULATION_ONLY`). Bằng chứng thực nghiệm hợp lệ duy nhất hiện tại là báo cáo Frangi v0.2 trên tập test CHASE_DB1 (`frangi_test_v0.2.json` và bản tái lập `frangi_test_v0.2_reproduced.json`) với Mean Dice 0.4201, IoU 0.2662, Sensitivity 0.4198, Specificity 0.9565; hash script chuẩn hóa LF/CRLF độc lập với OS. Thuật toán hoạt động như công cụ nghiên cứu offline, chưa đạt ngưỡng chất lượng đưa vào sản phẩm và chưa tích hợp vào API. |
| **PoC Thu nhận ảnh qua Apache NiFi** | **GO (Mức độ PoC & Test Harness)** | Flow template `camera_ingestion_flow.json` đã sửa lỗi routing `InvalidPayload = true`, bổ sung DistributedMapCacheServer/Client cho deduplication, gắn trace headers (`X-Correlation-Id`, `Idempotency-Key`) và lưu quarantine bền vững. Script `simulate_camera_nifi.py` bao phủ 6 kịch bản kiểm thử logic. Tuy nhiên, mức độ sẵn sàng 100% chỉ được xác nhận chính thức sau khi flow được triển khai và kiểm thử thực tế trên container Apache NiFi thật (`docker-compose.nifi.yml`). |
| **Mock Service có nhãn minh bạch** | **GO** | Đầy đủ cờ kiểm soát: `is_mock=True`, `model_version="mock-v0.1"`, `config_version="v0.1"`, `threshold_version="v0.1"`, thời gian xử lý đo thực tế (không cộng giả 45.0 ms), PNG mock hợp lệ chuẩn RFC 2083 và cảnh báo y tế `limitations`. |
| **Chẩn đoán bệnh lý tự động độc lập** | **NO-GO** | Chưa có mô hình lâm sàng đạt chuẩn và chưa có kiểm chứng lâm sàng độc lập (Clinical Trial). Bắt buộc phải có Doctor Review. |
| **Hỗ trợ ảnh chụp cắt lớp võng mạc (OCT Modality)** | **NO-GO** | Sprint 1 chỉ tập trung ảnh màu đáy mắt (`FUNDUS`). Hệ thống từ chối OCT với mã máy đọc được HTTP 422 `UNSUPPORTED_MODALITY`. |
| **Chế độ phân tích ngoài phạm vi (`iris_biometrics`, `hybrid`)** | **NO-GO** | Nằm ngoài phạm vi bài toán; AI Core từ chối với mã HTTP 422 `UNSUPPORTED_MODE` (chỉ chấp nhận `retina_vessels`). |

---

## 4. Ma trận Quản trị Rủi ro & Chiến lược Giảm thiểu (Risk Matrix)

| ID | Tên rủi ro | Mức độ | Khả năng | Tác động | Biện pháp giảm thiểu & Tiêu chuẩn áp dụng |
| :---: | :--- | :---: | :---: | :---: | :--- |
| **R-01** | **Suy diễn sai năng lực AI (Over-interpretation)**: Người dùng/phòng khám ngộ nhận kết quả heuristic là kết luận đột quỵ. | Cao | Trung bình | Nghiêm trọng | **Tuân thủ NFR-21**: Đính kèm Disclaimer pháp lý bắt buộc trên mọi API response và giao diện UI; cấm hiển thị kết luận khẳng định bệnh khi chưa qua bác sĩ. |
| **R-02** | **Chất lượng ảnh đáy mắt kém (Poor Quality Input)**: Ảnh mất nét, đục thủy tinh thể, thiếu sáng gây suy giảm nghiêm trọng độ chính xác. | Cao | Cao | Trung bình | Xây dựng bộ lọc kiểm tra chất lượng ảnh (Quality Assessment); từ chối xử lý hoặc cảnh báo độ tin cậy thấp `IMAGE_QUALITY_INSUFFICIENT`. |
| **R-03** | **Hiện tượng thiên kiến phụ thuộc máy (Automation Bias)**: Bác sĩ đồng thuận vô điều kiện với đề xuất AI mà không kiểm tra kỹ. | Trung bình | Trung bình | Nghiêm trọng | Tách biệt hoàn toàn bản ghi `DoctorReview` khỏi kết quả AI; giao diện yêu cầu bác sĩ chủ động thao tác xác nhận hoặc chỉnh sửa. |
| **R-04** | **Nghẽn cổ chai hiệu năng toàn luồng (End-to-End Latency)**: Tắc nghẽn tại hàng đợi RabbitMQ hoặc quá tải container AI Core. | Trung bình | Thấp | Trung bình | **Theo dõi NFR-1**: Bỏ con số giả định cũ (~461 ms). Thuật toán phân vùng Frangi v0.2 cục bộ trên CPU máy trạm (6 ảnh test CHASE_DB1) ghi nhận trung vị ~1.7s - 3.0s và p95 ~1.8s - 3.2s tùy môi trường phần cứng. **Lưu ý**: NFR-1 toàn luồng hệ thống (gồm API, Gateway, RabbitMQ, worker và database) ở trạng thái `NOT_VERIFIED`, cần phối hợp M1/M2 đo kiểm riêng sau khi tích hợp toàn diện. |
| **R-05** | **Trôi dữ liệu & Lệch thiết bị (Data & Device Drift)**: Khác biệt giữa ảnh thực tế tại phòng khám và CHASE_DB1; tập dữ liệu nhỏ, gồm ảnh của trẻ em trong nghiên cứu tại Anh, chưa đại diện cho quần thể người dùng mục tiêu. | Cao | Cao | Trung bình | **Tuân thủ NFR-23**: Version hóa ngưỡng đánh giá (`threshold_version="v0.1"`), theo dõi phân phối kích thước ảnh và lưu vết provenance qua NiFi. |
| **R-06** | **Rò rỉ thông tin y tế nhạy cảm (PHI Leakage)**: Lộ thông tin bệnh nhân trong payload xử lý AI hoặc log hệ thống. | Cao | Thấp | Rất cao | Phân hệ AI chỉ nhận ảnh ẩn danh kèm `request_id`, không nhận họ tên hoặc bệnh án; NiFi flow không chứa secret/credential trong file export. |

---

## 5. Model Card v0.1 — AI Core Microservice

### 5.1. Thông tin định danh
* **Tên dịch vụ**: AURA Retinal Vessel Segmentation & Risk Heuristic Service (AI Core Microservice)
* **Kiến trúc dịch vụ API runtime**: Mock Service (FastAPI) chuẩn hóa theo hợp đồng OpenAPI v0.1 (`model_version="mock-v0.1"`). **Lưu ý**: Mô hình học sâu U-Net chưa được huấn luyện hay tích hợp vào runtime ở Sprint 1.
* **Thuật toán nghiên cứu offline**: Thuật toán lọc mạch Frangi v0.2 (`frangi-baseline-v0.2`) được xây dựng và đánh giá độc lập trên dataset CHASE_DB1; hoàn toàn tách biệt khỏi API runtime.
* **Phiên bản cấu hình (Config Version)**: `v0.1`
* **Phiên bản ngưỡng (Threshold Version)**: `v0.1`
* **Ngày phát hành**: Tháng 10/2026

### 5.2. Phạm vi áp dụng (Intended Use)
* **Chỉ định (Intended Use)**: Hỗ trợ phân đoạn mạng lưới vi mạch võng mạc từ ảnh chụp màu đáy mắt góc $45^\circ$ FOV; trích xuất các chỉ số hình thái học vi mạch phục vụ nghiên cứu và sàng lọc ban đầu.
* **Chống chỉ định (Contraindications)**: Không sử dụng làm công cụ chẩn đoán độc lập cho các bệnh lý tim mạch, huyết áp hoặc tai biến mạch máu não; không dùng cho ảnh siêu âm, ảnh OCT, ảnh chụp bề mặt mống mắt.

### 5.3. Khả năng giải thích (Explainability — NFR-22)
* Trả về mặt nạ nhị phân phân đoạn mạch (`vessel_mask` Base64) và ảnh phủ lớp mạch (`overlay_image` Base64) để bác sĩ và kỹ thuật viên có thể trực quan hóa lớp mạch máu. Trong phiên bản mock hiện tại, API trả về chuỗi PNG mock hợp lệ chuẩn RFC 2083 (kích thước 1x1 trong suốt).
* Cung cấp các số liệu định lượng trong cấu trúc dữ liệu: Mật độ che phủ mạch (`vessel_density`), đường kính trung bình (`mean_diameter_px`), chỉ số xoắn vặn (`tortuosity_index`) và tỷ lệ ước lượng AVR (`avr_ratio`). Cần làm rõ: trong giai đoạn Sprint 1, các chỉ số này là **hằng số mock định trước** phục vụ kiểm thử tích hợp luồng, chưa phải kết quả trích xuất tự động từ ảnh thật.

### 5.4. Khả năng kiểm toán & Truy vết (Auditability — NFR-23)
* Mọi phản hồi runtime của API (`/api/v1/analyze`) đều lưu kèm đầy đủ metadata truy vết: `request_id`, mã phiên bản mô hình (`model_version="mock-v0.1"`), mã phiên bản cấu hình (`config_version="v0.1"`), mã phiên bản ngưỡng (`threshold_version="v0.1"`), cờ minh bạch `is_mock=True`, thời gian thực thi thực tế `execution_time_ms` và cảnh báo y tế `limitations`.
* **Làm rõ về trường `image_sha256`**: Mã băm SHA-256 của ảnh hiện tại được quản lý trong file manifest dataset (`docs/datasets/chase_db1/manifest.csv`) và trong header truy vết của NiFi (`simulate_camera_nifi.py` / Flow), **chưa nằm trong payload phản hồi của API `/api/v1/analyze`** theo hợp đồng OpenAPI v0.1 hiện hành. Nếu các phân hệ Gateway/Worker có nhu cầu bổ sung trường này vào API response root, M4 sẽ phối hợp M1/M2 cập nhật hợp đồng ở phiên bản kế tiếp.

---

## 6. Kế hoạch Lấp khoảng trống (Gap-Closing Roadmap cho Sprint 2–4)

1. **Sprint 2 (Deep Learning Training & Quality Assessment)**:
   * Triển khai baseline phân vùng trên CHASE_DB1 theo manifest đã lưu. Dùng train để xây dựng thuật toán, validation để chọn tham số và test để đánh giá sau khi chốt cấu hình. Nếu triển khai U-Net, augmentation chỉ áp dụng cho train. DRIVE là dataset bổ sung dự kiến, cần tải và kiểm tra trước khi sử dụng.
   * Tích hợp mạng phân loại chất lượng ảnh chụp đáy mắt (Acceptable / Unacceptable) trước khi đưa vào phân tích mạch.
2. **Sprint 3 (Multicentric Data & Biomarker Calibration)**:
   * Khảo sát tích hợp các tập dữ liệu đa trung tâm có kèm thông tin huyết áp lâm sàng (như ODIR-5K hoặc dữ liệu nghiên cứu hợp tác).
   * Hiệu chỉnh công thức tính tỷ lệ Động mạch / Tĩnh mạch (AVR Ratio) dựa trên nhãn phân tách riêng biệt giữa động mạch và tĩnh mạch (Artery/Vein classification).
3. **Sprint 4 (Doctor Feedback Loop & Clinical Validation)**:
   * Xây dựng luồng tiếp nhận phản hồi từ bác sĩ (Active Learning Loop) để thu thập các trường hợp phân đoạn sai phục vụ tái huấn luyện mô hình.
   * Đo lường độ trôi dữ liệu (Data Drift Monitoring) trên môi trường thử nghiệm thực tế.

---

## 7. Kết luận & Ký duyệt

Phân hệ AI Core (M4) đã hoàn thành các mục tiêu kỹ thuật Sprint 1 ở cấp độ mô phỏng tích hợp và thiết lập baseline nghiên cứu:
1. Hợp đồng API v0.1 và Mock Service FastAPI đã được chuẩn hóa và đồng bộ chặt chẽ với Analysis Worker (M2), vượt qua 20/20 unit tests tự động.
2. Kiểm tra tính toàn vẹn của ảnh đầu vào nghiêm ngặt bằng Pillow (`verify()` & `load()`), từ chối triệt để ảnh hỏng/ảnh rác, đo thời gian xử lý thực tế và trả mã PNG mock hợp lệ.
3. Nghiên cứu baseline phân vùng mạch Frangi v0.2 trên tập dữ liệu chuẩn CHASE_DB1 đã hoàn thành và chứng minh tái lập độc lập 100% trên cả Windows và Linux; các số liệu độ trễ và độ chính xác đã được tách bạch rõ ràng khỏi NFR toàn luồng hệ thống.
4. Cấu hình ingestion Apache NiFi và kịch bản test harness đã xử lý toàn diện các luồng nghiệp vụ (deduplication cache, trace headers, persistent quarantine); sẵn sàng kiểm chứng trên container NiFi thực tế.

Phân hệ đủ điều kiện **SẴN SÀNG CHO BƯỚC KIỂM CHỨNG TÍCH HỢP (READY FOR INTEGRATION TESTING)** cùng Analysis Worker (M2), Gateway/DevOps (M1) và Web UI (M5).

* **Đại diện kỹ thuật AI (M4)**: Đào Duy Quân — *Đã hoàn thiện tài liệu, đề xuất nghiệm thu*
* **Đại diện Đảm bảo Chất lượng (M3)**: Thanh Dat — *Chờ kiểm chứng kỹ thuật & review (SCRUM-293)*
* **Đại diện Kiến trúc & Tech Lead (M1)**: Nguyễn Trương Hậu — *Chờ review & phê duyệt thực tế trên Pull Request*
