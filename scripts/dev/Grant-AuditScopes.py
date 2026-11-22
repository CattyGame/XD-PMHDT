"""Add profile:audit only to existing owner-service identities; preserve principals, secrets and all other scopes."""
import argparse
import json
import os
from pathlib import Path
import stat
import tempfile
import uuid

OWNERS = ('identity-api', 'analysis-api', 'billing-api', 'notification-api')


def grant(path, apply=False):
    path = path.resolve(strict=True)
    if path.is_relative_to(Path(__file__).resolve().parents[2]):
        raise ValueError('Runtime credential registry must be outside the repository')
    original = path.read_bytes()
    registry = json.loads(original.decode('utf-8-sig'))
    if registry.get('schemaVersion') != 1 or registry.get('algorithm') != 'RS256':
        raise ValueError('Unexpected service-auth registry contract')
    changed = False
    for owner in OWNERS:
        clients = [c for c in registry['clients'] if c.get('clientId') == owner]
        if len(clients) != 1 or not clients[0].get('clientSecretHash'):
            raise ValueError('Provision exactly one existing credential for every audit owner first')
        targets = clients[0]['allowedTargets']
        scopes = targets.setdefault('aura-profile', [])
        if 'profile:audit' not in scopes:
            scopes.append('profile:audit')
            changed = True
    if not changed:
        return 'PASS: Audit scopes already granted; registry unchanged.'
    if not apply:
        return 'READY: Add profile:audit to four existing owner identities; use --apply to publish.'
    mode = stat.S_IMODE(path.stat().st_mode)
    descriptor, temporary = tempfile.mkstemp(prefix='.audit-registry-', dir=path.parent)
    try:
        with os.fdopen(descriptor, 'wb') as stream:
            if os.name != 'nt':
                os.fchmod(stream.fileno(), mode)
                os.fchown(stream.fileno(), path.stat().st_uid, path.stat().st_gid)
            stream.write((json.dumps(registry, indent=2) + '\n').encode('utf-8'))
            stream.flush()
            os.fsync(stream.fileno())
        if path.read_bytes() != original:
            raise RuntimeError('Registry changed concurrently; no update applied')
        backup = path.with_name(path.name + '.before-audit-' + uuid.uuid4().hex)
        with backup.open('xb') as stream:
            os.chmod(backup, mode)
            stream.write(original)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return 'PASS: Audit scopes granted; existing credentials preserved. Restart Identity to reload.'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--registry', required=True, type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    try:
        print(grant(args.registry, args.apply))
    except Exception as error:
        raise SystemExit('Audit scope update failed (' + type(error).__name__ + '); no credentials printed.')
