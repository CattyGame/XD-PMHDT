# AURA – Hướng dẫn sử dụng giao diện Sprint 4 / Module 5

**SCRUM-357** · Phiên bản hướng dẫn: 08/11/2026 · Áp dụng cho các PR SCRUM-354 / 355 / 356 (chưa merge chung).

> AURA hỗ trợ sàng lọc võng mạc, không thay thế chẩn đoán của bác sĩ. Dữ liệu xét nghiệm/ảnh phải được sử dụng đúng tài khoản được cấp quyền.

## 1. Điều kiện trước khi bắt đầu

- Mở web AURA, đăng nhập bằng Firebase Email hoặc Google. Đảm bảo API Gateway, Analysis, Billing và Notification hoạt động.
- **Bác sĩ** cần vai trò `DOCTOR`, `clinicContextId` (UUID) và các ca được phân công.
- **Nhân sự phòng khám** cần `CLINIC_ADMIN`, `DOCTOR` hoặc `STAFF` gắn với phòng khám trạng thái `ACTIVE`; bệnh nhân phải xuất hiện trong danh sách được cấp quyền.
- Bệnh nhân cá nhân không tự động có quyền truy cập màn hình Doctor Workspace hoặc tải ảnh phòng khám.
- Đăng xuất khỏi tài khoản trước khi chuyển người sử dụng máy; tránh lưu tài liệu chứa thông tin bệnh nhân trên thiết bị dùng chung.

## 2. Không gian bác sĩ – SCRUM-354

1. Vào `/doctor/workspace` và nhập **Clinic Context ID (UUID)** đã được cấp quyền. Bấm **Tải worklist**.
2. Dùng bộ lọc **Ngày tạo ca**, **Mức độ nghi ngờ**, **Trạng thái phân tích** để tìm ca. Lưu ý API hiện có `createdAt`, không có ngày chụp chính xác.
3. Bấm **Mở chi tiết** tại một ca; trang hiển thị mã ca, thông tin trạng thái và ảnh nếu backend cấp quyền xem tệp riêng tư.
4. Dùng công cụ phóng to/thu nhỏ, xoay, kéo rê, lớp phủ/độ trong suốt và so sánh song song khi ảnh mạch máu sẵn sàng. Nếu quyền ảnh hết hạn, bấm **Xin lại quyền xem ảnh**.
5. Chỉ với ca ở trạng thái `COMPLETED`, chọn **Đồng ý với kết quả AI** hoặc **Điều chỉnh mức nguy cơ**. Khi điều chỉnh, nhập **lý do bắt buộc**. Nhập ghi chú/khuyến nghị, kiểm tra rồi bấm **Xác nhận và lưu đánh giá**.
6. Nếu xuất hiện **Xung đột phiên đánh giá (HTTP 409)**, không cố ghi đè. Bấm **Tải phiên bản mới nhất**, đối chiếu thay đổi và thực hiện lại khi còn quyền.

**Mất mạng:** Không nhấn gửi nhiều lần liên tục. Giao diện hiện chưa chứng minh lưu bền vững ghi chú Review sau khi tải lại tab; không coi việc nhập liệu là đã được lưu. Không đưa ghi chú y khoa nhạy cảm vào `localStorage` dạng văn bản thuần vì nguy cơ lộ dữ liệu trên máy dùng chung. Cần giải pháp lưu nháp an toàn được phê duyệt trước khi nghiệm thu tiêu chí này.

## 3. Mua gói và thanh toán Sandbox – SCRUM-355

1. Mở `/billing/packages`; xem gói, giá và số Credit hiện có.
2. Chọn **Mua gói** rồi theo luồng `/billing/pay/{orderId}` để thanh toán Sandbox. Đây là thanh toán giả lập, không chuyển tiền thật.
3. Sau khi thành công, xem trang `/billing/success/{orderId}` và đối chiếu số dư Credit. Nếu kết quả không rõ vì lỗi mạng, kiểm tra lại trạng thái đơn trước khi thử thanh toán, tránh tạo giao dịch trùng.
4. Cho phép thông báo trình duyệt trong phần cài đặt FCM; hệ thống có thể hiển thị toast khi tab mở và thông báo hệ điều hành khi tab ẩn.
5. Khi có thông báo kết quả phân tích thật, nhấn thông báo để mở `/analyses/{analysisId}`. Thông báo kiểm thử Firebase **không đồng nghĩa** Analysis đã hoàn tất.

## 4. Tải ảnh hàng loạt và theo dõi Batch – SCRUM-356

1. Mở `/clinic-upload`, chọn phòng khám đang `ACTIVE` trong danh sách được cấp quyền.
2. Tìm bệnh nhân theo tên/chọn mã UUID hợp lệ cho từng ảnh; chọn mắt trái/phải khi biết.
3. Kéo thả hoặc chọn tối đa **100 ảnh JPG/PNG**, dung lượng mỗi ảnh **không quá 10 MiB**. Ảnh không hợp lệ sẽ hiện lý do tại trình duyệt.
4. Kiểm tra số ảnh hợp lệ và bệnh nhân đã gán rồi bấm **Gửi ... ảnh phòng khám**. Không gửi lại lượt tải mới khi lỗi mạng chưa rõ kết quả: hệ thống hỗ trợ kiểm tra inventory / tiếp tục lượt tải cùng batch.
5. Mở `/batches/{batchId}` để theo dõi tiến độ. Màn hình đọc trạng thái bằng GET và có cơ chế polling/backoff; nếu mất mạng tạm thời, theo dõi trạng thái kết nối lại mà không cần upload lại hoặc thanh toán lại.
6. Khi batch có trạng thái kết thúc, có thể dùng nút tải PDF/CSV qua API có xác thực (hiện chưa xác minh luồng presigned URL trong E2E). Đối chiếu tổng ảnh, số ảnh thành công/lỗi, và dòng báo cáo.
7. Ảnh thu nhỏ trong tab upload chỉ là xem trước tạm; sau khi mở lại tab có thể không còn thumbnail. API hiện chưa cung cấp thời gian xử lý riêng từng ảnh.

## 5. Lỗi thường gặp và xử lý

| Tình huống | Nguyên nhân / hành động đúng |
|---|---|
| Không có phòng khám ở danh sách | Tài khoản chưa có membership `ACTIVE`; đề nghị quản trị viên cấp quyền hợp lệ, không sửa UI để bỏ xác thực. |
| Doctor Workspace hiện 403 | Tài khoản không có vai trò DOCTOR hoặc ca ngoài phạm vi phân công. |
| Không mở được ảnh | Kiểm tra quyền, URL ảnh hết hạn, dùng nút xin lại quyền truy cập. |
| Analysis vẫn `QUEUED` | Có thể Worker chưa chạy; không coi là hoàn tất. |
| Batch báo mất mạng | Chờ polling nối lại hoặc bấm tải lại trạng thái (chỉ GET); không upload lại lượt mới. |
| Thanh toán chưa rõ kết quả | Tra cứu trạng thái đơn, dùng cùng idempotency key theo luồng backend, tránh trừ Credit trùng. |
| Doctor review báo 409 | Tải phiên bản mới; không ghi đè kết luận của người khác. |

## 6. Hỗ trợ và bảo mật

- Chỉ cấp quyền tối thiểu cần dùng; tránh chia sẻ token Firebase, địa chỉ URL ảnh riêng tư, dữ liệu bệnh nhân và mật khẩu trong ảnh chụp/video demo.
- Dùng tài khoản và dữ liệu **test** được cho phép để quay video/screenshot. Che tên bệnh nhân khi làm minh chứng.
- Các tính năng của SCRUM-354/355/356 đang ở PR riêng; đường dẫn và quyền cần kiểm lại sau khi tích hợp/merge.
