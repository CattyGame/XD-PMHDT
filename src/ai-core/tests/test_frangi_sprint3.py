"""
Comprehensive Sprint 3 Test Suite for AURA AI Core (M4 - SCRUM-329, SCRUM-330, SCRUM-331).

Covers:
1. RS256 Service JWT fail-closed verification (401, 403, 503) and /ready readiness probe.
2. Pre-Frangi quality assessment formulas and exact threshold boundary acceptance/rejection (422 LOW_QUALITY_IMAGE).
3. Real Fundus vessel segmentation (Frangi v0.2) on >= 3 CHASE_DB1 images via POST /api/v1/analyze.
4. Direct JSON Schema 2020-12 validation against canonical contracts:
   - contracts/ai-request-1.2.schema.json
   - contracts/ai-response-1.2.schema.json
   - contracts/ai-error.schema.json
5. Input payload guards (raw Base64 only, PNG/JPEG only, 10 MiB / 16 MP limits, 500 runtime error handling).
6. Supervised segmentation smoke-training pipeline isolation (train/validation only, seed=42, test untouched).
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import random
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict

import jsonschema
import numpy as np
import pytest
import yaml
from fastapi.testclient import TestClient
from PIL import Image, ImageDraw

from app.auth.service_jwt import (
    DEFAULT_AUDIENCE,
    DEFAULT_ISSUER,
    DEFAULT_REQUIRED_SCOPE,
    RSA_ENCRYPTION_OID_DER,
    SHA256_DIGEST_INFO_PREFIX,
    ServiceAuthConfigError,
    ServiceJwtVerifier,
    parse_spki_rsa_public_key,
)
from app.main import app
from app.services.frangi_service import (
    FrangiRuntimeConfig,
    evaluate_frangi_quality,
    run_frangi_segmentation,
)
from scripts.run_training_smoke import ensure_chase_db1_extracted, run_smoke_training

REPO_ROOT = Path(__file__).resolve().parents[3]
CONTRACTS_DIR = REPO_ROOT / "contracts"
CHASE_RAW_DIR = ensure_chase_db1_extracted()

REQUEST_SCHEMA = json.loads((CONTRACTS_DIR / "ai-request-1.2.schema.json").read_text(encoding="utf-8"))
RESPONSE_SCHEMA = json.loads((CONTRACTS_DIR / "ai-response-1.2.schema.json").read_text(encoding="utf-8"))
ERROR_SCHEMA = json.loads((CONTRACTS_DIR / "ai-error.schema.json").read_text(encoding="utf-8"))

VALID_SUB = "11111111-2222-4333-8444-555555555555"
EXPECTED_CLIENT_ID = "analysis-worker"


def validate_json_schema(instance: Dict[str, Any], schema: Dict[str, Any]) -> None:
    validator = jsonschema.Draft202012Validator(
        schema,
        format_checker=jsonschema.FormatChecker(),
    )
    validator.validate(instance)


def _der_len(length: int) -> bytes:
    if length < 0x80:
        return bytes([length])
    length_bytes = length.to_bytes((length.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(length_bytes)]) + length_bytes


def _der_tlv(tag: int, body: bytes) -> bytes:
    return bytes([tag]) + _der_len(len(body)) + body


def _der_int(n: int) -> bytes:
    raw = n.to_bytes(max(1, (n.bit_length() + 7) // 8), "big")
    if raw[0] & 0x80:
        raw = b"\x00" + raw
    return _der_tlv(0x02, raw)


def _encode_spki_pem(n: int, e: int) -> str:
    rsa_pub_seq = _der_tlv(0x30, _der_int(n) + _der_int(e))
    alg_id_seq = _der_tlv(0x30, RSA_ENCRYPTION_OID_DER + b"\x05\x00")
    bit_string = _der_tlv(0x03, b"\x00" + rsa_pub_seq)
    spki_der = _der_tlv(0x30, alg_id_seq + bit_string)
    b64_lines = [
        base64.b64encode(spki_der).decode("ascii")[i : i + 64]
        for i in range(0, len(base64.b64encode(spki_der)), 64)
    ]
    return "-----BEGIN PUBLIC KEY-----\n" + "\n".join(b64_lines) + "\n-----END PUBLIC KEY-----\n"


def _is_probable_prime(n: int) -> bool:
    if n < 2 or n % 2 == 0:
        return n == 2
    for p in (3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53):
        if n % p == 0:
            return n == p
    d = n - 1
    r = 0
    while d % 2 == 0:
        d //= 2
        r += 1
    for a in (2, 3, 5, 7, 11, 13, 17, 19):
        x = pow(a, d, n)
        if x == 1 or x == n - 1:
            continue
        for _ in range(r - 1):
            x = pow(x, 2, n)
            if x == n - 1:
                break
        else:
            return False
    return True


def _next_prime(start: int) -> int:
    candidate = start | 1
    while not _is_probable_prime(candidate):
        candidate += 2
    return candidate


@dataclass(frozen=True)
class RsaKeyPairFixture:
    n: int
    e: int
    d: int
    public_pem: str
    key_id: str


def generate_deterministic_rsa_keypair(seed: int, bits: int = 2048) -> RsaKeyPairFixture:
    rng = random.Random(seed)
    half = bits // 2
    e = 65537
    while True:
        p_start = (1 << (half - 1)) | (1 << (half - 2)) | rng.getrandbits(half - 2) | 1
        q_start = (1 << (half - 1)) | (1 << (half - 2)) | rng.getrandbits(half - 2) | 1
        p = _next_prime(p_start)
        q = _next_prime(q_start)
        if p == q:
            continue
        n = p * q
        if n.bit_length() < bits:
            continue
        phi = (p - 1) * (q - 1)
        if math_gcd(e, phi) == 1:
            d = pow(e, -1, phi)
            public_pem = _encode_spki_pem(n, e)
            _, _, key_id, _ = parse_spki_rsa_public_key(public_pem)
            return RsaKeyPairFixture(n=n, e=e, d=d, public_pem=public_pem, key_id=key_id)


def math_gcd(a: int, b: int) -> int:
    while b:
        a, b = b, a % b
    return a


def b64url_encode(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def sign_rs256_jwt(
    keypair: RsaKeyPairFixture,
    header: Dict[str, Any],
    payload: Dict[str, Any],
) -> str:
    header_b64 = b64url_encode(json.dumps(header, separators=(",", ":")).encode("utf-8"))
    payload_b64 = b64url_encode(json.dumps(payload, separators=(",", ":")).encode("utf-8"))
    signing_input = f"{header_b64}.{payload_b64}".encode("ascii")

    key_bytes = (keypair.n.bit_length() + 7) // 8
    digest = hashlib.sha256(signing_input).digest()
    t_bytes = SHA256_DIGEST_INFO_PREFIX + digest
    ps_len = key_bytes - len(t_bytes) - 3
    em = b"\x00\x01" + (b"\xff" * ps_len) + b"\x00" + t_bytes
    sig_int = pow(int.from_bytes(em, "big"), keypair.d, keypair.n)
    sig_bytes = sig_int.to_bytes(key_bytes, "big")
    return f"{header_b64}.{payload_b64}.{b64url_encode(sig_bytes)}"


@pytest.fixture(scope="module")
def rsa_keypair() -> RsaKeyPairFixture:
    return generate_deterministic_rsa_keypair(seed=20261028, bits=2048)


@pytest.fixture(scope="module")
def other_rsa_keypair() -> RsaKeyPairFixture:
    return generate_deterministic_rsa_keypair(seed=99999999, bits=2048)


@pytest.fixture
def configured_frangi_env(monkeypatch, rsa_keypair: RsaKeyPairFixture):
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "false")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", rsa_keypair.public_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)


@pytest.fixture
def valid_bearer_headers(rsa_keypair: RsaKeyPairFixture) -> Dict[str, str]:
    now = int(time.time())
    token = sign_rs256_jwt(
        rsa_keypair,
        header={"alg": "RS256", "typ": "JWT", "kid": rsa_keypair.key_id},
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


def make_synthetic_fundus_png_b64(width: int = 160, height: int = 160) -> str:
    """Create a synthetic high-contrast, sharp fundus-like image that passes quality thresholds."""
    img = Image.new("RGB", (width, height), color=(0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse([8, 8, width - 8, height - 8], fill=(180, 110, 60))
    for offset in range(-50, 60, 12):
        draw.line([width // 2 + offset, 20, width // 2 - offset, height - 20], fill=(30, 15, 10), width=3)
        draw.line([20, height // 2 + offset, width - 20, height // 2 - offset], fill=(220, 190, 130), width=2)
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


# ==============================================================================
# 1. Service JWT RS256 & Readiness Tests (SCRUM-329)
# ==============================================================================

def test_ready_returns_200_when_frangi_and_signing_key_loaded(client, configured_frangi_env):
    resp = client.get("/ready")
    assert resp.status_code == 200, resp.text
    data = resp.json()
    assert data["status"] == "ready"
    assert data["engine"] == "frangi"
    assert data["model_version"] == "frangi-v0.2"
    assert data["config_version"] == "s3-frangi-1.0.0"
    assert data["threshold_version"] == "frangi-threshold-0.02-v1"
    assert data["jwt_verification_ready"] is True
    assert data["timestamp"].endswith("Z")


def test_ready_returns_503_when_signing_key_missing(client, monkeypatch):
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", raising=False)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPath", raising=False)
    monkeypatch.delenv("ServiceAuth__PublicKeyPem", raising=False)

    resp = client.get("/ready")
    assert resp.status_code == 503
    err = resp.json()
    validate_json_schema(err, ERROR_SCHEMA)
    assert err["error_code"] == "SERVICE_NOT_READY"


def test_ready_returns_503_when_in_mock_mode(client, configured_frangi_env, monkeypatch):
    monkeypatch.setenv("AURA_AI_ENGINE", "mock")
    monkeypatch.setenv("AURA_ALLOW_MOCK", "true")
    resp = client.get("/ready")
    assert resp.status_code == 503
    err = resp.json()
    validate_json_schema(err, ERROR_SCHEMA)
    assert err["error_code"] == "SERVICE_NOT_READY"


def test_analyze_returns_503_when_private_key_or_ambiguous_key_supplied(
    client, rsa_keypair: RsaKeyPairFixture, monkeypatch, tmp_path
):
    fake_private_pem = "-----BEGIN PRIVATE KEY-----\nMIIBVgIBADANBgkqhkiG9w0BAQEFAASCAUA=\n-----END PRIVATE KEY-----\n"

    # Case 1: Private key supplied to verifier -> rejected fail-closed with 503
    monkeypatch.setenv("AURA_AI_ENGINE", "frangi")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", fake_private_pem)
    monkeypatch.delenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", raising=False)
    resp = client.post("/api/v1/analyze", json={})
    assert resp.status_code == 503
    validate_json_schema(resp.json(), ERROR_SCHEMA)

    # Case 2: Both PATH and PEM set -> rejected as ambiguous with 503
    pub_file = tmp_path / "signing-public.pem"
    pub_file.write_text(rsa_keypair.public_pem, encoding="utf-8")
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PEM", rsa_keypair.public_pem)
    monkeypatch.setenv("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", str(pub_file))
    resp2 = client.post("/api/v1/analyze", json={})
    assert resp2.status_code == 503
    validate_json_schema(resp2.json(), ERROR_SCHEMA)


def test_weak_rsa_key_under_2048_bits_rejected():
    weak_pem = _encode_spki_pem(n=(1 << 1023) | 1, e=65537)
    with pytest.raises(ServiceAuthConfigError, match="2048"):
        ServiceJwtVerifier(public_key_pem=weak_pem)


def test_analyze_jwt_authentication_and_authorization_failures(
    client,
    configured_frangi_env,
    rsa_keypair: RsaKeyPairFixture,
    other_rsa_keypair: RsaKeyPairFixture,
):
    now = int(time.time())
    base_header = {"alg": "RS256", "typ": "JWT", "kid": rsa_keypair.key_id}
    base_claims = {
        "iss": DEFAULT_ISSUER,
        "aud": DEFAULT_AUDIENCE,
        "sub": VALID_SUB,
        "client_id": EXPECTED_CLIENT_ID,
        "scope": DEFAULT_REQUIRED_SCOPE,
        "iat": now,
        "nbf": now,
        "exp": now + 300,
    }

    # 1. Missing Authorization header -> 401 before body validation
    r_missing = client.post("/api/v1/analyze", json={})
    assert r_missing.status_code == 401
    validate_json_schema(r_missing.json(), ERROR_SCHEMA)

    # 2. Wrong signature (signed by another key) -> 401
    bad_sig_token = sign_rs256_jwt(other_rsa_keypair, base_header, base_claims)
    r_sig = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {bad_sig_token}"})
    assert r_sig.status_code == 401
    validate_json_schema(r_sig.json(), ERROR_SCHEMA)

    # 3. Wrong kid -> 401
    bad_kid_token = sign_rs256_jwt(rsa_keypair, {**base_header, "kid": "aura-local-ffffffffffffffff"}, base_claims)
    r_kid = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {bad_kid_token}"})
    assert r_kid.status_code == 401
    validate_json_schema(r_kid.json(), ERROR_SCHEMA)

    # 4. Wrong issuer -> 401
    bad_iss_token = sign_rs256_jwt(rsa_keypair, base_header, {**base_claims, "iss": "aura-identity-local"})
    r_iss = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {bad_iss_token}"})
    assert r_iss.status_code == 401
    validate_json_schema(r_iss.json(), ERROR_SCHEMA)

    # 5. Expired token (exp < now - 30) -> 401
    exp_token = sign_rs256_jwt(
        rsa_keypair,
        base_header,
        {**base_claims, "iat": now - 400, "nbf": now - 400, "exp": now - 100},
    )
    r_exp = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {exp_token}"})
    assert r_exp.status_code == 401
    validate_json_schema(r_exp.json(), ERROR_SCHEMA)

    # 6. Token lifetime > 300s -> 401
    long_token = sign_rs256_jwt(
        rsa_keypair,
        base_header,
        {**base_claims, "iat": now, "nbf": now, "exp": now + 600},
    )
    r_long = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {long_token}"})
    assert r_long.status_code == 401
    validate_json_schema(r_long.json(), ERROR_SCHEMA)

    # 7. Zero UUID sub -> 401
    zero_sub_token = sign_rs256_jwt(
        rsa_keypair,
        base_header,
        {**base_claims, "sub": "00000000-0000-0000-0000-000000000000"},
    )
    r_sub = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {zero_sub_token}"})
    assert r_sub.status_code == 401
    validate_json_schema(r_sub.json(), ERROR_SCHEMA)

    # 8. Wrong audience -> 403
    bad_aud_token = sign_rs256_jwt(rsa_keypair, base_header, {**base_claims, "aud": "aura-billing"})
    r_aud = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {bad_aud_token}"})
    assert r_aud.status_code == 403
    validate_json_schema(r_aud.json(), ERROR_SCHEMA)
    assert r_aud.json()["error_code"] == "FORBIDDEN_SERVICE_AUDIENCE"

    # 9. Wrong scope -> 403
    bad_scope_token = sign_rs256_jwt(rsa_keypair, base_header, {**base_claims, "scope": "profile:resolve"})
    r_scope = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {bad_scope_token}"})
    assert r_scope.status_code == 403
    validate_json_schema(r_scope.json(), ERROR_SCHEMA)
    assert r_scope.json()["error_code"] == "FORBIDDEN_SERVICE_SCOPE"

    # 10. Wrong caller client_id -> 403
    bad_client_token = sign_rs256_jwt(rsa_keypair, base_header, {**base_claims, "client_id": "analysis-api"})
    r_client = client.post("/api/v1/analyze", json={}, headers={"Authorization": f"Bearer {bad_client_token}"})
    assert r_client.status_code == 403
    validate_json_schema(r_client.json(), ERROR_SCHEMA)
    assert r_client.json()["error_code"] == "FORBIDDEN_SERVICE_CLIENT"


# ==============================================================================
# 2. Pre-Frangi Quality Formulas & Exact Boundary Tests (SCRUM-330)
# ==============================================================================

def test_quality_threshold_exact_equality_accepted_and_below_rejected():
    """
    Verify config/ai-frangi.yaml quality rule:
    rejectIf: region empty OR regionRatio < 0.15 OR luminance < 15 OR luminance > 240
              OR contrast < 8 OR sharpness < 3.5
    Equality to each threshold MUST be accepted.
    """
    rgb = np.zeros((100, 100, 3), dtype=np.uint8)
    interior = np.zeros((100, 100), dtype=bool)
    interior[20:50, 20:70] = True
    assert int(np.count_nonzero(interior)) == 1500

    yy, xx = np.indices((100, 100))
    checker = ((yy + xx) % 2 == 0)
    rgb[interior & checker] = np.array([120, 120, 120], dtype=np.uint8)
    rgb[interior & ~checker] = np.array([60, 60, 60], dtype=np.uint8)

    measured = evaluate_frangi_quality(rgb, interior=interior)
    assert measured.region_ratio == pytest.approx(0.15)
    assert measured.is_acceptable is True

    # Exact boundary equality for luminance, contrast, sharpness via custom FrangiRuntimeConfig
    exact_cfg = FrangiRuntimeConfig(
        min_estimated_region_ratio=measured.region_ratio,
        min_luminance=measured.luminance,
        max_luminance=measured.luminance,
        min_contrast=measured.contrast,
        min_sharpness=measured.sharpness,
    )
    exact_report = evaluate_frangi_quality(rgb, interior=interior, config=exact_cfg)
    assert exact_report.is_acceptable is True
    assert exact_report.failed_checks == ()

    # Slightly below regionRatio -> rejected
    interior_1499 = interior.copy()
    interior_1499[20, 20] = False
    r_ratio = evaluate_frangi_quality(rgb, interior=interior_1499)
    assert r_ratio.is_acceptable is False
    assert "LOW_REGION_RATIO" in r_ratio.failed_checks

    # Slightly below min_luminance -> rejected
    cfg_low_lum = FrangiRuntimeConfig(min_luminance=measured.luminance + 0.01)
    r_low_lum = evaluate_frangi_quality(rgb, interior=interior, config=cfg_low_lum)
    assert r_low_lum.is_acceptable is False
    assert "LOW_LUMINANCE" in r_low_lum.failed_checks

    # Slightly above max_luminance -> rejected
    cfg_high_lum = FrangiRuntimeConfig(max_luminance=measured.luminance - 0.01)
    r_high_lum = evaluate_frangi_quality(rgb, interior=interior, config=cfg_high_lum)
    assert r_high_lum.is_acceptable is False
    assert "HIGH_LUMINANCE" in r_high_lum.failed_checks

    # Slightly below min_contrast -> rejected
    cfg_low_con = FrangiRuntimeConfig(min_contrast=measured.contrast + 0.01)
    r_low_con = evaluate_frangi_quality(rgb, interior=interior, config=cfg_low_con)
    assert r_low_con.is_acceptable is False
    assert "LOW_CONTRAST" in r_low_con.failed_checks

    # Slightly below min_sharpness -> rejected
    cfg_low_sharp = FrangiRuntimeConfig(min_sharpness=measured.sharpness + 0.01)
    r_low_sharp = evaluate_frangi_quality(rgb, interior=interior, config=cfg_low_sharp)
    assert r_low_sharp.is_acceptable is False
    assert "LOW_SHARPNESS" in r_low_sharp.failed_checks

    # Empty interior region -> rejected
    empty_interior = np.zeros((100, 100), dtype=bool)
    r_empty = evaluate_frangi_quality(rgb, interior=empty_interior)
    assert r_empty.is_acceptable is False
    assert "EMPTY_ESTIMATED_REGION" in r_empty.failed_checks


def test_low_quality_image_returns_422_without_segmentation(
    client, configured_frangi_env, valid_bearer_headers
):
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), color=(0, 0, 0)).save(buf, format="PNG")
    dark_b64 = base64.b64encode(buf.getvalue()).decode("ascii")

    req_body = {
        "request_id": str(uuid.uuid4()),
        "patient_id": None,
        "eye_side": "left",
        "mode": "retina_vessels",
        "modality": "FUNDUS",
        "image_base64": dark_b64,
        "include_mask": True,
        "include_overlay": True,
    }
    validate_json_schema(req_body, REQUEST_SCHEMA)

    resp = client.post("/api/v1/analyze", json=req_body, headers=valid_bearer_headers)
    assert resp.status_code == 422, resp.text
    err = resp.json()
    validate_json_schema(err, ERROR_SCHEMA)
    assert err["error_code"] == "LOW_QUALITY_IMAGE"
    assert err["details"]["is_mock"] is False
    assert err["details"]["quality_assessment_performed"] is True
    assert "segmentation" not in err
    assert "metrics" not in err


# ==============================================================================
# 3. Real CHASE_DB1 Fundus Inference (>= 3 Real Images) & Schema 1.2 Validation
# ==============================================================================

@pytest.mark.parametrize(
    "image_filename, eye_side",
    [
        ("Image_01L.jpg", "left"),
        ("Image_02L.jpg", "left"),
        ("Image_11R.jpg", "right"),
    ],
)
def test_real_chase_db1_fundus_inference_conforms_to_contract_1_2(
    client,
    configured_frangi_env,
    valid_bearer_headers,
    image_filename: str,
    eye_side: str,
):
    image_path = CHASE_RAW_DIR / image_filename
    assert image_path.is_file(), f"Missing CHASE_DB1 image: {image_path}"

    raw_bytes = image_path.read_bytes()
    req_id = str(uuid.uuid4())
    req_payload = {
        "request_id": req_id,
        "patient_id": None,
        "eye_side": eye_side,
        "mode": "retina_vessels",
        "modality": "FUNDUS",
        "image_base64": base64.b64encode(raw_bytes).decode("ascii"),
        "include_mask": True,
        "include_overlay": True,
    }

    validate_json_schema(req_payload, REQUEST_SCHEMA)

    response = client.post("/api/v1/analyze", json=req_payload, headers=valid_bearer_headers)
    assert response.status_code == 200, response.text
    result = response.json()

    validate_json_schema(result, RESPONSE_SCHEMA)

    assert result["request_id"] == req_id
    assert result["patient_id"] is None
    assert result["timestamp"].endswith("Z")
    assert result["status"] == "WARNING"
    assert result["is_mock"] is False
    assert result["model_version"] == "frangi-v0.2"
    assert result["config_version"] == "s3-frangi-1.0.0"
    assert result["threshold_version"] == "frangi-threshold-0.02-v1"
    assert result["risk_available"] is False
    assert result["risk_assessment"] is None
    assert any(w["code"] == "RISK_UNAVAILABLE" for w in result["warnings"])

    assert result["image_info"] == {
        "width": 999,
        "height": 960,
        "channels": 3,
        "eye_side": eye_side,
        "modality": "FUNDUS",
    }

    metrics = result["metrics"]
    assert 0.0 < metrics["vessel_density"] < 0.35
    assert metrics["tortuosity_index"] is None
    assert metrics["av_ratio"] is None
    assert metrics["fractal_dimension"] is None
    assert metrics["branching_points"] is None

    seg = result["segmentation"]
    assert seg["mask_format"] == "png_base64"

    mask_bytes = base64.b64decode(seg["mask_base64"], validate=True)
    with Image.open(io.BytesIO(mask_bytes)) as mask_img:
        mask_img.load()
        assert mask_img.format == "PNG"
        assert mask_img.mode == "L"
        assert mask_img.size == (999, 960)
        mask_arr = np.asarray(mask_img, dtype=np.uint8)
        unique_vals = set(np.unique(mask_arr).tolist())
        assert unique_vals == {0, 255}
        expected_density = round(float(np.count_nonzero(mask_arr)) / float(999 * 960), 6)
        assert metrics["vessel_density"] == pytest.approx(expected_density)

    overlay_bytes = base64.b64decode(seg["overlay_base64"], validate=True)
    with Image.open(io.BytesIO(overlay_bytes)) as overlay_img:
        overlay_img.load()
        assert overlay_img.format == "PNG"
        assert overlay_img.mode == "RGB"
        assert overlay_img.size == (999, 960)


# ==============================================================================
# 4. Payload Constraints, Edge Cases, and Runtime Error Handling
# ==============================================================================

def test_frangi_rejects_data_url_and_webp_and_invalid_invariants(
    client, configured_frangi_env, valid_bearer_headers
):
    synth_b64 = make_synthetic_fundus_png_b64()
    base_req = {
        "request_id": str(uuid.uuid4()),
        "patient_id": None,
        "eye_side": "unknown",
        "mode": "retina_vessels",
        "modality": "FUNDUS",
        "image_base64": synth_b64,
        "include_mask": True,
        "include_overlay": True,
    }

    # 1. Data URL prefix rejected with 400
    r_data_url = client.post(
        "/api/v1/analyze",
        json={**base_req, "image_base64": f"data:image/png;base64,{synth_b64}"},
        headers=valid_bearer_headers,
    )
    assert r_data_url.status_code == 400
    validate_json_schema(r_data_url.json(), ERROR_SCHEMA)
    assert r_data_url.json()["error_code"] == "INVALID_IMAGE_PAYLOAD"

    # 2. WEBP format rejected in Frangi mode with 400 (only PNG/JPEG allowed)
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), color=(100, 80, 60)).save(buf, format="WEBP")
    webp_b64 = base64.b64encode(buf.getvalue()).decode("ascii")
    r_webp = client.post(
        "/api/v1/analyze",
        json={**base_req, "image_base64": webp_b64},
        headers=valid_bearer_headers,
    )
    assert r_webp.status_code == 400
    validate_json_schema(r_webp.json(), ERROR_SCHEMA)

    # 3. Non-UUID request_id rejected with 422
    r_bad_uuid = client.post(
        "/api/v1/analyze",
        json={**base_req, "request_id": "not-a-uuid"},
        headers=valid_bearer_headers,
    )
    assert r_bad_uuid.status_code == 422
    validate_json_schema(r_bad_uuid.json(), ERROR_SCHEMA)

    # 4. Non-null patient_id rejected with 422
    r_pat = client.post(
        "/api/v1/analyze",
        json={**base_req, "patient_id": "PAT-001"},
        headers=valid_bearer_headers,
    )
    assert r_pat.status_code == 422
    validate_json_schema(r_pat.json(), ERROR_SCHEMA)

    # 5. include_mask=False rejected with 422
    r_no_mask = client.post(
        "/api/v1/analyze",
        json={**base_req, "include_mask": False},
        headers=valid_bearer_headers,
    )
    assert r_no_mask.status_code == 422
    validate_json_schema(r_no_mask.json(), ERROR_SCHEMA)


def test_all_zero_frangi_response_returns_zero_mask():
    rgb = np.full((64, 64, 3), 128, dtype=np.uint8)
    interior = np.ones((64, 64), dtype=bool)
    vessel_mask, response = run_frangi_segmentation(rgb, interior)
    assert not np.any(vessel_mask)
    assert float(np.max(response)) == 0.0


def test_runtime_exception_returns_500_conforming_to_error_schema(
    client, configured_frangi_env, valid_bearer_headers, monkeypatch
):
    def boom(*args, **kwargs):
        raise RuntimeError("Simulated internal Frangi failure")

    monkeypatch.setattr("app.main.process_frangi_analysis", boom)
    req_body = {
        "request_id": str(uuid.uuid4()),
        "patient_id": None,
        "eye_side": "left",
        "mode": "retina_vessels",
        "modality": "FUNDUS",
        "image_base64": make_synthetic_fundus_png_b64(),
        "include_mask": True,
        "include_overlay": True,
    }
    resp = client.post("/api/v1/analyze", json=req_body, headers=valid_bearer_headers)
    assert resp.status_code == 500
    err = resp.json()
    validate_json_schema(err, ERROR_SCHEMA)
    assert err["error_code"] == "INFERENCE_RUNTIME_ERROR"


# ==============================================================================
# 5. Smoke Training Pipeline Isolation Verification (SCRUM-330)
# ==============================================================================

def test_segmentation_smoke_training_pipeline_does_not_touch_test_split(tmp_path):
    config = yaml.safe_load((REPO_ROOT / "src/ai-core/config/segmentation-training-smoke.yaml").read_text(encoding="utf-8"))
    config["output"]["reportPath"] = str(tmp_path / "training-report.json")
    config_path = tmp_path / "training-config.yaml"
    config_path.write_text(yaml.safe_dump(config), encoding="utf-8")
    report = run_smoke_training(config_path)
    assert report["status"] == "SEGMENTATION_TRAINING_SMOKE_PASSED"
    assert report["replaces_runtime_frangi"] is False
    assert report["risk_available"] is False
    assert report["risk_labels_used"] is False
    assert report["dataset"]["seed"] == 42
    assert report["dataset"]["train_image_count"] == 16
    assert report["dataset"]["validation_image_count"] == 6
    assert report["dataset"]["test_images_loaded"] == 0
    assert report["dataset"]["test_split_touched"] is False
    assert report["dataset"]["subject_leakage_detected"] is False
    assert report["loss_decreased"] is True

def test_correlation_id_middleware_echoes_or_generates_id(client):
    # 1. Echo incoming correlation ID
    custom_cid = "aura-trace-custom-998877"
    resp = client.get("/health", headers={"X-Correlation-Id": custom_cid})
    assert resp.status_code == 200
    assert resp.headers.get("X-Correlation-Id") == custom_cid

    # 2. Auto-generate UUID when missing
    resp_no_header = client.get("/health")
    assert resp_no_header.status_code == 200
    generated_cid = resp_no_header.headers.get("X-Correlation-Id")
    assert generated_cid is not None
    # Validate UUID format
    parsed = uuid.UUID(generated_cid)
    assert parsed.version == 4
