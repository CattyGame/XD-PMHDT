"""Unit checks for live-gate guardrails; these do not certify live pipeline throughput."""
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import uuid
import importlib.util

import test_real_batch as batch


class LiveGateTests(unittest.TestCase):
    def test_scope_upgrade_preserves_credentials_and_is_idempotent(self):
        spec = importlib.util.spec_from_file_location("grant", Path(__file__).with_name("Grant-NotificationBillingScope.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "registry.json"
            original = {"schemaVersion": 1, "algorithm": "RS256", "clients": [
                {"clientId": "notification-api", "clientSecretHash": "preserved", "allowedTargets": {"aura-analysis": ["analysis:notify"]}},
                {"clientId": "other", "clientSecretHash": "untouched", "allowedTargets": {}},
            ]}
            path.write_text(json.dumps(original))
            before = path.read_bytes()
            module.grant(path)
            self.assertEqual(before, path.read_bytes())
            module.grant(path, True)
            result = json.loads(path.read_text())
            self.assertEqual(result["clients"][1], original["clients"][1])
            self.assertEqual(result["clients"][0]["clientSecretHash"], "preserved")
            self.assertEqual(result["clients"][0]["allowedTargets"]["aura-billing"], ["billing:notify"])
            published = path.read_bytes()
            module.grant(path, True)
            self.assertEqual(published, path.read_bytes())

    def run_gate(self, directory, succeeded):
        output = Path(directory) / "result.json"
        image_dir = Path(directory) / "images"
        image_dir.mkdir()
        (image_dir / "one.png").write_bytes(b"unit-fixture-one")
        (image_dir / "two.png").write_bytes(b"unit-fixture-two")
        declared = []
        batch_id = str(uuid.uuid4())

        def response(base, token, path, method="GET", body=None, content_type=None, key=None):
            if path == "/api/v1/analysis-upload-batches":
                declared.extend(json.loads(body)["items"])
                return {"batchId": batch_id, "items": [{**x, "uploadStatus": "DECLARED"} for x in declared]}
            if method == "PUT":
                self.assertIn(b"unit-fixture-", body)
                self.assertTrue(content_type.startswith("multipart/form-data; boundary="))
                self.assertEqual(path.split("/")[-1], key)
                return {"itemId": key, "uploadStatus": "STAGED"}
            if method == "POST":
                return {"items": [{"analysisId": str(uuid.uuid4()), "fileName": x["fileName"], "uploadStatus": "ACCEPTED"} for x in declared]}
            return {"summary": {"total": 2, "active": 0, "succeeded": succeeded, "failed": 2-succeeded, "duplicate": 0}}

        def docker(command, **kwargs):
            if command[1] == "stats":
                return SimpleNamespace(stdout=json.dumps({"MemPerc": "10%"}) + "\n")
            return SimpleNamespace(stdout=json.dumps([{"State": {"OOMKilled": False, "ExitCode": 0, "Running": True}}]))
        args = ["test_real_batch", "--url", "http://127.0.0.1:1234", "--patient", str(uuid.uuid4()),
                "--images", str(image_dir), "--output", str(output), "--count", "2", "--containers", "test-worker"]
        with patch.dict(os.environ, {"AURA_TEST_FIREBASE_TOKEN": "unit-only"}), patch("sys.argv", args), patch.object(batch, "request", side_effect=response), patch.object(batch.subprocess, "run", side_effect=docker), patch.object(batch.time, "sleep"):
            if succeeded == 2:
                batch.main()
            else:
                with self.assertRaises(RuntimeError):
                    batch.main()
        return json.loads(output.read_text())

    def test_all_real_admissions_and_terminal_success_are_required(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = self.run_gate(directory, 2)
            self.assertIn(evidence["status"], ("PASS", "PASS_PIPELINE_ONLY"))
            self.assertEqual(len(evidence["uploaded"]), 2)

    def test_terminal_failures_never_report_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.run_gate(directory, 1)["status"], "FAIL")


if __name__ == "__main__":
    unittest.main()
