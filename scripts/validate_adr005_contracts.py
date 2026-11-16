"""Validate current ADR005 contracts separately from the byte-pinned Sprint3 artifacts."""
from copy import deepcopy
import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate
from openapi_spec_validator.readers import read_from_filename

ROOT = Path(__file__).resolve().parents[1]


def main():
    for name in ("analysis-openapi-0.9.yaml", "billing-openapi.yaml", "profile-openapi.yaml", "identity-openapi.yaml",
                 "ai-openapi-1.3.yaml", "ai_service_openapi.yaml", "ai_service_openapi.json",
                 "notification-openapi.yaml", "internal-analysis-openapi.yaml"):
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
    current = json.loads((ROOT / "contracts/notification-context.schema.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(current)
    context = Draft202012Validator(current, format_checker=FormatChecker())
    sample = json.loads((ROOT / "infra/sprint3/examples/notification-context.json").read_text(encoding="utf-8"))
    context.validate(sample)
    sample["clinicContextId"] = "55555555-5555-4555-8555-555555555555"
    context.validate(sample)
    invalid = deepcopy(sample)
    invalid["clinicContextId"] = "not-a-clinic-uuid"
    if context.is_valid(invalid):
        raise ValueError("Current notification context accepted an invalid clinic selector")
    historical = Draft202012Validator(json.loads((ROOT / "infra/sprint3/contracts/notification-context.schema.json").read_text(encoding="utf-8")))
    if historical.is_valid(sample):
        raise ValueError("Historical Sprint3 context unexpectedly changed")
    print("PASS ADR005: nine OpenAPI contracts; event0.8 canonical, event0.7 historical compatibility and current clinic notification context, with negative controls.")


if __name__ == "__main__":
    main()
