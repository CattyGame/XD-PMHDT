# DATA CARD — DỮ LIỆU HUẤN LUYỆN & KIỂM THỬ AURA (MODULE M4)
> **Dự án**: AURA - Phân tích Mạch máu Võng mạc Hỗ trợ Đánh giá Sức khỏe
> **Mã công việc Jira**: [SCRUM-59] Kiểm tra dataset và giấy phép | [SCRUM-290] Version NiFi và dữ liệu huấn luyện
> **Người biên soạn**: Đào Duy Quân (M4 - AI Engineer)
> **Phiên bản**: 1.0.0

---

## 1. Tổng quan các bộ dữ liệu y tế

Hệ thống AURA sử dụng 3 bộ dữ liệu ảnh đáy mắt chuẩn mở trong giai đoạn nghiên cứu thuật toán và đo đạc baseline:

| Tên Dataset | Nguồn cung cấp | Số lượng mẫu | Độ phân giải gốc | Mục đích sử dụng trong AURA |
| :--- | :--- | :--- | :--- | :--- |
| **DRIVE** | Utrecht University (Hà Lan) | 40 ảnh màu đáy mắt | $565 \times 584$ | Huấn luyện & Benchmark phân vùng hệ mạch (Segmentation Baseline) |
| **STARE** | University of California (Mỹ) | 20 ảnh màu đáy mắt | $700 \times 605$ | Kiểm thử chéo độ bền vững thuật toán hình thái học (Frangi / U-Net) |
| **APTOS 2019** | Aravind Eye Hospital (Ấn Độ) | 3,662 ảnh màu đáy mắt | Biến thiên ($1536 \times 2048 \rightarrow 3216 \times 2136$) | Khảo sát phân loại bệnh võng mạc tiểu đường (DR Grades 0–4) |

---

## 2. Bản quyền, Giấy phép & Điều kiện pháp lý y tế

### 2.1. DRIVE Dataset
* **Nguồn chính thức**: [https://drive.grand-challenge.org/](https://drive.grand-challenge.org/)
* **Giấy phép**: Tự do sử dụng cho mục đích học thuật, phi thương mại (Creative Commons / Academic Use with Citation).
* **Điều kiện trích dẫn**: Bắt buộc trích dẫn công trình: *Staal, J., et al. (2004). Ridge based vessel segmentation in color images of the retina. IEEE TMI, 23(4), 501-509*.
* **Bảo vệ dữ liệu (PII/PHI)**: Ảnh đã được khử định danh 100% (De-identified) theo chuẩn y tế, không chứa họ tên, số hồ sơ bệnh án hoặc thông tin định danh cá nhân.

### 2.2. APTOS 2019 Blindness Detection
* **Nguồn chính thức**: Kaggle / Asia Pacific Tele-Ophthalmology Society & Aravind Eye Hospital.
* **Giấy phép**: Thỏa thuận thi đấu Kaggle (Non-commercial research use only).
* **Quyền sử dụng**: Chỉ sử dụng cho mục đích huấn luyện mô hình thực nghiệm trong khuôn khổ đồ án môn học; nghiêm cấm phân phối dữ liệu gốc hoặc thương mại hóa khi chưa có chấp thuận của Hội đồng Đạo đức Y sinh (IRB).

---

## 3. Quy chuẩn Phân chia tập dữ liệu (Dataset Splitting)

Để ngăn chặn hiện tượng rò rỉ dữ liệu (Data Leakage) giữa tập huấn luyện và kiểm thử:
* **Quy tắc phân chia**: Áp dụng **Official Split** chính thức của DRIVE:
  - Tập huấn luyện (`train`): 20 ảnh (kèm nhãn phân đoạn thủ công Manual 1).
  - Tập kiểm thử (`test`): 20 ảnh (kèm nhãn đối chứng độc lập giữa Manual 1 và Manual 2).
* **Cố định ngẫu nhiên (Reproducibility Seed)**:
  - Giá trị seed: `seed = 42`.
  - Toàn bộ các script tiền xử lý, augmentation và phân chia tập đều cố định seed 42 để đảm bảo kết quả benchmark tái lập chính xác.
* **Cô lập bệnh nhân (Patient Isolation)**: Ảnh chụp hai mắt (Mắt trái - OS, Mắt phải - OD) của cùng một bệnh nhân bắt buộc phải nằm trong cùng một phân vùng (train hoặc test), không chia tách riêng rẽ.

---

## 4. Đặc điểm quang học & Giới hạn nhân khẩu học

1. **Thiết bị chụp (Acquisition Camera)**:
   - DRIVE: Chụp bằng camera Canon CR5 không giãn đồng tử, góc nhìn $45^\circ$ FOV (Field of View).
   - APTOS: Chụp bằng nhiều thiết bị di động và máy chụp để bàn tại các phòng khám nông thôn Ấn Độ (Topcon, Zeiss, máy cầm tay), dẫn đến độ phân giải và chất lượng ánh sáng không đồng đều.
2. **Quần thể dân số (Demographics)**:
   - Dữ liệu tập trung vào bệnh nhân châu Âu (DRIVE) và Nam Á (APTOS); chưa đại diện đầy đủ cho cấu trúc sắc tố võng mạc của người Đông Nam Á / Việt Nam.
3. **Mặt nạ vùng quan sát (FOV Mask)**:
   - Mỗi ảnh đáy mắt đều có mặt nạ hình tròn nhị phân FOV (Field of View). Mọi phép đo độ chính xác (Dice, IoU, Accuracy) chỉ được tính toán bên trong vùng FOV, loại bỏ viền đen ngoài rìa.

---

## 5. Tuyên bố Khuyến nghị Y tế (Medical AI Disclaimer)

* Các chỉ số trích xuất hình học (Mật độ mạch máu - Vessel Density, Độ xoắn mạch - Tortuosity Index, Tỷ lệ AVR) là **chỉ số đo đạc kỹ thuật hỗ trợ quyết định (Clinical Decision Support)**.
* Hệ thống **tuyệt đối không tự động kết luận chẩn đoán** đột quỵ hay cao huyết áp toàn thân từ ảnh chụp đáy mắt mà bắt buộc phải có sự đánh giá, xác nhận và chỉ định chuyên khoa của Bác sĩ Lâm sàng (Clinician/Doctor).
