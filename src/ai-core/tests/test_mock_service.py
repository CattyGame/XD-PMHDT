"""
Unit test for the mock inference service and endpoints (Module M4 - SCRUM-62).
Validates strict rejection of invalid base64, non-image payloads, OCT modality, and unsupported modes.
"""
import base64
import sys
import os

from fastapi.testclient import TestClient

from app.main import app
from app.schemas.common import EyeSide, AnalysisMode, RiskLevel, AnalysisStatus
from app.schemas.analysis import AnalysisRequest
from app.services.mock_service import process_mock_analysis, SAMPLE_VALID_PNG_BASE64

client = TestClient(app)


def test_mock_analysis_generation():
    req = AnalysisRequest(
        request_id="req_unit_test_001",
        patient_id="PAT-9999",
        eye_side=EyeSide.LEFT,
        mode=AnalysisMode.RETINA_VESSELS,
        modality="FUNDUS",
        image_base64=SAMPLE_VALID_PNG_BASE64,
        include_mask=True,
        include_overlay=True,
    )

    resp = process_mock_analysis(req)

    assert resp.request_id == "req_unit_test_001"
    assert resp.status == AnalysisStatus.SUCCESS
    assert resp.is_mock is True
    assert resp.model_version == "mock-v0.1"
    assert resp.threshold_version == "v0.1"
    assert resp.config_version == "v0.1"
    assert "Mock Engine" in resp.limitations
    assert resp.processing_time_ms > 0
    assert resp.image_info.eye_side == EyeSide.LEFT
    assert resp.image_info.modality == "FUNDUS"

    # Metrics range checks
    assert 0.05 <= resp.metrics.vessel_density <= 0.35
    assert 1.0 <= resp.metrics.tortuosity_index <= 2.0
    assert 0.5 <= resp.metrics.av_ratio <= 1.0
    assert resp.metrics.branching_points > 0

    # Risk checks
    assert resp.risk_assessment.risk_level in [RiskLevel.LOW, RiskLevel.MODERATE, RiskLevel.HIGH]
    assert 0.0 <= resp.risk_assessment.risk_score <= 1.0
    assert len(resp.risk_assessment.disclaimer) > 10

    # Segmentation checks
    assert resp.segmentation is not None
    assert resp.segmentation.mask_base64 is not None
    assert resp.segmentation.overlay_base64 is not None


def test_analyze_endpoint_success():
    payload = {
        "request_id": "req_api_001",
        "image_base64": SAMPLE_VALID_PNG_BASE64,
        "modality": "FUNDUS",
        "mode": "retina_vessels",
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "SUCCESS"
    assert data["is_mock"] is True
    assert data["model_version"] == "mock-v0.1"
    assert data["threshold_version"] == "v0.1"
    assert data["config_version"] == "v0.1"


def test_oct_modality_returns_422():
    payload = {
        "request_id": "req_api_oct",
        "image_base64": SAMPLE_VALID_PNG_BASE64,
        "modality": "OCT",
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 422
    data = response.json()
    assert data["error_code"] == "UNSUPPORTED_MODALITY"
    assert "OCT" in data["message"]


def test_invalid_modality_returns_422():
    payload = {
        "request_id": "req_api_mri",
        "image_base64": SAMPLE_VALID_PNG_BASE64,
        "modality": "MRI",
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 422
    data = response.json()
    assert data["error_code"] == "INVALID_MODALITY"


def test_unsupported_mode_returns_422():
    payload = {
        "request_id": "req_api_iris",
        "image_base64": SAMPLE_VALID_PNG_BASE64,
        "modality": "FUNDUS",
        "mode": "iris_biometrics",
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 422
    data = response.json()
    assert data["error_code"] == "UNSUPPORTED_MODE"


def test_blank_image_returns_400():
    payload = {
        "request_id": "req_api_blank",
        "image_base64": "",
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 400
    data = response.json()
    assert data["error_code"] == "INVALID_IMAGE_PAYLOAD"


def test_corrupted_base64_returns_400():
    payload = {
        "request_id": "req_api_corrupt",
        "image_base64": "!!!not_valid_base64_data???",
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 400
    data = response.json()
    assert data["error_code"] == "INVALID_IMAGE_PAYLOAD"


def test_non_image_bytes_returns_400():
    # Valid Base64 encoding of non-image plain text string
    fake_b64 = base64.b64encode(b"This is a plain text file, not an image.").decode("utf-8")
    payload = {
        "request_id": "req_api_non_image",
        "image_base64": fake_b64,
    }
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 400
    data = response.json()
    assert data["error_code"] == "INVALID_IMAGE_PAYLOAD"


def test_health_endpoint_conforms_to_contract():
    response = client.get("/health")
    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "ok"
    assert data["service"] == "AURA.AiCore"
    assert data["model_loaded"] is False
    assert data["version"] == "0.1.0"
    assert data["device"] == "cpu"
    assert "uptime_seconds" in data
    assert isinstance(data["uptime_seconds"], (int, float))
