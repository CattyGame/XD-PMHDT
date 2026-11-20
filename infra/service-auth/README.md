# Service identity — Sprint 2

Owner chuẩn bị cấu hình: M1 / SCRUM-300.

Owner triển khai Identity issuance/validation: M6 / SCRUM-314.

Cập nhật: 2026-10-23.

## Trạng thái

Baseline đối chiếu: `develop` tại `87af21d`, sau PR #56.

Đã có trong mã nguồn:

- Registry client/principal/audience/scope, công cụ tạo khóa RS256 và
  credentials ngoài repository; kiểm tra cặp khóa và hash secret.
- Identity đọc registry và private key theo cấu hình Compose, phát JWT,
  cung cấp public key và identity context.
- Profile kiểm service token ở provision/access resolve; dùng client riêng
  `profile-api` khi giải quyết Firebase user token qua Identity/context.
- Billing kiểm service token và gọi Profile access resolve trước reserve.

Người dùng đã chạy local thành công Firebase → Gateway → Identity/Profile,
Profile PATCH/ETag, 12 ca HTTP âm và Billing reserve/replay/concurrency cùng
replay sau restart. Đây là bằng chứng cho các ca đã chạy trên cấu hình đó.

Các mục còn mở:

- Identity còn fallback sinh key/secret Development nếu thiếu cấu hình;
  consumer Profile còn tìm một số đường dẫn key dự phòng. M6 cần sửa cơ chế
  này, kiểm tra cấu hình lỗi và không cho dùng private key của issuer ở consumer.
- Chưa xác nhận mọi nhánh token hết hạn/thu hồi, issuer/chữ ký sai, quyền
  Clinic/assignment hoặc toàn bộ transition/reconciliation Billing.
- Analysis runtime chưa nối endpoint và các adapter nghiệp vụ; client
  `analysis-api` có trong registry không chứng minh đã có luồng upload thật.

Không xóa hoặc tạo lại bộ credentials đang dùng để xử lý các mục còn mở.
Firebase user token và service JWT là hai loại token khác nhau.

## Registry công khai

Nguồn cấu hình: clients.example.json.

| Caller | Audience | Scope |
| --- | --- | --- |
| analysis-api | aura-identity | identity:context |
| analysis-api | aura-profile | profile:resolve |
| analysis-api | aura-billing | billing:reserve, billing:read, billing:release |
| identity-api | aura-profile | profile:provision |
| billing-api | aura-profile | profile:resolve |
| profile-api | aura-identity | identity:context |

Mỗi caller có principalId UUID ổn định.

Token sub dùng principalId; client_id dùng tên client.

billing:release chỉ phục vụ compensation hợp lệ.

Billing vẫn kiểm tra trạng thái bền vững, không cho caller

tự quyết định request đã abort bằng một boolean.

analysis:reconcile bổ sung khi endpoint đối soát được triển khai.

Quyền Worker được bổ sung theo phạm vi Sprint 3.

## Tạo credentials trên máy local

Chạy từ thư mục gốc repository:

```powershell
$registryPath = (
    Resolve-Path "infra/service-auth/clients.example.json"
).Path
$outputDirectory = Join-Path $env:USERPROFILE `
    ".aura\service-auth\local-v1"
dotnet run `
    --project "tools/AURA.ServiceIdentity.Setup/AURA.ServiceIdentity.Setup.csproj" `
    --configuration Release `
    --no-launch-profile `
    -- `
    --registry $registryPath `
    --output $outputDirectory
```

Công cụ:

- Chỉ ghi output ngoài repository.

- Dừng nếu thư mục output đã tồn tại.

- Tạo RSA 3072 bit; private key PEM PKCS#8.

- Tạo public key PEM SubjectPublicKeyInfo.

- Tạo secret ngẫu nhiên riêng cho mỗi client.

- Không in secret hoặc private key.

- Ghi file trong thư mục tạm rồi chuyển thành output khi hoàn tất.

Chạy lại không được xóa thư mục để tự động sinh lại khóa đang dùng.

Thay khóa hoặc credentials phải có kế hoạch rotation riêng.

## File được tạo và phạm vi cấp quyền đọc

| File tương đối trong output | Bên sử dụng |
| --- | --- |
| issuer/signing-key.pem | Chỉ Identity |
| issuer/identity-clients.json | Chỉ Identity |
| public/signing-public.pem | Consumer xác minh JWT |
| clients/analysis-api.json | Chỉ Analysis |
| clients/identity-api.json | Chỉ Identity khi gọi Profile |
| clients/billing-api.json | Chỉ Billing |
| clients/profile-api.json | Chỉ Profile khi gọi Identity context |
| manifest.json | Metadata keyId/issuer/version |

Không mount toàn bộ thư mục output cho mọi service.

identity-clients.json chứa clientSecretHash và allowlist.

File clients/*.json chứa clientSecret thực tế.

Caller không được nhận registry hash của các client khác.

privateKeyPath và publicKeyPath trong issuer config được giải

tương đối từ thư mục chứa identity-clients.json.

## Hợp đồng đọc cấu hình

Identity đọc file được chỉ định bởi ServiceAuth__RegistryPath.

Identity runtime đã đọc key này. Việc bind đúng cấu hình không thay thế kiểm tra quyền và các ca thiếu/sai cấu hình.

Các key theo contract dùng ở consumer/caller:

- ServiceAuth__Issuer.

- ServiceAuth__Audience: audience mà API hiện tại chấp nhận.

- ServiceAuth__PublicKeyPath.

- ServiceAuth__ClientId.

- ServiceAuth__ClientSecret.

- Services__Identity__BaseUrl.

- Services__Profile__BaseUrl (Notification consultation authority).

ServiceAuth__Audience của consumer không được nhầm với audience

đích khi caller xin token cho một service khác.

Identity nạp lại khóa đã lưu khi khởi động.

Khi thay đổi allowedTargets trong clients.example.json, chạy lại công cụ
AURA.ServiceIdentity.Setup để tạo registry/hash cục bộ tương ứng. SCRUM-391
cần notification-api → aura-profile/profile:resolve.

Không sinh khóa mới mỗi lần chạy.

Công cụ setup ghi keyId metadata. Identity runtime hiện tính keyId từ SHA-256 của public key SPKI; cần dùng keyId runtime khi đối chiếu JWT/public key, không giả định hai giá trị metadata luôn giống nhau.

## Endpoint issuance đã có trong Identity

POST /internal/v1/identity/service-tokens.

Request chốt để lựa chọn đúng service đích:

```json
{
  "clientId": "analysis-api",
  "clientSecret": "<cung cấp riêng>",
  "audience": "aura-profile",
  "scope": "profile:resolve"
}
```

Quy tắc:

1. Tra client trong issuer config.

2. Xác thực clientSecret với clientSecretHash.

3. Kiểm audience có trong allowedTargets của chính client đó.

4. Nếu có scope yêu cầu, toàn bộ scope phải thuộc audience đó.

5. Nếu thiếu scope, dùng danh sách được server cấu hình cho audience.

6. Từ chối scope lạ; không tự ghép quyền từ audience khác.

7. Lấy principalId từ registry, không nhận từ body.

8. Credentials sai trả 401; audience/scope không được cấp trả 403.

Response thành công:

```json
{
  "accessToken": "<service JWT>",
  "tokenType": "Bearer",
  "expiresIn": 300
}
```

Endpoint chỉ nội bộ, không route qua Gateway public.

## Định dạng clientSecretHash

```text
pbkdf2-sha256$600000$<Base64 salt>$<Base64 derived key>
```

- PBKDF2-HMAC-SHA256.

- 600000 iterations.

- Salt 16 byte.

- Derived key 32 byte.

- So sánh bằng CryptographicOperations.FixedTimeEquals.

Công cụ có hàm HashSecret/VerifySecret làm nguồn tham chiếu.

Không lưu plaintext clientSecret vào bảng/registry của issuer.

## JWT và consumer validation

- Algorithm: RS256, chỉ cho phép thuật toán này.

- Issuer: aura-identity-local.

- Audience: đúng service đích.

- TTL: 300 giây.

- Claims: sub UUID, client_id, iss, aud, exp, iat, jti,

  scope phân cách bằng dấu cách.

- Consumer kiểm chữ ký, issuer, audience, expiry và scope endpoint.

- Cấu hình clock skew rõ ràng, đề xuất tối đa 30 giây cho local.

- Chỉ dùng public key/JWKS tin cậy do môi trường cung cấp.

- Không tin URL khóa do token tự chỉ định.

- Không log JWT hoặc secret.

Identity context cần service JWT ở Authorization và Firebase

user token riêng ở X-User-Authorization.

Không chấp nhận Firebase token như service JWT hoặc ngược lại.

Gateway public loại X-User-Authorization do browser gửi.

## Ma trận kiểm tra service JWT cần nghiệm thu

- Đúng credentials/audience/scope: phát token.

- Sai secret hoặc client không tồn tại: từ chối.

- Audience/scope vượt quyền: từ chối.

- Consumer từ chối token sai chữ ký, hết hạn, issuer/audience sai.

- Token hợp lệ nhưng thiếu scope: từ chối endpoint.

- Identity restart vẫn xác minh được token chưa hết hạn

  bằng public key đã cấp.

- Không gọi /internal/v1 qua Gateway public.

- Profile resolve/provision và Identity context dùng service JWT thật.

Kết quả tạo credentials không thay thế các ca tích hợp này. Trạng thái từng ca phải đi kèm output thực chạy; không đánh dấu toàn bộ PASS từ kết quả của một luồng hợp lệ.

## Kết quả local 2026-10-16

- Tạo credentials analysis-api, identity-api, billing-api: PASS.

- Kiểm tra hash với secret đúng và sai: PASS.

- Ký/xác minh bằng cặp RSA xuất ra: PASS.

- Output nằm ngoài repository: PASS.

- Không in private key, client secret hoặc token.


## Bổ sung Profile caller — bản sửa ngày 2026-10-23

Profile GET/PATCH /me dùng client riêng profile-api, audience aura-identity,
scope identity:context. Xem [luồng Profile identity](../../docs/development/profile_identity_resolution.md).
Máy đã có credentials dùng Register-AuraProfileServiceClient.py để bổ sung
client, giữ nguyên các khóa và ba client cũ. Không chạy lại công cụ tạo toàn bộ
credentials vào thư mục đang dùng. Các checklist runtime cũ ở trên là lịch sử;
chỉ ghi PASS mới sau khi đã chạy kiểm chứng trên máy triển khai.

Theo dõi phần còn mở tại [kế hoạch sửa Sprint 2](../../docs/integration/sprint2_closeout_plan.md).

## Runtime fail-closed (Sprint 2 closeout)

Identity yêu cầu ServiceAuth:Issuer, TokenLifetimeSeconds (1–300), PrivateKeyPath
và RegistryPath rõ ràng. Registry đã provision phải có schemaVersion=1,
algorithm=RS256, issuer/lifetime/keyId khớp cấu hình và khóa đang dùng,
cùng clientSecretHash PBKDF2-SHA256/600000 hợp lệ cho mọi client. Registry
ví dụ chưa có secret hash không được dùng trực tiếp làm runtime registry.
Không tự sinh khóa, seed client, secret mặc định hoặc tìm file dự phòng.
Cấu hình sai làm startup thất bại trước khi health endpoint được mở.

Profile yêu cầu ServiceAuth:Issuer, Audience=aura-profile, và đúng một nguồn
PublicKeyPath hoặc PublicKeyPem. Chỉ nhận RSA public PEM tối thiểu 2048 bit;
không nhận private PEM, không tìm signing-key.pem và không tự fetch key qua HTTP.
Compose mount riêng thư mục public read-only vào Profile, không mount issuer
hay client credential directory. Khóa public và registry/secret hiện có không
bị sửa bởi gói source này. Nếu đổi khóa, phải provision/triển khai đồng bộ;
không có tự động rotation/discovery trong phiên bản này.

Kiểm tra source bằng tests/AURA.ServiceAuth.Checks. Sau khi nạp cấu hình dev,
chạy cùng project với -- --check-local-material để kiểm tra chỉ đọc bộ
AURA_SERVICE_AUTH_HOME ngoài repo: key pair, registry và credential từng client,
bao gồm đối chiếu allowedTargets. Không gửi HTTP, không tạo/lưu token ra file,
không in secret/hash/PEM và không restart. Xem
[service_auth_fail_closed.md](../../docs/development/service_auth_fail_closed.md).
