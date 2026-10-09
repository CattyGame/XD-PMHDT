"""
HealthCheck and Platform Schemas for AURA AI Service (Module M4).
"""
from pydantic import BaseModel, Field


class HealthResponse(BaseModel):
    status: str = Field("ok", description="Trạng thái dịch vụ")
    version: str = Field("0.1.0", description="Phiên bản dịch vụ")
    service: str = Field("AURA.AiCore", description="Tên dịch vụ")
    device: str = Field("cpu", description="Thiết bị tính toán")
    model_loaded: bool = Field(False, description="Tình trạng nạp mô hình (luôn false ở giai đoạn mock)")
    uptime_seconds: float = Field(0.0, description="Thời gian hoạt động tính theo giây")


class PingResponse(BaseModel):
    service: str = Field("AURA.AiCore", description="Tên dịch vụ")
    message: str = Field("AI Core template is running", description="Thông điệp kiểm tra kết nối")
    timestamp: str = Field(..., description="Thời điểm phản hồi UTC (ISO 8601)")
