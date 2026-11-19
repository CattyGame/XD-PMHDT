"""
Camera test client for the current AURA NiFi ingestion flow.

Default: prepare and describe test requests without sending.
--live: send requests to ListenHTTP.

HTTP 200 confirms ingress receipt only.
Inspect NiFi queues and quarantine files to verify routing.
This script does not verify Gateway, authentication, dedup or retry.
"""

import argparse
import base64
import hashlib
import hmac
import json
import os
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_IMAGE = REPO_ROOT / "datasets/CHASE_DB1/raw/Image_03L.jpg"
DEFAULT_PATIENT = "11111111-1111-4111-8111-111111111111"


def json_bytes(payload):
    return json.dumps(
        payload, ensure_ascii=False, separators=(",", ":")
    ).encode("utf-8")


def build_cases(image_bytes, patient_id, tenant_id=DEFAULT_PATIENT, clinic_id=None,
                device_id="CAM-DEMO", device_key=""):
    image_base64 = base64.b64encode(image_bytes).decode("ascii")

    def payload():
        event = {
            "request_id": str(uuid.uuid4()),
            "patient_id": patient_id,
            "device_id": device_id,
            "tenant_id": tenant_id,
            "clinic_id": clinic_id,
            "version": 1,
            "modality": "FUNDUS",
            "eye_side": "left",
            "image_base64": image_base64,
        }
        fields = ("1", event["request_id"], tenant_id, clinic_id or "", patient_id,
                  device_id, event["modality"], event["eye_side"], hashlib.sha256(image_bytes).hexdigest())
        event["signature"] = hmac.digest(device_key.encode(), "\n".join(fields).encode(), "sha256").hex()
        return event

    cases = []

    valid = payload()
    cases.append((
        "ValidFundus",
        json_bytes(valid),
        valid["request_id"],
        "Accepted-Camera-Receipt after gateway admission",
    ))

    unsupported = payload()
    unsupported["modality"] = "OCT"
    cases.append((
        "UnsupportedModality",
        json_bytes(unsupported),
        unsupported["request_id"],
        "quarantine/UnsupportedModality",
    ))

    missing = payload()
    del missing["patient_id"]
    cases.append((
        "MissingPatient",
        json_bytes(missing),
        missing["request_id"],
        "quarantine/unmatched",
    ))

    missing_image = payload()
    del missing_image["image_base64"]
    cases.append((
        "MissingImage",
        json_bytes(missing_image),
        missing_image["request_id"],
        "quarantine/MISSING_IMAGE_BASE64",
    ))

    invalid_base64 = payload()
    invalid_base64["image_base64"] = "@@@@"
    cases.append((
        "InvalidBase64",
        json_bytes(invalid_base64),
        invalid_base64["request_id"],
        "quarantine/INVALID_BASE64",
    ))

    not_image = payload()
    not_image["image_base64"] = base64.b64encode(
        b"This is not an image"
    ).decode("ascii")
    cases.append((
        "NotAnImage",
        json_bytes(not_image),
        not_image["request_id"],
        "quarantine/INVALID_OR_UNSUPPORTED_IMAGE",
    ))

    cases.append((
        "InvalidJSON",
        b'{"request_id":',
        None,
        "quarantine/INVALID_JSON",
    ))

    return cases


def send_request(url, body, timeout):
    request = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, ""
    except urllib.error.HTTPError as error:
        return error.code, "HTTP error"
    except (urllib.error.URLError, TimeoutError, OSError) as error:
        return 0, str(error)


def main():
    parser = argparse.ArgumentParser(
        description="Prepare or send camera test requests to NiFi."
    )
    parser.add_argument("--live", action="store_true")
    parser.add_argument(
        "--image", type=Path, default=DEFAULT_IMAGE
    )
    parser.add_argument(
        "--patient-id", default=DEFAULT_PATIENT
    )
    parser.add_argument("--tenant-id", default=DEFAULT_PATIENT)
    parser.add_argument("--clinic-id")
    parser.add_argument("--device-id", default="CAM-DEMO")
    parser.add_argument(
        "--url", default="http://127.0.0.1:8081/ingest/camera"
    )
    parser.add_argument("--timeout", type=float, default=15.0)
    args = parser.parse_args()

    if args.timeout <= 0:
        parser.error("--timeout must be greater than zero")

    try:
        patient_id = str(uuid.UUID(args.patient_id))
        tenant_id = str(uuid.UUID(args.tenant_id))
        clinic_id = str(uuid.UUID(args.clinic_id)) if args.clinic_id else None
    except ValueError:
        parser.error("Patient, tenant and clinic selectors must be UUIDs.")

    if not args.image.is_file():
        parser.error(f"Image not found: {args.image}")

    if not 0 < args.image.stat().st_size <= 10 * 1024 * 1024:
        parser.error("Image must be nonempty and at most 10 MiB")

    image_bytes = args.image.read_bytes()

    is_png = image_bytes.startswith(b"\x89PNG\r\n\x1a\n")
    is_jpeg = image_bytes.startswith(b"\xff\xd8\xff")
    if not (is_png or is_jpeg):
        parser.error("Test image must have a PNG or JPEG signature")

    device_key = os.environ.get("AURA_CAMERA_DEVICE_KEY", "")
    if args.live and (args.tenant_id == DEFAULT_PATIENT or len(device_key.encode()) < 32):
        parser.error("Live mode requires the registered --tenant-id and AURA_CAMERA_DEVICE_KEY.")
    cases = build_cases(image_bytes, patient_id, tenant_id, clinic_id, args.device_id, device_key)

    print("Mode:", "LIVE INGRESS" if args.live else "DRY RUN")
    print("Patient UUID:", patient_id)
    print("The default patient UUID is synthetic, not a backend record.")
    print("Image:", args.image)
    print("Image bytes:", len(image_bytes))
    print("Expected destinations require manual NiFi inspection.")
    print()

    errors = 0
    received = 0

    for name, body, request_id, expected in cases:
        print(f"[{name}]")
        print("  Request ID:", request_id or "(invalid JSON)")
        print("  Expected destination:", expected)

        if args.live:
            status, error = send_request(args.url, body, args.timeout)
            print("  HTTP:", status)
            if status == 200:
                received += 1
                print("  RECEIVED by ingress; routing not verified.")
            else:
                errors += 1
                print("  SEND FAILED:", error)
        else:
            print("  Prepared bytes:", len(body))
            print("  Not sent.")
        print()

    if args.live:
        print(f"Ingress received: {received}/{len(cases)}")
        print("Inspect queues and quarantine to verify each case.")
    else:
        print(f"Prepared {len(cases)} cases. No requests were sent.")

    print("Gateway, service token, dedup and retry are NOT verified.")
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
