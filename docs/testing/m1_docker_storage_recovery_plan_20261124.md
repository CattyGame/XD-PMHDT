# M1 Sprint 7 — phương án phục hồi Docker ngày 24/11/2026

Người vận hành đã chấp thuận sao lưu và chuyển dữ liệu Docker sang D ngày 24/11/2026. Bản sao lạnh ban đầu đã khớp SHA-256 cả hai VHDX; Docker đang thực hiện chuyển vị trí qua giao diện chính thức. Chưa chạy sửa filesystem. Phê duyệt WSL 7 GiB trước đó đã được áp dụng riêng.

## Bằng chứng hiện tại

- Docker engine dừng giữa bước COPY của build Analysis API; báo EOF. Backend ghi `signal: bus error` và `daemon.json is invalid` khi kiểm tra bằng tiến trình bị lỗi.
- Log VM cùng thời điểm ghi I/O error trên `sdd`, lỗi chuyển unwritten extents và `Detected aborted journal` của EXT4. Không kết luận chắc chắn nguyên nhân gốc chỉ từ dung lượng ổ.
- Trước khi sao lưu, ổ C còn khoảng 3,05 GiB; file `%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx` khoảng 96,45 GiB. Ổ D còn khoảng 201,7 GiB. Đích mới là `D:\DockerDesktopData`.
- Pagefile Windows đang cấp phát khoảng 8 GiB. Không đổi hoặc tắt pagefile để làm đẹp số RAM.

## Quy trình đã được phê duyệt

1. Dừng Docker/WSL và xác minh VHDX không còn được ghi; giữ bản settings hiện tại cùng inventory/evidence đã có.
2. Sao chép VHDX sang D, đối chiếu kích thước và SHA-256. Bản C được giữ nguyên trong thời gian xác minh.
3. Dùng chức năng Disk image location của Docker Desktop để sử dụng bản sao tại `D:\DockerDesktopData`, tránh ghi đè bản sao đã có hoặc khởi tạo disk rỗng. Nếu thao tác yêu cầu thay thế/xóa bản gốc, dừng ở bước đó để đánh giá.
4. Kiểm tra engine, images, container labels, volume names, schema và các count/hash dữ liệu đã chốt. Chỉ tiếp tục build/workload khi các kiểm tra này đạt.
5. Nếu bản sao vẫn lỗi filesystem, giữ cả hai bản và thực hiện chẩn đoán offline. Chưa cho phép `fsck` có ghi, format, factory reset, xóa volume, unregister distro hoặc xóa VHDX gốc trong phương án này.

Phương án thay thế: người vận hành giải phóng ít nhất khoảng 20 GiB trên C rồi kiểm tra lại Docker; vẫn cần giữ snapshot trước mọi sửa chữa filesystem vì đã có lỗi I/O thực tế.

## Diễn biến cần bảo lưu

- Bản sao lạnh hoàn tất lúc 03:24:07 UTC ngày 24/11; `docker-disk-copy-20261124.json` ghi nhận hai cặp SHA-256 khớp. Đây là trạng thái trước lần khởi động lại, không phải chứng nhận filesystem hoặc mã băm của disk đang chạy.
- Docker Desktop Windows 4.93.0 tự thêm thư mục `DockerDesktopWSL` dưới đích đã chọn. Giao diện chỉ hỏi xác nhận di chuyển; sau đó backend chạy `robocopy /MOV`, không đưa ra lựa chọn dùng disk đã có. Do đó bản sao đích được Docker ghi lại từ bản nguồn đã khởi động thêm một lần.
- Đã giữ hard link `docker_data.pre-move-20261124.vhdx` tới VHDX nguồn khi engine dừng, trước khi lệnh di chuyển hoàn tất. Liên kết này giữ dữ liệu gốc nếu Docker xóa tên tệp nguồn cũ; không phải bản sao vật lý độc lập trên ổ khác và chưa giải phóng dung lượng C.
- Cần đối chiếu dữ liệu sau cutover và giữ một bản backup đã xác minh trước khi dọn bản giữ trên C. Không dùng mã băm của snapshot ban đầu để tuyên bố disk sau cutover khớp khi chưa kiểm tra lại.
- Sau cutover, registry WSL ghi `docker-desktop` tại `D:\DockerDesktopData\DockerDesktopWSL\main`; engine giữ giới hạn khoảng 7 GiB. `docker-cutover-data-20261124.json` đạt `PASS_OWNED_DATA_CHECKS`: đủ năm volume của fixture, SHA-256 hai model không đổi, 100 dòng ledger/reservation của batch đã phục hồi khớp toàn bộ bản trước chuyển. Phạm vi này không chứng nhận nội dung mọi project Docker hoặc toàn bộ filesystem.
- Đang tạo backup độc lập bằng `bsdtar --zstd` có sẵn trên Windows; chỉ công nhận khi đọc giải nén đầy đủ và đối chiếu SHA-256 với nguồn. Lượt gzip quá chậm đã dừng, tệp partial được giữ riêng, không coi là backup hợp lệ. VHDX gốc giữ trên C chưa được dọn.

[Docker hướng dẫn thay vị trí dữ liệu WSL](https://docs.docker.com/desktop/features/wsl/). [Disk image location và sử dụng image đã có](https://docs.docker.com/desktop/settings-and-maintenance/settings/).

## Phạm vi nghiệm thu

Không chuyển SCRUM-398/400/401 sang Done từ các kết quả chưa đủ. Lượt một worker WSL7 cũ bị gián đoạn ở 99/100; bản phục hồi sau sửa lease đạt 100 terminal và 100 reservation đối soát, nhưng không thay benchmark liên tục. Profile TLS còn cần build lại Analysis adapter hỗ trợ HTTPS Azurite và chạy đủ kiểm tra runtime. AES-256 at rest vẫn chưa có bằng chứng: hai ổ hiện ghi XTS-AES 128.
