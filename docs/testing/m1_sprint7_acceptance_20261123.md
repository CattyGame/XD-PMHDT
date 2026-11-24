# M1 Sprint 7 — đối soát candidate ngày 23/11/2026

Nhánh `feat/m1-sprint7-completion`, PR #112. Commit mã nguồn được kiểm thử: `03021e3f1873de2129588c02da79f9fc5c52296e`. Chưa merge `develop`; chưa chuyển Jira sang Done. Đây là fixture nghiên cứu/Sandbox TEST, không phải chứng nhận triển khai production hoặc nghiệm thu khoa học SCRUM-373.

**Kết luận: chưa đủ điều kiện nghiệm thu toàn bộ M1 Sprint 7.** Hai lượt đều hội tụ đủ 100 ảnh và đối soát đủ credit, không retry job/OOM kill; lượt bốn worker FAIL gate RAM. Resolver và phạm vi security còn thiếu như bảng dưới. Không dùng số liệu trước FIFO thay cho lượt mới.

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
