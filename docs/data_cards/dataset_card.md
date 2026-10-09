> **Trạng thái**: Đã kiểm tra CHASE_DB1 và tạo manifest chia tập theo đối tượng; chưa đánh giá thuật toán trên dữ liệu thật

> **Dự án**: AURA — Phân tích mạch máu võng mạc hỗ trợ đánh giá sức khỏe
> **Mã Jira**: SCRUM-59 — Kiểm tra dataset và giấy phép; SCRUM-290 — Version NiFi và dữ liệu huấn luyện
> **Người biên soạn ban đầu**: Đào Duy Quân (M4 — AI Engineer)
> **Phiên bản tài liệu**: 1.1.0
> **Ngày cập nhật**: 2026-10-09
> **Trạng thái**: Đã kiểm tra dữ liệu CHASE_DB1; chưa hoàn thành chia tập và đánh giá thuật toán trên dữ liệu thật

---

## 1. Dataset thực tế và phạm vi sử dụng

Dataset thực tế đã tải và kiểm tra là CHASE_DB1.

| Dataset | Trạng thái trong dự án | Mục đích |
|---|---|---|
| CHASE_DB1 | Đã tải và kiểm tra ảnh–nhãn | Baseline phân vùng mạch máu và kiểm thử xử lý ảnh |
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

Dự án giữ ảnh gốc ở máy local và không commit ảnh vào Git. Đây là quy ước quản lý repository của nhóm.

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

Quy ước đánh giá dự kiến:
- Dùng `_1stHO.png` làm nhãn tham chiếu chính.
- Dùng `_2ndHO.png` để đánh giá sự khác biệt giữa hai người đánh dấu.
- Không coi mask của người đánh dấu thứ hai là kết quả dự đoán của AI.
- Chuẩn hóa mask thành nhãn 0/1 trước khi tính metric.

Dataset được Git bỏ qua. Tài liệu, script kiểm tra và manifest chia tập sẽ được quản lý phiên bản trong repository.

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
- Chưa thực hiện benchmark thuật toán.
## 6. Tiền xử lý và vùng đánh giá

Trạng thái hiện tại: chưa xác nhận pipeline tiền xử lý thực tế trên CHASE_DB1.

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

Chưa có kết quả Dice/IoU của thuật toán trên CHASE_DB1 được xác nhận trong Data Card này.

Các kết quả từ dữ liệu giả lập, giá trị sinh ngẫu nhiên hoặc vòng lặp mô phỏng:
- Phải được ghi rõ là mô phỏng.
- Không được trình bày như độ chính xác phân vùng trên ảnh thật.
- Không được dùng để xác nhận độ trễ toàn luồng AURA.

Một benchmark thực tế cần:
- Đọc ảnh thật.
- Chạy thuật toán tạo mask dự đoán.
- So sánh mask dự đoán với nhãn tham chiếu.
- Đo thời gian thực thi.
- Ghi dataset, split, cấu hình, phiên bản mã nguồn và môi trường chạy.

Phần triển khai và chạy benchmark thuộc task baseline/benchmark tương ứng.

---

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