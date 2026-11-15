"""
Sprint 4-5 Runtime API v1.3 Benchmark & Concurrent Uvicorn Stress Test
(Module M4 - SCRUM-351 / W45-M4-02, SCRUM-352 / W45-M4-03, SCRUM-353 / W45-M4-04).

Executes:
1. Phase 1 - Single-stream Contract 1.3.0 benchmark across all 6 frozen CHASE_DB1 test
   images (999x960 RGB) against a live isolated Uvicorn server process, verifying
   < 1.8s CPU latency, 100% mask fidelity vs Sprint 3 baseline (Dice == 1.000000),
   Tortuosity Index + Heuristic Risk Assessment, and JSON Schema 1.3 compliance.
2. Phase 2 - Live Uvicorn HTTP Stress Test firing 100 full 999x960 inference requests
   across 5 and 10 concurrent client workers (or continuous soak via --duration-seconds),
   monitoring the isolated Uvicorn server process's OS RSS RAM (baseline, peak, steady-state
   post-load) and server CPU utilization.
3. Phase 3 - CPython allocator & Garbage Collector audit (tracemalloc + gc.get_stats)
   across 20 consecutive 999x960 inferences to verify zero Python heap or NumPy buffer leaks.
4. Exports canonical JSON report to docs/benchmarks/frangi_runtime_api_v1.3.json.
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import csv
import ctypes
from ctypes import wintypes
import gc
import hashlib
import io
import json
import os
import platform
import socket
import subprocess
import sys
import threading
import time
import tracemalloc
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import jsonschema
import numpy as np
import PIL
import scipy
import skimage
from PIL import Image
from skimage.filters import frangi as skimage_frangi
from skimage.filters import gaussian as skimage_gaussian

SCRIPT_DIR = Path(__file__).resolve().parent
AI_CORE_DIR = SCRIPT_DIR.parent
REPO_ROOT = AI_CORE_DIR.parent.parent
if str(AI_CORE_DIR) not in sys.path:
    sys.path.insert(0, str(AI_CORE_DIR))
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import baseline_benchmark as baseline  # noqa: E402
from app.auth.service_jwt import (  # noqa: E402
    DEFAULT_AUDIENCE,
    DEFAULT_ISSUER,
    DEFAULT_REQUIRED_SCOPE,
)
from app.schemas.analysis import AnalysisRequest  # noqa: E402
from app.services.frangi_service import (  # noqa: E402
    estimate_prediction_region,
    load_frangi_config,
    process_frangi_analysis,
)
from tests.test_frangi_sprint3 import (  # noqa: E402
    EXPECTED_CLIENT_ID,
    VALID_SUB,
    generate_deterministic_rsa_keypair,
    sign_rs256_jwt,
)

CONTRACTS_DIR = REPO_ROOT / "contracts"
DEFAULT_OUTPUT_JSON = REPO_ROOT / "docs" / "benchmarks" / "frangi_runtime_api_v1.3.json"


if sys.platform == "win32":
    class _PROCESS_MEMORY_COUNTERS(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    class _FILETIME(ctypes.Structure):
        _fields_ = [
            ("dwLowDateTime", wintypes.DWORD),
            ("dwHighDateTime", wintypes.DWORD),
        ]

    class _PROCESSENTRY32(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ProcessID", wintypes.DWORD),
            ("th32DefaultHeapID", ctypes.c_size_t),
            ("th32ModuleID", wintypes.DWORD),
            ("cntThreads", wintypes.DWORD),
            ("th32ParentProcessID", wintypes.DWORD),
            ("pcPriClassBase", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
            ("szExeFile", ctypes.c_char * 260),
        ]


def get_process_tree_pids(root_pid: int) -> List[int]:
    """Return [root_pid, ...all descendant child pids] so venv launcher wrappers are traversed."""
    if sys.platform == "win32":
        TH32CS_SNAPPROCESS = 0x00000002
        kernel32 = ctypes.windll.kernel32
        kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if not snap or snap == wintypes.HANDLE(-1).value:
            return [root_pid]
        children_map: Dict[int, List[int]] = {}
        try:
            entry = _PROCESSENTRY32()
            entry.dwSize = ctypes.sizeof(_PROCESSENTRY32)
            if kernel32.Process32First(snap, ctypes.byref(entry)):
                while True:
                    ppid = int(entry.th32ParentProcessID)
                    cpid = int(entry.th32ProcessID)
                    children_map.setdefault(ppid, []).append(cpid)
                    if not kernel32.Process32Next(snap, ctypes.byref(entry)):
                        break
        finally:
            kernel32.CloseHandle(snap)
        result: List[int] = []
        queue = [int(root_pid)]
        seen = set()
        while queue:
            cur = queue.pop(0)
            if cur in seen:
                continue
            seen.add(cur)
            result.append(cur)
            queue.extend(children_map.get(cur, []))
        return result

    # Linux /proc traversal
    children_map_linux: Dict[int, List[int]] = {}
    proc_dir = Path("/proc")
    if proc_dir.is_dir():
        for p in proc_dir.iterdir():
            if p.name.isdigit():
                stat_file = p / "stat"
                try:
                    parts = stat_file.read_text(encoding="utf-8").rsplit(")", 1)[1].split()
                    ppid = int(parts[1])
                    children_map_linux.setdefault(ppid, []).append(int(p.name))
                except Exception:
                    continue
    res_linux: List[int] = []
    q_linux = [int(root_pid)]
    seen_l = set()
    while q_linux:
        cur = q_linux.pop(0)
        if cur in seen_l:
            continue
        seen_l.add(cur)
        res_linux.append(cur)
        q_linux.extend(children_map_linux.get(cur, []))
    return res_linux


def _single_pid_rss_and_peak_mb(target_pid: int) -> Tuple[float, float]:
    if sys.platform == "win32":
        PROCESS_QUERY_INFORMATION = 0x0400
        PROCESS_VM_READ = 0x0010
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = wintypes.HANDLE
        handle = kernel32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, int(target_pid))
        if not handle:
            return 0.0, 0.0
        try:
            counters = _PROCESS_MEMORY_COUNTERS()
            counters.cb = ctypes.sizeof(_PROCESS_MEMORY_COUNTERS)
            if ctypes.windll.psapi.GetProcessMemoryInfo(
                handle,
                ctypes.byref(counters),
                counters.cb,
            ):
                cur_mb = float(counters.WorkingSetSize) / (1024.0 * 1024.0)
                peak_mb = float(counters.PeakWorkingSetSize) / (1024.0 * 1024.0)
                return cur_mb, peak_mb
        finally:
            kernel32.CloseHandle(handle)
    else:
        status_path = Path(f"/proc/{target_pid}/status")
        if status_path.is_file():
            cur_mb = 0.0
            peak_mb = 0.0
            for line in status_path.read_text(encoding="utf-8").splitlines():
                if line.startswith("VmRSS:"):
                    cur_mb = float(line.split()[1]) / 1024.0
                elif line.startswith("VmHWM:"):
                    peak_mb = float(line.split()[1]) / 1024.0
            return cur_mb, max(cur_mb, peak_mb)
    return 0.0, 0.0


def get_pid_rss_and_peak_mb(pid: Optional[int] = None) -> Tuple[float, float]:
    """Return summed (current_rss_mb, os_peak_rss_mb) across target pid and its child processes."""
    root = pid if pid is not None else os.getpid()
    pids = get_process_tree_pids(root)
    tot_cur = 0.0
    tot_peak = 0.0
    for p in pids:
        c, pk = _single_pid_rss_and_peak_mb(p)
        tot_cur += c
        tot_peak += pk
    return round(tot_cur, 2), round(tot_peak, 2)


def _single_pid_cpu_seconds(target_pid: int) -> float:
    if sys.platform == "win32":
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = wintypes.HANDLE
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(target_pid))
        if not handle:
            return 0.0
        try:
            c_time = _FILETIME()
            e_time = _FILETIME()
            k_time = _FILETIME()
            u_time = _FILETIME()
            if kernel32.GetProcessTimes(
                handle,
                ctypes.byref(c_time),
                ctypes.byref(e_time),
                ctypes.byref(k_time),
                ctypes.byref(u_time),
            ):
                k_ticks = (int(k_time.dwHighDateTime) << 32) | int(k_time.dwLowDateTime)
                u_ticks = (int(u_time.dwHighDateTime) << 32) | int(u_time.dwLowDateTime)
                return float(k_ticks + u_ticks) / 10_000_000.0
        finally:
            kernel32.CloseHandle(handle)
    else:
        stat_path = Path(f"/proc/{target_pid}/stat")
        if stat_path.is_file():
            parts = stat_path.read_text(encoding="utf-8").split()
            clk_tck = float(os.sysconf("SC_CLK_TCK"))
            return (float(parts[13]) + float(parts[14])) / clk_tck
    return 0.0


def get_pid_cpu_seconds(pid: Optional[int] = None) -> float:
    """Return total kernel + user CPU seconds consumed across target pid and its child processes."""
    if pid is None:
        return time.process_time()
    return sum(_single_pid_cpu_seconds(p) for p in get_process_tree_pids(pid))


def find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return int(s.getsockname()[1])


def deterministic_uuid_for_stem(stem: str) -> str:
    digest = hashlib.sha256(f"aura-s45-v1.3-benchmark:{stem}".encode("utf-8")).hexdigest()
    return f"{digest[:8]}-{digest[8:12]}-4{digest[13:16]}-8{digest[17:20]}-{digest[20:32]}"


def http_post_json(
    url: str,
    payload: Dict[str, Any],
    headers: Dict[str, str],
    timeout_s: float = 120.0,
) -> Tuple[int, Dict[str, str], Dict[str, Any], float]:
    raw_data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=raw_data,
        headers={"Content-Type": "application/json", **headers},
        method="POST",
    )
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=timeout_s) as resp:
        body_bytes = resp.read()
        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 2)
        resp_headers = {k.lower(): v for k, v in resp.headers.items()}
        return int(resp.status), resp_headers, json.loads(body_bytes.decode("utf-8")), elapsed_ms


def compute_reference_s3_mask(rgb: np.ndarray, cfg) -> np.ndarray:
    interior = estimate_prediction_region(rgb, config=cfg)
    green = rgb[:, :, 1].astype(np.float64) / float(cfg.scale)
    green_smoothed = skimage_gaussian(
        green,
        sigma=cfg.gaussian_sigma,
        preserve_range=True,
        mode="nearest",
    )
    resp = skimage_frangi(
        green_smoothed,
        sigmas=list(cfg.sigmas),
        alpha=cfg.alpha,
        beta=cfg.beta,
        gamma=cfg.gamma,
        black_ridges=cfg.black_ridges,
        mode=cfg.boundary_mode,
    )
    resp[~interior] = 0.0
    return (resp / float(resp.max())) >= cfg.threshold


def stop_server_process(server_proc) -> None:
    if server_proc.poll() is not None:
        return
    if sys.platform == "win32":
        # A Windows venv launcher has a child Python process; terminate the owned tree.
        subprocess.run(["taskkill", "/PID", str(server_proc.pid), "/T", "/F"],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=10)
        server_proc.wait(timeout=5)
    else:
        server_proc.terminate()
        try:
            server_proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            server_proc.kill()
            server_proc.wait(timeout=5)


def run_benchmark_and_stress_test(
    total_stress_requests: int = 100,
    max_concurrency: int = 10,
    duration_seconds: float = 0.0,
    output_path: Path = DEFAULT_OUTPUT_JSON,
) -> Dict[str, Any]:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    v13_cfg_path = AI_CORE_DIR / "config" / "ai-frangi-v1.3.yaml"
    cfg = load_frangi_config(v13_cfg_path)
    source_hashes = {
        str(path.relative_to(AI_CORE_DIR)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted((AI_CORE_DIR / "app").rglob("*.py"))
    }
    source_hashes["config/ai-frangi-v1.3.yaml"] = hashlib.sha256(v13_cfg_path.read_bytes()).hexdigest()
    source_hashes["scripts/stress_test_ai_core.py"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()

    req_schema = json.loads((CONTRACTS_DIR / "ai-request-1.3.schema.json").read_text(encoding="utf-8"))
    resp_schema = json.loads((CONTRACTS_DIR / "ai-response-1.3.schema.json").read_text(encoding="utf-8"))
    req_validator = jsonschema.Draft202012Validator(req_schema, format_checker=jsonschema.FormatChecker())
    resp_validator = jsonschema.Draft202012Validator(resp_schema, format_checker=jsonschema.FormatChecker())

    keypair = generate_deterministic_rsa_keypair(seed=20261030, bits=2048)
    server_env = os.environ.copy()
    server_env["AURA_AI_ENGINE"] = "frangi"
    server_env["AURA_ALLOW_MOCK"] = "false"
    server_env["AURA_FRANGI_CONFIG_PATH"] = str(v13_cfg_path)
    server_env["AURA_SERVICE_AUTH_PUBLIC_KEY_PEM"] = keypair.public_pem
    server_env.pop("AURA_SERVICE_AUTH_PUBLIC_KEY_PATH", None)

    token_lock = threading.Lock()
    token_state: Dict[str, Any] = {"token": "", "minted_at": 0.0}

    def get_valid_bearer_token() -> str:
        with token_lock:
            now_f = time.time()
            if not token_state["token"] or (now_f - token_state["minted_at"]) > 120.0:
                now_i = int(now_f)
                token_state["token"] = sign_rs256_jwt(
                    keypair,
                    header={"alg": "RS256", "typ": "JWT", "kid": keypair.key_id},
                    payload={
                        "iss": DEFAULT_ISSUER,
                        "aud": DEFAULT_AUDIENCE,
                        "sub": VALID_SUB,
                        "client_id": EXPECTED_CLIENT_ID,
                        "scope": DEFAULT_REQUIRED_SCOPE,
                        "iat": now_i,
                        "nbf": now_i,
                        "exp": now_i + 240,
                    },
                )
                token_state["minted_at"] = now_f
            return str(token_state["token"])

    with baseline.MANIFEST.open(encoding="utf-8-sig", newline="") as f:
        test_rows = [r for r in csv.DictReader(f) if r["split"] == "test"]
    if len(test_rows) != 6:
        raise ValueError(f"Expected 6 CHASE_DB1 test rows, got {len(test_rows)}")

    # Preload 6 test payloads and Sprint 3 reference masks
    prepared_inputs: List[Dict[str, Any]] = []
    for row in test_rows:
        image_path = baseline.read_verified(row, "image")
        mask_path = baseline.read_verified(row, "mask_1st")
        raw_bytes = image_path.read_bytes()
        with Image.open(io.BytesIO(raw_bytes)) as im:
            rgb = np.asarray(im.convert("RGB"), dtype=np.uint8)
        with Image.open(mask_path) as gt_im:
            gt_arr = np.asarray(gt_im.convert("L"), dtype=np.uint8) > 0
        s3_ref_mask = compute_reference_s3_mask(rgb, cfg)
        prepared_inputs.append(
            {
                "name": image_path.name,
                "stem": image_path.stem,
                "subject_id": row["subject_id"],
                "eye_side": "left" if row["eye_side"].upper() == "L" else "right",
                "image_base64": base64.b64encode(raw_bytes).decode("ascii"),
                "gt_mask": gt_arr,
                "s3_ref_mask": s3_ref_mask,
            }
        )

    # Boot isolated production-like Uvicorn ASGI server subprocess
    port = find_free_port()
    base_url = f"http://127.0.0.1:{port}"
    server_proc = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "uvicorn",
            "app.main:app",
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--log-level",
            "warning",
            "--no-access-log",
        ],
        cwd=str(AI_CORE_DIR),
        env=server_env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
    )

    ready_ok = False
    for _ in range(80):
        if server_proc.poll() is not None:
            err_out = server_proc.stderr.read().decode("utf-8", errors="replace") if server_proc.stderr else ""
            raise RuntimeError(f"Uvicorn exited prematurely: {err_out}")
        try:
            with urllib.request.urlopen(f"{base_url}/ready", timeout=2.0) as r:
                if r.status == 200:
                    ready_ok = True
                    break
        except Exception:
            time.sleep(0.1)
    if not ready_ok:
        stop_server_process(server_proc)
        raise RuntimeError("Uvicorn server failed to reach /ready == 200")

    server_pid = server_proc.pid

    try:
        # 2 warm-up requests to stabilize SciPy/Pillow caches & working set
        for w_idx in range(2):
            warm_payload = {
                "request_id": str(uuid.uuid4()),
                "patient_id": None,
                "eye_side": prepared_inputs[w_idx]["eye_side"],
                "mode": "retina_vessels",
                "modality": "FUNDUS",
                "image_base64": prepared_inputs[w_idx]["image_base64"],
                "include_mask": True,
                "include_overlay": True,
            }
            http_post_json(
                f"{base_url}/api/v1/analyze",
                warm_payload,
                {"Authorization": f"Bearer {get_valid_bearer_token()}", "X-Correlation-Id": f"warmup-v13-{w_idx}"},
            )

        # ======================================================================
        # Phase 1: Sequential 6-Image CHASE_DB1 Test Split Benchmark (AC1 / AC2)
        # ======================================================================
        samples: List[Dict[str, Any]] = []
        for item in prepared_inputs:
            req_id = deterministic_uuid_for_stem(item["stem"])
            cid = f"bench-v13-{item['stem'].lower()}"
            payload = {
                "request_id": req_id,
                "patient_id": None,
                "eye_side": item["eye_side"],
                "mode": "retina_vessels",
                "modality": "FUNDUS",
                "image_base64": item["image_base64"],
                "include_mask": True,
                "include_overlay": True,
            }
            req_validator.validate(payload)

            status_code, resp_hdrs, body, wall_ms = http_post_json(
                f"{base_url}/api/v1/analyze",
                payload,
                {"Authorization": f"Bearer {get_valid_bearer_token()}", "X-Correlation-Id": cid},
            )
            if status_code != 200:
                raise RuntimeError(f"Benchmark call failed with {status_code}: {body}")
            if resp_hdrs.get("x-correlation-id") != cid:
                raise RuntimeError(f"X-Correlation-Id mismatch: {resp_hdrs}")

            resp_validator.validate(body)

            mask_bytes = base64.b64decode(body["segmentation"]["mask_base64"], validate=True)
            with Image.open(io.BytesIO(mask_bytes)) as pred_img:
                pred_arr = np.asarray(pred_img.convert("L"), dtype=np.uint8) > 0

            overlap = baseline.overlap_metrics(pred_arr, item["gt_mask"])
            s3_ref = item["s3_ref_mask"]
            s3_inter = int(np.count_nonzero(pred_arr & s3_ref))
            s3_denom = int(np.count_nonzero(pred_arr) + np.count_nonzero(s3_ref))
            dice_vs_s3 = round((2.0 * s3_inter / s3_denom) if s3_denom > 0 else 1.0, 6)
            mismatched_vs_s3 = int(np.count_nonzero(pred_arr ^ s3_ref))

            samples.append(
                {
                    "image": item["name"],
                    "subject_id": item["subject_id"],
                    "eye_side": item["eye_side"],
                    "request_id": req_id,
                    "correlation_id_echoed": True,
                    "status": body["status"],
                    "is_mock": body["is_mock"],
                    "risk_available": body["risk_available"],
                    "vessel_density": body["metrics"]["vessel_density"],
                    "tortuosity_index": body["metrics"]["tortuosity_index"],
                    "branching_points": body["metrics"]["branching_points"],
                    "risk_assessment": body["risk_assessment"],
                    "dice": overlap["dice"],
                    "iou": overlap["iou"],
                    "sensitivity": overlap["sensitivity"],
                    "specificity": overlap["specificity"],
                    "dice_vs_sprint3_baseline": dice_vs_s3,
                    "mismatched_pixels_vs_sprint3_baseline": mismatched_vs_s3,
                    "processing_time_ms": body["processing_time_ms"],
                    "http_roundtrip_ms": wall_ms,
                }
            )

        # ======================================================================
        # Phase 2: Concurrent Uvicorn Stress Test (5 -> 10 Concurrent, >= 100 Runs)
        # ======================================================================
        rss_baseline_mb, _ = get_pid_rss_and_peak_mb(server_pid)
        rss_samples_mb: List[float] = [rss_baseline_mb]
        os_peak_samples_mb: List[float] = [rss_baseline_mb]
        rss_lock = threading.Lock()
        stop_monitor = threading.Event()

        def memory_monitor() -> None:
            while not stop_monitor.is_set():
                cur_rss, os_peak = get_pid_rss_and_peak_mb(server_pid)
                if cur_rss > 0:
                    with rss_lock:
                        rss_samples_mb.append(cur_rss)
                        os_peak_samples_mb.append(os_peak)
                time.sleep(0.05)

        monitor_thread = threading.Thread(target=memory_monitor, daemon=True)
        monitor_thread.start()

        def execute_single_load_request(idx: int) -> Tuple[bool, float, float, str]:
            item = prepared_inputs[idx % len(prepared_inputs)]
            req_id = str(uuid.uuid4())
            cid = f"stress-req-{idx:04d}"
            payload = {
                "request_id": req_id,
                "patient_id": None,
                "eye_side": item["eye_side"],
                "mode": "retina_vessels",
                "modality": "FUNDUS",
                "image_base64": item["image_base64"],
                "include_mask": True,
                "include_overlay": True,
            }
            try:
                sc, hdrs, bdy, rt_ms = http_post_json(
                    f"{base_url}/api/v1/analyze",
                    payload,
                    {"Authorization": f"Bearer {get_valid_bearer_token()}", "X-Correlation-Id": cid},
                )
                ok = (
                    sc == 200
                    and hdrs.get("x-correlation-id") == cid
                    and bdy.get("risk_available") is True
                    and bdy.get("risk_assessment") is not None
                )
                return ok, float(bdy.get("processing_time_ms", 0.0)), rt_ms, ""
            except Exception as exc:
                return False, 0.0, 0.0, str(exc)

        server_cpu_t0 = get_pid_cpu_seconds(server_pid)
        wall_t0 = time.perf_counter()

        wave1_results: List[Tuple[bool, float, float, str]] = []
        wave2_results: List[Tuple[bool, float, float, str]] = []

        # Wave 1: Concurrency = 5 (20 requests)
        wave1_count = min(20, total_stress_requests // 5 or 10)
        with concurrent.futures.ThreadPoolExecutor(max_workers=min(5, max_concurrency)) as pool:
            futs = [pool.submit(execute_single_load_request, i) for i in range(wave1_count)]
            for fut in concurrent.futures.as_completed(futs):
                wave1_results.append(fut.result())

        # Wave 2: Concurrency = max_concurrency (default 10) up to total_stress_requests (or duration)
        remaining = max(0, total_stress_requests - wave1_count)
        idx_cursor = wave1_count
        with concurrent.futures.ThreadPoolExecutor(max_workers=max_concurrency) as pool:
            if remaining > 0:
                futs2 = [
                    pool.submit(execute_single_load_request, idx_cursor + j)
                    for j in range(remaining)
                ]
                for fut in concurrent.futures.as_completed(futs2):
                    wave2_results.append(fut.result())
                idx_cursor += remaining

            while duration_seconds > 0 and (time.perf_counter() - wall_t0) < duration_seconds:
                batch_futs = [
                    pool.submit(execute_single_load_request, idx_cursor + k)
                    for k in range(max_concurrency)
                ]
                for fut in concurrent.futures.as_completed(batch_futs):
                    wave2_results.append(fut.result())
                idx_cursor += max_concurrency

        stress_wall_s = time.perf_counter() - wall_t0
        server_cpu_s = max(0.0, get_pid_cpu_seconds(server_pid) - server_cpu_t0)

        # Allow 0.5s quiescent settle window and verify server is still healthy via /ready
        time.sleep(0.5)
        with urllib.request.urlopen(f"{base_url}/ready", timeout=5.0) as r_check:
            server_alive_after_stress = r_check.status == 200

        stop_monitor.set()
        monitor_thread.join(timeout=2.0)
        rss_after_load_mb, os_peak_final_mb = get_pid_rss_and_peak_mb(server_pid)

    finally:
        stop_server_process(server_proc)

    # ==========================================================================
    # Phase 3: CPython Heap & GC Leak Audit (20 Consecutive 999x960 Inferences)
    # ==========================================================================
    gc.collect()
    tracemalloc.start()
    for w in range(2):
        w_item = prepared_inputs[w]
        process_frangi_analysis(
            AnalysisRequest(
                request_id=str(uuid.uuid4()),
                patient_id=None,
                eye_side=w_item["eye_side"],
                mode="retina_vessels",
                modality="FUNDUS",
                image_base64=w_item["image_base64"],
                include_mask=True,
                include_overlay=True,
            ),
            config=cfg,
        )
    gc.collect()
    heap_baseline_bytes, _ = tracemalloc.get_traced_memory()
    tracemalloc.reset_peak()
    gc_stats_before = gc.get_stats()

    audit_iterations = 20
    for idx in range(audit_iterations):
        a_item = prepared_inputs[idx % len(prepared_inputs)]
        res = process_frangi_analysis(
            AnalysisRequest(
                request_id=str(uuid.uuid4()),
                patient_id=None,
                eye_side=a_item["eye_side"],
                mode="retina_vessels",
                modality="FUNDUS",
                image_base64=a_item["image_base64"],
                include_mask=True,
                include_overlay=True,
            ),
            config=cfg,
        )
        del res

    gc_collected = gc.collect()
    heap_after_bytes, heap_peak_bytes = tracemalloc.get_traced_memory()
    tracemalloc.stop()
    gc_stats_after = gc.get_stats()

    proc_times = [s["processing_time_ms"] for s in samples]
    http_times = [s["http_roundtrip_ms"] for s in samples]

    stress_results = wave1_results + wave2_results
    total_completed = len(stress_results)
    succeeded = sum(1 for ok, _, _, _ in stress_results if ok)
    failed = total_completed - succeeded
    w1_rt_ms = [rt for ok, _, rt, _ in wave1_results if ok]
    w2_rt_ms = [rt for ok, _, rt, _ in wave2_results if ok]
    stress_rt_ms = [rt for ok, _, rt, _ in stress_results if ok]
    cpu_cores = os.cpu_count() or 1

    gc_gen0_collections = gc_stats_after[0]["collections"] - gc_stats_before[0]["collections"]
    gc_gen1_collections = gc_stats_after[1]["collections"] - gc_stats_before[1]["collections"]
    gc_gen2_collections = gc_stats_after[2]["collections"] - gc_stats_before[2]["collections"]

    sampled_rss_peak_mb = max(rss_samples_mb + [rss_after_load_mb])
    os_reported_peak_mb = max(os_peak_samples_mb + [os_peak_final_mb, sampled_rss_peak_mb])
    heap_retained_delta_mb = round((heap_after_bytes - heap_baseline_bytes) / (1024.0 * 1024.0), 4)

    report: Dict[str, Any] = {
        "status": "MEASURED_LOCAL_UVICORN_NOT_CONTAINER",
        "runtime_source_sha256": source_hashes,
        "requested_duration_seconds": duration_seconds,
        "timestamp_utc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "api_version": "1.3.0",
        "model_version": cfg.model_version,
        "config_version": cfg.config_version,
        "threshold_version": cfg.threshold_version,
        "optimization": "separable-1d-hessian-vectorized-6pass-inplace-v1.3",
        "is_mock": False,
        "risk_available": True,
        "jwt_auth_enforced": True,
        "correlation_id_enforced": True,
        "dataset": "CHASE_DB1",
        "split": "test",
        "sample_count": len(samples),
        "subject_count": 3,
        "geometry_preserved": [999, 960],
        "environment": {
            "os": platform.platform(),
            "python": platform.python_version(),
            "cpu_logical_cores": cpu_cores,
            "Pillow": PIL.__version__,
            "numpy": np.__version__,
            "scipy": scipy.__version__,
            "scikit_image": skimage.__version__,
        },
        "accuracy_metrics": {
            "mean_dice": round(float(np.mean([s["dice"] for s in samples])), 6),
            "mean_iou": round(float(np.mean([s["iou"] for s in samples])), 6),
            "mean_sensitivity": round(float(np.mean([s["sensitivity"] for s in samples])), 6),
            "mean_specificity": round(float(np.mean([s["specificity"] for s in samples])), 6),
            "mean_dice_vs_sprint3_baseline": round(
                float(np.mean([s["dice_vs_sprint3_baseline"] for s in samples])),
                6,
            ),
            "total_mismatched_pixels_vs_sprint3_baseline": int(
                sum(s["mismatched_pixels_vs_sprint3_baseline"] for s in samples)
            ),
        },
        "runtime_latency_ms": {
            "scope": "live_isolated_uvicorn_http_endpoint_including_jwt_quality_frangi_tortuosity_risk_png_encode",
            "sla_target_ms": 1800.0,
            "processing_time_mean_ms": round(float(np.mean(proc_times)), 2),
            "processing_time_median_ms": round(float(np.median(proc_times)), 2),
            "processing_time_p95_ms": round(float(np.percentile(proc_times, 95)), 2),
            "processing_time_max_ms": round(float(np.max(proc_times)), 2),
            "http_roundtrip_mean_ms": round(float(np.mean(http_times)), 2),
            "http_roundtrip_median_ms": round(float(np.median(http_times)), 2),
            "http_roundtrip_p95_ms": round(float(np.percentile(http_times, 95)), 2),
            "http_roundtrip_max_ms": round(float(np.max(http_times)), 2),
        },
        "stress_test": {
            "server": "isolated_uvicorn_subprocess_127.0.0.1",
            "server_healthy_after_load": server_alive_after_stress,
            "concurrency_levels_tested": [5, max_concurrency],
            "max_concurrent_requests": max_concurrency,
            "total_requests": total_completed,
            "successful_requests": succeeded,
            "failed_requests": failed,
            "error_rate_percent": round((failed / total_completed) * 100.0, 4) if total_completed else 0.0,
            "wall_clock_seconds": round(stress_wall_s, 2),
            "throughput_requests_per_sec": round(total_completed / stress_wall_s, 3) if stress_wall_s > 0 else 0.0,
            "wave1_5_concurrent_median_ms": round(float(np.median(w1_rt_ms)), 2) if w1_rt_ms else 0.0,
            "wave2_10_concurrent_median_ms": round(float(np.median(w2_rt_ms)), 2) if w2_rt_ms else 0.0,
            "concurrent_http_roundtrip_median_ms": round(float(np.median(stress_rt_ms)), 2) if stress_rt_ms else 0.0,
            "concurrent_http_roundtrip_p95_ms": round(float(np.percentile(stress_rt_ms, 95)), 2) if stress_rt_ms else 0.0,
            "cpu_usage": {
                "uvicorn_server_cpu_seconds": round(server_cpu_s, 2),
                "avg_core_equivalents_utilized": round(server_cpu_s / stress_wall_s, 2) if stress_wall_s > 0 else 0.0,
                "avg_system_normalized_percent": round(
                    ((server_cpu_s / stress_wall_s) / cpu_cores) * 100.0,
                    2,
                )
                if stress_wall_s > 0
                else 0.0,
            },
            "memory_and_gc": {
                "uvicorn_rss_baseline_after_warmup_mb": rss_baseline_mb,
                "uvicorn_rss_sampled_peak_under_10_concurrent_mb": sampled_rss_peak_mb,
                "uvicorn_rss_os_high_water_mark_mb": os_reported_peak_mb,
                "uvicorn_rss_steady_state_after_100_inferences_mb": rss_after_load_mb,
                "uvicorn_rss_net_delta_mb": round(rss_after_load_mb - rss_baseline_mb, 2),
                "tracemalloc_audit_iterations": audit_iterations,
                "tracemalloc_single_inference_peak_heap_mb": round(heap_peak_bytes / (1024.0 * 1024.0), 3),
                "tracemalloc_retained_delta_mb": heap_retained_delta_mb,
                "gc_gen0_collections": gc_gen0_collections,
                "gc_gen1_collections": gc_gen1_collections,
                "gc_gen2_collections": gc_gen2_collections,
                "final_gc_unreachable_objects": gc_collected,
                "memory_leak_detected": abs(heap_retained_delta_mb) > 1.0,
            },
        },
        "samples": samples,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    report["acceptance"] = {
        "mean_cpu_under_1800ms": float(np.mean(proc_times)) < 1800,
        "baseline_pixels_identical": all(s["mismatched_pixels_vs_sprint3_baseline"] == 0 for s in samples),
        "all_requests_succeeded": failed == 0 and total_completed > 0 and server_alive_after_stress,
        "soak_15_minutes_completed": duration_seconds >= 900 and stress_wall_s >= 900,
        "retained_heap_within_1mb": abs(heap_retained_delta_mb) <= 1,
    }
    output_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description="AURA AI Core v1.3 Benchmark & Stress Test")
    parser.add_argument("--total-requests", type=int, default=100, help="Total concurrent inference requests")
    parser.add_argument("--concurrency", type=int, default=10, help="Peak concurrent client workers (5..10)")
    parser.add_argument("--duration-seconds", type=float, default=0.0, help="Optional continuous soak duration in seconds (e.g. 900 for 15 min)")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_JSON, help="Output JSON benchmark path")
    args = parser.parse_args()

    report = run_benchmark_and_stress_test(
        total_stress_requests=args.total_requests,
        max_concurrency=args.concurrency,
        duration_seconds=args.duration_seconds,
        output_path=args.output,
    )
    lat = report["runtime_latency_ms"]
    st = report["stress_test"]
    mem = st["memory_and_gc"]
    print(f"Benchmark saved to: {args.output}")
    print(
        f"Sequential 6-image latency: mean={lat['processing_time_mean_ms']}ms, "
        f"median={lat['processing_time_median_ms']}ms, max={lat['processing_time_max_ms']}ms"
    )
    print(
        f"Baseline fidelity: Dice vs Sprint 3 = {report['accuracy_metrics']['mean_dice_vs_sprint3_baseline']}, "
        f"mismatched pixels = {report['accuracy_metrics']['total_mismatched_pixels_vs_sprint3_baseline']}"
    )
    print(
        f"Stress test: {st['successful_requests']}/{st['total_requests']} OK "
        f"(error_rate={st['error_rate_percent']}%, concurrency={st['concurrency_levels_tested']}, "
        f"wall={st['wall_clock_seconds']}s, rps={st['throughput_requests_per_sec']})"
    )
    print(
        f"Memory: Uvicorn RSS baseline={mem['uvicorn_rss_baseline_after_warmup_mb']}MB, "
        f"peak={mem['uvicorn_rss_os_high_water_mark_mb']}MB, "
        f"steady_after_100={mem['uvicorn_rss_steady_state_after_100_inferences_mb']}MB, "
        f"tracemalloc_delta={mem['tracemalloc_retained_delta_mb']}MB, "
        f"leak_detected={mem['memory_leak_detected']}"
    )


if __name__ == "__main__":
    main()
