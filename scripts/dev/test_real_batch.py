"""Explicit live acceptance: real image admission, bounded uploads and terminal outcomes."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import ssl
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


def request(base, token, path, method="GET", body=None, content_type="application/json", key=None):
    headers = {"Authorization": "Bearer " + token, "Content-Type": content_type}
    if key:
        headers["Idempotency-Key"] = key
    req = urllib.request.Request(base + path, data=body, method=method, headers=headers)
    # Never forward bearer credentials through a redirect.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args):
            return None
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    with opener.open(req, timeout=120) as response:
        content = response.read()
        return json.loads(content) if content else None


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--url", required=True)
    p.add_argument("--patient", help="Fallback patient GUID when --manifest is not supplied")
    p.add_argument("--manifest", type=Path, help="JSON array of {fileName, patientId, expectedStatus} records")
    p.add_argument("--resume-batch-id", help="Resume an existing parent batch by re-reading its server inventory")
    p.add_argument("--stop-after-uploads", type=int, help="Stop before finalize after N uploads to exercise resume")
    p.add_argument("--expected-succeeded", type=int)
    p.add_argument("--expected-failed", type=int)
    p.add_argument("--postgres-container", help="Optional migrated PostgreSQL container for financial reconciliation")
    p.add_argument("--require-financial-reconciliation", action="store_true")
    p.add_argument("--images", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--count", type=int, default=100)
    p.add_argument("--timeout", type=int, default=1800)
    p.add_argument("--containers", nargs="+", required=True)
    args = p.parse_args()
    token = os.environ.get("AURA_TEST_FIREBASE_TOKEN")
    if args.require_financial_reconciliation and not args.postgres_container:
        raise ValueError("--require-financial-reconciliation requires --postgres-container")
    url = urllib.parse.urlparse(args.url)
    if not token or not (url.scheme == "https" or url.scheme == "http" and url.hostname in ("localhost", "127.0.0.1")) or url.query or url.fragment or url.username:
        raise ValueError("Explicit Firebase token and HTTPS (or loopback HTTP) origin required")
    if args.patient:
        uuid.UUID(args.patient)
    if not 1 <= args.count <= 100:
        raise ValueError("count must be 1..100")
    files = sorted(f for f in args.images.iterdir() if f.suffix.lower() in (".png", ".jpg", ".jpeg"))
    if len(files) != args.count:
        raise ValueError("Dataset must contain exactly count real images")
    manifest = {}
    if args.manifest:
        records = json.loads(args.manifest.read_text(encoding="utf-8"))
        if not isinstance(records, list):
            raise ValueError("Manifest must be a JSON array")
        for record in records:
            name = record.get("fileName")
            patient_id = str(uuid.UUID(record.get("patientId", "")))
            expected = record.get("expectedStatus", "SUCCEEDED").upper()
            if expected not in ("SUCCEEDED", "FAILED") or name in manifest:
                raise ValueError("Manifest filenames must be unique and expectedStatus must be SUCCEEDED or FAILED")
            manifest[name] = {"patientId": patient_id, "expectedStatus": expected}
        if set(manifest) != {f.name for f in files}:
            raise ValueError("Manifest filenames must match the image directory exactly")
    elif not args.patient:
        raise ValueError("Specify --patient or --manifest")
    if (args.expected_succeeded is None) != (args.expected_failed is None):
        raise ValueError("Specify both expected outcome counts or neither")
    expected_succeeded = args.expected_succeeded if args.expected_succeeded is not None else args.count
    expected_failed = args.expected_failed if args.expected_failed is not None else 0
    if expected_succeeded + expected_failed != args.count:
        raise ValueError("Expected success and failure counts must add up to count")
    if args.stop_after_uploads is not None and not 1 <= args.stop_after_uploads < args.count:
        raise ValueError("stop-after-uploads must be between 1 and count-1")
    items = []
    hashes = set()
    for f in files:
        if not 0 < f.stat().st_size <= 10 * 1024 * 1024:
            raise ValueError("Image outside per-image byte limit")
        digest = hashlib.sha256(f.read_bytes()).hexdigest()
        if digest in hashes:
            raise ValueError("Dataset contains duplicate bytes; use distinct images")
        hashes.add(digest)
        item = manifest.get(f.name, {"patientId": args.patient, "expectedStatus": "SUCCEEDED"})
        items.append(dict(itemId=str(uuid.uuid4()), patientId=item["patientId"], fileName=f.name,
                          sha256=digest, modality="FUNDUS", eyeSide="unknown",
                          expectedStatus=item["expectedStatus"]))
    evidence = {"status": "RUNNING", "declared": len(items), "samples": []}
    started = time.monotonic()
    base = args.url.rstrip("/")
    def sample():
        result = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{json .}}", *args.containers], capture_output=True, text=True, check=True)
        rows = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        if len(rows) != len(args.containers):
            raise RuntimeError("Missing container resource samples")
        evidence["samples"].append(rows)
        if any(float(row["MemPerc"].rstrip("%")) >= 80 for row in rows):
            raise RuntimeError("Observed memory >=80%")
    try:
        sample()
        if args.resume_batch_id:
            batch = str(uuid.UUID(args.resume_batch_id))
            inventory = request(base, token, f"/api/v1/analysis-upload-batches/{batch}")
            server_items = {entry["sha256"].lower(): entry for entry in inventory["items"]}
            if len(server_items) != len(items) or set(server_items) != hashes:
                raise RuntimeError("Resume inventory does not match the selected image bytes")
            for item in items:
                server = server_items[item["sha256"]]
                if server["patientId"] != item["patientId"] or server["fileName"] != item["fileName"]:
                    raise RuntimeError("Resume inventory patient or filename does not match the manifest")
                item["itemId"] = server["itemId"]
                item["uploadStatus"] = server["uploadStatus"]
        else:
            inventory = request(base, token, "/api/v1/analysis-upload-batches", "POST", json.dumps({"items": [{k:v for k,v in item.items() if k != "expectedStatus"} for item in items]}).encode(), key=str(uuid.uuid4()))
            batch = str(uuid.UUID(inventory["batchId"]))
            for item, admitted in zip(items, inventory["items"]):
                if item["itemId"] != admitted["itemId"] or item["sha256"] != admitted["sha256"]:
                    raise RuntimeError("Admission inventory changed the declared item identity")
                item["uploadStatus"] = admitted["uploadStatus"]
        evidence["batchId"] = batch
        if len(inventory["items"]) != len(items) or any(i["uploadStatus"] not in ("DECLARED", "STAGED", "UNKNOWN", "ACCEPTED") for i in items):
            raise RuntimeError("Admission rejected one or more images")
        def upload(pair):
            item, file = pair
            boundary = "aura-" + uuid.uuid4().hex
            mime = "image/png" if file.suffix.lower() == ".png" else "image/jpeg"
            body = (f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="image{file.suffix.lower()}"\r\nContent-Type: {mime}\r\n\r\n'.encode()
                    + file.read_bytes() + f"\r\n--{boundary}--\r\n".encode())
            return request(base, token, f"/api/v1/analysis-upload-batches/{batch}/items/{item['itemId']}", "PUT", body, "multipart/form-data; boundary=" + boundary, item["itemId"])
        pending = [(item, file) for item, file in zip(items, files) if item["uploadStatus"] == "DECLARED"]
        if args.stop_after_uploads is not None:
            pending = pending[:args.stop_after_uploads]
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            futures = [executor.submit(upload, pair) for pair in pending]
            while not all(f.done() for f in futures):
                sample()
                time.sleep(1)
            evidence["uploaded"] = []
            for (item, _), future in zip(pending, futures):
                receipt = future.result()
                item["uploadStatus"] = receipt["uploadStatus"]
                evidence["uploaded"].append(receipt["itemId"])
        if args.stop_after_uploads is not None:
            refreshed = request(base, token, f"/api/v1/analysis-upload-batches/{batch}")
            evidence["inventoryAfterInterruption"] = {
                "accepted": sum(i["uploadStatus"] == "ACCEPTED" for i in refreshed["items"]),
                "staged": sum(i["uploadStatus"] == "STAGED" for i in refreshed["items"]),
                "declared": sum(i["uploadStatus"] == "DECLARED" for i in refreshed["items"])}
            evidence["status"] = "INTERRUPTED"
            evidence["resumeBatchId"] = batch
            return
        still_declared = [i for i in items if i["uploadStatus"] == "DECLARED"]
        if still_declared:
            raise RuntimeError("Some items remain unaccepted; re-read inventory and resume before finalize")
        accepted = request(base, token, f"/api/v1/analysis-upload-batches/{batch}/finalize", "POST", b"{}", key=str(uuid.uuid4()))
        expected_by_analysis = {}
        expected_by_name = {item["fileName"]: item["expectedStatus"] for item in items}
        for receipt in accepted["items"]:
            if receipt.get("analysisId"):
                expected_by_analysis[receipt["analysisId"]] = expected_by_name[receipt["fileName"]]
        while time.monotonic() - started < args.timeout:
            sample()
            result = request(base, token, f"/api/v1/batches/{batch}")
            evidence["outcome"] = result
            summary = result["summary"]
            if summary["total"] == len(items) and summary["active"] == 0:
                if summary["succeeded"] != expected_succeeded or summary["failed"] != expected_failed or summary["duplicate"]:
                    raise RuntimeError("Terminal outcome counts do not match the acceptance manifest")
                break
            time.sleep(2)
        else:
            raise TimeoutError("Pipeline did not reach terminal outcomes")
        states = json.loads(subprocess.run(["docker", "inspect", *args.containers], capture_output=True, text=True, check=True).stdout)
        if any(s["State"]["OOMKilled"] or s["State"]["ExitCode"] == 137 or not s["State"]["Running"] for s in states):
            raise RuntimeError("Container stopped or was OOMKilled")
        evidence["status"] = "PASS_PIPELINE_ONLY"
        if args.postgres_container:
            sql = f"""
                SELECT i.id::text, r.id::text, r.state,
                       max(l.id::text) FILTER (WHERE l.entry_type='RESERVE'),
                       max(l.id::text) FILTER (WHERE l.entry_type='CAPTURE'),
                       max(l.id::text) FILTER (WHERE l.entry_type='RELEASE'),
                       max(l.amount) FILTER (WHERE l.entry_type='RESERVE'),
                       max(l.amount) FILTER (WHERE l.entry_type='CAPTURE'),
                       max(l.amount) FILTER (WHERE l.entry_type='RELEASE'),
                       w.id::text, w.balance,
                       (SELECT x.balance_after FROM billing.credit_ledger_entries x WHERE x.wallet_id=w.id ORDER BY x.created_at DESC,x.id DESC LIMIT 1)
                FROM analysis.images i
                LEFT JOIN billing.credit_reservations r ON r.analysis_id=i.id AND r.tenant_id=i.tenant_id
                LEFT JOIN billing.credit_ledger_entries l ON l.reservation_id=r.id
                LEFT JOIN billing.credit_wallets w ON w.id=r.wallet_id
                WHERE i.batch_id='{batch}'::uuid
                GROUP BY i.id,r.id,r.state,w.id,w.balance ORDER BY i.id;
            """
            result = subprocess.run(["docker", "exec", args.postgres_container, "psql", "-X", "-A", "-t", "-F", "\\t", "-U", "aura_dev", "-d", "aura_dev", "-c", sql], capture_output=True, text=True, check=True)
            ledger = []
            discrepancies = []
            for line in result.stdout.splitlines():
                fields = line.split("\t")
                if len(fields) != 12:
                    continue
                image_id, reservation_id, state, reserve_id, capture_id, release_id, reserve_amount, capture_amount, release_amount, wallet_id, balance, ledger_balance = fields
                expected = expected_by_analysis.get(image_id)
                row = {"analysisId": image_id, "reservationId": reservation_id or None, "state": state or None,
                       "reserveId": reserve_id or None, "captureId": capture_id or None, "releaseId": release_id or None,
                       "reserveAmount": int(reserve_amount) if reserve_amount else None,
                       "captureAmount": int(capture_amount) if capture_amount else None,
                       "releaseAmount": int(release_amount) if release_amount else None,
                       "expectedStatus": expected,
                       "walletId": wallet_id or None, "walletBalance": int(balance) if balance else None,
                       "latestLedgerBalance": int(ledger_balance) if ledger_balance else None}
                ledger.append(row)
                if row["walletBalance"] is not None and row["walletBalance"] != row["latestLedgerBalance"]:
                    discrepancies.append({"walletId": wallet_id, "walletBalance": row["walletBalance"], "ledgerBalance": row["latestLedgerBalance"]})
                expected_state = "CAPTURED" if expected == "SUCCEEDED" else "RELEASED"
                expected_amounts = (-1, 0, None) if expected_state == "CAPTURED" else (-1, None, 1)
                actual_amounts = (row["reserveAmount"], row["captureAmount"], row["releaseAmount"])
                if not row["reserveId"] or row["state"] != expected_state or actual_amounts != expected_amounts or (expected_state == "CAPTURED" and (not row["captureId"] or row["releaseId"])) or (expected_state == "RELEASED" and (not row["releaseId"] or row["captureId"])):
                    discrepancies.append({"analysisId": image_id, "expectedState": expected_state, "actualState": row["state"]})
            evidence["financialLedger"] = ledger
            evidence["financialDiscrepancies"] = discrepancies
            if len(ledger) != args.count or discrepancies or any(not row["reservationId"] or row["state"] not in ("CAPTURED", "RELEASED") for row in ledger):
                raise RuntimeError("Financial ledger is incomplete or wallet balance differs from latest ledger entry")
            evidence["status"] = "PASS"
    except Exception as error:
        evidence["status"] = "FAIL"
        evidence["errorType"] = type(error).__name__
        raise
    finally:
        evidence["elapsedSeconds"] = time.monotonic() - started
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(evidence, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
