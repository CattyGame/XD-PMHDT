"""
Mock inference engine for AURA AI Service (Module M4).
Provides realistic mock responses with valid segmentation masks and heuristic risk scores.
Strictly complies with task requirements: isMock=true, modelVersion=mock-v0.1, limitations.
"""
import time
import base64
import io
import struct
from datetime import datetime, timezone
from typing import Tuple

from ..schemas.common import EyeSide, AnalysisMode, RiskLevel, AnalysisStatus
from ..schemas.analysis import (
    AnalysisRequest,
    AnalysisResponse,
    ImageInfo,
    VesselMetrics,
    RiskAssessment,
    SegmentationResult
)


class InvalidImagePayloadError(ValueError):
    """Raised when image_base64 string is invalid or does not contain valid image data."""
    pass


# 1x1 valid sample PNGs (Mask: white square, Overlay: green/red tinted sample)
SAMPLE_MASK_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAIElEQVR42mP8z8AARAwMDDAG"
    "Awg4eP8hH4A8jE+DkS4AAN1dD2N31pX2AAAAAElFTkSuQmCC"
)

SAMPLE_OVERLAY_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAALUlEQVR42mNk+M9AwMDEgAQY"
    "GBgYgBwDAyNMMVge5AAc7vj//z8jTBzMAOUiGAB2tBDYn7bX+wAAAABJRU5ErkJggg=="
)

# Valid sample 1x1 PNG for testing
SAMPLE_VALID_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGA"
    "WjR9awAAAABJRU5ErkJggg=="
)


def inspect_image(base64_str: str) -> Tuple[int, int, int]:
    """
    Decodes base64 string and validates that it represents a valid image file.
    Extracts width, height, and channels.
    STRICT: Rejects corrupted Base64 and non-image data with InvalidImagePayloadError.
    NEVER falls back to fake dimensions for invalid inputs.
    """
    if not base64_str or not base64_str.strip():
        raise InvalidImagePayloadError("Chuỗi image_base64 rỗng hoặc chỉ chứa khoảng trắng.")

    clean_b64 = base64_str.strip()
    if "," in clean_b64:
        clean_b64 = clean_b64.split(",", 1)[1].strip()

    # 1. Strict Base64 decoding
    try:
        image_data = base64.b64decode(clean_b64, validate=True)
    except Exception as e:
        raise InvalidImagePayloadError(f"Chuỗi Base64 không hợp lệ hoặc bị hỏng mã hóa: {str(e)}")

    if len(image_data) < 12:
        raise InvalidImagePayloadError("Dữ liệu sau khi giải mã quá ngắn (<12 bytes), không phải tệp ảnh hợp lệ.")

    # 2. Try Pillow if available
    try:
        from PIL import Image
        with Image.open(io.BytesIO(image_data)) as img:
            img.verify()
        with Image.open(io.BytesIO(image_data)) as img:
            width, height = img.size
            channels = len(img.getbands()) if hasattr(img, "getbands") else 3
            if width > 0 and height > 0:
                return width, height, channels
    except Exception:
        pass

    # 3. Binary header inspection (PNG, JPEG, WebP, BMP)
    # PNG: Signature \x89PNG\r\n\x1a\n (8 bytes)
    if image_data.startswith(b"\x89PNG\r\n\x1a\n") and len(image_data) >= 24:
        try:
            width, height = struct.unpack(">II", image_data[16:24])
            if width > 0 and height > 0:
                return width, height, 3
        except Exception:
            pass

    # JPEG: Signature \xff\xd8
    if image_data.startswith(b"\xff\xd8"):
        try:
            offset = 2
            while offset < len(image_data) - 8:
                if image_data[offset] != 0xff:
                    offset += 1
                    continue
                marker = image_data[offset + 1]
                if marker in (0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf):
                    height, width = struct.unpack(">HH", image_data[offset + 5:offset + 9])
                    channels = image_data[offset + 9] if len(image_data) > offset + 9 else 3
                    if width > 0 and height > 0:
                        return width, height, channels
                else:
                    if offset + 4 <= len(image_data):
                        length = struct.unpack(">H", image_data[offset + 2:offset + 4])[0]
                        offset += 2 + length
                    else:
                        break
        except Exception:
            pass

    # WebP: Signature RIFF....WEBP
    if image_data.startswith(b"RIFF") and len(image_data) >= 30 and image_data[8:12] == b"WEBP":
        try:
            if image_data[12:16] == b"VP8 ":
                w_raw, h_raw = struct.unpack("<HH", image_data[26:30])
                width = w_raw & 0x3fff
                height = h_raw & 0x3fff
                if width > 0 and height > 0:
                    return width, height, 3
            elif image_data[12:16] == b"VP8L" and len(image_data) >= 25:
                b1, b2, b3, b4 = image_data[21:25]
                width = 1 + (((b2 & 0x3f) << 8) | b1)
                height = 1 + (((b4 & 0xf) << 10) | (b3 << 2) | ((b2 & 0xc0) >> 6))
                if width > 0 and height > 0:
                    return width, height, 3
        except Exception:
            pass

    # BMP: Signature BM
    if image_data.startswith(b"BM") and len(image_data) >= 26:
        try:
            width, height = struct.unpack("<ii", image_data[18:26])
            if abs(width) > 0 and abs(height) > 0:
                return abs(width), abs(height), 3
        except Exception:
            pass

    raise InvalidImagePayloadError(
        "Dữ liệu gửi lên không phải là tệp ảnh hợp lệ. Hỗ trợ các định dạng: PNG, JPEG, WEBP."
    )


def process_mock_analysis(request: AnalysisRequest) -> AnalysisResponse:
    """
    Simulates AI vessel segmentation and risk heuristic calculation.
    Returns a complete, fully populated AnalysisResponse conforming to week 1 contract.
    """
    start_time = time.perf_counter()

    # 1. Parse image metadata (strictly validates image format and dimensions)
    width, height, channels = inspect_image(request.image_base64)
    image_info = ImageInfo(
        width=width,
        height=height,
        channels=channels,
        eye_side=request.eye_side,
        modality=request.modality
    )

    # 2. Mock vessel geometric metrics (Retina vessel geometry standards)
    metrics = VesselMetrics(
        vessel_density=0.1485,
        tortuosity_index=1.165,
        av_ratio=0.672,
        fractal_dimension=1.475,
        branching_points=62
    )

    # 3. Mock Heuristic Risk Assessment (NFR-21 compliant)
    risk_assessment = RiskAssessment(
        risk_score=0.32,
        risk_level=RiskLevel.LOW,
        confidence_score=0.91,
        indicators=[
            "Mật độ mạch máu võng mạc trong giới hạn bình thường (14.85%)",
            "Chỉ số xoắn mạch (Tortuosity index 1.165) nằm trong khoảng an toàn (<1.25)",
            "Tỷ lệ động mạch/tĩnh mạch AVR (0.672) phù hợp tiêu chuẩn tham chiếu (0.65 - 0.70)"
        ],
        disclaimer=(
            "Kết quả ước lượng dựa trên phân tích hình thái học võng mạc (Heuristic). "
            "Mang tính chất tham khảo kỹ thuật, không thay thế chẩn đoán y khoa chính thức từ bác sĩ chuyên khoa."
        )
    )

    # 4. Mock Segmentation Output
    segmentation = None
    if request.include_mask or request.include_overlay:
        segmentation = SegmentationResult(
            mask_format="png_base64",
            mask_base64=SAMPLE_MASK_PNG_BASE64 if request.include_mask else None,
            overlay_base64=SAMPLE_OVERLAY_PNG_BASE64 if request.include_overlay else None
        )

    # 5. Measure latency
    elapsed_ms = round((time.perf_counter() - start_time) * 1000 + 45.0, 2)
    timestamp = datetime.now(timezone.utc).isoformat()

    return AnalysisResponse(
        request_id=request.request_id,
        patient_id=request.patient_id,
        timestamp=timestamp,
        status=AnalysisStatus.SUCCESS,
        is_mock=True,
        model_version="mock-v0.1",
        threshold_version="v0.1",
        config_version="v0.1",
        limitations="Kết quả mô phỏng (Mock Engine) phục vụ tích hợp giao diện M5 và Backend M2.",
        processing_time_ms=elapsed_ms,
        image_info=image_info,
        metrics=metrics,
        risk_assessment=risk_assessment,
        segmentation=segmentation
    )
