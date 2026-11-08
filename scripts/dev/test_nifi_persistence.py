"""Restart an explicitly selected test NiFi and compare queued FlowFile UUIDs and bytes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.request
import uuid

from test_real_batch import request


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--url", required=True)
    p.add_argument("--container", required=True)
    p.add_argument("--connection", required=True)
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    uuid.UUID(args.connection)
    if not args.container.startswith("aura-test-"):
        raise ValueError("Only an explicit aura-test-* NiFi container may be restarted")
    container = json.loads(subprocess.run(["docker", "inspect", args.container], check=True, capture_output=True, text=True).stdout)[0]
    if "nifi" not in container["Config"]["Image"].lower():
        raise ValueError("Selected container is not NiFi")
    if not container["Mounts"]:
        raise ValueError("Persistent NiFi repositories must be mounted")
    token = os.environ.get("AURA_TEST_NIFI_TOKEN")
    if not token or not args.url.startswith("https://"):
        raise ValueError("Verified HTTPS NiFi endpoint and bearer token required")
    base = args.url.rstrip("/")
    connection = "/flowfile-queues/" + args.connection

    def snapshot():
        created = request(base, token, connection + "/listing-requests", "POST", b"{}")
        path = connection + "/listing-requests/" + created["listingRequest"]["id"]
        try:
            for _ in range(60):
                listing = request(base, token, path)["listingRequest"]
                if listing["finished"]:
                    break
                time.sleep(1)
            else:
                raise TimeoutError("Queue listing timed out")
            summaries = listing.get("flowFileSummaries", [])
            count = listing["queueSize"]["objectCount"]
            if count < 1 or len(summaries) != count:
                raise RuntimeError("Queue must be nonempty and listing must cover every FlowFile")
            result = {}
            for row in summaries:
                identifier = str(uuid.UUID(row["uuid"]))
                req = urllib.request.Request(base + connection + "/flowfiles/" + identifier + "/content", headers={"Authorization": "Bearer " + token})
                class NoRedirect(urllib.request.HTTPRedirectHandler):
                    def redirect_request(self, *args):
                        return None
                with urllib.request.build_opener(NoRedirect()).open(req, timeout=60) as content:
                    result[identifier] = hashlib.file_digest(content, "sha256").hexdigest()
            return result
        finally:
            request(base, token, path, "DELETE")

    before = snapshot()
    subprocess.run(["docker", "restart", args.container], check=True, capture_output=True)
    deadline = time.monotonic() + 180
    while True:
        try:
            after = snapshot()
            break
        except (OSError, TimeoutError):
            if time.monotonic() >= deadline:
                raise
            time.sleep(2)
    evidence = {"status": "PASS" if before == after else "FAIL", "container": args.container,
                "before": before, "after": after, "scope": "queued FlowFile identity/bytes after NiFi restart; downstream processors must remain stopped"}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    if before != after:
        raise RuntimeError("Queued FlowFile identity or bytes changed across NiFi restart")


if __name__ == "__main__":
    main()
