"""
AURA Camera Ingestion Pipeline Test & Simulation Harness (Module M4 - SCRUM-63 & SCRUM-290).

Provides:
1. Logic verification test suite mimicking NiFi pipeline rules (validation, deduplication, retry, quarantine, correlation headers).
2. Live HTTP test client for testing running Apache NiFi instance (http://localhost:8081/ingest/camera).

Note: Running in simulation mode (--mock) verifies pipeline business logic locally and does NOT
constitute proof that the live containerized Apache NiFi service is running. Live verification
requires a running NiFi instance with --live flag.
"""
import argparse
import base64
import json
import sys
import time
import urllib.error
import urllib.request
from typing import Dict, Any, Tuple, List, Optional

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

SAMPLE_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGA"
    "WjR9awAAAABJRU5ErkJggg=="
)


class MockNiFiPipeline:
    """
    Simulates Apache NiFi process group behavior as defined in infra/nifi/camera_ingestion_flow.json.
    """

    def __init__(self, gateway_fail_attempts: int = 0):
        self.seen_request_ids = set()
        self.quarantine_queue: List[Dict[str, Any]] = []
        self.gateway_forwarded: List[Dict[str, Any]] = []
        self.audit_log: List[str] = []
        self.gateway_fail_attempts = gateway_fail_attempts
        self.gateway_call_count = 0

    def process_camera_flowfile(self, payload: Dict[str, Any]) -> Tuple[str, str, Dict[str, Any]]:
        """
        Simulates:
        ListenHTTP -> EvaluateJsonPath -> RouteOnAttribute -> DetectDuplicate ->
        UpdateAttribute (trace headers) -> InvokeHTTP (with Retry & Quarantine)
        """
        req_id = payload.get("request_id", "").strip() if payload.get("request_id") else ""
        modality = payload.get("modality", "").strip().upper() if payload.get("modality") else ""
        b64 = payload.get("image_base64", "").strip() if payload.get("image_base64") else ""

        correlation_id = req_id if req_id else f"corr-anon-{int(time.time()*1000)}"
        idempotency_key = f"idemp-{req_id}" if req_id else f"idemp-anon-{int(time.time()*1000)}"

        # 1. Validation (RouteOnAttribute-Validation)
        if not req_id:
            reason = "MISSING_REQUEST_ID"
            entry = {
                "payload": payload,
                "reason": reason,
                "correlation_id": correlation_id,
                "timestamp": time.time(),
            }
            self.quarantine_queue.append(entry)
            return "QUARANTINE", reason, entry

        if modality == "OCT":
            reason = "UNSUPPORTED_MODALITY: OCT (Chỉ hỗ trợ FUNDUS)"
            entry = {
                "payload": payload,
                "reason": reason,
                "correlation_id": correlation_id,
                "timestamp": time.time(),
            }
            self.quarantine_queue.append(entry)
            return "QUARANTINE", reason, entry

        if modality != "FUNDUS" or not b64 or len(b64) < 16:
            reason = f"INVALID_PAYLOAD: modality '{modality}', image_len {len(b64)}"
            entry = {
                "payload": payload,
                "reason": reason,
                "correlation_id": correlation_id,
                "timestamp": time.time(),
            }
            self.quarantine_queue.append(entry)
            return "QUARANTINE", reason, entry

        # 2. Deduplication (DetectDuplicate-RequestId via DistributedMapCacheClient)
        if req_id in self.seen_request_ids:
            reason = f"DUPLICATE_DETECTED: request_id '{req_id}' đã tồn tại trong 24h"
            entry = {
                "payload": payload,
                "reason": reason,
                "correlation_id": correlation_id,
                "timestamp": time.time(),
            }
            self.quarantine_queue.append(entry)
            return "QUARANTINE", reason, entry

        self.seen_request_ids.add(req_id)

        # 3. Add Trace Headers (UpdateAttribute-AddTraceHeaders)
        headers = {
            "X-Correlation-Id": correlation_id,
            "Idempotency-Key": idempotency_key,
            "X-Device-Id": str(payload.get("device_id", "UNKNOWN")),
        }

        # 4. Forward to Gateway & Retry Backoff (InvokeHTTP & RetryFlowFile)
        max_retries = 3
        retry_count = 0
        while retry_count <= max_retries:
            self.gateway_call_count += 1
            if self.gateway_call_count <= self.gateway_fail_attempts:
                # Simulated Gateway 503 / Timeout
                retry_count += 1
                if retry_count > max_retries:
                    reason = f"RETRIES_EXCEEDED: Gateway lỗi sau {max_retries} lần thử"
                    entry = {
                        "payload": payload,
                        "reason": reason,
                        "correlation_id": correlation_id,
                        "retries": retry_count,
                        "timestamp": time.time(),
                    }
                    self.quarantine_queue.append(entry)
                    return "QUARANTINE", reason, entry
                continue

            # Gateway Success (202 Accepted)
            forward_data = {
                "requestId": req_id,
                "patientId": payload.get("patient_id"),
                "deviceId": payload.get("device_id"),
                "modality": modality,
                "eyeSide": payload.get("eye_side", "unknown"),
                "imagePayload": b64,
                "headers": headers,
                "timestamp": time.time(),
            }
            self.gateway_forwarded.append(forward_data)
            self.audit_log.append(f"SUCCESS: Forwarded {req_id} [CorrId: {correlation_id}]")
            return "SUCCESS", f"Forwarded to Gateway (202 Accepted) [Retries: {retry_count}]", forward_data


def run_simulation_tests():
    print("=" * 75)
    print("AURA Camera Ingestion - Logic Verification Test Suite (SCRUM-63 & SCRUM-290)")
    print("Mode: Local Mock Simulator (Logic Verification)")
    print("=" * 75)

    nifi = MockNiFiPipeline(gateway_fail_attempts=0)

    # Scenario 1: Valid Fundus Image
    p1 = {
        "request_id": "cam_req_test_001",
        "patient_id": "PAT-001",
        "device_id": "TOPCON-TRC-NW400",
        "modality": "FUNDUS",
        "eye_side": "right",
        "image_base64": SAMPLE_PNG_BASE64,
    }
    route, msg, data = nifi.process_camera_flowfile(p1)
    print(f"[Scenario 1] Valid Fundus Upload          -> Route: {route} | Msg: {msg}")
    assert route == "SUCCESS", "Scenario 1 should succeed"
    assert data["headers"]["X-Correlation-Id"] == "cam_req_test_001"
    assert data["headers"]["Idempotency-Key"] == "idemp-cam_req_test_001"

    # Scenario 2: Duplicate Request ID
    route, msg, _ = nifi.process_camera_flowfile(p1)
    print(f"[Scenario 2] Duplicate Upload Prevention    -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE" and "DUPLICATE" in msg, "Scenario 2 should quarantine duplicate"

    # Scenario 3: OCT Modality (Unsupported)
    p3 = {
        "request_id": "cam_req_test_002",
        "patient_id": "PAT-002",
        "device_id": "ZEISS-CIRRUS-OCT",
        "modality": "OCT",
        "eye_side": "left",
        "image_base64": SAMPLE_PNG_BASE64,
    }
    route, msg, _ = nifi.process_camera_flowfile(p3)
    print(f"[Scenario 3] Unsupported OCT Modality      -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE" and "UNSUPPORTED_MODALITY" in msg, "Scenario 3 should quarantine OCT"

    # Scenario 4: Missing Request ID / Invalid Payload
    p4 = {
        "request_id": "",
        "patient_id": "PAT-003",
        "modality": "FUNDUS",
        "image_base64": SAMPLE_PNG_BASE64,
    }
    route, msg, _ = nifi.process_camera_flowfile(p4)
    print(f"[Scenario 4] Missing Request ID / Payload  -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE" and "MISSING_REQUEST_ID" in msg

    # Scenario 5: Gateway Transient Failure & Retry with Backoff
    nifi_retry = MockNiFiPipeline(gateway_fail_attempts=2)
    p5 = {
        "request_id": "cam_req_test_005",
        "patient_id": "PAT-005",
        "device_id": "CANON-CR2",
        "modality": "FUNDUS",
        "eye_side": "right",
        "image_base64": SAMPLE_PNG_BASE64,
    }
    route, msg, _ = nifi_retry.process_camera_flowfile(p5)
    print(f"[Scenario 5] Gateway Retry Recovered       -> Route: {route} | Msg: {msg}")
    assert route == "SUCCESS" and "Retries: 2" in msg

    # Scenario 6: Gateway Outage (Exceeds Max Retries -> Quarantine)
    nifi_outage = MockNiFiPipeline(gateway_fail_attempts=10)
    p6 = {
        "request_id": "cam_req_test_006",
        "patient_id": "PAT-006",
        "device_id": "CANON-CR2",
        "modality": "FUNDUS",
        "eye_side": "left",
        "image_base64": SAMPLE_PNG_BASE64,
    }
    route, msg, _ = nifi_outage.process_camera_flowfile(p6)
    print(f"[Scenario 6] Gateway Max Retries Exceeded  -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE" and "RETRIES_EXCEEDED" in msg

    print("-" * 75)
    print(f"Summary: Forwarded: {len(nifi.gateway_forwarded) + 1} | Quarantined: {len(nifi.quarantine_queue) + 1}")
    print("[LOGIC SIMULATION COMPLETED] Camera ingestion logic verified locally.")
    print("Note: Live container verification is performed by running with --live when NiFi container is active.")
    print("=" * 75)


def send_live_request(url: str, payload: Dict[str, Any]) -> Tuple[int, str]:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status, resp.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", errors="replace")
    except Exception as e:
        return 0, str(e)


def run_live_tests(target_url: str):
    print("=" * 75)
    print(f"AURA Camera Ingestion - Live Container Test against {target_url}")
    print("=" * 75)

    p1 = {
        "request_id": f"live_cam_{int(time.time())}",
        "patient_id": "PAT-LIVE-001",
        "device_id": "TOPCON-TRC-LIVE",
        "modality": "FUNDUS",
        "eye_side": "right",
        "image_base64": SAMPLE_PNG_BASE64,
    }

    code, body = send_live_request(target_url, p1)
    print(f"[Live Test 1] Send Valid Fundus Image -> HTTP Status: {code} | Body: {body[:100]}")
    if code == 200:
        print("[SUCCESS] Live NiFi service is reachable and accepted the camera payload.")
    else:
        print(f"[WARNING] Live NiFi returned status {code} (ensure NiFi container is running on {target_url}).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Test and simulate NiFi camera ingestion pipeline")
    parser.add_argument("--live", action="store_true", help="Send actual HTTP requests to live NiFi service")
    parser.add_argument("--url", default="http://localhost:8081/ingest/camera", help="Live NiFi endpoint URL")
    args = parser.parse_args()

    if args.live:
        run_live_tests(args.url)
    else:
        run_simulation_tests()
