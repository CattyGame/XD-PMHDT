# M1 Sprint 7 — runbook v2

Jira thật: SCRUM-398 (security), 399 (DR), 400 (capacity), 401 (resilience). Không dùng mã từ kế hoạch cũ để thay quyết định trên Jira. Profile này dùng nghiên cứu/Sandbox TEST, không phải triển khai lâm sàng hay thanh toán production.

## Trạng thái kiểm chứng ngày 23/11/2026

Đây vẫn là candidate, chưa nghiệm thu toàn bộ Sprint 7. Người vận hành xác nhận C/D đạt 100% mã hóa, Protection On và Secure Boot hoạt động; công cụ trong phiên chưa có quyền Administrator để thu lại trạng thái. Output trước đó ghi XTS-AES 128, chưa chứng minh yêu cầu AES-256 của NFR-9. Không thay bằng chứng volume bằng mã hóa backup. Sau sửa tái sử dụng token worker, phép đo resolver 3 lượt × 3.000 mẫu sau warm-up có p95 7,8713 / 7,0063 / 6,7479 ms, gộp 7,2862 ms; vẫn chưa đạt dưới 5 ms. Giữ nguyên quyền hiện hành, không cache kết quả cho phép để hạ số đo.

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

Máy local có thể thử thêm `-f compose.capacity.local.yaml` cuối cùng và `--memory-headroom-bytes 536870912`. Docker engine đã đo có khoảng 5,65 GiB RAM khả dụng. Tổng cap toàn stack với bốn worker là 5.248 MiB, cộng 512 MiB headroom; AI có 2.272 MiB, RabbitMQ 320 MiB, Analysis API 320 MiB, Billing 160 MiB, mỗi worker 296 MiB, Identity/Profile mỗi service 128 MiB. Cap được phân bổ lại theo các đỉnh đã đo: ứng viên trước vượt gate ở AI, RabbitMQ và Billing. Đây vẫn là cấu hình ứng viên cần chạy tải thật, không phải kết luận đã đạt; không dùng cho production. Nếu một container vượt 80% cap/OOM, ghi FAIL và giữ đầy đủ kết quả/ledger để chẩn đoán; việc thu thập tiếp không được biến lượt chạy thành PASS.

Redis giữ mật khẩu và cấu hình persistence/eviction hiện có, nhưng override `maxmemory` thành 16 MiB cho cap local 64 MiB (64 MiB cho cap chuẩn 256 MiB). File cấu hình chung có maxmemory 512 MiB nên không thể dùng nguyên với các cap này. Budget từ chối giới hạn cache thiếu/tốn quá nửa cap và hai giá trị `mem_limit`/`deploy.resources.limits.memory` mâu thuẫn; đây là chặn cấu hình sai, không thay kiểm thử peak RAM thực tế.

Profile local dành cho máy đã đo **12 logical CPU**, cấp AI 8 CPU / 8 OMP-MKL threads và một inference slot. Một ảnh warm thử nghiệm đạt khoảng 18 giây; đó chưa phải p95 workload. Lưu artifact đã đối chiếu SHA-256 vào local Linux volume và mount read-only để tránh đọc lại hàng trăm MB qua Windows bind mount; vẫn xác minh hash ở mỗi inference, không cache kết quả ảnh. So sánh một/bốn worker phải giữ nguyên data/model/hardware/thread cấu hình này và chạy tuần tự.

Ứng viên CPU affinity tách AI vào CPU 0–7, các service còn lại vào 8–11; client đo HTTP cũng dùng 8–11. Topology Docker đã đọc cho thấy hai nhóm không dùng chung cặp sibling. Mục đích là kiểm tra ảnh hưởng tranh chấp CPU tới tail latency; chưa coi p95 đạt cho đến khi đo lại. Chỉ dùng profile này trên host có đủ 12 CPU và lưu `cpusetCpus` thực tế trong evidence.

Thử nghiệm managed heap 55% trên cap 256 MiB đã gây `OutOfMemoryException` khi giải mã ảnh HRF; giữ báo cáo FAIL và không dùng cấu hình này để nghiệm thu. Ứng viên hiện tại giữ heap Analysis API/worker ở 192 MiB (`DOTNET_GCHeapHardLimit=0xC000000`) và Identity/Profile ở 96 MiB (`0x6000000`), dành phần cap còn lại cho HTTP/storage và native memory. Bỏ biến `DOTNET_GCHeapHardLimitPercent` trong fixture khi chuyển cấu hình. Biến môi trường GC dùng số hexadecimal theo [tài liệu .NET](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit). Phải kiểm tra heap hiệu lực, ảnh HRF và tải thật trước khi chấp nhận. Worker tái sử dụng tối đa hai JWT dịch vụ theo audience/scope, làm mới trước hết hạn 30 giây và khi secret thay đổi; mỗi lần kiểm quyền vẫn gọi Profile hiện hành. Không coi các lượt dùng image cũ là nghiệm thu cho thay đổi này.

## Budget thực tế theo cấu hình

- Năm API có pool tối đa 16/API: 80 kết nối. Analysis outbox là **data source khác**, thêm 8.
- Bốn worker × 12: 48. Tổng 136, cộng 16 headroom vận hành = 152, nhỏ hơn `max_connections=160`.
- Overlay capacity cuối cùng đặt `AURA_WORKER_REPLICAS=4` mặc định; dùng `1` cho phép đo đối chứng, rồi render/check lại budget. Overlay research riêng vẫn mặc định một worker để chạy độc lập an toàn.
- Retry/dead-letter của worker dùng `aura.s3.retry.jobs` / `aura.s3.dead.jobs`, routing key `jobs`. Handoff dùng channel publish riêng, mandatory và confirm trước ACK. Mỗi worker có thêm tối đa một channel publish ngắn hạn khi chuyển retry/dead-letter.
- Pool tối thiểu 0. Upload đồng thời 4, pool Analysis 16 đáp ứng `2 × capacity + 4`. Không cache quyền cho phép để đạt latency.
- Mỗi worker một consumer/channel có ACK, prefetch được chặn; đo số consumer thật bằng `Test-RabbitMqScaling.ps1`. Một AI slot có thể làm bốn worker chờ/429; không hứa tăng tốc tuyến tính.
- Profile capacity bật `AURA_AI_MAX_WAITERS=3`: một inference hoạt động và tối đa ba HTTP waiter FIFO, mỗi waiter chờ tối đa 120 giây; ngoài giới hạn vẫn trả 429/Retry-After. Mặc định ngoài profile này là 0 waiter. Handoff giữ slot cho waiter đầu hàng để worker vừa xong không giành lại trước; không tăng số inference chạy đồng thời hoặc sửa thuật toán. RabbitMQ/job lease vẫn giữ nghĩa vụ bền vững; hàng đợi HTTP không thay thế chúng. Invalid waiter config trả 503. Phải chạy lại workload trên image mới trước khi dùng số đo cũ làm nghiệm thu.
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

Đo đọc đồng thời bằng `python scripts/dev/reader_capacity.py --url <gateway-fixture> --analysis-ids <private-json-array-of-owned-analysis-ids> --postgres-container <fixture> --fixture-label <owned-label> --database <capacity-clone> --output <outside-git>`. Token lấy từ cùng biến môi trường/file riêng như harness batch. Mặc định 20 reader, 10 warm-up/reader và 100 request đo/reader; mỗi response phải trả đúng analysis ID được yêu cầu, lỗi 401/403/5xx không được tính thành công. Script kiểm tra số bản ghi DB tối thiểu 2.000 account và 10.000 analysis metadata. Dữ liệu nền tổng hợp phải ghi rõ là fixture, không phải 10.000 lượt AI thật; 20 reader dùng một principal được cấp quyền, không phải 20 tài khoản đăng nhập khác nhau. PASS của phép đọc chỉ xác nhận phạm vi này; p95, lỗi và kiểm quyền vẫn phải đối chiếu yêu cầu nghiệm thu riêng.

Chuẩn bị population bằng `scripts/dev/seed_reader_capacity.sql` trên bản sao PostgreSQL có tên `aura_m1_capacity_<8 ký tự hex>`. SQL từ chối tên DB khác, tạo 2.000 account/tenant/patient và 10.000 metadata REJECTED, không tạo file ảnh, kết quả AI hoặc quyết định bác sĩ. Đã kiểm tra chạy lặp không tăng số dòng và chặn DB sai trên PostgreSQL riêng. Dùng bản sao, hướng các API đọc tới bản sao đó trong thời gian đo và khôi phục cấu hình sau test; không đưa các metadata không có file vào diễn tập restore private assets hoặc đánh giá độ chính xác AI. Các request đo đọc các analysis thật đã có trong snapshot, không dùng metadata tổng hợp làm kết quả y khoa.
