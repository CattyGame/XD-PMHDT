# Artifact Registry & Hướng dẫn Tái lập Đúng Giấy phép — Module M4 (`W8-M4-01` / `SCRUM-423`)

**Truy vết RTM:** `NFR-16` (Cập nhật model/config không downtime & rollback), `NFR-21` (REST contract AI), `NFR-23` (Truy vết phiên bản model/config/threshold/hash trên từng báo cáo).
**Test ID thiết kế:** `TC-W8-M4-01` (`src/ai-core/tests/test_w8_m4_sprint8.py`).
**Registry máy đọc được:** [`docs/benchmarks/w8_artifact_registry_v1.0.json`](../benchmarks/w8_artifact_registry_v1.0.json).
**Trạng thái lâm sàng:** `clinicalValidation = false`, `icdrGradingSupported = false`, gate khoa học `SCRUM-373` giữ mở độc lập (`OPEN_NOT_PASSED`).

---

## 1. Danh mục Artifact Thực tế, Định dạng, Hash & Giấy phép

Git repository **không chứa** file trọng số nhị phân (`*.pt`, `*.safetensors`, `*.onnx`), bộ ảnh dataset giải nén (`datasets/`) hay credentials/keys (`.env`, `*.pem`, `*.key`). Người dùng có quyền hợp lệ kiểm tra và tái lập từ cấu hình + manifest đã pin SHA-256 bên dưới:

| Artifact ID | Định dạng thực tế | Đường dẫn | SHA-256 (`LF` với text / `Raw` với binary) | Nguồn & Cách lấy | Giấy phép |
|---|---|---|---|---|---|
| `frangi-baseline-config-v1.0` | YAML Config (`ONNX: không`) | `src/ai-core/config/ai-frangi.yaml` | `0b0451cad981b69471920b374c19042f169b991a26c49e2c3e29395db7c6a0ee` | Có sẵn trong Git (ngưỡng `0.02` chốt trên CHASE_DB1 val) | Internal coursework config (AURA SP26SE025) |
| `frangi-vectorized-config-v1.3` | YAML Config (`ONNX: không`) | `src/ai-core/config/ai-frangi-v1.3.yaml` | `dd444ce85afdcda830cdc825ace92d34d13e476739bdd2575890be13e8305087` | Có sẵn trong Git (6-pass Hessian + heuristic tortuosity) | Internal coursework config (AURA SP26SE025) |
| `frangi-hrf-clahe-config-v1.0` | YAML Config (`ONNX: không`) | `src/ai-core/config/ai-frangi-hrf.yaml` | `8af374dc5dc1e70df09e333730e46a27c40e51a9f76e554498d3fa8e27119aca` | Có sẵn trong Git (HRF OpenCV CLAHE research profile) | Internal coursework config (AURA SP26SE025) |
| `dr-l2-spectral-13f-weights` | JSON weights (`không dùng pickle/joblib`) | `src/ai-core/config/dr_screening_weights.json` | `3916060233716b0d0435527464d8ad10d5bb3e63e5c7ff1011a43041c7a53446` | Tái lập bằng `train_dr_screening.py` từ `HRF45` | Academic Research derivative (FAU HRF) |
| `dr-resnet34-hrf-v1-pytorch` | PyTorch `.pt` (`state_dict`) | `model-artifacts/dr-resnet34-hrf-v1/model.pt` | `8c9114c54f1b06300081bb82b80a0ea3a10e3d035232e1c5f3570b2db42df8f7` | Tái lập local bằng `train_dr_cnn.py` (`seed=42`, epoch `2`) | `BSD-3-Clause` (`IMAGENET1K_V1`) + FAU HRF Research |
| `avr-rrwnet-rite-safetensors` | SafeTensors | `model-artifacts/avr-rrwnet-rite/model.safetensors` | `32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485` | Tải pin revision `9b61e904...` qua `fetch_avr_rrwnet.py` | **CC-BY-NC-4.0** (nghiên cứu phi thương mại; code wrapper MIT) |
| `avr-lunetv2-odc-onnx` | **ONNX** (chỉ có cho OD localization) | `model-artifacts/avr-rrwnet-rite/lunetv2_odc.onnx` | `f72f6c9ea2ade798b80539de77fa610bb6553bb0fd7fcb78d1e841982cae0b5f` | Tải pin revision `cf1f116d...` qua `fetch_avr_rrwnet.py` | PVBM / RLAD Research License (`third_party/pvbm/LICENSE`) |

> **Lưu ý về ONNX & Joblib:** Chỉ thành phần định vị đĩa thị `lunetv2_odc.onnx` thực sự dùng định dạng `ONNX`. Mô hình Frangi v0.2 là bộ lọc giải tích không có trọng số (không quy đổi sang ONNX/INT8/FP16), còn bộ phân loại tuyến tính 13 đặc trưng DR lưu hệ số bằng JSON thuần (`dr_screening_weights.json`) để loại bỏ hoàn toàn rủi ro deserialization của `pickle`/`joblib`.

---

## 2. Danh mục Dataset, Giấy phép & Quy tắc Cấm Phân phối lại (`Iowa40`)

1. **`CHASE_DB1` (28 ảnh / 14 đối tượng, `999×960`)**: Giấy phép `CC BY 4.0`. Split cố định `16 train / 6 val / 6 test` trong `docs/datasets/chase_db1/manifest.csv` (`sha256_lf: b22bb7f635aad4c1ef670abd15a68e038d9c5d9a1383a01976f8ae7664a6455a`).
2. **`HRF45` (45 ảnh: 15 DR / 15 Healthy / 15 Glaucoma OOD)**: Giấy phép Academic Research Use (FAU). Split theo bệnh nhân `18 train / 6 val / 6 test` + `15 glaucoma_ood` tách riêng trong `docs/datasets/hrf/manifest.csv` (`sha256_lf: a6d0d719066d01e113ca2e1ade0e89324e99ce36ef5772afd1742a55973a11ce`).
3. **`AVRDB100` (99 ca hợp lệ, bỏ `IM000189` rỗng)**: Giấy phép `CC BY 4.0` (DOI `10.17632/3csr652p9y.2`). Split `70 train / 15 val / 14 test` trong `docs/datasets/inspire_avr/manifest.csv` (`sha256_lf: 2ae7cba1d278178fc337dd622a7fba12609e2afbba0d9af1eefc980c89ac9211`).
4. **`Iowa40` (40 ca bảng chuẩn 2 chuyên gia Đại học Iowa — `SCRUM-373`)**:
   - **Cấm tuyệt đối** đóng gói hoặc phân phối lại trong Git (`redistributedInGit = false`) và trong mọi file ZIP (`redistributedInZip = false`).
   - Không dùng `Iowa40` để hiệu chỉnh ngưỡng (`calibrationAllowed = false`).
   - Người đánh giá có quyền hợp lệ xin cấp dataset trực tiếp từ chủ sở hữu học thuật Đại học Iowa, đặt tại thư mục ngoài Git và chạy lệnh đo độc lập bên dưới.

---

## 3. Hướng dẫn Tái lập Từ Máy Sạch (Dành cho Người có Quyền)

Thực thi lần lượt từ thư mục gốc repository:

```bash
# Bước 1: Tạo môi trường Python 3.12 sạch theo lockfile runtime & research
python -m pip install -r src/ai-core/requirements.lock -r src/ai-core/requirements-dev.txt
# (Tùy chọn nếu tái lập ResNet34 / RRWNet / ONNX OD trên máy nghiên cứu)
python -m pip install torch torchvision safetensors onnxruntime

# Bước 2: Giải nén và xác minh manifest 3 dataset công khai (không có Iowa40)
PYTHONPATH=src/ai-core python src/ai-core/scripts/prepare_chase_db1.py
PYTHONPATH=src/ai-core python src/ai-core/scripts/prepare_hrf.py
PYTHONPATH=src/ai-core python src/ai-core/scripts/prepare_avrdb.py

# Bước 3: Tải trọng số AVR nghiên cứu (RRWNet CC-BY-NC-4.0 + LU-Net v2 ONNX) với kiểm tra SHA-256 fail-closed
python src/ai-core/scripts/fetch_avr_rrwnet.py --destination model-artifacts/avr-rrwnet-rite

# Bước 4: Tái huấn luyện checkpoint DR nhị phân HRF45 (tách bệnh nhân, epoch theo Val AUC)
PYTHONPATH=src/ai-core python src/ai-core/scripts/train_dr_cnn.py --output model-artifacts/dr-resnet34-hrf-v1/model.pt --epochs 100

# Bước 5: Chạy kiểm định toàn bộ Registry, Protocol V2, Latency/RAM và Bộ test Sprint 6-8
python src/ai-core/scripts/manage_w8_artifact_registry.py --verify
python src/ai-core/scripts/verify_model_card_v2_registry.py
PYTHONPATH=src/ai-core python src/ai-core/scripts/evaluate_v2_protocol.py --verify docs/benchmarks/v2_evaluation_report_s7.json
python src/ai-core/scripts/benchmark_w7_latency_ram.py --verify docs/benchmarks/w7_latency_ram_profile_v1.0.json
cd src/ai-core && python -m pytest tests -q

# Bước 6 (Chỉ khi người đánh giá có quyền sở hữu ngoài bộ dữ liệu Iowa40):
PYTHONPATH=src/ai-core python src/ai-core/scripts/benchmark_expert_references.py --dataset iowa --root <iowa40_dir> --output /tmp/iowa40_eval.json
```

---

## 4. Cơ chế Fail-Closed khi Sai Hash & Rollout Không Downtime (`NFR-16`, `NFR-23`)

1. **Sai SHA-256 luôn dừng ngay (Fail-Closed):**
   - Trong `manage_w8_artifact_registry.py --verify` hoặc `--verify-artifact <id> --file <path>`, chỉ cần sai `1` byte so với digest trong registry sẽ thoát với mã lỗi khác `0`.
   - Trong runtime API `RuntimeManifestRegistry` (`src/ai-core/app/services/runtime_registry.py`), kích hoạt manifest sai hash trả `HTTP 409 ERR_HASH_MISMATCH`; thiếu hoặc hỏng checkpoint DR/AVR trong chế độ tích hợp trả `HTTP 503 RESEARCH_MODEL_UNAVAILABLE` — **tuyệt đối không fallback sang `mock` hoặc tải trọng số từ URL tự do**.
2. **Đóng gói Repro Pack Sạch (Không gồm Iowa / Secrets):**
   - Lệnh `python src/ai-core/scripts/manage_w8_artifact_registry.py --bundle-repro-pack <out.zip>` chỉ đóng gói cấu hình YAML/JSON, split manifests CSV, license/NOTICE, registry JSON và hướng dẫn này kèm `PACK_MANIFEST.json`, đồng thời từ chối fail-closed nếu phát hiện bất kỳ file Iowa, key/token hay ảnh thô nào.
3. **Cập nhật không downtime:** Quản trị viên chuyển đổi giữa 3 manifest đã ký sẵn trong `src/ai-core/config/artifact-manifests.json` qua cơ chế `smoke_runner` trước chuyển mạch nguyên tử, giữ nguyên snapshot cho request đang chạy (`in-flight pinning`) và hỗ trợ rollback về manifest liền trước.

---

## 5. Giới hạn & Protocol đi kèm Registry

- **Không dùng nhãn tham chiếu lúc suy luận (`rgbOnlyInference = true`):** Toàn bộ hàm dự đoán chỉ nhận ảnh RGB.
- **Binary DR không phải ICDR:** Mô hình `ResNet34` trên `HRF45` chỉ phân biệt `DR` vs `Healthy`, bản đồ giải thích `GRAD_CAM` bám đúng geometry `IMAGENET_CENTER_CROP_224` (`Resize 256 -> CenterCrop 224×224`).
- **AVR `unavailable -> null + reason`:** Không thay bằng `0` hay mức `LOW`; luôn tính ca `unavailable` vào mẫu số `coverage`.
- **Không tự đóng `SCRUM-373`:** Gate độ lệch `|bias| <= 0.02` trên `Iowa40` giữ nguyên trạng thái `OPEN_NOT_PASSED`.
