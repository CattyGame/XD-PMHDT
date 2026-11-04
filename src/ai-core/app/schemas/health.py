"""
HealthCheck, Readiness, and Platform Schemas for AURA AI Service (Module M4).
"""
from pydantic import BaseModel, Field


class HealthResponse(BaseModel):
    status: str = Field("ok", description="Trạng thái dịch vụ")
    version: str = Field("0.1.0", description="Phiên bản dịch vụ")
    service: str = Field("AURA.AiCore", description="Tên dịch vụ")
    device: str = Field("cpu", description="Thiết bị tính toán")
    model_loaded: bool = Field(
        False,
        description="Tình trạng nạp trọng số mô hình học sâu (false với Frangi không có trọng số huấn luyện)",
    )
    uptime_seconds: float = Field(0.0, description="Thời gian hoạt động tính theo giây")


class ReadyResponse(BaseModel):
    status: str = Field("ready", description="Trạng thái sẵn sàng xử lý")
    service: str = Field("AURA.AiCore", description="Tên dịch vụ")
    version: str = Field("0.2.0", description="Phiên bản dịch vụ")
    api_version: str = Field("1.3.0", description="Phiên bản AI API contract")
    engine: str = Field("frangi", description="Engine phân vùng đang kích hoạt")
    model_version: str = Field("frangi-v0.2", description="Phiên bản thuật toán/mô hình")
    config_version: str = Field("s3-frangi-1.0.0", description="Phiên bản cấu hình")
    threshold_version: str = Field("frangi-threshold-0.02-v1", description="Phiên bản ngưỡng")
    jwt_verification_ready: bool = Field(True, description="Đã nạp khóa công khai xác thực Service JWT")
    timestamp: str = Field(..., description="Thời điểm phản hồi UTC (ISO 8601 kết thúc bằng Z)")


class PingResponse(BaseModel):
    service: str = Field("AURA.AiCore", description="Tên dịch vụ")
    message: str = Field("AI Core template is running", description="Thông điệp kiểm tra kết nối")
    timestamp: str = Field(..., description="Thời điểm phản hồi UTC (ISO 8601)")
