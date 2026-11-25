"""Sprint 7 W7-M4-02 (SCRUM-406): CPU Latency, RAM, Concurrency, and Golden Parity Benchmark.

Measures real AI Core FastAPI inference latency (cold vs warm p50/p95/max),
process RSS & Python heap memory against compose.yaml container limits,
verifies bounded admission (429 RESEARCH_MODEL_BUSY + Retry-After), and
enforces the Golden / Precision Parity Gate so optimizations never silently
alter masks, metrics, or probabilities.
"""

from __future__ import annotations

import argparse
import base64
import ctypes
import gc
import hashlib
import io
import json
import os
import platform
import subprocess
import sys
import time
import tracemalloc
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

import numpy as np
import yaml
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parents[3]
AI_CORE_DIR = REPO_ROOT / "src" / "ai-core"
if str(AI_CORE_DIR) not in sys.path:
    sys.path.insert(0, str(AI_CORE_DIR))

from fastapi.testclient import TestClient  # noqa: E402
from skimage.filters import frangi as skimage_frangi  # noqa: E402
from skimage.filters import gaussian  # noqa: E402

from app import main as main_module  # noqa: E402
from app.auth.service_jwt import DEFAULT_AUDIENCE, DEFAULT_ISSUER, DEFAULT_REQUIRED_SCOPE  # noqa: E402
from app.services.frangi_service import (  # noqa: E402
    estimate_prediction_region,
    load_frangi_config,
    run_frangi_segmentation,
)
from tests.test_frangi_sprint3 import (  # noqa: E402
    EXPECTED_CLIENT_ID,
    VALID_SUB,
    generate_deterministic_rsa_keypair,
    make_synthetic_fundus_png_b64,
    sign_rs256_jwt,
)

DEFAULT_OUTPUT_PATH = REPO_ROOT / "docs" / "benchmarks" / "w7_latency_ram_profile_v1.0.json"
DEFAULT_DR_CHECKPOINT = REPO_ROOT / "model-artifacts" / "dr-resnet34-hrf-v1" / "model.pt"
EXPECTED_DR_CHECKPOINT_SHA256 = "8c9114c54f1b06300081bb82b80a0ea3a10e3d035232e1c5f3570b2db42df8f7"
SRS_HARD_SLA_P95_MS = 20000.0
STRETCH_TARGET_P95_MS = 1500.0


def current_rss_mb() -> float:
    try:
        import psutil  # type: ignore

        return round(psutil.Process().memory_info().rss / (1024.0 * 1024.0), 2)
    except ImportError:
        pass
    if os.name == "nt":
        class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
            _fields_ = [
                ("cb", ctypes.c_ulong),
                ("PageFaultCount", ctypes.c_ulong),
                ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t),
            ]

        counters = PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
        get_current_process = ctypes.windll.kernel32.GetCurrentProcess
        get_current_process.restype = ctypes.c_void_p
        get_mem_info = ctypes.windll.psapi.GetProcessMemoryInfo
        get_mem_info.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong]
        get_mem_info.restype = ctypes.c_int
        handle = get_current_process()
        if get_mem_info(handle, ctypes.byref(counters), counters.cb):
            return round(counters.WorkingSetSize / (1024.0 * 1024.0), 2)
    if Path("/proc/self/statm").is_file():
        pages = int(Path("/proc/self/statm").read_text(encoding="utf-8").split()[1])
        return round((pages * os.sysconf("SC_PAGE_SIZE")) / (1024.0 * 1024.0), 2)
    return 0.0


def git_commit_sha(root: Path = REPO_ROOT) -> str:
    try:
        return (
            subprocess.check_output(
                ["git", "rev-parse", "HEAD"], cwd=str(root), stderr=subprocess.DEVNULL, text=True
            ).strip()
            or "UNKNOWN"
        )
    except Exception:
        return "UNKNOWN"


def parse_compose_ai_core_budget(root: Path = REPO_ROOT) -> Dict[str, Any]:
    compose_path = root / "compose.yaml"
    data = yaml.safe_load(compose_path.read_text(encoding="utf-8"))
    svc = data["services"]["ai-core"]
    mem_raw = str(svc.get("mem_limit", "")).strip().lower()
    if mem_raw.endswith("g"):
        mem_mib = int(float(mem_raw[:-1]) * 1024)
    elif mem_raw.endswith("m"):
        mem_mib = int(float(mem_raw[:-1]))
    else:
        mem_mib = 0
    command = svc.get("command", [])
    command_str = " ".join(command) if isinstance(command, list) else str(command)
    return {
        "sourceFile": "compose.yaml",
        "service": "ai-core",
        "cpus": float(svc.get("cpus", 0)),
        "memLimitRaw": str(svc.get("mem_limit", "")),
        "memLimitMib": mem_mib,
        "pidsLimit": int(svc.get("pids_limit", 0)),
        "singleWorkerPinned": "--workers 1" in command_str,
        "command": command_str,
    }


def _make_auth_header_and_env() -> Tuple[Dict[str, str], Dict[str, Optional[str]]]:
    keypair = generate_deterministic_rsa_keypair(seed=20261121, bits=2048)
    env_keys = (
        "AURA_AI_ENGINE",
        "AURA_ALLOW_MOCK",
        "AURA_SERVICE_AUTH_PUBLIC_KEY_PEM",
        "AURA_SERVICE_AUTH_PUBLIC_KEY_PATH",
        "ServiceAuth__PublicKeyPath",
        "ServiceAuth__PublicKeyPem",
        "AURA_INTEGRATED_RESEARCH_ENABLED",
    )
    saved = {k: os.environ.get(k) for k in env_keys}
    os.environ["AURA_AI_ENGINE"] = "frangi"
    os.environ["AURA_ALLOW_MOCK"] = "false"
    os.environ["AURA_INTEGRATED_RESEARCH_ENABLED"] = "false"
    os.environ["AURA_SERVICE_AUTH_PUBLIC_KEY_PEM"] = keypair.public_pem
    for drop in ("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", "ServiceAuth__PublicKeyPath", "ServiceAuth__PublicKeyPem"):
        os.environ.pop(drop, None)

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
    return {"Authorization": f"Bearer {token}"}, saved


def _restore_env(saved: Dict[str, Optional[str]]) -> None:
    for k, v in saved.items():
        if v is None:
            os.environ.pop(k, None)
        else:
            os.environ[k] = v


def _load_frangi_benchmark_samples(root: Path) -> Tuple[str, List[Tuple[str, str, int, int, np.ndarray]]]:
    chase_dir = root / "datasets" / "CHASE_DB1" / "raw"
    test_names = [
        "Image_01L.jpg",
        "Image_01R.jpg",
        "Image_02L.jpg",
        "Image_02R.jpg",
        "Image_11L.jpg",
        "Image_11R.jpg",
    ]
    samples: List[Tuple[str, str, int, int, np.ndarray]] = []
    if all((chase_dir / name).is_file() for name in test_names):
        for name in test_names:
            p = chase_dir / name
            raw_bytes = p.read_bytes()
            rgb = np.asarray(Image.open(p).convert("RGB"), dtype=np.uint8)
            samples.append((name, base64.b64encode(raw_bytes).decode("ascii"), rgb.shape[1], rgb.shape[0], rgb))
        return "CHASE_DB1_TEST_6_REAL_IMAGES", samples

    b64 = make_synthetic_fundus_png_b64(width=512, height=512)
    rgb = np.asarray(Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGB"), dtype=np.uint8)
    for idx in range(6):
        samples.append((f"synthetic_fundus_{idx + 1}.png", b64, rgb.shape[1], rgb.shape[0], rgb))
    return "SYNTHETIC_FUNDUS_512_FALLBACK", samples


def run_golden_parity_gate(rgb: np.ndarray, root: Path = REPO_ROOT) -> Dict[str, Any]:
    """Compare production vectorized Frangi vs reference skimage.filters.frangi on same RGB."""
    cfg = load_frangi_config(root / "src" / "ai-core" / "config" / "ai-frangi.yaml")
    interior = estimate_prediction_region(rgb, cfg)

    prod_mask, prod_norm = run_frangi_segmentation(rgb, interior, cfg)

    green = rgb[:, :, 1].astype(np.float64) / float(cfg.scale)
    green_smoothed = gaussian(green, sigma=cfg.gaussian_sigma, preserve_range=True, mode="nearest")
    ref_raw = skimage_frangi(
        green_smoothed,
        sigmas=tuple(cfg.sigmas),
        alpha=cfg.alpha,
        beta=cfg.beta,
        gamma=cfg.gamma,
        black_ridges=cfg.black_ridges,
        mode=cfg.boundary_mode,
    )
    ref_raw = np.where(interior, ref_raw, float(cfg.outside_region))
    ref_max = float(ref_raw.max())
    ref_norm = (ref_raw / ref_max) if ref_max > 0.0 else np.zeros_like(ref_raw)
    ref_mask = (ref_norm >= cfg.threshold) & interior

    inter = int(np.count_nonzero(prod_mask & ref_mask))
    union_sum = int(np.count_nonzero(prod_mask)) + int(np.count_nonzero(ref_mask))
    dice = (2.0 * inter / union_sum) if union_sum > 0 else 1.0
    mismatched_pixels = int(np.count_nonzero(prod_mask != ref_mask))
    interior_px = int(np.count_nonzero(interior))
    prod_density = int(np.count_nonzero(prod_mask)) / interior_px if interior_px > 0 else 0.0
    ref_density = int(np.count_nonzero(ref_mask)) / interior_px if interior_px > 0 else 0.0
    density_diff = abs(prod_density - ref_density)
    max_resp_diff = float(np.max(np.abs(prod_norm - ref_norm)))

    passed = dice >= 0.999999 and mismatched_pixels == 0 and density_diff == 0.0
    return {
        "gateName": "frangi_vectorized_vs_skimage_reference",
        "passed": passed,
        "dice": round(dice, 8),
        "mismatchedPixels": mismatched_pixels,
        "totalPixels": int(prod_mask.size),
        "maxAbsVesselDensityDiff": round(density_diff, 10),
        "maxAbsNormalizedResponseDiff": round(max_resp_diff, 10),
        "thresholds": {
            "minDice": 1.0,
            "maxMismatchedPixels": 0,
            "maxAbsVesselDensityDiff": 0.0,
        },
        "quantizationAndOnnxPolicy": {
            "frangiOnnxFp16Int8Applicable": False,
            "frangiRationale": "Frangi v0.2 is a weightless analytical 2D Hessian filter already vectorized in float64 with Dice=1.000000.",
            "neuralQuantizationPolicy": "Opt-in only; blocked unless golden parity holds (max_abs_prob_diff <= 1e-4, gradcam_cosine >= 0.99).",
        },
    }


def benchmark_dr_cnn_optional(root: Path = REPO_ROOT, checkpoint: Path = DEFAULT_DR_CHECKPOINT) -> Dict[str, Any]:
    hrf_sample = root / "datasets" / "HRF" / "images" / "02_dr.JPG"
    if not checkpoint.is_file() or not hrf_sample.is_file():
        return {
            "status": "NOT_TESTED_MISSING_ASSET",
            "reason": "DR ResNet34 checkpoint or HRF sample image is not present locally.",
        }
    try:
        import torch  # noqa: F401
        from app.services.dr_cnn_service import _load_verified_dr_model, screen_dr_cnn
    except ImportError as exc:
        return {
            "status": "NOT_TESTED_MISSING_TORCH",
            "reason": f"PyTorch not installed: {exc}",
        }

    actual_sha = hashlib.sha256(checkpoint.read_bytes()).hexdigest()
    if actual_sha != EXPECTED_DR_CHECKPOINT_SHA256:
        return {
            "status": "NOT_TESTED_CHECKPOINT_HASH_MISMATCH",
            "reason": f"Expected {EXPECTED_DR_CHECKPOINT_SHA256}, observed {actual_sha}.",
        }

    rgb = np.asarray(Image.open(hrf_sample).convert("RGB"), dtype=np.uint8)
    _load_verified_dr_model.cache_clear()
    gc.collect()
    rss_before = current_rss_mb()

    t0 = time.perf_counter()
    cold_out = screen_dr_cnn(rgb, str(checkpoint), actual_sha)
    cold_ms = round((time.perf_counter() - t0) * 1000.0, 2)
    cold_heat = np.asarray(cold_out["explainability"]["heatmap"], dtype=np.float64).ravel()

    warm_latencies: List[float] = []
    warm_probs: List[float] = []
    min_gradcam_cos = 1.0
    for _ in range(3):
        ts = time.perf_counter()
        out = screen_dr_cnn(rgb, str(checkpoint), actual_sha)
        warm_latencies.append(round((time.perf_counter() - ts) * 1000.0, 2))
        warm_probs.append(float(out["probability_dr"]))
        heat = np.asarray(out["explainability"]["heatmap"], dtype=np.float64).ravel()
        denom = float(np.linalg.norm(cold_heat) * np.linalg.norm(heat))
        cos = float(np.dot(cold_heat, heat) / denom) if denom > 0 else 1.0
        min_gradcam_cos = min(min_gradcam_cos, cos)

    rss_after = current_rss_mb()
    max_prob_drift = max(abs(p - float(cold_out["probability_dr"])) for p in warm_probs)
    return {
        "status": "EVALUATED",
        "optimization": "Verified read-only LRU model cache keyed by (resolved_path, verified_sha256); SHA-256 verified on every call.",
        "modelVersion": cold_out["model_version"],
        "checkpointSha256": actual_sha,
        "inputImage": "datasets/HRF/images/02_dr.JPG",
        "inputResolution": f"{rgb.shape[1]}x{rgb.shape[0]}",
        "modelInputGeometry": cold_out["explainability"]["geometry"],
        "coldMs": cold_ms,
        "warmSampleSize": len(warm_latencies),
        "warmLatenciesMs": warm_latencies,
        "warmP50Ms": round(float(np.percentile(warm_latencies, 50)), 2),
        "warmP95Ms": round(float(np.percentile(warm_latencies, 95)), 2),
        "warmMaxMs": round(float(max(warm_latencies)), 2),
        "rssBeforeMb": rss_before,
        "rssAfterMb": rss_after,
        "rssDeltaMb": round(rss_after - rss_before, 2),
        "goldenParity": {
            "maxAbsProbDiff": round(max_prob_drift, 10),
            "minGradcamCosine": round(min_gradcam_cos, 8),
            "passed": max_prob_drift <= 1e-4 and min_gradcam_cos >= 0.99,
        },
    }


def run_benchmark(root: Path = REPO_ROOT, warmup_runs: int = 1) -> Dict[str, Any]:
    compose_budget = parse_compose_ai_core_budget(root)
    input_source, samples = _load_frangi_benchmark_samples(root)
    headers, saved_env = _make_auth_header_and_env()

    try:
        gc.collect()
        rss_before = current_rss_mb()
        tracemalloc.start()

        cold_ms = 0.0
        warm_latencies: List[float] = []
        peak_rss = rss_before

        with TestClient(main_module.app) as client:
            # Cold request (1st call on process)
            _, first_b64, w0, h0, first_rgb = samples[0]
            t_cold = time.perf_counter()
            r_cold = client.post(
                "/api/v1/analyze",
                headers=headers,
                json={"request_id": str(uuid.uuid4()), "image_base64": first_b64},
            )
            cold_ms = round((time.perf_counter() - t_cold) * 1000.0, 2)
            if r_cold.status_code != 200:
                raise RuntimeError(f"Cold Frangi API request failed with HTTP {r_cold.status_code}: {r_cold.text}")
            peak_rss = max(peak_rss, current_rss_mb())

            # Optional additional warm-up passes
            for _ in range(max(0, warmup_runs - 1)):
                r_w = client.post(
                    "/api/v1/analyze",
                    headers=headers,
                    json={"request_id": str(uuid.uuid4()), "image_base64": first_b64},
                )
                if r_w.status_code != 200:
                    raise RuntimeError(f"Warmup request failed with HTTP {r_w.status_code}")

            # Warm sample measurements
            for name, img_b64, _, _, _ in samples:
                t0 = time.perf_counter()
                resp = client.post(
                    "/api/v1/analyze",
                    headers=headers,
                    json={"request_id": str(uuid.uuid4()), "image_base64": img_b64},
                )
                elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 2)
                if resp.status_code != 200:
                    raise RuntimeError(f"Warm request for {name} failed with HTTP {resp.status_code}: {resp.text}")
                warm_latencies.append(elapsed_ms)
                peak_rss = max(peak_rss, current_rss_mb())

            # Bounded concurrency backpressure verification (429 + Retry-After)
            os.environ["AURA_INTEGRATED_RESEARCH_ENABLED"] = "true"
            acquired = main_module.CNN_SLOT.acquire(blocking=False)
            try:
                busy_resp = client.post(
                    "/api/v1/analyze",
                    headers=headers,
                    json={"request_id": str(uuid.uuid4()), "image_base64": first_b64},
                )
            finally:
                if acquired:
                    main_module.CNN_SLOT.release()
                os.environ["AURA_INTEGRATED_RESEARCH_ENABLED"] = "false"

        current_heap, peak_heap = tracemalloc.get_traced_memory()
        tracemalloc.stop()
        gc.collect()
        rss_after = current_rss_mb()
        peak_rss = max(peak_rss, rss_after)

        golden_parity = run_golden_parity_gate(first_rgb, root=root)
        dr_profile = benchmark_dr_cnn_optional(root=root)
    finally:
        _restore_env(saved_env)

    warm_p50 = round(float(np.percentile(warm_latencies, 50)), 2)
    warm_p95 = round(float(np.percentile(warm_latencies, 95)), 2)
    warm_max = round(float(max(warm_latencies)), 2)
    warm_mean = round(float(np.mean(warm_latencies)), 2)
    mem_limit_mib = compose_budget["memLimitMib"]

    return {
        "profileVersion": "1.0.0-s7",
        "taskId": "W7-M4-02",
        "jiraKey": "SCRUM-406",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "gitCommitSha": git_commit_sha(root),
        "environment": {
            "os": platform.system(),
            "platform": platform.platform(),
            "processor": platform.processor() or "UNKNOWN",
            "logicalCpuCount": os.cpu_count() or 1,
            "python": platform.python_version(),
            "numpy": np.__version__,
            "threadEnv": {
                "OMP_NUM_THREADS": os.getenv("OMP_NUM_THREADS"),
                "OPENBLAS_NUM_THREADS": os.getenv("OPENBLAS_NUM_THREADS"),
                "MKL_NUM_THREADS": os.getenv("MKL_NUM_THREADS"),
            },
        },
        "composeBudget": compose_budget,
        "frangiApiProfile": {
            "endpoint": "POST /api/v1/analyze",
            "inputSource": input_source,
            "imageResolution": f"{w0}x{h0}",
            "warmupRuns": warmup_runs,
            "warmSampleSize": len(warm_latencies),
            "coldMs": cold_ms,
            "warmLatenciesMs": warm_latencies,
            "warmMeanMs": warm_mean,
            "warmP50Ms": warm_p50,
            "warmP95Ms": warm_p95,
            "warmMaxMs": warm_max,
            "srsHardSla": {
                "maxP95Ms": SRS_HARD_SLA_P95_MS,
                "passed": warm_p95 < SRS_HARD_SLA_P95_MS,
            },
            "stretchTarget": {
                "targetP95Ms": STRETCH_TARGET_P95_MS,
                "isOptionalStretch": True,
                "doesNotReplaceSrs20s": True,
                "met": warm_p95 < STRETCH_TARGET_P95_MS,
            },
        },
        "memoryProfile": {
            "rssBeforeMb": rss_before,
            "rssAfterMb": rss_after,
            "peakRssMb": peak_rss,
            "peakPythonHeapMb": round(peak_heap / (1024.0 * 1024.0), 2),
            "retainedPythonHeapMb": round(current_heap / (1024.0 * 1024.0), 2),
            "containerMemLimitMib": mem_limit_mib,
            "withinContainerLimit": peak_rss < float(mem_limit_mib) if mem_limit_mib > 0 else False,
        },
        "concurrencyGate": {
            "mechanism": "BoundedSemaphore(1) per single-worker container",
            "httpStatus": busy_resp.status_code,
            "errorCode": busy_resp.json().get("error_code"),
            "retryAfter": busy_resp.headers.get("Retry-After"),
            "passed": (
                busy_resp.status_code == 429
                and busy_resp.json().get("error_code") == "RESEARCH_MODEL_BUSY"
                and busy_resp.headers.get("Retry-After") == "1"
            ),
        },
        "goldenParityGate": golden_parity,
        "optionalDrCnnCpuProfile": dr_profile,
        "reproduceCommands": {
            "runProfile": "PYTHONPATH=src/ai-core python src/ai-core/scripts/benchmark_w7_latency_ram.py --output docs/benchmarks/w7_latency_ram_profile_v1.0.json",
            "verifyProfile": "python src/ai-core/scripts/benchmark_w7_latency_ram.py --verify docs/benchmarks/w7_latency_ram_profile_v1.0.json",
        },
    }


def verify_profile(profile_path: Path = DEFAULT_OUTPUT_PATH, root: Path = REPO_ROOT) -> List[str]:
    errors: List[str] = []
    if not profile_path.is_file():
        return [f"Missing latency/RAM profile: {profile_path}"]

    data = json.loads(profile_path.read_text(encoding="utf-8"))
    actual_budget = parse_compose_ai_core_budget(root)
    recorded_budget = data.get("composeBudget", {})
    for key in ("cpus", "memLimitMib", "pidsLimit", "singleWorkerPinned"):
        if recorded_budget.get(key) != actual_budget.get(key):
            errors.append(
                f"compose.yaml budget drift for {key}: profile={recorded_budget.get(key)} vs compose={actual_budget.get(key)}"
            )
    if actual_budget.get("singleWorkerPinned") is not True:
        errors.append("compose.yaml ai-core service must pin --workers 1.")

    frangi = data.get("frangiApiProfile", {})
    sla = frangi.get("srsHardSla", {})
    stretch = frangi.get("stretchTarget", {})
    if sla.get("maxP95Ms") != SRS_HARD_SLA_P95_MS or sla.get("passed") is not True:
        errors.append("SRS hard SLA (< 20000 ms p95) must be recorded and passed.")
    if stretch.get("doesNotReplaceSrs20s") is not True or stretch.get("isOptionalStretch") is not True:
        errors.append("Stretch target must explicitly state it does not replace SRS 20s SLA.")
    if not isinstance(frangi.get("coldMs"), (int, float)) or not isinstance(frangi.get("warmP95Ms"), (int, float)):
        errors.append("Cold and warm p95 latencies must be recorded separately.")

    mem = data.get("memoryProfile", {})
    if mem.get("withinContainerLimit") is not True or float(mem.get("peakRssMb", 0.0)) <= 0.0:
        errors.append("Peak RSS must be measured (> 0 MB) and stay within compose.yaml container memory limit.")

    conc = data.get("concurrencyGate", {})
    if (
        conc.get("passed") is not True
        or conc.get("httpStatus") != 429
        or conc.get("errorCode") != "RESEARCH_MODEL_BUSY"
        or conc.get("retryAfter") != "1"
    ):
        errors.append("Concurrency backpressure gate must pass with HTTP 429 RESEARCH_MODEL_BUSY and Retry-After: 1.")

    parity = data.get("goldenParityGate", {})
    if (
        parity.get("passed") is not True
        or parity.get("mismatchedPixels") != 0
        or parity.get("dice", 0.0) < 0.999999
        or parity.get("maxAbsVesselDensityDiff") != 0.0
    ):
        errors.append("Golden / Precision Parity Gate must pass with Dice=1.0 and 0 mismatched pixels.")

    return errors


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Sprint 7 W7-M4-02 Latency, RAM & Golden Parity Benchmark.")
    parser.add_argument("--output", type=Path, default=None, help="Output JSON profile path.")
    parser.add_argument("--verify", type=Path, default=None, help="Verify an existing profile JSON.")
    parser.add_argument("--warmup", type=int, default=1, help="Number of warmup calls (>= 1).")
    args = parser.parse_args(argv)

    if args.verify is not None:
        errors = verify_profile(args.verify)
        if errors:
            for err in errors:
                print(f"FAIL: {err}", file=sys.stderr)
            return 1
        print(f"PASS: Sprint 7 latency/RAM/parity profile verified ({args.verify}).")
        return 0

    output_path = args.output or DEFAULT_OUTPUT_PATH
    profile = run_benchmark(warmup_runs=max(1, args.warmup))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(profile, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    errors = verify_profile(output_path)
    if errors:
        for err in errors:
            print(f"FAIL after generation: {err}", file=sys.stderr)
        return 1
    print(f"PASS: Generated and verified latency/RAM profile at {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
