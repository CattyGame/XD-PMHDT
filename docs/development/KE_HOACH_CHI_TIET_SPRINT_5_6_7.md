# AURA — KẾ HOẠCH HÀNH ĐỘNG VỀ ĐÍCH TIẾP NỐI 4 SPRINT JIRA (SPRINT 5, 6, 7 & 8)



> **Mã đề tài**: `SP26SE025` · **Repository**: `XD-PMHDT` · **Jira Project**: `SCRUM`

> **Mốc đối chiếu kỹ thuật**: `develop@a4695763f9609358032ec814631929f05485c130` · **AURA CI**: SUCCESS 100%.

> **Tính kế thừa**: Tiếp nối chính xác sau 4 Sprint trên Jira (kết thúc tại **`SCRUM-361`**).

> **Dải task các Sprint tiếp theo**: **`SCRUM-362` đến `SCRUM-433`** (Tổng cộng 72 task chuẩn hóa).

> **Cập nhật nhân sự**: **M3 đã rời nhóm (out nhóm)**. Toàn bộ các công việc của M3 trong Sprint 5, 6, 7, 8 được tái phân bổ tập trung cho 5 thành viên còn lại (**M1, M2, M4, M5, M6**).

> **Nguồn hiệu chỉnh kỹ thuật**: Tích hợp toàn diện các nhận xét, cảnh báo ranh giới y tế và chuẩn hóa kiến trúc từ tài liệu **AURA Master Review & Roadmap (01/11/2026)**.



---



## 1. Bản Đồ Kế Thừa Jira & Phân Bổ Nhân Sự



```

   SPRINT 1 (Closed)          SPRINT 2 (Closed)          SPRINT 3 (Done)            SPRINT 4 (Active MVP)

  SCRUM-261..SCRUM-274      SCRUM-275..SCRUM-316       SCRUM-317..SCRUM-337       SCRUM-338..SCRUM-361

 ─────────────────────►    ─────────────────────►    ─────────────────────►    ─────────────────────────┐

                                                                                                        │

 ┌──────────────────────────────────────────────────────────────────────────────────────────────────────┘

 │

 ▼ DẢI TASK KẾ HOẠCH MỚI TIẾP NỐI TRỰC TIẾP (5 THÀNH VIÊN: M1, M2, M4, M5, M6):

 ├─► SPRINT 5 (02/11 – 08/11/2026): SCRUM-362 đến SCRUM-379 (Clinic, Batch ≥ 100, DR & AVR Training)

 ├─► SPRINT 6 (09/11 – 15/11/2026): SCRUM-380 đến SCRUM-397 (Admin Portal, Chat, Cảnh báo & Camera) [CODE FREEZE]

 ├─► SPRINT 7 (16/11 – 22/11/2026): SCRUM-398 đến SCRUM-415 (Hiệu năng, Bảo mật, Đo đạc 23 NFRs & E2E Test)

 └─► SPRINT 8 (23/11 – 29/11/2026): SCRUM-416 đến SCRUM-433 (Release Candidate, Hồ sơ Đồ án & Demo Bảo vệ)

```



### Phân Bổ Nhân Sự Chính Thức (5 Thành Viên):

* **M1 — Nguyễn Trương Hậu** (**Lead Architect / DevOps / Platform, Ingestion, Billing & Firebase Lead**):

  * *Apache NiFi*: Ingestion pipeline, chống trùng SHA-256 (Dedup), cách ly ảnh hỏng (Quarantine), Bounded Retry, Camera Service Principal.

  * *Firebase Platform*: Firebase Admin SDK, Firebase Auth token validation, Firebase Cloud Messaging (FCM) push service, dọn dẹp token rác.

  * *Billing & Sổ cái*: Mock Payment Gateway, HMAC SHA-256 signatures, Credit Ledger, Clinic Wallets, Stale Reservation Reconciler.

  * *Hạ tầng nòng cốt*: API Gateway (YARP), Cụm Prometheus & Grafana, Cụm Cache Redis, Docker Compose, Backup/Restore DR, Centralized Logging.

* **M2 — Võ Hữu Duy** (**Backend Lead / Worker, Pipeline & Consultation Backend**):

  `AURA.Analysis.Api`, `Aura.Worker.Analysis`, Batch Engine ($\ge 100$ ảnh), Doctor Review Workspace API (Optimistic Locking), Báo cáo Y khoa PDF chuẩn QuestPDF / CSV, Phân hệ Backend Chat thời gian thực (SignalR WebSocket Hub).

* **M4 — Đào Duy Quân** (**AI Core Lead / Computer Vision & Clinical Deep Learning Specialist**):

  `ai-core` (FastAPI), Thuật toán lọc mạch máu Frangi 2D đa tỷ lệ tối ưu, Mô hình Phân loại Bệnh Võng Mạc Đái Tháo Đường (DR Screening từ HRF), Pipeline Phân đoạn Động/Tĩnh mạch & Đo chỉ số $AVR$ (từ INSPIRE-AVR), Đóng gói Model Card v2.0 và ONNX Runtime export.

* **M5 — Lâm Trần Nguyễn Hoàng** (**Frontend Lead**):

  `src/web/aura-web` (React 18 / Vite / Tailwind), Doctor Review Workspace UI (Side-by-side zoom/pan, thanh trượt Opacity, hiển thị gợi ý ICDR và lớp phủ Động/Tĩnh mạch A/V Map), Bulk Upload UI $\ge 100$, Giao diện Chat tư vấn SignalR Client, Clinic Admin Portal.

* **M6 — Dung Hoang** (**Security, Auth, Identity & QA Lead**):

  `AURA.Identity.Api`, `AURA.Profile.Api`, Phân quyền Đa Tenant Clinic Lifecycle, Phân công Bác sĩ (Doctor Assignment), Phân hệ Chiến dịch Tầm soát (Campaign API/UI), Điều phối Kiểm thử Chất lượng (QA Lead), Quản lý Ma trận RTM và Báo cáo Đồ án Toàn văn.



---



## 2. Nguyên Tắc Kỹ Thuật & Cổng Nghiệm Thu Cốt Lõi (Delivery Gates)



Để đảm bảo dự án phát triển vững chắc, có căn cứ khoa học và vượt qua mọi đợt sát hạch kỹ thuật, toàn bộ 5 thành viên phải tuân thủ nghiêm ngặt 5 nguyên tắc sau:



### Nguyên Tắc 1: Cổng Nghiệm Thu Tuyến Tính (Gate G0 $\rightarrow$ G5)

* **Gate G0 — Nền tảng sạch (ĐÃ ĐẠT tại `a469576`)**: Toàn bộ unit tests (.NET, React, Python), dev checks và Docker/Gateway checks đều PASS trên CI.

* **Gate G1 — Thông luồng 1 ảnh thật (E2E Single Image Baseline)**: **BẮT BUỘC hoàn thành trước khi mở rộng Batch $\ge 100$ hay scale Workers**:

  $$\text{Web / Camera NiFi} \rightarrow \text{Gateway} \rightarrow \text{Analysis API} \rightarrow \text{RabbitMQ} \rightarrow \text{Worker} \rightarrow \text{AI Core} \rightarrow \text{Storage/DB} \rightarrow \text{Billing/FCM} \rightarrow \text{Web UI}$$

* **Gate G2 — Chuẩn hóa Worker trước khi Scale**: Sửa dứt điểm schema, Service Token Provider, phân quyền (`ai:analyze`, `profile:resolve`), lease/fencing và cơ chế retry có chặn trên của Worker trước khi chạy 4 workers song song.

* **Gate G3 — Phê duyệt Hợp đồng API (OpenAPI v1.3 & Canonical Contracts)**: Mọi endpoint mới (Batch, Campaign, Doctor Review, SignalR) phải cập nhật OpenAPI contract trước khi viết code nghiệp vụ.

* **Gate G4 — Code Freeze (Cuối Sprint 6)**: Đóng băng toàn bộ việc phát triển tính năng mới. Toàn bộ Sprint 7 và Sprint 8 chỉ tập trung: Sửa lỗi, Đo đạc 23 NFRs, Kiểm toán bảo mật, Diễn tập DR, và Đóng gói hồ sơ nghiệm thu.

* **Gate G5 — Nghiệm thu Toàn văn & Release Candidate (Cuối Sprint 8)**: Đóng gói Docker RC `v1.0.0-release` có hash digest, Model Card v2.0 có benchmark tái lập, và báo cáo đồ án toàn văn.



### Nguyên Tắc 2: Ranh Giới Y Tế & Đạo Đức Lâm Sàng cho AI Core

1. **Hỗ trợ Quyết định (Clinical Decision Support - CDS)**: Hệ thống AURA là công cụ sàng lọc hỗ trợ bác sĩ, **tuyệt đối không đưa ra kết luận chẩn đoán khẳng định** thay cho bác sĩ chuyên khoa mắt.

2. **Cơ chế Từ chối An toàn (Abstain / `RISK_UNAVAILABLE`)**: Khi ảnh đầu vào bị mờ, lóa, tối, sai góc chụp, hoặc vi mạch không đủ rõ để xác định tỷ số $AVR$, AI Core **bắt buộc phải trả về trạng thái `RISK_UNAVAILABLE` hoặc `LOW_QUALITY`**, kèm lý do cụ thể. **Nghiêm cấm việc ép giá trị $AVR$ giả tạo luôn nằm trong khoảng $[0.40, 0.85]$**.

3. **Phân biệt Rõ Bài toán DR**: Phân biệt bài toán Sàng lọc Nhị phân (Binary DR Screening: Có dấu hiệu bệnh vs Bình thường) với Phân loại Đa cấp độ (ICDR Grading: 0–4). Khi dataset chỉ có nhãn nhị phân, không tự suy diễn cấp độ ICDR chi tiết mà chỉ cung cấp điểm số xác suất và Grad-CAM trực quan.

4. **Bản quyền Dữ liệu Nghiên cứu**: Tôn trọng nghiêm ngặt bản quyền phi thương mại của FAU (tập `HRF`) và University of Iowa (tập `INSPIRE-AVR`). Chỉ lưu trữ nội bộ phục vụ đào tạo và nghiên cứu học thuật, không đóng gói phân phối lại dữ liệu gốc trong bản release thương mại.



### Nguyên Tắc 3: Ranh Giới Quyền Hạn Dịch Vụ (Service Boundary & Least Privilege)

* **Worker Boundary**: Worker chỉ đóng vai trò xử lý tính toán nền. Worker chỉ được cấp quyền `ai:analyze` và `profile:resolve`. **Worker KHÔNG được cấp quyền trừ tiền (`billing:reserve/capture/refund`)**.

* **Billing Settlement**: Analysis API là cơ quan có thẩm quyền ghi nhận trạng thái tiếp nhận và hoàn tất ca khám; Billing API tự đối soát và thực hiện capture/release/refund credit dựa trên domain events bền vững (`AnalysisCompletedEvent`, `AnalysisFailedEvent`).

* **Không Đưa PHI Vào Notification Push**: Để tuân thủ SRS §7.5 và bảo vệ quyền riêng tư người bệnh, thông báo đẩy FCM chỉ chứa tiêu đề chung (ví dụ: *"Bạn có kết quả phân tích mới"*) và liên kết bảo mật (Deep Link yêu cầu đăng nhập xác thực). Tuyệt đối không nhúng họ tên, CCCD/BHYT hoặc mức độ rủi ro vào nội dung push.



---



## 3. SPRINT 5 — Clinic, Batch Processing $\ge 100$ Ảnh, DR & AVR Training

> **Thời gian**: 02/11/2026 – 08/11/2026

> **Dải mã Jira**: **`SCRUM-362` đến `SCRUM-379`** (18 Tasks)

> **Mục tiêu**: Vận hành xử lý Batch lớn $\ge 100$ ảnh qua RabbitMQ Worker song song; hoàn thiện NiFi Dedup & Quarantine; tích hợp FCM Push Backend; hoàn thiện ví Clinic & tự động hoàn tiền; huấn luyện mô hình Đái tháo đường (HRF) và phân đoạn Động/Tĩnh mạch đo $AVR$ (INSPIRE-AVR).



### 🟡 M1 — NGUYỄN TRƯƠNG HẬU (Hạ Tầng, NiFi, Firebase & Billing Platform)



#### 1. `SCRUM-362` (`W5-M1-01` · P0 · 8h · Epic E1 `SCRUM-261`)

**Cấu hình scaling Worker RabbitMQ và Quota tài nguyên hạ tầng**

* **Phải làm sao để hoàn thành task**:

  * Tạo file `compose.workers.yaml` cấu hình mở rộng 4 workers: `docker compose -f compose.yaml -f compose.workers.yaml up -d --scale analysis-worker=4`.

  * Cấu hình giới hạn CPU/RAM trong Compose: `cpus: 1.5`, `memory: 1G` cho mỗi worker. Tính toán tổng ngân sách CPU/RAM cho toàn stack để không gây OOM trên máy dev.

  * Thiết lập `BasicQos(0, 1, false)` (Prefetch Count = 1) trong `Aura.Worker.Analysis` để RabbitMQ phân phối đều đặn từng ảnh cho 4 worker xử lý song song. Chốt giá trị Prefetch qua benchmark thực tế.

  * Cấu hình YARP API Gateway (`appsettings.json`): Tăng `MaxRequestBodySize` lên `100MB` phục vụ upload batch lớn qua multipart chunking.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Chạy scale 4 workers, cả 4 container đều kết nối ổn định vào Quorum Queue `aura.s3.analysis.jobs`.

  * **AC2**: Gửi batch 100 ảnh, đồ thị Grafana hiển thị tốc độ xử lý tăng gấp 3.5 lần so với 1 worker, không có worker nào bị crash OOM.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Phải hoàn thiện sửa schema, Service Token Provider, quyền và lease/fencing của Worker trước khi bật scale 4 instances. 4 worker không đồng nghĩa với 4 AI instances (AI Core là service độc lập).

* **Trạng thái thực thi & Hoàn tất Action Items (02/11/2026)**:
  - **ĐÃ HOÀN THÀNH 100% (PASS 8/8 `AURA.Redis.Checks`, 360/360 Full Unit Tests)**.
  - **Action Item 1.1 (Fair Dispatch)**: Hỗ trợ `WorkerOptions__Prefetch: ${WORKER_PREFETCH_COUNT:-2}` (mặc định 2 cho Fair Dispatch chống Head-of-Line Blocking, có thể override thành 10 khi cần throughput) trong `compose.workers.yaml`.
  - **Action Item 1.2 (Bảo vệ Gateway DoS)**: Phân tách route trong `AURA.ApiGateway`: áp dụng 100MB cho route upload ảnh (`/api/v1/analyses*`, `/api/v1/batches*`), áp dụng 5MB cho các route khác, chặn drop sớm ở middleware đầu vào trước xác thực JWT (HTTP 413).
  - **Action Item 1.3 (AI Core Multi-processing)**: Cấu hình tham số `--workers 4` trong Uvicorn (`src/ai-core/Dockerfile` và `compose.yaml`) giúp AI Core xử lý đồng thời 4 tiến trình độc lập phục vụ 4 worker C# song song.
  - **Action Item 2.1 (Postgres Pool Limit)**: Bổ sung `Maximum Pool Size=20;Timeout=5;Command Timeout=5` vào chuỗi kết nối PostgreSQL trong `compose.workers.yaml`, giữ tổng kết nối 4 worker ($4 	imes 20 = 80$) an toàn dưới ngưỡng 100 của PostgreSQL.



#### 2. `SCRUM-363` (`W5-M1-02` · P0 · 10h · Epic E9 `SCRUM-269`)

**Hoàn thiện luồng Apache NiFi: Dedup SHA-256, Quarantine cách ly ảnh lỗi và Bounded Retry**

* **Phải làm sao để hoàn thành task**:

  * Trong Process Group `AURA-S3-Reporting-Delivery-v1`, cấu hình processor `Record-Camera-Receipt-Safe-v2` (`ExecuteGroovyScript`):

    - Đọc `image_hash_sha256`, bắt lỗi `SQLSTATE 23505` (Duplicate Key) $\rightarrow$ Phân biệt rõ giữa gửi lặp cùng payload (Idempotent OK) và xung đột bản ghi $\rightarrow$ Gán `reporting.status = HOLD`.

    - Bắt lỗi mất kết nối DB $\rightarrow$ Gán `reporting.status = RETRY` $\rightarrow$ Đẩy sang `Bounded-Reporting-Retry-Delivery-v1` áp dụng Backoff (5s, 10s, 20s...).

    - Phát hiện tệp hỏng cấu trúc nhị phân $\rightarrow$ Đẩy ngay vào thư mục `/quarantine/` trong $< 1$s.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Thực hiện thành công cả 4 kịch bản theo đúng tài liệu `docs/integration/M1_Sprint3_camera_demo_plan.md`.

  * **AC2**: Không một FlowFile nào bị biến mất khi restart container NiFi hoặc PostgreSQL.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Không coi mọi mã `23505` là cùng một loại lỗi; xác minh transaction outcome trước khi retry. Kiểm tra tính toàn vẹn của receipt trên PostgreSQL sau khi restart container.

* **Trạng thái thực thi nghiệm thu (Closed-Loop Verified — 02/11/2026)**:
  - **Trạng thái:** **ĐÃ HOÀN THÀNH 100% (DONE / PASSED)**.
  - **Bằng chứng kiểm thử tự động:** Suite `scripts/dev/Test-Sprint5-Task2-NiFi-Ingestion.ps1` đạt **5/5 PASS (100%)**:
    + AC1.1 (Idempotent Retry): PASS (Gửi lặp trùng khóa và payload không sinh duplicate trong DB, giữ nguyên ACID).
    + AC1.2 (Conflict on 23505): PASS (Bắt `SQLSTATE 23505`, gán `reporting.status = HOLD`, cách ly an toàn vào `Reporting-Reconcile`, DB không bị ghi đè).
    + AC1.3 (Mất kết nối DB & Bounded Retry): PASS (Bắt transient connection drop, gán `reporting.status = RETRY`, tự động thử lại theo Exponential Backoff 5s, phục hồi 0% mất mát dữ liệu).
    + AC1.4 (Cách ly tệp hỏng trong < 1s): PASS (Phát hiện ảnh hỏng cấu trúc nhị phân và cách ly trong **40ms** $< 1000\text{ms}$, gán `quarantine.reason`, không ghi rác vào DB).
    + AC2 (Bền vững Zero-Loss qua Container Restart): PASS (Toàn bộ 14 receipts và FlowFiles được bảo toàn 100% sau khi restart container PostgreSQL nhờ Persistent Volumes).
  - **Tài liệu & Artifacts bàn giao:** `docs/nifi/camera_ingestion_runbook_v1.3.md`, `infra/nifi/camera_ingestion_flow_v2.json`, `infra/nifi/flow.json.gz`, `infra/nifi/RecordCameraReceiptSafeV2.groovy`.



#### 3. `SCRUM-364` (`W5-M1-03` · P1 · 6h · Epic E7 `SCRUM-267`)

**Triển khai Dịch vụ Thông báo Đẩy Firebase Cloud Messaging (FCM Backend Service)**

* **Phải làm sao để hoàn thành task**:

  * Tích hợp `FirebaseAdmin` SDK vào `AURA.Notification.Api` bằng credentials service account `aura-dev-service-account.json`.

  * Lắng nghe sự kiện `analysis.completed` từ RabbitMQ exchange `aura.s3.events`.

  * Truy vấn bảng `notification.device_registrations` lấy danh sách thiết bị và gọi `FirebaseMessaging.DefaultInstance.SendMulticastAsync()`.

  * Bổ sung cơ chế lưu job gửi thông báo bền vững (Durable Delivery Job) để retry khi mạng Firebase tạm thời gián đoạn.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Khi Worker hoàn tất ca phân tích, thông điệp push được chuyển giao sang Firebase FCM trong $< 200\text{ms}$.

  * **AC2**: Tự động dọn dẹp các FCM registration token không còn hợp lệ (`UNREGISTERED`) khỏi `notification.device_registrations`.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Đo riêng thời gian: Service nhận event $\rightarrow$ Gửi FCM thành công $\rightarrow$ Browser hiển thị. Nội dung push TUYỆT ĐỐI không chứa tên bệnh nhân hay kết quả chẩn đoán nhạy cảm (tuân thủ SRS §7.5).

* **Trạng thái thực thi & Kết quả nghiệm thu (02/11/2026)**:
  - **ĐÃ HOÀN THÀNH 100% (PASS 37/37 `AURA.Notification.InboxChecks`, 12/12 `AURA.Notification.CryptoChecks`)**.
  - **AC1 (Tối ưu Multicast & Latency)**: Triển khai `IFcmClient` và `FirebaseAdminFcmClient` sử dụng `SendEachForMulticastAsync()`, chuyển giao toàn bộ thiết bị trong **53ms** ($< 200	ext{ms}$).
  - **AC2 (Tự động dọn dẹp Token lỗi)**: Phát hiện lỗi `UNREGISTERED` và `InvalidArgument` qua `BatchResponse.Responses` và lập tức gọi `deviceRepository.DeleteIfTokenMatchesAsync(...)` để xóa khỏi `notification.device_registrations`, bảo toàn các token hợp lệ khác.
  - **Bảo mật dữ liệu nhạy cảm (SRS §7.5)**: 100% Zero-PII, tiêu đề và nội dung push chuẩn hóa trung tính không rò rỉ thông tin cá nhân hay bệnh lý võng mạc.
  - **Master Audit 3 Mốc Thời Gian**: Đo đạc đầy đủ `ReceivedAt` $
ightarrow$ `FcmDispatchedAt` $
ightarrow$ `TotalPipelineMs`, lưu trữ bền vững "Persist Before ACK" trong `notification.inbox_items`.



#### 4. `SCRUM-368` / `SCRUM-365` (`W5-M1-04` · P0 · 8h · Epic E8 `SCRUM-268`)

**Triển khai Billing cấp tổ chức (Clinic Packages & Entitlements)**

*(M1 tiếp nhận bàn giao từ M3 — hoàn thiện trên nền tảng SCRUM-348)*

* **Trạng thái**: ✅ **HOÀN THÀNH (100% PASS — 88/88 test cases)**

* **Phải làm sao để hoàn thành task**:

  * Mở rộng bảng `billing.credit_wallets` và `billing.package_price_versions` hỗ trợ các gói cước khám lưu động (Gói Cơ bản 500 ảnh - 7.000.000 VNĐ, Gói Chiến dịch 5.000 ảnh - 65.000.000 VNĐ) qua migration `010_billing_clinic_campaign_packages.sql`.

  * Backend xác minh chặt chẽ vai trò `ClinicAdmin` và quyền sở hữu Clinic (`X-Clinic-ID`, `X-Actor-ID`) qua `OrderRepository.VerifyClinicAdminAuthorityAsync` đối chiếu trực tiếp schema `profile` trước khi cho phép thao tác ví.

  * Tách biệt rõ môi trường `TEST` (Sandbox Mock Gateway) và `LIVE` trong cấu hình.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: ClinicAdmin mua gói khám tổ chức thành công, số dư ví phòng khám được cộng đúng hạn mức ngay khi Sandbox webhook xác nhận. Snapshot đơn hàng (`billing.orders`) lưu trữ bất biến số credit và đơn vị tiền tệ/giá tại thời điểm giao dịch.

  * **AC2**: Ngăn chặn hoàn toàn việc trừ credit chéo giữa các Clinic (`HTTP 403 Forbidden`). Bác sĩ Clinic B tuyệt đối không thể trừ credit thuộc ví Clinic A.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Integration tests chống IDOR toàn diện: Cố tình thay đổi `orderId`, `walletId`, hoặc `clinicId` khi tạo, tra cứu, thanh toán đều bị từ chối an toàn (`HTTP 403 Forbidden`). Webhook sandbox replay 5 lần liên tiếp được dedup an toàn chống Double Spending.



#### 5. `SCRUM-369` / `SCRUM-366` (`W5-M1-05` · P0 · 10h · Epic E8 `SCRUM-268`) — **[ĐÃ HOÀN THÀNH - 100% PASS]**

**Triển khai Cơ chế Đối soát & Tự Động Hoàn Credit Batch (Idempotent Batch Refund)**

*(M1 tiếp nhận bàn giao từ M3 — hoàn thiện trên nền tảng SCRUM-348)*

* **Trạng thái thực thi**: **DONE (Đạt chuẩn 100% - 111/111 checks PASS)**
* **Phải làm sao để hoàn thành task**:

  * Hoàn thiện quy trình giao dịch 3 bước an toàn cho Batch lớn $\ge 100$ ảnh:

    1. *Reserve*: Đặt trước số credit tương ứng khi tiếp nhận batch.

    2. *Capture*: Khấu trừ dần credit từng ảnh khi Worker phân tích thành công.

    3. *Release / Refund*: Tự động giải phóng credit đã reserve (Release) hoặc hoàn tiền credit đã capture (Refund qua bút toán `ADJUSTMENT`) cho các ảnh bị lỗi cục bộ (Partial Failure).

  * Đảm bảo tính Idempotent: API Batch Partial Refund (`POST /internal/v1/billing/reservations/batch-refund`) dù gọi lặp lại cũng chỉ hoàn tiền đúng 1 lần duy nhất (`creditsRefunded = 0`, status `ALREADY_RELEASED` / `ALREADY_REFUNDED`).

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Batch 100 ảnh có 5 ảnh lỗi, hệ thống chỉ capture 95 credits và tự động hoàn trả đúng 5 credits về ví Clinic (`Balance = 105`, net debit = 95).

  * **AC2**: Sổ cái `billing.credit_ledger_entries` ghi nhận đủ các dòng `RESERVE` (100), `CAPTURE` (95), `RELEASE` (5) khớp tuyệt đối với số dư ví, sai số đối soát bằng 0 (Zero Error).

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Phân biệt rành mạch giữa `RELEASE` (hủy giữ chỗ trước) và `REFUND` (hoàn trả tiền đã trừ thực tế qua bút toán `ADJUSTMENT`). Khóa bi quan `SELECT ... FOR UPDATE` kết hợp sắp xếp thứ tự deterministic `OrderBy(x => x.ReservationId)` triệt tiêu 100% nguy cơ Deadlock (`40P01`). Bảo vệ đa khách thuê IDOR (`HTTP 403 Forbidden`). Đạt kiểm chứng 111/111 test cases trong `tests/AURA.Billing.ReservationChecks`.



---



### 🟡 M2 — VÕ HỮU DUY (Batch Processing Engine, Báo Cáo Y Khoa & Đối Soát Batch)



#### 1. `SCRUM-365` (`W5-M2-01` · P0 · 8h · Epic E4 `SCRUM-264`)

**Triển khai Batch Processing Engine ($\ge 100$ ảnh) & Cơ chế Resume khi ngắt quãng**

* **Phải làm sao để hoàn thành task**:

  * Tạo endpoint `POST /api/v1/analyses/batches` trong `AURA.Analysis.Api` tiếp nhận danh sách metadata của $\ge 100$ ảnh.

  * Sử dụng bảng `analysis.batches` (`id`, `clinic_id`, `campaign_id`, `total_items`, `completed_items`, `status`).

  * Lưu trạng thái tiến độ vào Redis Hash để đọc nhanh, nhưng đảm bảo Redis chỉ là cache và có thể dựng lại hoàn toàn từ database PostgreSQL.

  * Xử lý lỗi cục bộ (Partial Failure): Một ảnh lỗi không làm dừng batch; các ảnh còn lại vẫn tiếp tục phân tích bình thường.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Tiếp nhận batch 100 ảnh, trả về mã tiếp nhận `batch_id` kèm `HTTP 202 Accepted` trong $< 1.5\text{s}$.

  * **AC2**: API `GET /api/v1/analyses/batches/{id}` phản ánh chính xác số ảnh đã xử lý xong theo thời gian thực.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Thống nhất một schema batch chuẩn, không tạo các bảng batch trùng lặp. Đảm bảo hỗ trợ resume an toàn nếu quá trình upload hoặc worker bị gián đoạn giữa chừng.



#### 2. `SCRUM-366` (`W5-M2-02` · P0 · 10h · Epic E4 `SCRUM-264`)

**Triển khai Động cơ Kết xuất Báo cáo Y khoa PDF chuẩn A4 (QuestPDF) & CSV**

* **Phải làm sao để hoàn thành task**:

  * Sử dụng thư viện `QuestPDF` trên .NET 10 render báo cáo bệnh án chuẩn y tế:

    - Bố cục trang A4: Thông tin Clinic, Bác sĩ, Bệnh nhân, Ngày khám.

    - Ảnh đáy mắt màu gốc đặt song song với Ảnh mặt nạ mạch máu; bảng chỉ số định lượng (`vessel_density`, `tortuosity_index`, `av_ratio`).

    - Kết luận chẩn đoán lâm sàng của bác sĩ (Doctor Revision note) và cấp độ ICDR có ghi rõ mã phiên bản AI.

  * Xuất báo cáo tổng kết Campaign dạng CSV: Chống lỗ hổng CSV Formula Injection (escape các ký tự `=`, `+`, `-`, `@`).

  * Lưu PDF vào Azurite/Azure Blob Storage; sinh Presigned URL có thời hạn 15 phút. Mã QR trên báo cáo chỉ trỏ đến liên kết bảo mật có xác thực, không nhúng token hay PHI.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Tệp PDF xuất ra chuẩn y tế A4, dung lượng nén $< 1.2\text{MB}$ cho từng ca bệnh đơn lẻ.

  * **AC2**: Link tải hết hạn sau 15 phút, truy cập trực tiếp blob bị chặn `HTTP 403`.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Báo cáo PDF lấy đúng phiên bản mô hình AI và ghi chú sửa đổi của bác sĩ đã lưu trong DB, không tự ý suy diễn ICDR khi bác sĩ chưa duyệt. Áp dụng giới hạn dung lượng phù hợp cho báo cáo tổng hợp.



#### 3. `SCRUM-367` (`W5-M2-03` · P1 · 6h · Epic E13 `SCRUM-273`)

**Kiểm thử Batch/Report, liên kết Campaign và cập nhật Hợp đồng API**

* **Phải làm sao để hoàn thành task**:

  * Viết integration test `BatchProcessingTests.cs` kiểm tra luồng gửi batch 100 ảnh.

  * Phối hợp với M6 để kiểm tra việc liên kết batch ảnh vào đúng `campaign_id` của Clinic.

  * Cập nhật tài liệu OpenAPI `contracts/analysis-openapi.yaml` bổ sung schema `BatchResponse`, `MedicalReportExportRequest`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: 100% test cases của phân hệ Batch và Report pass trên môi trường CI.

  * **AC2**: Hợp đồng OpenAPI đồng bộ tuyệt đối với mã nguồn Controller thực tế.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Ghi nhận chính xác số test pass, commit hash và log thực tế; không tuyên bố hoàn thành 100% nếu chưa kiểm thử các trường hợp biên.



#### 4. `SCRUM-370` (`W5-M2-04` · P1 · 6h · Epic E13 `SCRUM-273`)

**Kiểm thử Tích hợp Batch-Credit và Hoàn thiện Tài liệu Đối Soát Tài Chính**

*(M2 tiếp nhận bàn giao từ M3 — phối hợp chặt chẽ với M1)*

* **Phải làm sao để hoàn thành task**:

  * Viết integration test kiểm tra luồng kết hợp giữa Batch Processing của M2 và Billing Reservation của M1: Analysis tiếp nhận và reserve, Worker xử lý AI, Billing đối soát settlement.

  * Xác nhận Worker KHÔNG gọi trực tiếp các API trừ tiền; mọi cập nhật tài chính đều thông qua event-driven bền vững.

  * Hoàn thiện tài liệu `docs/billing/clinic_batch_billing_reconciliation.md`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Không xảy ra trường hợp số dư ví bị âm khi chạy batch lớn có partial failure.

  * **AC2**: Đối soát khớp 100% giữa số lượng ảnh phân tích thành công và số credit bị trừ.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Giữ vững ranh giới kiến trúc: Worker chỉ thực hiện tính toán AI và lưu kết quả, không can thiệp vào nghiệp vụ trừ tiền của Billing.



---



### 🟡 M4 — ĐÀO DUY QUÂN (AI Core: Phân Vùng Mạch, Đái Tháo Đường HRF & $AVR$ INSPIRE)



#### 1. `SCRUM-371` (`W5-M4-01` · P0 · 8h · Epic E5 `SCRUM-265`)

**Tối ưu hóa Phân vùng Mạch máu Frangi 2D v0.2 & Đo kiểm Benchmark trên Tập HRF**

* **Phải làm sao để hoàn thành task**:

  * Tối ưu hóa pipeline tiền xử lý ảnh trong `src/ai-core/app/services/image_preprocessor.py`:

    - Cân bằng độ tương phản cục bộ CLAHE (`clipLimit=2.0`, `tileGridSize=(8,8)`).

    - Cố định kích thước ảnh, FOV mask, kiểu dữ liệu float32, số luồng thực thi và giới hạn RAM của container.

    - Đo kiểm độc lập độ chính xác thuật toán Frangi 2D trên tập ảnh siêu phân giải **HRF ($3504 \times 2336$ pixel)** tại `datasets/HRF/images` so sánh với mask chuẩn `datasets/HRF/manual1`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Báo cáo đầy đủ các chỉ số p50/p95 latency, peak RAM sử dụng, Dice coefficient và IoU so với ground truth của HRF.

  * **AC2**: Thời gian phân vùng tối ưu hóa đạt ngưỡng mục tiêu trên CPU thông qua vector hóa và xử lý tách chiều.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > So sánh output baseline không thay thế đánh giá phân vùng thực tế; mục tiêu $< 1.5\text{s}$ trên ảnh siêu phân giải $3504 \times 2336$ cần đo kiểm khả thi (feasibility benchmark) kèm phương án resize thông minh.



#### 2. `SCRUM-372` (`W5-M4-02` · P0 · 10h · Epic E5 `SCRUM-265`)

**Huấn luyện Mô hình Phân loại Bệnh Võng Mạc Đái Tháo Đường (DR Screening) trên Tập HRF**

* **Phải làm sao để hoàn thành task**:

  * Khai thác tập dữ liệu `datasets/HRF`: 15 ca Bệnh võng mạc đái tháo đường (`dr`) vs 15 ca Mắt khỏe mạnh (`h`) vs 15 ca Glôcôm (`g`).

  * Tách biệt rõ ràng bài toán: Sàng lọc Nhị phân (Binary Classification: Bệnh vs Bình thường) phục vụ tầm soát sớm.

  * Áp dụng Transfer Learning (ResNet-34 / EfficientNet) kết hợp chia dữ liệu theo cấp bệnh nhân (Patient-level split) để chống rò rỉ dữ liệu (Data Leakage).

  * Tích hợp trích xuất bản đồ nhiệt giải thích **Grad-CAM (Explainable AI — NFR-22)** hỗ trợ bác sĩ định vị vùng tổn thương.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Mô hình phân loại chính xác giữa ca đái tháo đường (`dr`) và ca bình thường (`h`) với AUC-ROC $\ge 0.88$ trên tập validation độc lập.

  * **AC2**: Trích xuất bản đồ Grad-CAM trực quan hỗ trợ bác sĩ quan sát vùng xuất huyết/vi phình mạch.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Tập HRF chỉ có 45 ảnh (15 dr, 15 h, 15 g) và không có nhãn phân độ 5 cấp ICDR. Do đó, task này được định nghĩa chuẩn xác là Sàng lọc Nhị phân (Binary DR Screening), KHÔNG tự tuyên bố chẩn đoán phân độ ICDR khi chưa có dữ liệu gán nhãn chuyên khoa.



#### 3. `SCRUM-373` (`W5-M4-03` · P1 · 6h · Epic E5 `SCRUM-265`)

**Xây dựng Pipeline Phân đoạn Động mạch / Tĩnh mạch & Đo Tỷ Số $AVR$ trên Tập INSPIRE-AVR**

* **Phải làm sao để hoàn thành task**:

  * Kiểm tra tính toàn vẹn và provenance của 100 ca chụp chuẩn y khoa tại `datasets/INSPIRE_AVR`: sử dụng mặt nạ Động mạch (`--arteries.jpg`) và Tĩnh mạch (`--veins.jpg`).

  * Xây dựng pipeline: Xác định vùng quan tâm quanh đĩa thị (Optic Disc ROI: 2–3 đường kính đĩa thị), đo khẩu kính mạch máu theo công thức chuẩn Parr-Hubbard / Knudtson (CRAE, CRVE).

  * Tính toán **Tỷ số Động/Tĩnh mạch ($AVR$)**: $AVR = \text{CRAE} / \text{CRVE}$.

  * Cho phép trả về `RISK_UNAVAILABLE` khi không định vị được đĩa thị hoặc mạch máu quá mờ.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Pipeline phân biệt được mạng lưới Động mạch (Đỏ) và Tĩnh mạch (Xanh) trên các ca test của INSPIRE-AVR.

  * **AC2**: Giá trị $AVR$ đo được phản ánh chính xác tỷ số CRAE/CRVE; khi ảnh không đủ điều kiện, hệ thống trả về `RISK_UNAVAILABLE` có giải trình rõ ràng.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > XÓA BỎ TIÊU CHÍ ÉP BUỘC $AVR$ LUÔN NẰM TRONG $[0.40, 0.85]$. Trong y tế, khi dữ liệu kém chất lượng hoặc không trích xuất được, hệ thống phải trung thực báo không đo được thay vì bịa đặt một con số sinh học giả tạo.



---



### 🟡 M5 — LÂM TRẦN NGUYỄN HOÀNG (Frontend Lead / Web UI)



#### 1. `SCRUM-374` (`W5-M5-01` · P0 · 8h · Epic E12 `SCRUM-272`)

**Xây dựng Giao diện Tải ảnh Hàng loạt (Bulk Upload UI $\ge 100$ ảnh)**

* **Phải làm sao để hoàn thành task**:

  * Nâng cấp trang Upload hỗ trợ kéo thả cùng lúc lên đến 100 ảnh võng mạc.

  * Kiểm tra hợp lệ phía Client: Định dạng `.jpg`/`.png`, dung lượng mỗi ảnh $\le 10\text{MB}$.

  * Phân đoạn tải lên (Chunked / Concurrent Uploads) với giới hạn đồng thời (ví dụ: 4–6 ảnh cùng lúc) để không gây nghẽn mạng và vượt quá giới hạn 100MB của Gateway.

  * Hiển thị danh sách thumbnail kèm lựa chọn mắt chụp (`OD - Mắt phải`, `OS - Mắt trái`).

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Kéo thả 100 ảnh cùng lúc, giao diện xử lý mượt mà, hiển thị danh sách xem trước không bị đơ trình duyệt.

  * **AC2**: Hỗ trợ resume hoặc retry từng ảnh riêng lẻ nếu có sự cố đường truyền.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > 100 ảnh $\times 10\text{MB}$ có thể đạt tới 1GB dữ liệu; do đó không được gửi toàn bộ trong một request multipart duy nhất mà phải áp dụng tải lên theo hàng đợi có kiểm soát luồng.



#### 2. `SCRUM-375` (`W5-M5-02` · P0 · 10h · Epic E12 `SCRUM-272`)

**Xây dựng Giao diện Theo dõi Tiến độ Batch & Nút Tải Báo cáo PDF/CSV**

* **Phải làm sao để hoàn thành task**:

  * Tạo trang `BatchDetailPage.tsx`: Hiển thị thanh tiến trình tổng (ví dụ: `Đã hoàn thành 68/100 ảnh`), danh sách chi tiết trạng thái từng ảnh (`Đang chờ`, `Đang phân tích`, `Thành công`, `Lỗi`).

  * Cơ chế cập nhật: Kết hợp Polling thông minh có giãn cách (Exponential Backoff) cho MVP và chuẩn bị tiếp nhận Web Push từ FCM.

  * Nút "Tải Báo Cáo PDF" cho từng ca bệnh và nút "Tải Tất Cả (ZIP)" cho toàn bộ batch.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Tiến trình batch được cập nhật liên tục, không gây quá tải request lên Backend.

  * **AC2**: Bấm nút tải PDF tải được đúng file báo cáo chuẩn của bệnh nhân tương ứng.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Không cho phép người dùng bấm retry vô tội vạ đối với các ca đang xử lý hoặc chưa rõ kết quả, tránh nguy cơ bị trừ phí credit hai lần.



#### 3. `SCRUM-376` (`W5-M5-03` · P1 · 6h · Epic E13 `SCRUM-273`)

**Kiểm thử Giao diện Batch Frontend và Xử lý Mất Mạng Ngoại Lệ**

* **Phải làm sao để hoàn thành task**:

  * Viết component test với React Testing Library cho form Bulk Upload và thanh tiến trình Batch.

  * Kiểm tra các trường hợp ngoại lệ: Mất mạng giữa chừng khi đang upload, người dùng F5 tải lại trang khi batch đang chạy.

  * Xử lý dữ liệu nháp: Dữ liệu nhạy cảm của bệnh nhân không được ghi lưu vĩnh viễn không mã hóa vào `LocalStorage`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Khi F5 lại trang, giao diện khôi phục chính xác tiến độ batch từ `batchId` trên URL.

  * **AC2**: 100% component tests của module Upload pass.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Tuân thủ chính sách bảo mật dữ liệu y tế: Không lưu trữ thông tin nhận dạng bệnh nhân trong bộ nhớ trình duyệt sau khi phiên làm việc kết thúc.



---



### 🟡 M6 — DUNG HOANG (Identity, Clinic & Phân Hệ Campaign)



#### 1. `SCRUM-377` (`W5-M6-01` · P0 · 8h · Epic E12 `SCRUM-272`)

**Xây dựng Giao diện Quản lý Thành viên Bác sĩ & Bệnh nhân Clinic**

* **Phải làm sao để hoàn thành task**:

  * Xây dựng màn hình `ClinicMemberPage.tsx` và `ClinicPatientPage.tsx`.

  * Cho phép ClinicAdmin mời bác sĩ vào tổ chức, gán vai trò (`Doctor`, `Staff`), kích hoạt hoặc thu hồi quyền truy cập.

  * Quản lý danh bạ bệnh nhân thuộc sở hữu của Clinic, liên kết lịch sử các đợt khám.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Thêm mới bác sĩ thành công, bác sĩ đăng nhập bằng Google nhận đúng ngữ cảnh Clinic.

  * **AC2**: Bác sĩ chỉ nhìn thấy danh sách bệnh nhân thuộc phòng khám của mình, tuyệt đối không thấy bệnh nhân của clinic khác.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Phân định trách nhiệm rõ ràng: M6 chịu trách nhiệm Backend và Access Control Policy, phối hợp với M5 để tái sử dụng các components UI dùng chung.



#### 2. `SCRUM-378` (`W5-M6-02` · P0 · 10h · Epic E3 `SCRUM-263`)

**Triển khai Phân hệ Chiến dịch Tầm soát (Campaign API & UI trong Analysis)**

* **Phải làm sao để hoàn thành task**:

  * Hiện thực module Campaign trong bounded context của Analysis:

    - Bảng `analysis.campaigns` (`id`, `clinic_id`, `name`, `target_patient_count`, `start_date`, `end_date`, `status`).

    - API: `POST /api/v1/campaigns`, `GET /api/v1/campaigns/{id}`, `POST /api/v1/campaigns/{id}/batches`.

  * Xây dựng giao diện tạo và quản lý chiến dịch tầm soát lưu động cho Clinic.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Quản trị viên Clinic tạo chiến dịch khám mắt cộng đồng, gán các batch chụp ảnh vào chiến dịch đó thành công.

  * **AC2**: Dữ liệu chiến dịch hoàn toàn cô lập theo `clinic_id`.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Tuân thủ ranh giới dịch vụ: Service Profile của M6 không được ghi trực tiếp vào Database của Analysis; mọi tương tác giữa Profile và Campaign phải thông qua API hoặc Service Token hợp lệ.



#### 3. `SCRUM-379` (`W5-M6-03` · P1 · 6h · Epic E13 `SCRUM-273`)

**Kiểm thử Campaign Đa Tenant và Cập nhật Tài liệu Nghiệp vụ**

* **Phải làm sao để hoàn thành task**:

  * Viết test HTTP thực tế qua Gateway sử dụng token thật của các vai trò khác nhau.

  * Kiểm tra phân quyền đa tenant: Clinic B cố tình truy cập hoặc thêm batch vào Campaign của Clinic A sẽ nhận ngay lỗi `HTTP 403 Forbidden` (hoặc `HTTP 404 Not Found` theo hợp đồng).

  * Cập nhật tài liệu thiết kế trong `docs/design/campaign_management_sequence.md`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Kiểm thử 10 ca gọi chéo tenant đều bị chặn an toàn.

  * **AC2**: Kiểm tra Positive Control trước Negative Control để bảo đảm test không bị pass giả tạo.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Phải kiểm tra cả trường hợp truy cập hợp lệ (Positive Control) trước khi kiểm tra chặn vi phạm (Negative Control) để tránh ngộ nhận mọi response 403 là hệ thống chạy đúng.



---



## 4. SPRINT 6 — Admin Portal, Consultation/Chat, Cảnh Báo & Camera Ingestion [CODE FREEZE]

> **Thời gian**: 09/11/2026 – 15/11/2026

> **Dải mã Jira**: **`SCRUM-380` đến `SCRUM-397`** (18 Tasks)

> **Mục tiêu**: Admin quản trị toàn hệ thống; Bệnh nhân và Bác sĩ trò chuyện tư vấn trực tuyến qua SignalR; Clinic có dashboard cảnh báo; Camera ingestion vận hành theo chuẩn Service Principal; Tích hợp trọn vẹn mô hình DR & $AVR$ vào Doctor Workspace. **TIẾN HÀNH CODE FREEZE VÀO CUỐI TUẦN**.



### 🟡 M1 — NGUYỄN TRƯƠNG HẬU (Audit, Camera NiFi & Quản Trị FCM/Billing)



#### 1. `SCRUM-380` (`W6-M1-01` · P0 · 8h · Epic E11 `SCRUM-271`)

**Triển khai Nền tảng Audit Log Tập trung & Bảo vệ Dữ liệu Riêng tư (Privacy)**

* **Phải làm sao để hoàn thành task**:

  * Thiết lập bảng lưu vết kiểm toán tập trung `profile.audit_events` (Append-Only) có ghi nhận `actor_id`, `service_principal`, `clinic_id`, `action`, `resource_id`, `correlation_id` và timestamp.

  * Cấu hình cơ chế Serilog Redaction: Tự động che dấu (Masking) số CCCD/BHYT, token và mật khẩu.

  * Thiết lập chính sách lưu trữ (Data Retention Policy) và kiểm soát quyền truy cập log kiểm toán.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Mọi hành vi xem bệnh án, tải PDF hoặc đổi cấu hình đều được ghi nhận audit log đầy đủ.

  * **AC2**: File log hệ thống và database audit tuyệt đối không chứa token JWT hay dữ liệu thô nhạy cảm.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Xác định rõ service sở hữu dữ liệu audit; không để các microservices khác tự do ghi trực tiếp vào schema database của nhau.



#### 2. `SCRUM-381` (`W6-M1-02` · P0 · 10h · Epic E9 `SCRUM-269`)

**Hoàn thiện Cấu hình Camera Flow & Service Principal Authentication (NiFi Ingestion)**

* **Phải làm sao để hoàn thành task**:

  * Cấu hình Service Principal có thời hạn (Client Credentials) cho từng trạm Camera hoặc cụm NiFi Ingestion.

  * Processor `InvokeHTTP` trên NiFi tự động lấy token xác thực từ Identity API trước khi đẩy gói tin sang Gateway.

  * Truyền ngữ cảnh `X-Clinic-ID` và `X-Camera-ID` trong header HTTP và đối chiếu với camera grants trong database.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: NiFi gửi ảnh qua Gateway vào Analysis API thành công với mã `HTTP 202 Accepted` kèm Service Token hợp lệ.

  * **AC2**: Khi thu hồi quyền của một camera, mọi yêu cầu tiếp nhận tiếp theo bị từ chối an toàn với `HTTP 401/403`.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Phân biệt rõ việc thu hồi Service Principal với thời hạn sống của token đã phát; cấu hình cơ chế kiểm tra trạng thái thu hồi tức thời.



#### 3. `SCRUM-382` (`W6-M1-03` · P1 · 6h · Epic E11 `SCRUM-271`)

**Kiểm thử Quản trị Hạ tầng, TLS và Chốt Tài liệu ADR-005 Camera Principal**

* **Phải làm sao để hoàn thành task**:

  * Kiểm tra toàn bộ chứng chỉ TLS cục bộ trên các cổng kết nối (Gateway 8443, NiFi 8543).

  * Viết kịch bản kiểm tra ngắt kết nối mạng Camera và kiểm tra khả năng lưu đệm tại chỗ của NiFi trước khi đẩy bù khi có mạng lại.

  * Cập nhật quyết định kiến trúc `docs/adr/ADR-005-camera-service-principal.md`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Báo cáo kiểm thử hạ tầng xác nhận tính toàn vẹn và an toàn của luồng camera ingestion.

  * **AC2**: Tài liệu ADR giải trình đầy đủ lý do kiến trúc và phương án quản lý khóa bảo mật.



#### 4. `SCRUM-387` (`W6-M1-04` · P0 · 10h · Epic E8 `SCRUM-268`)

**Triển khai Cổng Quản trị Gói Cước & Chính Sách Thông Báo Firebase FCM (Admin Billing & FCM)**

*(M1 tiếp nhận bàn giao từ M3 — quản lý trọn vẹn Firebase & Billing)*

* **Phải làm sao để hoàn thành task**:

  * Cung cấp API cho SuperAdmin quản lý danh mục gói cước: Thêm mới gói, điều chỉnh giá, tạm ngừng gói cũ với versioning giá an toàn (không làm ảnh hưởng các đơn hàng đã tạo).

  * Quản trị Template thông báo Firebase FCM: Soạn thảo mẫu thông báo cho từng sự kiện (`ANALYSIS_READY`, `PAYMENT_SUCCESS`).

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Đổi giá gói cước mới áp dụng cho các giao dịch mới, bảo toàn nguyên vẹn lịch sử giao dịch cũ.

  * **AC2**: Thông báo đẩy FCM tuân thủ nguyên tắc không lộ PHI; gửi thông báo chung kèm Deep Link yêu cầu đăng nhập.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Bỏ hoàn toàn các placeholder chứa PHI (`patient_name`, `high-risk`) trong nội dung push để tuân thủ SRS §7.5; chỉ gửi thông báo trung tính.



---



### 🟡 M2 — VÕ HỮU DUY (Thống Kê Y Tế, Alert Rules & Backend Chat SignalR)



#### 1. `SCRUM-383` (`W6-M2-01` · P0 · 8h · Epic E10 `SCRUM-270`)

**Triển khai API Thống kê Y tế & Hoạt động Clinic/Admin**

* **Phải làm sao để hoàn thành task**:

  * Xây dựng controller `AnalyticsController.cs` cung cấp số liệu thống kê chuẩn:

    - Tổng số ảnh đã chụp, tỷ lệ xử lý thành công vs lỗi (loại bỏ các ảnh mock/test).

    - Phân bố mức độ nguy cơ tổn thương mạch máu võng mạc (Low, Moderate, High).

    - Năng suất duyệt ca của từng bác sĩ trong Clinic (đáp ứng FR-21).

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: API trả về dữ liệu tổng hợp chính xác theo `clinic_id`, thời gian phản hồi $< 200\text{ms}$ nhờ tận dụng Redis Cache.

  * **AC2**: Quản trị viên SuperAdmin có endpoint riêng để xem thống kê toàn hệ thống.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Định nghĩa rõ mẫu số và nguồn dữ liệu thống kê; không gọi phân bố nguy cơ heuristic của một nhóm nhỏ là tỷ lệ mắc bệnh trong dân số.



#### 2. `SCRUM-384` (`W6-M2-02` · P0 · 10h · Epic E10 `SCRUM-270`)

**Triển khai Động cơ Cảnh báo & Xuất Dữ liệu Nghiên cứu Ẩn danh (Research Export)**

* **Phải làm sao để hoàn thành task**:

  * Thiết lập Alert Rules nghiệp vụ: Tự động phát cảnh báo khi một ca chụp có chỉ số $AVR$ bất thường hoặc có dấu hiệu bệnh lý nghiêm trọng.

  * Xây dựng tính năng Research Export: Xuất tập dữ liệu ẩn danh (ảnh mask và các thông số đo đạc) phục vụ nghiên cứu khoa học; tự động loại bỏ toàn bộ thông tin PHI.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Ca chụp nguy cơ cao được gắn cờ cảnh báo lâm sàng rõ ràng.

  * **AC2**: Tệp dữ liệu xuất ra được khử định danh hoàn toàn, bảo vệ quyền riêng tư người bệnh.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Tách bạch rõ 2 bài toán: Cảnh báo lâm sàng (cần căn cứ y khoa đã thẩm định) và Xuất dữ liệu ẩn danh (cần kiểm soát quyền truy cập và chính sách lưu trữ).



#### 3. `SCRUM-385` (`W6-M2-03` · P1 · 6h · Epic E13 `SCRUM-273`)

**Kiểm thử Thống kê, Đối soát Dữ liệu và Cập nhật Tài liệu Công thức Dịch tễ**

* **Phải làm sao để hoàn thành task**:

  * Viết test kiểm tra thuật toán thống kê: Đảm bảo các ca chụp trùng lặp hoặc ảnh lỗi không bị đếm vào tổng số ca khám thành công.

  * Cập nhật tài liệu công thức tính toán dịch tễ trong `docs/analytics/epidemiological_metrics_spec.md`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: 100% ca kiểm thử số liệu thống kê đạt kết quả chính xác tuyệt đối.

  * **AC2**: Các ca thiếu dữ liệu nguy cơ (`RISK_UNAVAILABLE`) được phân loại riêng, không gộp bừa vào nhóm nguy cơ thấp.



#### 4. `SCRUM-386` (`W6-M2-04` · P0 · 8h · Epic E7 `SCRUM-267`)

**Triển khai Backend Tư vấn Trực tuyến (Consultation/Chat Service qua SignalR Hub)**

*(M2 tiếp nhận bàn giao từ M3 — triển khai Hub thời gian thực)*

* **Phải làm sao để hoàn thành task**:

  * Xây dựng SignalR Hub `ConsultationChatHub.cs`:

    - Bảng `notification.conversations` (`id`, `patient_id`, `doctor_id`, `analysis_id`, `status`).

    - Bảng `notification.messages` (`id`, `conversation_id`, `sender_id`, `content`, `sent_at`, `is_read`).

  * Quản lý phòng chat theo `conversation_id`: Kiểm tra quyền tham gia của Bệnh nhân và Bác sĩ tại thời điểm kết nối và trong từng tin nhắn gửi đi.

  * Lưu trữ tin nhắn bền vững trong PostgreSQL, hỗ trợ phân trang lịch sử tin nhắn.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Bác sĩ và Bệnh nhân trao đổi tin nhắn thời gian thực qua WebSocket với độ trễ $< 50\text{ms}$.

  * **AC2**: Người ngoài cố tình kết nối trái phép bị chặn an toàn với `HTTP 403 Forbidden`.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Cần kiểm tra quyền phân công (Doctor-Patient Assignment) liên tục trên từng tin nhắn gửi và đọc, không chỉ kiểm tra một lần duy nhất lúc mở kết nối WebSocket.



---



### 🟡 M4 — ĐÀO DUY QUÂN (Tích Hợp AI Core REST API & Đóng Gói Model Card v2.0)



#### 1. `SCRUM-389` (`W6-M4-01` · P0 · 8h · Epic E5 `SCRUM-265`)

**Tích hợp Mô hình Đái Tháo Đường (HRF) & Đo Chỉ Số $AVR$ (INSPIRE-AVR) vào AI Core API**

* **Phải làm sao để hoàn thành task**:

  * Nâng cấp endpoint `POST /api/v1/analyze` trong FastAPI trả về đầy đủ các trường y sinh học thực nghiệm theo hợp đồng OpenAPI v1.3:

    - `vessel_segmentation`: Mask và Overlay mạch máu Frangi.

    - `diabetic_retinopathy`: Nhãn dự đoán nguy cơ Đái tháo đường, điểm số xác suất và đường dẫn ảnh nhiệt Grad-CAM.

    - `artery_vein_analysis`: Tỷ số đường kính Động/Tĩnh mạch `av_ratio` ($AVR$) và bản đồ phân màu Động mạch (Đỏ) - Tĩnh mạch (Xanh).

    - `risk_assessment`: Tích hợp đa yếu tố xếp tầng `LOW / MODERATE / HIGH / UNAVAILABLE`.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Endpoint tiếp nhận ảnh Fundus, xử lý đồng thời các pipeline trong thời gian chấp nhận được trên CPU.

  * **AC2**: Phản hồi JSON tuân thủ chuẩn xác hợp đồng OpenAPI `contracts/ai_service_openapi.yaml`.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Giữ tính tương thích ngược của schema v1.3. Mỗi output phải có trường trạng thái `available` và `reason`; khi chưa đủ điều kiện tính toán, trả về `available: false` thay vì trả dữ liệu giả.



#### 2. `SCRUM-390` (`W6-M4-02` · P0 · 10h · Epic E5 `SCRUM-265`)

**Triển khai Cơ chế Tải lại Cấu hình AI Động (Dynamic Config & Model Reload)**

* **Phải làm sao để hoàn thành task**:

  * Cung cấp endpoint quản trị `/api/v1/admin/config/reload` cho phép cập nhật ngưỡng nhị phân hóa hoặc thay thế file weights `.onnx` mà không cần restart container.

  * Đảm bảo xác thực Service Token đặc quyền của SuperAdmin trước khi thực hiện reload.

  * Kiểm tra tính nguyên tử (Atomic Activation): Các request đang chạy dở dang tiếp tục dùng mô hình cũ, request mới dùng mô hình mới.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Cập nhật tham số AI thành công qua API có xác thực, không làm gián đoạn các request đang xử lý.

  * **AC2**: Không cho phép endpoint reload nhận đường dẫn tùy tiện từ bên ngoài gây lỗ hổng bảo mật.



#### 3. `SCRUM-391` (`W6-M4-03` · P1 · 6h · Epic E13 `SCRUM-273`)

**Đóng Gói Model Card v2.0 Toàn Diện (Benchmark CHASE_DB1, HRF & INSPIRE-AVR)**

* **Phải làm sao để hoàn thành task**:

  * Biên soạn tài liệu `docs/data_cards/model_card_v2.0.md` theo chuẩn Google Model Card Toolkit.

  * Trình bày chi tiết bảng kết quả thực nghiệm tách biệt cho từng bài toán:

    - `CHASE_DB1`: Độ chính xác phân vùng vi mạch (Dice, IoU, Sensitivity, Specificity).

    - `HRF`: Độ chính xác sàng lọc Đái tháo đường (AUC-ROC, Accuracy).

    - `INSPIRE_AVR`: Sai số đo tỷ số $AVR$ so với nhãn chuyên gia Iowa.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Bảng số liệu khoa học đầy đủ, có thể tái lập bằng script benchmark kèm mã hash commit và cấu hình.

  * **AC2**: Nêu rõ ranh giới y đức: AI là công cụ hỗ trợ sàng lọc (CDS), không thay thế quyết định của bác sĩ.



---



### 🟡 M5 — LÂM TRẦN NGUYỄN HOÀNG (Frontend Lead / Web UI)



#### 1. `SCRUM-392` (`W6-M5-01` · P0 · 8h · Epic E12 `SCRUM-272`)

**Xây dựng Giao diện Quản trị Hệ thống (Admin Portal UI)**

* **Phải làm sao để hoàn thành task**:

  * Xây dựng layout Admin chuyên biệt cho SuperAdmin:

    - Quản lý danh mục Gói cước: Xem, tạo mới, chỉnh sửa giá gói.

    - Quản lý Cấu hình AI: Thanh trượt trực quan điều chỉnh tham số Frangi có xác nhận và lưu nhật ký thay đổi.

    - Quản lý Mẫu thông báo FCM.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Chỉ tài khoản có vai trò `SuperAdmin` mới truy cập được các trang quản trị này.

  * **AC2**: Thao tác cập nhật cấu hình được kiểm tra hợp lệ phía Backend và lưu vết audit log đầy đủ.



#### 2. `SCRUM-393` (`W6-M5-02` · P0 · 10h · Epic E12 `SCRUM-272`)

**Xây dựng Giao diện Chat Tư Vấn Bệnh Nhân – Bác Sĩ (SignalR Client)**

* **Phải làm sao để hoàn thành task**:

  * Tạo component `ConsultationChatBox.tsx`:

    - Khung chat trực quan: Danh sách tin nhắn, bong bóng chat phân biệt Bác sĩ và Bệnh nhân, trạng thái Đang gửi / Đã gửi.

    - Tự động cuộn xuống tin nhắn mới nhất, hỗ trợ mở nhanh Bệnh án phân tích tương ứng.

    - Kết nối SignalR Client tự động kết nối lại khi mất mạng (Auto-reconnect).

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Trò chuyện mượt mà giữa 2 cửa sổ trình duyệt (Bác sĩ và Bệnh nhân) qua WebSocket.

  * **AC2**: Khi mất mạng, giao diện hiển thị cảnh báo trạng thái mất kết nối rõ ràng.



#### 3. `SCRUM-394` (`W6-M5-03` · P1 · 6h · Epic E13 `SCRUM-273`)

**Nâng Cấp Doctor Workspace UI: Hiển Thị Layer Động/Tĩnh Mạch A/V & Gợi Ý Cấp Độ ICDR**

* **Phải làm sao để hoàn thành task**:

  * Trong `DoctorWorkspace.tsx`:

    - Bổ sung công tắc chuyển đổi lớp phủ: Ảnh gốc $\leftrightarrow$ Lớp phủ vi mạch Frangi $\leftrightarrow$ Lớp phủ Động/Tĩnh mạch (A/V Map) $\leftrightarrow$ Bản đồ nhiệt Grad-CAM.

    - Hiển thị bảng chỉ số sinh học: $AVR$, Mật độ vi mạch, Độ cong queo mạch.

    - Form chẩn đoán hiển thị gợi ý do AI đề xuất; Bác sĩ là người duy nhất có quyền xác nhận hoặc chỉnh sửa kết luận.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Bác sĩ chuyển đổi mượt mà giữa các lớp hiển thị ảnh y khoa.

  * **AC2**: Lưu kết luận của Bác sĩ kèm phiên bản chỉnh sửa (Revision Tracking) chống ghi đè mất dữ liệu.



---



### 🟡 M6 — DUNG HOANG (Admin Identity, Phê Duyệt Clinic & Code Freeze)



#### 1. `SCRUM-395` (`W6-M6-01` · P0 · 8h · Epic E10 `SCRUM-270`)

**Triển khai API Quản trị Tài khoản & Phê duyệt Clinic (Admin API)**

* **Phải làm sao để hoàn thành task**:

  * Cung cấp các endpoint quản trị trong `AURA.Identity.Api` & `AURA.Profile.Api`:

    - Phê duyệt / Từ chối hồ sơ đăng ký mở phòng khám (`POST /api/v1/admin/clinics/{id}/approve`).

    - Khóa / Mở khóa tài khoản người dùng vi phạm chính sách (`POST /api/v1/admin/users/{id}/suspend`).

    - Phân bổ vai trò đặc quyền có ghi nhật ký kiểm toán bắt buộc.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Khóa tài khoản thành công, người dùng bị chặn thực hiện các tác vụ tiếp theo ngay lập tức.

  * **AC2**: Cơ chế bảo vệ invariant: Chống việc SuperAdmin tự thu hồi quyền của mình hoặc tự xóa tài khoản cuối cùng.



#### 2. `SCRUM-396` (`W6-M6-02` · P0 · 10h · Epic E12 `SCRUM-272`)

**Xây dựng Giao diện Phê duyệt Clinic & Quản trị Danh tính**

* **Phải làm sao để hoàn thành task**:

  * Xây dựng trang `AdminClinicApprovalPage.tsx` và `AdminUserManagementPage.tsx`:

    - Bảng danh sách các Clinic đang chờ phê duyệt (kèm giấy phép kinh doanh, thông tin liên hệ).

    - Nút bấm "Phê duyệt" hoặc "Từ chối" kèm lý do.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Clinic được phê duyệt chuyển trạng thái `ACTIVE` và bắt đầu được mua credit/khám bệnh.

  * **AC2**: Danh sách người dùng hỗ trợ tìm kiếm theo Email, UID Firebase hoặc Tên hiển thị.



#### 3. `SCRUM-388` (`W6-M6-04` · P1 · 6h · Epic E13 `SCRUM-273`)

**Kiểm thử Tích hợp Chat, Alert & Quản trị Thanh toán**

*(M6 tiếp nhận bàn giao từ M3 — kiểm thử tích hợp chéo)*

* **Phải làm sao để hoàn thành task**:

  * Viết integration test kiểm tra việc thu hồi quyền: Khi bác sĩ bị xóa khỏi Clinic, bác sĩ đó tự động mất quyền gửi tin nhắn trong các cuộc chat của Clinic đó.

  * Kiểm tra tích hợp thông báo: Tin nhắn gửi khi người nhận offline kích hoạt thông báo FCM an toàn.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Kiểm tra thu hồi quyền ngắt kết nối WebSocket và chặn gửi tin nhắn thành công.

  * **AC2**: Thông báo offline được chuyển giao cho Notification API xử lý.



#### 4. `SCRUM-397` (`W6-M6-03` · P1 · 6h · Epic E11 `SCRUM-271`)

**Rà soát Phân quyền An toàn & Tiến hành CODE FREEZE Toàn Hệ Thống**

* **Phải làm sao để hoàn thành task**:

  * Rà soát toàn bộ hệ thống API Gateway, đảm bảo 100% endpoint nghiệp vụ đều được kiểm soát bởi phân quyền chặt chẽ.

  * **TIẾN HÀNH CODE FREEZE (KHÓA MÃ NGUỒN TÍNH NĂNG)** vào ngày cuối cùng của Sprint 6:

    - Chốt nhánh `develop`, tạo nhánh `release/v1.0`.

    - Thông báo toàn nhóm: Kể từ thời điểm này, **tuyệt đối không viết thêm bất kỳ tính năng mới nào**, toàn bộ thời gian của Sprint 7 và 8 dành riêng cho việc Kiểm thử, Tối ưu hóa, Đo đạc NFRs và Hoàn thiện Hồ sơ Đồ án.

* **Tiêu chí nghiệm thu (AC)**:

  * **AC1**: Toàn bộ các PR tính năng mới được merge hoàn tất trước thời hạn Code Freeze.

  * **AC2**: Lập biên bản Code Freeze và checklist tính năng gửi toàn nhóm xác nhận.

* **Hiệu chỉnh cốt lõi từ Master Audit**:

  > Code Freeze dựa trên CI và contract đã được kiểm chứng, không tùy tiện pass khi còn lỗi nghiêm trọng. Các task của Sprint 7 và 8 chỉ phục vụ verification, hardening và tài liệu.



---



## 5. SPRINT 7 — Hiệu Năng Cao, Bảo Mật Toàn Diện, Đo Đạc 23 NFRs & Kiểm Thử E2E

> **Thời gian**: 16/11/2026 – 22/11/2026

> **Dải mã Jira**: **`SCRUM-398` đến `SCRUM-415`** (18 Tasks)

> **Mục tiêu**: Toàn bộ các chức năng chính hoàn thành; 100% các chỉ số phi chức năng (23 NFRs) được đo đạc thực tế; triệt tiêu hoàn toàn các lỗi mất dữ liệu, sai quyền, tranh chấp credit trước ngày đóng gói bàn giao.



### 🟡 M1 — NGUYỄN TRƯƠNG HẬU (Bảo Mật, Diễn Tập DR & Đo Đạc NFR Hạ Tầng)

* **`SCRUM-398` (`W7-M1-01` · P0 · 8h · E11)**: **Kiểm toán Bảo mật & Quét Lỗ hổng Toàn Stack (Full-Stack Security Audit)**:

  Quét lỗ hổng dependencies bằng Trivy, cấu hình TLS/HTTPS trên Gateway và cấu hình mã hóa lưu trữ tĩnh. Bảo vệ nghiêm ngặt các tệp khóa bảo mật khỏi Git repository. (AC: Không còn lỗi High/Critical, Secret không lọt vào Git).

* **`SCRUM-399` (`W7-M1-02` · P0 · 10h · E1)**: **Kiểm tra Backup, Restore & Diễn tập Phục hồi Thảm họa (Disaster Recovery Drill)**:

  Chạy `Backup-AuraDatabase.ps1`, dựng môi trường phục hồi riêng (không xóa container đang giữ dữ liệu kiểm thử), chạy `Restore-AuraDatabase.ps1` phục hồi từ zero. Đo đạc RTO và RPO thực tế. (AC: RTO $< 12$ phút đáp ứng NFR-4).

* **`SCRUM-400` (`W7-M1-03` · P1 · 6h · E1)**: **Sửa Lỗi Hạ tầng, Tinh chỉnh Connection Pool & Chốt Runbook v2.0**:

  Tinh chỉnh Connection Pool trên PostgreSQL và channels RabbitMQ dựa trên số liệu đo đạc tải. Hoàn thiện sổ tay hướng dẫn cài đặt và vận hành sử dụng đường dẫn tương đối.

* **`SCRUM-405` (`W7-M1-04` · P0 · 10h · E13)**: **Đo đạc NFR & Kiểm tra Độ Ổn Định Dịch Vụ Hạ Tầng (Resilience & NFR Verification)**:

  Tiến hành đo đạc độc lập: NFR-1 (AI Core latency), NFR-2 (Batch $\ge 100$ ảnh), NFR-4 (Uptime $\ge 99\%$). Kiểm tra khả năng tự phục hồi của Billing, RabbitMQ DLQ và FCM khi đứt mạng. Ghi nhận kết quả trung thực theo các trạng thái PASS/FAIL/BLOCKED.



### 🟡 M2 — VÕ HỮU DUY (Tối Ưu Truy Vấn, Kiểm Tra Nhất Quán & Báo Cáo Backend)

* **`SCRUM-401` (`W7-M2-01` · P0 · 8h · E4)**: **Tối ưu hóa Analysis & Dashboard Queries (Chống nghẽn NFR-3)**:

  Sử dụng `EXPLAIN ANALYZE` rà soát truy vấn, loại bỏ triệt để lỗi N+1 Query trong EF Core bằng `AsNoTracking()`, bổ sung composite indexes cho các truy vấn theo `clinic_id` và `created_at`. (AC: Thời gian tải Dashboard $< 1.2\text{s}$ trên 10.000 bản ghi, đáp ứng NFR-3 $< 3$s).

* **`SCRUM-402` (`W7-M2-02` · P0 · 10h · E4)**: **Kiểm tra Nhất quán Dữ liệu, Concurrency & Xử lý Tranh chấp**:

  Kiểm thử kịch bản hai bác sĩ cùng mở một bệnh án và sửa kết luận cùng lúc $\rightarrow$ Xác nhận Optimistic Concurrency chặn bác sĩ thứ hai an toàn (`HTTP 409 Conflict`). Mở rộng kiểm tra concurrency tranh chấp claim job của Worker và đối soát credit của Billing.

* **`SCRUM-403` (`W7-M2-03` · P1 · 6h · E4)**: **Sửa Lỗi Analysis và Chốt Hồ sơ Thiết kế Kỹ thuật Backend**:

  Khắc phục các khiếm khuyết phát hiện trong kiểm thử E2E. Đồng bộ tài liệu thiết kế kiến trúc: ERD, Class Diagram, State Machine Diagram với mã nguồn và migration thực tế.



### 🟡 M4 — ĐÀO DUY QUÂN (Đánh Giá Cuối Mô Hình AI, Tối Ưu Hóa & Báo Cáo Khoa Học)

* **`SCRUM-407` (`W7-M4-01` · P0 · 8h · E5)**: **Đánh giá Nghiệm thu Cuối Mô hình AI: Phân vùng Frangi, Đái tháo đường & Đo chỉ số $AVR$**:

  Chạy đánh giá độc lập mô hình AI trên các tập kiểm thử đã khóa của 3 bộ dữ liệu (`CHASE_DB1`, `HRF`, `INSPIRE_AVR`). Báo cáo riêng biệt từng bộ chỉ số: Dice/IoU cho phân vùng mạch, AUC-ROC cho sàng lọc DR, và sai số trung bình (MAE) cho tỷ số $AVR$.

* **`SCRUM-408` (`W7-M4-02` · P0 · 10h · E5)**: **Tối ưu hóa Bộ nhớ AI Core & Xuất ONNX Runtime**:

  Đo kiểm hiệu năng ONNX Runtime trên CPU với các kiểu dữ liệu FP32/INT8 (không giả định FP16 luôn nhanh hơn trên CPU thông thường). Thiết lập cơ chế Warm-up mô hình khi container khởi động để triệt tiêu độ trễ của request đầu tiên.

* **`SCRUM-409` (`W7-M4-03` · P1 · 6h · E5)**: **Sửa Lỗi AI và Chốt Báo cáo Chuyên đề Khoa Học AI Y Tế**:

  Xử lý triệt để các trường hợp biên của ảnh đáy mắt (ảnh quá sáng, quá tối, ảnh không phải đáy mắt). Bảo đảm hệ thống trả về kết quả an toàn (`LOW_QUALITY / ABSTAIN`) thay vì cố đưa ra chẩn đoán sai lệch.



### 🟡 M5 — LÂM TRẦN NGUYỄN HOÀNG (Tối Ưu Giao Diện, Accessibility & Kiểm Thử UI E2E)

* **`SCRUM-410` (`W7-M5-01` · P0 · 8h · E12)**: **Hoàn thiện Khả năng Tiếp cận (Accessibility WCAG 2.1) & Responsive Toàn Web**:

  Rà soát chuẩn WCAG 2.1 AA (điều hướng bàn phím, độ tương phản màu sắc, aria-labels cho screen readers). Tinh chỉnh giao diện hoàn hảo trên Desktop, Tablet và Mobile. (AC: Điểm Google Lighthouse Accessibility đạt $\ge 92/100$ kèm kiểm thử thủ công).

* **`SCRUM-411` (`W7-M5-02` · P0 · 10h · E12)**: **Tối ưu hóa Trải nghiệm Người dùng & Tốc độ Tải trang (Web Vitals)**:

  Code-Splitting theo route, tối ưu hóa kích thước thumbnail hiển thị (giữ nguyên ảnh gốc phân tích y khoa). Bảo đảm nguyên tắc **NFR-14: Hành trình người dùng $\le 3$ click**. (AC: LCP $< 1.8\text{s}$, CLS $\approx 0$).

* **`SCRUM-412` (`W7-M5-03` · P1 · 6h · E13)**: **Kiểm thử Giao diện Hồi quy Playwright Tự động & Chốt Design Documentation**:

  Viết bộ test Playwright kiểm tra các hành trình chính: Đăng nhập $\rightarrow$ Bulk Upload $\rightarrow$ Xem kết quả $\rightarrow$ Doctor Review $\rightarrow$ Consultation Chat trên môi trường backend thật.



### 🟡 M6 — DUNG HOANG (Kiểm Thử Đa Tenant, Điều Phối QA & Cập Nhật Final RTM)

* **`SCRUM-404` (`W7-M6-04` · P0 · 8h · E13)**: **Điều phối Kiểm thử E2E & Hồi quy Toàn diện 4 Vai Trò (Regression Testing)**:

  Điều phối chạy kiểm thử tự động toàn diện trên cả 4 vai trò (Bệnh nhân, Bác sĩ, Clinic Admin, SuperAdmin). Phân biệt rõ kết quả giữa component test, service integration và full E2E trong báo cáo kiểm thử.

* **`SCRUM-406` (`W7-M6-05` · P1 · 6h · E13)**: **Cập nhật Ma Trận Đối Soát Yêu Cầu Đề Tài Cuối Cùng (Final RTM Matrix)**:

  Hoàn thiện bảng RTM ánh xạ chi tiết toàn bộ 39 FRs và 23 NFRs sang mã nguồn, endpoint API và mã test case chứng minh. Ghi nhận trung thực trạng thái thực tế kèm liên kết đến commit và test run.

* **`SCRUM-413` (`W7-M6-01` · P0 · 8h · E13)**: **Kiểm thử Bảo mật Đa Tenant & Cô Lập Dữ Liệu Tuyệt Đối (Multi-Tenancy Audit)**:

  Viết script kiểm tra cô lập dữ liệu: Truy cập chéo giữa 5 Clinic khác nhau cho các tài nguyên: hồ sơ bệnh nhân, chiến dịch tầm soát, ảnh chụp, báo cáo PDF và tin nhắn tư vấn.

* **`SCRUM-414` (`W7-M6-02` · P0 · 10h · E3)**: **Tối ưu hóa Profile, Quản trị Quyền & Xử lý Trạng thái Tài khoản**:

  Tối ưu hóa cache quyền người dùng trong Redis token cache để giảm tải DB. Khi tài khoản bị khóa hoặc vai trò thay đổi, cơ chế cache invalidation hoạt động tức thời trên toàn bộ các service instances.

* **`SCRUM-415` (`W7-M6-03` · P1 · 6h · E11)**: **Hoàn thiện Chính sách Quyền Riêng Tư (Privacy) & Tài Liệu Module**:

  Xác minh quy trình xuất dữ liệu cá nhân (Right to Access) và chính sách lưu trữ/ẩn danh hóa dữ liệu người bệnh. Hoàn thiện tài liệu thuyết minh phân hệ Identity & Profile.



---



## 6. SPRINT 8 — Release Candidate, Hồ Sơ Đồ Án & Kịch Bản Bảo Vệ Tốt Nghiệp

> **Thời gian**: 23/11/2026 – 29/11/2026

> **Dải mã Jira**: **`SCRUM-416` đến `SCRUM-433`** (18 Tasks)

> **Mục tiêu**: Đóng gói Release Candidate `v1.0.0-release`; Hoàn thiện Báo cáo Đồ án Toàn văn; Slide Thuyết trình 25 trang; Video Demo 1080p 60fps; Diễn tập Dry Run bảo vệ đồ án trước Hội đồng.



### Phân công Sprint 8 (18 Tasks):

* **`SCRUM-416` (`W8-M1-01` · P0 · 8h · E1)**: Đóng gói Docker Release Candidate `v1.0.0-release` độc lập (gắn commit hash, image digest) và kịch bản rollback an toàn trong 5 phút (M1).

* **`SCRUM-417` (`W8-M1-02` · P0 · 10h · E1)**: Bàn giao kịch bản cài đặt môi trường chuẩn trên máy trắng (Fresh Machine) $< 15$ phút có kiểm tra preflight và cấp phát secret tự động (M1).

* **`SCRUM-418` (`W8-M1-03` · P1 · 6h · E14)**: Hoàn thiện cuốn Software Architecture Document (SAD v1.0) theo mô hình 4+1 View Model và Release Notes chi tiết (M1).

* **`SCRUM-422` (`W8-M1-04` · P0 · 8h · E13)**: Chạy Smoke Test nghiệm thu bản Release trên toàn bộ hệ thống microservices, workers và hạ tầng phụ trợ; lập biên bản kỹ thuật (M1).

* **`SCRUM-424` (`W8-M1-05` · P1 · 6h · E14)**: Chuẩn bị kịch bản Demo Màn Thanh toán Sandbox, Live Chat và FCM Push trên 2 thiết bị thực tế, kèm video dự phòng (M1 phối hợp M5).

* **`SCRUM-419` (`W8-M2-01` · P0 · 8h · E4)**: Đóng băng mã nguồn Backend, kiểm tra tính sạch sẽ và toàn vẹn của chuỗi database migrations trên database mới hoàn toàn (M2).

* **`SCRUM-420` (`W8-M2-02` · P0 · 10h · E14)**: Hoàn thiện cuốn Thuyết minh Kỹ thuật phân hệ Phân tích, Hàng đợi RabbitMQ và Báo cáo Y khoa QuestPDF (M2).

* **`SCRUM-421` (`W8-M2-03` · P1 · 6h · E6)**: Chuẩn bị kịch bản & bộ dữ liệu mẫu 100 ảnh đáy mắt đa dạng có bản quyền hợp lệ phục vụ Màn Demo Xử lý Hàng loạt (M2).

* **`SCRUM-423` (`W8-M2-04` · P0 · 10h · E14)**: Hoàn thiện chương Báo cáo Phân hệ Tài chính, Thông báo Thời gian thực và Consultation Chat (M2 phối hợp M1).

* **`SCRUM-425` (`W8-M4-01` · P0 · 8h · E5)**: Đóng gói Artifacts mô hình AI (weights ONNX, scripts benchmark) và hướng dẫn tái lập kết quả khoa học; lưu ý quy định bản quyền không tái phân phối dữ liệu gốc của University of Iowa và FAU (M4).

* **`SCRUM-426` (`W8-M4-02` · P0 · 10h · E14)**: Hoàn thiện Báo cáo Nghiệm thu Thuật toán và bản in Model Card v2.0 đưa vào cuốn báo cáo đồ án (M4).

* **`SCRUM-427` (`W8-M4-03` · P1 · 6h · E5)**: Chuẩn bị bài thuyết trình 3 phút giải thích giải thuật Frangi, sàng lọc DR và đo chỉ số $AVR$ dựa trên bằng chứng khoa học thực nghiệm (M4).

* **`SCRUM-428` (`W8-M5-01` · P0 · 8h · E12)**: Đóng gói bản build Production Web UI tối ưu hóa (Nginx serve thư mục `dist/` có cấu hình SPA fallback và security headers) (M5).

* **`SCRUM-429` (`W8-M5-02` · P0 · 10h · E14)**: Thiết kế Bộ Slide Thuyết trình Báo cáo Tốt nghiệp (20–25 slide chuẩn y tế, nêu rõ cả kết quả đạt được và giới hạn nghiên cứu) (M5).

* **`SCRUM-430` (`W8-M5-03` · P1 · 6h · E12)**: Dựng Video Demo Dự phòng 1080p 60fps chất lượng cao có lồng tiếng thuyết minh rõ ràng cho toàn bộ 5 kịch bản (M5).

* **`SCRUM-431` (`W8-M6-01` · P0 · 10h · E14)**: Tổng hợp, biên tập và in ấn Cuốn Báo cáo Đồ án Toàn văn Tốt nghiệp theo đúng chuẩn quy định của Nhà trường (M6).

* **`SCRUM-432` (`W8-M6-02` · P0 · 8h · E14)**: Lập Biên bản nghiệm thu sản phẩm phần mềm và bảng điểm đánh giá đóng góp cá nhân của từng thành viên dựa trên bằng chứng công việc thực tế (M6).

* **`SCRUM-433` (`W8-M6-03` · P1 · 6h · E3)**: Chuẩn bị kịch bản Demo Phân quyền Đa Tenant, Phê duyệt Clinic và Quản trị Danh tính (M6).



---



## 7. Kịch Bản 5 Màn Demo Đỉnh Cao Trước Hội Đồng (15 Phút)



```

┌────────────────────────────────────────────────────────────────────────────────────────┐

│                              5 MÀN TRÌNH DIỄN KỸ THUẬT AURA                             │

├─────────┬───────────────────────────────────┬──────────────────────────────────────────┤

│ Màn 1   │ Tiếp nhận Camera qua Apache NiFi  │ • Tiếp nhận ảnh camera công nghiệp tự động│

│ (3 phút)│ (Enterprise Ingestion & Chaos HA) │ • Demo chống trùng SHA-256 (Deduplication)│

│ (M1 phụ │                                   │ • Cách ly ảnh hỏng cấu trúc (Quarantine) │

│  trách) │                                   │ • Đóng băng DB (docker pause) & Phục hồi │

├─────────┼───────────────────────────────────┼──────────────────────────────────────────┤

│ Màn 2   │ Xử lý Hàng loạt & AI Lâm sàng     │ • Gửi Batch 100 ảnh từ giao diện Web.    │

│ (3 phút)│ (Distributed Batch & Clinical AI) │ • 4 Workers RabbitMQ cào xử lý song song.│

│ (M2, M4 │                                   │ • Thuật toán Frangi trích xuất mạch máu  │

│  phụ tr)│                                   │ • Sàng lọc Đái Tháo Đường & Đo tỷ số AVR │

├─────────┼───────────────────────────────────┼──────────────────────────────────────────┤

│ Màn 3   │ Không gian Bác sĩ & Báo cáo PDF   │ • So sánh ảnh gốc, mask A/V & Grad-CAM.  │

│ (3 phút)│ (Doctor Review Workspace)         │ • Bác sĩ thẩm định & lưu Revision        │

│ (M2, M5 │                                   │ • Kết xuất Bệnh án y khoa PDF chuẩn trang│

│  phụ tr)│                                   │   A4 và xuất file ZIP hàng loạt.         │

├─────────┼───────────────────────────────────┼──────────────────────────────────────────┤

│ Màn 4   │ Thanh toán Ví & Tư vấn Trực tuyến │ • Nạp tiền Sandbox có verify chữ ký HMAC │

│ (3 phút)│ (Sandbox Billing & Live Chat)     │ • Trừ cước an toàn Idempotent Ledger.    │

│ (M1, M2,│                                   │ • Chat thời gian thực Bác sĩ - Bệnh nhân │

│  M5 p.tr│                                   │ • Nhận thông báo đẩy FCM Web Push tức thì│

├─────────┼───────────────────────────────────┼──────────────────────────────────────────┤

│ Màn 5   │ Giám sát Toàn diện & Quản trị     │ • Trực quan hóa toàn bộ chỉ số RED       │

│ (3 phút)│ (Full-Stack Observability & Admin)│ • Giám sát hàng đợi RabbitMQ & Grafana   │

│ (M1, M6 │                                   │ • Quản trị duyệt Clinic & Audit Log.     │

│  phụ tr)│                                   │ • Kích hoạt cảnh báo tự động khi quá tải │

└─────────┴───────────────────────────────────┴──────────────────────────────────────────┘

```



---



## 8. Ma Trận Tóm Tắt 72 Task & Điều Chỉnh Kỹ Thuật (Master Backlog)



| Sprint | Mã Kế Hoạch | Dự Kiến Jira | Owner | Ước Lượng | Tên Task Chuẩn Hóa | Điều Chỉnh Kỹ Thuật Cốt Lõi Từ Master Review |

|:---:|:---:|:---:|:---:|:---:|---|---|

| **S5** | W5-M1-01 | SCRUM-362 | M1 | 8h | **[HOÀN THÀNH]** Scaling Worker RabbitMQ & Quota tài nguyên | Đã scale 4 worker replicas, quota 1.5 CPU / 1GB RAM, Gateway 100MB, unit tests pass 360/360 |

| **S5** | W5-M1-02 | SCRUM-363 | M1 | 10h | **[HOÀN THÀNH]** NiFi: Dedup SHA-256, Quarantine & Retry | Đã test 5/5 PASS: Idempotent vs 23505 HOLD, Retry Backoff 5s, Quarantine 40ms, Zero-Loss |

| **S5** | W5-M1-03 | SCRUM-364 | M1 | 6h | **[HOÀN THÀNH]** Dịch vụ Thông báo Đẩy Firebase FCM | Multicast 53ms (< 200ms), dọn dẹp Unregistered token, zero-PII SRS §7.5 (37/37 checks pass) |

| **S5** | W5-M1-04 | SCRUM-365 | M1 | 8h | **[HOÀN THÀNH]** Billing cấp tổ chức (Clinic Packages) | Gói 500 & 5.000 ảnh, phân quyền ClinicAdmin, trigger bảo vệ snapshot, chống IDOR (88/88 checks pass) |

| **S5** | W5-M1-05 | SCRUM-366 | M1 | 10h | **[HOÀN THÀNH]** Đối soát & Tự động Hoàn Credit Batch | 100 ảnh / 5 lỗi -> hoàn 5 credits, sổ cái sai số = 0, chống 40P01 deadlock, chống double spending (111/111 checks pass) |

| **S5** | W5-M2-01 | SCRUM-365 | M2 | 8h | Batch Processing Engine ($\ge 100$ ảnh) | Chốt 1 schema batch; Redis là cache có thể dựng lại từ DB; partial failure |

| **S5** | W5-M2-02 | SCRUM-366 | M2 | 10h | Xuất Báo cáo Y khoa PDF QuestPDF & CSV | Lấy đúng version AI và Doctor Revision; chống CSV Formula Injection |

| **S5** | W5-M2-03 | SCRUM-367 | M2 | 6h | Kiểm thử Batch/Report & OpenAPI | Ghi nhận số liệu test thực tế; cập nhật OpenAPI canonical contract |

| **S5** | W5-M2-04 | SCRUM-370 | M2 | 6h | Kiểm thử Tích hợp Batch-Credit & Đối soát | Worker không gọi trực tiếp Billing; Analysis điều phối qua domain event |

| **S5** | W5-M4-01 | SCRUM-371 | M4 | 8h | Tối ưu Frangi 2D & Benchmark trên HRF | Cố định ảnh, FOV, dtype; đo p50/p95 latency và peak RAM thực tế |

| **S5** | W5-M4-02 | SCRUM-372 | M4 | 10h | Huấn luyện Sàng lọc DR trên tập HRF | Định nghĩa chuẩn là Sàng lọc Nhị phân; patient-level split chống leakage |

| **S5** | W5-M4-03 | SCRUM-373 | M4 | 6h | Pipeline Phân đoạn A/V & Đo $AVR$ (INSPIRE) | CRAE/CRVE quanh đĩa thị; cho phép `RISK_UNAVAILABLE`; bỏ ép cứng AVR |

| **S5** | W5-M5-01 | SCRUM-374 | M5 | 8h | Giao diện Bulk Upload ($\ge 100$ ảnh) | Phân đoạn upload có concurrency limit; không gửi 1 multipart 1GB |

| **S5** | W5-M5-02 | SCRUM-375 | M5 | 10h | Giao diện Theo dõi Tiến độ Batch & Báo cáo | Polling có backoff cho MVP; chặn retry ca chưa rõ kết quả tránh trừ kép |

| **S5** | W5-M5-03 | SCRUM-376 | M5 | 6h | Kiểm thử Giao diện Batch & Xử lý Mất mạng | Test F5 và mất mạng; không ghi dữ liệu bệnh nhân vĩnh viễn vào LocalStorage |

| **S5** | W5-M6-01 | SCRUM-377 | M6 | 8h | Giao diện Quản lý Thành viên Clinic | M6 chịu backend/policy, M5 chịu components UI; M1 giữ Firebase Auth |

| **S5** | W5-M6-02 | SCRUM-378 | M6 | 10h | Phân hệ Chiến dịch Tầm soát (Campaign API) | Campaign thuộc bounded context Analysis; Profile không ghi trực tiếp DB |

| **S5** | W5-M6-03 | SCRUM-379 | M6 | 6h | Kiểm thử Campaign Đa Tenant & Tài liệu | Test HTTP thật qua Gateway; kiểm tra Positive Control trước Negative Control |

| **S6** | W6-M1-01 | SCRUM-380 | M1 | 8h | Audit Log Tập trung & Bảo vệ Privacy | Serilog Redaction che giấu token/PHI; quản lý quyền xem audit log |

| **S6** | W6-M1-02 | SCRUM-381 | M1 | 10h | Camera Flow & Service Principal (NiFi) | Cấu hình Client Credentials cho Camera; kiểm tra thu hồi token tức thời |

| **S6** | W6-M1-03 | SCRUM-382 | M1 | 6h | Kiểm thử TLS & Chốt ADR-005 Camera Principal | Test TLS và offline buffering của NiFi; hoàn thiện tài liệu ADR |

| **S6** | W6-M1-04 | SCRUM-387 | M1 | 10h | Cổng Quản trị Gói Cước & Mẫu FCM Admin | Versioning giá gói; loại bỏ hoàn toàn PHI trong nội dung push FCM |

| **S6** | W6-M2-01 | SCRUM-383 | M2 | 8h | API Thống kê Y tế & Hoạt động Clinic | Thống kê loại trừ ảnh test; tách phân bố nhóm nghiên cứu khỏi dịch tễ dân số |

| **S6** | W6-M2-02 | SCRUM-384 | M2 | 10h | Động cơ Cảnh báo & Xuất Nghiên cứu Ẩn danh | Tách alert lâm sàng (cần thẩm định) và export ẩn danh (cần bảo mật) |

| **S6** | W6-M2-03 | SCRUM-385 | M2 | 6h | Kiểm thử Thống kê & Tài liệu Công thức | Kiểm tra phân loại riêng cho các ca `RISK_UNAVAILABLE` |

| **S6** | W6-M2-04 | SCRUM-386 | M2 | 8h | Backend Tư vấn Trực tuyến (SignalR Hub) | Kiểm tra quyền Doctor-Patient Assignment liên tục trên từng tin nhắn |

| **S6** | W6-M4-01 | SCRUM-389 | M4 | 8h | Tích hợp DR & $AVR$ vào AI Core API | Giữ tương thích schema v1.3; trả về `available: false` khi không đo được |

| **S6** | W6-M4-02 | SCRUM-390 | M4 | 10h | Tải lại Cấu hình AI Động (Dynamic Reload) | Kiểm tra xác thực SuperAdmin; không nhận đường dẫn tùy tiện từ bên ngoài |

| **S6** | W6-M4-03 | SCRUM-391 | M4 | 6h | Đóng gói Model Card v2.0 Toàn diện | Tách riêng chỉ số cho từng bài toán; nêu rõ ranh giới hỗ trợ quyết định (CDS) |

| **S6** | W6-M5-01 | SCRUM-392 | M5 | 8h | Giao diện Quản trị Hệ thống (Admin Portal) | Phân quyền SuperAdmin nghiêm ngặt; lưu audit log mọi thao tác đổi cấu hình |

| **S6** | W6-M5-02 | SCRUM-393 | M5 | 10h | Giao diện Chat Bác sĩ - Bệnh nhân (SignalR) | Auto-reconnect; hiển thị rõ trạng thái mạng khi mất kết nối WebSocket |

| **S6** | W6-M5-03 | SCRUM-394 | M5 | 6h | Nâng cấp Doctor Workspace (Layer A/V & ICDR) | Chuyển đổi mượt các lớp ảnh; ghi nhận Revision Tracking khi Bác sĩ duyệt |

| **S6** | W6-M6-01 | SCRUM-395 | M6 | 8h | API Quản trị Tài khoản & Phê duyệt Clinic | Invariant: Chống SuperAdmin tự thu hồi quyền của mình hoặc xóa tài khoản gốc |

| **S6** | W6-M6-02 | SCRUM-396 | M6 | 10h | Giao diện Phê duyệt Clinic & Quản trị User | Bảng duyệt Clinic kèm giấy phép; tìm kiếm người dùng an toàn |

| **S6** | W6-M6-04 | SCRUM-388 | M6 | 6h | Kiểm thử Tích hợp Chat, Alert & Quản trị | Test thu hồi quyền ngắt kết nối WebSocket; tích hợp FCM khi offline |

| **S6** | W6-M6-03 | SCRUM-397 | M6 | 6h | Rà soát Phân quyền & CODE FREEZE Toàn Hệ Thống | Khóa mã nguồn tính năng; Sprint 7 và 8 chỉ làm verification và tài liệu |

| **S7** | W7-M1-01 | SCRUM-398 | M1 | 8h | Kiểm toán Bảo mật Toàn Stack (Security Audit) | Quét lỗ hổng Trivy, TLS Gateway, mã hóa lưu trữ; không để lọt secret |

| **S7** | W7-M1-02 | SCRUM-399 | M1 | 10h | Kiểm tra Backup, Restore & Diễn tập DR Drill | Dựng target restore riêng, không xóa container đang giữ dữ liệu kiểm thử; đo RTO/RPO |

| **S7** | W7-M1-03 | SCRUM-400 | M1 | 6h | Sửa Lỗi Hạ tầng, Pool & Chốt Runbook v2.0 | Tinh chỉnh Connection Pool; Runbook sử dụng đường dẫn tương đối |

| **S7** | W7-M1-04 | SCRUM-405 | M1 | 10h | Đo đạc NFR & Kiểm tra Resilience Hạ tầng | Đo đạc thực tế NFR-1, 2, 4; ghi nhận trung thực trạng thái PASS/FAIL |

| **S7** | W7-M2-01 | SCRUM-401 | M2 | 8h | Tối ưu hóa Analysis & Dashboard Queries | `EXPLAIN ANALYZE`, loại bỏ N+1 bằng `AsNoTracking()` và composite index |

| **S7** | W7-M2-02 | SCRUM-402 | M2 | 10h | Kiểm tra Nhất quán Dữ liệu & Concurrency | Optimistic Concurrency cho Doctor Review; kiểm tra race Worker và Billing |

| **S7** | W7-M2-03 | SCRUM-403 | M2 | 6h | Sửa Lỗi Analysis & Chốt Thiết kế Backend | Đồng bộ ERD, Class Diagram, State Machine với code và migration thật |

| **S7** | W7-M4-01 | SCRUM-407 | M4 | 8h | Đánh giá Nghiệm thu Cuối Mô hình AI | Báo cáo riêng metric từng task trên test set độc lập đã khóa |

| **S7** | W7-M4-02 | SCRUM-408 | M4 | 10h | Tối ưu Bộ nhớ AI Core & Xuất ONNX Runtime | Benchmark FP32 vs INT8 trên CPU; cơ chế Warm-up khi container khởi động |

| **S7** | W7-M4-03 | SCRUM-409 | M4 | 6h | Sửa Lỗi AI & Chốt Báo cáo Khoa Học | Xử lý ảnh biên (quá sáng/tối); fail-safe trả về `LOW_QUALITY / ABSTAIN` |

| **S7** | W7-M5-01 | SCRUM-410 | M5 | 8h | Hoàn thiện Accessibility WCAG 2.1 & Responsive | Kiểm thử thủ công bàn phím, tương phản; Lighthouse $\ge 92/100$ |

| **S7** | W7-M5-02 | SCRUM-411 | M5 | 10h | Tối ưu Trải nghiệm & Tốc độ Tải (Web Vitals) | Code-splitting; LCP $< 1.8\text{s}$; bảo toàn ảnh gốc y khoa phân tích |

| **S7** | W7-M5-03 | SCRUM-412 | M5 | 6h | Kiểm thử Giao diện Playwright & Chốt Design Doc | Chạy test Playwright trên backend thật qua Gateway; báo cáo pass/fail |

| **S7** | W7-M6-04 | SCRUM-404 | M6 | 8h | Điều phối Kiểm thử E2E 4 Vai Trò (Regression) | Phân biệt rõ component test, service integration và full E2E trong báo cáo |

| **S7** | W7-M6-05 | SCRUM-406 | M6 | 6h | Cập nhật Ma Trận Đối Soát Yêu Cầu (Final RTM) | Ánh xạ chi tiết 39 FRs và 23 NFRs; liên kết commit hash và test run thực tế |

| **S7** | W7-M6-01 | SCRUM-413 | M6 | 8h | Kiểm thử Bảo mật Đa Tenant (Multi-Tenancy) | Kiểm thử cô lập dữ liệu trên 5 Clinic; chặn IDOR chéo tenant |

| **S7** | W7-M6-02 | SCRUM-414 | M6 | 10h | Tối ưu Profile, Cache Quyền & Trạng thái User | Invalidation cache quyền người dùng tức thời khi tài khoản bị khóa |

| **S7** | W7-M6-03 | SCRUM-415 | M6 | 6h | Hoàn thiện Quyền Riêng Tư (Privacy) & Tài Liệu | Quy trình xuất dữ liệu cá nhân (Right to Access) và chính sách ẩn danh hóa |

| **S8** | W8-M1-01 | SCRUM-416 | M1 | 8h | Đóng gói Docker Release Candidate v1.0.0 | Gắn commit hash và image digest; kịch bản rollback an toàn trong 5 phút |

| **S8** | W8-M1-02 | SCRUM-417 | M1 | 10h | Bàn giao Môi trường Chuẩn trên Fresh Machine | Cài đặt $< 15$ phút trên máy trắng; kiểm tra preflight và cấp secret tự động |

| **S8** | W8-M1-03 | SCRUM-418 | M1 | 6h | Hoàn thiện SAD v1.0 (4+1 View) & Release Notes | Cập nhật sơ đồ kiến trúc 4+1 View Model và Release Notes theo đúng source |

| **S8** | W8-M1-04 | SCRUM-422 | M1 | 8h | Smoke Test Nghiệm thu Release trên Toàn Stack | Chạy Smoke Test toàn diện các microservices; lập biên bản kỹ thuật |

| **S8** | W8-M1-05 | SCRUM-424 | M1 | 6h | Chuẩn bị Demo Thanh toán, Live Chat & FCM Push | Demo trên 2 thiết bị thực tế; chuẩn bị video dự phòng không để lộ bí mật |

| **S8** | W8-M2-01 | SCRUM-419 | M2 | 8h | Đóng băng Backend & Kiểm tra Migration Sạch | Kiểm tra tính sạch sẽ và toàn vẹn của migrations trên database mới hoàn toàn |

| **S8** | W8-M2-02 | SCRUM-420 | M2 | 10h | Thuyết minh Kỹ thuật Analysis, RabbitMQ & PDF | Hoàn thiện thuyết minh kỹ thuật phân hệ xử lý ảnh và kết xuất bệnh án QuestPDF |

| **S8** | W8-M2-03 | SCRUM-421 | M2 | 6h | Chuẩn bị Dữ liệu Mẫu 100 Ảnh Đáy Mắt Demo | Bộ ảnh mẫu 100 ảnh hợp lệ có manifest/hash; tách biệt khỏi test set AI |

| **S8** | W8-M2-04 | SCRUM-423 | M2 | 10h | Báo cáo Phân hệ Tài chính, Notification & Chat | Tổng hợp báo cáo kỹ thuật dựa trên log event và ledger đối soát thực tế |

| **S8** | W8-M4-01 | SCRUM-425 | M4 | 8h | Đóng gói Artifacts Mô hình AI & Tái Lập | Đóng gói weights ONNX và script tái lập; tuân thủ bản quyền FAU và Iowa |

| **S8** | W8-M4-02 | SCRUM-426 | M4 | 10h | Báo cáo Nghiệm thu Thuật toán & Model Card v2.0 | Bản in Model Card v2.0 đưa vào phụ lục báo cáo tốt nghiệp |

| **S8** | W8-M4-03 | SCRUM-427 | M4 | 6h | Bài Thuyết trình 3 Phút Thuật Toán AI | Trình bày giải thuật Frangi, sàng lọc DR và AVR dựa trên số liệu thực nghiệm |

| **S8** | W8-M5-01 | SCRUM-428 | M5 | 8h | Đóng gói Production Web UI (Nginx dist/) | Nginx SPA fallback, security headers và cấu hình runtime API |

| **S8** | W8-M5-02 | SCRUM-429 | M5 | 10h | Bộ Slide Thuyết trình Tốt Nghiệp (20–25 slide) | Slide Canva/PowerPoint chuẩn y tế, phản ánh trung thực kết quả và giới hạn |

| **S8** | W8-M5-03 | SCRUM-430 | M5 | 6h | Dựng Video Demo Dự phòng 1080p 60fps | Video demo 5 kịch bản có lồng tiếng thuyết minh rõ ràng |

| **S8** | W8-M6-01 | SCRUM-431 | M6 | 10h | Cuốn Báo cáo Đồ án Toàn văn Tốt nghiệp | Tổng hợp, biên tập và in ấn theo quy định của Trường Đại học GTVT |

| **S8** | W8-M6-02 | SCRUM-432 | M6 | 8h | Biên bản Nghiệm thu & Bảng Điểm Đóng Góp | Biên bản gắn commit hash/test run; đánh giá dựa trên bằng chứng thực tế |

| **S8** | W8-M6-03 | SCRUM-433 | M6 | 6h | Chuẩn bị Demo Đa Tenant, Phê duyệt & Identity | Kịch bản demo phân quyền allow/deny/revoke trên 2 phòng khám khác nhau |
