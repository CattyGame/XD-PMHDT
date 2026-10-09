> SUPERSEDED: bộ hợp đồng đang sửa dùng BASELINE-W1-v0.2.md (REST 0.6.0, event/job 0.6). Nội dung bên dưới là lịch sử v0.5, không dùng hash này để kiểm tra v0.6.

# BASELINE-W1-v0.1 - Hợp đồng Analysis REST và event

- Phiên bản file hợp đồng: **0.5.0** (schemaVersion event và job: 0.5)
- Ngày khóa: 2026-10-09
- Người lập: M2 (Võ Hữu Duy). Task: SCRUM-282, SCRUM-280
- Trạng thái: bản dự thảo sau rà soát M2; chờ M1, M4, M5 xác nhận trước khi khóa baseline (xem trang Confluence "Contract Baseline v0.1")
- Tag Git đề xuất: `contract-baseline-w1-v0.1`

## Mã băm SHA-256

| File | SHA-256 |
|---|---|
| `analysis-openapi.yaml` | `94a3a148e85577dc9095be12a49944ea226306f60bc1eac45fdb44b816ff84e2` |
| `analysis-events.schema.json` | `361bd618b1d51f99fd02e692ffba263d7a1d4a1ac73f776a7c84dd0285d5a954` |
| `analysis-job.schema.json` | `83d21209a93c3089a61502fe46cdb32d9492e1f3b77b18b64f4dc8522be7b1df` |
| `examples/analysis-completed-200.json` | `31047e4e66b8b9310749110bfb903c6581d502c7f0f5a7392a97d83604d5d107` |
| `examples/analysis-duplicate-200.json` | `54acad26802e844af66e9a1c23c68173f1236beed53319eba4a9a1dafda1834b` |
| `examples/analysis-failed-200.json` | `b08eb7f7168e891721b22ca1a74a633cb1a6462402ec56616abce7a58f187645` |
| `examples/analysis-low-quality-200.json` | `7b2c122b01023f5877f8331a2a0dcb88c41280a3d92677861730c1ede4b52ccb` |
| `examples/analysis-processing-200.json` | `710a523c80fea73f7f46302fa05386893fab2bc85290027718a71f2c48475726` |
| `examples/analysis-reviewed-200.json` | `d619f1ec1d466991f15999a7b063bf0734cfcbc82c6d08c44231f405055b1900` |
| `examples/batch-200.json` | `3e54cc401a0bb6d71837973bf9c1d33014abf7c23371f196d0f945ef36d393cf` |
| `examples/create-analysis-202.json` | `db1f79fa9d0fa85ab847c9a6ea50c7cebb3430340300d42e85cc73073cee34ae` |
| `examples/error-400.json` | `31835f0f7922f83aa9174d98de2933b4af07bd625bb20e716ce3a705d04ec48d` |
| `examples/error-401.json` | `5382ebfbcd087537bda20c82959b115a342fd9ad017024612657c9adb5f68ef9` |
| `examples/error-403.json` | `d404325db58b6d4bd38892f8bba341f5eab5893538632996a9b2a5a43b31cc8e` |
| `examples/error-409.json` | `8062dfb55e19068ec1a42e36a564151a76d0a6ac82fa4da10eb65aed6aa4be7c` |
| `examples/event-completed.json` | `006f1eec30c6f77acd55efd8f130804b106096c0e0988a117ed4b6624670faaf` |
| `examples/event-failed.json` | `b62ab623e5ca4e4624212c38050ee3754a2b3d0919f1c9cfce2db4f303f59ba9` |
| `examples/event-requested.json` | `92ca4bf5c005c012b5185e7ca2b2a8be9b3c05a38000007dd3e69daa720bd76e` |
| `examples/event-reviewed.json` | `b46454e12210561ec00fa94b1ccd7a17e47ed1ad7cabf5c0373d23180aa9486d` |
| `examples/file-access-200.json` | `5e3b2d8b77b6f8185ef91a6ed64cb979e0d488a958ed47ee98781f4c566d73b7` |
| `examples/job-run.json` | `febec261e644c9d2bf012d829b160bf163380375081ded56b942095cb42ccfdc` |

Kiểm tra trên Linux hoặc Git Bash: `sha256sum contracts/analysis-openapi.yaml`.

## Quy tắc thay đổi

1. Không sửa trực tiếp các file trên sau khi khóa; mở task Jira nêu file, lý do, người bị ảnh hưởng.
2. Đổi tên hoặc kiểu trường, enum, mã lỗi là thay đổi phá vỡ: tăng baseline (v0.2) và báo M1, M3, M4, M5 trước khi gộp.
3. Chỉ thêm trường tùy chọn, ví dụ, mô tả: tăng phiên bản phụ (0.5.1), ghi lịch sử.
4. Sau mỗi thay đổi: kiểm tra lại OpenAPI, ví dụ, schema; cập nhật mã băm và file này; tạo tag mới.


## Ghi chú cập nhật sau rà soát M2

Bản dự thảo này đã cập nhật SHA-256 theo các file hiện tại trong gói làm việc. Các hash này chỉ xác định đúng nội dung của gói này; không chứng minh đã được nhóm nghiệm thu hoặc đã có commit/tag phát hành. Sau khi M1/M4/M5 duyệt, cần chốt phiên bản hợp đồng, commit SHA và tag baseline tương ứng.
