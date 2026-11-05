"""Grant billing:notify to an existing notification-api registry entry; preserve keys and credentials."""
import argparse
import json
import os
from pathlib import Path
import stat
import tempfile
import uuid


def grant(path, apply=False):
    path = path.resolve(strict=True)
    repo = Path(__file__).resolve().parents[2]
    if path.is_relative_to(repo):
        raise ValueError("Runtime registry must be outside the repository")
    original = path.read_bytes()
    mode = stat.S_IMODE(path.stat().st_mode)
    registry = json.loads(original.decode("utf-8-sig"))
    if registry.get("schemaVersion") != 1 or registry.get("algorithm") != "RS256":
        raise ValueError("Unexpected registry contract")
    clients = [c for c in registry["clients"] if c["clientId"] == "notification-api"]
    if len(clients) != 1 or not clients[0].get("clientSecretHash"):
        raise ValueError("Provision exactly one notification-api with its existing secret hash first")
    targets = clients[0]["allowedTargets"]
    if targets.get("aura-billing") == ["billing:notify"]:
        return "PASS: billing:notify already granted; registry unchanged."
    targets["aura-billing"] = ["billing:notify"]
    if not apply:
        return "READY: grant billing:notify to existing notification-api; use --apply to publish."
    descriptor, temporary = tempfile.mkstemp(prefix=".m1-registry-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            if os.name != "nt":
                os.fchmod(stream.fileno(), mode)
                source = path.stat()
                if (os.fstat(stream.fileno()).st_uid, os.fstat(stream.fileno()).st_gid) != (source.st_uid, source.st_gid):
                    os.fchown(stream.fileno(), source.st_uid, source.st_gid)
            stream.write((json.dumps(registry, indent=2) + "\n").encode())
            stream.flush()
            os.fsync(stream.fileno())
        if path.read_bytes() != original:
            raise RuntimeError("Registry changed concurrently; stop and review")
        backup = path.with_name(path.name + ".before-m1-" + uuid.uuid4().hex)
        with open(backup, "xb") as stream:
            os.chmod(backup, mode)
            stream.write(original)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return "PASS: billing:notify granted; secret hashes and other clients preserved. Restart Identity to reload."


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        print(grant(args.registry, args.apply))
    except Exception as error:
        raise SystemExit("Registry update failed (" + type(error).__name__ + "); no credentials are printed.")
