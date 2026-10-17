# AURA — Profile, Billing và Notification Design v0.1

## 1. Thông tin tài liệu

- Dự án: AURA — SP26SE025.
- Task: SCRUM-57.
- Người biên soạn: Nguyễn Trương Hậu, tiếp nhận công việc M3.
- Ngày cập nhật: 2026-10-11.
- Trạng thái: Hoàn thành bản thiết kế v0.1.
- Phạm vi: thiết kế nghiệp vụ, dữ liệu, quyền và luồng xử lý.
- Chưa xác nhận triển khai đầy đủ hoặc kiểm thử tích hợp thực tế.
- Các lựa chọn chưa chốt được liệt kê để xử lý khi triển khai.

Tên entity, field và trạng thái bên dưới là đề xuất thiết kế.
Endpoint và schema chính thức phải được đồng bộ với contracts.
Không tự thay đổi các contract Analysis hiện có theo tài liệu này.

## 2. Yêu cầu liên quan

| Module | Yêu cầu |
| --- | --- |
| Profile và Identity | FR-1, FR-8, FR-13, FR-22, FR-23, FR-31, FR-32 |
| Billing | FR-11, FR-12, FR-27, FR-28, FR-34, FR-35 |
| Notification | FR-9, FR-29, FR-39 |
| Yêu cầu chung | NFR-5, NFR-9, NFR-12, NFR-17, NFR-18, NFR-23 |

## 3. Ranh giới trách nhiệm

| Thành phần | Trách nhiệm |
| --- | --- |
| Identity | Xác thực, liên kết danh tính bên ngoài, trạng thái tài khoản và role |
| Profile | Thông tin cá nhân và hồ sơ bệnh nhân theo quyền |
| Clinic | Tổ chức, membership và Doctor assignment |
| Billing | Gói dịch vụ, đơn hàng, thanh toán và credit ledger |
| Analysis | Tiếp nhận phân tích, trạng thái job và kết quả |
| Notification | Bản ghi thông báo, người nhận và trạng thái gửi |
| Gateway | Chuyển request và kiểm tra theo chính sách authentication |
| Frontend | Hiển thị, nhập dữ liệu và xử lý trạng thái UI |

Gateway authentication không thay thế authorization ở service.
Backend phải kiểm tra quyền tài nguyên, tenant và assignment.

## 4. Profile và Identity

### 4.1. Mô hình dữ liệu đề xuất

| Entity | Mục đích |
| --- | --- |
| UserAccount | Định danh nội bộ, trạng thái tài khoản và thông tin quản trị |
| ExternalIdentity | Liên kết account với provider và subject/UID bên ngoài |
| UserProfile | Thông tin cá nhân có thể cập nhật theo quyền |
| PatientProfile | Hồ sơ bệnh nhân phục vụ nghiệp vụ sàng lọc |
| ClinicMembership | Liên kết account với Clinic và quyền trong tổ chức |
| DoctorAssignment | Liên kết Doctor với bệnh nhân trong phạm vi Clinic |

Ràng buộc:

- UserAccount ID và PatientProfile ID là các định danh khác nhau.
- Không giả định mọi bệnh nhân đều có tài khoản đăng nhập.
- ExternalIdentity cần uniqueness theo provider và subject/UID.
- Email không được dùng làm khóa liên kết danh tính duy nhất.
- Không lưu mật khẩu của Firebase hoặc provider trong database ứng dụng.
- Membership không tự tạo Doctor assignment.
- Trường y tế được tách khỏi thông tin tài khoản thông thường.
- Sửa hoặc hủy quan hệ không tự xóa kết quả phân tích lịch sử.

### 4.2. Luồng xác thực và liên kết tài khoản

1. Người dùng đăng nhập qua provider đã chọn.
2. Frontend nhận credential/token theo cơ chế của provider.
3. Backend xác minh chữ ký, issuer, audience và thời hạn phù hợp.
4. Backend tra ExternalIdentity.
5. Nếu chưa liên kết, thực hiện quy trình tạo hoặc liên kết đã thống nhất.
6. Kiểm tra trạng thái UserAccount.
7. Tải role, membership và quyền từ nguồn dữ liệu có thẩm quyền.
8. Cho phép truy cập tài nguyên theo authorization.

Không tin UID, email, role hoặc clinicId do client tự khai báo.
Không tự liên kết hai tài khoản chỉ vì có cùng email.

Cần chốt cơ chế xác minh thu hồi token, liên kết tài khoản,
role lưu tại ứng dụng hay claims và thời điểm quyền mới có hiệu lực.

### 4.3. Xem và cập nhật Profile

1. Người dùng yêu cầu xem hồ sơ được phép.
2. Backend xác định account từ danh tính đã xác thực.
3. Kiểm tra ownership, tenant hoặc Doctor assignment.
4. Chỉ trả các trường được phép.
5. Khi cập nhật, kiểm tra dữ liệu và phiên bản hồ sơ.
6. Lưu thay đổi và audit phù hợp.
7. Trả hồ sơ cập nhật.

Ràng buộc:

- Client không được sửa role hoặc trạng thái tài khoản qua API Profile.
- Trường định danh do provider quản lý cần quy trình riêng.
- Không cho cập nhật hồ sơ bệnh nhân ngoài phạm vi được cấp.
- Nếu có cập nhật đồng thời, xử lý xung đột theo contract.
- Không ghi thông tin y tế vào log request thông thường.

## 5. Billing

### 5.1. Mô hình dữ liệu đề xuất

| Entity | Mục đích |
| --- | --- |
| ServicePackage | Danh mục gói và quyền lợi |
| PackagePriceVersion | Giá và thời điểm có hiệu lực |
| Order | Yêu cầu mua, lưu snapshot giá và quyền lợi |
| PaymentTransaction | Giao dịch và tham chiếu từ payment provider |
| Entitlement | Quyền sử dụng được cấp sau giao dịch hợp lệ |
| CreditLedgerEntry | Biến động credit có nguồn gốc và định danh |
| CreditReservation | Giữ credit cho một yêu cầu phân tích nếu dùng cơ chế reservation |

Billing owner cần phân biệt account cá nhân và Clinic.
Không dùng một ID mơ hồ cho cả hai loại chủ thể.

Các bảng và tên trạng thái cần được thống nhất khi tạo migration.

### 5.2. Nguồn dữ liệu credit

- Ledger là nguồn có thẩm quyền cho biến động credit.
- Redis chỉ dùng cache; không là nguồn số dư duy nhất.
- Không cho frontend tự gửi số dư mới.
- Mỗi biến động gắn với nghiệp vụ và định danh chống xử lý trùng.
- Tách credit đã cấp, đã dùng, đang giữ và hết hạn nếu áp dụng.
- Số dư cần có quy tắc tính rõ ràng theo chính sách sản phẩm.
- Có cơ chế đối soát ledger với reservation và yêu cầu Analysis.

Cần chốt thời hạn credit, thứ tự sử dụng và chính sách hoàn credit.

### 5.3. Luồng mua gói

1. Backend trả danh mục gói đang bán.
2. Người dùng chọn gói cho account hoặc Clinic được phép.
3. Backend tạo Order từ giá và quyền lợi hiện hành.
4. Tạo yêu cầu thanh toán với provider đã chọn.
5. Người dùng thực hiện thanh toán.
6. Backend nhận callback hoặc kiểm tra trạng thái đáng tin cậy.
7. Xác minh nguồn gửi, giao dịch, số tiền, tiền tệ và Order.
8. Xử lý idempotent.
9. Cập nhật giao dịch và cấp quyền lợi/credit trong giao dịch dữ liệu phù hợp.
10. Ghi audit và tạo sự kiện cho thông báo nếu cần.

Ràng buộc:

- Redirect hoặc thông báo từ frontend không chứng minh đã thanh toán.
- Callback trùng không được cấp credit lần thứ hai.
- Xử lý callback đến sai thứ tự theo trạng thái và dữ liệu provider.
- Giá trên Order giữ nguyên khi danh mục thay đổi.
- Không log bí mật hoặc toàn bộ payload thanh toán nhạy cảm.
- Lỗi sau thanh toán cần có đối soát và khả năng xử lý lại an toàn.

### 5.4. Tích hợp credit với Analysis

Luồng thiết kế:

1. Xác thực người tạo và billing owner.
2. Kiểm tra ảnh, request và idempotency theo contract Analysis.
3. Giữ hoặc trừ credit theo chính sách được chốt.
4. Lưu yêu cầu phân tích và dữ liệu cần thiết trước khi xác nhận tiếp nhận.
5. Tạo outbox/event theo thiết kế độ tin cậy.
6. Worker thực hiện phân tích.
7. Hoàn tất reservation hoặc xử lý hoàn credit theo kết quả và chính sách.
8. Đối soát khi lỗi, retry hoặc gián đoạn dịch vụ.

Ràng buộc:

- Không đủ credit trả lỗi theo contract hiện có.
- Retry không trừ credit thêm cho cùng nghiệp vụ.
- Hai request đồng thời không được sử dụng cùng phần credit vượt số dư.
- Nếu reservation và Analysis nằm ở hai service,
  phải thiết kế bù trừ và đối soát; không giả định có transaction chung.
- Failed và quality-reject không tự đồng nghĩa được hoàn credit.
  Chính sách này cần được quyết định rõ.
- Lỗi phân tích không làm mất ảnh đã tiếp nhận hợp lệ.

### 5.5. Xem lịch sử và số dư

- User chỉ xem lịch sử của mình.
- Clinic operator chỉ xem billing của Clinic được phép.
- Doctor không mặc nhiên có quyền quản lý thanh toán Clinic.
- Admin cần quyền billing riêng.
- Lịch sử có pagination và bộ lọc theo contract.
- Hiển thị phân biệt pending, thành công, thất bại và hoàn tiền.
- Số dư cache cần có chiến lược invalidation sau thay đổi.

### 5.6. Chính sách Billing trong Sprint 2 — SCRUM-305

**Trạng thái:** Draft for review
**Phạm vi:** Credit, reservation, capture/release, Billing Owner, price snapshot, idempotency và payment provider.

#### 5.6.1. Đơn vị credit và điều kiện tính phí

- Một ảnh FUNDUS được xử lý hợp lệ cho một lượt phân tích tiêu thụ 1 credit.
- Credit sử dụng đơn vị nguyên.
- Yêu cầu ảnh trùng lặp không được reserve hoặc capture thêm credit.
- Sprint 2 chỉ hỗ trợ FUNDUS qua public API. Ảnh OCT hoặc định dạng không hợp lệ phải bị từ chối trước bước reserve.
- Nếu số credit khả dụng không đủ, trả `402 ERR_INSUFFICIENT_CREDIT`.

#### 5.6.2. Billing Owner

- Billing Owner của tenant cá nhân có loại `ACCOUNT`.
- Billing Owner của yêu cầu thuộc phòng khám có loại `CLINIC` hợp lệ.
- Billing Owner được xác định từ Profile và phạm vi tenant đã xác thực; client không được tự chọn ví thuộc chủ thể khác.
- Các thao tác Billing phải kiểm tra tenant, actor và Billing Owner tương ứng.

#### 5.6.3. Reservation lifecycle

**Reserve**

- Billing phải reserve credit trước khi Analysis trả `202 Accepted`.
- Thông tin reserve tối thiểu theo contract gồm `analysisId`, `tenantId`, `actorAccountId`, `patientId`, `billingOwner`, `units` và `generation`.
- Việc reserve phải chống xử lý trùng và không được làm số credit bị giữ vượt quá số dư khả dụng.

**Capture**

- Chỉ capture khi Analysis hoàn tất hợp lệ với trạng thái `SUCCESS` hoặc `WARNING`.
- Capture cùng một thao tác nhiều lần không được trừ credit nhiều lần.

**Release**

- Release khi Analysis có trạng thái cuối cùng `FAILED` hoặc `LOW_QUALITY` mà không có kết quả phân tích.
- Nếu lỗi còn có thể retry, giữ reservation trong thời gian luồng xử lý vẫn tiếp tục retry.
- Nếu người dùng retry thủ công sau khi reservation đã release, yêu cầu mới phải reserve lại với `generation` mới.

**Trạng thái kết thúc**

- Capture và release là hai trạng thái kết thúc loại trừ lẫn nhau.
- Thao tác lặp hợp lệ phải được xử lý idempotent.
- Chuyển trạng thái mâu thuẫn phải trả `409 ERR_RESERVATION_STATE_CONFLICT`.

#### 5.6.4. API nội bộ theo contract

Các endpoint nội bộ được quy định trong contract chung:

- `POST /internal/v1/billing/reservations`
- `GET /internal/v1/billing/reservations/{reservationId}`
- `POST /internal/v1/billing/reservations/{reservationId}/capture`
- `POST /internal/v1/billing/reservations/{reservationId}/release`

Quy tắc idempotency:

- Cùng key và cùng payload: trả lại kết quả của thao tác trước.
- Cùng key nhưng payload khác: trả `409 Conflict`.
- Không tự thay đổi contract Analysis hiện có theo thiết kế Billing này; mọi khác biệt phải được thống nhất giữa các bên phụ trách.

#### 5.6.5. Ledger và price snapshot

- Ledger là nguồn dữ liệu có thẩm quyền để xác định các biến động credit.
- Redis chỉ là cache catalog, không phải nguồn chuẩn của số dư.
- Giá gói và phiên bản giá liên quan phải được snapshot cho giao dịch lịch sử; cập nhật catalog không được làm thay đổi giá đã snapshot.
- Cần kiểm tra schema và code hiện tại trước khi xác nhận các quy tắc này đã được triển khai.

#### 5.6.6. Hoàn credit và payment provider

- Release reservation chưa capture là giải phóng credit đã giữ, không đồng nghĩa với hoàn tiền thanh toán.
- Chính sách hoàn credit sau capture cần được người có thẩm quyền xác nhận trước khi triển khai.
- Sprint 2 chưa tích hợp payment provider thật; không được giả lập một giao dịch thanh toán thành công như thể provider đã xác nhận.
- AI mock chỉ được sử dụng ví/ledger `TEST` được cô lập. Event có `isMock=true` không được capture credit từ ví `LIVE`.

#### 5.6.7. Kiểm tra khi review

- [ ] Reserve trước `202 Accepted`.
- [ ] Capture đúng `SUCCESS`/`WARNING` hợp lệ.
- [ ] Release đúng `FAILED` cuối cùng/`LOW_QUALITY` không có kết quả.
- [ ] Retry tạm thời và retry thủ công được xử lý khác nhau.
- [ ] Yêu cầu trùng không phát sinh charge trùng.
- [ ] Billing Owner và tenant được kiểm tra.
- [ ] Idempotency và trạng thái kết thúc loại trừ lẫn nhau được kiểm tra.
- [ ] Ledger và price snapshot được đối chiếu với triển khai.
- [ ] TEST và LIVE được cô lập.
- [ ] Chính sách hoàn credit sau capture đã được xác nhận hoặc được ghi rõ là còn chờ quyết định.

Các mục chưa có bằng chứng kiểm thử phải giữ trạng thái chưa kiểm tra. Việc hoàn thành tài liệu không đồng nghĩa với việc đã triển khai hoặc nghiệm thu các API.

## 6. Notification

### 6.1. Mô hình dữ liệu đề xuất

| Entity | Mục đích |
| --- | --- |
| Notification | Nội dung trong ứng dụng và tham chiếu nghiệp vụ |
| NotificationRecipient | Người nhận, thời điểm đọc và quyền liên quan |
| NotificationDelivery | Kênh, lần thử và kết quả gửi |
| DeviceRegistration | Token thiết bị, account sở hữu và trạng thái |
| NotificationTemplateVersion | Phiên bản mẫu và các biến được phép |

Không dùng FCM token làm định danh người dùng.
Một account có thể có nhiều thiết bị.
Một thiết bị đăng xuất hoặc đổi account phải cập nhật quan hệ token.

### 6.2. Luồng thông báo kết quả hoàn tất

1. Analysis phát sự kiện kết quả hợp lệ.
2. Consumer kiểm tra schema và định danh sự kiện.
3. Chống xử lý trùng.
4. Xác định người nhận theo ownership, membership và assignment.
5. Tạo bản ghi thông báo trong ứng dụng.
6. Nếu được phép, lên lịch gửi push nội dung chung.
7. Ghi trạng thái từng lần gửi và xử lý retry.
8. Người dùng mở ứng dụng để đọc nội dung được phép.

Ràng buộc:

- Xử lý event lặp không tạo thông báo nghiệp vụ trùng.
- Push không chứa tên bệnh nhân, ảnh hoặc mức nguy cơ cá nhân.
- Deep link không thay thế authorization.
- Quyền xem tài nguyên phải được kiểm tra lại khi mở.
- Gửi push thành công không chứng minh người nhận đã đọc.
- Failed không được thông báo như phân tích đã thành công.
- Dữ liệu mock không được phát cảnh báo lâm sàng thật.

### 6.3. Đăng ký thiết bị và quyền trình duyệt

1. Chỉ yêu cầu quyền thông báo sau thao tác chủ động của người dùng.
2. Kiểm tra môi trường HTTPS/localhost và hỗ trợ trình duyệt.
3. Nếu bị chặn, hiển thị hướng dẫn sửa quyền.
4. Nếu được cấp quyền và có cấu hình, lấy token qua SDK.
5. Gửi token đến backend qua API có authentication.
6. Backend liên kết thiết bị với account đã xác thực.
7. Cập nhật token khi thay đổi và loại bỏ token không còn hợp lệ.

Ràng buộc:

- Không tự xin quyền khi mở trang.
- Không ghi token ra console hoặc log thông thường.
- Không truyền token trên query string.
- Không coi đăng ký service worker là đã kết nối FCM thành công.
- Không coi permission granted là đã lấy token hoặc nhận push.
- API demo MSW không phải API đăng ký thiết bị chính thức.

### 6.4. Đăng xuất và thu hồi token

- Hủy hoặc vô hiệu hóa liên kết thiết bị với account tại backend.
- Xử lý token tại SDK theo chính sách của ứng dụng.
- Không tiếp tục gửi thông báo của account cũ khi đổi account.
- Xử lý trường hợp mất mạng bằng trạng thái và cơ chế đối soát.
- Khi provider báo token không hợp lệ, vô hiệu hóa registration.
- Không tuyên bố thu hồi hoàn tất nếu mới xóa token ở client.

### 6.5. Thông báo nguy cơ cao và xu hướng bất thường

- Chỉ tạo khi có nguồn dữ liệu và quy tắc được chấp thuận.
- Phân biệt kết quả AI và kết quả Doctor đã xác nhận.
- Lưu phiên bản quy tắc hoặc cấu hình dùng để tạo cảnh báo.
- Xác định đúng Clinic và người nhận có quyền.
- Không sử dụng mức nguy cơ giả lập để phát cảnh báo thật.
- Cần quy định dữ liệu tối thiểu trước khi suy luận xu hướng.
- Không suy ra nguy cơ bệnh từ chỉ số benchmark phân đoạn mạch máu.

## 7. Quyền truy cập tổng hợp

| Tài nguyên | Quy tắc |
| --- | --- |
| Profile cá nhân | Account sở hữu hoặc actor có quyền cụ thể |
| Hồ sơ bệnh nhân | Ownership/tenant/assignment theo nghiệp vụ |
| Membership | Người quản lý Clinic được cấp quyền |
| Order và credit cá nhân | Billing owner hoặc quyền billing hợp lệ |
| Order và credit Clinic | Quyền billing trong Clinic tương ứng |
| Thông báo trong ứng dụng | Người nhận được xác thực |
| DeviceRegistration | Account sở hữu và quy trình đăng ký thiết bị |
| Gói dịch vụ | Đọc danh mục theo chính sách; sửa cần quyền Admin billing |
| Notification template | Quyền quản trị thông báo |

Mọi quy tắc được kiểm tra ở backend.
Không sử dụng việc ẩn nút trên frontend thay cho kiểm tra quyền.

## 8. Error handling và độ tin cậy

- Đồng bộ cấu trúc lỗi với contract tương ứng.
- Có correlationId cho truy vết khi thiết kế yêu cầu.
- Phân biệt lỗi validation, authentication, authorization,
  xung đột, thiếu credit và lỗi dịch vụ.
- Timeout chưa xác nhận kết quả của thao tác ghi.
- Retry thao tác ghi cần idempotency hoặc truy vấn đối soát.
- Consumer hỗ trợ event lặp và lỗi tạm thời.
- Có giới hạn retry và nơi theo dõi lỗi không xử lý được.
- Không trả bí mật, token hoặc thông tin y tế trong lỗi kỹ thuật.
- Giữ dữ liệu nghiệp vụ để có thể phục hồi và đối soát.

## 9. Test scenario dự kiến

Các scenario sau là thiết kế kiểm thử, chưa phải bằng chứng chạy thật.

| ID | Scenario | Kết quả mong đợi |
| --- | --- | --- |
| TC-PBN01 | User sửa role qua Profile | Backend từ chối; role giữ nguyên |
| TC-PBN02 | Doctor đọc bệnh nhân chưa được phân công | Không trả dữ liệu ngoài quyền |
| TC-PBN03 | Hai account có email giống nhau | Không tự liên kết danh tính |
| TC-PBN04 | Hai cập nhật Profile đồng thời | Xử lý xung đột theo contract |
| TC-PBN05 | Callback thanh toán gửi hai lần | Chỉ cấp credit một lần |
| TC-PBN06 | Callback sai chữ ký hoặc số tiền | Không ghi nhận thanh toán thành công |
| TC-PBN07 | Retry cùng yêu cầu Analysis | Không trừ credit thêm |
| TC-PBN08 | Hai request tranh cùng số dư | Không dùng credit vượt số dư |
| TC-PBN09 | Analysis failed | Giữ ảnh và áp dụng đúng chính sách credit |
| TC-PBN10 | Event completed lặp | Không tạo thông báo nghiệp vụ trùng |
| TC-PBN11 | Người ngoài mở deep link | Backend từ chối truy cập |
| TC-PBN12 | Người dùng từ chối quyền thông báo | Không lấy hoặc đăng ký token |
| TC-PBN13 | Đổi account trên cùng thiết bị | Không nhận thông báo account cũ |
| TC-PBN14 | Provider báo token hết hiệu lực | Vô hiệu hóa registration tương ứng |
| TC-PBN15 | Push kết quả hoàn tất | Chỉ có nội dung chung, không có dữ liệu sức khỏe |
| TC-PBN16 | Redis Billing không khả dụng | Đọc nguồn có thẩm quyền theo thiết kế |

Test Plan sẽ bổ sung dữ liệu, bước chạy, môi trường và actual result.

## 10. Hiện trạng và khoảng cách triển khai

| Thành phần | Bằng chứng đã biết | Phần cần triển khai/kiểm chứng |
| --- | --- | --- |
| Identity/Profile | Yêu cầu và thiết kế nền tảng | Authentication thật, liên kết account, API Profile và kiểm tra quyền |
| Billing | Catalog PostgreSQL/Redis PoC | Order, payment, ledger, reservation và đối soát |
| Analysis frontend | MSW và test scenario local | API thật và luồng dữ liệu bền vững |
| Notification PoC | Device API có Firebase auth, token mã hóa, lifecycle frontend, PostgreSQL checks và push local foreground/background/click | Analysis event-to-notification, inbox/templates, token rotation thật, production và các kiểm chứng bổ sung |
| AI | Mock runtime và benchmark offline | Tích hợp thuật toán thực, truy vết và kiểm chứng phù hợp |

Bằng chứng frontend local đã ghi nhận 46 test PASS, lint/build PASS
và npm audit không phát hiện lỗ hổng tại thời điểm kiểm tra.

Các kết quả này không xác nhận authentication, thanh toán
hoặc FCM production đã hoàn thành.

## 11. Các quyết định cần xử lý khi triển khai

1. Provider và cơ chế xác thực backend.
2. Cách tạo/link account và xử lý xung đột danh tính.
3. Schema Profile và trường thông tin y tế cần lưu.
4. Chính sách thu hồi role, membership và phiên đăng nhập.
5. Payment provider, loại tiền và callback contract.
6. Thời hạn credit và chính sách hoàn credit.
7. Reservation, capture, release và đối soát giữa service.
8. Review contract device API đã có và tích hợp vào luồng nghiệp vụ.
9. Chốt chính sách foreground/background cho production; PoC local đã kiểm chứng.
10. Kiểm chứng token rotation thật và chính sách đăng xuất mất mạng; frontend hiện giữ phiên khi thao tác hủy/xóa token lỗi.
11. Thời gian lưu thông báo, delivery và audit.
12. Quy tắc cảnh báo nguy cơ và người nhận hợp lệ.

## 12. Phạm vi bàn giao

Tài liệu bàn giao:

- Ranh giới Profile, Identity, Billing và Notification.
- Mô hình dữ liệu đề xuất và các ràng buộc.
- Luồng nghiệp vụ chính.
- Quyền truy cập và error handling.
- 16 test scenario.
- Khoảng cách giữa PoC hiện tại và chức năng thật.
- Danh sách quyết định cho bước triển khai.

Hoàn thành tài liệu không đồng nghĩa đã triển khai toàn bộ module.

## 13. Nguồn tham chiếu

- Đề tài AURA — SP26SE025.
- docs/requirements/srs_v0.1.md.
- docs/requirements/requirements_traceability_matrix_v0.1.md.
- docs/requirements/clinic_campaign_use_cases_v0.1.md.
- docs/requirements/admin_configuration_use_cases_v0.1.md.
- contracts/analysis-openapi.yaml.
- contracts/analysis-events.schema.json.
- contracts/analysis-job.schema.json.
- contracts/BASELINE-W1-v0.2.md.
- src/web/aura-web/README.md.
- Permissions matrix, ADR và ERD hiện có.
- Task SCRUM-57 trên Jira.

## 14. Lịch sử thay đổi

| Phiên bản | Ngày | Nội dung |
| --- | --- | --- |
| 0.1 | 2026-10-11 | Hoàn thành thiết kế Profile, Billing và Notification |
| 0.2 | 2026-10-17 | Bổ sung chính sách Billing Sprint 2 theo SCRUM-305; chờ review và xác nhận contract |

## Cập nhật Notification sau đợt sửa M2–M5

Ngày thực thi: 2026-10-11.
HEAD b2b3dde cùng working tree có các thay đổi chưa commit;
không coi HEAD là phiên bản chứa toàn bộ bản sửa.

- Frontend lần chạy cuối: 81/81 tests PASS trên 7 file.
- NotificationSettings: 30; NotificationPushTestPanel: 9;
  tokenLifecycle: 6; App: 7; handlers: 10;
  FcmDemoPanel: 7; AnalysisDemoPanel: 12.
- Lint: 0 warnings/errors; TypeScript và Vite build PASS.
- Notification API Debug build PASS.
- CryptoChecks: 12/12 PASS.
- PostgreSQL checks PASS, gồm xóa có điều kiện và giữ token mới.
- Browser xác nhận đăng ký/đồng bộ và foreground receipt.
- Người thao tác xác nhận background hiển thị và click focus tab AURA đang mở.
- Worker local và worker Vite phục vụ khớp SHA256.

Một lần frontend có 80/81 PASS; test lỗi chạy riêng PASS,
lần chạy toàn bộ tiếp theo 81/81 PASS. Chưa xác định nguyên nhân.

Đăng ký lần đầu cần thao tác chủ động của người dùng.
Sau đăng ký thành công trong phiên, focus/visible đồng bộ token.
Không coi các kết quả trên là nghiệm thu Analysis event-to-notification,
production TLS/RBAC, token rotation thật hoặc toàn bộ FR/NFR.

- [Notification contract](../api/notification_service_contract_v0.1.md).
- [Bằng chứng kiểm thử](../testing/m2_m5_notification_verification_2026-10-11.md).
