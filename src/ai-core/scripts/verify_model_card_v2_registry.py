"""Verify Model Card V2, dataset separation, leakage controls, artifact hashes, and honest scientific gates."""
from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path
from typing import List

ROOT = Path(__file__).resolve().parents[3]
REGISTRY_PATH = ROOT / "docs/benchmarks/model_card_v2_registry.json"
REQUIRED_DATASETS = {"CHASE_DB1", "HRF45", "AVRDB100", "Iowa40"}
FORBIDDEN_CLINICAL_CLAIMS = (
    "icdr grading pass",
    "clinically certified",
    "overall accuracy across all tasks",
    "bias gate passed",
)


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def verify_registry(registry_path: Path = REGISTRY_PATH, root: Path = ROOT) -> List[str]:
    errors: List[str] = []
    if not registry_path.is_file():
        return [f"Missing registry file: {registry_path}"]

    raw_text = registry_path.read_text(encoding="utf-8")
    lowered = raw_text.lower()
    for phrase in FORBIDDEN_CLINICAL_CLAIMS:
        if phrase in lowered:
            errors.append(f"Forbidden clinical or misleading aggregate claim detected: {phrase!r}")

    data = json.loads(raw_text)
    if data.get("clinicalValidation") is not False or data.get("icdrGradingSupported") is not False:
        errors.append("Registry must explicitly keep clinicalValidation=false and icdrGradingSupported=false.")

    gate = data.get("scientificGateScrum373", {})
    if gate.get("status") != "OPEN_NOT_PASSED":
        errors.append("SCRUM-373 scientific gate must remain OPEN_NOT_PASSED until verified.")
    if gate.get("thresholdAbsBias") != 0.02:
        errors.append("SCRUM-373 thresholdAbsBias must remain pinned at 0.02.")

    datasets = data.get("datasets", {})
    if set(datasets.keys()) != REQUIRED_DATASETS:
        errors.append(f"Expected separated datasets {sorted(REQUIRED_DATASETS)}, got {sorted(datasets.keys())}")

    for name, info in datasets.items():
        if info.get("redistributedInGit") is not False:
            errors.append(f"Dataset {name} must not mark raw binary data redistributed in Git.")
        manifest_rel = info.get("manifestPath")
        expected_sha = info.get("manifestSha256")
        if name != "Iowa40":
            if not manifest_rel or not expected_sha:
                errors.append(f"Dataset {name} requires manifestPath and manifestSha256.")
                continue
            manifest_abs = root / manifest_rel
            if not manifest_abs.is_file():
                errors.append(f"Missing manifest file for {name}: {manifest_rel}")
            elif sha256_file(manifest_abs) != expected_sha:
                errors.append(f"Manifest SHA-256 mismatch for {name}: {manifest_rel}")
        else:
            if info.get("calibrationAllowed") is not False or info.get("redistributedInZip") is not False:
                errors.append("Iowa40 must forbid calibration leakage and ZIP redistribution.")

    models = {m["id"]: m for m in data.get("models", [])}
    if not {"frangi-vessel-segmentation", "dr-screening-resnet34-gradcam", "avr-v8-rrwnet-knudtson"} <= set(models):
        errors.append("Registry must document Frangi, binary DR ResNet34/Grad-CAM, and AVR V8 models.")

    frangi = models.get("frangi-vessel-segmentation", {})
    for cfg in frangi.get("configs", []):
        cfg_path = root / cfg["configPath"]
        if not cfg_path.is_file():
            errors.append(f"Missing Frangi config: {cfg['configPath']}")
        elif sha256_file(cfg_path) != cfg["sha256"]:
            errors.append(f"Config SHA-256 mismatch: {cfg['configPath']}")

    dr = models.get("dr-screening-resnet34-gradcam", {})
    if dr.get("notIcdrGrading") is not True or dr.get("gradcamGeometry") != "IMAGENET_CENTER_CROP_224":
        errors.append("DR CNN entry must declare notIcdrGrading=true and IMAGENET_CENTER_CROP_224.")
    if not dr.get("dataLeakageControls") or not dr.get("reproduceCommand"):
        errors.append("DR CNN entry requires explicit dataLeakageControls and reproduceCommand.")

    avr = models.get("avr-v8-rrwnet-knudtson", {})
    abstention = avr.get("abstentionPolicy", {})
    if not (
        abstention.get("returnsNullWithReason") is True
        and abstention.get("neverSubstitutesZeroOrLowRisk") is True
        and abstention.get("unavailableInCoverageDenominator") is True
    ):
        errors.append("AVR V8 entry must declare strict null+reason abstention and coverage denominator policy.")

    return errors


def main() -> int:
    errors = verify_registry()
    if errors:
        for err in errors:
            print(f"FAIL: {err}", file=sys.stderr)
        return 1
    print("PASS: Model Card V2 registry verified (4 cohorts, SHA-256 checksums, leakage controls, abstention, SCRUM-373 OPEN).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
