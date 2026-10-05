"""
Analysis request and response schemas for AURA AI Service (Module M4).
Conforms strictly to OpenAPI 3.0 contract in `contracts/ai_service_openapi.yaml`.
"""
from typing import Optional, List, Dict, Any
from datetime import datetime, timezone
from .common import EyeSide, AnalysisMode, RiskLevel, AnalysisStatus

try:
    from pydantic import BaseModel, Field
except ImportError:
    class BaseModel:
        def __init__(self, **kwargs):
            for k, v in kwargs.items():
                setattr(self, k, v)
        def model_dump(self):
            return self.__dict__
        def dict(self):
            return self.__dict__
    def Field(*args, **kwargs):
        return kwargs.get("default", None)


class AnalysisRequest(BaseModel):
    request_id: str
    patient_id: Optional[str] = None
    eye_side: EyeSide = EyeSide.UNKNOWN
    mode: AnalysisMode = AnalysisMode.RETINA_VESSELS
    image_base64: str
    include_mask: bool = True
    include_overlay: bool = True


class ImageInfo(BaseModel):
    width: int
    height: int
    channels: int = 3
    eye_side: EyeSide = EyeSide.UNKNOWN


class VesselMetrics(BaseModel):
    vessel_density: float
    tortuosity_index: float
    av_ratio: float
    fractal_dimension: float
    branching_points: int


class RiskAssessment(BaseModel):
    risk_score: float
    risk_level: RiskLevel
    confidence_score: float
    indicators: List[str]
    disclaimer: str = (
        "Kết quả mang tính chất tham khảo kỹ thuật và hỗ trợ nghiên cứu, "
        "không thay thế chẩn đoán y khoa chính thức từ bác sĩ chuyên khoa."
    )


class SegmentationResult(BaseModel):
    mask_format: str = "png_base64"
    mask_base64: Optional[str] = None
    overlay_base64: Optional[str] = None


class AnalysisResponse(BaseModel):
    request_id: str
    patient_id: Optional[str] = None
    timestamp: str
    status: AnalysisStatus = AnalysisStatus.SUCCESS
    processing_time_ms: float
    image_info: ImageInfo
    metrics: VesselMetrics
    risk_assessment: RiskAssessment
    segmentation: Optional[SegmentationResult] = None


class ErrorResponse(BaseModel):
    error_code: str
    message: str
    details: Optional[Dict[str, Any]] = None
    timestamp: str
