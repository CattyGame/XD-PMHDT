"""
Main FastAPI Application for AURA AI Service (Module M4).
"""
import time
from datetime import datetime, timezone
from typing import Dict, Any

from fastapi import FastAPI, HTTPException, Request, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.exceptions import RequestValidationError

from .schemas.common import AnalysisStatus
from .schemas.health import HealthResponse
from .schemas.analysis import AnalysisRequest, AnalysisResponse, ErrorResponse
from .services.mock_service import process_mock_analysis

# Application metadata
APP_START_TIME = time.time()
SERVICE_NAME = "aura-ai-service"
VERSION = "1.0.0"

app = FastAPI(
    title="AURA AI Analysis Service",
    description=(
        "Microservice phân tích mạch máu võng mạc và đánh giá rủi ro tim mạch (Module M4). "
        "Tuân thủ hợp đồng giao tiếp OpenAPI 3.0 với Gateway (M1) và Backend (M2)."
    ),
    version=VERSION,
    docs_url="/docs",
    redoc_url="/redoc"
)

# Enable CORS for Frontend (M5) and Gateway (M1)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    """Handles 422 JSON validation errors with standard ErrorResponse schema."""
    error_payload = {
        "error_code": "SCHEMA_VALIDATION_ERROR",
        "message": "Dữ liệu gửi lên không đúng định dạng hoặc thiếu trường bắt buộc.",
        "details": {"errors": exc.errors()},
        "timestamp": datetime.now(timezone.utc).isoformat()
    }
    return JSONResponse(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, content=error_payload)

@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    """Handles standard HTTP errors."""
    error_payload = {
        "error_code": f"HTTP_{exc.status_code}",
        "message": exc.detail if isinstance(exc.detail, str) else "Lỗi xử lý yêu cầu.",
        "details": exc.detail if isinstance(exc.detail, dict) else None,
        "timestamp": datetime.now(timezone.utc).isoformat()
    }
    return JSONResponse(status_code=exc.status_code, content=error_payload)

@app.get("/", tags=["Info"])
def root():
    """Root info endpoint."""
    return {
        "service": SERVICE_NAME,
        "version": VERSION,
        "status": "running",
        "documentation": "/docs",
        "health": "/health",
        "analyze_endpoint": "/api/v1/analyze"
    }

@app.get("/health", response_model=HealthResponse, tags=["Health"])
def health_check():
    """
    HealthCheck endpoint for Docker Compose, Kubernetes and Gateway (M1).
    """
    uptime = round(time.time() - APP_START_TIME, 2)
    return HealthResponse(
        status="healthy",
        version=VERSION,
        service=SERVICE_NAME,
        device="cpu",
        model_loaded=True,
        uptime_seconds=uptime
    )

@app.post("/api/v1/analyze", response_model=AnalysisResponse, tags=["Analysis"])
def analyze_retina(request: AnalysisRequest):
    """
    Endpoint phân tích ảnh võng mạc (Mock Engine cho tuần 1).
    Nhận chuỗi ảnh Base64 và thông tin yêu cầu, trả về kết quả phân vùng và chỉ số hình học.
    """
    try:
        # Validate that image_base64 is present and not blank
        if not request.image_base64 or len(request.image_base64.strip()) == 0:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail="Chuỗi image_base64 không được để trống."
            )

        # Execute mock inference
        result = process_mock_analysis(request)
        return result
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail=f"Lỗi nội bộ khi xử lý mô hình AI: {str(e)}"
        )
