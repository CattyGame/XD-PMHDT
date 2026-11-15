"""
Analysis request and response schemas for AURA AI Service (Module M4).
Conforms strictly to OpenAPI 1.2.0 contract and JSON schemas for Sprint 3
(SCRUM-329, SCRUM-330, SCRUM-331) while preserving explicit mock test support.
"""
from typing import Any, Dict, List, Optional, Literal
from pydantic import BaseModel, ConfigDict, Field, model_validator

from .common import AnalysisMode, AnalysisStatus, EyeSide, RiskLevel


class AnalysisRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    request_id: str = Field(..., description="Mã định danh duy nhất của yêu cầu (UUID analysisId)")
    patient_id: Optional[str] = Field(
        None,
        description="Mã định danh bệnh nhân (bắt buộc null trên đường truyền Worker -> AI theo contract 1.2.0)",
    )
    eye_side: EyeSide = Field(EyeSide.UNKNOWN, description="Mắt trái / mắt phải / không xác định")
    mode: AnalysisMode = Field(AnalysisMode.RETINA_VESSELS, description="Chế độ phân tích")
    modality: str = Field("FUNDUS", description="Loại ảnh y tế: FUNDUS (OCT bị từ chối HTTP 422)")
    image_base64: str = Field(..., description="Chuỗi ảnh Base64 trần (PNG/JPEG)")
    include_mask: bool = Field(True, description="Trả về ảnh mặt nạ nhị phân")
    include_overlay: bool = Field(True, description="Trả về ảnh phủ viền mạch")


class ImageInfo(BaseModel):
    model_config = ConfigDict(extra="forbid")

    width: int = Field(..., ge=1)
    height: int = Field(..., ge=1)
    channels: int = Field(3)
    eye_side: EyeSide = EyeSide.UNKNOWN
    modality: str = "FUNDUS"


class VesselMetrics(BaseModel):
    model_config = ConfigDict(extra="forbid")

    vessel_density: float = Field(
        ...,
        ge=0.0,
        le=1.0,
        description="Tỷ lệ pixel mạch máu foreground trên tổng diện tích ảnh gốc",
    )
    tortuosity_index: Optional[float] = Field(
        None,
        description="Chỉ số xoắn mạch máu (null với Frangi baseline v0.2)",
    )
    av_ratio: Optional[float] = Field(
        None,
        description="Tỷ lệ đường kính động mạch / tĩnh mạch (null với Frangi baseline v0.2)",
    )
    fractal_dimension: Optional[float] = Field(
        None,
        description="Số chiều Fractal phân nhánh (null với Frangi baseline v0.2)",
    )
    branching_points: Optional[int] = Field(
        None,
        ge=0,
        description="Số điểm phân nhánh mạch máu (null với Frangi baseline v0.2)",
    )


class RiskAssessment(BaseModel):
    model_config = ConfigDict(extra="forbid")

    risk_score: float = Field(..., ge=0.0, le=1.0, description="Điểm rủi ro chuẩn hóa (0.0 -> 1.0)")
    risk_level: RiskLevel = Field(..., description="Mức độ rủi ro: LOW, MODERATE, HIGH")
    confidence_score: float = Field(..., ge=0.0, le=1.0, description="Độ tin cậy (0.0 -> 1.0)")
    indicators: List[str] = Field(..., description="Danh sách các nhận định")
    disclaimer: str = Field(
        "Kết quả ước lượng dựa trên phân tích hình thái học võng mạc (Heuristic). "
        "Mang tính chất tham khảo kỹ thuật, không thay thế chẩn đoán y khoa chính thức từ bác sĩ chuyên khoa.",
        min_length=1,
        description="Tuyên bố miễn trừ trách nhiệm y khoa bắt buộc",
    )


class SegmentationResult(BaseModel):
    model_config = ConfigDict(extra="forbid")

    mask_format: str = "png_base64"
    mask_base64: Optional[str] = None
    overlay_base64: Optional[str] = None
    av_overlay_base64: Optional[str] = Field(None, exclude_if=lambda value: value is None)
    gradcam_base64: Optional[str] = Field(None, exclude_if=lambda value: value is None)


class AvrResearchResult(BaseModel):
    model_config = ConfigDict(extra="forbid")
    measurement_available: bool
    avr: Optional[float] = Field(None, gt=0, allow_inf_nan=False)
    crae_px: Optional[float] = Field(None, gt=0, allow_inf_nan=False)
    crve_px: Optional[float] = Field(None, gt=0, allow_inf_nan=False)
    unavailable_reason: Optional[str] = None
    model_version: str = Field(min_length=1)
    model_sha256: str = Field(pattern=r"^[a-f0-9]{64}$")

    @model_validator(mode="after")
    def validate_measurement(self):
        values = (self.avr, self.crae_px, self.crve_px)
        if self.measurement_available:
            if any(value is None for value in values) or self.unavailable_reason is not None:
                raise ValueError("Available AVR requires finite measurements and no unavailable reason.")
            if abs(self.avr - self.crae_px / self.crve_px) > 1e-6:
                raise ValueError("AVR must equal CRAE / CRVE.")
        elif any(value is not None for value in values) or not self.unavailable_reason:
            raise ValueError("Unavailable AVR requires null measurements and a reason.")
        return self


class DrResearchResult(BaseModel):
    model_config = ConfigDict(extra="forbid")
    probability_dr: float = Field(ge=0, le=1, allow_inf_nan=False)
    model_version: str = Field(min_length=1)
    checkpoint_sha256: str = Field(pattern=r"^[a-f0-9]{64}$")
    gradcam_method: Literal["GRAD_CAM"]
    gradcam_geometry: Literal["IMAGENET_CENTER_CROP_224"]


class ResearchResults(BaseModel):
    model_config = ConfigDict(extra="forbid")
    avr: AvrResearchResult
    dr: DrResearchResult


class AnalysisWarning(BaseModel):
    model_config = ConfigDict(extra="forbid")

    code: str = Field(
        ...,
        min_length=1,
        description="Mã cảnh báo máy đọc được.",
    )
    message: str = Field(
        ...,
        min_length=1,
        description="Nội dung cảnh báo.",
    )


class AnalysisResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    request_id: str
    patient_id: Optional[str] = None
    timestamp: str

    status: AnalysisStatus = AnalysisStatus.WARNING

    warnings: List[AnalysisWarning] = Field(
        default_factory=list,
        description=(
            "Cảnh báo đi kèm kết quả phân tích. "
            "WARNING không mặc định đồng nghĩa ảnh kém chất lượng."
        ),
    )

    is_mock: bool = Field(
        False,
        description="True nếu tạo bởi Mock Engine; False khi chạy thuật toán Frangi thật.",
    )

    model_version: str = Field(
        "frangi-v0.2",
        min_length=1,
        description="Phiên bản thuật toán hoặc mô hình.",
    )

    config_version: str = Field(
        "s3-frangi-1.0.0",
        min_length=1,
        description="Phiên bản cấu hình xử lý.",
    )

    threshold_version: str = Field(
        "frangi-threshold-0.02-v1",
        min_length=1,
        description="Phiên bản cấu hình ngưỡng.",
    )

    limitations: str = Field(
        "Segmentation research baseline; no clinical risk assessment.",
        min_length=1,
        description="Giới hạn kỹ thuật của phiên bản hiện tại.",
    )

    processing_time_ms: float = Field(..., ge=0.0)
    image_info: ImageInfo
    metrics: VesselMetrics
    risk_available: Optional[bool] = Field(
        None,
        description="False khi thuật toán chỉ phân vùng mạch máu và không cung cấp đánh giá rủi ro.",
    )
    risk_assessment: Optional[RiskAssessment] = None
    segmentation: Optional[SegmentationResult] = None
    research: Optional[ResearchResults] = Field(None, exclude_if=lambda value: value is None)

    @model_validator(mode="after")
    def _validate_risk_invariants(self) -> "AnalysisResponse":
        if self.risk_available is None:
            self.risk_available = self.risk_assessment is not None
        if not self.risk_available and self.risk_assessment is not None:
            raise ValueError("risk_assessment must be null when risk_available is false.")
        if self.risk_available and self.risk_assessment is None:
            raise ValueError("risk_assessment is required when risk_available is true.")
        if self.research is not None and self.risk_available:
            raise ValueError("research results cannot provide clinical risk assessment.")
        if self.research is not None and self.metrics.av_ratio != self.research.avr.avr:
            raise ValueError("metrics.av_ratio must match the research AVR measurement.")
        return self


class ErrorResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    error_code: str = Field(..., min_length=1, description="Mã lỗi máy đọc được")
    message: str = Field(..., min_length=1, description="Thông điệp mô tả lỗi chi tiết")
    details: Optional[Dict[str, Any]] = Field(None, description="Chi tiết lỗi bổ sung (nếu có)")
    timestamp: str = Field(..., description="Thời điểm xảy ra lỗi theo chuẩn ISO 8601 UTC kết thúc bằng Z")
