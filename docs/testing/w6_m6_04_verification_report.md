# Báo cáo kiểm thử chéo Chat / FCM / Alert & Revoke Socket (W6-M6-04 / TC-W6-M6-04)

- **Task**: `W6-M6-04` (`TC-W6-M6-04`) — Owner: **M6**
- **Jira**: `<JIRA_KEY>` (tham chiếu release gate `SCRUM-396`; key cũ `SCRUM-388` chỉ là placeholder)
- **Baseline**: `develop` (`4fcc312` sau baseline `c299af38cc3289c7934960542a8a303e032199db` / tree `a9cf024dd6a9c2cd5ec56cea55095e60acbd2779`)
- **Nhánh thực thi**: `feature/W6-M6-04-chat-fcm-alert-revoke-verification`
- **Phạm vi**: Kế hoạch môn học (`not clinical/production acceptance`); không thay đổi trạng thái `SCRUM-373` (`OPEN`) hay `OCT` (`DEFERRED`).

---

## 1. Lỗi liên dịch vụ phát hiện & đã khắc phục (FCM / Inbox Deep-Link `/consultations/{conversationId}`)

Khi kiểm thử chéo FCM / Notification Inbox với route frontend `/consultations/:conversationId`:
1. **`contracts/notification-openapi.yaml`**: Bổ sung `CONSULTATION_MESSAGE` vào `enum`, cập nhật `targetPath.pattern` hỗ trợ `/consultations/{uuid}` và nhánh `allOf` tương ứng (`analysisId = null`, `orderId = null`).
2. **`src/web/aura-web/src/firebase/notificationNavigation.ts`**: Cho phép prefix `consultations` trong `notificationRoute`, chặn query string trên `/consultations/`.
3. **`src/web/aura-web/src/api/notificationInbox.ts` & `src/pages/NotificationInboxPage.tsx`**: Parse và validate `CONSULTATION_MESSAGE` (`/consultations/{uuid}`, `analysisId === null`), hiển thị label và link `"Mở tư vấn"`.
4. **`src/web/aura-web/src/routes/ApplicationRoutes.tsx` & `src/pages/consultations/ConsultationsPage.tsx`**: Đăng ký `<Route path="/consultations/:conversationId" ... />`, đọc `:conversationId` làm `initialConversationId`, và chặn khi `conversationId` không thuộc danh sách được cấp quyền hiện hành (hiện thông báo lỗi quyền thay vì dựng dữ liệu cũ).

---

## 2. Kết quả kiểm thử (Sau lần sửa mã cuối cùng — 0 skipped bắt buộc)

### 2.1. Backend, PostgreSQL 17 cô lập & Cross-Service Socket/FCM/Alert

Môi trường database: container Docker PostgreSQL 17 cô lập (`postgres:17-alpine`, `127.0.0.1:55432`, DB `aura_dev`, `AURA_TEST_ALLOW_DATABASE=aura_dev`, container được dọn sạch sau khi chạy).

| Nhóm kiểm tra | Lệnh thực thi | Kết quả | Chi tiết chính |
|---|---|---|---|
| Build solution | `dotnet build AURA.sln --configuration Release` | **PASS** (0 errors) | Bao gồm `AURA.Notification.ConsultationChecks` & `AURA.Analysis.Api.Tests` |
| Migrations (2 lượt) | `Initialize-AuraDevDatabase.ps1 -PlanOnly` (43 SQL files) | **PASS** | Pass 1 fresh + Pass 2 idempotent trên `postgres:17-alpine` |
| Profile Multi-Tenant | `dotnet run --project tests/AURA.Profile.MultiTenantChecks/AURA.Profile.MultiTenantChecks.csproj --configuration Release --no-build --no-launch-profile -- --database` | **47/47 PASS** + **21/21 HTTP PASS** | Thực thi dưới quyền `aura_profile_app` |
| Notification Inbox | `dotnet run --project tests/AURA.Notification.InboxChecks/AURA.Notification.InboxChecks.csproj --configuration Release --no-build --no-launch-profile` | **27/27 PASS** (Failed: 0) | Account isolation, dedup, cursor pagination, FCM decoupling |
| Consultation Cross & Revoke | `dotnet run --project tests/AURA.Notification.ConsultationChecks/AURA.Notification.ConsultationChecks.csproj --configuration Release --no-build --no-launch-profile -- --database` | **PASS (0 skipped)** | Ma trận 4 vai trò x 2 clinic; **assignment revoke 428ms**, **membership revoke 846ms**, **account disabled 836ms** (`<= 5000ms`); redact `access_token`; replay dedup; FCM generic push & tách biệt `FCM accepted` vs `actual receipt` |
| Analysis Alert Matrix | `dotnet test src/services/AURA.Analysis.Api.Tests/AURA.Analysis.Api.Tests.csproj --configuration Release --no-build --filter "Alert"` | **10/10 PASS** (Skipped: 0) | Gồm `AlertCrossClinicIntegrationTests` trên PostgreSQL 17: replay không nhân alert, 4 vai trò x 2 clinic |
| M1 Notification Worker | `dotnet test tests/AURA.M1.RegressionTests/AURA.M1.RegressionTests.csproj --configuration Release --no-build --filter "NotificationTests"` | **11/11 PASS** (Skipped: 0) | Event replay / durable worker notification checks |
| OpenAPI Contract | `python scripts/validate_openapi.py` | **PASS** | 42 Analysis/Campaign routes & required schemas hợp lệ |

### 2.2. Frontend Unit, Build & Playwright E2E (`src/web/aura-web`)

| Nhóm kiểm tra | Lệnh thực thi | Kết quả |
|---|---|---|
| Lint | `npm run lint` | **PASS** (0 errors, 17 pre-existing warnings trên 173 files) |
| Unit / Integration | `npx vitest run --maxWorkers=2` | **58/58 files, 569/569 tests PASS** (0 skipped) — gồm `notificationNavigation.test.ts` (13), `notificationInbox.test.ts` (14), `ConsultationsPage.w6m604.test.tsx` (4) |
| Build + Service Worker | `npm run build` | **PASS** (`public/firebase-messaging-sw.js` 53.30 kB + `dist/`) |
| Playwright E2E (mock API) | `npx playwright test --config playwright.m5-mock.config.ts` | **16/16 PASS** (0 failed, 0 skipped) — gồm `MOCK: TC-W6-M6-04 FCM consultation deep-link enforces current authorization and blocks revoked users` |

---

## 3. Ranh giới kiểm thử & Giới hạn đã ghi nhận

1. **FCM accepted vs. actual device receipt**: Các test backend và UI kiểm chứng hàng đợi `outbox` / adapter trả về accepted của FCM và chặn payload PHI trong offline push (`generic title/body`, yêu cầu mở app auth lại); không giả lập thiết bị FCM thật nhận push đầu cuối (`FCM accepted != actual receipt`).
2. **Test doubles ở ranh giới liên dịch vụ**:
   - `AURA.Notification.ConsultationChecks` chạy ASP.NET Core `Profile` & `Notification` HTTP/SignalR server thật trên PostgreSQL 17, kèm HTTP fake cho cổng FCM bên ngoài.
   - `AlertCrossClinicIntegrationTests` chạy `PostgresAlertRepository` & `AlertService` trên PostgreSQL 17 thật với `MatrixAnalyticsAccessResolver` làm ranh giới phân quyền Profile/Analytics.
   - Bộ Playwright chạy chế độ API mock (`playwright.m5-mock.config.ts`), kiểm chứng hành vi UI/router/deep-link.
