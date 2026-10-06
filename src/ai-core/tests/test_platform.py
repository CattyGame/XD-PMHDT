from datetime import datetime

from fastapi.testclient import TestClient

from app.main import app


client = TestClient(app)


def test_health_reports_model_not_loaded():
    response = client.get("/health")

    assert response.status_code == 200

    data = response.json()
    assert data["status"] == "ok"
    assert data["service"] == "AURA.AiCore"
    assert data["model_loaded"] is False


def test_ping_returns_service_and_timezone():
    response = client.get("/api/v1/platform/ping")

    assert response.status_code == 200

    data = response.json()
    assert data["service"] == "AURA.AiCore"

    timestamp = datetime.fromisoformat(
        data["timestamp"].replace("Z", "+00:00")
    )
    assert timestamp.tzinfo is not None


def test_openapi_contains_ping_endpoint():
    response = client.get("/openapi.json")

    assert response.status_code == 200
    assert "get" in response.json()["paths"]["/api/v1/platform/ping"]