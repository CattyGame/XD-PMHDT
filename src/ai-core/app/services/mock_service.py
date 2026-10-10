"""
AURA mock inference service.
Outputs are synthetic and must not be used as clinical results.
"""
import base64
import io
import os
import time
from datetime import datetime, timezone
from typing import Tuple

from PIL import Image, ImageDraw, UnidentifiedImageError

from ..schemas.common import RiskLevel, AnalysisStatus
from ..schemas.analysis import (
    AnalysisRequest,
    AnalysisResponse,
    AnalysisWarning,
    ImageInfo,
    VesselMetrics,
    RiskAssessment,
    SegmentationResult,
)

MAX_IMAGE_BYTES = 10 * 1024 * 1024
MAX_PIXELS = 16_000_000
SUPPORTED_FORMATS = ("PNG", "JPEG", "WEBP")


class InvalidImagePayloadError(ValueError):
    """Input does not contain a valid supported image."""


class MockLowQualityImageError(ValueError):
    """Controlled mock rejection; not a real image-quality assessment."""


def create_mock_mask_png(width: int = 512, height: int = 512) -> str:
    """Create a binary PNG mask matching the input dimensions."""
    mask = Image.new("L", (width, height), color=0)
    draw = ImageDraw.Draw(mask)
    line_width = max(1, width // 128)

    draw.line(
        [
            (width // 4, height // 4),
            (width // 2, height // 2),
            (3 * width // 4, 3 * height // 4),
        ],
        fill=255,
        width=line_width,
    )

    buffer = io.BytesIO()
    mask.save(buffer, format="PNG", optimize=True)
    return base64.b64encode(buffer.getvalue()).decode("ascii")


def create_mock_overlay_png(width: int = 512, height: int = 512) -> str:
    """Create a synthetic RGB PNG; this is not an overlay on the source image."""
    overlay = Image.new("RGB", (width, height), color=(10, 10, 10))
    draw = ImageDraw.Draw(overlay)
    line_width = max(1, width // 128)

    draw.line(
        [
            (width // 4, height // 4),
            (width // 2, height // 2),
            (3 * width // 4, 3 * height // 4),
        ],
        fill=(0, 255, 128),
        width=line_width,
    )

    buffer = io.BytesIO()
    overlay.save(buffer, format="PNG", optimize=True)
    return base64.b64encode(buffer.getvalue()).decode("ascii")


SAMPLE_VALID_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAIAAAAuKetIAAAAZUlEQVR4nO3PUQkA"
    "IBTAwBfCEMaxfxpD+HEIgwW4zdnr64YLGtCCBrSgAS1oQAsa0IIGtKABLWhAC"
    "xrQgga0oAEtaEALGtCCBrSgAS1oQAsa0IIGtKABLWhACxrQgga0oAEteOwCadm"
    "QWyWfhSMAAAAASUVORK5CYII="
)
SAMPLE_MASK_PNG_BASE64 = create_mock_mask_png(512, 512)
SAMPLE_OVERLAY_PNG_BASE64 = create_mock_overlay_png(512, 512)


def inspect_image(base64_str: str) -> Tuple[int, int, int]:
    """
    Check format and dimensions before fully decoding pixels.
    Verify integrity, then reopen and load the accepted image.
    """
    if not base64_str or not base64_str.strip():
        raise InvalidImagePayloadError(
            "Chuỗi image_base64 rỗng hoặc chỉ chứa khoảng trắng."
        )

    clean_b64 = base64_str.strip()
    if "," in clean_b64:
        clean_b64 = clean_b64.split(",", 1)[1].strip()

    try:
        image_data = base64.b64decode(clean_b64, validate=True)
    except Exception as error:
        raise InvalidImagePayloadError(
            f"Chuỗi Base64 không hợp lệ hoặc bị hỏng mã hóa: {error}"
        ) from error

    if len(image_data) > MAX_IMAGE_BYTES:
        raise InvalidImagePayloadError(
            f"Dung lượng ảnh ({len(image_data)} bytes) vượt quá "
            f"giới hạn ({MAX_IMAGE_BYTES} bytes)."
        )

    if len(image_data) < 12:
        raise InvalidImagePayloadError(
            "Dữ liệu giải mã quá ngắn (<12 bytes), không phải ảnh hợp lệ."
        )

    try:
        with Image.open(io.BytesIO(image_data)) as image:
            format_name = (image.format or "").upper()
            width, height = image.size

            if format_name not in SUPPORTED_FORMATS:
                raise InvalidImagePayloadError(
                    f"Định dạng '{format_name}' không được hỗ trợ. "
                    f"Chỉ chấp nhận: {', '.join(SUPPORTED_FORMATS)}."
                )

            if width <= 0 or height <= 0:
                raise InvalidImagePayloadError(
                    f"Kích thước ảnh ({width}x{height}) không hợp lệ."
                )

            if width * height > MAX_PIXELS:
                raise InvalidImagePayloadError(
                    f"Độ phân giải ảnh ({width}x{height} = "
                    f"{width * height} pixels) vượt quá "
                    f"giới hạn ({MAX_PIXELS} pixels)."
                )

            image.verify()

        with Image.open(io.BytesIO(image_data)) as image:
            image.load()
            channels = len(image.getbands())

    except InvalidImagePayloadError:
        raise
    except (
        UnidentifiedImageError,
        Image.DecompressionBombError,
        OSError,
        SyntaxError,
        ValueError,
    ) as error:
        raise InvalidImagePayloadError(
            "Dữ liệu không phải ảnh hợp lệ hoặc bị cắt cụt/lỗi CRC: "
            f"{error}"
        ) from error

    return width, height, channels


def process_mock_analysis(request: AnalysisRequest) -> AnalysisResponse:
    """Return synthetic results for controlled integration testing."""
    start_time = time.perf_counter()

    width, height, channels = inspect_image(request.image_base64)

    scenario = os.getenv(
        "AURA_MOCK_SCENARIO", "SUCCESS"
    ).strip().upper()

    if scenario not in {"SUCCESS", "WARNING", "LOW_QUALITY", "INFERENCE_ERROR"}:
        raise RuntimeError("Unsupported mock scenario configuration.")

    if scenario != "SUCCESS":
        enabled = os.getenv(
            "AURA_MOCK_SCENARIOS_ENABLED", "false"
        ).strip().lower()

        if enabled != "true":
            raise RuntimeError("Mock scenarios are not enabled.")

    if scenario == "LOW_QUALITY":
        raise MockLowQualityImageError(
            "[MOCK] Giả lập từ chối ảnh vì chất lượng không đủ. "
            "Chưa thực hiện đánh giá chất lượng ảnh thật."
        )

    if scenario == "INFERENCE_ERROR":
        raise RuntimeError("Controlled mock inference failure.")

    result_status = AnalysisStatus.SUCCESS
    result_warnings = []

    if scenario == "WARNING":
        result_status = AnalysisStatus.WARNING
        result_warnings = [
            AnalysisWarning(
                code="AI_WARNING",
                message=(
                    "[MOCK] Cảnh báo kỹ thuật phục vụ kiểm thử. "
                    "Kết quả giả lập vẫn được trả đầy đủ; "
                    "cảnh báo này không xác nhận ảnh kém chất lượng."
                ),
            )
        ]

    image_info = ImageInfo(
        width=width,
        height=height,
        channels=channels,
        eye_side=request.eye_side,
        modality=request.modality,
    )

    metrics = VesselMetrics(
        vessel_density=0.1485,
        tortuosity_index=1.165,
        av_ratio=0.672,
        fractal_dimension=1.475,
        branching_points=62,
    )

    risk_assessment = RiskAssessment(
        risk_score=0.32,
        risk_level=RiskLevel.LOW,
        confidence_score=0.91,
        indicators=[
            "[MOCK] Mật độ mạch giả lập: vessel_density=0.1485",
            "[MOCK] Chỉ số uốn lượn giả lập: tortuosity_index=1.165",
            "[MOCK] Tỷ lệ động/tĩnh mạch giả lập: av_ratio=0.672",
        ],
        disclaimer=(
            "Kết quả hoàn toàn là dữ liệu giả lập phục vụ kiểm thử tích hợp. "
            "Không phải kết quả chẩn đoán và không thay thế đánh giá của bác sĩ."
        ),
    )

    segmentation = None
    if request.include_mask or request.include_overlay:
        segmentation = SegmentationResult(
            mask_format="png_base64",
            mask_base64=(
                create_mock_mask_png(width, height)
                if request.include_mask else None
            ),
            overlay_base64=(
                create_mock_overlay_png(width, height)
                if request.include_overlay else None
            ),
        )

    elapsed_ms = round((time.perf_counter() - start_time) * 1000, 2)

    return AnalysisResponse(
        request_id=request.request_id,
        patient_id=request.patient_id,
        timestamp=datetime.now(timezone.utc).isoformat(),
        status=result_status,
        warnings=result_warnings,
        is_mock=True,
        model_version="mock-v0.1",
        threshold_version="v0.1",
        config_version="v0.1",
        limitations=(
            "Kết quả mô phỏng (Mock Engine) phục vụ kiểm thử tích hợp; "
            "chưa tích hợp thuật toán phân vùng thật. "
            "Mask và overlay là hình vẽ tổng hợp, không phải kết quả từ ảnh."
        ),
        processing_time_ms=elapsed_ms,
        image_info=image_info,
        metrics=metrics,
        risk_assessment=risk_assessment,
        segmentation=segmentation,
    )
