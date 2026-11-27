"""
Sprint 6 Verification Suite for Module M4 (W6-M4-01 / W6-M4-02 / W6-M4-03).

Covers:
1. TC-W6-M4-01: Honest research/clinical/mock separation, transform & Grad-CAM provenance,
   independent DR output when AVR abstains, LOW_QUALITY_IMAGE gate, and 429 RESEARCH_MODEL_BUSY.
2. TC-W6-M4-02: Verified server-side AI manifest registry (400/404/409/503 fail-closed),
   candidate smoke check before atomic switch, in-flight snapshot pinning, rollback, and
   internal-only AI Core boundary (no public reload endpoint).
3. TC-W6-M4-03: Model Card V2 registry verification, dataset/split/license separation,
   negative-control tamper detection, and honest SCRUM-373 OPEN_NOT_PASSED gate.
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import sys
import time
import uuid
from pathlib import Path
from typing import Any, Dict

import pytest
from fastapi.testclient import TestClient
from PIL import Image
from pydantic import ValidationError

from app import main as main_module
from app.auth.service_jwt import DEFAULT_AUDIENCE, DEFAULT_ISSUER, DEFAULT_REQUIRED_SCOPE
from app.main import app, resolve_active_frangi_config
from app.schemas.analysis import AnalysisRequest, AnalysisResponse, DrResearchResult
from app.services import integrated_inference
from app.services.runtime_registry import (
    RuntimeManifestError,
    RuntimeManifestRegistry,
    default_smoke_runner,
)
from scripts import verify_model_card_v2_registry as registry_verifier
from tests.test_frangi_sprint3 import (
    EXPECTED_CLIENT_ID,
    VALID_SUB,
    generate_deterministic_rsa_keypair,
    make_synthetic_fundus_png_b64,
    sign_rs256_jwt,
)

REPO_ROOT = Path(__file__).resolve().parents[3]
AI_CORE_DIR = REPO_ROOT / "src" / "ai-core"
MANIFEST_PATH = AI_CORE_DIR / "config" / "artifact-manifests.json"
TINY_PNG_B64 = base64.b64encode(
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01"
    b"\x08\x02\x00\x00\x00\x90wS\xde\x00\x00\x00\x0cIDATx\x9cc\xf8\xcf\xc0"
    b"\x00\x00\x03\x01\x01\x00\xc9\xfe\x92\xef\x00\x00\x00\x00IEND\xaeB`\x82"
).decode("ascii")


def _auth_headers(monkeypatch) -> Dict[str, str]:
    keypair = generate_deterministic_rsa_keypair(seed=20261118, bits=2048)
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", keypair.public_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)
    now = int(time.time())
    token = sign_rs256_jwt(
        keypair,
        header={"alg": "RS256", "typ": "JWT", "kid": keypair.key_id},
        payload={
            "iss": DEFAULT_ISSUER,
            "aud": DEFAULT_AUDIENCE,
            "sub": VALID_SUB,
            "client_id": EXPECTED_CLIENT_ID,
            "scope": DEFAULT_REQUIRED_SCOPE,
            "iat": now,
            "nbf": now,
            "exp": now + 300,
        },
    )
    return {"Authorization": f"Bearer {token}"}


def _base_response_payload() -> Dict[str, Any]:
    return {
        "request_id": str(uuid.uuid4()),
        "status": "WARNING",
        "is_mock": False,
        "risk_available": False,
        "model_version": "frangi-v0.2",
        "config_version": "s3-frangi-1.0.0-integrated-research-v1",
        "threshold_version": "frangi-threshold-0.02-v1",
        "processing_time_ms": 12.5,
        "image_info": {"width": 160, "height": 160, "channels": 3, "eye_side": "unknown", "modality": "FUNDUS"},
        "metrics": {"vessel_density": 0.14, "tortuosity_index": None, "av_ratio": None, "fractal_dimension": None, "branching_points": None},
        "risk_assessment": None,
        "segmentation": {"mask_format": "png_base64", "mask_base64": TINY_PNG_B64, "overlay_base64": TINY_PNG_B64},
        "research": {
            "avr": {
                "measurement_available": False,
                "avr": None,
                "crae_px": None,
                "crve_px": None,
                "unavailable_reason": "INSUFFICIENT_SIX_VESSEL_SETS",
                "model_version": "avr-v8-rrwnet-test",
                "model_sha256": "a" * 64,
            },
            "dr": {
                "probability_dr": 0.61,
                "model_version": "resnet34-hrf-v1",
                "checkpoint_sha256": "b" * 64,
                "gradcam_method": "GRAD_CAM",
                "gradcam_geometry": "IMAGENET_CENTER_CROP_224",
            },
        },
        "warnings": [{"code": "AVR_UNAVAILABLE", "message": "AVR unavailable"}],
        "timestamp": "2026-11-18T00:00:00Z",
        "limitations": "Binary HRF DR and Zone B AVR; not ICDR grading.",
    }


def _fake_artifact_files(tmp_path: Path, monkeypatch) -> tuple[str, str]:
    avr_file = tmp_path / "avr.bin"
    avr_file.write_bytes(b"avr-test-artifact")
    avr_sha = hashlib.sha256(avr_file.read_bytes()).hexdigest()
    dr_file = tmp_path / "dr.pt"
    dr_file.write_bytes(b"dr-test-checkpoint")
    dr_sha = hashlib.sha256(dr_file.read_bytes()).hexdigest()
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED", "true")
    monkeypatch.setenv("AURA_AVR_MODEL_PATH", str(avr_file))
    monkeypatch.setenv("AURA_AVR_MODEL_SHA256", avr_sha)
    monkeypatch.setenv("AURA_DR_CNN_CHECKPOINT", str(dr_file))
    monkeypatch.setenv("AURA_DR_CNN_SHA256", dr_sha)
    return avr_sha, dr_sha


# ==============================================================================
# TC-W6-M4-01: Schema separation, honest provenance, abstention & busy/quality
# ==============================================================================


def test_w6_m4_01_schema_rejects_mock_or_clinical_risk_with_research():
    valid = AnalysisResponse.model_validate(_base_response_payload())
    assert valid.is_mock is False
    assert valid.risk_available is False
    assert valid.metrics.av_ratio is None

    mock_payload = _base_response_payload()
    mock_payload["is_mock"] = True
    with pytest.raises(ValidationError, match="mock"):
        AnalysisResponse.model_validate(mock_payload)

    clinical_payload = _base_response_payload()
    clinical_payload["risk_available"] = True
    clinical_payload["risk_assessment"] = {
        "risk_score": 0.42,
        "risk_level": "MODERATE",
        "confidence_score": 0.85,
        "indicators": ["test"],
    }
    with pytest.raises(ValidationError, match="clinical risk"):
        AnalysisResponse.model_validate(clinical_payload)

    prob_desc = DrResearchResult.model_fields["probability_dr"].description or ""
    geom_desc = DrResearchResult.model_fields["gradcam_geometry"].description or ""
    assert "HRF" in prob_desc and "ICDR" in prob_desc
    assert "Resize(shorter_edge=256)" in geom_desc and "CenterCrop(224x224)" in geom_desc


def test_w6_m4_01_integrated_avr_unavailable_preserves_independent_dr_output(tmp_path: Path, monkeypatch):
    avr_sha, dr_sha = _fake_artifact_files(tmp_path, monkeypatch)
    fake_avr_mod = type("M", (), {"load_avr_model": staticmethod(lambda p, s: object())})
    fake_avr_svc = type(
        "S",
        (),
        {
            "calculate_automatic_avr": staticmethod(
                lambda rgb, model, generate_visualization=True, include_masks=False: {
                    "measurement_available": False,
                    "avr": None,
                    "crae_px": None,
                    "crve_px": None,
                    "unavailable_reason": "INSUFFICIENT_SIX_VESSEL_SETS",
                    "model_version": "avr-v8-rrwnet-test",
                    "model_sha256": avr_sha,
                    "overlay_base64_png": TINY_PNG_B64,
                }
            )
        },
    )
    fake_dr_svc = type(
        "D",
        (),
        {
            "screen_dr_cnn": staticmethod(
                lambda rgb, p, s: {
                    "probability_dr": 0.73,
                    "model_version": "resnet34-hrf-v1",
                    "checkpoint_sha256": dr_sha,
                    "explainability": {
                        "method": "GRAD_CAM",
                        "geometry": "IMAGENET_CENTER_CROP_224",
                        "overlay_base64_png": TINY_PNG_B64,
                    },
                }
            )
        },
    )
    monkeypatch.setitem(sys.modules, "app.services.avr_model", fake_avr_mod)
    monkeypatch.setitem(sys.modules, "app.services.avr_service", fake_avr_svc)
    monkeypatch.setitem(sys.modules, "app.services.dr_cnn_service", fake_dr_svc)

    cfg = resolve_active_frangi_config()
    req = AnalysisRequest(request_id=str(uuid.uuid4()), image_base64=make_synthetic_fundus_png_b64())
    resp = integrated_inference.process_integrated_analysis(req, cfg)

    assert resp.is_mock is False
    assert resp.risk_available is False
    assert resp.risk_assessment is None
    assert resp.metrics.av_ratio is None
    assert resp.research is not None
    assert resp.research.avr.measurement_available is False
    assert resp.research.avr.avr is None
    assert resp.research.avr.unavailable_reason == "INSUFFICIENT_SIX_VESSEL_SETS"
    assert resp.research.dr.probability_dr == pytest.approx(0.73)
    assert resp.research.dr.gradcam_geometry == "IMAGENET_CENTER_CROP_224"
    warning_codes = {w.code for w in resp.warnings}
    assert {"AVR_UNAVAILABLE", "RESEARCH_RESULTS"} <= warning_codes


def test_w6_m4_01_busy_429_and_low_quality_422_gates(tmp_path: Path, monkeypatch):
    headers = _auth_headers(monkeypatch)
    _fake_artifact_files(tmp_path, monkeypatch)

    with TestClient(app) as client:
        dark = Image.new("RGB", (128, 128), color=(0, 0, 0))
        buf = io.BytesIO()
        dark.save(buf, format="PNG")
        dark_b64 = base64.b64encode(buf.getvalue()).decode("ascii")
        q_resp = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "image_base64": dark_b64},
        )
        assert q_resp.status_code == 422
        assert q_resp.json()["error_code"] == "LOW_QUALITY_IMAGE"

        assert main_module.CNN_SLOT.acquire(blocking=False) is True
        try:
            busy_resp = client.post(
                "/api/v1/analyze",
                headers=headers,
                json={"request_id": str(uuid.uuid4()), "image_base64": make_synthetic_fundus_png_b64()},
            )
            assert busy_resp.status_code == 429
            assert busy_resp.headers.get("retry-after") == "1"
            assert busy_resp.json()["error_code"] == "RESEARCH_MODEL_BUSY"
        finally:
            main_module.CNN_SLOT.release()


# ==============================================================================
# TC-W6-M4-02: Verified manifest activation, smoke gate, pinning & rollback
# ==============================================================================


def _manifest_shas() -> Dict[str, str]:
    raw = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    return {item["artifactManifestId"]: item["thresholdConfigSha256"] for item in raw["manifests"]}


def test_w6_m4_02_manifest_registry_validation_smoke_switch_pinning_and_rollback(monkeypatch):
    shas = _manifest_shas()
    m_base = "11111111-1111-4111-8111-111111110301"
    m_v13 = "11111111-1111-4111-8111-111111110405"
    registry = RuntimeManifestRegistry()

    initial = registry.get_active_snapshot()
    assert initial.version == 1
    assert initial.artifact_manifest_id == m_base
    assert initial.config.config_version == "s3-frangi-1.0.0"

    # 400 rejects arbitrary weights path / URL fields
    with pytest.raises(RuntimeManifestError) as err_extra:
        registry.activate_manifest(
            {
                "expectedVersion": 1,
                "artifactManifestId": m_v13,
                "thresholdConfigSha256": shas[m_v13],
                "reason": "bad",
                "weights_path": "/etc/passwd",
            }
        )
    assert err_extra.value.status_code == 400

    # 404 unknown manifest
    with pytest.raises(RuntimeManifestError) as err_404:
        registry.activate_manifest(
            {
                "expectedVersion": 1,
                "artifactManifestId": "99999999-9999-4999-8999-999999999999",
                "thresholdConfigSha256": shas[m_v13],
                "reason": "unknown",
            }
        )
    assert err_404.value.status_code == 404

    # 409 stale expectedVersion or hash mismatch
    with pytest.raises(RuntimeManifestError) as err_ver:
        registry.activate_manifest(
            {"expectedVersion": 99, "artifactManifestId": m_v13, "thresholdConfigSha256": shas[m_v13], "reason": "stale"}
        )
    assert err_ver.value.status_code == 409

    with pytest.raises(RuntimeManifestError) as err_hash:
        registry.activate_manifest(
            {"expectedVersion": 1, "artifactManifestId": m_v13, "thresholdConfigSha256": "0" * 64, "reason": "tampered"}
        )
    assert err_hash.value.status_code == 409

    # 503 candidate smoke failure keeps version=1 active
    with pytest.raises(RuntimeManifestError) as err_smoke:
        registry.activate_manifest(
            {"expectedVersion": 1, "artifactManifestId": m_v13, "thresholdConfigSha256": shas[m_v13], "reason": "fail smoke"},
            smoke_runner=lambda _cfg: (_ for _ in ()).throw(RuntimeError("smoke boom")),
        )
    assert err_smoke.value.status_code == 503
    assert registry.get_active_snapshot().version == 1
    assert registry.get_active_snapshot().artifact_manifest_id == m_base

    # Real candidate smoke check + atomic switch (pins snapshot already held by in-flight work)
    inflight_snapshot = registry.get_active_snapshot()
    monkeypatch.delenv("AURA_FRANGI_CONFIG_PATH", raising=False)
    monkeypatch.setattr(main_module, "RUNTIME_REGISTRY", registry)

    activated = registry.activate_manifest(
        {"expectedVersion": 1, "artifactManifestId": m_v13, "thresholdConfigSha256": shas[m_v13], "reason": "promote v1.3"},
        smoke_runner=default_smoke_runner,
    )
    assert activated.version == 2
    assert activated.artifact_manifest_id == m_v13
    assert resolve_active_frangi_config().config_version == "s45-frangi-1.3.0"
    assert inflight_snapshot.config.config_version == "s3-frangi-1.0.0"

    # Rollback atomically restores base manifest at version=3
    rolled_back = registry.rollback_manifest(expected_version=2, reason="rollback verification", smoke_runner=default_smoke_runner)
    assert rolled_back.version == 3
    assert rolled_back.artifact_manifest_id == m_base
    assert resolve_active_frangi_config().config_version == "s3-frangi-1.0.0"


def test_w6_m4_02_ai_core_exposes_no_public_reload_route():
    route_paths = {getattr(route, "path", "") for route in app.routes}
    assert not any("reload" in path.lower() or "/admin/ai/config" in path.lower() for path in route_paths)


# ==============================================================================
# TC-W6-M4-03: Model Card V2 registry, negative controls & honest SCRUM-373
# ==============================================================================


def test_w6_m4_03_model_card_v2_registry_and_negative_controls(tmp_path: Path):
    assert registry_verifier.verify_registry() == []

    base_registry = json.loads((REPO_ROOT / "docs/benchmarks/model_card_v2_registry.json").read_text(encoding="utf-8"))

    # Tampered SHA-256 must fail
    tampered_hash = json.loads(json.dumps(base_registry))
    tampered_hash["models"][0]["configs"][0]["sha256"] = "0" * 64
    p1 = tmp_path / "tampered_hash.json"
    p1.write_text(json.dumps(tampered_hash), encoding="utf-8")
    assert any("SHA-256 mismatch" in err for err in registry_verifier.verify_registry(p1, REPO_ROOT))

    # Fake SCRUM-373 PASS or ICDR grading pass must fail
    fake_pass = json.loads(json.dumps(base_registry))
    fake_pass["scientificGateScrum373"]["status"] = "PASS"
    fake_pass["icdrGradingSupported"] = True
    fake_pass["notes"] = "Fake claim: ICDR grading pass"
    p2 = tmp_path / "fake_pass.json"
    p2.write_text(json.dumps(fake_pass), encoding="utf-8")
    errs = registry_verifier.verify_registry(p2, REPO_ROOT)
    assert any("SCRUM-373" in err for err in errs)
    assert any("icdrGradingSupported" in err for err in errs)
    assert any("Forbidden clinical" in err for err in errs)


def test_w6_m4_03_docs_remove_stale_mock_claim_and_include_v2_sections():
    dataset_card = (REPO_ROOT / "docs/data_cards/dataset_card.md").read_text(encoding="utf-8")
    model_card = (REPO_ROOT / "docs/data_cards/model_card_v1.3.md").read_text(encoding="utf-8")
    assert "AI endpoint vẫn mock" not in dataset_card
    assert "16. Cập nhật Dataset Registry & Protocol Đo V2" in dataset_card
    assert "7. Model Card V2 Addendum" in model_card
    assert "OPEN_NOT_PASSED" in model_card and "OPEN_NOT_PASSED" in dataset_card


def _config_headers(keypair, client_id: str = "analysis-api", scope: str = "ai:config") -> Dict[str, str]:
    now = int(time.time())
    token = sign_rs256_jwt(
        keypair,
        header={"alg": "RS256", "typ": "JWT", "kid": keypair.key_id},
        payload={
            "iss": DEFAULT_ISSUER,
            "aud": DEFAULT_AUDIENCE,
            "sub": VALID_SUB,
            "client_id": client_id,
            "scope": scope,
            "iat": now,
            "nbf": now,
            "exp": now + 300,
        },
    )
    return {"Authorization": f"Bearer {token}"}


def test_w6_m4_04_internal_config_auth_enforces_401_403_and_allows_analysis_api_scope(monkeypatch, tmp_path: Path):
    keypair = generate_deterministic_rsa_keypair(seed=20261126, bits=2048)
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", keypair.public_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)
    registry = RuntimeManifestRegistry(state_path=tmp_path / "runtime-state.json")
    monkeypatch.setattr(main_module, "RUNTIME_REGISTRY", registry)

    client = TestClient(app)
    assert client.get("/internal/v1/ai/config").status_code == 401
    # Worker scope (ai:analyze) and worker client_id must both fail 403 on internal config endpoint
    worker_headers = _config_headers(keypair, client_id=EXPECTED_CLIENT_ID, scope=DEFAULT_REQUIRED_SCOPE)
    assert client.get("/internal/v1/ai/config", headers=worker_headers).status_code == 403
    wrong_client_headers = _config_headers(keypair, client_id=EXPECTED_CLIENT_ID, scope="ai:config")
    assert client.get("/internal/v1/ai/config", headers=wrong_client_headers).status_code == 403

    admin_client_headers = _config_headers(keypair, client_id="analysis-api", scope="ai:config")
    ok_resp = client.get("/internal/v1/ai/config", headers=admin_client_headers)
    assert ok_resp.status_code == 200
    assert ok_resp.json()["version"] == 1


def test_w6_m4_05_put_changes_real_inference_even_with_env_override_and_persists_restart(monkeypatch, tmp_path: Path):
    keypair = generate_deterministic_rsa_keypair(seed=20261127, bits=2048)
    state_file = tmp_path / "runtime-state.json"
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", keypair.public_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)
    # Simulate stale env var present on container: once admin promotes v1.3, env must NOT override active state
    monkeypatch.setenv("AURA_FRANGI_CONFIG_PATH", str(AI_CORE_DIR / "config" / "ai-frangi.yaml"))

    registry = RuntimeManifestRegistry(state_path=state_file)
    monkeypatch.setattr(main_module, "RUNTIME_REGISTRY", registry)
    client = TestClient(app)

    cfg_headers = _config_headers(keypair, client_id="analysis-api", scope="ai:config")
    analyze_headers = _config_headers(keypair, client_id=EXPECTED_CLIENT_ID, scope=DEFAULT_REQUIRED_SCOPE)

    baseline_req = {
        "request_id": str(uuid.uuid4()),
        "patient_id": None,
        "modality": "FUNDUS",
        "mode": "retina_vessels",
        "image_base64": make_synthetic_fundus_png_b64(),
        "include_mask": True,
        "include_overlay": True,
    }
    base_resp = client.post("/api/v1/analyze", json=baseline_req, headers=analyze_headers)
    assert base_resp.status_code == 200
    assert base_resp.json()["config_version"] == "s3-frangi-1.0.0"

    v13_sha = "dd444ce85afdcda830cdc825ace92d34d13e476739bdd2575890be13e8305087"
    v13_id = "11111111-1111-4111-8111-111111110405"
    inflight_snapshot = registry.get_active_snapshot()

    put_resp = client.put(
        "/internal/v1/ai/config",
        json={
            "expectedVersion": 1,
            "artifactManifestId": v13_id,
            "thresholdConfigSha256": v13_sha,
            "reason": "live promote v1.3",
        },
        headers={**cfg_headers, "Idempotency-Key": "idem-promote-1"},
    )
    assert put_resp.status_code == 200
    assert put_resp.json()["version"] == 2
    assert put_resp.json()["artifactManifestId"] == v13_id

    # In-flight pinned snapshot keeps baseline config while new inference uses promoted v1.3
    assert inflight_snapshot.config.config_version == "s3-frangi-1.0.0"
    v13_req = {**baseline_req, "request_id": str(uuid.uuid4())}
    v13_resp = client.post("/api/v1/analyze", json=v13_req, headers=analyze_headers)
    assert v13_resp.status_code == 200
    assert v13_resp.json()["config_version"] == "s45-frangi-1.3.0"
    assert v13_resp.json()["risk_available"] is True

    # Lost-ACK replay with both Idempotency-Key and expectedVersion=1 (without key) returns version=2 without bumping
    replay_with_key = client.put(
        "/internal/v1/ai/config",
        json={
            "expectedVersion": 1,
            "artifactManifestId": v13_id,
            "thresholdConfigSha256": v13_sha,
            "reason": "live promote v1.3",
        },
        headers={**cfg_headers, "Idempotency-Key": "idem-promote-1"},
    )
    assert replay_with_key.status_code == 200
    assert replay_with_key.json()["version"] == 2

    replay_no_key = client.put(
        "/internal/v1/ai/config",
        json={
            "expectedVersion": 1,
            "artifactManifestId": v13_id,
            "thresholdConfigSha256": v13_sha,
            "reason": "live promote v1.3",
        },
        headers=cfg_headers,
    )
    assert replay_no_key.status_code == 200
    assert replay_no_key.json()["version"] == 2

    # Different payload on stale expectedVersion=1 fails 409
    stale_diff = client.put(
        "/internal/v1/ai/config",
        json={
            "expectedVersion": 1,
            "artifactManifestId": v13_id,
            "thresholdConfigSha256": v13_sha,
            "reason": "different command",
        },
        headers=cfg_headers,
    )
    assert stale_diff.status_code == 409

    # Recreated registry instance (process restart simulation) preserves v2 and allows rollback to v3
    restarted = RuntimeManifestRegistry(state_path=state_file)
    monkeypatch.setattr(main_module, "RUNTIME_REGISTRY", restarted)
    assert restarted.get_active_snapshot().version == 2
    assert restarted.get_active_snapshot().config.config_version == "s45-frangi-1.3.0"

    rb_resp = client.post(
        "/internal/v1/ai/config/rollback",
        json={"expectedVersion": 2, "reason": "rollback after restart"},
        headers=cfg_headers,
    )
    assert rb_resp.status_code == 200
    assert rb_resp.json()["version"] == 3
    assert resolve_active_frangi_config().config_version == "s3-frangi-1.0.0"


def test_w6_m4_06_corrupted_state_and_integrated_smoke_failure_fail_closed_503(monkeypatch, tmp_path: Path):
    keypair = generate_deterministic_rsa_keypair(seed=20261128, bits=2048)
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", keypair.public_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)

    corrupt_path = tmp_path / "corrupt-state.json"
    corrupt_path.write_text('{"schemaVersion": "1.0", "current": {"version": -1}}', encoding="utf-8")
    corrupt_reg = RuntimeManifestRegistry(state_path=corrupt_path)
    monkeypatch.setattr(main_module, "RUNTIME_REGISTRY", corrupt_reg)

    client = TestClient(app)
    assert client.get("/ready").status_code == 503
    assert client.get("/internal/v1/ai/config", headers=_config_headers(keypair)).status_code == 503

    # Integrated mode smoke verification fails closed 503 when artifacts are missing/invalid
    good_reg = RuntimeManifestRegistry(state_path=tmp_path / "good-state.json")
    monkeypatch.setattr(main_module, "RUNTIME_REGISTRY", good_reg)
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED", "true")
    monkeypatch.delenv("AURA_AVR_MODEL_PATH", raising=False)
    with pytest.raises(RuntimeManifestError) as err:
        good_reg.activate_manifest(
            {
                "expectedVersion": 1,
                "artifactManifestId": "11111111-1111-4111-8111-111111110405",
                "thresholdConfigSha256": "dd444ce85afdcda830cdc825ace92d34d13e476739bdd2575890be13e8305087",
                "reason": "missing integrated artifacts",
            },
            smoke_runner=default_smoke_runner,
        )
    assert err.value.status_code == 503
    assert good_reg.get_active_snapshot().version == 1
