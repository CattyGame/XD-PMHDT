# AURA · Sprint 7 M5

25/11/2026 (UTC+7) · Nhánh `feature/SCRUM-408-409-410-sprint7-frontend` · [Draft PR #114 → develop](https://github.com/CattyGame/XD-PMHDT/pull/114).

## Candidate và kết quả

- Code kiểm thử `beca9c627556b15357c74032781e2a937685fac0`, tree `bd69ddec1d4efcaeb7b3028826b1962191bf033b`. Base `develop@e48b1b0dd38e67db6f2d05c78a3caac5f0553645`, tree `3be1ea7d9a5c0d1c7bc979bb9a8305f14c404305`. Các commit báo cáo sau SHA này chỉ cập nhật evidence.
- `git fetch origin` xác nhận PR #102, #103, #104 đang open/draft, các commit `9e15fc8`, `cad9ce0`, `d744b6d` chưa thuộc lịch sử `develop`. Dùng checkout riêng `AURA-sprint7` để bảo toàn worktree trên ổ D. Pull `develop` trước SCRUM-409 và trước SCRUM-410, cả hai lượt đã cập nhật.
- PASS trên code candidate. 55 file/540 Vitest, lint exit 0 với 14 cảnh báo, production build, 11 Chromium mock, 11 Google Chrome, 11 Microsoft Edge, 11 Firefox, 0 skip trong required suites. CI GitHub 8/8 checks thành công. [CI candidate](https://github.com/CattyGame/XD-PMHDT/commit/beca9c627556b15357c74032781e2a937685fac0/checks).
- Smoke dùng tài khoản tổng hợp đã cấp qua Firebase TEST và Gateway thật. Ba tài khoản đã đăng nhập. SuperAdmin đọc clinic HTTP 200, Patient bị chặn ở Admin và sau logout. Chrome, Edge và Firefox mở được Result tổng hợp với ảnh riêng tư. Worklist thiếu ca TEST, PDF trả HTTP 503 và FCM chưa xác nhận receipt. Deployment tích hợp chưa được ghép manifest với SHA code.

## Acceptance checklist

| Jira | Đã có bằng chứng | Phần còn mở |
| --- | --- | --- |
| [SCRUM-408](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-408) · TC-W7-M5-01 | PASS cho focus/keyboard 409, Admin tabs, Login contrast/lang và reflow các viewport đã chạy. Lighthouse Login 100/100 | SKIPPED zoom 200% thủ công, screen reader và contrast toàn luồng. BLOCKED Chat và nghiệm thu a11y live đầy đủ |
| [SCRUM-409](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-409) · TC-W7-M5-02 | PASS hành trình ba click từ hồ sơ đã chọn trên Playwright. JS ban đầu 668,66 → 535,38 kB (−20%). Dashboard p95 TEST loopback 3.490 → 2.970 ms | FAIL Result p95 5.346 → 5.207 ms và stretch LCP. BLOCKED nghiệm thu performance live trên deployment SCRUM-397 |
| [SCRUM-410](https://nguyentruonghau2202.atlassian.net/browse/SCRUM-410) · TC-W7-M5-03 | PASS toàn bộ frontend gates và ma trận browser. Smoke live TEST xác nhận auth, Admin và Result loại dữ liệu tổng hợp | FAIL Worklist, PDF 503, FCM receipt timeout ở smoke local. BLOCKED gate tích hợp đầy đủ, Chat/revoke socket và click FCM thật |

Ma trận mock dùng Windows x64, Node 24.21.0, npm 11.19.0, Playwright 1.65.0, Vite production preview 4180, Firebase/API fixture và 1 worker. Chrome 158.0.8127.88, Edge 158.0.4370.33 và Firefox 159.0. Viewport Doctor 390×844, 768×1024, 1024×900, 1366×768, 1920×1080. Login, Personal Upload, Batch lookup và Admin chạy reflow ở 390×844 và 768×1024.

## Thay đổi, test và commit

Các đường dẫn source dưới `src/web/aura-web/`.

| Jira | Source/test đã sửa | Commit |
| --- | --- | --- |
| SCRUM-408 | `index.html`, `src/components/common/AppModal.tsx/.css`, `src/pages/LoginPage.css`, `src/pages/AdminManagementPage.tsx/.css`, `src/pages/doctor/DoctorWorkspacePage.tsx/.test.tsx`, `src/pages/doctor/doctor.module.css`, `e2e/scrum357.e2e.ts` và `e2e/m5-workflows.mock.e2e.ts`. Bổ sung keyboard và viewport còn thiếu trong fixture cũ | `86bd2ca`, `301beaf` · Evidence `547f355`, `0cd94f7` |
| SCRUM-409 | `src/routes/ApplicationRoutes.tsx/.test.tsx`, `src/routes/ClinicUploadNavigation.tsx`, `src/App.css`, `src/index.css`, `src/pages/ClinicPatientPage.tsx`, `src/pages/ClinicUploadPage.tsx`, `src/pages/AnalysisResultPage.tsx/.test.tsx`, `e2e/m5-workflows.mock.e2e.ts`. Test mới cho luồng ba click và vòng đời ảnh riêng tư | `a3d2cc9`, `a52853d`, `26cbed1` · Evidence `dfb326c`, `a1ad695` |
| SCRUM-410 | `src/pages/ClinicUploadPage.tsx/.test.tsx`, `playwright.m5-browsers.config.ts`, `e2e/m5-workflows.mock.e2e.ts`. Thêm hồi quy đổi clinic và mở rộng ca clinician với Result, F5, deny và logout | `196a4c5`, `beca9c6` · Evidence `0d9e7d8` |

Tái sử dụng test auth 311/312, Batch/Result 332/333/334, Doctor/Billing/Batch 354–357 và Bulk/Report/Recovery 374–376. REUSED làm tham chiếu Sprint 6 ở đúng SHA gốc cho live SCRUM-389/393/394. Chi tiết assertion, phân loại PASS/FAIL/REUSED/BLOCKED/SKIPPED và lý do được ghi ở từng báo cáo.

- [SCRUM-408 · Accessibility và responsive](SCRUM-408-accessibility-responsive.md)
- [SCRUM-409 · Web Vitals và hành trình](SCRUM-409-web-vitals.md)
- [SCRUM-410 · Browser regression](SCRUM-410-browser-regression.md)

## Blocker và công việc nghiệm thu còn lại

| Owner | Phụ thuộc và bước tiếp theo |
| --- | --- |
| M6 · Dung Hoang | Chốt SCRUM-397 bằng manifest SHA/tree, image/model/config/migration và triển khai môi trường TEST tương ứng. Verifier cục bộ báo BLOCKED dù 6 contracts và 42 migrations kiểm tra được, còn 5 blocker trong cấu hình |
| M5 | Đối chiếu các draft #102–104 sau review, thực hiện keyboard/screen reader/zoom 200% trên deployment, rồi chạy các AC live còn thiếu. Các suite fixture đã có cấu hình tái lập |
| M1 · Nguyễn Trương Hậu và M4 · Đào Duy Quân | Xác minh catalog/version, history, usage, template SCRUM-383, artifact/config SCRUM-392 và ca research có crop, geometry, provenance để chạy mục Sprint 6 còn mở |
| M2 · Võ Hữu Duy và M6 | Xác minh PDF 503, worklist fixture và đăng ký/receipt/click FCM. Hoàn thành SCRUM-391/396 với mất ACK thật, bù gap, nhiều tab, token expiry và revoke socket |

## Nội dung comment Jira để bàn giao

**SCRUM-408** — Đã push accessibility/responsive, kiểm keyboard và modal 409, Admin tabs, labels, contrast mẫu và reflow. Lighthouse Login 100/100. Cần checklist thủ công 200%/screen reader và live Chat trên candidate tích hợp. [Draft PR #114](https://github.com/CattyGame/XD-PMHDT/pull/114).

**SCRUM-409** — Playwright xác nhận ba click từ hồ sơ bệnh nhân đã chọn và dùng Result đúng clinic. JS tải ban đầu giảm khoảng 20%. p95 TEST loopback đo 30 lượt mỗi route và build. Dashboard 3.490 → 2.970 ms, Result 5.346 → 5.207 ms và vẫn vượt 3 s. Cần đo tách thời gian API, cấp quyền và bytes ảnh trên deployment SCRUM-397. [Draft PR #114](https://github.com/CattyGame/XD-PMHDT/pull/114).

**SCRUM-410** — Code `beca9c6`, 540 Vitest, lint và build PASS, 11 Chromium và 33 ca qua Chrome/Edge/Firefox, CI 8/8. Result tổng hợp live qua cả ba browser. Worklist thiếu ca TEST, PDF HTTP 503 và FCM timeout. Gate live còn chờ manifest SCRUM-397 và coverage Chat/FCM. [Draft PR #114](https://github.com/CattyGame/XD-PMHDT/pull/114).
