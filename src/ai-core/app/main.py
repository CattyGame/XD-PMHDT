"""
AURA AI Core: platform endpoints, RS256 service JWT authentication,
real Frangi v0.2 retinal vessel segmentation API (Sprint 3 Contract 1.2.0),
and explicit opt-in mock mode for unit regression testing.
"""

from __future__ import annotations

import logging
import gc
import os
import pickle
import time
import uuid
from collections import Counter
from pathlib import Path
from threading import BoundedSemaphore, Lock
from typing import Optional, Tuple

from fastapi import Depends, FastAPI, Header, HTTPException, Request, Security, status
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, PlainTextResponse
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from .auth.service_jwt import (
    ServiceAuthConfigError,
    ServiceAuthError,
    load_config_verifier_from_env,
    load_verifier_from_env,
)
from .schemas.analysis import (
    AnalysisRequest,
    AnalysisResponse,
    ErrorResponse,
)
from .schemas.common import AnalysisMode
from .schemas.health import HealthResponse, PingResponse, ReadyResponse
from .services.frangi_service import (
    FrangiConfigError,
    FrangiRuntimeConfig,
    LowQualityImageError,
     SUPPORTED_FRANGI_FORMATS,
    load_frangi_config,
    decode_and_validate_fundus_image,
    estimate_prediction_region,
    evaluate_frangi_quality,
    process_frangi_analysis,
    utc_timestamp_z,
)
from .services.mock_service import (
    InvalidImagePayloadError,
    MockLowQualityImageError,
    process_mock_analysis,
)
from .services.integrated_inference import (
    IntegratedModelUnavailable,
    configured_artifacts,
    process_integrated_analysis,
    verify_integrated_runtime,
)
from .services.runtime_registry import RUNTIME_REGISTRY, RuntimeManifestError

_ORIGINAL_LOAD_FRANGI_CONFIG = load_frangi_config

APP_START_TIME = time.time()
SERVICE_NAME = "AURA.AiCore"
VERSION = "0.2.0"
API_VERSION = "1.3.0"
HTTP_COUNTS = Counter()
HTTP_SECONDS = Counter()
CNN_SLOT = BoundedSemaphore(1)
_WARM_LOCK = Lock()
_WARM_CACHE: Optional[Tuple[int, str, str, str, str, str, str]] = None


def reset_integrated_warm_cache() -> None:
    """Clear cached integrated warm verification state."""
    global _WARM_CACHE
    with _WARM_LOCK:
        _WARM_CACHE = None


def _current_warm_key(frangi_config: FrangiRuntimeConfig, artifacts: Tuple[str, str, str, str]) -> Tuple[int, str, str, str, str, str, str]:
    avr_path, avr_sha, dr_path, dr_sha = artifacts
    if load_frangi_config is _ORIGINAL_LOAD_FRANGI_CONFIG:
        snap = RUNTIME_REGISTRY.get_active_snapshot()
        return (snap.version, snap.artifact_manifest_id, snap.threshold_config_sha256, avr_path, avr_sha, dr_path, dr_sha)
    return (0, "override", frangi_config.config_version, avr_path, avr_sha, dr_path, dr_sha)


def _mark_integrated_warm(frangi_config: FrangiRuntimeConfig) -> None:
    global _WARM_CACHE
    artifacts = configured_artifacts()
    key = _current_warm_key(frangi_config, artifacts)
    with _WARM_LOCK:
        _WARM_CACHE = key


def _ensure_integrated_warm(frangi_config: FrangiRuntimeConfig) -> None:
    global _WARM_CACHE
    artifacts = configured_artifacts()
    key = _current_warm_key(frangi_config, artifacts)
    with _WARM_LOCK:
        if _WARM_CACHE == key:
            return
    if not CNN_SLOT.acquire(blocking=False):
        raise IntegratedModelUnavailable("Integrated inference slot is busy; readiness warm deferred.")
    try:
        verify_integrated_runtime(frangi_config)
        with _WARM_LOCK:
            _WARM_CACHE = key
    finally:
        CNN_SLOT.release()

logger = logging.getLogger(__name__)

service_bearer_scheme = HTTPBearer(
    scheme_name="serviceBearer",
    bearerFormat="JWT",
    description="RS256 service token issued by Identity (`aura-identity-s3-v1`) with audience `aura-ai` and scope `ai:analyze`.",
    auto_error=False,
)

app = FastAPI(
    title="AURA AI Analysis Service API",
    description=(
        "AI API nội bộ của AURA (Contract 1.3.0). Runtime mặc định chạy "
        "thuật toán phân vùng mạch máu võng mạc Frangi v0.2 thật theo cấu hình cố định "
        "trong config/ai-frangi.yaml và xác thực RS256 Service JWT fail-closed. "
        "Phân vùng Frangi chưa cung cấp đánh giá rủi ro lâm sàng (risk_available=false, "
        "risk_assessment=null, status=WARNING kèm RISK_UNAVAILABLE)."
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


@app.middleware("http")
async def correlation_id_middleware(request: Request, call_next):
    """
    Echo hoặc tự sinh correlation ID để phục vụ distributed tracing xuyên suốt
    Web -> Gateway -> Analysis API -> Worker -> AI Core.
    """
    started = time.perf_counter()
    raw_cid = request.headers.get("X-Correlation-Id")
    correlation_id = raw_cid.strip() if (raw_cid and raw_cid.strip()) else str(uuid.uuid4())
    try:
        response = await call_next(request)
    except Exception:
        logger.exception("Unhandled exception in AI Core request pipeline.")
        response = JSONResponse(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            content={
                "error_code": "INFERENCE_RUNTIME_ERROR",
                "message": "Lỗi nội bộ khi xử lý yêu cầu AI Core.",
                "details": None,
                "timestamp": utc_timestamp_z(),
            },
        )
    response.headers["X-Correlation-Id"] = correlation_id
    route = request.scope.get("route")
    path = getattr(route, "path", "unmatched")
    method = request.method if request.method in {"GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "HEAD"} else "OTHER"
    HTTP_COUNTS[(method, path, response.status_code)] += 1
    HTTP_SECONDS[(method, path)] += time.perf_counter() - started
    return response


def resolve_active_frangi_config() -> FrangiRuntimeConfig:
    """Return an immutable snapshot of active config; honors explicit unit-test monkeypatches only."""
    if load_frangi_config is not _ORIGINAL_LOAD_FRANGI_CONFIG:
        return load_frangi_config()
    return RUNTIME_REGISTRY.get_active_snapshot().config


def _verify_service_bearer(
    request: Request,
    verifier_loader,
    credentials: Optional[HTTPAuthorizationCredentials],
    authorization: Optional[str],
) -> None:
    try:
        verifier = verifier_loader()
    except ServiceAuthConfigError as error:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "SERVICE_NOT_READY",
                "message": str(error),
                "details": None,
            },
        ) from error

    raw_auth_header = authorization or request.headers.get("Authorization")
    if not raw_auth_header and credentials is not None:
        raw_auth_header = f"{credentials.scheme} {credentials.credentials}"

    try:
        verifier.verify_authorization_header(raw_auth_header)
    except ServiceAuthError as error:
        headers = {"WWW-Authenticate": "Bearer"} if error.status_code == 401 else None
        raise HTTPException(
            status_code=error.status_code,
            detail={
                "error_code": error.error_code,
                "message": error.message,
                "details": error.details,
            },
            headers=headers,
        ) from error


def verify_config_auth(
    request: Request,
    credentials: Optional[HTTPAuthorizationCredentials] = Security(service_bearer_scheme),
    authorization: Optional[str] = Header(default=None, alias="Authorization"),
) -> None:
    """Fail-closed RS256 Service JWT dependency for /internal/v1/ai/config (scope ai:config, client analysis-api)."""
    _verify_service_bearer(request, load_config_verifier_from_env, credentials, authorization)


def is_explicit_mock_mode() -> bool:
    """Mock is an explicit test/demo capability, never a missing-key fallback."""
    return (
        os.getenv("AURA_AI_ENGINE", "frangi").strip().lower() == "mock"
        and os.getenv("AURA_ALLOW_MOCK", "false").strip().lower() == "true"
        and os.getenv("AURA_RUNTIME_MODE", "LIVE").strip().upper() in {"TEST", "DEMO"}
    )


def verify_runtime_and_auth(
    request: Request,
    credentials: Optional[HTTPAuthorizationCredentials] = Security(service_bearer_scheme),
    authorization: Optional[str] = Header(default=None, alias="Authorization"),
) -> Optional[FrangiRuntimeConfig]:
    """
    FastAPI dependency executed before request body validation on POST /api/v1/analyze:
    - In explicit mock mode (unit regression only), skips JWT & Frangi config checks.
    - In default Sprint 3 Frangi mode, enforces fail-closed readiness (503) and
      RS256 service JWT authentication (401) + authorization (403).
    """
    if is_explicit_mock_mode():
        return None

    configured_engine = os.getenv("AURA_AI_ENGINE", "frangi").strip().lower()
    if configured_engine != "frangi":
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "SERVICE_NOT_READY",
                "message": (
                    f"AI engine '{configured_engine}' không khả dụng hoặc mock mode "
                    "bị khoá (allowMock=false) trong Sprint 3."
                ),
                "details": {"engine": configured_engine},
            },
        )

    try:
        frangi_config = resolve_active_frangi_config()
    except RuntimeManifestError as error:
        raise HTTPException(
            status_code=error.status_code,
            detail={
                "error_code": error.error_code,
                "message": error.message,
                "details": None,
            },
        ) from error
    except FrangiConfigError as error:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "SERVICE_NOT_READY",
                "message": f"Cấu hình Frangi chưa sẵn sàng: {error}",
                "details": None,
            },
        ) from error

    _verify_service_bearer(request, load_verifier_from_env, credentials, authorization)

    return frangi_config


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
                    "timestamp": utc_timestamp_z(),
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
            "timestamp": utc_timestamp_z(),
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
            "timestamp": utc_timestamp_z(),
        },
        headers=exc.headers,
    )


@app.get("/", include_in_schema=False)
def root():
    integrated = os.getenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false").strip().lower() == "true"
    return {
        "service": SERVICE_NAME,
        "version": VERSION,
        "api_version": API_VERSION,
        "status": "running",
        "health": "/health",
        "ready": "/ready",
        "ping": "/api/v1/platform/ping",
        "analyze": "/api/v1/analyze",
        "capabilities": {
            "segmentation_algorithm": "FRANGI_VESSEL_SEGMENTATION",
            "optional_research_risk_method": "HEURISTIC_VESSEL_DENSITY_TORTUOSITY",
            "dr_cnn_available": integrated,
            "grad_cam_available": integrated,
            "avr_available": integrated,
            "clinical_validation": "NOT_ESTABLISHED",
        },
    }


@app.get("/health", response_model=HealthResponse, tags=["Health"])
def health():
    """Liveness check only."""
    return HealthResponse(
        status="ok",
        version= "0.1.0",
        service=SERVICE_NAME,
        device="cpu",
        model_loaded=False,
        uptime_seconds=round(time.time() - APP_START_TIME, 2),
    )


@app.get(
    "/ready",
    response_model=ReadyResponse,
    responses={
        503: {
            "model": ErrorResponse,
            "description": "Not ready (missing/invalid Frangi config, missing/invalid JWT public key, or running in mock mode).",
        },
    },
    tags=["Health"],
)
def ready():
    """
    Readiness probe for Sprint 3: returns HTTP 200 only when real Frangi configuration
    and RS256 service JWT public key are loaded and validated; returns 503 otherwise.
    """
    configured_engine = os.getenv("AURA_AI_ENGINE", "frangi").strip().lower()
    if is_explicit_mock_mode() or configured_engine != "frangi":
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "SERVICE_NOT_READY",
                "message": "AI Core đang ở chế độ mock hoặc chưa bật engine Frangi thật.",
                "details": {
                    "engine": configured_engine,
                    "is_mock_mode": is_explicit_mock_mode(),
                },
            },
        )

    try:
        frangi_config = resolve_active_frangi_config()
    except RuntimeManifestError as error:
        raise HTTPException(
            status_code=error.status_code,
            detail={
                "error_code": error.error_code,
                "message": error.message,
                "details": None,
            },
        ) from error
    except FrangiConfigError as error:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "SERVICE_NOT_READY",
                "message": f"Cấu hình Frangi chưa sẵn sàng: {error}",
                "details": None,
            },
        ) from error

    try:
        load_verifier_from_env()
    except ServiceAuthConfigError as error:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "SERVICE_NOT_READY",
                "message": str(error),
                "details": None,
            },
        ) from error

    if os.getenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false").strip().lower() == "true":
        try:
            _ensure_integrated_warm(frangi_config)
        except IntegratedModelUnavailable as error:
            raise HTTPException(status_code=503, detail={"error_code": "SERVICE_NOT_READY",
                "message": str(error), "details": None}) from error

    return ReadyResponse(
        status="ready",
        service=SERVICE_NAME,
        version=VERSION,
        api_version=API_VERSION,
        engine=frangi_config.engine,
        model_version=frangi_config.model_version,
        config_version=frangi_config.config_version + (
            "-integrated-research-v1" if os.getenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false").strip().lower() == "true" else ""),
        threshold_version=frangi_config.threshold_version,
        jwt_verification_ready=True,
        timestamp=utc_timestamp_z(),
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
        timestamp=utc_timestamp_z(),
    )


@app.post(
    "/api/v1/analyze",
    operation_id="analyzeRetina",
    response_model=AnalysisResponse,
    responses={
        429: {"model": ErrorResponse, "description": "Inference slot busy when the optional research profile is enabled."},
        400: {
            "model": ErrorResponse,
            "description": "Invalid Base64/image payload",
        },
        401: {
            "model": ErrorResponse,
            "description": "Invalid token",
        },
        403: {
            "model": ErrorResponse,
            "description": "Wrong audience/scope",
        },
        422: {
            "model": ErrorResponse,
            "description": "LOW_QUALITY_IMAGE or unsupported input",
        },
        500: {
            "model": ErrorResponse,
            "description": "Processing failure",
        },
        503: {
            "model": ErrorResponse,
            "description": "Not ready",
        },
    },
    tags=["Analysis"],
)
def analyze_retina(
    request: AnalysisRequest,
    frangi_config: Optional[FrangiRuntimeConfig] = Depends(verify_runtime_and_auth),
):
    """
    API nội bộ nhận ảnh FUNDUS Base64 và thực thi phân vùng mạch máu võng mạc Frangi v0.2 thật.

    - Yêu cầu xác thực RS256 Service JWT (`iss=aura-identity-s3-v1`, `aud=aura-ai`, `scope=ai:analyze`).
    - Giữ nguyên kích thước ảnh gốc (không crop/resize 512), trả về PNG mask (`L`, `0/255`) và PNG overlay (`RGB`).
    - Trả về `status=WARNING` kèm `RISK_UNAVAILABLE`, `is_mock=false`, `risk_available=false`, `risk_assessment=null`.
    - Từ chối ảnh kém chất lượng với HTTP 422 `LOW_QUALITY_IMAGE`.
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

    # Explicit mock mode for unit/scenario regression tests only
    if is_explicit_mock_mode():
        try:
            return process_mock_analysis(request)
        except InvalidImagePayloadError as error:
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail={
                    "error_code": "INVALID_IMAGE_PAYLOAD",
                    "message": str(error),
                    "details": {
                        "supported_formats": ["PNG", "JPEG", "WEBP"],
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
            logger.exception("Mock AI analysis processing failed.")
            raise HTTPException(
                status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
                detail={
                    "error_code": "INFERENCE_RUNTIME_ERROR",
                    "message": "Lỗi nội bộ khi xử lý AI.",
                    "details": None,
                },
            ) from error

    # Sprint 3 Contract 1.2 strict request invariants in real Frangi mode
    try:
        uuid.UUID(str(request.request_id))
    except ValueError as error:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "SCHEMA_VALIDATION_ERROR",
                "message": "request_id phải là chuỗi UUID hợp lệ theo schema 1.2.",
                "details": {"field": "request_id", "received": request.request_id},
            },
        ) from error

    if request.patient_id is not None:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "SCHEMA_VALIDATION_ERROR",
                "message": "patient_id bắt buộc là null trong AI request contract 1.2.",
                "details": {"field": "patient_id"},
            },
        )

    if not request.include_mask or not request.include_overlay:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "SCHEMA_VALIDATION_ERROR",
                "message": "include_mask và include_overlay bắt buộc đều là true trong Sprint 3.",
                "details": {
                    "include_mask": request.include_mask,
                    "include_overlay": request.include_overlay,
                },
            },
        )

    integrated = os.getenv('AURA_INTEGRATED_RESEARCH_ENABLED', 'false').strip().lower() == 'true'
    bounded_profile = integrated or bool((os.getenv('AURA_AVR_MODEL_PATH') and os.getenv('AURA_AVR_MODEL_SHA256'))
                           or (os.getenv('AURA_DR_CNN_CHECKPOINT') and os.getenv('AURA_DR_CNN_SHA256')))
    if bounded_profile and not CNN_SLOT.acquire(blocking=False):
        raise HTTPException(429, detail={"error_code": "RESEARCH_MODEL_BUSY", "message": "Inference slot is busy; retry later."},
                            headers={"Retry-After": "1"})
    try:
        if integrated:
            return process_integrated_analysis(request, frangi_config)
        return process_frangi_analysis(request, config=frangi_config)

    except IntegratedModelUnavailable as error:
        raise HTTPException(503, detail={"error_code": "RESEARCH_MODEL_UNAVAILABLE",
                            "message": "Verified integrated AVR/DR artifacts are unavailable."}) from error

    except InvalidImagePayloadError as error:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "INVALID_IMAGE_PAYLOAD",
                "message": str(error),
                "details": {
                    "supported_formats": list(SUPPORTED_FRANGI_FORMATS),
                },
            },
        ) from error

    except LowQualityImageError as error:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "error_code": "LOW_QUALITY_IMAGE",
                "message": str(error),
                "details": error.report.to_details_dict(),
            },
        ) from error

    except HTTPException:
        raise

    except Exception as error:
        logger.exception("Real Frangi AI analysis processing failed.")
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={
                "error_code": "INFERENCE_RUNTIME_ERROR",
                "message": "Lỗi nội bộ khi xử lý phân vùng mạch máu Frangi.",
                "details": None,
            },
        ) from error


    finally:
        if bounded_profile:
            CNN_SLOT.release()


@app.get("/metrics", response_class=PlainTextResponse)
async def metrics_endpoint():
    """Prometheus metrics exposition endpoint for AI Core."""
    uptime = time.time() - APP_START_TIME
    mem_bytes = None
    cpu = os.times()
    cpu_seconds = cpu.user + cpu.system
    try:
        import psutil
        proc = psutil.Process()
        mem_bytes = proc.memory_info().rss
        cpu_seconds = sum(proc.cpu_times()[:2])
    except ImportError:
        if Path("/proc/self/statm").is_file():
            mem_bytes = int(Path("/proc/self/statm").read_text().split()[1]) * os.sysconf("SC_PAGE_SIZE")

    lines = [
        "# HELP http_requests_total Total number of HTTP requests processed",
        "# TYPE http_requests_total counter",
        "# HELP http_request_duration_seconds HTTP request latencies in seconds",
        "# TYPE http_request_duration_seconds summary",
        "# HELP process_cpu_seconds_total Total user and system CPU time spent in seconds",
        "# TYPE process_cpu_seconds_total counter",
        f"process_cpu_seconds_total {cpu_seconds:.2f}",
        "# HELP process_uptime_seconds The uptime of the process in seconds",
        "# TYPE process_uptime_seconds gauge",
        f"process_uptime_seconds {uptime:.1f}",
    ]
    for (method, path, code), count in sorted(HTTP_COUNTS.items()):
        lines.append(f'http_requests_total{{method="{method}",path="{path}",status="{code}"}} {count}')
    for (method, path), seconds in sorted(HTTP_SECONDS.items()):
        count = sum(n for (m, p, _), n in HTTP_COUNTS.items() if m == method and p == path)
        labels = f'method="{method}",path="{path}"'
        lines.extend([f'http_request_duration_seconds_sum{{{labels}}} {seconds}',
                      f'http_request_duration_seconds_count{{{labels}}} {count}'])
    if mem_bytes is not None:
        lines.extend(["# TYPE process_resident_memory_bytes gauge", f"process_resident_memory_bytes {mem_bytes}"])
    lines.extend(["# TYPE python_gc_collections_total counter", "# TYPE python_gc_uncollectable_total counter"])
    for generation, statistics in enumerate(gc.get_stats()):
        lines.append(f'python_gc_collections_total{{generation="{generation}"}} {statistics["collections"]}')
        lines.append(f'python_gc_uncollectable_total{{generation="{generation}"}} {statistics["uncollectable"]}')
    return "\n".join(lines) + "\n"


def run_research_inference(request, config, path, digest, inference):
    if config is None or not path or not digest:
        raise HTTPException(503, detail={"error_code": "RESEARCH_MODEL_UNAVAILABLE", "message": "Configure a verified research artifact and real service authentication."})
    if request.patient_id is not None or request.modality.strip().upper() != "FUNDUS":
        raise HTTPException(422, detail={"error_code": "INVALID_RESEARCH_REQUEST", "message": "FUNDUS only; patient_id must be null."})
    if not CNN_SLOT.acquire(blocking=False):
        raise HTTPException(429, detail={"error_code": "RESEARCH_MODEL_BUSY", "message": "Retry after the current research inference completes."},
                            headers={"Retry-After": "1"})
    try:
        try:
            uuid.UUID(request.request_id)
            rgb, _, _ = decode_and_validate_fundus_image(request.image_base64, config)
            report = evaluate_frangi_quality(rgb, estimate_prediction_region(rgb, config), config)
            if not report.is_acceptable:
                raise HTTPException(422, detail={"error_code": "LOW_QUALITY_IMAGE", "message": report.message})
        except ValueError as error:
            raise HTTPException(400, detail={"error_code": "INVALID_IMAGE_PAYLOAD", "message": str(error)}) from error
        try:
            return {"request_id": request.request_id, **inference(rgb, path, digest)}
        except (ImportError, OSError, ValueError, RuntimeError, KeyError, TypeError, IndexError, EOFError, pickle.UnpicklingError) as error:
            logger.warning("Research model unavailable: %s", type(error).__name__)
            raise HTTPException(503, detail={"error_code": "RESEARCH_MODEL_UNAVAILABLE", "message": "Research dependencies or verified artifact are unavailable."}) from error
    finally:
        CNN_SLOT.release()


@app.post("/api/v1/research/dr-screening", tags=["Research"],
          summary="Experimental HRF binary ResNet34 with gradient Grad-CAM; not clinical grading")
def research_dr_screening(request: AnalysisRequest,
                          config: Optional[FrangiRuntimeConfig] = Depends(verify_runtime_and_auth)):
    def infer(rgb, path, digest):
        from .services.dr_cnn_service import screen_dr_cnn
        return screen_dr_cnn(rgb, path, digest)
    return run_research_inference(request, config, os.getenv("AURA_DR_CNN_CHECKPOINT"),
                                  os.getenv("AURA_DR_CNN_SHA256"), infer)


@app.post("/api/v1/research/avr", tags=["Research"],
          summary="Experimental A/V segmentation and native-pixel vessel diameters; abstains when insufficient")
def research_avr(request: AnalysisRequest,
                 config: Optional[FrangiRuntimeConfig] = Depends(verify_runtime_and_auth)):
    def infer(rgb, path, digest):
        from .services.avr_model import load_avr_model
        from .services.avr_service import calculate_automatic_avr
        return calculate_automatic_avr(rgb, load_avr_model(path, digest),
                                       generate_visualization=request.include_overlay, include_masks=request.include_mask)
    return run_research_inference(request, config, os.getenv("AURA_AVR_MODEL_PATH"),
                                  os.getenv("AURA_AVR_MODEL_SHA256"), infer)


def _raise_manifest_http_error(error: RuntimeManifestError) -> None:
    raise HTTPException(
        status_code=error.status_code,
        detail={
            "error_code": error.error_code,
            "message": error.message,
            "details": None,
        },
    ) from error


async def _parse_json_object(request: Request) -> dict:
    try:
        payload = await request.json()
    except Exception as error:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "ERR_VALIDATION",
                "message": "Request body must be valid JSON.",
                "details": None,
            },
        ) from error
    if not isinstance(payload, dict):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "ERR_VALIDATION",
                "message": "Request body must be a JSON object.",
                "details": None,
            },
        )
    return payload


@app.get("/internal/v1/ai/config", tags=["InternalConfig"], include_in_schema=False)
def get_internal_ai_config(_: None = Depends(verify_config_auth)):
    """Return active AI runtime manifest pointer managed by RUNTIME_REGISTRY."""
    try:
        return RUNTIME_REGISTRY.get_view()
    except RuntimeManifestError as error:
        _raise_manifest_http_error(error)


@app.put("/internal/v1/ai/config", tags=["InternalConfig"], include_in_schema=False)
async def put_internal_ai_config(
    request: Request,
    _: None = Depends(verify_config_auth),
    idempotency_key: Optional[str] = Header(default=None, alias="Idempotency-Key"),
):
    """Validate candidate manifest, run real smoke check, and atomically switch active config."""
    payload = await _parse_json_object(request)
    integrated = os.getenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false").strip().lower() == "true"
    if integrated and not CNN_SLOT.acquire(blocking=False):
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "ERR_SMOKE_CHECK_FAILED",
                "message": "Integrated inference slot is busy; candidate smoke deferred.",
                "details": None,
            },
        )
    try:
        snapshot = RUNTIME_REGISTRY.activate_manifest(payload, idempotency_key=idempotency_key)
        if integrated:
            _mark_integrated_warm(snapshot.config)
        else:
            reset_integrated_warm_cache()
        return RUNTIME_REGISTRY.snapshot_to_view(snapshot)
    except RuntimeManifestError as error:
        _raise_manifest_http_error(error)
    finally:
        if integrated:
            CNN_SLOT.release()


@app.post("/internal/v1/ai/config/rollback", tags=["InternalConfig"], include_in_schema=False)
async def rollback_internal_ai_config(
    request: Request,
    _: None = Depends(verify_config_auth),
    idempotency_key: Optional[str] = Header(default=None, alias="Idempotency-Key"),
):
    """Smoke-verify and atomically restore the previous verified runtime manifest."""
    payload = await _parse_json_object(request)
    allowed = {"expectedVersion", "reason"}
    if not allowed.issuperset(payload.keys()):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail={
                "error_code": "ERR_VALIDATION",
                "message": "Rollback payload contains unsupported fields.",
                "details": None,
            },
        )
    integrated = os.getenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false").strip().lower() == "true"
    if integrated and not CNN_SLOT.acquire(blocking=False):
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail={
                "error_code": "ERR_SMOKE_CHECK_FAILED",
                "message": "Integrated inference slot is busy; rollback smoke deferred.",
                "details": None,
            },
        )
    try:
        snapshot = RUNTIME_REGISTRY.rollback_manifest(
            expected_version=payload.get("expectedVersion"),
            reason=str(payload.get("reason", "")),
            idempotency_key=idempotency_key,
        )
        if integrated:
            _mark_integrated_warm(snapshot.config)
        else:
            reset_integrated_warm_cache()
        return RUNTIME_REGISTRY.snapshot_to_view(snapshot)
    except RuntimeManifestError as error:
        _raise_manifest_http_error(error)
    finally:
        if integrated:
            CNN_SLOT.release()


@app.get("/internal/v1/ai/config/commands/{idempotency_key}", tags=["InternalConfig"], include_in_schema=False)
def get_internal_ai_config_command(
    idempotency_key: str,
    _: None = Depends(verify_config_auth),
):
    """Query committed receipt for a previously submitted idempotent config command."""
    try:
        snapshot = RUNTIME_REGISTRY.get_idempotent_receipt(idempotency_key)
        if snapshot is None:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND,
                detail={
                    "error_code": "ERR_COMMAND_NOT_FOUND",
                    "message": "Idempotent AI config command receipt not found.",
                    "details": None,
                },
            )
        return RUNTIME_REGISTRY.snapshot_to_view(snapshot)
    except RuntimeManifestError as error:
        _raise_manifest_http_error(error)
