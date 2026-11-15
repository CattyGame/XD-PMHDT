"""Bounded, isolated Docker acceptance for M4; uses public CHASE images and synthetic service keys."""
from __future__ import annotations

import argparse
import base64
from concurrent.futures import ThreadPoolExecutor
import csv
import hashlib
import io
import json
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import urllib.request
import uuid

import jsonschema
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
AI = ROOT / "src/ai-core"
sys.path.insert(0, str(AI))
from scripts import baseline_benchmark as baseline
from scripts.stress_test_ai_core import compute_reference_s3_mask, http_post_json
from app.services.frangi_service import load_frangi_config
from app.auth.service_jwt import DEFAULT_AUDIENCE, DEFAULT_ISSUER, DEFAULT_REQUIRED_SCOPE
from tests.test_frangi_sprint3 import EXPECTED_CLIENT_ID, VALID_SUB, generate_deterministic_rsa_keypair, sign_rs256_jwt


def docker(*arguments):
    return subprocess.run(["docker", *map(str, arguments)], check=True, capture_output=True,
                          text=True, encoding="utf-8", errors="replace", timeout=120).stdout


def cgroup(container):
    code = """import json,pathlib
p=pathlib.Path('/sys/fs/cgroup')
print(json.dumps({n:(p/n).read_text().strip() for n in ['memory.current','memory.peak','memory.max','memory.events','cpu.stat','cpu.max']}))"""
    return json.loads(docker("exec", container, "python", "-c", code))


def acceptance(image: str, output: Path, duration: float, cpus: float, memory: str):
    if duration <= 0 or cpus <= 0:
        raise ValueError("duration and cpus must be positive")
    output.mkdir(parents=True, exist_ok=False)
    report = {"status": "RUNNING", "image": image, "scope": "Docker image, public CHASE test split, RS256, HTTP, cgroup v2",
              "requested_duration_seconds": duration, "telemetry": []}
    name = f"aura-m4-accept-{uuid.uuid4().hex[:10]}"
    key = generate_deterministic_rsa_keypair(seed=20261111, bits=2048)
    config = load_frangi_config(AI / "config/ai-frangi-v1.3.yaml")
    rows = list(csv.DictReader(baseline.MANIFEST.open(encoding="utf-8-sig")))
    samples = []
    for row in rows:
        if row["split"] != "test":
            continue
        path = baseline.read_verified(row, "image")
        raw = path.read_bytes()
        with Image.open(io.BytesIO(raw)) as source:
            rgb = np.asarray(source.convert("RGB"))
        samples.append({"name": path.name, "rgb": rgb, "sha256": hashlib.sha256(raw).hexdigest(),
                        "base64": base64.b64encode(raw).decode("ascii"),
                        "reference": compute_reference_s3_mask(rgb, config)})
    if len(samples) != 6:
        raise ValueError("The frozen CHASE test split must contain six images.")
    with socket.socket() as temporary:
        temporary.bind(("127.0.0.1", 0))
        port = temporary.getsockname()[1]
    base_url = f"http://127.0.0.1:{port}"
    token_state = {"minted": 0, "token": ""}
    lock = threading.Lock()

    def headers(cid):
        with lock:
            now = int(time.time())
            if now - token_state["minted"] >= 120:
                token_state.update(minted=now, token=sign_rs256_jwt(key,
                    header={"alg": "RS256", "typ": "JWT", "kid": key.key_id},
                    payload={"iss": DEFAULT_ISSUER, "aud": DEFAULT_AUDIENCE, "sub": VALID_SUB,
                             "client_id": EXPECTED_CLIENT_ID, "scope": DEFAULT_REQUIRED_SCOPE,
                             "iat": now, "nbf": now, "exp": now + 240}))
            return {"Authorization": f"Bearer {token_state['token']}", "X-Correlation-Id": cid}

    def request(sample, verify=False):
        request_id = str(uuid.uuid4())
        cid = f"m4-container-{request_id}"
        payload = {"request_id": request_id, "patient_id": None, "eye_side": "unknown", "mode": "retina_vessels",
                   "modality": "FUNDUS", "include_mask": True, "include_overlay": True, "image_base64": sample["base64"]}
        status, response_headers, body, wall = http_post_json(base_url + "/api/v1/analyze", payload, headers(cid))
        if status != 200 or response_headers.get("x-correlation-id") != cid or body.get("request_id") != request_id or body.get("is_mock") is not False:
            raise AssertionError(f"Invalid HTTP/correlation/identity response: {status} {body.get('error_code')}")
        if verify:
            schema = json.loads((ROOT / "contracts/ai-response-1.3.schema.json").read_text(encoding="utf-8"))
            jsonschema.Draft202012Validator(schema, format_checker=jsonschema.FormatChecker()).validate(body)
            with Image.open(io.BytesIO(base64.b64decode(body["segmentation"]["mask_base64"]))) as mask:
                if mask.mode != "L" or not np.array_equal(np.asarray(mask) > 0, sample["reference"]):
                    raise AssertionError("Mask drift from frozen Sprint 3 baseline")
            expected_overlay = sample["rgb"].copy()
            expected_overlay[sample["reference"]] = [0, 255, 128]
            with Image.open(io.BytesIO(base64.b64decode(body["segmentation"]["overlay_base64"]))) as overlay:
                if overlay.mode != "RGB" or not np.array_equal(np.asarray(overlay), expected_overlay):
                    raise AssertionError("Overlay drift from frozen Sprint 3 baseline")
        return {"request_id": request_id, "image_sha256": sample["sha256"], "http_ms": wall,
                "processing_time_ms": body["processing_time_ms"]}

    started = False
    try:
        docker("run", "--detach", "--name", name, "--label", "aura.acceptance=m4", "--cpus", cpus,
               "--memory", memory, "--pids-limit", "128", "--read-only", "--tmpfs", "/tmp:rw,size=128m",
               "--publish", f"127.0.0.1:{port}:8000", "--env", "AURA_AI_ENGINE=frangi",
               "--env", "AURA_ALLOW_MOCK=false", "--env", "AURA_FRANGI_CONFIG_PATH=/app/config/ai-frangi-v1.3.yaml",
               "--env", f"AURA_SERVICE_AUTH_PUBLIC_KEY_PEM={key.public_pem}", image)
        started = True
        for _ in range(120):
            try:
                with urllib.request.urlopen(base_url + "/ready", timeout=2) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(0.5)
        else:
            raise RuntimeError("Container did not become ready.")
        inspection = json.loads(docker("inspect", name))[0]
        report["container"] = {"image_id": inspection["Image"], "memory_limit": inspection["HostConfig"]["Memory"],
                               "nano_cpus": inspection["HostConfig"]["NanoCpus"], "command": inspection["Config"]["Cmd"]}
        for item in samples[:2]:
            request(item)
        report["sequential"] = [request(sample, verify=True) for sample in samples]
        report["baseline_fidelity"] = "six masks and RGB overlays pixel-identical; response contract 1.3 verified"
        with urllib.request.urlopen(base_url + "/metrics", timeout=5) as response:
            (output / "metrics-before.txt").write_bytes(response.read())
        began = time.perf_counter()
        completed = 0
        failures = []
        latest_telemetry = 0
        latencies = []
        with ThreadPoolExecutor(max_workers=10) as pool:
            while time.perf_counter() - began < duration:
                elapsed = time.perf_counter() - began
                concurrency = 5 if elapsed < duration / 2 else 10
                futures = [pool.submit(request, samples[(completed + index) % len(samples)]) for index in range(concurrency)]
                for future in futures:
                    try:
                        latencies.append(future.result()["http_ms"])
                    except Exception as error:
                        failures.append({"type": type(error).__name__, "message": str(error)[:250]})
                    completed += 1
                if elapsed - latest_telemetry >= 10 or latest_telemetry == 0:
                    telemetry = {"elapsed_seconds": round(elapsed, 2), "completed": completed, "failed": len(failures), **cgroup(name)}
                    report["telemetry"].append(telemetry)
                    (output / "progress.json").write_text(json.dumps(telemetry, indent=2), encoding="utf-8")
                    latest_telemetry = elapsed
        report["load"] = {"requests": completed, "successful": len(latencies), "failures": failures,
                          "seconds": round(time.perf_counter() - began, 2), "concurrency_levels": [5, 10],
                          "http_p95_ms": float(np.percentile(latencies, 95)) if latencies else None}
        time.sleep(2)
        report["final_cgroup"] = cgroup(name)
        with urllib.request.urlopen(base_url + "/metrics", timeout=5) as response:
            (output / "metrics-after.txt").write_bytes(response.read())
        with urllib.request.urlopen(base_url + "/ready", timeout=5) as response:
            assert response.status == 200
        state = json.loads(docker("inspect", name))[0]
        report["acceptance"] = {
            "all_requests_succeeded": not failures,
            "sustained_900_seconds": report["load"]["seconds"] >= 900,
            "mean_sequential_under_1800ms": np.mean([r["processing_time_ms"] for r in report["sequential"]]) < 1800,
            "no_oom_or_restart": not state["State"]["OOMKilled"] and state["RestartCount"] == 0
                                  and "oom_kill 0" in report["final_cgroup"]["memory.events"],
        }
        report["acceptance"] = {key: bool(value) for key, value in report["acceptance"].items()}
        report["status"] = "MEASURED"
    except Exception as error:
        report["status"] = "FAILED"
        report["error"] = {"type": type(error).__name__, "message": str(error)[:400]}
        raise
    finally:
        if started:
            try:
                logs = subprocess.run(["docker", "logs", name], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                      text=True, encoding="utf-8", errors="replace", timeout=30)
                (output / "server.log").write_text(logs.stdout, encoding="utf-8")
            finally:
                docker("rm", "--force", name)
        (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if not report["acceptance"]["all_requests_succeeded"] or not report["acceptance"]["no_oom_or_restart"]:
        raise RuntimeError("Container acceptance failed; inspect report.json")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--duration", type=float, default=900)
    parser.add_argument("--cpus", type=float, default=4)
    parser.add_argument("--memory", default="2g")
    arguments = parser.parse_args()
    result = acceptance(arguments.image, arguments.output, arguments.duration, arguments.cpus, arguments.memory)
    print(json.dumps(result["acceptance"]))
