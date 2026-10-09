"""
Unit test validating that OpenAPI schemas, contracts, and Python models parse properly (SCRUM-61).
"""
import io
import json
import os
import sys
import yaml
from PIL import Image
from fastapi.testclient import TestClient

from app.main import app
from app.schemas import (
    AnalysisRequest,
    ImageInfo,
    VesselMetrics,
    RiskAssessment,
    SegmentationResult,
    AnalysisResponse,
    ErrorResponse,
    HealthResponse,
    PingResponse,
    EyeSide,
    AnalysisMode,
    RiskLevel,
    AnalysisStatus,
)

client = TestClient(app)


def create_tiny_png_base64() -> str:
    import base64
    buf = io.BytesIO()
    img = Image.new("RGB", (16, 16), color=(100, 150, 200))
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("utf-8")


def test_analysis_request_and_response():
    req = AnalysisRequest(
        request_id="req_test_001",
        patient_id="PAT-12345",
        eye_side=EyeSide.RIGHT,
        mode=AnalysisMode.RETINA_VESSELS,
        image_base64="fake_base64_data",
        include_mask=True,
        include_overlay=True,
    )
    assert req.request_id == "req_test_001"
    assert req.eye_side == EyeSide.RIGHT

    img_info = ImageInfo(
        width=512,
        height=512,
        channels=3,
        eye_side=req.eye_side,
        modality="FUNDUS",
    )

    metrics = VesselMetrics(
        vessel_density=0.155,
        tortuosity_index=1.12,
        av_ratio=0.68,
        fractal_dimension=1.45,
        branching_points=52,
    )

    risk = RiskAssessment(
        risk_score=0.28,
        risk_level=RiskLevel.LOW,
        confidence_score=0.91,
        indicators=["Mật độ mạch máu bình thường", "Chỉ số xoắn mạch ổn định"],
    )

    seg = SegmentationResult(
        mask_format="png_base64",
        mask_base64="mask_base64_string",
        overlay_base64="overlay_base64_string",
    )

    resp = AnalysisResponse(
        request_id=req.request_id,
        patient_id=req.patient_id,
        timestamp="2026-10-05T18:00:00Z",
        status=AnalysisStatus.SUCCESS,
        processing_time_ms=135.2,
        image_info=img_info,
        metrics=metrics,
        risk_assessment=risk,
        segmentation=seg,
    )

    assert resp.request_id == "req_test_001"
    assert resp.metrics.vessel_density == 0.155
    assert resp.risk_assessment.risk_level == RiskLevel.LOW
    assert resp.threshold_version == "v0.1"
    assert resp.config_version == "v0.1"
    assert resp.is_mock is True


def test_contract_yaml_and_json_sync():
    current_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(current_dir)))
    yaml_path = os.path.join(repo_root, "contracts", "ai_service_openapi.yaml")
    json_path = os.path.join(repo_root, "contracts", "ai_service_openapi.json")

    assert os.path.exists(yaml_path), f"Missing {yaml_path}"
    assert os.path.exists(json_path), f"Missing {json_path}"

    with open(yaml_path, "r", encoding="utf-8") as f:
        yaml_data = yaml.safe_load(f)
    with open(json_path, "r", encoding="utf-8") as f:
        json_data = json.load(f)

    assert yaml_data == json_data, "contracts/ai_service_openapi.json is not identical to yaml!"
    assert yaml_data["openapi"] == "3.0.3"
    assert "/health" in yaml_data["paths"]
    assert "/api/v1/platform/ping" in yaml_data["paths"]
    assert "/api/v1/analyze" in yaml_data["paths"]


def test_runtime_openapi_schema_matches_contract():
    resp = client.get("/openapi.json")
    assert resp.status_code == 200
    runtime = resp.json()

    # Paths check
    for path in ["/health", "/api/v1/platform/ping", "/api/v1/analyze"]:
        assert path in runtime["paths"], f"Missing {path} in runtime OpenAPI"

    # Analyze responses check: 200, 400, 422, 500
    analyze_responses = runtime["paths"]["/api/v1/analyze"]["post"]["responses"]
    for code in ["200", "400", "422", "500"]:
        assert code in analyze_responses, f"Missing HTTP {code} in /api/v1/analyze responses"

    # Component schemas check
    schemas = runtime["components"]["schemas"]
    expected_schemas = [
        "HealthResponse",
        "PingResponse",
        "AnalysisRequest",
        "AnalysisResponse",
        "ErrorResponse",
        "ImageInfo",
        "VesselMetrics",
        "RiskAssessment",
        "SegmentationResult",
        "EyeSide",
        "AnalysisMode",
        "RiskLevel",
    ]
    for s in expected_schemas:
        assert s in schemas, f"Missing schema {s} in runtime OpenAPI"


def test_api_real_calls_conform_to_error_and_response_schemas():
    valid_b64 = create_tiny_png_base64()

    # 1. Success 200 conforms to AnalysisResponse
    ok_payload = {
        "request_id": "req_verify_200",
        "image_base64": valid_b64,
        "modality": "FUNDUS",
        "mode": "retina_vessels",
    }
    resp = client.post("/api/v1/analyze", json=ok_payload)
    assert resp.status_code == 200
    analysis_obj = AnalysisResponse(**resp.json())
    assert analysis_obj.status == AnalysisStatus.SUCCESS
    assert analysis_obj.is_mock is True

    # 2. 400 Bad Request conforms to ErrorResponse
    bad_img_resp = client.post("/api/v1/analyze", json={"request_id": "req_400", "image_base64": ""})
    assert bad_img_resp.status_code == 400
    err_400 = ErrorResponse(**bad_img_resp.json())
    assert err_400.error_code == "INVALID_IMAGE_PAYLOAD"

    # 3. 422 Unsupported Modality conforms to ErrorResponse
    oct_resp = client.post("/api/v1/analyze", json={"request_id": "req_422", "image_base64": valid_b64, "modality": "OCT"})
    assert oct_resp.status_code == 422
    err_422_oct = ErrorResponse(**oct_resp.json())
    assert err_422_oct.error_code == "UNSUPPORTED_MODALITY"

    # 4. 422 Unsupported Mode conforms to ErrorResponse
    mode_resp = client.post("/api/v1/analyze", json={"request_id": "req_422_mode", "image_base64": valid_b64, "mode": "iris_biometrics"})
    assert mode_resp.status_code == 422
    err_422_mode = ErrorResponse(**mode_resp.json())
    assert err_422_mode.error_code == "UNSUPPORTED_MODE"

    # 5. 422 Schema Validation Error conforms to ErrorResponse
    invalid_schema_resp = client.post("/api/v1/analyze", json={"mode": "retina_vessels"})
    assert invalid_schema_resp.status_code == 422
    err_schema = ErrorResponse(**invalid_schema_resp.json())
    assert err_schema.error_code == "SCHEMA_VALIDATION_ERROR"
    assert "errors" in err_schema.details
