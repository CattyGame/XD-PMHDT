import csv
import hashlib
import json
from pathlib import Path
from unittest.mock import patch

import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.main import app, CNN_SLOT
from app.services.avr_geometry import estimate_optic_disc, native_edge_diameter, trace_root_calibers
from app.services.avr_model import load_avr_model, pixel_probabilities, FEATURE_VERSION
from app.services.avr_service import calculate_automatic_avr
from scripts.prepare_avrdb import prepare, write_verified
from tests.test_research_http_metrics import payload
from tests.test_frangi_sprint4 import configured_frangi_v13_env, s4_rsa_keypair, s4_bearer_headers

ROOT = Path(__file__).resolve().parents[3]


def tiny_model():
    return dict(model_type="PIXEL_MLP_RGB_DOG", feature_version=FEATURE_VERSION,
                dataset_id="AVRDB", mean=[0.] * 25, std=[1.] * 25,
                vessel_threshold=0.8, layers=[dict(weight=np.zeros((25, 3)).tolist(), bias=[0., 0., 0.])])


@pytest.mark.parametrize("invalid", ["sha", "mean", "std", "threshold", "geometry", "empty", "root", "layer_type"])
def test_model_rejects_corruption_and_nonfinite_parameters(tmp_path, invalid):
    model = tiny_model()
    if invalid == "mean": model["mean"][0] = float("nan")
    if invalid == "std": model["std"][0] = float("inf")
    if invalid == "threshold": model["vessel_threshold"] = float("nan")
    if invalid == "geometry": model["layers"][0]["weight"] = [[0.]]
    if invalid == "empty": model["layers"] = []
    if invalid == "layer_type": model["layers"] = ["wrong layer"]
    if invalid == "root": model = []
    path = tmp_path / "model.json"
    path.write_text(json.dumps(model), encoding="utf-8")
    digest = hashlib.sha256(path.read_bytes()).hexdigest() if invalid != "sha" else "0" * 64
    with pytest.raises(ValueError):
        load_avr_model(str(path), digest)


def test_json_model_inference_has_normalized_probabilities(tmp_path):
    path = tmp_path / "model.json"
    path.write_text(json.dumps(tiny_model()), encoding="utf-8")
    model = load_avr_model(str(path), hashlib.sha256(path.read_bytes()).hexdigest())
    probabilities = pixel_probabilities(np.zeros((7, 9, 25), np.float32), model)
    np.testing.assert_allclose(probabilities, 1 / 3)
    assert probabilities.shape == (7, 9, 3)


@pytest.mark.parametrize("width", [4, 8, 16])
def test_native_diameter_measures_actual_edges(width):
    green = np.full((100, 100), 0.7, np.float32)
    green[:, 50 - width // 2:50 + width // 2] = 0.25
    measured = native_edge_diameter(green, 50, 49.5, np.array([0., 1.]), width / 2)
    assert measured is not None and abs(measured - width) <= 0.5
    assert native_edge_diameter(np.ones_like(green), 50, 50, np.array([0., 1.]), 4) is None


def test_disc_abstains_for_uniform_photo():
    assert estimate_optic_disc(np.full((200, 300, 3), 90, np.uint8)) is None


def test_native_root_tracing_keeps_diagonal_staircase():
    from scipy.ndimage import binary_dilation
    mask = np.zeros((260, 260), bool)
    for offset in range(25, 100):
        mask[130 + offset // 2, 130 + offset] = True
    mask = binary_dilation(mask, iterations=4)
    rgb = np.full((260, 260, 3), 180, np.uint8)
    rgb[mask] = 70
    traced = trace_root_calibers(rgb, mask, np.ones(mask.shape), dict(center_x=130, center_y=130, radius_px=30))
    assert len(traced["arteries"]) == 1 and not traced["veins"]
    assert traced["arteries"][0]["diameter_px"] > 3


def test_automatic_avr_never_fabricates_missing_six_vessel_result():
    rgb = np.full((260, 260, 3), 90, np.uint8)
    disc = dict(center_x=130, center_y=130, radius_px=20)
    model = {**tiny_model(), "sha256": "a" * 64}
    with patch('app.services.avr_geometry.estimate_optic_disc', return_value=disc), \
            patch('app.services.avr_model.segment_with_model', return_value=(np.zeros((260, 260)), np.zeros((260, 260)))):
        result = calculate_automatic_avr(rgb, model, generate_visualization=False)
    assert result["avr"] is None and not result["measurement_available"] and not result["risk_available"]
    assert result["unavailable_reason"] == "INSUFFICIENT_SIX_VESSEL_SETS"


def test_automatic_avr_preserves_ratio_above_one_without_claiming_clinical_risk():
    rgb = np.full((260, 260, 3), 90, np.uint8)
    traced = dict(arteries=[dict(diameter_px=18.) for _ in range(6)],
                  veins=[dict(diameter_px=10.) for _ in range(6)])
    with patch('app.services.avr_geometry.estimate_optic_disc', return_value=dict(center_x=130, center_y=130, radius_px=20)), \
            patch('app.services.avr_model.segment_with_model', return_value=(np.zeros((260, 260)), np.zeros((260, 260)))), \
            patch('app.services.avr_geometry.trace_root_calibers', return_value=traced):
        result = calculate_automatic_avr(rgb, {**tiny_model(), 'sha256': 'a' * 64}, generate_visualization=False)
    assert result['measurement_available'] and not result['risk_available']
    assert result['avr'] > 1 and result['avr'] == result['crae_px'] / result['crve_px']


def test_superseded_benchmark_cannot_republish_old_inventory():
    from scripts.benchmark_inspire_avr import run_inspire_avr_benchmark
    with pytest.raises(RuntimeError, match='Superseded'):
        run_inspire_avr_benchmark()


def test_preparation_refuses_wrong_archive_and_preserves_existing_output(tmp_path):
    archive = tmp_path / "bad.zip"
    archive.write_bytes(b"wrong archive")
    with pytest.raises(ValueError, match="SHA-256"):
        prepare(archive, tmp_path / "out", tmp_path / "manifest.csv")
    output = tmp_path / "existing.png"
    output.write_bytes(b"original")
    with pytest.raises(ValueError, match="differs"):
        write_verified(output, b"different")
    assert output.read_bytes() == b"original"


def test_avr_research_http_guards_and_dispatch(configured_frangi_v13_env, s4_bearer_headers, monkeypatch):
    with TestClient(app) as client:
        assert client.post('/api/v1/research/avr', json=payload()).status_code == 401
        monkeypatch.delenv('AURA_AVR_MODEL_PATH', raising=False)
        assert client.post('/api/v1/research/avr', json=payload(), headers=s4_bearer_headers).status_code == 503
        monkeypatch.setenv('AURA_AVR_MODEL_PATH', 'verified.json')
        monkeypatch.setenv('AURA_AVR_MODEL_SHA256', 'a' * 64)
        assert client.post('/api/v1/research/avr', json={**payload(), 'patient_id': 'PHI'}, headers=s4_bearer_headers).status_code == 422
        assert client.post('/api/v1/research/avr', json={**payload(), 'modality': 'OCT'}, headers=s4_bearer_headers).status_code == 422
        assert CNN_SLOT.acquire(blocking=False)
        try:
            with patch('app.main.decode_and_validate_fundus_image') as decode:
                assert client.post('/api/v1/research/avr', json=payload(), headers=s4_bearer_headers).status_code == 429
                decode.assert_not_called()
        finally:
            CNN_SLOT.release()
        with patch('app.services.avr_model.load_avr_model', return_value=tiny_model()) as loader, \
                patch('app.services.avr_service.calculate_automatic_avr', return_value={'status': 'RESEARCH_ONLY', 'risk_available': False}) as service:
            response = client.post('/api/v1/research/avr', json=payload(), headers={**s4_bearer_headers, 'X-Correlation-Id': 'avr-research'})
            assert response.status_code == 200 and response.headers['X-Correlation-Id'] == 'avr-research'
            loader.assert_called_once_with('verified.json', 'a' * 64)
            service.assert_called_once()
        with patch('app.services.avr_model.load_avr_model', side_effect=ValueError('corrupt artifact')):
            assert client.post('/api/v1/research/avr', json=payload(), headers=s4_bearer_headers).status_code == 503
