"""Guard the camera-to-Reporting delivery boundary in the shipped NiFi export."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


class CameraFlowTests(unittest.TestCase):
    def setUp(self):
        self.flow = json.loads((ROOT / "infra/nifi/camera_ingestion_flow_v2.json").read_text(encoding="utf-8"))["flowContents"]

    def test_success_receipt_has_one_reporting_consumer(self):
        reporting = next(g for g in self.flow["processGroups"] if g["name"] == "AURA-S3-Reporting-Delivery-v1")
        port = next(p for p in reporting["inputPorts"] if p["name"] == "Accepted-Camera-Receipt")
        routes = [c for c in self.flow["connections"] if c["source"]["name"] == "Send-Camera-To-Gateway" and "success" in c["selectedRelationships"]]
        self.assertEqual(1, len(routes))
        self.assertEqual(port["identifier"], routes[0]["destination"]["id"])
        self.assertEqual(reporting["identifier"], routes[0]["destination"]["groupId"])
        self.assertEqual("INPUT_PORT", routes[0]["destination"]["type"])

    def test_export_uses_reviewed_scripts_and_no_database_password(self):
        sender = next(p for p in self.flow["processors"] if p["name"] == "Send-Camera-To-Gateway")
        self.assertEqual((ROOT / "infra/nifi/SendCameraToGateway.groovy").read_text(encoding="utf-8"), sender["properties"]["Script Body"])
        reporting = self.flow["processGroups"][0]
        writer = next(p for p in reporting["processors"] if p["name"] == "Record-Camera-Receipt-Safe-v2")
        self.assertEqual((ROOT / "infra/nifi/RecordCameraReceiptSafeV2.groovy").read_text(encoding="utf-8"), writer["properties"]["groovyx-script-body"])
        self.assertIsNone(reporting["controllerServices"][0]["properties"]["Password"])

    def test_reporting_controller_reference_is_portable_on_import(self):
        reporting = self.flow["processGroups"][0]
        writer = next(p for p in reporting["processors"] if p["name"] == "Record-Camera-Receipt-Safe-v2")
        self.assertEqual(reporting["controllerServices"][0]["identifier"], writer["properties"]["CTL.reporting"])
        self.assertTrue(writer["propertyDescriptors"]["CTL.reporting"]["identifiesControllerService"])
        self.assertTrue(all(p["scheduledState"] == "ENABLED" for p in reporting["processors"]))


if __name__ == "__main__":
    unittest.main()
