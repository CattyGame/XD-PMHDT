"""
Mock inference engine for AURA AI Service (Module M4).
Provides realistic mock responses with valid segmentation masks and heuristic risk scores.
Used for contract verification, Frontend M5 and Gateway M1 integration testing.
"""
import time
import base64
import io
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

# 1x1 valid sample PNGs (Mask: white square, Overlay: green/red tinted sample)
SAMPLE_MASK_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAIElEQVR42mP8z8AARAwMDDAG"
    "Awg4eP8hH4A8jE+DkS4AAN1dD2N31pX2AAAAAElFTkSuQmCC"
)

SAMPLE_OVERLAY_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAALUlEQVR42mNk+M/AwMDEgAQY"
    "GBgYgBwDAyNMMVge5AAc7vj//z8jTBzMAOUiGAB2tBDYn7bX+wAAAABJRU5ErkJggg=="
)

def inspect_image(base64_str: str) -> Tuple[int, int, int]:
    """
    Attempts to read image dimensions from base64 string.
    Falls back to standard 512x512x3 if Pillow is absent or string is a stub.
    """
    try:
        from PIL import Image
        image_data = base64.b64decode(base64_str)
        with Image.open(io.BytesIO(image_data)) as img:
            width, height = img.size
            channels = len(img.getbands())
            return width, height, channels
    except Exception:
        return 512, 512, 3

def process_mock_analysis(request: AnalysisRequest) -> AnalysisResponse:
    """
    Simulates AI vessel segmentation and risk heuristic calculation.
    Returns a complete, fully populated AnalysisResponse.
    """
    start_time = time.perf_counter()

    # 1. Parse image metadata
    width, height, channels = inspect_image(request.image_base64)
    image_info = ImageInfo(
        width=width,
        height=height,
        channels=channels,
        eye_side=request.eye_side
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
            "Không thay thế chẩn đoán y khoa chính thức từ bác sĩ chuyên khoa."
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
    elapsed_ms = round((time.perf_counter() - start_time) * 1000 + 45.0, 2)  # Simulate ~45ms inference time
    timestamp = datetime.now(timezone.utc).isoformat()

    return AnalysisResponse(
        request_id=request.request_id,
        patient_id=request.patient_id,
        timestamp=timestamp,
        status=AnalysisStatus.SUCCESS,
        processing_time_ms=elapsed_ms,
        image_info=image_info,
        metrics=metrics,
        risk_assessment=risk_assessment,
        segmentation=segmentation
    )
