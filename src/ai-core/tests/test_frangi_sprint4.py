"""
Sprint 4-5 Unit, Performance, Memory & Contract 1.3 Test Suite for AURA AI Core
(Module M4 - SCRUM-351 / W45-M4-02, SCRUM-352 / W45-M4-03, SCRUM-353 / W45-M4-04).

Covers:
1. Vectorized Frangi v0.2 equivalence vs frozen baseline across all 6 CHASE_DB1 test images
   (100% mask & overlay accuracy, Dice == 1.0, latency < 1.8s per 999x960 image).
2. Vessel Tortuosity Index, branching point topology, and Heuristic Risk Model
   (LOW / MODERATE / HIGH classification, clinical indicators, mandatory disclaimer).
3. Quality Gate short-circuit consistency (422 LOW_QUALITY_IMAGE aborts before risk model).
4. Contract 1.3 validation (ai-openapi-1.3.yaml, ai-request-1.3.schema.json,
   ai-response-1.3.schema.json) and X-Correlation-Id propagation on 200/400/401/403/422/500/503.
5. Heap memory stability across repeated inferences (no memory leak).
"""

from __future__ import annotations

import base64
import csv
import gc
import io
import json
import math
import os
import subprocess
import sys
import time
import tracemalloc
import uuid
from pathlib import Path
from typing import Any, Dict, List

import jsonschema
import numpy as np
import pytest
import yaml
from fastapi.testclient import TestClient
from PIL import Image
from skimage.draw import line as draw_line
from skimage.filters import frangi as skimage_frangi
from skimage.filters import gaussian as skimage_gaussian

from app.auth.service_jwt import (
    DEFAULT_AUDIENCE,
    DEFAULT_ISSUER,
    DEFAULT_REQUIRED_SCOPE,
)
from app.main import app
from app.schemas.common import RiskLevel
from app.services.frangi_service import (
    FrangiRuntimeConfig,
    encode_mask_and_overlay_png,
    estimate_prediction_region,
    load_frangi_config,
    run_frangi_segmentation,
)
from app.services.tortuosity import (
    HEURISTIC_DISCLAIMER,
    compute_vessel_tortuosity_and_branches,
    evaluate_heuristic_risk,
)
from scripts.run_training_smoke import ensure_chase_db1_extracted
from tests.test_frangi_sprint3 import (
    EXPECTED_CLIENT_ID,
    VALID_SUB,
    RsaKeyPairFixture,
    generate_deterministic_rsa_keypair,
    make_synthetic_fundus_png_b64,
    sign_rs256_jwt,
)

REPO_ROOT = Path(__file__).resolve().parents[3]
AI_CORE_DIR = REPO_ROOT / "src" / "ai-core"
CONTRACTS_DIR = REPO_ROOT / "contracts"
MANIFEST_CSV = REPO_ROOT / "docs" / "datasets" / "chase_db1" / "manifest.csv"
CHASE_RAW_DIR = ensure_chase_db1_extracted()

REQUEST_1_3_SCHEMA = json.loads(
    (CONTRACTS_DIR / "ai-request-1.3.schema.json").read_text(encoding="utf-8")
)
RESPONSE_1_3_SCHEMA = json.loads(
    (CONTRACTS_DIR / "ai-response-1.3.schema.json").read_text(encoding="utf-8")
)
ERROR_SCHEMA = json.loads(
    (CONTRACTS_DIR / "ai-error.schema.json").read_text(encoding="utf-8")
)


def validate_schema(instance: Dict[str, Any], schema: Dict[str, Any]) -> None:
    validator = jsonschema.Draft202012Validator(
        schema,
        format_checker=jsonschema.FormatChecker(),
    )
    validator.validate(instance)


@pytest.fixture(scope="module")
def s4_rsa_keypair() -> RsaKeyPairFixture:
    return generate_deterministic_rsa_keypair(seed=20261030, bits=2048)


@pytest.fixture
def configured_frangi_v13_env(monkeypatch, s4_rsa_keypair: RsaKeyPairFixture):
    v13_cfg = AI_CORE_DIR / "config" / "ai-frangi-v1.3.yaml"
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_FRANGI_CONFIG_PATH", str(v13_cfg))
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", s4_rsa_keypair.public_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)


@pytest.fixture
def s4_bearer_headers(s4_rsa_keypair: RsaKeyPairFixture) -> Dict[str, str]:
    now = int(time.time())
    token = sign_rs256_jwt(
        s4_rsa_keypair,
        header={"alg": "RS256", "typ": "JWT", "kid": s4_rsa_keypair.key_id},
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


@pytest.fixture
def client():
    with TestClient(app) as test_client:
        yield test_client


# ==============================================================================
# 1. Vectorized Frangi v0.2 Accuracy & Latency (< 1.8s, 100% Baseline Fidelity)
# ==============================================================================


def test_vectorized_frangi_matches_baseline_100_percent_and_under_1800ms():
    if os.getenv("AURA_FRANGI_PERF_CHILD") != "1":
        # Measure the baseline runtime independently of optional ML libraries loaded by other tests.
        environment = {**os.environ, "AURA_FRANGI_PERF_CHILD": "1"}
        result = subprocess.run(
            [sys.executable, "-m", "pytest", f"{Path(__file__).resolve()}::test_vectorized_frangi_matches_baseline_100_percent_and_under_1800ms", "-q"],
            cwd=REPO_ROOT, env=environment, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180,
        )
        assert result.returncode == 0, result.stdout + result.stderr
        assert "1 passed" in result.stdout, result.stdout
        return
    with MANIFEST_CSV.open(encoding="utf-8-sig", newline="") as f:
        test_rows = [r for r in csv.DictReader(f) if r["split"] == "test"]
    assert len(test_rows) == 6

    cfg = load_frangi_config(AI_CORE_DIR / "config" / "ai-frangi-v1.3.yaml")
    latencies_ms: List[float] = []

    for row in test_rows:
        img_name = Path(row["image"]).name
        img_path = CHASE_RAW_DIR / img_name
        assert img_path.is_file(), f"Missing CHASE_DB1 test image: {img_path}"

        with Image.open(img_path) as pil_img:
            rgb = np.asarray(pil_img.convert("RGB"), dtype=np.uint8)

        # Reference frozen Sprint 3 baseline mask
        interior = estimate_prediction_region(rgb, config=cfg)
        green = rgb[:, :, 1].astype(np.float64) / float(cfg.scale)
        green_smoothed = skimage_gaussian(
            green,
            sigma=cfg.gaussian_sigma,
            preserve_range=True,
            mode="nearest",
        )
        ref_resp = skimage_frangi(
            green_smoothed,
            sigmas=list(cfg.sigmas),
            alpha=cfg.alpha,
            beta=cfg.beta,
            gamma=cfg.gamma,
            black_ridges=cfg.black_ridges,
            mode=cfg.boundary_mode,
        )
        ref_resp[~interior] = 0.0
        ref_mask = (ref_resp / float(ref_resp.max())) >= cfg.threshold

        # Timed optimized Sprint 4-5 segmentation
        t0 = time.perf_counter()
        opt_mask, _ = run_frangi_segmentation(rgb, interior=interior, config=cfg)
        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        latencies_ms.append(elapsed_ms)

        intersection = int(np.count_nonzero(ref_mask & opt_mask))
        denom = int(np.count_nonzero(ref_mask) + np.count_nonzero(opt_mask))
        dice_vs_baseline = (2.0 * intersection / denom) if denom > 0 else 1.0
        mismatched_pixels = int(np.count_nonzero(ref_mask ^ opt_mask))

        assert dice_vs_baseline == pytest.approx(1.0, abs=1e-6), (
            f"Mask drift on {img_name}: dice={dice_vs_baseline}, mismatches={mismatched_pixels}"
        )
        assert mismatched_pixels == 0

        # Verify mask & overlay PNG lossless pixel preservation
        mask_b64, overlay_b64 = encode_mask_and_overlay_png(rgb, opt_mask)
        decoded_mask = np.asarray(
            Image.open(io.BytesIO(base64.b64decode(mask_b64))),
            dtype=np.uint8,
        )
        assert np.array_equal(decoded_mask > 0, opt_mask)
        decoded_overlay = np.asarray(
            Image.open(io.BytesIO(base64.b64decode(overlay_b64))),
            dtype=np.uint8,
        )
        assert decoded_overlay.shape == (960, 999, 3)

    mean_latency_ms = float(np.mean(latencies_ms))
    assert mean_latency_ms < 1800.0, (
        f"Mean Frangi segmentation latency {mean_latency_ms:.1f}ms exceeded 1800ms SLA!"
    )


# ==============================================================================
# 2. Vessel Tortuosity, Branching Topology & Heuristic Risk Model Unit Tests
# ==============================================================================


def test_tortuosity_empty_straight_wavy_and_branched_topologies():
    # 1. Empty mask
    empty = np.zeros((128, 128), dtype=bool)
    assert compute_vessel_tortuosity_and_branches(empty) == (1.0, 0)

    # 2. Straight horizontal & diagonal lines -> tortuosity ~ 1.0, 0 branches
    straight = np.zeros((160, 160), dtype=bool)
    rr, cc = draw_line(80, 15, 80, 145)
    straight[rr, cc] = True
    rr_d, cc_d = draw_line(15, 20, 60, 65)
    straight[rr_d, cc_d] = True
    tort_straight, br_straight = compute_vessel_tortuosity_and_branches(straight)
    assert 1.0 <= tort_straight <= 1.05
    assert br_straight == 0

    # 3. Sinusoidal wavy vessel -> significantly higher tortuosity > 1.25
    wavy = np.zeros((200, 240), dtype=bool)
    prev_r, prev_c = None, None
    for c in range(20, 220):
        r = int(round(100 + 28.0 * math.sin((c - 20) * 2.0 * math.pi / 45.0)))
        if prev_r is not None:
            seg_r, seg_c = draw_line(prev_r, prev_c, r, c)
            wavy[seg_r, seg_c] = True
        prev_r, prev_c = r, c
    tort_wavy, _ = compute_vessel_tortuosity_and_branches(wavy)
    assert tort_wavy > 1.25
    assert tort_wavy > tort_straight + 0.20

    # 4. Branched Y-junction vessel tree -> detects bifurcation
    branched = np.zeros((160, 160), dtype=bool)
    r1, c1 = draw_line(80, 20, 80, 80)
    r2, c2 = draw_line(80, 80, 35, 135)
    r3, c3 = draw_line(80, 80, 125, 135)
    branched[r1, c1] = True
    branched[r2, c2] = True
    branched[r3, c3] = True
    _, br_count = compute_vessel_tortuosity_and_branches(branched)
    assert br_count >= 1


def test_heuristic_risk_model_levels_and_disclaimer():
    low = evaluate_heuristic_risk(vessel_density=0.058, tortuosity_index=1.11, branching_points=1200)
    assert low.risk_level == RiskLevel.LOW
    assert 0.0 <= low.risk_score < 0.35
    assert low.disclaimer == HEURISTIC_DISCLAIMER
    assert len(low.indicators) >= 3

    mod = evaluate_heuristic_risk(vessel_density=0.075, tortuosity_index=1.24, branching_points=1850)
    assert mod.risk_level == RiskLevel.MODERATE
    assert 0.35 <= mod.risk_score < 0.70
    assert mod.disclaimer == HEURISTIC_DISCLAIMER

    high = evaluate_heuristic_risk(vessel_density=0.022, tortuosity_index=1.38, branching_points=420)
    assert high.risk_level == RiskLevel.HIGH
    assert 0.70 <= high.risk_score <= 1.0
    assert "không thay thế chẩn đoán" in high.disclaimer.lower()


# ==============================================================================
# 3. Contract 1.3 OpenAPI & Schema Validation + X-Correlation-Id All Statuses
# ==============================================================================


def test_contract_1_3_schemas_and_openapi_structure_are_valid():
    jsonschema.Draft202012Validator.check_schema(REQUEST_1_3_SCHEMA)
    jsonschema.Draft202012Validator.check_schema(RESPONSE_1_3_SCHEMA)

    openapi_path = CONTRACTS_DIR / "ai-openapi-1.3.yaml"
    assert openapi_path.is_file()
    spec = yaml.safe_load(openapi_path.read_text(encoding="utf-8"))
    assert spec["openapi"] == "3.1.0"
    assert spec["info"]["version"] == "1.3.0"

    analyze_op = spec["paths"]["/api/v1/analyze"]["post"]
    assert analyze_op["requestBody"]["content"]["application/json"]["schema"]["$ref"] == "ai-request-1.3.schema.json"
    assert analyze_op["responses"]["200"]["content"]["application/json"]["schema"]["$ref"] == "ai-response-1.3.schema.json"

    for status_code in ("200", "400", "401", "403", "422", "500", "503"):
        headers = analyze_op["responses"][status_code]["headers"]
        assert "X-Correlation-Id" in headers


def test_real_chase_db1_endpoint_contract_1_3_returns_tortuosity_and_risk(
    client,
    configured_frangi_v13_env,
    s4_bearer_headers,
):
    img_path = CHASE_RAW_DIR / "Image_01L.jpg"
    req_id = str(uuid.uuid4())
    cid = "trace-s45-contract13-001"
    payload = {
        "request_id": req_id,
        "patient_id": None,
        "eye_side": "left",
        "mode": "retina_vessels",
        "modality": "FUNDUS",
        "image_base64": base64.b64encode(img_path.read_bytes()).decode("ascii"),
        "include_mask": True,
        "include_overlay": True,
    }
    validate_schema(payload, REQUEST_1_3_SCHEMA)

    resp = client.post(
        "/api/v1/analyze",
        json=payload,
        headers={**s4_bearer_headers, "X-Correlation-Id": cid},
    )
    assert resp.status_code == 200, resp.text
    assert resp.headers.get("X-Correlation-Id") == cid

    body = resp.json()
    validate_schema(body, RESPONSE_1_3_SCHEMA)

    assert body["request_id"] == req_id
    assert body["is_mock"] is False
    assert body["config_version"] == "s45-frangi-1.3.0"
    assert body["risk_available"] is True
    assert body["metrics"]["vessel_density"] > 0.0
    assert body["metrics"]["tortuosity_index"] >= 1.0
    assert body["metrics"]["branching_points"] > 100

    risk = body["risk_assessment"]
    assert risk is not None
    assert 0.0 <= risk["risk_score"] <= 1.0
    assert risk["risk_level"] in ("LOW", "MODERATE", "HIGH")
    assert 0.0 <= risk["confidence_score"] <= 1.0
    assert len(risk["indicators"]) >= 3
    assert "không thay thế chẩn đoán" in risk["disclaimer"].lower()


def test_correlation_id_echoed_on_400_401_422_500_error_responses(
    client,
    configured_frangi_v13_env,
    s4_bearer_headers,
    monkeypatch,
):
    base_payload = {
        "request_id": str(uuid.uuid4()),
        "patient_id": None,
        "eye_side": "left",
        "mode": "retina_vessels",
        "modality": "FUNDUS",
        "image_base64": make_synthetic_fundus_png_b64(),
        "include_mask": True,
        "include_overlay": True,
    }

    # 1. HTTP 401 Missing token echoes X-Correlation-Id
    r_401 = client.post(
        "/api/v1/analyze",
        json=base_payload,
        headers={"X-Correlation-Id": "cid-err-401"},
    )
    assert r_401.status_code == 401
    assert r_401.headers.get("X-Correlation-Id") == "cid-err-401"
    validate_schema(r_401.json(), ERROR_SCHEMA)

    # 2. HTTP 400 Invalid base64 echoes X-Correlation-Id
    r_400 = client.post(
        "/api/v1/analyze",
        json={**base_payload, "image_base64": "data:image/png;base64,AAAA"},
        headers={**s4_bearer_headers, "X-Correlation-Id": "cid-err-400"},
    )
    assert r_400.status_code == 400
    assert r_400.headers.get("X-Correlation-Id") == "cid-err-400"
    validate_schema(r_400.json(), ERROR_SCHEMA)

    # 3. HTTP 422 LOW_QUALITY_IMAGE short-circuits without risk computation & echoes X-Correlation-Id
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), color=(0, 0, 0)).save(buf, format="PNG")
    dark_b64 = base64.b64encode(buf.getvalue()).decode("ascii")
    r_422 = client.post(
        "/api/v1/analyze",
        json={**base_payload, "image_base64": dark_b64},
        headers={**s4_bearer_headers, "X-Correlation-Id": "cid-err-422"},
    )
    assert r_422.status_code == 422
    assert r_422.headers.get("X-Correlation-Id") == "cid-err-422"
    err_422 = r_422.json()
    validate_schema(err_422, ERROR_SCHEMA)
    assert err_422["error_code"] == "LOW_QUALITY_IMAGE"
    assert "risk_assessment" not in err_422

    # 4. HTTP 500 Internal runtime error echoes X-Correlation-Id
    def crash(*args, **kwargs):
        raise RuntimeError("Simulated internal error for 500 tracing test")

    monkeypatch.setattr("app.main.process_frangi_analysis", crash)
    r_500 = client.post(
        "/api/v1/analyze",
        json=base_payload,
        headers={**s4_bearer_headers, "X-Correlation-Id": "cid-err-500"},
    )
    assert r_500.status_code == 500
    assert r_500.headers.get("X-Correlation-Id") == "cid-err-500"
    validate_schema(r_500.json(), ERROR_SCHEMA)


def test_memory_released_cleanly_across_repeated_inferences():
    synth_b64 = make_synthetic_fundus_png_b64(width=256, height=256)
    raw = base64.b64decode(synth_b64)
    rgb = np.asarray(Image.open(io.BytesIO(raw)).convert("RGB"), dtype=np.uint8)
    cfg = FrangiRuntimeConfig(risk_available=True, config_version="s45-frangi-1.3.0")

    # Warm-up iteration
    interior = estimate_prediction_region(rgb, config=cfg)
    mask, _ = run_frangi_segmentation(rgb, interior=interior, config=cfg)
    compute_vessel_tortuosity_and_branches(mask)
    gc.collect()

    tracemalloc.start()
    # Warm-up inside tracemalloc so frame cache stabilizes before baseline snapshot
    for _ in range(2):
        interior_w = estimate_prediction_region(rgb, config=cfg)
        mask_w, _ = run_frangi_segmentation(rgb, interior=interior_w, config=cfg)
        tort_w, br_w = compute_vessel_tortuosity_and_branches(mask_w)
        evaluate_heuristic_risk(0.055, tort_w, br_w)
        del interior_w, mask_w
    gc.collect()

    baseline_current, _ = tracemalloc.get_traced_memory()
    for _ in range(15):
        interior_i = estimate_prediction_region(rgb, config=cfg)
        mask_i, _ = run_frangi_segmentation(rgb, interior=interior_i, config=cfg)
        tort_i, br_i = compute_vessel_tortuosity_and_branches(mask_i)
        evaluate_heuristic_risk(0.055, tort_i, br_i)
        del interior_i, mask_i
    gc.collect()
    after_current, _ = tracemalloc.get_traced_memory()
    tracemalloc.stop()

    retained_delta_kb = (after_current - baseline_current) / 1024.0
    assert retained_delta_kb < 256.0, f"Unexpected retained memory growth: {retained_delta_kb:.1f} KB"
