"""
Analysis request and response schemas for AURA AI Service (Module M4).
Conforms strictly to OpenAPI 3.0 contract and task requirements for SCRUM-61 & SCRUM-62.
"""
from typing import Optional, List, Dict, Any
from datetime import datetime, timezone
from pydantic import BaseModel, Field

from .common import EyeSide, AnalysisMode, RiskLevel, AnalysisStatus


class AnalysisRequest(BaseModel):
    request_id: str = Field(..., description="Mã định danh duy nhất của yêu cầu")
    patient_id: Optional[str] = Field(None, description="Mã định danh bệnh nhân (tùy chọn)")
    eye_side: EyeSide = Field(EyeSide.UNKNOWN, description="Mắt trái / mắt phải / không xác định")
    mode: AnalysisMode = Field(AnalysisMode.RETINA_VESSELS, description="Chế độ phân tích")
    modality: str = Field("FUNDUS", description="Loại ảnh y tế: FUNDUS hoặc OCT")
    image_base64: str = Field(..., description="Chuỗi ảnh Base64")
    include_mask: bool = Field(True, description="Trả về ảnh mặt nạ nhị phân")
    include_overlay: bool = Field(True, description="Trả về ảnh phủ viền mạch")


class ImageInfo(BaseModel):
    width: int
    height: int
    channels: int = 3
    eye_side: EyeSide = EyeSide.UNKNOWN
    modality: str = "FUNDUS"


class VesselMetrics(BaseModel):
    vessel_density: float = Field(..., description="Mật độ diện tích mạch máu")
    tortuosity_index: float = Field(..., description="Chỉ số xoắn mạch máu")
    av_ratio: float = Field(..., description="Tỷ lệ đường kính động mạch / tĩnh mạch")
    fractal_dimension: float = Field(..., description="Số chiều Fractal phân nhánh")
    branching_points: int = Field(..., description="Số điểm phân nhánh mạch máu")


class RiskAssessment(BaseModel):
    risk_score: float = Field(..., description="Điểm rủi ro tổng hợp chuẩn hóa (0.0 -> 1.0)")
    risk_level: RiskLevel = Field(..., description="Mức độ rủi ro: LOW, MODERATE, HIGH")
    confidence_score: float = Field(..., description="Độ tin cậy của thuật toán (0.0 -> 1.0)")
    indicators: List[str] = Field(..., description="Danh sách các nhận định dựa trên chỉ số hình thái học")
    disclaimer: str = Field(
        "Kết quả ước lượng dựa trên phân tích hình thái học võng mạc (Heuristic). "
        "Mang tính chất tham khảo kỹ thuật, không thay thế chẩn đoán y khoa chính thức từ bác sĩ chuyên khoa.",
        description="Tuyên bố miễn trừ trách nhiệm y khoa bắt buộc"
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
    is_mock: bool = Field(True, description="Cờ xác nhận kết quả là Mock Engine tuần 1")
    model_version: str = Field("mock-v0.1", description="Phiên bản mô hình suy luận")
    threshold_version: str = Field("v0.1", description="Phiên bản ngưỡng đánh giá rủi ro")
    config_version: str = Field("v0.1", description="Phiên bản cấu hình thuật toán")
    limitations: str = Field(
        "Kết quả mô phỏng (Mock Engine) phục vụ tích hợp giao diện M5 và Backend M2.",
        description="Giới hạn kỹ thuật của phiên bản hiện tại"
    )
    processing_time_ms: float
    image_info: ImageInfo
    metrics: VesselMetrics
    risk_assessment: RiskAssessment
    segmentation: Optional[SegmentationResult] = None


class ErrorResponse(BaseModel):
    error_code: str = Field(..., description="Mã lỗi máy đọc được")
    message: str = Field(..., description="Thông điệp mô tả lỗi chi tiết")
    details: Optional[Dict[str, Any]] = Field(None, description="Chi tiết lỗi bổ sung (nếu có)")
    timestamp: str = Field(..., description="Thời điểm xảy ra lỗi theo chuẩn ISO 8601 UTC")
