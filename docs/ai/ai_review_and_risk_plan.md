# REVIEW AI VÀ KẾ HOẠCH QUẢN TRỊ RỦI RO — MODULE M4

> **Dự án**: AURA
> **Mã Jira**: SCRUM-291
> **Người biên soạn ban đầu**: Đào Duy Quân — M4
> **Người duyệt đề xuất**: M1 và M3
> **Phiên bản tài liệu**: 1.2.0
> **Ngày cập nhật**: 2026-10-09
> **Trạng thái**: Đã rà soát mã nguồn và kiểm thử cục bộ; chưa nghiệm thu tích hợp hoặc hiệu quả lâm sàng.

## 1. Mục tiêu

Phân biệt rõ ba thành phần:
- AI API mock phục vụ kiểm thử giao tiếp.
- Baseline Frangi chạy offline trên dữ liệu thật.
- Flow NiFi tiếp nhận, xác thực và chuẩn bị dữ liệu.

Kết quả kiểm thử của một thành phần không được dùng để khẳng định
toàn bộ hệ thống đã hoàn thành hoặc đạt NFR.

## 2. Hiện trạng đã kiểm chứng

### 2.1. AI API mock

Endpoint nội bộ: `/api/v1/analyze`.

Runtime hiện dùng Mock Service, chưa tích hợp Frangi hoặc U-Net.
Đầu ra có:
- `is_mock: true`.
- `model_version: mock-v0.1`.
- `config_version: v0.1`.
- `threshold_version: v0.1`.
- `processing_time_ms`: thời gian thực thi đo tại runtime.
- `limitations`: mô tả giới hạn của kết quả mock.

Các chỉ số hiện là hằng số giả lập:

| Trường | Giá trị |
|---|---:|
| vessel_density | 0.1485 |
| tortuosity_index | 1.165 |
| av_ratio | 0.672 |
| fractal_dimension | 1.475 |
| branching_points | 62 |

Risk assessment hiện trả giá trị mock cố định:
- `risk_score`: 0.32.
- `risk_level`: LOW.
- `confidence_score`: 0.91.

Việc schema có enum mức nguy cơ không chứng minh runtime đã tạo
được nhiều tình huống nguy cơ khác nhau.

Bộ test AI hiện có đã chạy PASS. Kết quả này chưa xác nhận tích hợp
với Worker hoặc chất lượng phân tích ảnh thực tế.

### 2.2. Xác thực ảnh

Runtime kiểm tra:
- Base64 hợp lệ.
- Dung lượng ảnh giải mã không vượt 10 MiB.
- Định dạng được AI nội bộ hỗ trợ: PNG, JPEG, WEBP.
- Kích thước hợp lệ và không vượt 16 triệu pixel.
- Kiểm tra giới hạn trước `verify()` và giải mã pixel đầy đủ bằng `load()`.
- Từ chối dữ liệu không phải ảnh hoặc ảnh hỏng.

Đã kiểm tra ảnh vượt giới hạn bị từ chối trước `load()`.
Đã kiểm tra ảnh hợp lệ có kích thước:
3000×100, 100×3000 và 999×960.

Public Analysis API và nhánh camera NiFi hiện chỉ nhận JPEG/PNG
theo contract của từng thành phần. Không tự mở rộng public API
sang WEBP chỉ vì AI nội bộ hỗ trợ định dạng này.

Ảnh hợp lệ về định dạng không đồng nghĩa đạt chất lượng lâm sàng.
Chưa triển khai bộ đánh giá mất nét, thiếu sáng hoặc chất lượng đáy mắt.

### 2.3. Mask và overlay mock

Các trường trả về:
- `segmentation.mask_base64`.
- `segmentation.overlay_base64`.

Đầu ra giữ đúng kích thước ảnh đầu vào:
- Mask: PNG mode L, giá trị pixel 0 hoặc 255.
- Overlay: PNG mode RGB.

Đây là hình vẽ giả lập phục vụ tích hợp.
Overlay hiện không được tạo bằng cách phủ kết quả phân vùng thật
lên ảnh đầu vào.

Không dùng mask/overlay mock làm bằng chứng về khả năng giải thích
kết quả AI thực tế hoặc nghiệm thu NFR-22.

### 2.4. Dataset và baseline offline

Dataset đã tải và kiểm tra: CHASE_DB1.
- 28 ảnh, 56 mask, 14 đối tượng.
- Kích thước 999×960 pixel.
- Chia tập theo đối tượng, seed 42.
- Train: 16 ảnh; validation: 6 ảnh; test: 6 ảnh.
- Nhãn tham chiếu: `_1stHO.png`.

Baseline: Frangi v0.2.
- Không có bước huấn luyện U-Net.
- Threshold 0.02 được chọn trên validation.
- Test dùng cấu hình đã chốt, không tìm lại threshold.
- Metric được tính trên toàn ảnh.

| Metric trung bình theo ảnh | Validation | Test |
|---|---:|---:|
| Dice | 0.3892 | 0.4201 |
| IoU | 0.2423 | 0.2662 |
| Sensitivity | 0.4140 | 0.4198 |
| Specificity | 0.9437 | 0.9565 |

Bốn metric trung bình đã được tái lập, khớp báo cáo test gốc
trong sai số tuyệt đối < 0.0001.

Thời gian trong báo cáo test gốc:
- Median: 1729.30 ms.
- p95: 1765.00 ms.
- Max: 1768.73 ms.

Đây là thời gian thuật toán cục bộ trên 6 ảnh, chưa warm-up;
không gồm đọc/ghi file, SHA-256, metric, PNG output, API,
queue hoặc database.

Chưa xác nhận hiệu năng ổn định, chất lượng mục tiêu hoặc NFR toàn luồng.
Báo cáo mô phỏng cũ chỉ có giá trị lịch sử với nhãn `SIMULATION_ONLY`.

### 2.5. NiFi ingestion

Flow hiện có:
- Tiếp nhận JSON từ camera simulator.
- Trích xuất metadata và kiểm tra điều kiện routing.
- Xác thực Base64, định dạng và kích thước ảnh.
- Tính SHA-256 và ghi metadata ảnh vào FlowFile attributes.
- Đưa dữ liệu sai vào quarantine.
- Chuẩn bị multipart gồm patientId, modality, eyeSide và images.
- Đưa dữ liệu hợp lệ đến port `PreparedMultipart-PendingGateway`.

Đã kiểm tra JPEG thật được xác thực và giữ nguyên checksum
khi chuẩn bị multipart. Một số dữ liệu sai đã được lưu quarantine.

Flow hiện chưa có:
- InvokeHTTP gửi Gateway.
- DistributedMapCache phục vụ dedup.
- Luồng retry có giới hạn.
- Kiểm chứng service token với backend.

`gateway.idempotency_key` và `gateway.correlation_id` hiện là
FlowFile attributes, chưa phải bằng chứng HTTP headers đã được gửi.

Client simulator chuẩn bị 7 tình huống.
Dry-run không gửi request và không chứng minh các tình huống đã PASS.

HTTP 200 từ ListenHTTP chỉ xác nhận tiếp nhận request;
không xác nhận backend đã tạo analysis.

## 3. Quyết định phạm vi sử dụng

| Thành phần | Quyết định | Điều kiện |
|---|---|---|
| AI API mock | Tiếp tục dùng cho kiểm thử giao tiếp | Hiển thị rõ kết quả mock |
| Xác thực ảnh | Đạt các kiểm tra cục bộ đã chạy | Chưa đánh giá chất lượng đáy mắt |
| Frangi v0.2 | Tiếp tục nghiên cứu offline | Chưa tích hợp runtime; chất lượng còn hạn chế |
| NiFi ingestion | Tiếp tục PoC đến bước chuẩn bị multipart | Chưa nghiệm thu gửi Gateway, dedup hoặc retry |
| Đánh giá nguy cơ lâm sàng | Chưa đủ bằng chứng | Không dùng để chẩn đoán hoặc quyết định điều trị |
| OCT | Ngoài phạm vi hiện tại | Xử lý lỗi theo contract từng API |

Public REST Analysis 0.6.0 và AI API nội bộ là hai contract khác nhau.
Worker phải ánh xạ request, response và lỗi theo quyết định nghiệp vụ.

## 4. Rủi ro và công việc giảm thiểu

| ID | Rủi ro | Công việc cần thực hiện |
|---|---|---|
| R-01 | Người dùng hiểu mock là kết quả thật | Giữ is_mock, limitations và nhãn demo trên UI |
| R-02 | Ảnh hợp lệ nhưng chất lượng kém | Thiết kế đánh giá chất lượng; chốt tín hiệu LOW_QUALITY |
| R-03 | WARNING bị hiểu thành LOW_QUALITY | Chốt mapping AI–Worker; kiểm thử từng trường hợp |
| R-04 | Dùng latency thuật toán để kết luận NFR | Đo toàn luồng sau khi tích hợp |
| R-05 | Dataset nhỏ, lệch quần thể/thiết bị | Ghi giới hạn và đánh giá thêm dữ liệu phù hợp |
| R-06 | Lộ ảnh hoặc dữ liệu bệnh nhân | Kiểm tra quyền file; hạn chế log payload và secret |
| R-07 | Retry tạo analysis hoặc trừ credit trùng | Triển khai idempotency và kiểm thử retry với backend |
| R-08 | Tài liệu vượt quá năng lực mã nguồn | Review theo commit, test và phiên bản tài liệu cụ thể |

Các biện pháp trong bảng là công việc cần triển khai hoặc duy trì,
không mặc định đã hoàn tất.

## 5. Truy vết và versioning

AI response hiện có request_id và các trường phiên bản runtime.
Các trường này hỗ trợ truy vết nhưng không chứng minh audit
toàn hệ thống đã được triển khai.

SHA-256 ảnh hiện được lưu:
- Trong manifest dataset.
- Trong FlowFile attribute `image.sha256` của NiFi.

Không mô tả SHA-256 là HTTP header hoặc trường AI response
khi mã nguồn chưa thực hiện việc đó.

Phân biệt:
- Runtime mock: model_version `mock-v0.1`.
- Thuật toán offline: `frangi-baseline-v0.2`.
- Public REST Analysis: `0.6.0`.
- Event/job schemaVersion: `0.6`.

Không dùng phiên bản public contract làm phiên bản AI nội bộ.

## 6. Công việc tiếp theo

1. Chốt mapping giữa Worker và AI:
   request_id, WARNING, LOW_QUALITY, lỗi và file annotation.
2. Bổ sung các tình huống mock có thể kiểm thử rõ ràng,
   cập nhật schema nếu cần.
3. Worker kiểm tra PNG đầu ra, kích thước và lưu file riêng tư,
   sau đó trả annotation bằng fileId/kind.
4. Backend triển khai endpoint và service token cho ingestion.
5. NiFi gửi Gateway, xử lý lỗi, retry có giới hạn và giữ nguyên
   idempotency key/payload khi retry.
6. Kiểm thử import flow và tích hợp trong môi trường sạch.
7. Đo hiệu năng toàn luồng sau khi các thành phần hoạt động.
8. Tiếp tục cải thiện thuật toán bằng train/validation;
   ghi rõ việc tập test đã được quan sát.

Dataset bổ sung phải được kiểm tra nguồn, giấy phép, nhãn và
khả năng sử dụng trước khi đưa vào kế hoạch đánh giá.

## 7. Kết luận và review

Đã có:
- AI mock và kiểm thử cục bộ.
- Kiểm tra giới hạn ảnh trước giải mã đầy đủ.
- Dataset CHASE_DB1 cùng manifest chia tập.
- Baseline Frangi offline có kết quả tái lập.
- NiFi tiếp nhận, xác thực, quarantine và chuẩn bị multipart.

Chưa nghiệm thu:
- Worker–AI và NiFi–Gateway toàn luồng.
- Service token, phân quyền, idempotency và retry.
- Chất lượng phân vùng mục tiêu.
- NFR-1, NFR-22 và NFR-23 toàn hệ thống.
- Hiệu quả lâm sàng.

M4: người phụ trách kỹ thuật AI/NiFi.
M1 và M3: người duyệt đề xuất; chưa ghi nhận ký duyệt trong tài liệu này.
Chỉ cập nhật xác nhận khi có review thực tế.