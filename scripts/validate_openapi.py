#!/usr/bin/env python3
"""Check that every Analysis and Campaign endpoint route is represented in OpenAPI 0.8."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCES = [
    ROOT / "src/services/AURA.Analysis.Api/Http/AnalysisEndpoints.cs",
    ROOT / "src/services/AURA.Analysis.Api/Http/CampaignEndpoints.cs",
]
CONTRACT = ROOT / "contracts/analysis-openapi-0.8.yaml"
CAMPAIGN_CONTRACT = ROOT / "contracts/campaign-openapi.yaml"


def fail(message: str) -> None:
    print(f"OpenAPI validation failed: {message}", file=sys.stderr)
    raise SystemExit(1)


source = "\n".join(path.read_text(encoding="utf-8") for path in SOURCES)
contract = CONTRACT.read_text(encoding="utf-8")
campaign_contract = CAMPAIGN_CONTRACT.read_text(encoding="utf-8")
routes = {(method.lower(), path) for method, path in re.findall(r"\.Map(Get|Post|Patch)\(\s*\"([^\"]+)\"", source)}
if not routes:
    fail("No mapped routes found in AnalysisEndpoints or CampaignEndpoints")

documented: set[tuple[str, str]] = set()
for contract_text in (contract, campaign_contract):
    current_path: str | None = None
    for line in contract_text.splitlines():
        path_match = re.match(r"^  (/[^\s:]+):\s*$", line)
        if path_match:
            current_path = path_match.group(1)
            continue
        if current_path is not None:
            method_match = re.match(r"^    (get|post|put|patch|delete|options|head):\s*$", line)
            if method_match:
                documented.add((method_match.group(1), current_path))

missing = sorted(routes - documented)
if missing:
    fail("Undocumented controller routes: " + ", ".join(f"{method.upper()} {path}" for method, path in missing))
stale = sorted(documented - routes)
if stale:
    fail("OpenAPI operations without a mapped controller: " + ", ".join(f"{method.upper()} {path}" for method, path in stale))

required = [
    "  version: 0.8.0",
    "    ReportDownload:",
    "    DoctorReviewRequest:",
    "    DoctorReview:",
    "ERR_CONCURRENT_REVIEW",
    "ERR_REPORT_SOURCE_MISSING",
    "ERR_INVALID_STATE",
]
missing_contract_items = [item for item in required if item not in contract]
if missing_contract_items:
    fail("Missing 0.8 contract items: " + ", ".join(missing_contract_items))

print(f"PASS: OpenAPI 0.8 and campaign contracts document all {len(routes)} Analysis/Campaign routes and required review/report error schemas.")
