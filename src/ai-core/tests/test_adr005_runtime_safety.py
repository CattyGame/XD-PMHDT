import pytest
import base64
import io
import uuid
from PIL import Image
from fastapi.testclient import TestClient

from app.main import app, is_explicit_mock_mode
from app.schemas.analysis import AnalysisRequest
from app.services.frangi_service import FrangiRuntimeConfig, LowQualityImageError, process_frangi_analysis


@pytest.fixture
def isolated_environment(monkeypatch):
    for name in (
        "AURA_AI_ENGINE", "AURA_ALLOW_MOCK", "AURA_RUNTIME_MODE",
        "AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", "AURA_SERVICE_AUTH_PUBLIC_KEY_PATH",
        "ServiceAuth__PublicKeyPem", "ServiceAuth__PublicKeyPath",
    ):
        monkeypatch.delenv(name, raising=False)
    return monkeypatch


def test_missing_engine_and_key_never_enable_mock(isolated_environment):
    assert is_explicit_mock_mode() is False
    with TestClient(app) as client:
        assert client.get("/ready").status_code == 503
        response = client.post("/api/v1/analyze", json={})
        assert response.status_code == 503
        assert response.json()["error_code"] == "SERVICE_NOT_READY"


@pytest.mark.parametrize("mode", ["LIVE", "PRODUCTION", ""])
def test_mock_is_blocked_outside_explicit_test_or_demo(isolated_environment, mode):
    isolated_environment.setenv("AURA_AI_ENGINE", "mock")
    isolated_environment.setenv("AURA_ALLOW_MOCK", "true")
    isolated_environment.setenv("AURA_RUNTIME_MODE", mode)
    assert is_explicit_mock_mode() is False
    with TestClient(app) as client:
        response = client.post("/api/v1/analyze", json={})
        assert response.status_code == 503


@pytest.mark.parametrize("allow", ["false", "", "1"])
def test_test_environment_alone_does_not_bypass_auth(isolated_environment, allow):
    isolated_environment.setenv("AURA_AI_ENGINE", "mock")
    isolated_environment.setenv("AURA_ALLOW_MOCK", allow)
    isolated_environment.setenv("AURA_RUNTIME_MODE", "TEST")
    assert is_explicit_mock_mode() is False


def test_mock_requires_all_three_explicit_settings(isolated_environment):
    isolated_environment.setenv("AURA_AI_ENGINE", "mock")
    isolated_environment.setenv("AURA_ALLOW_MOCK", "true")
    isolated_environment.setenv("AURA_RUNTIME_MODE", "TEST")
    assert is_explicit_mock_mode() is True
    # Mock is never healthy for production readiness even when test inference is enabled.
    with TestClient(app) as client:
        assert client.get("/ready").status_code == 503


def test_runtime_version_and_capabilities_are_truthful():
    with TestClient(app) as client:
        assert client.get("/openapi.json").json()["info"]["version"] == "1.3.0"
        capabilities = client.get("/").json()["capabilities"]
        assert capabilities["segmentation_algorithm"] == "FRANGI_VESSEL_SEGMENTATION"
        assert capabilities["dr_cnn_available"] is False
        assert capabilities["grad_cam_available"] is False
        assert capabilities["avr_available"] is False
        assert capabilities["clinical_validation"] == "NOT_ESTABLISHED"


def test_real_pipeline_black_image_abstains_before_segmentation_or_risk(monkeypatch):
    buffer = io.BytesIO()
    Image.new("RGB", (256, 256), (0, 0, 0)).save(buffer, format="PNG")
    request = AnalysisRequest(request_id=str(uuid.uuid4()), image_base64=base64.b64encode(buffer.getvalue()).decode("ascii"))
    segmentation = []
    monkeypatch.setattr("app.services.frangi_service.run_frangi_segmentation", lambda *args, **kwargs: segmentation.append(True))
    with pytest.raises(LowQualityImageError) as error:
        process_frangi_analysis(request, FrangiRuntimeConfig(risk_available=True))
    assert error.value.report.is_acceptable is False
    assert segmentation == []
