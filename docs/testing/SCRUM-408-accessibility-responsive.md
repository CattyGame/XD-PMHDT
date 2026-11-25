# SCRUM-408 · Accessibility và responsive

**TC-W7-M5-01 · 25/11/2026 (UTC+7).** Code kiểm thử `86bd2ca93aafd02ee31a4e24269cfb1542c22db6`, tree `242bb1f970415d101bddad020534091d7c4526fe`, từ `develop@e48b1b0`. [Draft PR #114](https://github.com/CattyGame/XD-PMHDT/pull/114).

## Thay đổi và tiêu chí

- **PASS (tự động, mock)** — Doctor dùng dialog native và mô tả HTTP 409. Kiểm Enter để gửi, focus ban đầu, Tab/Shift+Tab giữa hai nút, Escape và trả focus về nút làm mới. Sau khi làm mới, focus đi vào heading chi tiết. Test RTL giữ kiểm tra khóa bản cũ và tải bản mới. Ca revoke xóa detail và không ghi nháp vào localStorage.
- **PASS (tự động, mock)** — Admin hỗ trợ Arrow Left/Right, Home/End và kích hoạt tab bằng nút native. Tab/panel có tên và liên kết ARIA. Test browser kiểm Arrow Right, Enter và Tab tới ô tìm kiếm. RTL tái sử dụng ca bắt buộc lý do, 409 và thao tác quản trị. Panel xác nhận nhận focus khi mở và hỗ trợ Escape trước khi gửi.
- **PASS (tự động)** — Login có focus outline và các label đọc được. Chromium kiểm Tab tới Email và có outline. `LoginPage.test.tsx` tái sử dụng các ca lỗi đăng nhập, trạng thái xác thực và chặn gửi trùng. Hai trang lỗi công khai 403/500 qua kiểm reflow ở 1024×768.
- **PASS (định lượng CSS, mẫu)** — Một số màu chữ cỡ thường đã điều chỉnh để qua ngưỡng 4,5:1. Login #8493a2 → #586e81 trên trắng (3,15 → 5,30). Admin #0d6efd → #0a58ca trên nền #f5f8fb (4,22 → 6,04), trắng trên nút mới đạt 6,44. Doctor #138595 → #146e80 trên nền workspace (4,04 → 5,44), chữ phụ #637d88 → #536e7b trên trắng (4,36 → 5,41).
- **PASS (Lighthouse hỗ trợ)** — `/login` đạt Accessibility **100/100**, Lighthouse 13.6.0, Google Chrome 158 headless, cấu hình mobile mặc định, Vite loopback với Firebase fixture, một lượt. Báo cáo JSON ở `src/web/aura-web/test-results/lh-login.json` trong checkout test. Phạm vi Lighthouse là trang công khai.
- **PASS (tự động)** — `301beaf` đặt ngôn ngữ tài liệu là tiếng Việt. Ba ca Login công khai tại 1024/1366/1920 tiếp tục qua cùng assertion reflow và xác nhận `lang="vi"`.
- **PASS (smoke live TEST cục bộ)** — Chrome đăng nhập bằng ba vai trò TEST. SuperAdmin mở `/admin` và đọc danh sách clinic HTTP 200. Patient gặp trạng thái từ chối quản trị và sau logout phải đăng nhập lại. Build frontend `beca9c6` dùng Firebase TEST và Gateway loopback.
- **BLOCKED** — Chat SCRUM-394 và các mở rộng SCRUM-389/393 chưa có trong `develop`. Gate live còn chờ candidate deployment khớp SCRUM-397. [PR #102](https://github.com/CattyGame/XD-PMHDT/pull/102) · [PR #103](https://github.com/CattyGame/XD-PMHDT/pull/103) · [PR #104](https://github.com/CattyGame/XD-PMHDT/pull/104).

## Viewport đã thực thi

| Màn hình | 390×844 | 768×1024 | 1366 | 1920 |
| --- | --- | --- | --- | --- |
| Login | PASS | PASS | PASS ×900 | PASS ×900 |
| Personal Upload | PASS mock | PASS mock | SKIPPED | SKIPPED |
| Batch lookup | PASS mock | PASS mock | SKIPPED | SKIPPED |
| Doctor Workspace và ca chi tiết | PASS mock | PASS mock | PASS mock ×768 | PASS mock ×1080 |
| Admin Portal `/admin` | PASS mock | PASS mock | SKIPPED | SKIPPED |
| Consultation Chat | BLOCKED | BLOCKED | BLOCKED | BLOCKED |

Các kiểm reflow dùng chiều rộng tài liệu trừ viewport ≤ 1 px. Doctor và Login còn chạy ở 1024×900. Engine là Chromium đi kèm Playwright 1.65.0 trên Windows. Test responsive mobile/tablet dùng Firebase/API fixture với phiên PATIENT và SYSTEM_ADMIN riêng.

## Thực thi và tái sử dụng

- Vitest `DoctorWorkspacePage.test.tsx`, `AdminManagementPage.test.tsx`, `LoginPage.test.tsx`: 45 PASS. Sau khi hoàn chỉnh focus Admin, chạy lại file Admin: 3 PASS.
- Playwright mock `Doctor approves|offline review`: 6 PASS. Ca nhỏ/mobile + Admin keyboard: 1 PASS. Chạy lại các ca công khai từ `scrum357.e2e.ts`: 5 PASS.
- Tái sử dụng fixture Doctor/Billing và assertion 409/revoke từ `m5-workflows.mock.e2e.ts`. Bổ sung viewport 390/768, kiểm keyboard và ca reflow còn thiếu. Test Doctor RTL mô phỏng show/close cho jsdom. Browser xác minh lifecycle thật.

## Checklist thủ công

| Kiểm tra | Trạng thái |
| --- | --- |
| Zoom trình duyệt thật 200%, kiểm che mất nội dung và khả năng dùng bảng cuộn | SKIPPED |
| Screen reader, thứ tự focus toàn bộ sáu luồng và thông báo loading/empty/null/409/deny | SKIPPED. Chat BLOCKED |
| Contrast thực tế mọi trạng thái tương tác, màu hệ thống và ảnh/gradient | SKIPPED. Đã đối chiếu mẫu màu CSS |
| Môi trường live đúng SHA, sáu luồng với quyền, điều kiện mạng và dữ liệu TEST | BLOCKED — M6 cung cấp candidate SCRUM-397. Ba tài khoản TEST đã dùng cho smoke cục bộ |

Nghiệm thu còn phụ thuộc checklist thủ công và các luồng tích hợp.
