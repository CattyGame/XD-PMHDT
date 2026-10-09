# BASELINE-W1-v0.2 — Analysis contracts

- REST: 0.6.0; event/job schemaVersion: 0.6.
- Trạng thái: dự thảo đồng bộ ERD/State Machine/ma trận quyền v0.3; chưa nghiệm thu tích hợp.
- Nguồn ban đầu: develop 5070ab6; các file trong gói này là thay đổi chưa commit trên nhánh sửa.
- Commit phát hành: chưa có; điền SHA thực trong biên bản Confluence sau commit, dùng link cố định.
- Job message attempt ánh xạ REST Job.attemptNo; retryCount = attemptNo - 1.
- Annotation public giữ fileId/kind, không đổi thành type trong REST.
- Mã băm: SHA-256 bytes UTF-8 với CRLF chuẩn hóa thành LF; không thay đổi khoảng trắng/nội dung khác.
- File baseline này không tự băm chính nó.

## Kiểm chứng

python scripts/validate_analysis_contract.py

## Mã băm

| File | SHA-256 |
|---|---|
| `contracts/BASELINE-W1-v0.1.md` | `48609405b906da467a48b1854e1cd71178b64aeff8b2501e4044bad0862f99f2` |
| `contracts/analysis-events.schema.json` | `e780da6c072746ca92a2f921a5594873c2971d416fab26c09333b0ca184412b4` |
| `contracts/analysis-job.schema.json` | `1892c2f57a18211798cd31d1573d798bc7b8649cc740ee2180fbf810ab2202bd` |
| `contracts/analysis-openapi.yaml` | `a0a3448e2b5c4a19d75bcd7aa295a5d7fb68233e332bfa4487e90be05f35ef9c` |
| `contracts/examples/analysis-completed-200.json` | `32409a7095a7ed0401816ad8adc98d764e31f9c4ee71b570ec752b1b6a0f578f` |
| `contracts/examples/analysis-duplicate-200.json` | `c8c4668b3d4fd95f0d22f9f47fd8087ad1b201e9fdf5976bb31122c63e29d995` |
| `contracts/examples/analysis-failed-200.json` | `f1e8e8212c8be4e49ce7fe2b1af9970d1f6c4d9aaa47bfd226fcccf4a9dd96d2` |
| `contracts/examples/analysis-low-quality-200.json` | `d877db690604756b6ebc9581082e406a042d196e13d7ef984284b6a80e2ff351` |
| `contracts/examples/analysis-processing-200.json` | `7835b1241f4379cd3e5dbf0d63a767aab52a0959a5895f1182490ebcec8feca3` |
| `contracts/examples/analysis-reviewed-200.json` | `05d8e097642f7e60c1c028741a6ffbe44569464169e4299e04d57793d7274cfb` |
| `contracts/examples/analysis-warning-200.json` | `26724b0ee18cea68f80ea612ad7612f811dae637e8a9ce4ef2466a4692ed1e27` |
| `contracts/examples/batch-200.json` | `483cc4d740f513c9958d41c832f4910b7203e9621b668713ee2449e65005e94e` |
| `contracts/examples/create-analysis-202.json` | `69368ed46cb322fe4647bd6f5256ecedd41bfb5854ed92eb29f647ba03e8430a` |
| `contracts/examples/error-400.json` | `c576f0580345fb2dc53152e59f10524755b60285cc02a8a708a2d0fd5f78c3c7` |
| `contracts/examples/error-401.json` | `3ee7a6e5778b29ea3eab5ba5b54a8681d2d036dc16309e49e6b8af32720bd988` |
| `contracts/examples/error-403.json` | `21cf4c6c509d8dda068322a986a114ce73aadf449d263632d7e46eed14a04e15` |
| `contracts/examples/error-404.json` | `1e0294da181b78a9fd19656bf20669aab932abba567c80881eea12085cdfdfd0` |
| `contracts/examples/error-409.json` | `741c2a578397d670f10edc4421ecc00ee57cae459fef8d7d40874f0b5a0db18b` |
| `contracts/examples/error-413.json` | `afda055ee77920fb8df7e4e863a6744a81a88ea2f3a1fd145f51adc1e7b6c137` |
| `contracts/examples/error-415.json` | `9f7a1af3da497e80997dad8f39ab8d6444a5623bb3427c6f57229535a1296c88` |
| `contracts/examples/error-429.json` | `867b7961d01f34c3a97371f655f81c0b86762e0c62f432258e2d50e09b29a7fb` |
| `contracts/examples/event-completed.json` | `df04bae5f6e6a5c68cc654cad132880c5f073db4e846a393cdc6b4bdb1f21c39` |
| `contracts/examples/event-failed.json` | `aca6b83e6b1088f4db0817188cb5ae8d92e47ea911f90fc96fe00beca6b3921b` |
| `contracts/examples/event-requested.json` | `b463f5c3a7b920f25cc0bea927211f3a964b2685a179470fcb10f1e04c98ea6d` |
| `contracts/examples/event-reviewed.json` | `f899b77e62a94ba283b25c339faf7ae44a6654a7ab46c121fa90e91d01c3c4ab` |
| `contracts/examples/file-access-200.json` | `e702d8e6594c051b1ff46a0da66fb1bfa589b5f264399b70e0cf67771a99b86f` |
| `contracts/examples/job-run.json` | `8f15767ff1a3997b773f7584c2f98efdc99c12528a62ce34a907d299decfd693` |
| `contracts/examples/review-approve-request.json` | `372abe9bc2e156babc1a0424ab1340481d06c82ba8d3fda752ef0e2b3f78d6d9` |
| `contracts/examples/review-correct-request.json` | `bf8a3b5ee63f254804b1ae51b25c892f6551a4cc4e267a87c03b477399f3507a` |
| `requirements-contract.txt` | `eb8344ade5180aed4b1cc2e21edbda01c90586c29b3c663dffb9e4852a5ed342` |
| `scripts/validate_analysis_contract.py` | `b7b2d5be0483edebf70adfd6a4c48f54f4ac4b659f5041b63727633411a7b874` |

## Kiểm soát thay đổi

Thay đổi kiểu/ràng buộc/trường bắt buộc cần nâng phiên bản và thông báo các bên. Cập nhật schema, examples và hash cùng nhau; chạy validator trước commit. Không coi schema pass là nghiệm thu backend.
