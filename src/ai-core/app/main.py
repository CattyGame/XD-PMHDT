"""
AURA AI Core Service (Module M4).
Integrates platform endpoints (health, ping) and AI analysis endpoints.
"""
from datetime import datetime, timezone
import time
from typing import Optional, Dict, Any

from fastapi import FastAPI, HTTPException, Request, status
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.exceptions import RequestValidationError
from fastapi.encoders import jsonable_encoder

from .schemas.common import AnalysisMode
from .schemas.health import HealthResponse, PingResponse
from .schemas.analysis import AnalysisRequest, AnalysisResponse, ErrorResponse
from .services.mock_service import process_mock_analysis, InvalidImagePayloadError

APP_START_TIME = time.time()
SERVICE_NAME = "AURA.AiCore"
VERSION = "0.1.0"
API_VERSION = "1.0.0"

app = FastAPI(
    title="AURA AI Analysis Service API",
    description=(
        "Hợp đồng giao tiếp API (Module M4 - Computer Vision & AI Service) cho hệ thống AURA. "
        "Cung cấp các endpoint phục vụ suy luận phân vùng mạch máu võng mạc, trích xuất đặc trưng hình học "
        "và tính toán chỉ số nguy cơ tim mạch/đột quỵ theo thuật toán Heuristic."
    ),
    version=API_VERSION,
    contact={
        "name": "Dao Duy Quan (M4 Lead)",
        "email": "quandd2937@ut.edu.vn",
    },
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
    errors = exc.errors()
    # Check if the error is specifically for unsupported mode
    for err in errors:
        loc = err.get("loc", ())
        if "mode" in loc:
            return JSONResponse(
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
                content={
                    "error_code": "UNSUPPORTED_MODE",
                    "message": "Chế độ phân tích nằm ngoài phạm vi hỗ trợ của phiên bản v0.1 (chỉ hỗ trợ retina_vessels).",
                    "details": {
                        "supported_modes": ["retina_vessels"],
                        "received_mode": str(err.get("input", "")),
                        "errors": jsonable_encoder(errors),
                    },
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                },
            )

    error_payload = {
        "error_code": "SCHEMA_VALIDATION_ERROR",
        "message": "Dữ liệu gửi lên không đúng định dạng hoặc thiếu trường bắt buộc.",
        "details": {"errors": jsonable_encoder(errors)},
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }
    return JSONResponse(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, content=error_payload)


@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    """Handles standard HTTP errors with machine-readable error codes."""
    error_code = f"HTTP_{exc.status_code}"
    message = "Lỗi xử lý yêu cầu."
    details = None
    if isinstance(exc.detail, dict):
        error_code = exc.detail.get("error_code", error_code)
        message = exc.detail.get("message", message)
        details = exc.detail.get("details", None)
    elif isinstance(exc.detail, str):
        message = exc.detail
        if "unsupported_modality" in message.lower():
            error_code = "UNSUPPORTED_MODALITY"
        elif "invalid_image_payload" in message.lower():
            error_code = "INVALID_IMAGE_PAYLOAD"

    error_payload = {
        "error_code": error_code,
        "message": message,
        "details": details,
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }
    return JSONResponse(status_code=exc.status_code, content=error_payload)


@app.get("/", include_in_schema=False)
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


@app.get("/health", response_model=HealthResponse, tags=["Health"])
def health():
    uptime = round(time.time() - APP_START_TIME, 2)
    return HealthResponse(
        status="ok",
        version=VERSION,
        service=SERVICE_NAME,
        device="cpu",
        model_loaded=False,
        uptime_seconds=uptime,
    )


@app.get("/api/v1/platform/ping", response_model=PingResponse, tags=["Platform"])
def ping():
    return PingResponse(
        service=SERVICE_NAME,
        message="AI Core template is running",
        timestamp=datetime.now(timezone.utc).isoformat(),
    )


@app.post(
    "/api/v1/analyze",
    response_model=AnalysisResponse,
    responses={
        400: {
            "model": ErrorResponse,
            "description": "Dữ liệu đầu vào không hợp lệ (Base64 hỏng, không phải ảnh, vượt kích thước).",
        },
        422: {
            "model": ErrorResponse,
            "description": "Lỗi định dạng JSON, thiếu trường bắt buộc, hoặc modality/mode không được hỗ trợ.",
        },
        500: {
            "model": ErrorResponse,
            "description": "Lỗi nội bộ trong quá trình xử lý mô hình AI.",
        },
    },
    tags=["Analysis"],
)
def analyze_retina(request: AnalysisRequest):
    """
    Endpoint phân tích ảnh võng mạc (Mock Engine cho tuần 1 - SCRUM-62).
    Nhận chuỗi ảnh Base64 và thông tin yêu cầu, trả về kết quả phân vùng và chỉ số hình học.
    Được gọi nội bộ từ Analysis Worker.
    """
    # 1. Check blank image
    if not request.image_base64 or len(request.image_base64.strip()) == 0:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "INVALID_IMAGE_PAYLOAD",
                "message": "Chuỗi image_base64 không được để trống.",
                "details": None,
            },
        )

    # 2. Modality check: only FUNDUS supported, OCT rejected with UNSUPPORTED_MODALITY (422)
    norm_modality = request.modality.strip().upper() if request.modality else "FUNDUS"
    if norm_modality == "OCT":
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "UNSUPPORTED_MODALITY",
                "message": "unsupported_modality: Định dạng ảnh OCT chưa được hỗ trợ trong phiên bản hiện tại (chỉ hỗ trợ FUNDUS).",
                "details": {"supported_modalities": ["FUNDUS"], "received_modality": request.modality},
            },
        )
    elif norm_modality != "FUNDUS":
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "INVALID_MODALITY",
                "message": f"Modality '{request.modality}' không hợp lệ. Chỉ hỗ trợ FUNDUS.",
                "details": {"supported_modalities": ["FUNDUS"], "received_modality": request.modality},
            },
        )

    # 3. Mode check: only retina_vessels allowed
    if request.mode != AnalysisMode.RETINA_VESSELS:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "UNSUPPORTED_MODE",
                "message": f"Chế độ phân tích '{request.mode.value}' nằm ngoài phạm vi hỗ trợ của phiên bản v0.1 (chỉ hỗ trợ retina_vessels).",
                "details": {"supported_modes": ["retina_vessels"], "received_mode": request.mode.value},
            },
        )

    # 4. Strict Image payload validation & mock processing
    try:
        result = process_mock_analysis(request)
        return result
    except InvalidImagePayloadError as e:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "INVALID_IMAGE_PAYLOAD",
                "message": str(e),
                "details": {"supported_formats": ["PNG", "JPEG", "WEBP"]},
            },
        )
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={
                "error_code": "INFERENCE_RUNTIME_ERROR",
                "message": f"Lỗi nội bộ khi xử lý mô hình AI: {str(e)}",
                "details": None,
            },
        )
