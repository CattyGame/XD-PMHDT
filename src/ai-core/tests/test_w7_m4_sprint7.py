"""
Sprint 7 Verification Suite for Module M4 (W7-M4-01 / W7-M4-02 / W7-M4-03).

Covers:
1. TC-W7-M4-01 (SCRUM-405): Reproducible V2 evaluation protocol, separated cohort
   reporting (CHASE_DB1, HRF45, AVRDB100, Iowa40), coverage denominator invariant,
   RGB-only signature audit, honest NOT_TESTED handling for missing datasets, and
   independent scientific gate SCRUM-373 (OPEN_NOT_PASSED, autoCloseAllowed=false).
2. TC-W7-M4-02 (SCRUM-406): Latency/RAM/compose budget verification, SRS 20s SLA
   vs optional stretch separation, Golden / Precision Parity Gate (Dice=1.0,
   0 mismatched pixels), and bounded concurrency backpressure (429 + Retry-After).
3. TC-W7-M4-03 (SCRUM-407): Hard-image regression & fail-closed matrix (positive
   control first, then dark/underexposed, overexposed/glare, blurred/low-contrast,
   wrong modality OCT/XRAY, corrupted/truncated bytes, WebP/data-URL rejection,
   AVR abstention null+reason, tampered artifact fail-closed 503/409, and research
   limitations handover document verification).
"""

from __future__ import annotations

import base64
import copy
import hashlib
import io
import json
import sys
import time
import uuid
from pathlib import Path
from typing import Any, Dict

import numpy as np
import pytest
from fastapi.testclient import TestClient
from PIL import Image, ImageFilter

from app import main as main_module
from app.auth.service_jwt import DEFAULT_AUDIENCE, DEFAULT_ISSUER, DEFAULT_REQUIRED_SCOPE
from app.main import app, resolve_active_frangi_config
from app.services import integrated_inference
from app.services.avr_service import measure_avr_prediction
from app.services.runtime_registry import RuntimeManifestError, RuntimeManifestRegistry
from scripts import benchmark_w7_latency_ram as perf_bench
from scripts import evaluate_v2_protocol as v2_eval
from tests.test_frangi_sprint3 import (
    EXPECTED_CLIENT_ID,
    VALID_SUB,
    generate_deterministic_rsa_keypair,
    make_synthetic_fundus_png_b64,
    sign_rs256_jwt,
)

REPO_ROOT = Path(__file__).resolve().parents[3]
V2_REPORT_PATH = REPO_ROOT / "docs" / "benchmarks" / "v2_evaluation_report_s7.json"
PERF_PROFILE_PATH = REPO_ROOT / "docs" / "benchmarks" / "w7_latency_ram_profile_v1.0.json"
HANDOVER_DOC_PATH = REPO_ROOT / "docs" / "ai" / "w7_m4_research_limitations_handover.md"


def _auth_headers(monkeypatch) -> Dict[str, str]:
    keypair = generate_deterministic_rsa_keypair(seed=20261121, bits=2048)
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED", "false")
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


def _encode_image(img: Image.Image, fmt: str = "PNG") -> str:
    buf = io.BytesIO()
    img.save(buf, format=fmt)
    return base64.b64encode(buf.getvalue()).decode("ascii")


def _make_blurred_low_contrast_fundus_b64() -> str:
    raw = base64.b64decode(make_synthetic_fundus_png_b64(width=256, height=256))
    img = Image.open(io.BytesIO(raw)).convert("RGB")
    blurred = img.filter(ImageFilter.GaussianBlur(radius=18))
    arr = np.asarray(blurred, dtype=np.float32)
    # Collapse contrast while keeping mid luminance so sharpness/contrast fail
    flat = np.clip(110.0 + (arr - arr.mean()) * 0.08, 0, 255).astype(np.uint8)
    return _encode_image(Image.fromarray(flat, mode="RGB"), "PNG")


# ==============================================================================
# TC-W7-M4-01 (SCRUM-405): V2 evaluation protocol & honest SCRUM-373 gate
# ==============================================================================


def test_w7_m4_01_v2_evaluation_report_passes_verifier_and_keeps_scrum373_open():
    assert V2_REPORT_PATH.is_file(), f"Expected committed report at {V2_REPORT_PATH}"
    errors = v2_eval.verify_report(V2_REPORT_PATH, root=REPO_ROOT)
    assert errors == [], f"Unexpected V2 evaluation verification errors: {errors}"

    report = json.loads(V2_REPORT_PATH.read_text(encoding="utf-8"))
    assert report["clinicalValidation"] is False
    assert report["icdrGradingSupported"] is False
    gate = report["scientificGateScrum373"]
    assert gate["status"] == "OPEN_NOT_PASSED"
    assert gate["autoCloseAllowed"] is False
    assert gate["iowaGatePassed"] is False
    assert gate["thresholdAbsBias"] == 0.02

    # RGB-only signature audit must pass live in CI as well
    live_audit = v2_eval.audit_rgb_only_inference()
    assert live_audit["rgbOnlyInferenceVerified"] is True
    assert live_audit["violations"] == []


def test_w7_m4_01_negative_controls_reject_closed_gate_or_hidden_abstentions(tmp_path: Path):
    base = json.loads(V2_REPORT_PATH.read_text(encoding="utf-8"))

    # 1. Attempting to auto-close SCRUM-373 must fail verification
    tampered_gate = copy.deepcopy(base)
    tampered_gate["scientificGateScrum373"]["status"] = "PASSED"
    tampered_gate["scientificGateScrum373"]["autoCloseAllowed"] = True
    p1 = tmp_path / "closed_gate.json"
    p1.write_text(json.dumps(tampered_gate), encoding="utf-8")
    assert v2_eval.verify_report(p1, root=REPO_ROOT)

    # 2. Hiding unavailable cases from coverage denominator must fail verification
    tampered_cov = copy.deepcopy(base)
    tampered_cov["cohorts"]["AVRDB100"]["unavailable"] = 0
    tampered_cov["cohorts"]["AVRDB100"]["coverage"] = 1.0
    p2 = tmp_path / "hidden_abstention.json"
    p2.write_text(json.dumps(tampered_cov), encoding="utf-8")
    assert v2_eval.verify_report(p2, root=REPO_ROOT)

    # 3. Claiming Iowa40 PASS without external dataset must fail verification
    tampered_iowa = copy.deepcopy(base)
    tampered_iowa["cohorts"]["Iowa40"]["status"] = "PASS"
    p3 = tmp_path / "fake_iowa.json"
    p3.write_text(json.dumps(tampered_iowa), encoding="utf-8")
    assert v2_eval.verify_report(p3, root=REPO_ROOT)


def test_w7_m4_01_missing_dataset_reports_not_tested_not_pass(tmp_path: Path):
    # Point to empty fake repo root containing manifests/configs only -> datasets absent
    for rel in (
        "docs/datasets/chase_db1/manifest.csv",
        "docs/datasets/hrf/manifest.csv",
        "src/ai-core/config/ai-frangi.yaml",
    ):
        dst = tmp_path / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        dst.write_bytes((REPO_ROOT / rel).read_bytes())

    chase_missing = v2_eval.evaluate_chase_db1(root=tmp_path)
    assert chase_missing["status"] == "NOT_TESTED_MISSING_DATASET"
    assert chase_missing["measured"] == 0
    assert chase_missing["coverage"] == 0.0

    hrf_missing = v2_eval.evaluate_hrf_dr(root=tmp_path, checkpoint_path=tmp_path / "missing.pt")
    assert hrf_missing["status"] == "NOT_TESTED_MISSING_CHECKPOINT"
    assert hrf_missing["measured"] == 0


# ==============================================================================
# TC-W7-M4-02 (SCRUM-406): Latency/RAM profile, compose budget & Golden Parity
# ==============================================================================


def test_w7_m4_02_latency_ram_profile_and_compose_budget_verified():
    assert PERF_PROFILE_PATH.is_file(), f"Expected committed latency profile at {PERF_PROFILE_PATH}"
    errors = perf_bench.verify_profile(PERF_PROFILE_PATH, root=REPO_ROOT)
    assert errors == [], f"Unexpected latency/RAM profile errors: {errors}"

    profile = json.loads(PERF_PROFILE_PATH.read_text(encoding="utf-8"))
    assert profile["composeBudget"]["singleWorkerPinned"] is True
    assert profile["composeBudget"]["memLimitMib"] == 2048
    assert profile["frangiApiProfile"]["srsHardSla"]["maxP95Ms"] == 20000.0
    assert profile["frangiApiProfile"]["srsHardSla"]["passed"] is True
    assert profile["frangiApiProfile"]["stretchTarget"]["doesNotReplaceSrs20s"] is True
    assert profile["goldenParityGate"]["passed"] is True
    assert profile["goldenParityGate"]["mismatchedPixels"] == 0


def test_w7_m4_02_live_golden_parity_gate_matches_skimage_exact():
    raw = base64.b64decode(make_synthetic_fundus_png_b64(width=192, height=192))
    rgb = np.asarray(Image.open(io.BytesIO(raw)).convert("RGB"), dtype=np.uint8)
    parity = perf_bench.run_golden_parity_gate(rgb, root=REPO_ROOT)
    assert parity["passed"] is True
    assert parity["dice"] == pytest.approx(1.0)
    assert parity["mismatchedPixels"] == 0
    assert parity["maxAbsVesselDensityDiff"] == 0.0


def test_w7_m4_02_concurrency_backpressure_returns_429_with_retry_after(tmp_path: Path, monkeypatch):
    headers = _auth_headers(monkeypatch)
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED", "true")
    f_avr = tmp_path / "avr.bin"
    f_dr = tmp_path / "dr.pt"
    f_avr.write_bytes(b"avr")
    f_dr.write_bytes(b"dr")
    monkeypatch.setenv("AURA_AVR_MODEL_PATH", str(f_avr))
    monkeypatch.setenv("AURA_AVR_MODEL_SHA256", hashlib.sha256(b"avr").hexdigest())
    monkeypatch.setenv("AURA_DR_CNN_CHECKPOINT", str(f_dr))
    monkeypatch.setenv("AURA_DR_CNN_SHA256", hashlib.sha256(b"dr").hexdigest())

    assert main_module.CNN_SLOT.acquire(blocking=False) is True
    try:
        with TestClient(app) as client:
            resp = client.post(
                "/api/v1/analyze",
                headers=headers,
                json={"request_id": str(uuid.uuid4()), "image_base64": make_synthetic_fundus_png_b64()},
            )
            assert resp.status_code == 429
            assert resp.json()["error_code"] == "RESEARCH_MODEL_BUSY"
            assert resp.headers.get("Retry-After") == "1"
    finally:
        main_module.CNN_SLOT.release()


# ==============================================================================
# TC-W7-M4-03 (SCRUM-407): Hard-image regression matrix & handover doc
# ==============================================================================


def test_w7_m4_03_hard_image_matrix_positive_first_then_fail_closed(monkeypatch):
    headers = _auth_headers(monkeypatch)
    with TestClient(app) as client:
        # 0. Positive control FIRST — proves pipeline accepts a valid fundus image
        ok_resp = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "image_base64": make_synthetic_fundus_png_b64()},
        )
        assert ok_resp.status_code == 200
        ok_body = ok_resp.json()
        assert ok_body["is_mock"] is False
        assert ok_body["risk_available"] is False
        assert ok_body["risk_assessment"] is None
        assert ok_body["status"] == "WARNING"
        assert any(w["code"] == "RISK_UNAVAILABLE" for w in ok_body["warnings"])

        # 1. Pitch-black / severely underexposed -> 422 LOW_QUALITY_IMAGE
        dark_b64 = _encode_image(Image.new("RGB", (256, 256), color=(0, 0, 0)), "PNG")
        r_dark = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "image_base64": dark_b64},
        )
        assert r_dark.status_code == 422
        assert r_dark.json()["error_code"] == "LOW_QUALITY_IMAGE"

        # 2. Overexposed / washed-out glare -> 422 LOW_QUALITY_IMAGE
        glare_b64 = _encode_image(Image.new("RGB", (256, 256), color=(254, 254, 254)), "PNG")
        r_glare = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "image_base64": glare_b64},
        )
        assert r_glare.status_code == 422
        assert r_glare.json()["error_code"] == "LOW_QUALITY_IMAGE"

        # 3. Blurred / low-contrast out-of-focus fundus -> 422 LOW_QUALITY_IMAGE
        r_blur = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "image_base64": _make_blurred_low_contrast_fundus_b64()},
        )
        assert r_blur.status_code == 422
        blur_body = r_blur.json()
        assert blur_body["error_code"] == "LOW_QUALITY_IMAGE"
        assert len(blur_body["details"]["failed_checks"]) >= 1

        # 4. Wrong modalities: OCT -> 422 UNSUPPORTED_MODALITY; XRAY -> 422 INVALID_MODALITY
        r_oct = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "modality": "OCT", "image_base64": make_synthetic_fundus_png_b64()},
        )
        assert r_oct.status_code == 422
        assert r_oct.json()["error_code"] == "UNSUPPORTED_MODALITY"

        r_xray = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "modality": "XRAY", "image_base64": make_synthetic_fundus_png_b64()},
        )
        assert r_xray.status_code == 422
        assert r_xray.json()["error_code"] == "INVALID_MODALITY"

        # 5. Corrupted / truncated bytes & unsupported format (data URL, WebP) -> 400 INVALID_IMAGE_PAYLOAD
        bad_payloads = [
            "not-valid-base64!!!",
            base64.b64encode(b"\x89PNG\r\n\x1a\ntruncated").decode("ascii"),
            "data:image/png;base64," + make_synthetic_fundus_png_b64(),
            _encode_image(Image.new("RGB", (128, 128), color=(90, 60, 40)), "WEBP"),
        ]
        for bad in bad_payloads:
            r_bad = client.post(
                "/api/v1/analyze",
                headers=headers,
                json={"request_id": str(uuid.uuid4()), "image_base64": bad},
            )
            assert r_bad.status_code == 400
            assert r_bad.json()["error_code"] == "INVALID_IMAGE_PAYLOAD"


def test_w7_m4_03_avr_abstention_returns_null_and_reason_never_zero(monkeypatch):
    # Ensure test works both with real OpenCV (full env) and without cv2 (CI baseline env).
    try:
        import cv2  # noqa: F401
    except ImportError:
        fake_geom = type(
            "FakeAvrGeometry",
            (),
            {"trace_root_calibers": staticmethod(lambda rgb, mask, prob, od: {"arteries": [], "veins": []})},
        )
        monkeypatch.setitem(sys.modules, "app.services.avr_geometry", fake_geom)

    rgb = np.zeros((160, 160, 3), dtype=np.uint8)
    labels = np.zeros((160, 160), dtype=np.uint8)
    prob = np.zeros((160, 160), dtype=np.float32)
    fake_model = {"model_type": "RRWNET_RITE", "sha256": "a" * 64, "dataset_id": "AVRDB"}

    # Missing optic disc -> OPTIC_DISC_NOT_LOCALIZED
    no_od = measure_avr_prediction(rgb, fake_model, None, labels, prob, generate_visualization=False)
    assert no_od["measurement_available"] is False
    assert no_od["avr"] is None
    assert no_od["crae_px"] is None
    assert no_od["crve_px"] is None
    assert no_od["risk_available"] is False
    assert no_od["unavailable_reason"] == "OPTIC_DISC_NOT_LOCALIZED"

    # Optic disc near image border -> INCOMPLETE_ZONE_B
    border_od = {"center_x": 15, "center_y": 15, "radius_px": 20}
    inc_zone = measure_avr_prediction(rgb, fake_model, border_od, labels, prob, generate_visualization=False)
    assert inc_zone["measurement_available"] is False
    assert inc_zone["avr"] is None
    assert inc_zone["unavailable_reason"] == "INCOMPLETE_ZONE_B"

    # Centered optic disc but no vessels -> INSUFFICIENT_SIX_VESSEL_SETS
    center_od = {"center_x": 80, "center_y": 80, "radius_px": 15}
    no_vessels = measure_avr_prediction(rgb, fake_model, center_od, labels, prob, generate_visualization=False)
    assert no_vessels["measurement_available"] is False
    assert no_vessels["avr"] is None
    assert no_vessels["unavailable_reason"] == "INSUFFICIENT_SIX_VESSEL_SETS"


def test_w7_m4_03_corrupted_or_mismatched_artifact_fails_closed(tmp_path: Path, monkeypatch):
    headers = _auth_headers(monkeypatch)
    # 1. Tampered integrated research checkpoint -> 503 RESEARCH_MODEL_UNAVAILABLE
    avr_file = tmp_path / "avr.bin"
    dr_file = tmp_path / "dr.pt"
    avr_file.write_bytes(b"avr-bytes")
    dr_file.write_bytes(b"dr-bytes-tampered")
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED", "true")
    monkeypatch.setenv("AURA_AVR_MODEL_PATH", str(avr_file))
    monkeypatch.setenv("AURA_AVR_MODEL_SHA256", hashlib.sha256(b"avr-bytes").hexdigest())
    monkeypatch.setenv("AURA_DR_CNN_CHECKPOINT", str(dr_file))
    monkeypatch.setenv("AURA_DR_CNN_SHA256", "0" * 64)  # mismatch

    with TestClient(app) as client:
        resp = client.post(
            "/api/v1/analyze",
            headers=headers,
            json={"request_id": str(uuid.uuid4()), "image_base64": make_synthetic_fundus_png_b64()},
        )
        assert resp.status_code == 503
        assert resp.json()["error_code"] == "RESEARCH_MODEL_UNAVAILABLE"

    # 2. Tampered Frangi config manifest hash -> RuntimeManifestError HTTP 409
    cfg_dir = tmp_path / "config"
    cfg_dir.mkdir()
    src_cfg = REPO_ROOT / "src" / "ai-core" / "config" / "ai-frangi.yaml"
    cfg_bytes = src_cfg.read_bytes()
    (cfg_dir / "ai-frangi.yaml").write_bytes(cfg_bytes)
    valid_sha = hashlib.sha256(cfg_bytes.replace(b"\r\n", b"\n")).hexdigest()
    default_uuid = str(uuid.uuid4())
    bad_uuid = str(uuid.uuid4())
    manifest_file = cfg_dir / "artifact-manifests.json"
    manifest_file.write_text(
        json.dumps(
            {
                "defaultManifestId": default_uuid,
                "manifests": [
                    {
                        "artifactManifestId": default_uuid,
                        "modelVersion": "frangi-v0.2",
                        "configVersion": "s3-frangi-1.0.0",
                        "thresholdVersion": "frangi-threshold-0.02-v1",
                        "riskAvailable": False,
                        "configFile": "ai-frangi.yaml",
                        "thresholdConfigSha256": valid_sha,
                    },
                    {
                        "artifactManifestId": bad_uuid,
                        "modelVersion": "frangi-v0.2",
                        "configVersion": "s3-frangi-1.0.0",
                        "thresholdVersion": "frangi-threshold-0.02-v1",
                        "riskAvailable": False,
                        "configFile": "ai-frangi.yaml",
                        "thresholdConfigSha256": "f" * 64,
                    },
                ],
            }
        ),
        encoding="utf-8",
    )
    registry = RuntimeManifestRegistry(manifest_path=manifest_file, config_dir=cfg_dir)
    with pytest.raises(RuntimeManifestError) as exc_info:
        registry.activate_manifest(
            {
                "artifactManifestId": bad_uuid,
                "thresholdConfigSha256": "f" * 64,
                "expectedVersion": 1,
                "reason": "Sprint 7 hash tamper regression check",
            }
        )
    assert exc_info.value.status_code == 409
    assert "HASH" in exc_info.value.error_code


def test_w7_m4_03_research_limitations_handover_doc_contains_required_disclosures():
    assert HANDOVER_DOC_PATH.is_file(), f"Missing handover document: {HANDOVER_DOC_PATH}"
    text = HANDOVER_DOC_PATH.read_text(encoding="utf-8")
    for required in (
        "IMAGENET_CENTER_CROP_224",
        "Resize(shorter_edge=256)",
        "CenterCrop(224x224)",
        "SCRUM-373",
        "OPEN_NOT_PASSED",
        "LOW_QUALITY_IMAGE",
        "AVR_UNAVAILABLE",
        "notIcdrGrading",
        "clinicalValidation",
    ):
        assert required in text, f"Handover doc missing required disclosure: {required}"
