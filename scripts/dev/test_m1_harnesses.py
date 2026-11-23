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
import urllib.parse

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

    def run_gate(self, directory, succeeded, clinic=None, interrupt=False, resume=False, financial=None, transient_read=False, diagnostic_failure=False, memory_percent=10, runtime_env=None):
        output = Path(directory) / "result.json"
        image_dir = Path(directory) / "images"
        image_dir.mkdir()
        (image_dir / "one.png").write_bytes(b"unit-fixture-one")
        (image_dir / "two.png").write_bytes(b"unit-fixture-two")
        declared = []
        batch_id = str(uuid.uuid4())
        paths = []
        analyses = []
        ledger_reads = []

        def response(base, token, path, method="GET", body=None, content_type=None, key=None):
            nonlocal transient_read
            parsed = urllib.parse.urlsplit(path)
            self.assertEqual(urllib.parse.parse_qs(parsed.query), {"clinicContextId": [clinic]} if clinic else {})
            path = parsed.path
            paths.append((method, path))
            if transient_read and path.startswith('/api/v1/batches/'):
                transient_read=False
                raise batch.urllib.error.HTTPError(base+path,503,'dependency unavailable',{},None)
            if path == "/api/v1/analysis-upload-batches":
                declared.extend(json.loads(body)["items"])
                return {"batchId": batch_id, "items": [{**x, "uploadStatus": "DECLARED"} for x in declared]}
            if method == "PUT":
                self.assertIn(b"unit-fixture-", body)
                self.assertTrue(content_type.startswith("multipart/form-data; boundary="))
                self.assertEqual(path.split("/")[-1], key)
                return {"itemId": key, "uploadStatus": "STAGED"}
            if method == "POST":
                analyses.extend(str(uuid.uuid4()) for _ in declared)
                return {"items": [{"analysisId": aid, "fileName": x["fileName"], "processingStatus": "QUEUED"} for x, aid in zip(declared, analyses)]}
            if diagnostic_failure and path.startswith('/api/v1/analyses/'):
                if path.endswith(analyses[-1]):return {'processingStatus':'FAILED','error':{'code':'ERR_TIMEOUT'}}
                return {'processingStatus':'COMPLETED','result':{'isMock':False,'processingTimeMs':12,'metrics':{'avRatio':0.8}}}
            if "analysis-upload-batches" in path:
                if not declared:
                    declared.extend({"sha256": batch.hashlib.sha256(f.read_bytes()).hexdigest(), "patientId": patient,
                                     "fileName": f.name, "itemId": str(uuid.uuid4())} for f in sorted(image_dir.iterdir()))
                return {"batchId": batch_id, "items": [{**x, "uploadStatus": "STAGED"} for x in declared]}
            return {"summary": {"total": 2, "active": 0, "succeeded": succeeded, "failed": 2-succeeded, "duplicate": 0}}

        def docker(command, **kwargs):
            if command[1] == "stats":
                return SimpleNamespace(stdout=json.dumps({"Name": "test-worker", "MemPerc": f"{memory_percent}%"}) + "\n")
            if command[1] == "exec":
                self.assertEqual(command[command.index("-F") + 1], "\t")
                ledger_reads.append(True)
                state = "RESERVED" if len(ledger_reads) == 1 else "CAPTURED"
                ids = analyses if financial != "wrong_ids" else [str(uuid.uuid4()) for _ in analyses]
                rows = ["\t".join([aid, str(uuid.uuid4()), state, "reserve", "capture" if state == "CAPTURED" else "", "", "-1", "0" if state == "CAPTURED" else "", "", "wallet", "8", "8"]) for aid in ids]
                if diagnostic_failure and state=='CAPTURED':
                    fields=rows[-1].split('\t');fields[2]='RELEASED';fields[4]='';fields[5]='release';fields[7]='';fields[8]='1'
                    rows[-1]='\t'.join(fields)
                return SimpleNamespace(stdout="\n".join(rows))
            return SimpleNamespace(stdout=json.dumps([{"State": {"OOMKilled": False, "ExitCode": 0, "Running": True}, "Config":{"Env":runtime_env or []}}]))
        patient = str(uuid.uuid4())
        args = ["test_real_batch", "--url", "http://127.0.0.1:1234", "--patient", patient,
                "--images", str(image_dir), "--output", str(output), "--count", "2", "--containers", "test-worker"]
        if clinic:
            args += ["--clinic-context-id", clinic]
        if interrupt:
            args += ["--stop-after-uploads", "1"]
        if resume:
            args += ["--resume-batch-id", batch_id]
        if financial:
            args += ["--postgres-container", "test-pg", "--require-financial-reconciliation", "--timeout", "1"]
        if diagnostic_failure:args+=['--require-real-results','--allow-quality-failures']
        with patch.dict(os.environ, {"AURA_TEST_FIREBASE_TOKEN": "unit-only"}), patch("sys.argv", args), patch.object(batch, "request", side_effect=response), patch.object(batch.subprocess, "run", side_effect=docker), patch.object(batch.time, "sleep"):
            if succeeded == 2 and financial != "wrong_ids" and memory_percent < 80:
                batch.main()
            else:
                with self.assertRaises(RuntimeError):
                    batch.main()
        evidence = json.loads(output.read_text())
        evidence["testPaths"] = paths
        evidence["testLedgerReads"] = len(ledger_reads)
        return evidence

    def test_clinic_selector_reaches_create_upload_finalize_and_poll(self):
        with tempfile.TemporaryDirectory() as directory:
            clinic = str(uuid.uuid4())
            evidence = self.run_gate(directory, 2, clinic=clinic)
            self.assertEqual(evidence["clinicContextId"], clinic)
            self.assertEqual([method for method, _ in evidence["testPaths"]].count("PUT"), 2)
            self.assertTrue(any(path.endswith("/finalize") for _, path in evidence["testPaths"]))

    def test_runtime_records_heap_setting_without_exporting_other_environment_values(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence=self.run_gate(directory,2,runtime_env=['DOTNET_GCHeapHardLimitPercent=0x37','PRIVATE_KEY=should-not-appear'])
            self.assertEqual('0x37',evidence['runtime'][0].get('gcHeapHardLimitPercentEnv'))
            self.assertNotIn('should-not-appear',json.dumps(evidence))

    def test_transient_read_outage_is_recorded_then_recovered_without_repeating_writes(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence=self.run_gate(directory,2,transient_read=True)
            self.assertEqual('PASS_PIPELINE_ONLY',evidence['status'])
            self.assertEqual(1,sum(s['httpErrorStatus']==503 for s in evidence['apiSamples']))
            self.assertEqual(1,sum(method=='POST' and path.endswith('/finalize') for method,path in evidence['testPaths']))

    def test_infrastructure_failure_remains_fail_but_retains_every_result_and_settlement(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence=self.run_gate(directory,1,financial='verify',diagnostic_failure=True)
            self.assertEqual('FAIL',evidence['status'])
            self.assertEqual(2,evidence['measurementCoverage']['denominator'])
            self.assertEqual(1,evidence['measurementCoverage']['PIPELINE_FAILURE'])
            self.assertEqual(2,len(evidence['financialLedger']))
            self.assertEqual([],evidence['financialDiscrepancies'])
            self.assertEqual(1,len(evidence['acceptanceViolations']))

    def test_memory_threshold_remains_fail_after_collecting_terminal_outcomes_and_settlement(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence=self.run_gate(directory,2,financial='settles',memory_percent=80)
            self.assertEqual('FAIL',evidence['status'])
            self.assertEqual(2,len(evidence.get('financialLedger',[])))
            self.assertEqual([],evidence['financialDiscrepancies'])
            self.assertEqual(2,evidence['outcome']['summary']['succeeded'])
            self.assertEqual({'test-worker':80},evidence['resourceViolations'])

    def test_clinic_selector_reaches_interruption_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.run_gate(directory, 2, clinic=str(uuid.uuid4()), interrupt=True)["status"], "INTERRUPTED")

    def test_clinic_selector_reaches_resume_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = self.run_gate(directory, 2, clinic=str(uuid.uuid4()), resume=True)
            self.assertEqual(evidence["status"], "PASS_PIPELINE_ONLY")
            self.assertFalse(any(method == "PUT" for method, _ in evidence["testPaths"]))

    def test_financial_gate_waits_for_async_capture(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = self.run_gate(directory, 2, financial="settles")
            self.assertEqual(evidence["status"], "PASS")
            self.assertEqual(evidence["testLedgerReads"], 2)

    def test_same_counts_with_wrong_analysis_ids_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = self.run_gate(directory, 2, financial="wrong_ids")
            self.assertEqual(evidence["status"], "FAIL")

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
