# SCRUM-382 — Vận hành camera qua HTTPS

Phạm vi là profile demo cục bộ của AURA. Áp dụng migration 020 và dùng flow SCRUM-381. Quyền camera do Profile kiểm tra theo ADR-005.

## Chuẩn bị và bật profile

1. Dùng Docker Compose 2.24.4 trở lên. Đặt `AURA_CAMERA_TLS_HOME` trỏ tới thư mục ngoài Git. Cấp certificate dùng cho TLS server, có SAN như bảng dưới, đầy đủ intermediate nếu CA có intermediate. Bảo vệ private key, chỉ cấp quyền đọc cho tiến trình cần dùng.

   | Tệp trong thư mục TLS | SAN cần có |
   | --- | --- |
   | `identity/server.crt` và `identity/server.key` (key PEM) | DNS `identity-api` |
   | `gateway/server.crt` và `gateway/server.key` (key PEM) | DNS `api-gateway`, DNS `localhost` |
   | `ingress/server.crt` và `ingress/server.key` (key PEM) | DNS `localhost` |

2. Tạo `nifi/trust.p12` từ **CA đã xác minh** và chỉ chứa certificate công khai. Ví dụ với JDK, chạy ở thư mục TLS đã tạo sẵn thư mục con `nifi`:

   ```powershell
   keytool -importcert -alias aura-camera-demo-ca -file ca.pem -keystore nifi/trust.p12 -storetype PKCS12 -storepass changeit
   ```

   `changeit` là mật khẩu của kho chỉ chứa CA công khai. Nếu NiFi có thêm connector HTTPS, nhập cả các CA mà connector đó cần. Camera/simulator xác minh CA và hostname riêng.

3. Dùng cùng mạng Docker AURA, thư mục TLS và bộ credentials đã provision cho cả hai lệnh. Giữ các overlay đang cần cho stack hiện hành ở trước overlay TLS. Các biến `NIFI_CAMERA_CLIENT_SECRET`, `NIFI_CAMERA_DEVICE_KEYS_JSON` phải mô tả client và thiết bị đã đăng ký. Cổng máy chủ bên dưới mặc định là loopback, có thể đổi bằng `AURA_CAMERA_HTTPS_PORT`, `AURA_NIFI_ADMIN_HTTPS_PORT`, `AURA_GATEWAY_HTTPS_PORT`.

   ```powershell
   docker compose --env-file <env-file> -f compose.yaml -f compose.dev.yaml -f compose.adr005.yaml -f compose.camera-tls.yaml up -d --build
   docker compose --env-file <env-file> -f infra/nifi/docker-compose.nifi.yml -f infra/nifi/docker-compose.camera-tls.yml up -d
   ```

4. Vào UI NiFi, nhập `infra/nifi/camera_ingestion_flow_v2.json` đã cập nhật, rồi khởi động các processor. Khi nâng cấp flow đang chạy, dừng đầu vào, giữ các queue chưa xử lý và cập nhật sender trong nhóm hiện hành bằng `SendCameraToGateway.groovy`. Giữ các volume chứa queue và content. Bản export đã nhúng cùng script. Nếu chạy giao diện React trong cùng buổi demo, đặt `VITE_API_BASE_URL=https://localhost:5443` theo cổng đã chọn và cấp CA của profile vào trust store của trình duyệt trước khi chạy lại Vite.
5. Từ máy demo, chạy simulator với `--live --ca-file <ca.pem> --tenant-id <tenant-uuid> --patient-id <patient-uuid> --device-id <device-id> --image <synthetic.png>` và thêm `--clinic-id <clinic-uuid>` cho grant của phòng khám. Nạp `AURA_CAMERA_DEVICE_KEY` vào tiến trình trước khi chạy. Dùng URL `https://localhost:8444/ingest/camera` hoặc chỉ định `--url` theo cổng đã đặt.

UI quản trị NiFi chỉ mở trên loopback. Nếu dùng certificate tự ký mặc định, xuất **certificate công khai** từ máy chạy container, xác minh fingerprint rồi cấp certificate đó vào trust store của máy quản trị. Chạy sau khi NiFi đã khởi động:

```powershell
docker exec aura-nifi bash -c 'keytool -exportcert -rfc -alias nifi-key -keystore conf/keystore.p12 -storepass "$(sed -n "s/^nifi.security.keystorePasswd=//p" conf/nifi.properties)"' |
    Set-Content -Encoding ascii nifi-admin.pem
```

Camera đi qua HTTPS tới ingress. Proxy chuyển tiếp đến listener NiFi trên mạng Docker riêng của stack. NiFi xác thực TLS tới Identity và Gateway. Cổng HTTP của listener v2 và cổng cấp token của Identity không được publish ra host. Gateway giữ cổng HTTP trên mạng nội bộ cho health/metrics, và từ chối POST camera qua cổng đó. Kiểm tra TLS ở từng đầu Camera → ingress, NiFi → Identity và NiFi → Gateway. Kết nối backend hiện có trong mạng Docker giữ theo Compose của hệ thống.

## Xoay khóa chữ ký của Identity

1. Ghi lại `kid` hiện hành. Chuẩn bị RSA mới ngoài repository và registry phiên bản mới khớp `kid` runtime. Giữ nguyên issuer, principal UUID, allowlist và hash của các client nếu chỉ xoay signing key. Sao lưu bộ issuer/public cũ có kiểm soát và xác minh cặp khóa trước khi chuyển.
2. Đặt public key mới cạnh public key hiện hành trong thư mục `public` đã mount riêng cho Profile. Đặt `AURA_CAMERA_PROFILE_ROTATION_KEY_PATH=/run/aura-service-auth/public/signing-next.pem` và recreate Profile trước khi chuyển issuer. Cấu hình cho phép hai khóa trong cửa sổ chuyển tiếp. Chạy thử xin token hiện hành và resolve grant.
3. Phối hợp cửa sổ bảo trì cho các consumer khác của Identity. Cập nhật public key hoặc làm mới instance giữ cache theo cấu hình từng consumer. Tạm dừng đầu vào và tiến trình gửi nếu chưa thể cập nhật đồng bộ. Triển khai private key và registry mới cùng nhau rồi recreate Identity. Kiểm tra token mới có đúng `kid`, token cũ còn hạn vẫn resolve tại Profile và kiểm tra các caller liên quan trước khi mở lại đầu vào.
4. Sau khi vượt TTL tối đa 300 giây cộng 30 giây clock skew kể từ token cuối cùng ký bằng khóa cũ, đặt khóa mới làm public key chính, bỏ cấu hình khóa phụ và recreate Profile. Kiểm tra khóa cũ bị từ chối và khóa mới thành công. Với nghi lộ khóa, bỏ khóa cũ ngay và chấp nhận gián đoạn có kiểm soát.
5. Nếu cần quay lại do lỗi chuyển cấu hình, dừng đầu vào, triển khai bộ issuer/registry/public trước đó đã kiểm chứng, đồng bộ các consumer và chạy lại ca hợp lệ trước khi xả hàng đợi. Chỉ quay lại khóa còn tin cậy.

## Thu hồi, đối soát và khôi phục

- Thu hồi grant theo đúng `grant_id` trong Profile. Với fixture, chủ sở hữu schema có thể cập nhật `profile.camera_grants` thành `REVOKED` cùng `updated_at`; kiểm tra đúng một dòng trong transaction. Sau đó gửi lại cùng sự kiện với token còn hạn để xác nhận quyền được kiểm tra lại. Nếu nghi khóa thiết bị bị lộ, xóa/đổi mục HMAC của thiết bị ở NiFi trước khi mở lại đầu vào.
- Ca dương phải nhận receipt từ `Accepted-Camera-Receipt`. Lấy `requestId`, version, hash, `batchId` và `analysisId` để đối soát. Gửi lại nguyên gói và đối chiếu ID/thời điểm gốc cùng số job và reservation. Số job và reservation phải giữ nguyên. Cùng ID nhưng ảnh đổi phải báo conflict.
- Gặp lỗi TLS/503, dừng đầu vào nếu lỗi kéo dài, giữ gói thử lại và hàng cách ly. Sửa lại CA/chuỗi certificate/SAN hoặc cấu hình upstream, recreate dịch vụ dưới cùng Compose project và giữ volumes. Nếu NiFi báo tiến trình cũ còn chạy sau sự cố, xác nhận container/JVM đã dừng và recreate container để thay lớp ghi tạm. Kiểm tra một ca hợp lệ trước khi phát lại.
- Trước khi phát lại từ hàng cách ly, đối chiếu kết quả phía Analysis theo ID/version/hash và quyền hiện hành. Giữ nguyên envelope có chữ ký và khóa idempotency. Ca có grant đã thu hồi hoặc chữ ký không còn hợp lệ cần được giữ lại để đối soát.
- Ghi nguồn commit, hash cấu hình đã che bí mật, kết quả từng ca dương/âm và bằng chứng trước/sau replay. Giữ khóa, JWT, mật khẩu, ảnh thật và nội dung FlowFile khỏi log bàn giao. HTTP 200 ở ingress chỉ xác nhận NiFi nhận yêu cầu.
