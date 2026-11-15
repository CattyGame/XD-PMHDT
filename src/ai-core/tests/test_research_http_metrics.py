from unittest.mock import patch
import base64
import io

from fastapi.testclient import TestClient
import numpy as np
from PIL import Image

from app.main import app, HTTP_COUNTS, CNN_SLOT
from tests.test_frangi_sprint3 import make_synthetic_fundus_png_b64
from tests.test_frangi_sprint4 import configured_frangi_v13_env, s4_rsa_keypair, s4_bearer_headers


def payload():
    return {'request_id': 'acb0e8be-f835-41ef-9d8f-cf7b59c72d41', 'patient_id': None,
            'image_base64': make_synthetic_fundus_png_b64(), 'modality': 'FUNDUS'}


def test_research_requires_auth_and_explicit_artifact(configured_frangi_v13_env, s4_bearer_headers, monkeypatch):
    monkeypatch.delenv('AURA_DR_CNN_CHECKPOINT', raising=False)
    monkeypatch.delenv('AURA_DR_CNN_SHA256', raising=False)
    with TestClient(app) as client:
        assert client.post('/api/v1/research/dr-screening', json=payload()).status_code == 401
        response = client.post('/api/v1/research/dr-screening', json=payload(), headers={**s4_bearer_headers, 'X-Correlation-Id': 'cnn-unavailable'})
    assert response.status_code == 503
    assert response.headers['X-Correlation-Id'] == 'cnn-unavailable'


def test_research_quality_gate_and_real_service_dispatch(configured_frangi_v13_env, s4_bearer_headers, monkeypatch):
    monkeypatch.setenv('AURA_DR_CNN_CHECKPOINT', 'verified.pt')
    monkeypatch.setenv('AURA_DR_CNN_SHA256', 'a' * 64)
    with TestClient(app) as client, patch('app.services.dr_cnn_service.screen_dr_cnn', return_value={'status': 'RESEARCH_ONLY', 'risk_available': False}) as service:
        response = client.post('/api/v1/research/dr-screening', json=payload(), headers=s4_bearer_headers)
        assert response.status_code == 200
        service.assert_called_once()
        assert service.call_args.args[1:] == ('verified.pt', 'a' * 64)
        rejected = {**payload(), 'patient_id': 'must-not-transmit-PHI'}
        assert client.post('/api/v1/research/dr-screening', json=rejected, headers=s4_bearer_headers).status_code == 422
        assert service.call_count == 1
        image = io.BytesIO()
        Image.new('RGB', (256, 256)).save(image, format='PNG')
        rejected = {**payload(), 'image_base64': base64.b64encode(image.getvalue()).decode('ascii')}
        response = client.post('/api/v1/research/dr-screening', json=rejected, headers=s4_bearer_headers)
        assert response.status_code == 422 and response.json()['error_code'] == 'LOW_QUALITY_IMAGE'
        assert service.call_count == 1


def test_metrics_count_actual_requests_and_bound_unknown_paths():
    key = ('GET', '/api/v1/platform/ping', 200)
    before = HTTP_COUNTS[key]
    with TestClient(app) as client:
        client.get('/api/v1/platform/ping')
        client.get('/api/v1/platform/ping')
        client.get('/unknown-patient-sensitive-id')
        text = client.get('/metrics').text
    assert HTTP_COUNTS[key] == before + 2
    assert f'http_requests_total{{method="GET",path="/api/v1/platform/ping",status="200"}} {before + 2}' in text
    assert 'unknown-patient-sensitive-id' not in text
    assert 'quantile=' not in text
    assert 'http_request_duration_seconds_count{method="GET",path="/api/v1/platform/ping"}' in text


def test_research_admission_rejects_busy_before_decoding(configured_frangi_v13_env, s4_bearer_headers, monkeypatch):
    monkeypatch.setenv('AURA_DR_CNN_CHECKPOINT', 'verified.pt')
    monkeypatch.setenv('AURA_DR_CNN_SHA256', 'a' * 64)
    assert CNN_SLOT.acquire(blocking=False)
    try:
        with TestClient(app) as client, patch('app.main.decode_and_validate_fundus_image') as decode:
            response = client.post('/api/v1/research/dr-screening', json=payload(), headers=s4_bearer_headers)
        assert response.status_code == 429
        decode.assert_not_called()
    finally:
        CNN_SLOT.release()
