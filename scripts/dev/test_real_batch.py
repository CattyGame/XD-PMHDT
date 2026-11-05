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
    p.add_argument("--patient", required=True)
    p.add_argument("--images", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--count", type=int, default=100)
    p.add_argument("--timeout", type=int, default=1800)
    p.add_argument("--containers", nargs="+", required=True)
    args = p.parse_args()
    token = os.environ.get("AURA_TEST_FIREBASE_TOKEN")
    url = urllib.parse.urlparse(args.url)
    if not token or not (url.scheme == "https" or url.scheme == "http" and url.hostname in ("localhost", "127.0.0.1")) or url.query or url.fragment or url.username:
        raise ValueError("Explicit Firebase token and HTTPS (or loopback HTTP) origin required")
    uuid.UUID(args.patient)
    if not 1 <= args.count <= 100:
        raise ValueError("count must be 1..100")
    files = sorted(f for f in args.images.iterdir() if f.suffix.lower() in (".png", ".jpg", ".jpeg"))
    if len(files) != args.count:
        raise ValueError("Dataset must contain exactly count real images")
    items = []
    hashes = set()
    for f in files:
        if not 0 < f.stat().st_size <= 10 * 1024 * 1024:
            raise ValueError("Image outside per-image byte limit")
        digest = hashlib.sha256(f.read_bytes()).hexdigest()
        if digest in hashes:
            raise ValueError("Dataset contains duplicate bytes; use distinct images")
        hashes.add(digest)
        items.append(dict(itemId=str(uuid.uuid4()), patientId=args.patient, fileName=f.name,
                          sha256=digest, modality="FUNDUS", eyeSide="unknown"))
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
        inventory = request(base, token, "/api/v1/analysis-upload-batches", "POST", json.dumps({"items": items}).encode(), key=str(uuid.uuid4()))
        batch = str(uuid.UUID(inventory["batchId"]))
        evidence["batchId"] = batch
        if len(inventory["items"]) != len(items) or any(i["uploadStatus"] != "DECLARED" for i in inventory["items"]):
            raise RuntimeError("Admission rejected one or more images")
        def upload(pair):
            item, file = pair
            boundary = "aura-" + uuid.uuid4().hex
            mime = "image/png" if file.suffix.lower() == ".png" else "image/jpeg"
            body = (f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="image{file.suffix.lower()}"\r\nContent-Type: {mime}\r\n\r\n'.encode()
                    + file.read_bytes() + f"\r\n--{boundary}--\r\n".encode())
            return request(base, token, f"/api/v1/analysis-upload-batches/{batch}/items/{item['itemId']}", "PUT", body, "multipart/form-data; boundary=" + boundary, item["itemId"])
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            futures = [executor.submit(upload, pair) for pair in zip(items, files)]
            while not all(f.done() for f in futures):
                sample()
                time.sleep(1)
            evidence["uploaded"] = [f.result()["itemId"] for f in futures]
        request(base, token, f"/api/v1/analysis-upload-batches/{batch}/finalize", "POST", b"{}", key=str(uuid.uuid4()))
        while time.monotonic() - started < args.timeout:
            sample()
            result = request(base, token, f"/api/v1/batches/{batch}")
            evidence["outcome"] = result
            summary = result["summary"]
            if summary["total"] == len(items) and summary["active"] == 0:
                if summary["succeeded"] != len(items) or summary["failed"] or summary["duplicate"]:
                    raise RuntimeError("Not every image completed successfully")
                break
            time.sleep(2)
        else:
            raise TimeoutError("Pipeline did not reach terminal outcomes")
        states = json.loads(subprocess.run(["docker", "inspect", *args.containers], capture_output=True, text=True, check=True).stdout)
        if any(s["State"]["OOMKilled"] or s["State"]["ExitCode"] == 137 or not s["State"]["Running"] for s in states):
            raise RuntimeError("Container stopped or was OOMKilled")
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
