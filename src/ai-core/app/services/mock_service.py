"""
Mock inference engine for AURA AI Service (Module M4).
Provides mock responses with valid segmentation masks and heuristic mock metrics for integration.
Strictly complies with task requirements: isMock=true, modelVersion=mock-v0.1, limitations.
"""
import time
import base64
import io
from datetime import datetime, timezone
from typing import Tuple

from PIL import Image, ImageDraw, UnidentifiedImageError

from ..schemas.common import EyeSide, AnalysisMode, RiskLevel, AnalysisStatus
from ..schemas.analysis import (
    AnalysisRequest,
    AnalysisResponse,
    ImageInfo,
    VesselMetrics,
    RiskAssessment,
    SegmentationResult
)

MAX_IMAGE_BYTES = 10 * 1024 * 1024  # 10 MB limit
MAX_PIXELS = 16_000_000  # 16 Megapixels limit
SUPPORTED_FORMATS = ("PNG", "JPEG", "WEBP")


class InvalidImagePayloadError(ValueError):
    """Raised when image_base64 string is invalid or does not contain valid image data."""
    pass


def create_mock_mask_png(width: int = 512, height: int = 512) -> str:
    """Generates a valid binary mask PNG (mode L) matching image dimensions."""
    w = width
    h = height
    mask = Image.new("L", (w, h), color=0)
    draw = ImageDraw.Draw(mask)
    line_w = max(1, w // 128)
    draw.line([(w // 4, h // 4), (w // 2, h // 2), (3 * w // 4, 3 * h // 4)], fill=255, width=line_w)
    buf = io.BytesIO()
    mask.save(buf, format="PNG", optimize=True)
    return base64.b64encode(buf.getvalue()).decode("utf-8")


def create_mock_overlay_png(width: int = 512, height: int = 512) -> str:
    """Generates a valid tinted overlay PNG (mode RGB) matching image dimensions."""
    w = width
    h = height
    overlay = Image.new("RGB", (w, h), color=(10, 10, 10))
    draw = ImageDraw.Draw(overlay)
    line_w = max(1, w // 128)
    draw.line([(w // 4, h // 4), (w // 2, h // 2), (3 * w // 4, 3 * h // 4)], fill=(0, 255, 128), width=line_w)
    buf = io.BytesIO()
    overlay.save(buf, format="PNG", optimize=True)
    return base64.b64encode(buf.getvalue()).decode("utf-8")


# Pre-generated valid sample PNGs for fast reference & testing
SAMPLE_VALID_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGA"
    "WjR9awAAAABJRU5ErkJggg=="
)
SAMPLE_MASK_PNG_BASE64 = create_mock_mask_png(512, 512)
SAMPLE_OVERLAY_PNG_BASE64 = create_mock_overlay_png(512, 512)


def inspect_image(base64_str: str) -> Tuple[int, int, int]:
    """
    Decodes base64 string and validates that it represents a valid image file.
    Uses Pillow verify() then load() to strictly ensure full pixel decoding.
    Rejects corrupted data, truncated images, header-only images, and unsupported formats.
    NEVER falls back to fake dimensions or raw header parsing.
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

    # 2. Check maximum raw payload size
    if len(image_data) > MAX_IMAGE_BYTES:
        raise InvalidImagePayloadError(
            f"Dung lượng ảnh ({len(image_data)} bytes) vượt quá giới hạn tối đa cho phép ({MAX_IMAGE_BYTES} bytes)."
        )

    if len(image_data) < 12:
        raise InvalidImagePayloadError("Dữ liệu sau khi giải mã quá ngắn (<12 bytes), không phải tệp ảnh hợp lệ.")

    # 3. Pillow validation: verify() followed by reopen and load()
    try:
        with Image.open(io.BytesIO(image_data)) as img:
            img.verify()
        with Image.open(io.BytesIO(image_data)) as img:
            img.load()  # Force complete decoding of all pixel data (detects truncated / bad CRC)
            format_name = (img.format or "").upper()
            width, height = img.size
            channels = len(img.getbands()) if hasattr(img, "getbands") else 3
    except (UnidentifiedImageError, OSError, SyntaxError, ValueError) as e:
        raise InvalidImagePayloadError(
            f"Dữ liệu gửi lên không phải là tệp ảnh hợp lệ hoặc bị cắt cụt/lỗi CRC: {str(e)}"
        )

    # 4. Check supported format (PNG, JPEG, WEBP)
    if format_name not in SUPPORTED_FORMATS:
        raise InvalidImagePayloadError(
            f"Định dạng ảnh '{format_name}' không được hỗ trợ. Chỉ chấp nhận các định dạng: PNG, JPEG, WEBP."
        )

    # 5. Check resolution / pixel count limits
    if width <= 0 or height <= 0:
        raise InvalidImagePayloadError(f"Kích thước ảnh ({width}x{height}) không hợp lệ.")
    if width * height > MAX_PIXELS:
        raise InvalidImagePayloadError(
            f"Độ phân giải ảnh ({width}x{height} = {width * height} pixels) vượt quá giới hạn ({MAX_PIXELS} pixels)."
        )

    return width, height, channels


def process_mock_analysis(request: AnalysisRequest) -> AnalysisResponse:
    """
    Simulates AI vessel segmentation and risk heuristic calculation for pipeline integration.
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

    # 2. Mock vessel geometric metrics (Simulated values for integration testing)
    metrics = VesselMetrics(
        vessel_density=0.1485,
        tortuosity_index=1.165,
        av_ratio=0.672,
        fractal_dimension=1.475,
        branching_points=62
    )

    # 3. Mock Heuristic Risk Assessment (NFR-21 compliant, explicitly marked as mock)
    risk_assessment = RiskAssessment(
        risk_score=0.32,
        risk_level=RiskLevel.LOW,
        confidence_score=0.91,
        indicators=[
            "[MOCK] Giá trị giả lập mật độ mạch (vessel_density=0.1485) phục vụ kiểm thử tích hợp",
            "[MOCK] Giá trị giả lập chỉ số uốn lượn (tortuosity_index=1.165) phục vụ kiểm thử tích hợp",
            "[MOCK] Giá trị giả lập tỷ lệ động/tĩnh mạch (av_ratio=0.672) phục vụ kiểm thử tích hợp"
        ],
        disclaimer=(
            "Kết quả hoàn toàn là dữ liệu giả lập (Mock Engine v0.1) phục vụ tích hợp giao diện M5 và Worker M2. "
            "Không phải kết quả chẩn đoán y tế thực tế và không thay thế kết luận của bác sĩ chuyên khoa."
        )
    )

    # 4. Mock Segmentation Output (valid PNGs matching input dimensions)
    segmentation = None
    if request.include_mask or request.include_overlay:
        segmentation = SegmentationResult(
            mask_format="png_base64",
            mask_base64=create_mock_mask_png(width, height) if request.include_mask else None,
            overlay_base64=create_mock_overlay_png(width, height) if request.include_overlay else None
        )

    # 5. Measure latency (pure actual measured execution time, no artificial offset)
    elapsed_ms = round((time.perf_counter() - start_time) * 1000, 2)
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
        limitations="Kết quả mô phỏng (Mock Engine v0.1) phục vụ tích hợp giao diện M5 và Worker M2; chưa tích hợp mô hình phân vùng thực tế.",
        processing_time_ms=elapsed_ms,
        image_info=image_info,
        metrics=metrics,
        risk_assessment=risk_assessment,
        segmentation=segmentation
    )
