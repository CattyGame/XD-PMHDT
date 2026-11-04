#!/usr/bin/env python3
"""Record reproducible ADR-005 candidate inputs and evidence; never manufacture image/test completion."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import platform
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

REQUIRED_IMAGES = (
    "api-gateway", "identity-api", "profile-api", "analysis-api", "analysis-worker",
    "billing-api", "notification-api", "ai-core", "aura-web",
)
REQUIRED_CHECKS = (
    "dotnet-build", "analysis-tests", "billing-tests", "worker-tests", "profile-security",
    "notification-tests", "frontend-tests", "ai-tests", "contract-tests", "migrations-fresh",
    "migrations-upgrade", "end-to-end", "ai-validation",
)


def run(root: Path, *command: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=root, text=True, capture_output=True, check=False, timeout=30)


def file_record(path: Path, root: Path) -> dict:
    path = path.resolve()
    name = path.relative_to(root).as_posix() if path.is_relative_to(root) else path.name
    if not path.is_file():
        return {"path": name, "exists": False}
    content = path.read_bytes()
    return {"path": name, "exists": True, "bytes": len(content),
            "sha256": hashlib.sha256(content).hexdigest(),
            "lfsPointerOnly": content.startswith(b"version https://git-lfs.github.com/spec/v1")}


def migration_plan(root: Path) -> list[dict]:
    initializer = root / "scripts/dev/Initialize-AuraDevDatabase.ps1"
    text = initializer.read_text(encoding="utf-8-sig")
    match = re.search(r"(?ms)^\$migrations\s*=\s*@\(\s*(.*?)^\)", text)
    if not match:
        raise ValueError("Cannot extract initializer migration order; refusing to guess.")
    lines = [line.strip() for line in match.group(1).splitlines() if line.strip() and not line.lstrip().startswith("#")]
    plan = []
    for index, line in enumerate(lines, 1):
        entry = re.fullmatch(r'["\'](infra/postgres/migrations/[^"\']+\.sql)["\'],?', line)
        if not entry:
            raise ValueError(f"Initializer contains a dynamic/unsupported migration expression at entry {index}.")
        path = (root / entry.group(1)).resolve()
        if not path.is_relative_to(root):
            raise ValueError("Migration escapes repository.")
        plan.append({"order": index, **file_record(path, root)})
    if not plan:
        raise ValueError("Empty migration plan.")
    return plan  # Preserve duplicate reapplication of role hardening and explicit order.


def trx_records(paths: list[Path], root: Path) -> list[dict]:
    files = []
    for path in paths:
        files.extend(sorted(path.rglob("*.trx")) if path.is_dir() else [path])
    records = []
    for path in files:
        record = file_record(path, root)
        try:
            tree = ET.parse(path)
            counters = next(node for node in tree.iter() if node.tag.split("}")[-1] == "Counters")
            values = {key: int(value) for key, value in counters.attrib.items()}
            total = values.get("total", 0)
            failed = sum(values.get(key, 0) for key in ("failed", "error", "timeout", "aborted"))
            skipped = values.get("notExecuted", 0)
            status = "FAIL" if failed else "SKIP" if skipped else "PASS" if total > 0 and values.get("passed", 0) == total else "BLOCKED"
            record.update(status=status, counters=values)
        except (OSError, ET.ParseError, StopIteration, ValueError):
            record.update(status="BLOCKED", reason="Missing or invalid TRX counters.")
        records.append(record)
    return records


def overall(records: list[dict]) -> str:
    if not records:
        return "BLOCKED"
    statuses = {row["status"] for row in records}
    return next((status for status in ("FAIL", "BLOCKED", "SKIP") if status in statuses), "PASS")


def pairs(entries: list[str]) -> dict[str, str]:
    result = {}
    for entry in entries:
        name, separator, value = entry.partition("=")
        if not separator or not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", name) or not value:
            raise ValueError("Expected name=value with a stable lowercase name.")
        if name in result:
            raise ValueError(f"Duplicate input {name}.")
        result[name] = value
    return result


def inspect_image(root: Path, name: str, reference: str | None) -> dict:
    if reference is None:
        return {"component": name, "status": "NOT_BUILT", "imageId": None, "repoDigests": []}
    try:
        inspected = run(root, "docker", "image", "inspect", reference)
        if inspected.returncode:
            raise ValueError()
        row = json.loads(inspected.stdout)[0]
        image_id = row["Id"]
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id):
            raise ValueError()
        return {"component": name, "reference": reference, "status": "INSPECTED",
                "imageId": image_id, "repoDigests": row.get("RepoDigests") or [],
                "repoTags": row.get("RepoTags") or []}
    except (OSError, subprocess.TimeoutExpired, ValueError, KeyError, IndexError):
        return {"component": name, "reference": reference, "status": "UNAVAILABLE", "imageId": None, "repoDigests": []}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path)
    parser.add_argument("--mode", choices=("TEST", "LIVE"))
    parser.add_argument("--image", action="append", default=[], metavar="COMPONENT=DOCKER_REFERENCE")
    parser.add_argument("--required-image", action="append", default=[])
    parser.add_argument("--check", action="append", default=[], metavar="NAME=PASS|FAIL|SKIP|BLOCKED")
    parser.add_argument("--evidence", action="append", default=[], metavar="NAME=PATH")
    parser.add_argument("--trx", action="append", default=[], metavar="NAME=PATH_OR_DIRECTORY")
    parser.add_argument("--model-artifact", action="append", default=[], metavar="NAME=PATH")
    parser.add_argument("--evidence-run-id", action="append", default=[])
    parser.add_argument("--require-release", action="store_true")
    parser.add_argument("--print-migrations", action="store_true")
    parser.add_argument("--verify-trx", nargs="+", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.print_migrations:
            print(json.dumps(migration_plan(root), indent=2))
            return 0
        if args.verify_trx:
            records = trx_records(args.verify_trx, root)
            print(json.dumps({"status": overall(records), "results": records}, indent=2))
            return 0 if overall(records) == "PASS" else 1
        if not args.output or not args.mode:
            parser.error("--output and explicit --mode TEST|LIVE are required.")
        git = run(root, "git", "rev-parse", "HEAD")
        sha = git.stdout.strip()
        if git.returncode or not re.fullmatch(r"[0-9a-f]{40}", sha):
            raise ValueError("No exact Git SHA is available.")
        dirty = run(root, "git", "status", "--porcelain=v1", "--untracked-files=normal")
        if dirty.returncode:
            raise ValueError("Cannot establish working tree status.")
        migrations = migration_plan(root)
        checks_input, evidence_input, trx_input = pairs(args.check), pairs(args.evidence), pairs(args.trx)
        checks = {}
        for name in sorted(set(REQUIRED_CHECKS) | set(checks_input) | set(trx_input)):
            if name in trx_input:
                results = trx_records([Path(trx_input[name]).resolve()], root)
                checks[name] = {"status": overall(results), "source": "TRX_COUNTERS", "artifacts": results}
            else:
                status = checks_input.get(name, "NOT_RUN")
                if status not in {"PASS", "FAIL", "SKIP", "BLOCKED", "NOT_RUN"}:
                    raise ValueError(f"Unsupported status for {name}.")
                artifact = file_record(Path(evidence_input[name]), root) if name in evidence_input else None
                checks[name] = {"status": "BLOCKED" if status == "PASS" and not (artifact and artifact["exists"]) else status,
                                "source": "OPERATOR_OR_CI_STEP_STATUS", "artifact": artifact}
        image_input = pairs(args.image)
        images = [inspect_image(root, name, image_input.get(name)) for name in sorted(set(REQUIRED_IMAGES) | set(args.required_image) | set(image_input))]
        config_paths = set(root.glob("src/ai-core/config/**/*.yaml")) | set(root.glob("src/ai-core/config/**/*.yml")) | set(root.glob("src/ai-core/config/**/*.json"))
        locks = set(root.glob("requirements*.txt")) | set(root.glob("src/ai-core/requirements*"))
        locks |= set(root.glob("src/services/*/packages.lock.json")) | {root / "src/web/aura-web/package-lock.json", root / "global.json"}
        locks |= set(root.glob("src/services/*/*.csproj")) | set(root.glob("src/common/*/*.csproj"))
        locks |= set(root.glob("src/gateway/*/*.csproj")) | set(root.glob("tests/*/*.csproj"))
        locks |= {root / "Directory.Packages.props", root / "NuGet.Config", root / "src/web/aura-web/package.json"}
        contracts = sorted(p for p in (root / "contracts").rglob("*") if p.is_file())
        models = [{"name": name, **file_record(Path(path), root)} for name, path in pairs(args.model_artifact).items()]
        reasons = []
        if dirty.stdout.strip(): reasons.append("Working tree differs from the recorded Git commit.")
        if any(not row["exists"] for row in migrations): reasons.append("Required migration file is missing.")
        if any(row["status"] != "INSPECTED" for row in images): reasons.append("Not all required component images were inspected.")
        if any(checks[name]["status"] != "PASS" for name in REQUIRED_CHECKS): reasons.append("Required checks are missing, failed, blocked or skipped.")
        if any(not row["exists"] or row["lfsPointerOnly"] for row in models): reasons.append("Model artifact bytes are missing or only an LFS pointer.")
        if not args.evidence_run_id: reasons.append("No evidence run identity was supplied.")
        sdk = run(root, "dotnet", "--list-sdks")
        manifest = {
            "schemaVersion": "1.0", "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
            "git": {"sha": sha, "dirty": bool(dirty.stdout.strip())}, "mode": args.mode,
            "releaseReady": not reasons, "releaseBlockers": reasons,
            "readinessScope": "Artifact and required-evidence completeness; domain acceptance remains tied to the evidence protocols.",
            "initializer": file_record(root / "scripts/dev/Initialize-AuraDevDatabase.ps1", root),
            "migrations": migrations, "contracts": [file_record(p, root) for p in contracts],
            "dependencyInputs": [file_record(p, root) for p in sorted(locks) if p.is_file()],
            "runtime": {"python": platform.python_version(), "platform": platform.platform(),
                        "dotnetSdks": sdk.stdout.splitlines() if sdk.returncode == 0 else None},
            "modelConfiguration": [file_record(p, root) for p in sorted(config_paths)], "modelArtifacts": models,
            "images": images, "checks": checks, "evidenceRunIds": args.evidence_run_id,
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"output": str(args.output), "gitSha": sha, "releaseReady": manifest["releaseReady"], "releaseBlockers": reasons}))
        return 1 if args.require_release and reasons else 0
    except (ValueError, OSError, subprocess.TimeoutExpired) as error:
        print(json.dumps({"error": str(error), "releaseReady": False}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
