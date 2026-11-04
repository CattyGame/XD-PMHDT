"""Validate current ADR005 contracts separately from the byte-pinned Sprint3 artifacts."""
from copy import deepcopy
import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate
from openapi_spec_validator.readers import read_from_filename

ROOT = Path(__file__).resolve().parents[1]


def main():
    for name in ("analysis-openapi-0.9.yaml", "billing-openapi.yaml", "profile-openapi.yaml", "identity-openapi.yaml"):
        document, base = read_from_filename(str(ROOT / "contracts" / name))
        validate(document, base_uri=base)
    validators = {}
    for version in ("0.7", "0.8"):
        schema = json.loads((ROOT / "contracts" / f"analysis-events-{version}.schema.json").read_text(encoding="utf-8-sig"))
        Draft202012Validator.check_schema(schema)
        validators[version] = Draft202012Validator(schema, format_checker=FormatChecker())
    event = {
        "eventId": "11111111-1111-4111-8111-111111111111", "eventType": "batch.completed", "schemaVersion": "0.8",
        "occurredAt": "2026-11-04T00:00:00Z", "tenantId": "22222222-2222-4222-8222-222222222222",
        "correlationId": "adr005-contract", "analysisId": "33333333-3333-4333-8333-333333333333",
        "data": {"batchId": "33333333-3333-4333-8333-333333333333", "patientId": "44444444-4444-4444-8444-444444444444",
                 "batchStatus": "PARTIAL_FAILED", "totalImages": 3, "completedImages": 2, "failedImages": 1},
    }
    validators["0.8"].validate(event)
    invalid = deepcopy(event)
    invalid["data"]["batchStatus"] = "PARTIAL_COMPLETED"
    if validators["0.8"].is_valid(invalid):
        raise ValueError("Canonical0.8 accepted the legacy partial-status alias")
    invalid["schemaVersion"] = "0.7"
    validators["0.7"].validate(invalid)
    if validators["0.7"].is_valid(event):
        raise ValueError("Historical0.7 accepted a new0.8 envelope")
    print("PASS ADR005: four OpenAPI contracts; event0.8 canonical and event0.7 historical compatibility, with negative controls.")


if __name__ == "__main__":
    main()
