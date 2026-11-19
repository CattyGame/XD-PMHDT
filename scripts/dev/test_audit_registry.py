import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


class AuditRegistryTests(unittest.TestCase):
    def test_scope_grant_is_explicit_idempotent_and_preserves_existing_credentials(self):
        path = Path(__file__).with_name('Grant-AuditScopes.py')
        spec = importlib.util.spec_from_file_location('audit_grant', path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as folder:
            registry = Path(folder) / 'registry.json'
            data = {'schemaVersion': 1, 'algorithm': 'RS256', 'issuer': 'fixture', 'clients': [
                {'clientId': name + '-api', 'principalId': str(index), 'clientSecretHash': 'fixture-hash-' + name,
                 'allowedTargets': {'aura-profile': ['profile:resolve'], 'aura-identity': ['identity:context']}}
                for index, name in enumerate(('identity', 'analysis', 'billing', 'notification'))]}
            registry.write_text(json.dumps(data), encoding='utf-8')
            before = registry.read_bytes()
            module.grant(registry, False)
            self.assertEqual(before, registry.read_bytes())
            module.grant(registry, True)
            result = json.loads(registry.read_text(encoding='utf-8'))
            for original, current in zip(data['clients'], result['clients']):
                self.assertEqual(original['clientSecretHash'], current['clientSecretHash'])
                self.assertEqual(original['principalId'], current['principalId'])
                self.assertEqual(['identity:context'], current['allowedTargets']['aura-identity'])
                self.assertEqual(['profile:resolve', 'profile:audit'], current['allowedTargets']['aura-profile'])
            changed = registry.read_bytes()
            module.grant(registry, True)
            self.assertEqual(changed, registry.read_bytes())
            self.assertEqual(1, len(list(Path(folder).glob('*.before-audit-*'))))


if __name__ == '__main__':
    unittest.main()
