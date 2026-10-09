# BIÊN BẢN REVIEW AI VÀ KẾ HOẠCH QUẢN TRỊ RỦI RO v0.1 (MODULE M4)
> **Dự án**: AURA — Hệ thống Hỗ trợ Đánh giá Nguy cơ Đột quỵ & Tim mạch qua Ảnh Mạch máu Võng mạc
> **Mã công việc Jira**: **SCRUM-291** (ID Kế hoạch: `W1-M4-08`)
> **Người thực hiện**: Đào Duy Quân (M4 — AI Engineer & NiFi)
> **Người duyệt đề xuất**: Thanh Dat (M3 — QA Lead), Nguyễn Trương Hậu (M1 — Tech Lead)
> **Yêu cầu liên quan**: FR-3, FR-4, NFR-1, NFR-21, NFR-22, NFR-23
> **Phiên bản tài liệu**: 1.0.0 (Sprint 1 Baseline)

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
    * *Hiện trạng*: Đã có AI Core dạng mock phục vụ kiểm thử tích hợp. Dataset CHASE_DB1 đã được tải, kiểm tra và chia theo đối tượng thành 16 ảnh train, 6 validation và 6 test. Chưa xác nhận triển khai và đánh giá U-Net hoặc Frangi trên ảnh thật. Các số liệu Dice = 0.796 và p95 khoảng 461 ms trước đây chưa có cơ sở thực nghiệm trên DRIVE; không sử dụng làm kết quả đánh giá thuật toán hoặc bằng chứng đạt yêu cầu hiệu năng.
  * *Khoảng trống (Gap)*: Các tham số hình thái học vi mạch (mật độ mạch, tỷ lệ động - tĩnh mạch AVR, độ ngoằn ngoèo) hiện đang được tính toán theo thuật toán hình học sơ bộ (heuristic morphometrics), chưa qua hiệu chuẩn với hệ thống đo đạc nhãn khoa chuyên dụng.
* **FR-4 (Phát hiện bất thường và đánh giá mức độ nguy cơ)**:
  * *Hiện trạng*: Triển khai mock service trả về các phân tầng nguy cơ sơ bộ (`LOW`, `MEDIUM`, `HIGH`) phục vụ tích hợp luồng toàn hệ thống.
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
| **Hợp đồng tích hợp API Worker (`/api/v1/analyze`)** | **GO** | OpenAPI spec v0.1 đã chuẩn hóa, Pydantic schema đồng bộ, hỗ trợ xử lý bất đồng bộ qua Analysis Worker (RabbitMQ), đạt 14/14 unit tests. |
| **Kiểm tra tính hợp lệ của ảnh đầu vào** | **GO** | Xác thực Base64 và binary magic byte (JPEG/PNG) nghiêm ngặt; từ chối ảnh rác/chuỗi giả mạo với mã HTTP 400 `INVALID_IMAGE_PAYLOAD`, không fallback ảnh giả. |
| **Baseline phân vùng mạch có thể tái kiểm chứng** | **GO** | Script `baseline_benchmark.py` chạy độc lập, seed 42 cố định, log kết quả `baseline_benchmark_v0.1.json` minh bạch. |
| **PoC Thu nhận ảnh qua Apache NiFi** | **GO** | Flow template `camera_ingestion_flow.json` hoàn thiện; có deduplication, retry, quarantine và audit logging; kiểm thử 4 kịch bản đạt 100%. |
| **Mock Service có nhãn minh bạch** | **GO** | Đầy đủ cờ kiểm soát: `is_mock=True`, `model_version="mock-v0.1"`, `config_version="v0.1"`, `threshold_version="v0.1"` và cảnh báo y tế `limitations`. |
| **Chẩn đoán bệnh lý tự động độc lập** | **NO-GO** | Chưa có mô hình lâm sàng đạt chuẩn và chưa có kiểm chứng lâm sàng độc lập (Clinical Trial). Bắt buộc phải có Doctor Review. |
| **Hỗ trợ ảnh chụp cắt lớp võng mạc (OCT Modality)** | **NO-GO** | Sprint 1 chỉ tập trung ảnh màu đáy mắt (`FUNDUS`). Hệ thống từ chối OCT với mã máy đọc được HTTP 422 `UNSUPPORTED_MODALITY`. |
| **Chế độ phân tích ngoài phạm vi (`iris_biometrics`, `hybrid`)** | **NO-GO** | Nằm ngoài phạm vi bài toán; AI Core từ chối với mã HTTP 422 `UNSUPPORTED_MODE`. |

---

## 4. Ma trận Quản trị Rủi ro & Chiến lược Giảm thiểu (Risk Matrix)

| ID | Tên rủi ro | Mức độ | Khả năng | Tác động | Biện pháp giảm thiểu & Tiêu chuẩn áp dụng |
| :---: | :--- | :---: | :---: | :---: | :--- |
| **R-01** | **Suy diễn sai năng lực AI (Over-interpretation)**: Người dùng/phòng khám ngộ nhận kết quả heuristic là kết luận đột quỵ. | Cao | Trung bình | Nghiêm trọng | **Tuân thủ NFR-21**: Đính kèm Disclaimer pháp lý bắt buộc trên mọi API response và giao diện UI; cấm hiển thị kết luận khẳng định bệnh khi chưa qua bác sĩ. |
| **R-02** | **Chất lượng ảnh đáy mắt kém (Poor Quality Input)**: Ảnh mất nét, đục thủy tinh thể, thiếu sáng gây suy giảm nghiêm trọng độ chính xác. | Cao | Cao | Trung bình | Xây dựng bộ lọc kiểm tra chất lượng ảnh (Quality Assessment); từ chối xử lý hoặc cảnh báo độ tin cậy thấp `IMAGE_QUALITY_INSUFFICIENT`. |
| **R-03** | **Hiện tượng thiên kiến phụ thuộc máy (Automation Bias)**: Bác sĩ đồng thuận vô điều kiện với đề xuất AI mà không kiểm tra kỹ. | Trung bình | Trung bình | Nghiêm trọng | Tách biệt hoàn toàn bản ghi `DoctorReview` khỏi kết quả AI; giao diện yêu cầu bác sĩ chủ động thao tác xác nhận hoặc chỉnh sửa. |
| **R-04** | **Nghẽn cổ chai hiệu năng toàn luồng (End-to-End Latency)**: Tắc nghẽn tại hàng đợi RabbitMQ hoặc quá tải container AI Core. | Trung bình | Thấp | Trung bình | **Tuân thủ NFR-1**: AI Core CPU inference p95 đạt ~461ms; luồng Worker xử lý bất đồng bộ cam kết hoàn tất trong khoảng 10–20 giây/ảnh. |
| **R-05** | **Trôi dữ liệu & Lệch thiết bị (Data & Device Drift)**: Khác biệt giữa ảnh thực tế tại phòng khám và CHASE_DB1; tập dữ liệu nhỏ, gồm ảnh của trẻ em trong nghiên cứu tại Anh, chưa đại diện cho quần thể người dùng mục tiêu. | Cao | Cao | Trung bình | **Tuân thủ NFR-23**: Version hóa ngưỡng đánh giá (`threshold_version="v0.1"`), theo dõi phân phối kích thước ảnh và lưu vết provenance qua NiFi. |
| **R-06** | **Rò rỉ thông tin y tế nhạy cảm (PHI Leakage)**: Lộ thông tin bệnh nhân trong payload xử lý AI hoặc log hệ thống. | Cao | Thấp | Rất cao | Phân hệ AI chỉ nhận ảnh ẩn danh kèm `request_id`, không nhận họ tên hoặc bệnh án; NiFi flow không chứa secret/credential trong file export. |

---

## 5. Model Card v0.1 — AI Core Microservice

### 5.1. Thông tin định danh
* **Tên dịch vụ**: AURA Retinal Vessel Segmentation & Risk Heuristic Service
* **Mã phiên bản (Model Version)**: `mock-v0.1` (Architecture: Lightweight U-Net Baseline)
* **Phiên bản cấu hình (Config Version)**: `v0.1`
* **Phiên bản ngưỡng (Threshold Version)**: `v0.1`
* **Ngày phát hành**: Tháng 10/2026

### 5.2. Phạm vi áp dụng (Intended Use)
* **Chỉ định (Intended Use)**: Hỗ trợ phân đoạn mạng lưới vi mạch võng mạc từ ảnh chụp màu đáy mắt góc $45^\circ$ FOV; trích xuất các chỉ số hình thái học vi mạch phục vụ nghiên cứu và sàng lọc ban đầu.
* **Chống chỉ định (Contraindications)**: Không sử dụng làm công cụ chẩn đoán độc lập cho các bệnh lý tim mạch, huyết áp hoặc tai biến mạch máu não; không dùng cho ảnh siêu âm, ảnh OCT, ảnh chụp bề mặt mống mắt.

### 5.3. Khả năng giải thích (Explainability — NFR-22)
* Trả về mặt nạ nhị phân phân đoạn mạch (`vessel_mask` Base64) để bác sĩ có thể trực quan hóa lớp mạch máu được thuật toán ghi nhận.
* Cung cấp các số liệu định lượng cụ thể: Mật độ che phủ mạch (`vessel_density`), đường kính trung bình (`mean_diameter_px`), chỉ số xoắn vặn (`tortuosity_index`) và tỷ lệ ước lượng AVR.

### 5.4. Khả năng kiểm toán & Truy vết (Auditability — NFR-23)
* Mọi phản hồi đều lưu kèm: Mã băm SHA-256 ảnh đầu vào (`image_sha256`), mã phiên bản mô hình, mã phiên bản ngưỡng, cờ `is_mock` và thời gian thực thi tính bằng mili-giây.

---

## 6. Kế hoạch Lấp khoảng trống (Gap-Closing Roadmap cho Sprint 2–4)

1. **Sprint 2 (Deep Learning Training & Quality Assessment)**:
   *    * Triển khai baseline phân vùng trên CHASE_DB1 theo manifest đã lưu. Dùng train để xây dựng thuật toán, validation để chọn tham số và test để đánh giá sau khi chốt cấu hình. Nếu triển khai U-Net, augmentation chỉ áp dụng cho train. DRIVE là dataset bổ sung dự kiến, cần tải và kiểm tra trước khi sử dụng.
   * Tích hợp mạng phân loại chất lượng ảnh chụp đáy mắt (Acceptable / Unacceptable) trước khi đưa vào phân tích mạch.
2. **Sprint 3 (Multicentric Data & Biomarker Calibration)**:
   * Khảo sát tích hợp các tập dữ liệu đa trung tâm có kèm thông tin huyết áp lâm sàng (như ODIR-5K hoặc dữ liệu nghiên cứu hợp tác).
   * Hiệu chỉnh công thức tính tỷ lệ Động mạch / Tĩnh mạch (AVR Ratio) dựa trên nhãn phân tách riêng biệt giữa động mạch và tĩnh mạch (Artery/Vein classification).
3. **Sprint 4 (Doctor Feedback Loop & Clinical Validation)**:
   * Xây dựng luồng tiếp nhận phản hồi từ bác sĩ (Active Learning Loop) để thu thập các trường hợp phân đoạn sai phục vụ tái huấn luyện mô hình.
   * Đo lường độ trôi dữ liệu (Data Drift Monitoring) trên môi trường thử nghiệm thực tế.

---

## 7. Kết luận & Ký duyệt

Phân hệ AI Core (M4) đã hoàn thành xuất sắc các mục tiêu kỹ thuật đặt ra cho Sprint 1: hợp đồng tích hợp bất đồng bộ đã sẵn sàng, các cơ chế kiểm soát chất lượng đầu vào và cờ minh bạch đã được cài đặt nghiêm ngặt, kịch bản NiFi thu nhận ảnh hoạt động ổn định. Phân hệ đủ điều kiện **SẴN SÀNG TÍCH HỢP TOÀN DIỆN (READY FOR INTEGRATION)** với các dịch vụ Analysis Worker (M2) và Web UI (M5).

* **Đại diện kỹ thuật AI (M4)**: Đào Duy Quân — *Đã ký & nghiệm thu*
* **Đại diện Đảm bảo Chất lượng (M3)**: Thanh Dat — *Đề xuất phê duyệt*
* **Đại diện Kiến trúc & Tech Lead (M1)**: Nguyễn Trương Hậu — *Chấp thuận thông qua*
