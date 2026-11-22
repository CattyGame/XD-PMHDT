import copy
import json
from pathlib import Path
import sys
import unittest

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parents[1]
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import verify_sprint7_entry_gate as gate


class Sprint7EntryGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = json.loads((SCRIPT_DIR / "sprint7-entry-gate.json").read_text(encoding="utf-8"))

    def test_current_repo_has_all_contracts_workflows_and_role_tests(self) -> None:
        report = gate.evaluate_gate(REPO_ROOT, self.config, {"frontend-tests": "PASS"})
        self.assertEqual(report["errors"], [])
        self.assertGreater(report["migrationCount"], 0)
        self.assertIn(report["entryGateStatus"], ("BLOCKED", "PASS"))
        for role in ("PATIENT", "DOCTOR", "CLINIC_ADMIN", "SYSTEM_ADMIN"):
            self.assertTrue(
                all(item["exists"] for item in report["roleCoverage"][role]),
                f"Missing test coverage file for role {role}",
            )

    def test_skipped_required_check_never_passes(self) -> None:
        report = gate.evaluate_gate(REPO_ROOT, self.config, {"adr005-postgres": "SKIP"})
        self.assertEqual(report["entryGateStatus"], "FAIL")
        self.assertTrue(any("not PASS" in err for err in report["errors"]))

    def test_closing_scrum373_or_passing_oct_fails_invariant(self) -> None:
        mutated = copy.deepcopy(self.config)
        mutated["scopeV2"]["scientificGate"]["status"] = "CLOSED"
        report = gate.evaluate_gate(REPO_ROOT, mutated)
        self.assertEqual(report["entryGateStatus"], "FAIL")
        self.assertTrue(any("SCRUM-373" in err for err in report["errors"]))

    def test_open_blocker_keeps_gate_blocked_never_pass(self) -> None:
        mutated = copy.deepcopy(self.config)
        mutated["blockersAndGaps"] = [
            {
                "code": "W6-M6-04",
                "jira": "SCRUM-396",
                "owner": "M6",
                "status": "OPEN",
                "scopeReductionPlan": "Move to Sprint 7 QA window if prerequisite misses freeze.",
            }
        ]
        report = gate.evaluate_gate(REPO_ROOT, mutated, {"frontend-tests": "PASS"})
        self.assertEqual(report["entryGateStatus"], "BLOCKED")
        self.assertEqual(len(report["openBlockers"]), 1)


if __name__ == "__main__":
    unittest.main()
