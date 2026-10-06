"""
AURA AI Core Service (Module M4).
Integrates platform endpoints (health, ping) and AI analysis endpoints.
"""
from datetime import datetime, timezone
import time
from typing import Optional

from fastapi import FastAPI, HTTPException, Request, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.exceptions import RequestValidationError
from pydantic import BaseModel

from .schemas.analysis import AnalysisRequest, AnalysisResponse
from .services.mock_service import process_mock_analysis

APP_START_TIME = time.time()
SERVICE_NAME = "AURA.AiCore"
VERSION = "0.1.0"

app = FastAPI(
    title="AURA AI Core",
    description="Microservice phân tích mạch máu võng mạc và đánh giá rủi ro tim mạch (Module M4).",
    version=VERSION,
)

# Enable CORS for Frontend (M5) and Gateway (M1)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


class HealthResponse(BaseModel):
    status: str
    service: str
    model_loaded: bool


class PingResponse(BaseModel):
    service: str
    message: str
    timestamp: datetime


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    """Handles 422 JSON validation errors with standard ErrorResponse schema."""
    error_payload = {
        "error_code": "SCHEMA_VALIDATION_ERROR",
        "message": "Dữ liệu gửi lên không đúng định dạng hoặc thiếu trường bắt buộc.",
        "details": {"errors": exc.errors()},
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }
    return JSONResponse(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, content=error_payload)


@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    """Handles standard HTTP errors."""
    error_payload = {
        "error_code": f"HTTP_{exc.status_code}",
        "message": exc.detail if isinstance(exc.detail, str) else "Lỗi xử lý yêu cầu.",
        "details": exc.detail if isinstance(exc.detail, dict) else None,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }
    return JSONResponse(status_code=exc.status_code, content=error_payload)


@app.get("/", tags=["Info"])
def root():
    """Root info endpoint."""
    return {
        "service": SERVICE_NAME,
        "version": VERSION,
        "status": "running",
        "health": "/health",
        "ping": "/api/v1/platform/ping",
        "analyze": "/api/v1/analyze",
    }


@app.get("/health", response_model=HealthResponse, tags=["Platform"])
def health():
    return HealthResponse(
        status="ok",
        service=SERVICE_NAME,
        model_loaded=False,
    )


@app.get("/api/v1/platform/ping", response_model=PingResponse, tags=["Platform"])
def ping():
    return PingResponse(
        service=SERVICE_NAME,
        message="AI Core template is running",
        timestamp=datetime.now(timezone.utc),
    )


@app.post("/api/v1/analyze", response_model=AnalysisResponse, tags=["Analysis"])
def analyze_retina(request: AnalysisRequest):
    """
    Endpoint phân tích ảnh võng mạc (Mock Engine cho tuần 1 - SCRUM-62).
    Nhận chuỗi ảnh Base64 và thông tin yêu cầu, trả về kết quả phân vùng và chỉ số hình học.
    """
    try:
        # Check blank image
        if not request.image_base64 or len(request.image_base64.strip()) == 0:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="Chuỗi image_base64 không được để trống.",
            )

        # OCT modality check (Task requirement: OCT returns 422 unsupported_modality)
        if request.modality and request.modality.upper() == "OCT":
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
                detail="unsupported_modality: Định dạng ảnh OCT chưa được hỗ trợ trong phiên bản hiện tại (chỉ hỗ trợ Fundus).",
            )

        result = process_mock_analysis(request)
        return result
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"Lỗi nội bộ khi xử lý mô hình AI: {str(e)}",
        )
