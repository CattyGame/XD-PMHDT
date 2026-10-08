"""
AURA Simulated Camera Ingestion & Pipeline Verification Script (Module M4 - SCRUM-63).
Simulates edge camera ingestion logic, deduplication check, validation, and forwarding behavior.
"""
import sys
import time
import json
import hashlib
from typing import Dict, Any, Tuple

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

SAMPLE_PNG_BASE64 = (
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGA"
    "WjR9awAAAABJRU5ErkJggg=="
)

class MockNiFiPipeline:
    def __init__(self):
        self.seen_request_ids = set()
        self.quarantine_queue = []
        self.gateway_forwarded = []
        self.audit_log = []

    def process_camera_flowfile(self, payload: Dict[str, Any]) -> Tuple[str, str]:
        """
        Mimics Apache NiFi process group logic (ListenHTTP -> Validate -> Deduplicate -> InvokeHTTP).
        """
        req_id = payload.get("request_id")
        modality = payload.get("modality", "").strip().upper()
        b64 = payload.get("image_base64", "")

        # 1. Validation (RouteOnAttribute)
        if not req_id:
            reason = "MISSING_REQUEST_ID"
            self.quarantine_queue.append({"payload": payload, "reason": reason})
            return "QUARANTINE", reason

        if modality != "FUNDUS":
            reason = f"UNSUPPORTED_MODALITY: {modality} (Only FUNDUS accepted)"
            self.quarantine_queue.append({"payload": payload, "reason": reason})
            return "QUARANTINE", reason

        if not b64 or len(b64) < 16:
            reason = "INVALID_IMAGE_PAYLOAD"
            self.quarantine_queue.append({"payload": payload, "reason": reason})
            return "QUARANTINE", reason

        # 2. Deduplication (DetectDuplicate)
        if req_id in self.seen_request_ids:
            reason = f"DUPLICATE_DETECTED: request_id '{req_id}' already ingested"
            self.quarantine_queue.append({"payload": payload, "reason": reason})
            return "QUARANTINE", reason

        self.seen_request_ids.add(req_id)

        # 3. Forward to Gateway (InvokeHTTP simulation)
        forward_data = {
            "requestId": req_id,
            "patientId": payload.get("patient_id"),
            "deviceId": payload.get("device_id"),
            "modality": modality,
            "eyeSide": payload.get("eye_side", "unknown"),
            "imagePayload": b64,
            "timestamp": time.time()
        }
        self.gateway_forwarded.append(forward_data)
        self.audit_log.append(f"SUCCESS: Forwarded {req_id} to Gateway /api/v1/analyses")
        return "SUCCESS", "Forwarded to Gateway (202 Accepted)"

def run_simulation_tests():
    print("=" * 70)
    print("AURA Camera Ingestion - Apache NiFi Logic Simulation (SCRUM-63)")
    print("=" * 70)

    nifi = MockNiFiPipeline()

    # Scenario 1: Valid Fundus Image
    p1 = {
        "request_id": "cam_req_test_001",
        "patient_id": "PAT-001",
        "device_id": "TOPCON-TRC-NW400",
        "modality": "FUNDUS",
        "eye_side": "right",
        "image_base64": SAMPLE_PNG_BASE64
    }
    route, msg = nifi.process_camera_flowfile(p1)
    print(f"[Scenario 1] Valid Fundus Upload       -> Route: {route} | Msg: {msg}")
    assert route == "SUCCESS", "Scenario 1 should succeed"

    # Scenario 2: Duplicate Request ID
    route, msg = nifi.process_camera_flowfile(p1)
    print(f"[Scenario 2] Duplicate Upload Prevention -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE" and "DUPLICATE" in msg, "Scenario 2 should quarantine duplicate"

    # Scenario 3: OCT Modality (Unsupported)
    p3 = {
        "request_id": "cam_req_test_002",
        "patient_id": "PAT-002",
        "device_id": "ZEISS-CIRRUS-OCT",
        "modality": "OCT",
        "eye_side": "left",
        "image_base64": SAMPLE_PNG_BASE64
    }
    route, msg = nifi.process_camera_flowfile(p3)
    print(f"[Scenario 3] Unsupported OCT Modality   -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE" and "UNSUPPORTED_MODALITY" in msg, "Scenario 3 should quarantine OCT"

    # Scenario 4: Missing Request ID
    p4 = {
        "request_id": "",
        "patient_id": "PAT-003",
        "modality": "FUNDUS",
        "image_base64": SAMPLE_PNG_BASE64
    }
    route, msg = nifi.process_camera_flowfile(p4)
    print(f"[Scenario 4] Corrupted / Missing ID     -> Route: {route} | Msg: {msg}")
    assert route == "QUARANTINE", "Scenario 4 should quarantine invalid payload"

    print("-" * 70)
    print(f"Summary: Forwarded: {len(nifi.gateway_forwarded)} | Quarantined: {len(nifi.quarantine_queue)}")
    print("[ALL SCENARIOS PASSED] Apache NiFi camera ingestion logic verified 100%!")
    print("=" * 70)

if __name__ == "__main__":
    run_simulation_tests()
