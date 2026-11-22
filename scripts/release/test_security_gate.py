import datetime as dt
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('security_gate', Path(__file__).with_name('security_gate.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class SecurityGateTests(unittest.TestCase):
    def test_local_and_remote_images_require_full_immutable_digest(self):
        for value in ['sha256:'+'a'*64,'registry.example/aura@sha256:'+'0'*64]:
            self.assertTrue(gate.pinned_image(value))
        for value in ['aura:latest','sha256:abc','repo@sha256:nope','bad name@sha256:'+'a'*64]:
            self.assertFalse(gate.pinned_image(value))
    def test_secret_matches_are_redacted_but_remain_blocking(self):
        report = {'Results': [{'Target': 'source', 'Secrets': [{'RuleID': 'private-key', 'Match': 'REAL_KEY', 'Severity': 'HIGH'}]}]}
        safe = gate.redact(report)
        self.assertNotIn('REAL_KEY', str(safe))
        self.assertEqual('FAIL', gate.evaluate([safe], [], dt.date(2026, 11, 22))['status'])

    def test_expired_or_incomplete_exception_never_waives_high_finding(self):
        report = {'Results': [{'Target': 'lock', 'Vulnerabilities': [{'VulnerabilityID': 'CVE-fixture',
            'PkgName': 'pkg', 'InstalledVersion': '1', 'Severity': 'HIGH'}]}]}
        exception = {'id': 'CVE-fixture', 'target': 'lock', 'package': 'pkg', 'version': '1',
                     'owner': 'M1', 'reason': 'Nonreachable fixture dependency', 'expires': '2026-11-21'}
        self.assertEqual('FAIL', gate.evaluate([report], [exception], dt.date(2026, 11, 22))['status'])
        exception['expires'] = '2026-11-28'
        self.assertEqual('PASS_WITH_EXCEPTIONS', gate.evaluate([report], [exception], dt.date(2026, 11, 22))['status'])
        exception.pop('reason')
        self.assertEqual('FAIL', gate.evaluate([report], [exception], dt.date(2026, 11, 22))['status'])

    def test_missing_or_malformed_scan_is_not_a_clean_scan(self):
        self.assertEqual('NOT_TESTED', gate.evaluate([], [], dt.date.today())['status'])
        with self.assertRaises(ValueError): gate.evaluate([{'error': 'scanner failed'}], [], dt.date.today())
        self.assertEqual('PASS', gate.evaluate([{'Results': []}], [], dt.date.today())['status'])


if __name__ == '__main__': unittest.main()
