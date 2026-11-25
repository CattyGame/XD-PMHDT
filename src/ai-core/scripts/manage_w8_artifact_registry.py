"""Sprint 8 W8-M4-01 (SCRUM-423): Verifiable artifact registry & license-compliant repro pack.

Checks SHA-256 digests fail-closed across configs, manifests, third-party licenses,
and optional local model weights; audits Git hygiene so raw models, datasets, Iowa40,
and credentials are never tracked; and packages a clean metadata/config reproducibility
bundle while explicitly forbidding Iowa dataset or credential inclusion.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path
from typing import Any, Dict, List, Optional

REPO_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_REGISTRY_PATH = REPO_ROOT / "docs" / "benchmarks" / "w8_artifact_registry_v1.0.json"
DEFAULT_MANIFEST_CATALOG_PATH = REPO_ROOT / "src" / "ai-core" / "config" / "artifact-manifests.json"
DEFAULT_MODEL_CARD_REGISTRY_PATH = REPO_ROOT / "docs" / "benchmarks" / "model_card_v2_registry.json"
DEFAULT_S7_REPORT_PATH = REPO_ROOT / "docs" / "benchmarks" / "v2_evaluation_report_s7.json"

_SHA256_RE = re.compile(r"^[a-f0-9]{64}$")

FORBIDDEN_CLINICAL_CLAIMS = (
    "icdr grading pass",
    "clinically certified",
    "overall accuracy across all tasks",
    "bias gate passed",
    "scrum-373 closed",
)

_FORBIDDEN_WEIGHT_EXTS = (".pt", ".pth", ".ckpt", ".safetensors", ".onnx", ".joblib", ".pkl", ".h5")
_FORBIDDEN_SECRET_PATTERNS = (
    re.compile(r"(^|/)\.env($|\.(?!example$))", re.IGNORECASE),
    re.compile(r"\.(pem|key)$", re.IGNORECASE),
    re.compile(r"firebase-adminsdk.*\.json$", re.IGNORECASE),
    re.compile(r"service-account.*\.json$", re.IGNORECASE),
)

_REPRO_GUIDE_REL = "docs/data_cards/w8_artifact_reproducibility_guide.md"


class ArtifactVerificationError(RuntimeError):
    """Fail-closed error when artifact hash, license, or hygiene check fails."""


def sha256_lf(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def sha256_raw(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def verify_artifact_file_sha256(
    path: Path,
    expected_sha256: str,
    canonical_lf: bool = False,
) -> str:
    """Verify a single file's SHA-256 digest fail-closed and return the computed digest."""
    expected = str(expected_sha256 or "").strip().lower()
    if not _SHA256_RE.fullmatch(expected):
        raise ArtifactVerificationError(f"Invalid expected SHA-256 format: {expected_sha256!r}")
    if not path.is_file():
        raise ArtifactVerificationError(f"Missing artifact file for verification: {path}")
    actual = sha256_lf(path) if canonical_lf else sha256_raw(path)
    if actual != expected:
        raise ArtifactVerificationError(
            f"SHA-256 mismatch for {path}: expected {expected}, observed {actual}"
        )
    return actual


def audit_git_hygiene(root: Path = REPO_ROOT) -> List[str]:
    """Ensure Git tracks zero model weights, unpacked datasets, Iowa files, or credentials."""
    if not (root / ".git").exists():
        return []
    try:
        output = subprocess.check_output(
            ["git", "ls-files"],
            cwd=str(root),
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
        )
    except Exception as exc:
        return [f"Unable to run git ls-files hygiene audit: {exc}"]

    violations: List[str] = []
    for raw_line in output.splitlines():
        rel = raw_line.strip().replace("\\", "/")
        if not rel:
            continue
        low = rel.lower()
        if any(low.endswith(ext) for ext in _FORBIDDEN_WEIGHT_EXTS):
            violations.append(f"Git tracks forbidden model/serialized weight file: {rel}")
        if low.startswith("datasets/") or low.startswith("model-artifacts/"):
            violations.append(f"Git tracks unpacked dataset/model directory entry: {rel}")
        if "iowa" in low and (low.startswith("datasets/") or low.startswith("data-assets/") or low.endswith(".zip")):
            violations.append(f"Git tracks forbidden external Iowa dataset file: {rel}")
        for pat in _FORBIDDEN_SECRET_PATTERNS:
            if pat.search(low):
                violations.append(f"Git tracks credential/secret file: {rel}")
    return violations


def verify_w8_registry(
    registry_path: Path = DEFAULT_REGISTRY_PATH,
    root: Path = REPO_ROOT,
    require_local_weights: bool = False,
) -> Dict[str, Any]:
    """Validate docs/benchmarks/w8_artifact_registry_v1.0.json fail-closed."""
    errors: List[str] = []
    if not registry_path.is_file():
        return {
            "ok": False,
            "errors": [f"Missing W8 artifact registry file: {registry_path}"],
            "localArtifactStatuses": {},
            "gitHygieneClean": False,
        }

    raw_text = registry_path.read_text(encoding="utf-8")
    lowered = raw_text.lower()
    for phrase in FORBIDDEN_CLINICAL_CLAIMS:
        if phrase in lowered:
            errors.append(f"Forbidden clinical claim detected in registry: {phrase!r}")

    data = json.loads(raw_text)
    if data.get("taskId") != "W8-M4-01" or data.get("jiraKey") != "SCRUM-423":
        errors.append("Registry taskId/jiraKey must be W8-M4-01 / SCRUM-423.")
    if data.get("clinicalValidation") is not False or data.get("icdrGradingSupported") is not False:
        errors.append("Registry must preserve clinicalValidation=false and icdrGradingSupported=false.")
    if data.get("iowaRedistributedInGit") is not False or data.get("iowaRedistributedInZip") is not False:
        errors.append("Registry must forbid Iowa dataset redistribution in Git and ZIP.")
    if data.get("gitContainsModelWeightsOrCredentials") is not False:
        errors.append("gitContainsModelWeightsOrCredentials must remain false.")

    gate = data.get("scientificGateScrum373", {})
    if gate.get("status") != "OPEN_NOT_PASSED" or gate.get("autoCloseAllowed") is not False:
        errors.append("SCRUM-373 must remain OPEN_NOT_PASSED with autoCloseAllowed=false.")
    if gate.get("thresholdAbsBias") != 0.02:
        errors.append("SCRUM-373 thresholdAbsBias must remain 0.02.")

    # Verify runtime manifest catalog consistency
    manifest_block = data.get("runtimeManifestCatalog", {})
    catalog_rel = manifest_block.get("catalogPath", "")
    catalog_abs = root / catalog_rel
    try:
        verify_artifact_file_sha256(catalog_abs, manifest_block.get("catalogSha256Lf", ""), canonical_lf=True)
        disk_catalog = json.loads(catalog_abs.read_text(encoding="utf-8"))
        disk_map = {m["artifactManifestId"]: m for m in disk_catalog.get("manifests", [])}
        reg_map = {m["artifactManifestId"]: m for m in manifest_block.get("manifests", [])}
        if set(disk_map.keys()) != set(reg_map.keys()):
            errors.append("Runtime manifest IDs in W8 registry differ from artifact-manifests.json.")
        for mid, entry in reg_map.items():
            d_entry = disk_map.get(mid)
            if d_entry is None:
                continue
            for field in ("configFile", "thresholdConfigSha256", "modelVersion", "configVersion", "thresholdVersion"):
                if entry.get(field) != d_entry.get(field):
                    errors.append(f"Manifest {mid} field {field!r} drifted from artifact-manifests.json.")
    except ArtifactVerificationError as exc:
        errors.append(str(exc))

    # Verify artifacts list
    artifacts = data.get("artifacts", [])
    by_id = {item.get("id"): item for item in artifacts if isinstance(item, dict)}
    required_ids = {
        "frangi-baseline-config-v1.0",
        "frangi-vectorized-config-v1.3",
        "frangi-hrf-clahe-config-v1.0",
        "dr-l2-spectral-13f-weights",
        "dr-resnet34-hrf-v1-pytorch",
        "avr-rrwnet-rite-safetensors",
        "avr-lunetv2-odc-onnx",
    }
    if not required_ids.issubset(by_id.keys()):
        errors.append(f"Missing required artifact entries: {sorted(required_ids - set(by_id.keys()))}")

    local_statuses: Dict[str, str] = {}
    onnx_ids = [item["id"] for item in artifacts if isinstance(item, dict) and item.get("onnxAvailable") is True]
    if onnx_ids != ["avr-lunetv2-odc-onnx"]:
        errors.append(f"Only genuinely present ONNX artifact may set onnxAvailable=true; got {onnx_ids}.")

    for item in artifacts:
        art_id = item.get("id", "<unknown>")
        rel_path = item.get("path", "")
        abs_path = root / rel_path
        if not item.get("license") or not item.get("source") or not item.get("datasetSplit"):
            errors.append(f"Artifact {art_id} requires non-empty license, source, and datasetSplit.")

        if item.get("trackedInGit") is True:
            expected_lf = item.get("sha256Lf", "")
            try:
                verify_artifact_file_sha256(abs_path, expected_lf, canonical_lf=True)
                local_statuses[art_id] = "VERIFIED_IN_REPO"
            except ArtifactVerificationError as exc:
                errors.append(f"{art_id}: {exc}")
                local_statuses[art_id] = "FAILED"
        else:
            expected_raw = item.get("sha256Raw", "")
            if abs_path.is_file():
                try:
                    verify_artifact_file_sha256(abs_path, expected_raw, canonical_lf=False)
                    local_statuses[art_id] = "VERIFIED_LOCAL_BINARY"
                except ArtifactVerificationError as exc:
                    errors.append(f"{art_id}: {exc}")
                    local_statuses[art_id] = "FAILED"
            elif require_local_weights:
                errors.append(f"{art_id}: missing required local binary at {rel_path}")
                local_statuses[art_id] = "MISSING_REQUIRED"
            else:
                local_statuses[art_id] = "SKIPPED_NOT_DOWNLOADED_LOCALLY"

        lic_rel = item.get("licenseFileInRepo")
        lic_sha = item.get("licenseFileSha256Lf")
        if lic_rel:
            try:
                verify_artifact_file_sha256(root / lic_rel, lic_sha or "", canonical_lf=True)
            except ArtifactVerificationError as exc:
                errors.append(f"{art_id} license file: {exc}")

    # Verify datasets & notices
    datasets = data.get("datasets", {})
    if set(datasets.keys()) != {"CHASE_DB1", "HRF45", "AVRDB100", "Iowa40"}:
        errors.append("Datasets must list exactly CHASE_DB1, HRF45, AVRDB100, and Iowa40.")
    for name, info in datasets.items():
        if info.get("redistributedInGit") is not False:
            errors.append(f"Dataset {name} must keep redistributedInGit=false.")
        if name == "Iowa40":
            if (
                info.get("redistributedInZip") is not False
                or info.get("calibrationAllowed") is not False
                or info.get("manifestPath") is not None
            ):
                errors.append("Iowa40 must keep redistributedInZip=false, calibrationAllowed=false, manifestPath=null.")
            continue
        for path_key, sha_key in (("manifestPath", "manifestSha256Lf"), ("validationReportPath", "validationReportSha256Lf")):
            try:
                verify_artifact_file_sha256(root / info.get(path_key, ""), info.get(sha_key, ""), canonical_lf=True)
            except ArtifactVerificationError as exc:
                errors.append(f"Dataset {name} ({path_key}): {exc}")

    for notice in data.get("thirdPartyNotices", []):
        try:
            verify_artifact_file_sha256(root / notice.get("path", ""), notice.get("sha256Lf", ""), canonical_lf=True)
        except ArtifactVerificationError as exc:
            errors.append(f"Third-party notice: {exc}")

    # Cross-check Model Card V2 registry and S7 report when present
    mc_path = root / "docs" / "benchmarks" / "model_card_v2_registry.json"
    if mc_path.is_file():
        mc = json.loads(mc_path.read_text(encoding="utf-8"))
        for ds in ("CHASE_DB1", "HRF45", "AVRDB100"):
            if datasets.get(ds, {}).get("manifestSha256Lf") != mc.get("datasets", {}).get(ds, {}).get("manifestSha256"):
                errors.append(f"Manifest SHA mismatch for {ds} between W8 registry and model_card_v2_registry.json.")

    s7_path = root / "docs" / "benchmarks" / "v2_evaluation_report_s7.json"
    if s7_path.is_file():
        s7 = json.loads(s7_path.read_text(encoding="utf-8"))
        if s7.get("scientificGateScrum373", {}).get("status") != gate.get("status"):
            errors.append("SCRUM-373 status diverged from v2_evaluation_report_s7.json.")

    git_violations = audit_git_hygiene(root)
    errors.extend(git_violations)

    return {
        "ok": len(errors) == 0,
        "errors": errors,
        "localArtifactStatuses": local_statuses,
        "gitHygieneClean": len(git_violations) == 0,
    }


def _assert_safe_repro_member(rel_posix: str) -> None:
    low = rel_posix.lower()
    if "iowa" in low:
        raise ArtifactVerificationError(
            f"Refusing to bundle forbidden Iowa dataset reference into ZIP: {rel_posix}"
        )
    for pat in _FORBIDDEN_SECRET_PATTERNS:
        if pat.search(low):
            raise ArtifactVerificationError(f"Refusing to bundle credential/secret into ZIP: {rel_posix}")
    if any(low.endswith(ext) for ext in _FORBIDDEN_WEIGHT_EXTS + (".jpg", ".jpeg", ".png")):
        raise ArtifactVerificationError(
            f"Refusing to bundle raw dataset image or binary weight into repro pack: {rel_posix}"
        )


def build_reproducibility_pack(
    output_zip: Path,
    registry_path: Path = DEFAULT_REGISTRY_PATH,
    root: Path = REPO_ROOT,
    extra_source_dir: Optional[Path] = None,
) -> Dict[str, Any]:
    """Create a deterministic metadata/config reproducibility ZIP after fail-closed verification."""
    report = verify_w8_registry(registry_path=registry_path, root=root, require_local_weights=False)
    if not report["ok"]:
        raise ArtifactVerificationError(
            "Refusing to build reproducibility pack because registry verification failed: "
            + "; ".join(report["errors"])
        )

    if extra_source_dir is not None:
        if not extra_source_dir.is_dir():
            raise ArtifactVerificationError(f"Missing extra_source_dir: {extra_source_dir}")
        for candidate in sorted(extra_source_dir.rglob("*")):
            if candidate.is_file():
                rel = candidate.relative_to(extra_source_dir).as_posix()
                _assert_safe_repro_member(rel)

    reg_data = json.loads(registry_path.read_text(encoding="utf-8"))
    bundle_paths: List[str] = [
        registry_path.resolve().relative_to(root.resolve()).as_posix(),
        reg_data["runtimeManifestCatalog"]["catalogPath"],
    ]
    for item in reg_data.get("artifacts", []):
        if item.get("trackedInGit") is True:
            bundle_paths.append(item["path"])
        if item.get("licenseFileInRepo"):
            bundle_paths.append(item["licenseFileInRepo"])
    for ds_name, ds_info in reg_data.get("datasets", {}).items():
        if ds_name == "Iowa40":
            continue
        bundle_paths.append(ds_info["manifestPath"])
        bundle_paths.append(ds_info["validationReportPath"])
    for notice in reg_data.get("thirdPartyNotices", []):
        bundle_paths.append(notice["path"])
    if (root / _REPRO_GUIDE_REL).is_file():
        bundle_paths.append(_REPRO_GUIDE_REL)

    dedup_paths = sorted(set(bundle_paths))
    for rel in dedup_paths:
        _assert_safe_repro_member(rel)
        if not (root / rel).is_file():
            raise ArtifactVerificationError(f"Missing reproducibility pack member: {rel}")

    output_zip.parent.mkdir(parents=True, exist_ok=True)
    tmp_zip = output_zip.with_suffix(".tmp.zip")
    entries_meta: List[Dict[str, Any]] = []
    try:
        with zipfile.ZipFile(tmp_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
            for rel in dedup_paths:
                abs_file = root / rel
                content = abs_file.read_bytes()
                zinfo = zipfile.ZipInfo(rel, date_time=(2026, 11, 24, 0, 0, 0))
                zinfo.compress_type = zipfile.ZIP_DEFLATED
                zf.writestr(zinfo, content)
                entries_meta.append(
                    {
                        "path": rel,
                        "sizeBytes": len(content),
                        "sha256Raw": hashlib.sha256(content).hexdigest(),
                        "sha256Lf": sha256_lf(abs_file),
                    }
                )
            manifest = {
                "packVersion": "1.0.0",
                "taskId": "W8-M4-01",
                "jiraKey": "SCRUM-423",
                "iowaDatasetIncluded": False,
                "credentialsIncluded": False,
                "fileCount": len(entries_meta),
                "files": entries_meta,
            }
            zinfo = zipfile.ZipInfo("PACK_MANIFEST.json", date_time=(2026, 11, 24, 0, 0, 0))
            zinfo.compress_type = zipfile.ZIP_DEFLATED
            zf.writestr(zinfo, json.dumps(manifest, indent=2, ensure_ascii=False).encode("utf-8"))
        tmp_zip.replace(output_zip)
    finally:
        if tmp_zip.exists():
            tmp_zip.unlink()

    return {
        "outputZip": str(output_zip),
        "fileCount": len(entries_meta),
        "iowaDatasetIncluded": False,
        "packSha256": sha256_raw(output_zip),
    }


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Verify W8 M4 artifact registry and build clean repro packs.")
    parser.add_argument("--verify", action="store_true", help="Verify W8 registry hashes, licenses, and Git hygiene.")
    parser.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY_PATH, help="Path to w8_artifact_registry_v1.0.json")
    parser.add_argument("--require-local-weights", action="store_true", help="Fail if local model binaries are absent.")
    parser.add_argument("--verify-artifact", type=str, default=None, help="Verify a single artifact ID against --file.")
    parser.add_argument("--file", type=Path, default=None, help="Local artifact path for --verify-artifact.")
    parser.add_argument("--bundle-repro-pack", type=Path, default=None, help="Output ZIP path for license-clean metadata/config pack.")
    args = parser.parse_args(argv)

    if args.verify_artifact:
        if args.file is None:
            print("FAIL: --file is required with --verify-artifact.", file=sys.stderr)
            return 1
        data = json.loads(args.registry.read_text(encoding="utf-8"))
        matches = [a for a in data.get("artifacts", []) if a.get("id") == args.verify_artifact]
        if not matches:
            print(f"FAIL: Unknown artifact id {args.verify_artifact!r}", file=sys.stderr)
            return 1
        item = matches[0]
        canonical_lf = item.get("trackedInGit") is True
        expected = item.get("sha256Lf") if canonical_lf else item.get("sha256Raw")
        try:
            actual = verify_artifact_file_sha256(args.file, expected or "", canonical_lf=canonical_lf)
        except ArtifactVerificationError as exc:
            print(f"FAIL: {exc}", file=sys.stderr)
            return 1
        print(json.dumps({"ok": True, "artifactId": args.verify_artifact, "sha256": actual}, indent=2))
        return 0

    if args.bundle_repro_pack:
        try:
            summary = build_reproducibility_pack(args.bundle_repro_pack, registry_path=args.registry, root=REPO_ROOT)
        except ArtifactVerificationError as exc:
            print(f"FAIL: {exc}", file=sys.stderr)
            return 1
        print(json.dumps(summary, indent=2, ensure_ascii=False))
        return 0

    result = verify_w8_registry(
        registry_path=args.registry,
        root=REPO_ROOT,
        require_local_weights=args.require_local_weights,
    )
    if not result["ok"]:
        for err in result["errors"]:
            print(f"FAIL: {err}", file=sys.stderr)
        return 1
    print(
        "PASS: W8-M4-01 artifact registry verified "
        f"(gitHygieneClean={result['gitHygieneClean']}, artifacts={len(result['localArtifactStatuses'])}, SCRUM-373=OPEN_NOT_PASSED)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
