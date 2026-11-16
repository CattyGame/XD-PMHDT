"""Register the dedicated Profile caller or add its Analysis binding scope.

Default: validate only. --apply preserves existing keys, secrets and principals;
an existing identity-only Profile registration gains analysis:binding.
Never emits credentials, JWTs, or registry contents.
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import stat
import sys
import tempfile
import uuid

CLIENT_ID = "profile-api"
PRINCIPAL = "a1000000-0000-4000-8000-000000000004"
LEGACY_TARGETS = {"aura-identity": ["identity:context"]}
TARGETS = {**LEGACY_TARGETS, "aura-analysis": ["analysis:binding"]}


class RegistrationError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise RegistrationError(message)


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def verify(secret, encoded):
    parts = encoded.split("$")
    require(len(parts) == 4 and parts[:2] == ["pbkdf2-sha256", "600000"],
            "Unexpected secret hash format.")
    salt = base64.b64decode(parts[2], validate=True)
    expected = base64.b64decode(parts[3], validate=True)
    require(len(salt) == 16 and len(expected) == 32, "Invalid secret hash dimensions.")
    actual = hashlib.pbkdf2_hmac("sha256", secret.encode(), salt, 600000, 32)
    return hmac.compare_digest(actual, expected)


def write_new(path, content):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(content)
        stream.flush()
        os.fsync(stream.fileno())


def encode(value):
    return (json.dumps(value, indent=2) + "\n").encode()


def add_binding_scope(registry_path, credential_path, registry, credential):
    paths = (credential_path, registry_path)
    originals = {path: path.read_bytes() for path in paths}
    lock = registry_path.parent.parent / ".profile-client-registration.lock"
    write_new(lock, b"Profile binding scope upgrade in progress.\n")
    temporary = []
    replaced = []
    try:
        for client in registry["clients"]:
            if client["clientId"] == CLIENT_ID:
                client["allowedTargets"] = TARGETS
        credential["allowedTargets"] = TARGETS
        for path, value in zip(paths, (credential, registry)):
            write_new(path.with_name(path.name + ".before-binding-" + uuid.uuid4().hex), originals[path])
            fd, name = tempfile.mkstemp(prefix=".profile-binding-", dir=path.parent)
            temporary.append(Path(name))
            with os.fdopen(fd, "wb") as stream:
                if os.name != "nt":
                    source = path.stat()
                    os.fchmod(stream.fileno(), stat.S_IMODE(source.st_mode))
                    os.fchown(stream.fileno(), source.st_uid, source.st_gid)
                stream.write(encode(value))
                stream.flush()
                os.fsync(stream.fileno())
        require(all(path.read_bytes() == data for path, data in originals.items()), "Registration changed concurrently.")
        for path, staged in zip(paths, temporary):
            os.replace(staged, path)
            replaced.append(path)
    except Exception:
        for path in reversed(replaced):
            path.write_bytes(originals[path])
        raise
    finally:
        for path in temporary:
            path.unlink(missing_ok=True)
        lock.unlink(missing_ok=True)


def register(home, repo, apply=False):
    home, repo = home.resolve(), repo.resolve()
    require((repo / "AURA.sln").is_file(), "Run from the repository root.")
    require(not home.is_relative_to(repo), "Service-auth directory must be outside the repository.")
    registry_path = home / "issuer" / "identity-clients.json"
    credential_path = home / "clients" / "profile-api.json"
    require((home / "clients").is_dir(), "Existing clients directory is missing.")
    protected = [home / "issuer" / "signing-key.pem", home / "public" / "signing-public.pem"]
    protected += [home / "clients" / (name + ".json")
                  for name in ("analysis-api", "identity-api", "billing-api")]
    require(all(p.is_file() for p in protected), "Existing keys/client credentials are missing.")
    before = {p: p.read_bytes() for p in protected}
    original_bytes = registry_path.read_bytes()
    registry_stat = registry_path.stat()
    registry = json.loads(original_bytes.decode("utf-8-sig"))
    issuer = registry.get("issuer")
    require(registry.get("schemaVersion") == 1 and isinstance(issuer, str)
            and 0 < len(issuer) <= 128 and not any(c.isspace() for c in issuer)
            and registry.get("algorithm") == "RS256" and registry.get("tokenLifetimeSeconds") == 300,
            "Existing registry does not match the local service-auth contract.")
    clients = registry.get("clients")
    require(isinstance(clients, list), "Registry clients must be an array.")
    ids = [c.get("clientId") for c in clients]
    principals = [str(uuid.UUID(c["principalId"])) for c in clients]
    require(len(ids) == len(set(ids)) and len(principals) == len(set(principals)),
            "Registry has duplicate client/principal IDs.")
    matches = [c for c in clients if c.get("clientId") == CLIENT_ID]
    if matches or credential_path.exists():
        require(len(matches) == 1 and credential_path.is_file(),
                "Partial Profile registration exists. Preserve files and review before retry.")
        registered, credential = matches[0], read(credential_path)
        principal = str(uuid.UUID(registered["principalId"]))
        require(uuid.UUID(principal).int != 0, "Existing Profile principal is empty.")
        for value in (registered, credential):
            require(value.get("clientId") == CLIENT_ID and value.get("principalId") == principal
                    and value.get("allowedTargets") in (LEGACY_TARGETS, TARGETS), "Existing Profile client metadata differs.")
        require(registered["allowedTargets"] == credential["allowedTargets"], "Partial Profile scope upgrade; stop and review.")
        require(verify(credential["clientSecret"], registered["clientSecretHash"]),
                "Existing Profile credentials do not match the issuer registry.")
        if registered["allowedTargets"] == LEGACY_TARGETS:
            if not apply:
                print("READY: add analysis:binding to existing Profile registration; use --apply. Credentials unchanged.")
                return
            add_binding_scope(registry_path, credential_path, registry, credential)
            print("PASS: Profile analysis:binding added; keys and secrets preserved. Restart Identity to reload.")
            return
        print("PASS: Existing Profile client verified; keys and credentials were not changed.")
        return
    require(PRINCIPAL not in principals, "Reserved Profile principal ID is already used.")
    require(set(("analysis-api", "identity-api", "billing-api")).issubset(ids),
            "Expected existing service clients are missing.")
    if not apply:
        print("PASS: Ready to append profile-api with identity:context and analysis:binding.")
        print("CHECK ONLY: existing keys, registry and credentials were not changed.")
        return

    lock_path = home / ".profile-client-registration.lock"
    # Exclusive lock blocks concurrent registrations. A stale lock is reviewed manually.
    write_new(lock_path, b"Profile client registration in progress.\n")
    temporary_path = None
    try:
        require(registry_path.read_bytes() == original_bytes and not credential_path.exists(),
                "Registry/credential changed during validation; stop to review.")
        secret = secrets.token_urlsafe(32)
        salt = secrets.token_bytes(16)
        derived = hashlib.pbkdf2_hmac("sha256", secret.encode(), salt, 600000, 32)
        secret_hash = "pbkdf2-sha256$600000$" + base64.b64encode(salt).decode() + "$" + base64.b64encode(derived).decode()
        require(verify(secret, secret_hash), "New secret hash failed verification.")
        new_client = {"clientId": CLIENT_ID, "principalId": PRINCIPAL,
                      "allowedTargets": TARGETS, "clientSecretHash": secret_hash}
        # Preserve each old client object and all issuer/key metadata.
        clients.append(new_client)
        credential = {"schemaVersion": 1, "clientId": CLIENT_ID, "principalId": PRINCIPAL,
                      "clientSecret": secret, "issuer": registry["issuer"], "allowedTargets": TARGETS}
        backup = registry_path.with_name("identity-clients.before-profile-" + uuid.uuid4().hex + ".json")
        write_new(backup, original_bytes)
        write_new(credential_path, encode(credential))
        fd, name = tempfile.mkstemp(prefix=".profile-registry-", dir=registry_path.parent)
        temporary_path = Path(name)
        with os.fdopen(fd, "wb") as stream:
            if os.name != "nt":
                os.fchmod(stream.fileno(), stat.S_IMODE(registry_stat.st_mode))
                temporary_stat = os.fstat(stream.fileno())
                if (temporary_stat.st_uid, temporary_stat.st_gid) != (registry_stat.st_uid, registry_stat.st_gid):
                    os.fchown(stream.fileno(), registry_stat.st_uid, registry_stat.st_gid)
            stream.write(encode(registry))
            stream.flush()
            os.fsync(stream.fileno())
        require(registry_path.read_bytes() == original_bytes,
                "Registry changed before publication. Preserve the partial registration for review.")
        os.replace(temporary_path, registry_path)
        temporary_path = None
        require(all(p.read_bytes() == value for p, value in before.items()),
                "Existing keys/credentials changed concurrently; stop to review.")
        require(verify(read(credential_path)["clientSecret"], read(registry_path)["clients"][-1]["clientSecretHash"]),
                "Published Profile registration failed verification.")
        print("PASS: Profile client appended with identity:context and analysis:binding only.")
        print("PASS: Existing signing keys and three client credential files are byte-for-byte unchanged.")
        print("PASS: Original issuer registry was backed up outside the repository.")
        print("Identity must reload its registry; Profile must load its own new client secret.")
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)
        lock_path.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--auth-home", type=Path)
    args = parser.parse_args()
    try:
        home = args.auth_home
        if home is None:
            value = os.environ.get("AURA_SERVICE_AUTH_HOME", "")
            require(bool(value), "AURA_SERVICE_AUTH_HOME is missing.")
            home = Path(value)
        require(home.is_absolute(), "Service-auth directory must be absolute.")
        register(home, args.repo, args.apply)
        return 0
    except Exception as exc:
        print("STOP: " + (str(exc) if isinstance(exc, RegistrationError) else type(exc).__name__), file=sys.stderr)
        print("Do not regenerate keys or remove existing service-auth files.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
