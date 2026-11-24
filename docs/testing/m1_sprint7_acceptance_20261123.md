# M1 Sprint 7 — đối soát candidate ngày 23/11/2026

Nhánh `feat/m1-sprint7-completion`, PR #112. Commit mã nguồn được kiểm thử: `03021e3f1873de2129588c02da79f9fc5c52296e`. Chưa merge `develop`; chưa chuyển Jira sang Done. Đây là fixture nghiên cứu/Sandbox TEST, không phải chứng nhận triển khai production hoặc nghiệm thu khoa học SCRUM-373.

**Kết luận: chưa đủ điều kiện nghiệm thu toàn bộ M1 Sprint 7.** Phần ngày 23/11 dưới đây được bảo lưu như lịch sử; cập nhật 24/11 ở cuối ghi nhận profile TLS mới, RAM quan sát dưới 80% nhưng resolver và AES-256 vẫn chưa đạt. Không dùng kết quả mới để xóa các lần FAIL cũ.

| Task | Kết quả đã có | Điều kiện còn thiếu |
| --- | --- | --- |
| SCRUM-398 — security | Scan source/Python/NuGet và tám image chính xác PASS, không finding chặn hoặc exception; HTTPS gateway và các ca từ chối chứng chỉ/hostname/TLS 1.1 PASS | Chưa chứng minh AES-256 cho volume; profile dev còn HTTP nội bộ |
| SCRUM-399 — restore | PASS trên target trống và đã có dữ liệu, DB/private assets khớp snapshot; negative controls chặn key/tamper/target sai | Sẵn sàng review trong phạm vi fixture; không suy ra RPO=0 cho ghi sau backup |
| SCRUM-400 — capacity | Budget source/fixture và 20 reader PASS; không cache quyền cho phép | Resolver warm p95 trên 5 ms; worker dài hạn vượt gate RAM ở lượt bốn worker |
| SCRUM-401 — resilience | Cả hai lượt 100 ảnh terminal, 100 reservation đối soát, 0 retry job; dependency read recovery/cache/replay có bằng chứng riêng | Lượt bốn worker FAIL RAM; SLO kế thừa SCRUM-400 chưa đạt |

## Workload và cấu hình

- 100 input khác bytes, gồm 99 AVRDB và một HRF; không thay ảnh khó bằng số đo giả. Hai lượt dùng tài khoản/patient TEST riêng để tránh dedupe, giữ nguyên bytes/model/threads.
- Docker engine 12 logical CPU, RAM 6.061.568.000 bytes. AI CPU 0–7, service/client đo CPU 8–11. Một inference hoạt động và tối đa ba HTTP waiter FIFO; quá giới hạn vẫn 429/Retry-After, cấu hình sai trả 503.
- Tổng cap bốn worker 5.248 MiB, thêm 512 MiB headroom; worker 296 MiB và managed heap 192 MiB. Pool ứng dụng tối đa 136, cộng 16 headroom, dưới PostgreSQL `max_connections=160`.
- HRF prewarm thật trước workload: đủ bốn annotation, `isMock=false`, peak worker 165.322.752 bytes trong cap 310.378.496 bytes. Đây không phải bằng chứng process-cold; lần đầu quan sát và cold resolver được lưu riêng.

| Phép đo | Một worker FIFO | Bốn worker FIFO |
| --- | --- | --- |
| Trạng thái tổng thể harness | PASS | FAIL RAM, luồng và tài chính hội tụ |
| MEASURED / UNAVAILABLE / QUALITY_FAILURE / PIPELINE_FAILURE | 55 / 25 / 20 / 0 | 55 / 25 / 20 / 0 |
| Reservation đối soát / sai lệch | 100 / 0 | 100 / 0 |
| AI processing p95, 80 response có thời gian xử lý | 14.010,61 ms | 13.302,28 ms |
| Thời gian cửa sổ admission → terminal + settlement | 1.166,532 s | 1.111,360 s |
| Ảnh cần retry job | 0 | 0 |
| Peak RAM cao nhất / gate <80% | 78,57% / PASS | 81,06% / FAIL |

AI processing loại trừ queue/network, không phải thời gian chờ của người dùng. Worker elapsed p95 trên 100 job (gồm chất lượng) là 13.930,372 / 50.416,778 ms; bốn worker phải chờ một AI slot. Durable queue p95 là 968.113,124 / 883.317,371 ms trong batch 100 ảnh. Hai lượt giữ cùng AI container/image và cùng bytes; không suy tuyến tính 3,5 lần từ số worker.

AI HTTP của mỗi lượt có 80 response 200 và 20 response 422, không 429 hoặc 5xx. Gateway có lần lượt 22 / 17 phản hồi PUT 409 được harness retry đúng hợp đồng; không gọi đó là 0 lỗi HTTP. Worker số 1 được giữ xuyên hai lượt; ba worker mới có peak thấp hơn, nên không dùng số đo process mới để che đỉnh process dài hạn. Các counter `oom`/`oom_kill`, restart và runtime được lưu riêng; không có OOM kill. `null` AVR kèm lý do được giữ nguyên, không biến thành 0 hoặc nguy cơ thấp.

## Resolver và phép đọc đồng thời

Mỗi chế độ có ba lượt định trước: cold riêng, 500 warm-up rồi 3.000 mẫu đo/lượt. Mỗi quyết định vẫn đọc quyền hiện hành; chỉ JWT máy được tái sử dụng có giới hạn.

| Chế độ | Ba p95 warm (ms) | p95 gộp 9.000 mẫu (ms) | Gate <5 ms |
| --- | --- | --- | --- |
| Một worker | 7,7405 / 5,6361 / 5,6237 | 6,2863 | FAIL |
| Bốn worker | 5,2075 / 5,3453 / 5,9967 | 5,5221 | FAIL |

20 reader trên DB clone có 2.007 account và 10.700 metadata analysis: 2.000 request đo, 0 lỗi, p95 719,49 ms. Đây là một principal được cấp quyền với 20 reader; 2.000 account và 10.000 metadata nền được tạo tổng hợp, không phải 10.000 lượt AI hay nhãn bác sĩ. Clone đã được xóa, cấu hình và số bản ghi DB nguồn được phục hồi/đối chiếu không đổi.

## Restore, lỗi và security

- Restore snapshot 68 bảng và 2.044 private blob, 1.644 reference canonical, không lệch hash/size. Fresh RTO 134,799 s, populated RTO 113,893 s, dưới mục tiêu 720 s. Snapshot được bảo vệ không mất bản ghi; một ghi ACK sau backup được cố tình chứng minh chưa được bảo vệ. Dùng Azurite qua Azure SDK thật; fixture không có reference PDF, không thay cho kiểm định Azure cloud production.
- Dừng Redis/broker/PostgreSQL trong fixture: batch read vẫn 200 khi Redis/broker dừng; PostgreSQL dừng trả 503 fail closed. Phục hồi lần lượt 9,110 / 4,328 / 9,406 s. FLUSHDB rồi đọc PostgreSQL dựng lại hai key cache. Phạm vi này là dependency read recovery, không phải mọi tình huống in-flight hoặc uptime năm.
- Replay một event canonical thuộc batch FIFO bốn worker mới ba lần có publisher confirm và cả hai consumer nhận: tập ID ledger/inbox không đổi. Phép đo này độc lập với FAIL RAM, không đổi trạng thái benchmark. Provider FCM outage/restart được kiểm bằng dispatcher và job PostgreSQL thật với provider thử nghiệm; không gây outage Google FCM hoặc gửi push cho người thật.
- HTTPS test dùng certificate self-signed cục bộ được client thử nghiệm tin cậy rõ ràng, TLS 1.3 / `TLS_AES_256_GCM_SHA384`; CA lạ, hostname sai và TLS 1.1 bị từ chối. Không cài certificate vào Windows trust store; profile HTTP dev được phục hồi sau test.
- Người vận hành xác nhận C/D mã hóa 100%, Protection On. Output từng cung cấp ghi XTS-AES 128; công cụ chưa có quyền Administrator thu lại trạng thái. Không coi đó là bằng chứng AES-256, cũng không dùng archive backup AES-256 thay bằng chứng volume. Không tự decrypt/rekey ổ đĩa hoặc đổi tiêu chí.

## Kiểm chứng và hồ sơ

CI của commit code `03021e3`: cả năm workflow PASS. Regression đã đọc kết quả: Analysis 485, worker 51, M1 45, Billing 29; controls dev 35 và security 5 đều PASS. AI image regression: 265 PASS, ba SKIP yêu cầu dataset độc lập ngoài container, 11 deprecation warning. Ba SKIP không được tính thành PASS khoa học. Review tự thực hiện theo yêu cầu không subagent.

Evidence local: `D:/XD-PMHDT/.review-evidence/m1-sprint7-20261122/`. Các file cấu hình/token/private key ở thư mục riêng ngoài Git, không nằm trong báo cáo. Các lượt chẩn đoán FAIL RAM/timeout và báo cáo trước FIFO được giữ riêng; không thay cửa sổ đầy đủ bằng cửa sổ resume.

Các file chính: `final-verification-fifo.json`, `fifo-sequence.json`, `batch-one-clean.json`, `batch-four-clean.json`, `auth-fifo-{one,four}-{1,2,3}.json`, `timing-fifo-{one,four}.json`, `fifo-runtime-final.json`, `capacity-fifo-{source,fixture}-four.json`, `security-fifo-final.json`, `reader-capacity-live.json`, `reader-capacity-cleanup.json`, `gateway-tls-live.json`, `recovery-full-azure.json`, `dependency-recovery-affinity.json`, `event-replay-fifo.json`.

## Điều kiện để chốt các gate còn lại

1. Giữ một worker cho fixture local; không promote cap bốn worker hiện tại. Cần budget/deployment có thêm headroom cho worker dài hạn và đo lại toàn cửa sổ; không tăng cap bằng cách bỏ headroom hoặc thu nhỏ AI chưa kiểm chứng. Profile chuẩn cho host lớn hơn và profile local phải được phân biệt.
2. Đạt resolver <5 ms trên môi trường triển khai đã chốt, hoặc nhóm phê duyệt thay đổi SLO/phạm vi thành phiên bản có giải trình. Không cache quyền hiện hành hoặc lặp benchmark đến khi có một lần đạt.
3. Thu bằng chứng AES-256 cho nơi thực sự lưu Docker/DB/assets và TLS theo profile nghiệm thu, hoặc chốt ngoại lệ phạm vi minh bạch. Việc rekey/đổi mã hóa ổ đang sử dụng là quyết định vận hành riêng, không tự thực hiện từ phép kiểm thử này.
4. Chạy lại protocol tương ứng sau thay đổi, kiểm đủ terminal/settlement, retry, RAM và quyền hiện hành; review/merge/Done theo kết quả thật.

Chỉ số latency và security còn thiếu phải được sửa trên môi trường đáp ứng hoặc được nhóm phê duyệt thay đổi phạm vi có ghi nhận. Bản kiểm định này không tự miễn gate, không thay đổi nhãn y khoa và không công bố toàn bộ M1 Sprint 7 đã Done.

## Cập nhật 24/11/2026 — TLS và phục hồi lưu trữ

Mã nguồn kiểm thử: `70f1f792984d3dbee6959265b09dad5346b95077`. Bốn image thay đổi gắn revision này; bốn vai trò còn lại có source path không đổi so với baseline `03021e3`. Toàn bộ là fixture TEST; trạng thái tổng hợp **NOT_ACCEPTED**. Chưa merge `develop` hoặc chuyển Jira Done.

| Phép đo | Một worker TLS | Bốn worker TLS |
| --- | --- | --- |
| MEASURED / UNAVAILABLE / QUALITY_FAILURE / PIPELINE_FAILURE | 55 / 25 / 20 / 0 | 55 / 25 / 20 / 0 |
| Terminal / reservation đối soát / sai lệch | 100 / 100 / 0 | 100 / 100 / 0 |
| Retry job / attempt mỗi job | 0 / 1 | 0 / 1 |
| Peak RAM AI / worker dài hạn | 71,13% / 59,18% | 78,59% / 61,22% |
| Warm resolver p95 gộp 9.000 mẫu | 12,7225 ms — FAIL | 5,2854 ms — FAIL |
| PUT 409 được client retry theo hợp đồng | 27 | 27 |

Hai lượt giữ cùng 100 file khác bytes, AI container/image/model/8 threads và worker số 1. Không OOM hoặc restart; counter kernel `oom`/`oom_kill` bằng 0. Các phần trăm RAM là đỉnh quan sát qua lấy mẫu, không phải chứng nhận mọi tải. Mỗi chế độ resolver có ba lượt cố định: 500 warm-up loại khỏi thống kê + 3.000 mẫu đo/lượt; cold ghi riêng. P95 từng lượt một worker: 13,1761 / 13,5835 / 11,4469 ms; bốn worker: 5,3083 / 5,3703 / 5,1739 ms. Không cache quyết định cấp quyền để giảm số đo.

Thời gian tổng quan sát là 1.624,234 / 1.235,703 giây; AI processing p95 trên 80 kết quả có thời gian xử lý là 20.152,13 / 14.889,66 ms, loại queue/network và chưa phân loại cold/warm. **Không nghiệm thu so sánh hiệu năng AI từ hai cửa sổ này:** HopBench/build chẩn đoán chồng lên lượt một; CPU placement A/B/A chồng lên lượt bốn sau ba phép đo resolver định trước. Diagnostic mở rộng affinity vẫn FAIL <5 ms; đã trả service/client về CPU 8–11, AI giữ 0–7, không restart process hoặc đổi model/RAM. Không suy CPU guest thành pin vật lý P/E core, không công bố hệ số tăng tốc.

### Kiểm chứng bổ sung và phạm vi

- Worker native integration: 54/54; suite M1 native: 51/51, không skip; Analysis blob/broker: 30/30; dev controls: 39/39; security controls: 5/5. Năm workflow CI commit `70f1f79` PASS. Regression AI cũ 265 PASS/3 external-dataset SKIP giữ nguyên phạm vi; không coi là nghiệm thu SCRUM-373.
- Profile TLS tùy chọn: 12 endpoint handshake TLS 1.3; sai CA/hostname, TLS 1.1 và plaintext bị từ chối. Kiểm chứng native PostgreSQL, RabbitMQ, Redis và luồng Azurite HTTPS thành công. Client kiểm thử tin CA cục bộ rõ ràng; không cài trust root vào Windows. Các báo cáo harness lỗi ban đầu được giữ, cùng positive control TLS 1.1.
- Replay đúng bytes của một event canonical trong batch TLS bốn worker ba lần: publisher confirm, Billing và Notification mỗi bên tăng delivery/ACK đúng 3; queue về 0; tập ledger/inbox và số settlement không đổi. Không suy rộng thành exactly-once cho mọi tình huống.
- Scan source/NuGet và đúng tám application image cuối PASS, không blocker/exception; không thay bằng chứng host encryption hoặc mọi infrastructure image. Profile TLS không tự chứng nhận cloud production; các phép restore/dependency fault cũ vẫn có phạm vi riêng, chưa chạy lại full restore dưới profile TLS mới.
- Docker đã chuyển data sang `D:\DockerDesktopData\DockerDesktopWSL`. Archive `D:\DockerDesktopData\docker_data.pre-move-20261124.tar.zst` có 44.794.813.257 bytes; đọc giải nén đủ 103.560.511.488 bytes khớp SHA-256 nguồn `7470784207ece39fadf5d52d851f64652fd406d2688d8227a0764dd776cfe323`. Sau đó chỉ xóa bản VHDX cũ không dùng trên C, thu hồi 96,45 GiB; C còn 102,07 GiB tại thời điểm dọn. Chương trình/cấu hình Docker giữ nguyên. Đối chiếu lại năm volume fixture, hai model và 100 dòng tài chính canonical PASS; không chứng nhận mọi project hoặc toàn bộ EXT4. Không chạy fsck/reset/prune volumes.
- Đã thu log/runtime/counter trước khi bỏ ba container worker phụ; giữ một worker cùng AI và toàn bộ volumes. Không còn job active trong lượt cuối. Người vận hành báo C/D 100%, Protection On, **XTS-AES 128**; chưa có bằng chứng AES-256 và chưa tự thay đổi mã hóa.

Evidence cùng thư mục local đã ghi ở trên: `EVIDENCE_INDEX_FOLLOWUP_20261124.json` chứa SHA-256 của 40 tệp; `final-verification-tls-followup-20261124.json` ghi `NOT_ACCEPTED`. Các file chính gồm `tls-20261124-sequence.json`, `batch-{one,four}-tls-20261124.json`, `auth-tls-20261124-{one,four}-{1,2,3}.json`, `transport-handshakes-final-wire-r2-20261124.json`, `native-transport-tls-final-20261124.json`, `event-replay-tls-final-20261124.json`, `security-tls-final-20261124.json`, `tls-code-image-bindings-20261124.json`, `cpu-placement-diagnostic-20261124.json`, `docker-backup-verification-r1-20261124.json`, `docker-c-cleanup-20261124.json` và `docker-post-cleanup-data-20261124.json`.

Để nghiệm thu tiếp: xử lý resolver <5 ms trên môi trường triển khai đã chốt và bằng chứng AES-256, hoặc nhóm quyết định thay đổi tiêu chí/phạm vi bằng phiên bản có giải trình. Nếu cần kết luận scaling AI, chạy protocol riêng không chèn diagnostics. Không tự miễn gate hoặc coi toàn bộ Sprint 7 đã Done.
