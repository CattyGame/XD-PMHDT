# M1 Sprint 7 — runbook v2

Jira thật: SCRUM-398 (security), 399 (DR), 400 (capacity), 401 (resilience). Không dùng mã từ kế hoạch cũ để thay quyết định trên Jira. Profile này dùng nghiên cứu/Sandbox TEST, không phải triển khai lâm sàng hay thanh toán production.

## Trạng thái kiểm chứng ngày 23/11/2026

Đây vẫn là candidate, chưa nghiệm thu toàn bộ Sprint 7. Người vận hành chưa xác nhận BitLocker/Device Encryption của ổ dữ liệu; không thay bằng bằng chứng mã hóa backup. Phép đo resolver trên máy local, 3 lượt × 3.000 mẫu sau warm-up, có p95 gộp 6,4936 ms, chưa đạt mục tiêu dưới 5 ms. Giữ nguyên quyền hiện hành, không cache kết quả cho phép để hạ số đo.

Lượt 100 ảnh sau sửa worker đã có 55 MEASURED, 25 UNAVAILABLE và 20 QUALITY_FAILURE, không có PIPELINE_FAILURE; đủ 100 reservation đã đối soát. Tuy nhiên, lượt admission đã ghi nhận worker 206,8 MiB/256 MiB (80,79%), vượt gate RAM 80%. Kết quả PASS của cửa sổ resume chỉ chứng minh hội tụ và đối soát trong cửa sổ đó; không xóa lỗi RAM hoặc thay thế benchmark liên tục từ đầu. Lượt bốn worker phải có hồ sơ riêng, cùng bytes/model/threads, và ghi rõ mọi thay đổi cấu hình hoặc tải nền.

## Chuẩn bị cấu hình

Giữ khóa Firebase, notification encryption key, service registry/private key và mật khẩu runtime bên ngoài Git. Dùng `scripts/dev/Set-AuraDevEnvironment.ps1` trong cùng cửa sổ PowerShell; cấp LOGIN/password cho các role bằng quy trình provision hiện có. Broker dùng `infra/rabbitmq/m1.topology.json` và tài khoản riêng cho publisher/worker/Billing/Notification. Không dùng superuser DB trong ứng dụng.

Đặt riêng `ANALYSIS_PUBLISHER_DB_PASSWORD`, `ANALYSIS_PUBLISHER_RABBITMQ_PASSWORD`, `AURA_PIPELINE_START_UTC` cho lần chạy được phép. Thời điểm bắt đầu publisher phải bao phủ workload cần xử lý; không kéo lùi để vô tình phát lại fixture cũ.

Thứ tự Compose chuẩn: `compose.yaml`, `compose.dev.yaml`, `compose.adr005.yaml`, `compose.workers.yaml`, `compose.pipeline.yaml`, cuối cùng `compose.capacity.yaml`. Nếu cần V2 nghiên cứu, thêm `compose.research-avr.yaml` và `compose.research-integrated.yaml` trước capacity. Profile integrated mặc định một worker để phù hợp một AI slot; thử bốn worker phải override replica rõ và ghi cấu hình thực tế.

```powershell
$files = @('compose.yaml','compose.dev.yaml','compose.adr005.yaml','compose.workers.yaml','compose.pipeline.yaml','compose.capacity.yaml')
$dc = @('--project-directory','.')
foreach ($f in $files) { $dc += @('-f',$f) }
# Render chứa secret: lưu vào thư mục riêng bên ngoài Git, không in toàn bộ ra log.
docker compose @dc config --format json | Set-Content "$env:USERPROFILE/.aura/capacity-resolved.json" -Encoding utf8
$dockerMemory = docker info --format '{{.MemTotal}}'
python scripts/dev/capacity_budget.py "$env:USERPROFILE/.aura/capacity-resolved.json" --host-memory-bytes $dockerMemory --output "$env:USERPROFILE/.aura/capacity-evidence.json"
```

Chỉ khởi động khi budget đạt và đủ RAM vật lý cho tổng các memory limit cùng headroom hệ điều hành. Không chạy `down -v` hoặc xóa volume môi trường nhóm để diễn tập.

Máy local có thể thử thêm `-f compose.capacity.local.yaml` cuối cùng và `--memory-headroom-bytes 536870912`. Docker engine đã đo có khoảng 5,65 GiB RAM khả dụng. Tổng cap toàn stack với bốn worker là 5.056 MiB, cộng 512 MiB headroom; AI có 2.304 MiB, mỗi worker 256 MiB. Đây là cấu hình ứng viên cần chạy tải thật, không phải kết luận đã đạt; không dùng cho production. Nếu một container vượt 80% cap/OOM, ghi FAIL và giữ đầy đủ kết quả/ledger để chẩn đoán; việc thu thập tiếp không được biến lượt chạy thành PASS.

Profile local dành cho máy đã đo **12 logical CPU**, cấp AI 8 CPU / 8 OMP-MKL threads và một inference slot. Một ảnh warm thử nghiệm đạt khoảng 18 giây; đó chưa phải p95 workload. Lưu artifact đã đối chiếu SHA-256 vào local Linux volume và mount read-only để tránh đọc lại hàng trăm MB qua Windows bind mount; vẫn xác minh hash ở mỗi inference, không cache kết quả ảnh. So sánh một/bốn worker phải giữ nguyên data/model/hardware/thread cấu hình này và chạy tuần tự.

Ứng viên tiếp theo giới hạn managed heap của Analysis API và worker ở 55% cap (`DOTNET_GCHeapHardLimitPercent=0x37`), dành phần còn lại cho bộ đệm HTTP/storage và native memory. Biến môi trường GC dùng số hexadecimal theo [tài liệu .NET](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit-percent). Phải xác nhận giá trị hiệu lực và chạy lại tải thật; tổng memory limit không thay đổi. Worker tái sử dụng tối đa hai JWT dịch vụ theo audience/scope, làm mới trước hết hạn 30 giây và khi secret thay đổi; mỗi lần kiểm quyền vẫn gọi Profile hiện hành. Không coi các lượt dùng image cũ là nghiệm thu cho thay đổi này.

## Budget thực tế theo cấu hình

- Năm API có pool tối đa 16/API: 80 kết nối. Analysis outbox là **data source khác**, thêm 8.
- Bốn worker × 12: 48. Tổng 136, cộng 16 headroom vận hành = 152, nhỏ hơn `max_connections=160`.
- Overlay capacity cuối cùng đặt `AURA_WORKER_REPLICAS=4` mặc định; dùng `1` cho phép đo đối chứng, rồi render/check lại budget. Overlay research riêng vẫn mặc định một worker để chạy độc lập an toàn.
- Retry/dead-letter của worker dùng `aura.s3.retry.jobs` / `aura.s3.dead.jobs`, routing key `jobs`. Handoff dùng channel publish riêng, mandatory và confirm trước ACK. Mỗi worker có thêm tối đa một channel publish ngắn hạn khi chuyển retry/dead-letter.
- Pool tối thiểu 0. Upload đồng thời 4, pool Analysis 16 đáp ứng `2 × capacity + 4`. Không cache quyền cho phép để đạt latency.
- Mỗi worker một consumer/channel có ACK, prefetch được chặn; đo số consumer thật bằng `Test-RabbitMqScaling.ps1`. Một AI slot có thể làm bốn worker chờ/429; không hứa tăng tốc tuyến tính.
- Reporting NiFi là DB riêng; không cộng vào PostgreSQL nghiệp vụ và không cho writer ghi chéo schema. Nếu triển khai Reporting hoặc NiFi, tính thêm RAM và pool riêng theo `camera_tls_runbook.md`.

## Security

```powershell
python -m unittest discover -s scripts/release -p test_security_gate.py
python scripts/release/security_gate.py --source . --output "$env:USERPROFILE/.aura/source-security.json"
dotnet list AURA.sln package --vulnerable --include-transitive --format json | Set-Content "$env:USERPROFILE/.aura/nuget-vulnerabilities.json" -Encoding utf8
python scripts/release/security_gate.py --source . --nuget-report "$env:USERPROFILE/.aura/nuget-vulnerabilities.json" --output "$env:USERPROFILE/.aura/source-and-nuget-security.json"
# Xuất từng image đúng candidate; không quét tag cũ rồi coi là image mới.
docker image save aura-analysis:candidate -o "$env:USERPROFILE/.aura/analysis-candidate.tar"
python scripts/release/security_gate.py --source . --image-archive "$env:USERPROFILE/.aura/analysis-candidate.tar" --output "$env:USERPROFILE/.aura/candidate-security.json"
$imageId = docker image inspect aura-analysis:candidate --format '{{.Id}}'
python scripts/release/security_gate.py --source . --image $imageId --output "$env:USERPROFILE/.aura/local-image-security.json"
```

Scanner được pin digest. Chỉ loại trừ hằng số công khai `devstoreaccount1` của [Azurite](https://github.com/Azure/Azurite#default-storage-account), không loại trừ toàn bộ Azure key. High/Critical còn lại phải sửa hoặc có exception cụ thể: ID, target, package/version, lý do, owner, expiry. `PASS_WITH_EXCEPTIONS` vẫn trả exit code khác 0 để người nghiệm thu xem riêng. Scan lỗi/mất kết quả không được coi PASS.

TLS camera kiểm theo `camera_tls_runbook.md`: CA tin cậy + hostname, từ chối CA lạ/hostname sai/plain HTTP. JWT kiểm current/previous key trong thời gian rotation, từ chối expired/wrong-audience/foreign key. Không ghi token vào URL/log.

**At rest:** archive backup AES-256 + HMAC chỉ bảo vệ archive; không chứng minh PostgreSQL/Azurite volume đã mã hóa. Máy chạy dữ liệu cần volume được BitLocker/LUKS bảo vệ hoặc storage mã hóa tương ứng có bằng chứng từ máy thực. Docker network nội bộ không phải TLS nội bộ. Profile dev còn HTTP nội bộ phải được ghi rõ trong biên bản; không công bố security production PASS dựa vào profile này.

## DR và RPO

```powershell
pwsh scripts/backup/Test-BackupRecovery.ps1 -Container <postgres-fixture-riêng> -User aura_dev -TemplateDatabase aura_dev -Output "$env:USERPROFILE/.aura/recovery-evidence.json"
```

Fixture clone riêng; so toàn bộ count/hash bảng, bao gồm ledger/outbox/inbox, và hash 3 private assets ở cả target trống và target đã có dữ liệu. RTO tính cả tạo target/restore/đối soát, mục tiêu **<720 giây**. Wrong key/tamper/wrong target phải bị chặn. Role/khóa mã hóa phải được provision độc lập và còn giữ phiên bản khóa của backup.

RPO phải nêu điểm snapshot được bảo vệ và ghi đã ACK sau snapshot. Diễn tập cố tình thêm một ghi sau backup để thể hiện phần chưa được bảo vệ; không tuyên bố daily backup cho RPO=0. Muốn phục hồi các ghi đó cần WAL/PITR và đối soát private assets tương ứng. `MaxBackupAgeSeconds` là giới hạn tuổi backup; alias `MaxRpoSeconds` chỉ giữ tương thích, không biến tuổi backup thành RPO.

## Workload và phục hồi

Chạy `Test-BatchInfrastructureStress.ps1` với manifest 100 ảnh khác bytes, dữ liệu được phép sử dụng, gồm measured/unavailable/quality failure; cùng patient/clinic context được cấp quyền. Firebase token qua môi trường riêng, không đưa vào manifest. Upload 4–6 ảnh đồng thời, không gửi 100 ảnh trong một multipart. UNKNOWN giữ nghĩa vụ đối soát, không tự release do timeout.

Thêm `-RequireRealResults -AllowQualityFailures -RequireFinancialReconciliation`: chỉ cho phép lỗi chất lượng ảnh có mã rõ ràng; lỗi hạ tầng không được coi là quality failure. Harness lưu checkpoint `RUNNING`, hash script, thời gian quan sát; lần resume không đại diện thời gian từ admission. Đối chiếu timeline PostgreSQL riêng.

Lượt chạy dài có thể dùng `AURA_TEST_FIREBASE_TOKEN_FILE` trỏ tới JSON token riêng ngoài Git (`idToken`). Harness đọc lại ở mỗi request; tiến trình cấp token test phải thay file nguyên tử trước khi token hết hạn. Không ghi token/refresh token vào báo cáo hoặc CLI. Evidence cuối lưu image ID, cap CPU/RAM và ba biến thread được cho phép, không dump environment chứa secrets.

Trong fixture dùng riêng có label `aura.acceptance`, chạy `python scripts/dev/test_dependency_recovery.py --url http://127.0.0.1:<gateway> --batch-id <batch> --fixture-label <giá-trị-label> --postgres <container> --redis <container> --rabbitmq <container> --output <ngoài-Git>`. Script từ chối container sai label, phục hồi dependency trong `finally`, kiểm 503 khi PostgreSQL dừng, đọc bền vững khi Redis/broker dừng và dựng lại cache sau `FLUSHDB`. Đây không thay thế gate settlement/replay hoặc kiểm FCM provider.

Đo riêng cold/warm/queue/retry/429; ghi CPU/RAM, artifact hash, actual replicas, số mẫu và cửa sổ quan sát. Xóa Redis chỉ trong fixture, rồi đọc lại từ PostgreSQL. Ngắt broker/Redis/DB/FCM có khôi phục; kiểm ledger/inbox không tăng khi replay. Mất FCM không được làm mất inbox. Uptime trong cửa sổ thử không đại diện uptime năm; gate khoa học SCRUM-373 vẫn độc lập.
