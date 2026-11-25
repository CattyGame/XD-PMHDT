# Bài nói AI Nghiên cứu 3 Phút & Minh họa Ca Đo được vs. Không Đo được (`W8-M4-03` / `SCRUM-425`)

**Owner:** Đào Duy Quân (Module M4 — AI Core)
**Truy vết RTM:** `FR-3`, `FR-4` · **Test ID:** `TC-W8-M4-03`
**Kiểm định tự động:** `python src/ai-core/scripts/verify_w8_defense_script.py --verify`

---

## 1. Kịch bản Bài nói Bảo vệ 3 Phút Có Bấm Giờ (`[0:00–3:00]`)

<!-- SPEECH_TRANSCRIPT_START -->
[0:00–0:40] Thưa hội đồng, trong đồ án AURA, Module M4 phụ trách dịch vụ suy luận ảnh đáy mắt AI Core theo hợp đồng REST 1.3.0 qua xác thực RS256 Service JWT nội bộ. Ở lớp nền mặc định, mô hình `frangi-v0.2` sử dụng bộ lọc Hessian 2D tách chiều 6 lượt trên kênh xanh lá, giữ nguyên kích thước ảnh gốc thay vì ép crop 512×512. Toàn bộ cổng kiểm tra chất lượng đầu vào chạy trước suy luận để chặn ảnh tối, lóa, mờ hoặc sai định dạng như OCT, giúp độ chính xác tương đương tuyệt đối Golden Parity `Dice = 1.000000` và độ trễ p95 khoảng 1.65 giây, đạt ngưỡng SLA dưới 20 giây của SRS.

[0:40–1:25] Với nhánh nghiên cứu V2 kích hoạt tường minh, mô hình sàng lọc DR nhị phân dùng backbone `ResNet34` đóng băng kết hợp lớp tuyến tính trên tập `HRF45`, đạt Validation AUC 0.8889 và tách riêng 15 ca glaucoma sang tập OOD. Chúng em khẳng định đây chỉ là phân loại nhị phân nghiên cứu giữa DR và nhóm khỏe mạnh, hoàn toàn không phải phân độ 5 mức ICDR hay chẩn đoán lâm sàng. Bản đồ giải thích `GRAD_CAM` tính gradient thật tại `layer4` và tuân thủ đúng hệ quy chiếu `IMAGENET_CENTER_CROP_224`, tức resize cạnh ngắn về 256 rồi cắt vùng trung tâm 224×224, không tự ý kéo giãn phủ toàn khung ảnh gốc làm sai lệch vị trí giải phẫu.

[1:25–2:20] Thành phần thứ ba là đo tỷ số động-tĩnh mạch AVR hình học bằng `RRWNET_RITE` kết hợp định vị đĩa thị `lunetv2_odc.onnx` trong vành khăn `Zone B` từ 2 đến 3 bán kính đĩa thị, áp dụng công thức ghép cặp `Knudtson` cải tiến cho đúng 6 động mạch và 6 tĩnh mạch độc lập. Chúng em minh họa hai trường hợp đối nghịch trực tiếp từ mã nguồn. Ở ca đo được, khi đĩa thị rõ và đủ 6 nhánh mỗi loại, hệ thống đặt `measurement_available = true`, trả `crae_px`, `crve_px` và tỷ số `AVR` khớp đúng `metrics.av_ratio`, đồng thời giữ `risk_available = false`. Ngược lại ở ca không đo được, khi không thấy đĩa thị, `Zone B` bị cắt biên hoặc thiếu đủ 6 cặp mạch, hệ thống bắt buộc trả `measurement_available = false`, giữ `avr = null` cùng `unavailable_reason` và cảnh báo `AVR_UNAVAILABLE`, tuyệt đối không giả lập số 0 hay gán mức nguy cơ thấp, và vẫn tính ca thiếu này vào mẫu số độ phủ.

[2:20–3:00] Về vai trò chuyên môn và giới hạn khoa học, AI Core là công cụ hỗ trợ kỹ thuật phi lâm sàng với `clinicalValidation = false`; mọi nhận định ICDR chính thức chỉ thuộc về bác sĩ chuyên khoa mắt trên giao diện duyệt ca `Doctor Review`, không bao giờ tự sinh nhãn xác nhận bác sĩ từ đầu ra tự động. Đồng thời, cổng đánh giá khoa học `SCRUM-373` yêu cầu sai số tuyệt đối dưới 0.02 trên bảng chuẩn hai chuyên gia 40 ca Đại học Iowa hiện vẫn giữ nguyên trạng thái `OPEN_NOT_PASSED` với độ lệch baseline surrogate `-0.2136`. Em xin cảm ơn hội đồng.
<!-- SPEECH_TRANSCRIPT_END -->

---

## 2. Minh họa 2 Ca Đối chiếu Chuẩn Hợp đồng `AnalysisResponse` (`1.3.0`)

Các số liệu AVR trong Ca 1 (`crae_px = 10.744`, `crve_px = 19.317`, `avr = 0.5561940259874721`) và lý do dừng ở Ca 2 (`INSUFFICIENT_SIX_VESSEL_SETS`) được sinh trực tiếp từ hàm `measure_avr_prediction()` trong `src/ai-core/app/services/avr_service.py` (xác minh sống qua `verify_w8_defense_script.py --print-live`).

### 2.1. Ca 1 — Đo được AVR (`measurement_available = true`, `RESEARCH_ONLY`)

- **Ngữ cảnh tương đương thực tế:** Tương ứng ca fundus đủ sáng rõ, đĩa thị nằm trong khung ảnh, `Zone B (2R–3R)` trọn vẹn và tìm đủ 6 động mạch + 6 tĩnh mạch độc lập (ví dụ `IM001246` trong cohort `AVRDB100`).
- **Trên Web UI (`AnalysisResultPage.tsx`) & PDF (`MedicalReportPdfRenderer.cs`):** Hiển thị giá trị `avRatio = 0.556194...`, `CRAE = 10.744 px`, `CRVE = 19.317 px`, xác suất DR nhị phân + chú thích `Grad-CAM DR — crop 224×224`, đồng thời giữ mục đánh giá nguy cơ lâm sàng ở trạng thái chưa hỗ trợ (`risk_available = false`, chờ bác sĩ review).

<!-- CASE_MEASURED_JSON -->
```json
{
  "request_id": "11111111-1111-4111-8111-111111110801",
  "patient_id": null,
  "timestamp": "2026-11-24T06:15:00Z",
  "status": "WARNING",
  "warnings": [
    {
      "code": "RISK_UNAVAILABLE",
      "message": "Research configuration only; clinical risk assessment is disabled."
    },
    {
      "code": "RESEARCH_RESULTS",
      "message": "AVR và DR là kết quả nghiên cứu. Grad-CAM dùng crop 224×224, không phải mask tổn thương trên toàn ảnh."
    }
  ],
  "is_mock": false,
  "model_version": "frangi-v0.2",
  "config_version": "s3-frangi-1.0.0-integrated-research-v1",
  "threshold_version": "frangi-threshold-0.02-v1",
  "limitations": "Segmentation research baseline; no clinical risk assessment. Native-pixel AVR V8 and binary HRF DR/Grad-CAM research; no clinical validation or ICDR grading.",
  "processing_time_ms": 1482.5,
  "image_info": {
    "width": 512,
    "height": 512,
    "channels": 3,
    "eye_side": "left",
    "modality": "FUNDUS"
  },
  "metrics": {
    "vessel_density": 0.0642,
    "tortuosity_index": null,
    "av_ratio": 0.5561940259874721,
    "fractal_dimension": null,
    "branching_points": null
  },
  "risk_available": false,
  "risk_assessment": null,
  "segmentation": {
    "mask_format": "png_base64",
    "mask_base64": "iVBORw0KGgo=",
    "overlay_base64": "iVBORw0KGgo=",
    "av_overlay_base64": "iVBORw0KGgo=",
    "gradcam_base64": "iVBORw0KGgo="
  },
  "research": {
    "avr": {
      "measurement_available": true,
      "avr": 0.5561940259874721,
      "crae_px": 10.744,
      "crve_px": 19.317,
      "unavailable_reason": null,
      "model_version": "avr-rrwnet_rite-native-edge-v8-32d93fa770f2",
      "model_sha256": "32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485"
    },
    "dr": {
      "probability_dr": 0.526169,
      "model_version": "resnet34-8c9114c54f1b0630",
      "checkpoint_sha256": "8c9114c54f1b06300081bb82b80a0ea3a10e3d035232e1c5f3570b2db42df8f7",
      "gradcam_method": "GRAD_CAM",
      "gradcam_geometry": "IMAGENET_CENTER_CROP_224"
    }
  }
}
```

### 2.2. Ca 2 — Không đo được AVR (`measurement_available = false`, Abstention)

- **Ngữ cảnh tương đương thực tế:** Ảnh không đủ 6 động mạch hoặc 6 tĩnh mạch tách biệt trong `Zone B` (`INSUFFICIENT_SIX_VESSEL_SETS`), đĩa thị sát rìa làm `Zone B` bị cắt biên (`INCOMPLETE_ZONE_B`), hoặc không định vị được đĩa thị (`OPTIC_DISC_NOT_LOCALIZED`).
- **Hành vi fail-closed bắt buộc:** Trả `measurement_available = false`, `avr = null`, `crae_px = null`, `crve_px = null`, `metrics.av_ratio = null`, đính kèm cảnh báo `AVR_UNAVAILABLE` và vẫn giữ kết quả phân vùng mạch máu + xác suất DR nhị phân độc lập (tuyệt đối không điền `0`, không suy ra `LOW` risk, và không loại ca này khỏi mẫu số `coverage`).

<!-- CASE_UNAVAILABLE_JSON -->
```json
{
  "request_id": "22222222-2222-4222-8222-222222220802",
  "patient_id": null,
  "timestamp": "2026-11-24T06:16:00Z",
  "status": "WARNING",
  "warnings": [
    {
      "code": "RISK_UNAVAILABLE",
      "message": "Research configuration only; clinical risk assessment is disabled."
    },
    {
      "code": "AVR_UNAVAILABLE",
      "message": "Chưa đủ điều kiện tính AVR: INSUFFICIENT_SIX_VESSEL_SETS"
    },
    {
      "code": "RESEARCH_RESULTS",
      "message": "AVR và DR là kết quả nghiên cứu. Grad-CAM dùng crop 224×224, không phải mask tổn thương trên toàn ảnh."
    }
  ],
  "is_mock": false,
  "model_version": "frangi-v0.2",
  "config_version": "s3-frangi-1.0.0-integrated-research-v1",
  "threshold_version": "frangi-threshold-0.02-v1",
  "limitations": "Segmentation research baseline; no clinical risk assessment. Native-pixel AVR V8 and binary HRF DR/Grad-CAM research; no clinical validation or ICDR grading.",
  "processing_time_ms": 1410.2,
  "image_info": {
    "width": 512,
    "height": 512,
    "channels": 3,
    "eye_side": "right",
    "modality": "FUNDUS"
  },
  "metrics": {
    "vessel_density": 0.0318,
    "tortuosity_index": null,
    "av_ratio": null,
    "fractal_dimension": null,
    "branching_points": null
  },
  "risk_available": false,
  "risk_assessment": null,
  "segmentation": {
    "mask_format": "png_base64",
    "mask_base64": "iVBORw0KGgo=",
    "overlay_base64": "iVBORw0KGgo=",
    "av_overlay_base64": "iVBORw0KGgo=",
    "gradcam_base64": "iVBORw0KGgo="
  },
  "research": {
    "avr": {
      "measurement_available": false,
      "avr": null,
      "crae_px": null,
      "crve_px": null,
      "unavailable_reason": "INSUFFICIENT_SIX_VESSEL_SETS",
      "model_version": "avr-rrwnet_rite-native-edge-v8-32d93fa770f2",
      "model_sha256": "32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485"
    },
    "dr": {
      "probability_dr": 0.41474,
      "model_version": "resnet34-8c9114c54f1b0630",
      "checkpoint_sha256": "8c9114c54f1b06300081bb82b80a0ea3a10e3d035232e1c5f3570b2db42df8f7",
      "gradcam_method": "GRAD_CAM",
      "gradcam_geometry": "IMAGENET_CENTER_CROP_224"
    }
  }
}
```

---

## 3. Bảng Đối chiếu Thuật ngữ ↔ Mã nguồn (`Source Alignment`)

| Thuật ngữ trong bài nói | Định danh trong mã nguồn / Contract | Đường dẫn xác minh |
|---|---|---|
| Frangi v0.2 Hessian 6 lượt tách chiều | `fast_vectorized_frangi_2d`, `model_version="frangi-v0.2"` | `src/ai-core/app/services/frangi_service.py` |
| DR nhị phân nghiên cứu (không ICDR) | `screen_dr_cnn`, `task="hrf_dr_vs_healthy"` | `src/ai-core/app/services/dr_cnn_service.py` |
| Grad-CAM đúng vùng crop 224×224 | `gradcam_method="GRAD_CAM"`, `gradcam_geometry="IMAGENET_CENTER_CROP_224"` | `src/ai-core/app/schemas/analysis.py`, `dr_cnn_service.py` |
| AVR hình học Zone B Knudtson | `compute_knudtson_crae` (`0.88`), `compute_knudtson_crve` (`0.95`), `2R–3R` | `src/ai-core/app/services/avr_service.py`, `avr_geometry.py` |
| Chính sách từ chối đo AVR (`null + reason`) | `OPTIC_DISC_NOT_LOCALIZED`, `INCOMPLETE_ZONE_B`, `INSUFFICIENT_SIX_VESSEL_SETS` | `src/ai-core/app/services/avr_service.py` |
| Vai trò Bác sĩ & không tự sinh `expert_confirmed` | `DoctorReportReview` chỉ xuất ICDR khi `review?.IsApproved == true` | `src/services/AURA.Analysis.Api/Upload/MedicalReportPdfRenderer.cs` |
| Gate khoa học mở độc lập | `SCRUM-373`, `status="OPEN_NOT_PASSED"`, `autoCloseAllowed=false` | `docs/benchmarks/v2_evaluation_report_s7.json` |

---

## 4. Hỏi-Đáp Phản biện Hội đồng Thường gặp (Q&A)

1. **Tại sao xác suất DR từ `ResNet34` không chia thành 5 độ ICDR?**
   Vì `HRF45` chỉ có nhãn nhị phân `DR` vs `Healthy` (18 train / 6 val / 6 test, tách riêng `15` ca glaucoma). Hệ thống không được phép suy diễn hay bịa nhãn 5 mức ICDR khi thiếu dữ liệu nhãn bác sĩ đa trung tâm.
2. **Tại sao ảnh Grad-CAM không khớp nguyên khung 999×960 mà chỉ là 224×224?**
   Transform chuẩn của `ResNet34_Weights.IMAGENET1K_V1` thực hiện `Resize(shorter_edge=256)` rồi `CenterCrop(224×224)`. Hiển thị đúng ảnh crop 224×224 bảo đảm nhiệt độ gradient khớp 100% với đặc trưng mạng nhìn thấy (`IMAGENET_CENTER_CROP_224`).
3. **Khi `AVR = null`, người dùng hoặc bác sĩ có bị hiểu nhầm thành nguy cơ thấp (`LOW`) không?**
   Không. Hợp đồng AI Core, Web UI (`AnalysisResultPage.tsx`) và PDF (`MedicalReportPdfRenderer.cs`) đều tách riêng `measurement_available = false`, hiển thị rõ `unavailable_reason` + câu nhắc *"Thiếu AVR không có nghĩa nguy cơ thấp."*
4. **Tại sao không nén hay xóa bớt ca `unavailable` khỏi tỷ lệ đánh giá?**
   Giữ ca `unavailable` trong mẫu số `coverage = measured / denominator` ngăn việc "chọn ca dễ" (cherry-picking) làm đẹp chỉ số độ chính xác một cách giả tạo.
5. **Trọng số PyTorch `.pt` / `.safetensors` / `.onnx` nằm đâu nếu Git không lưu?**
   Git lưu manifest SHA-256 + giấy phép tại `docs/benchmarks/w8_artifact_registry_v1.0.json`. Máy triển khai dùng `fetch_avr_rrwnet.py` hoặc `train_dr_cnn.py` theo hướng dẫn tại `docs/data_cards/w8_artifact_reproducibility_guide.md`, mọi sai lệch `1` byte đều bị chặn fail-closed ở mức SHA-256.
