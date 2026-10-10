import base64
import io

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.main import app


@pytest.fixture
def client():
    with TestClient(app) as test_client:
        yield test_client


@pytest.fixture
def image_payload():
    buffer = io.BytesIO()
    Image.new("RGB", (64, 48), color=(80, 40, 20)).save(
        buffer, format="PNG"
    )
    return {
        "request_id": "mock-scenario-check-001",
        "image_base64": base64.b64encode(buffer.getvalue()).decode("ascii"),
        "include_mask": True,
        "include_overlay": True,
    }


def test_success_without_mock_scenario_enablement(
    client, image_payload, monkeypatch
):
    monkeypatch.setenv("AURA_MOCK_SCENARIO", "SUCCESS")
    monkeypatch.setenv("AURA_MOCK_SCENARIOS_ENABLED", "false")
    response = client.post("/api/v1/analyze", json=image_payload)
    assert response.status_code == 200, response.text
    result = response.json()
    assert result["status"] == "SUCCESS"
    assert result["warnings"] == []
    assert result["is_mock"] is True


def test_warning_preserves_results_and_segmentation(
    client, image_payload, monkeypatch
):
    monkeypatch.setenv("AURA_MOCK_SCENARIO", "WARNING")
    monkeypatch.setenv("AURA_MOCK_SCENARIOS_ENABLED", "true")
    response = client.post("/api/v1/analyze", json=image_payload)
    assert response.status_code == 200, response.text
    result = response.json()
    assert result["status"] == "WARNING"
    assert result["is_mock"] is True
    assert result["warnings"]
    assert result["warnings"][0]["code"] == "AI_WARNING"
    assert result["warnings"][0]["message"].strip()
    assert result["metrics"]
    assert result["risk_assessment"]
    assert result["image_info"]["width"] == 64
    assert result["image_info"]["height"] == 48
    assert result["segmentation"] is not None
    for field in ("mask_base64", "overlay_base64"):
        encoded = result["segmentation"][field]
        assert encoded
        image_bytes = base64.b64decode(encoded, validate=True)
        with Image.open(io.BytesIO(image_bytes)) as image:
            image.load()
            assert image.format == "PNG"
            assert image.size == (64, 48)
            if field == "mask_base64":
                assert image.mode == "L"
                assert set(image.tobytes()) <= {0, 255}
            else:
                assert image.mode == "RGB"


def test_warning_requires_explicit_mock_enablement(
    client, image_payload, monkeypatch
):
    monkeypatch.setenv("AURA_MOCK_SCENARIO", "WARNING")
    monkeypatch.setenv("AURA_MOCK_SCENARIOS_ENABLED", "false")
    response = client.post("/api/v1/analyze", json=image_payload)
    assert response.status_code == 500, response.text
    result = response.json()
    assert result["error_code"] == "INFERENCE_RUNTIME_ERROR"
    assert result.get("details") is None
    assert "metrics" not in result
    assert "segmentation" not in result


def test_runtime_openapi_documents_warnings(client):
    response = client.get("/openapi.json")
    assert response.status_code == 200
    schema = response.json()
    assert schema["info"]["version"] == "1.1.0"
    schemas = schema["components"]["schemas"]
    response_properties = schemas["AnalysisResponse"]["properties"]
    assert "warnings" in response_properties
    assert response_properties["warnings"]["type"] == "array"
    assert response_properties["warnings"]["items"]["$ref"] == (
        "#/components/schemas/AnalysisWarning"
    )
    warning_schema = schemas["AnalysisWarning"]
    assert {"code", "message"} <= set(warning_schema["required"])


@pytest.mark.parametrize(
    "scenario, expected_status, expected_code",
    [
        ("LOW_QUALITY", 422, "LOW_QUALITY_IMAGE"),
        ("INFERENCE_ERROR", 500, "INFERENCE_RUNTIME_ERROR"),
    ],
)
def test_enabled_mock_error_scenarios(
    client,
    image_payload,
    monkeypatch,
    scenario,
    expected_status,
    expected_code,
):
    monkeypatch.setenv("AURA_MOCK_SCENARIO", scenario)
    monkeypatch.setenv("AURA_MOCK_SCENARIOS_ENABLED", "true")
    response = client.post("/api/v1/analyze", json=image_payload)
    assert response.status_code == expected_status, response.text
    result = response.json()
    assert result["error_code"] == expected_code
    assert result["timestamp"]
    assert "metrics" not in result
    assert "segmentation" not in result
    if scenario == "LOW_QUALITY":
        assert result["details"]["is_mock"] is True
        assert result["details"]["reason"] == "MOCK_LOW_QUALITY"
        assert result["details"]["quality_assessment_performed"] is False
    else:
        assert result.get("details") is None
        assert result["message"] == "Lỗi nội bộ khi xử lý AI."


@pytest.mark.parametrize("scenario", ["LOW_QUALITY", "INFERENCE_ERROR"])
def test_mock_errors_require_explicit_enablement(
    client, image_payload, monkeypatch, scenario
):
    monkeypatch.setenv("AURA_MOCK_SCENARIO", scenario)
    monkeypatch.setenv("AURA_MOCK_SCENARIOS_ENABLED", "false")
    response = client.post("/api/v1/analyze", json=image_payload)
    assert response.status_code == 500, response.text
    result = response.json()
    assert result["error_code"] == "INFERENCE_RUNTIME_ERROR"
    assert result.get("details") is None
    assert "metrics" not in result
    assert "segmentation" not in result


def test_invalid_image_rejected_before_mock_quality_scenario(
    client, image_payload, monkeypatch
):
    monkeypatch.setenv("AURA_MOCK_SCENARIO", "LOW_QUALITY")
    monkeypatch.setenv("AURA_MOCK_SCENARIOS_ENABLED", "true")
    payload = {**image_payload, "image_base64": "not-valid-base64!!!"}
    response = client.post("/api/v1/analyze", json=payload)
    assert response.status_code == 400, response.text
    assert response.json()["error_code"] == "INVALID_IMAGE_PAYLOAD"
