
# AURA — Baseline phân vùng mạch máu Frangi v0.2

## 1. Phạm vi

Liên quan: SCRUM-60 và phần benchmark thuật toán của SCRUM-64.

Đã triển khai baseline Frangi chạy trực tiếp trên ảnh CHASE_DB1,
tạo mask dự đoán và so sánh với nhãn người đánh dấu thứ nhất.

Đây là baseline nghiên cứu ban đầu, chưa tích hợp vào API/Worker
và chưa được xác nhận phù hợp sử dụng lâm sàng.

## 2. Dataset và chia tập

- Dataset: CHASE_DB1.
- 28 ảnh của 14 đối tượng, mỗi đối tượng có hai mắt.
- Kích thước: 999 × 960 pixel.
- Nhãn tham chiếu: `_1stHO.png`.
- Chia theo đối tượng, seed 42.
- Train: 16 ảnh của 8 đối tượng.
- Validation: 6 ảnh của 3 đối tượng.
- Test: 6 ảnh của 3 đối tượng.

Manifest: `docs/datasets/chase_db1/manifest.csv`.

Frangi là phương pháp không cần huấn luyện trọng số.
Ở phiên bản này, train chưa được dùng để huấn luyện.
Validation được dùng để điều chỉnh cấu hình và chọn ngưỡng;
test dùng để đánh giá cấu hình đã chốt.

## 3. Thuật toán

- Trích kênh xanh lá và chia giá trị pixel cho 255.
- Làm mượt Gaussian, sigma = 1.
- Ước lượng vùng ảnh từ max(R, G, B) > 20.
- Lấp lỗ và erosion 12 lần với cấu trúc 3 × 3.
- Chạy Frangi với sigmas = [1, 2, 3, 5, 8].
- alpha = 0.5; beta = 0.5; gamma = None.
- black_ridges = True; boundary mode = reflect.
- Đặt response ngoài vùng dự đoán về 0.
- Chuẩn hóa response theo giá trị lớn nhất của từng ảnh.
- Chọn ngưỡng 0.02 bằng mean Dice trên validation.

Vùng dự đoán được suy ra từ ảnh đầu vào, không dùng nhãn.
Đây không phải FOV mask chính thức.
Metric được tính trên toàn ảnh ở kích thước gốc.

## 4. Kết quả

Các giá trị là trung bình metric tính riêng trên từng ảnh.

| Chỉ số | Validation v0.2 | Test v0.2 |
|---|---:|---:|
| Dice | 0.3892 | 0.4201 |
| IoU | 0.2423 | 0.2662 |
| Sensitivity | 0.4140 | 0.4198 |
| Specificity | 0.9437 | 0.9565 |

Ngưỡng và thuật toán không được điều chỉnh dựa trên kết quả test.

## 5. Thời gian xử lý trên test

| Chỉ số | Thời gian |
|---|---:|
| Trung vị | 1729.30 ms |
| p95 | 1765.00 ms |
| Lớn nhất | 1768.73 ms |

Phạm vi đo:
- Trích kênh xanh lá.
- Làm mượt và ước lượng vùng dự đoán.
- Frangi, chuẩn hóa và áp dụng ngưỡng.

Không gồm:
- Đọc/ghi file và kiểm tra SHA-256.
- Tính metric và lưu PNG.
- API, hàng đợi và database.

Không thực hiện warm-up.
p95 dùng phép nội suy tuyến tính trên 6 mẫu.
Đây là thống kê mô tả của lần chạy nhỏ, chưa phải đánh giá
hiệu năng ổn định hoặc thời gian toàn luồng.

NFR toàn luồng: NOT_VERIFIED.

## 6. Hạn chế

- Mask dự đoán còn nhiều nhiễu.
- Một số mạch lớn bị phát hiện thành hai đường mảnh.
- Sensitivity khoảng 0.42 cho thấy còn bỏ sót nhiều pixel mạch.
- Specificity cao không đủ để kết luận chất lượng tốt
  vì ảnh có nhiều pixel nền.
- Test chỉ gồm 3 đối tượng; chưa đủ khẳng định khả năng tổng quát.
- Chưa triển khai hoặc đánh giá U-Net.
- Chưa xác nhận đạt ngưỡng chất lượng mục tiêu của dự án.
- Không suy ra chẩn đoán tim mạch hoặc đột quỵ từ kết quả này.

## 7. Tái lập

Chạy tại thư mục gốc repository:

### 7.1. Khởi tạo môi trường ảo chuyên dụng cho baseline
```powershell
uv venv .venv-dataset
# hoặc: python -m venv .venv-dataset

.\.venv-dataset\Scripts\python.exe -m pip install -r src/ai-core/requirements-baseline.txt
```

### 7.2. Kiểm tra bộ dữ liệu và chuẩn bị manifest
```powershell
.\.venv-dataset\Scripts\python.exe src/ai-core/scripts/prepare_chase_db1.py
```

### 7.3. Tái lập đánh giá trên tập kiểm thử (Test Set)
```powershell
.\.venv-dataset\Scripts\python.exe src/ai-core/scripts/evaluate_frangi_test.py
```

*Quy tắc truy vết & An toàn dữ liệu*:
- Script tự động chuẩn hóa ký tự xuống dòng (LF / CRLF) khi kiểm tra mã băm SHA-256, đảm bảo tính nhất quán giữa môi trường Windows và Linux.
- Khi tệp báo cáo gốc `docs/benchmarks/frangi_test_v0.2.json` đã tồn tại, script mặc định bảo lưu tệp gốc và lưu kết quả tái lập mới vào `docs/benchmarks/frangi_test_v0.2_reproduced.json`.
- Script tự động so sánh đối chiếu kết quả tái lập với baseline đã chốt (Dice 0.4201, IoU 0.2662), xác nhận khớp chính xác 100%.
- Không dùng kết quả test để tinh chỉnh lại tham số hay ngưỡng phân ngưỡng của thuật toán.