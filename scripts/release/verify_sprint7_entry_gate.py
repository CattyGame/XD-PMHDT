#!/usr/bin/env python3
"""Verify Sprint 6 Feature Freeze and Sprint 7 Entry Gate (W6-M6-03 / SCRUM-397)."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sys

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import create_candidate_manifest as ccm


def git_info(root: Path) -> dict:
    head = ccm.run(root, "git", "rev-parse", "HEAD")
    tree = ccm.run(root, "git", "rev-parse", "HEAD^{tree}")
    status = ccm.run(root, "git", "status", "--porcelain")
    return {
        "headSha": head.stdout.strip() if head.returncode == 0 else None,
        "treeSha": tree.stdout.strip() if tree.returncode == 0 else None,
        "dirty": bool(status.stdout.strip()) if status.returncode == 0 else True,
    }


def evaluate_gate(
    root: Path,
    config: dict,
    check_results: dict[str, str] | None = None,
) -> dict:
    check_results = check_results or {}
    errors: list[str] = []

    # 1. Scope V2 & scientific/clinical invariants (never auto-close SCRUM-373, OCT remains DEFERRED)
    sci = config.get("scopeV2", {}).get("scientificGate", {})
    if sci.get("jira") != "SCRUM-373" or sci.get("status") != "OPEN" or sci.get("autoCloseAllowed") is not False:
        errors.append("SCRUM-373 scientific gate must remain OPEN with autoCloseAllowed=false.")

    deferred = config.get("scopeV2", {}).get("deferred", [])
    if not any("OCT" in str(item) and "DEFERRED" in str(item) for item in deferred):
        errors.append("OCT inference must remain explicitly marked DEFERRED in scopeV2.")

    # 2. Contracts & workflows
    contracts = [ccm.file_record(root / p, root) for p in config.get("requiredContracts", [])]
    workflows = [ccm.file_record(root / p, root) for p in config.get("requiredWorkflows", [])]
    missing_files = [
        item["path"]
        for item in (*contracts, *workflows)
        if not item.get("exists")
    ]

    # 3. Four-role positive/negative test coverage presence
    role_coverage: dict[str, list[dict]] = {}
    for role, paths in config.get("roleCoverage", {}).items():
        records = [ccm.file_record(root / p, root) for p in paths]
        role_coverage[role] = records
        missing_files.extend(r["path"] for r in records if not r.get("exists"))

    # 4. Invariant tests presence
    invariants = {
        key: ccm.file_record(root / path, root)
        for key, path in config.get("invariantTests", {}).items()
    }
    missing_files.extend(r["path"] for r in invariants.values() if not r.get("exists"))

    if missing_files:
        errors.append(f"Missing required contract/workflow/test files: {', '.join(sorted(set(missing_files)))}")

    # 5. Migration plan integrity
    try:
        migrations = ccm.migration_plan(root)
        missing_migrations = [m["path"] for m in migrations if not m.get("exists")]
        if missing_migrations:
            errors.append(f"Missing migration SQL files: {', '.join(missing_migrations)}")
    except ValueError as exc:
        migrations = []
        errors.append(str(exc))

    # 6. No skipped/failed required checks
    bad_checks = {
        name: status
        for name, status in check_results.items()
        if status not in ("PASS",)
    }
    if bad_checks:
        errors.append(f"Required checks not PASS (skip/fail/blocked cannot pass): {bad_checks}")

    # 7. Blockers & P0/P1 defects
    blockers = config.get("blockersAndGaps", [])
    open_blockers = [
        b for b in blockers if b.get("status") not in ("MERGED", "VERIFIED")
    ]
    for b in open_blockers:
        if not b.get("owner") or not b.get("scopeReductionPlan"):
            errors.append(f"Open blocker {b.get('code')} missing owner or scopeReductionPlan.")

    open_p0_p1 = [
        d for d in config.get("defects", [])
        if d.get("severity") in ("P0", "P1") and d.get("status") != "CLOSED"
    ]

    if errors:
        entry_gate_status = "FAIL"
    elif open_blockers or open_p0_p1:
        entry_gate_status = "BLOCKED"
    else:
        entry_gate_status = "PASS"

    return {
        "gate": config.get("gate"),
        "jira": config.get("jira"),
        "evaluatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "git": git_info(root),
        "entryGateStatus": entry_gate_status,
        "errors": errors,
        "openBlockers": open_blockers,
        "openP0P1Defects": open_p0_p1,
        "migrationCount": len(migrations),
        "contracts": contracts,
        "workflows": workflows,
        "roleCoverage": role_coverage,
        "invariants": invariants,
        "checks": check_results,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--config", type=Path, default=SCRIPT_DIR / "sprint7-entry-gate.json")
    parser.add_argument("--check", action="append", default=[], metavar="NAME=PASS|FAIL|SKIP|BLOCKED")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--strict", action="store_true", help="Exit 1 unless entryGateStatus == PASS")
    args = parser.parse_args()

    root = args.root.resolve()
    config = json.loads(args.config.read_text(encoding="utf-8"))
    checks = ccm.pairs(args.check)
    report = evaluate_gate(root, config, checks)

    rendered = json.dumps(report, indent=2, ensure_ascii=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered + "\n", encoding="utf-8")
    else:
        print(rendered)

    if args.strict and report["entryGateStatus"] != "PASS":
        return 1
    if report["entryGateStatus"] == "FAIL":
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
