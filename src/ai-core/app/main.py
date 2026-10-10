"""
AURA AI Core: platform endpoints and mock analysis API.
"""
import logging
import time
from datetime import datetime, timezone

from fastapi import FastAPI, HTTPException, Request, status
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from .schemas.common import AnalysisMode
from .schemas.health import HealthResponse, PingResponse
from .schemas.analysis import (
    AnalysisRequest,
    AnalysisResponse,
    ErrorResponse,
)
from .services.mock_service import (
    process_mock_analysis,
    InvalidImagePayloadError,
    MockLowQualityImageError,
)

APP_START_TIME = time.time()
SERVICE_NAME = "AURA.AiCore"
VERSION = "0.1.0"
API_VERSION = "1.1.0"

logger = logging.getLogger(__name__)

app = FastAPI(
    title="AURA AI Analysis Service API",
    description=(
        "AI API nội bộ của AURA. Runtime hiện là Mock Service phục vụ "
        "kiểm thử giao tiếp, chưa tích hợp thuật toán phân vùng thật. "
        "WARNING có kết quả hợp lệ không tự đồng nghĩa LOW_QUALITY."
    ),
    version=API_VERSION,
    contact={
        "name": "Dao Duy Quan (M4 Lead)",
        "email": "quandd2937@ut.edu.vn",
    },
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(
    request: Request,
    exc: RequestValidationError,
):
    errors = exc.errors()

    for error in errors:
        if "mode" in error.get("loc", ()):
            return JSONResponse(
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
                content={
                    "error_code": "UNSUPPORTED_MODE",
                    "message": (
                        "Chế độ phân tích không được hỗ trợ; "
                        "chỉ chấp nhận retina_vessels."
                    ),
                    "details": {
                        "supported_modes": ["retina_vessels"],
                        "received_mode": str(error.get("input", "")),
                        "errors": jsonable_encoder(errors),
                    },
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                },
            )

    return JSONResponse(
        status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
        content={
            "error_code": "SCHEMA_VALIDATION_ERROR",
            "message": (
                "Dữ liệu không đúng định dạng hoặc thiếu trường bắt buộc."
            ),
            "details": {"errors": jsonable_encoder(errors)},
            "timestamp": datetime.now(timezone.utc).isoformat(),
        },
    )


@app.exception_handler(HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):
    error_code = f"HTTP_{exc.status_code}"
    message = "Lỗi xử lý yêu cầu."
    details = None

    if isinstance(exc.detail, dict):
        error_code = exc.detail.get("error_code", error_code)
        message = exc.detail.get("message", message)
        details = exc.detail.get("details")
    elif isinstance(exc.detail, str):
        message = exc.detail
        if "unsupported_modality" in message.lower():
            error_code = "UNSUPPORTED_MODALITY"
        elif "invalid_image_payload" in message.lower():
            error_code = "INVALID_IMAGE_PAYLOAD"

    return JSONResponse(
        status_code=exc.status_code,
        content={
            "error_code": error_code,
            "message": message,
            "details": details,
            "timestamp": datetime.now(timezone.utc).isoformat(),
        },
        headers=exc.headers,
    )


@app.get("/", include_in_schema=False)
def root():
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
    return HealthResponse(
        status="ok",
        version=VERSION,
        service=SERVICE_NAME,
        device="cpu",
        model_loaded=False,
        uptime_seconds=round(time.time() - APP_START_TIME, 2),
    )


@app.get(
    "/api/v1/platform/ping",
    response_model=PingResponse,
    tags=["Platform"],
)
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
            "description": (
                "Base64 hỏng, không phải ảnh, ảnh hỏng hoặc vượt giới hạn."
            ),
        },
        422: {
            "model": ErrorResponse,
            "description": (
                "Sai schema, modality/mode không được hỗ trợ hoặc LOW_QUALITY_IMAGE."
            ),
        },
        500: {
            "model": ErrorResponse,
            "description": "Lỗi nội bộ hoặc cấu hình mock không hợp lệ.",
        },
    },
    tags=["Analysis"],
)
def analyze_retina(request: AnalysisRequest):
    """
    API nội bộ nhận ảnh Base64 và trả kết quả giả lập.

    Mặc định SUCCESS. WARNING được bật bằng cấu hình kiểm thử
    phía server và vẫn trả kết quả đầy đủ. LOW_QUALITY trả lỗi 422;
    INFERENCE_ERROR trả lỗi 500. Các kịch bản bổ sung cần bật rõ ràng.
    """
    if not request.image_base64 or not request.image_base64.strip():
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "INVALID_IMAGE_PAYLOAD",
                "message": "Chuỗi image_base64 không được để trống.",
                "details": None,
            },
        )

    norm_modality = (
        request.modality.strip().upper()
        if request.modality else "FUNDUS"
    )

    if norm_modality == "OCT":
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "UNSUPPORTED_MODALITY",
                "message": "OCT chưa được hỗ trợ; chỉ chấp nhận FUNDUS.",
                "details": {
                    "supported_modalities": ["FUNDUS"],
                    "received_modality": request.modality,
                },
            },
        )

    if norm_modality != "FUNDUS":
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "INVALID_MODALITY",
                "message": "Modality không hợp lệ; chỉ chấp nhận FUNDUS.",
                "details": {
                    "supported_modalities": ["FUNDUS"],
                    "received_modality": request.modality,
                },
            },
        )

    if request.mode != AnalysisMode.RETINA_VESSELS:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "UNSUPPORTED_MODE",
                "message": "Chỉ hỗ trợ chế độ retina_vessels.",
                "details": {
                    "supported_modes": ["retina_vessels"],
                    "received_mode": request.mode.value,
                },
            },
        )

    try:
        return process_mock_analysis(request)

    except InvalidImagePayloadError as error:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "INVALID_IMAGE_PAYLOAD",
                "message": str(error),
                "details": {
                    "supported_formats": ["PNG", "JPEG", "WEBP"]
                },
            },
        ) from error

    except MockLowQualityImageError as error:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "LOW_QUALITY_IMAGE",
                "message": str(error),
                "details": {
                    "is_mock": True,
                    "reason": "MOCK_LOW_QUALITY",
                    "quality_assessment_performed": False,
                },
            },
        ) from error

    except HTTPException:
        raise

    except Exception as error:
        logger.exception("AI analysis processing failed.")
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={
                "error_code": "INFERENCE_RUNTIME_ERROR",
                "message": "Lỗi nội bộ khi xử lý AI.",
                "details": None,
            },
        ) from error
