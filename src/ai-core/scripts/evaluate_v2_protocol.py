"""Sprint 7 W7-M4-01 (SCRUM-405): Reproducible V2 evaluation protocol and verifier.

Evaluates locked public cohorts independently without pooling datasets or hiding
abstentions, audits RGB-only inference signatures, pins canonical LF SHA-256
checksums for manifests and configs, and keeps scientific gate SCRUM-373
explicitly separate (OPEN_NOT_PASSED) until verified on the 40-case Iowa table.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import inspect
import json
import math
import platform
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

import numpy as np
from PIL import Image

REPO_ROOT = Path(__file__).resolve().parents[3]
AI_CORE_DIR = REPO_ROOT / "src" / "ai-core"
if str(AI_CORE_DIR) not in sys.path:
    sys.path.insert(0, str(AI_CORE_DIR))

from app.services.frangi_service import (  # noqa: E402
    estimate_prediction_region,
    load_frangi_config,
    process_frangi_analysis,
    run_frangi_segmentation,
)
from app.services.avr_service import calculate_automatic_avr  # noqa: E402
from app.services.dr_cnn_service import screen_dr_cnn  # noqa: E402

DEFAULT_OUTPUT_PATH = REPO_ROOT / "docs" / "benchmarks" / "v2_evaluation_report_s7.json"
DEFAULT_DR_CHECKPOINT = REPO_ROOT / "model-artifacts" / "dr-resnet34-hrf-v1" / "model.pt"
EXPECTED_DR_CHECKPOINT_SHA256 = "8c9114c54f1b06300081bb82b80a0ea3a10e3d035232e1c5f3570b2db42df8f7"
INSPIRE_BENCHMARK_PATH = REPO_ROOT / "docs" / "benchmarks" / "inspire_avr_benchmark_v1.0.json"

FORBIDDEN_CLINICAL_CLAIMS = (
    "icdr grading pass",
    "clinically certified",
    "overall accuracy across all tasks",
    "bias gate passed",
    "scrum-373 closed",
)

FORBIDDEN_GT_PARAMS = {
    "gt_mask",
    "ground_truth",
    "artery_gt_mask",
    "vein_gt_mask",
    "reference_mask",
    "manual_mask",
    "label",
    "labels",
}


def sha256_lf(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def sha256_raw(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def git_commit_sha(root: Path = REPO_ROOT) -> str:
    try:
        out = subprocess.check_output(
            ["git", "rev-parse", "HEAD"],
            cwd=str(root),
            stderr=subprocess.DEVNULL,
            text=True,
        ).strip()
        return out or "UNKNOWN"
    except Exception:
        return "UNKNOWN"


def roc_auc_exact(y_true: Sequence[int], y_score: Sequence[float]) -> Optional[float]:
    """Exact Mann-Whitney U ROC-AUC with mid-rank tie handling (no sklearn dependency)."""
    pos = [float(s) for y, s in zip(y_true, y_score) if int(y) == 1]
    neg = [float(s) for y, s in zip(y_true, y_score) if int(y) == 0]
    if not pos or not neg:
        return None
    wins = 0.0
    for p in pos:
        for n in neg:
            if p > n:
                wins += 1.0
            elif p == n:
                wins += 0.5
    return round(wins / (len(pos) * len(neg)), 6)


def binary_segmentation_metrics(pred: np.ndarray, gt: np.ndarray) -> Dict[str, Any]:
    pred_b = pred.astype(bool)
    gt_b = gt.astype(bool)
    tp = int(np.count_nonzero(pred_b & gt_b))
    fp = int(np.count_nonzero(pred_b & ~gt_b))
    fn = int(np.count_nonzero(~pred_b & gt_b))
    tn = int(np.count_nonzero(~pred_b & ~gt_b))
    dice = (2.0 * tp) / (2.0 * tp + fp + fn) if (2 * tp + fp + fn) > 0 else 0.0
    iou = tp / (tp + fp + fn) if (tp + fp + fn) > 0 else 0.0
    sensitivity = tp / (tp + fn) if (tp + fn) > 0 else 0.0
    specificity = tn / (tn + fp) if (tn + fp) > 0 else 0.0
    return {
        "dice": round(dice, 6),
        "iou": round(iou, 6),
        "sensitivity": round(sensitivity, 6),
        "specificity": round(specificity, 6),
        "tp": tp,
        "fp": fp,
        "fn": fn,
        "tn": tn,
    }


def audit_rgb_only_inference() -> Dict[str, Any]:
    targets = {
        "run_frangi_segmentation": run_frangi_segmentation,
        "process_frangi_analysis": process_frangi_analysis,
        "screen_dr_cnn": screen_dr_cnn,
        "calculate_automatic_avr": calculate_automatic_avr,
    }
    signatures: Dict[str, List[str]] = {}
    violations: List[str] = []
    for name, fn in targets.items():
        params = list(inspect.signature(fn).parameters.keys())
        signatures[name] = params
        for p in params:
            if p.lower() in FORBIDDEN_GT_PARAMS:
                violations.append(f"{name} exposes forbidden ground-truth parameter {p!r}")
    return {
        "rgbOnlyInferenceVerified": len(violations) == 0,
        "checkedFunctions": signatures,
        "violations": violations,
    }


def evaluate_chase_db1(root: Path = REPO_ROOT) -> Dict[str, Any]:
    manifest_rel = "docs/datasets/chase_db1/manifest.csv"
    manifest_path = root / manifest_rel
    config_rel = "src/ai-core/config/ai-frangi.yaml"
    config_path = root / config_rel

    rows: List[Dict[str, str]] = []
    with manifest_path.open("r", encoding="utf-8-sig", newline="") as f:
        for row in csv.DictReader(f):
            if row["split"].strip().lower() == "test":
                rows.append(row)

    denominator = len(rows)
    base: Dict[str, Any] = {
        "cohortId": "CHASE_DB1",
        "split": "test",
        "task": "retinal_vessel_segmentation_frangi",
        "manifestPath": manifest_rel,
        "manifestSha256": sha256_lf(manifest_path),
        "configPath": config_rel,
        "configSha256": sha256_lf(config_path),
        "denominator": denominator,
        "measured": 0,
        "unavailable": denominator,
        "coverage": 0.0,
        "reproduceCommand": "PYTHONPATH=src/ai-core python src/ai-core/scripts/evaluate_v2_protocol.py --output docs/benchmarks/v2_evaluation_report_s7.json",
    }

    missing_files = [
        r["image"]
        for r in rows
        if not (root / r["image"]).is_file() or not (root / r["mask_1st"]).is_file()
    ]
    if missing_files:
        return {
            **base,
            "status": "NOT_TESTED_MISSING_DATASET",
            "reason": f"Missing {len(missing_files)}/{denominator} CHASE_DB1 test image/mask files locally.",
            "metrics": None,
            "perImage": [],
        }

    cfg = load_frangi_config(config_path)
    per_image: List[Dict[str, Any]] = []
    for row in rows:
        img_path = root / row["image"]
        gt_path = root / row["mask_1st"]
        rgb = np.asarray(Image.open(img_path).convert("RGB"), dtype=np.uint8)
        gt = np.asarray(Image.open(gt_path).convert("L"), dtype=np.uint8) > 127
        interior = estimate_prediction_region(rgb, cfg)
        pred_mask, _ = run_frangi_segmentation(rgb, interior, cfg)
        m = binary_segmentation_metrics(pred_mask, gt)
        per_image.append(
            {
                "subjectId": row["subject_id"],
                "eyeSide": row["eye_side"],
                "image": row["image"],
                "width": int(rgb.shape[1]),
                "height": int(rgb.shape[0]),
                "metrics": m,
            }
        )

    measured = len(per_image)
    mean_dice = round(float(np.mean([p["metrics"]["dice"] for p in per_image])), 6)
    mean_iou = round(float(np.mean([p["metrics"]["iou"] for p in per_image])), 6)
    mean_sens = round(float(np.mean([p["metrics"]["sensitivity"] for p in per_image])), 6)
    mean_spec = round(float(np.mean([p["metrics"]["specificity"] for p in per_image])), 6)
    return {
        **base,
        "status": "EVALUATED",
        "measured": measured,
        "unavailable": denominator - measured,
        "coverage": round(measured / denominator, 6) if denominator else 0.0,
        "metrics": {
            "meanDice": mean_dice,
            "meanIou": mean_iou,
            "meanSensitivity": mean_sens,
            "meanSpecificity": mean_spec,
        },
        "perImage": per_image,
    }


def evaluate_hrf_dr(
    root: Path = REPO_ROOT,
    checkpoint_path: Path = DEFAULT_DR_CHECKPOINT,
) -> Dict[str, Any]:
    manifest_rel = "docs/datasets/hrf/manifest.csv"
    manifest_path = root / manifest_rel

    val_rows: List[Dict[str, str]] = []
    test_rows: List[Dict[str, str]] = []
    ood_rows: List[Dict[str, str]] = []
    with manifest_path.open("r", encoding="utf-8-sig", newline="") as f:
        for row in csv.DictReader(f):
            cat = row["category"].strip().lower()
            split = row["split"].strip().lower()
            if cat == "g":
                ood_rows.append(row)
            elif split == "validation":
                val_rows.append(row)
            elif split == "test":
                test_rows.append(row)

    denominator = len(test_rows)
    base: Dict[str, Any] = {
        "cohortId": "HRF45",
        "task": "hrf_dr_vs_healthy_binary_screening_and_gradcam",
        "notIcdrGrading": True,
        "clinicalValidation": False,
        "glaucomaOodExcludedFromTestAuc": True,
        "gradcamMethod": "GRAD_CAM",
        "gradcamGeometry": "IMAGENET_CENTER_CROP_224",
        "gradcamTransformContract": "Resize(shorter_edge=256) -> CenterCrop(224x224) -> Normalize(ImageNet)",
        "manifestPath": manifest_rel,
        "manifestSha256": sha256_lf(manifest_path),
        "checkpointPath": str(checkpoint_path.relative_to(root)).replace("\\", "/")
        if checkpoint_path.is_relative_to(root)
        else str(checkpoint_path),
        "checkpointSha256": EXPECTED_DR_CHECKPOINT_SHA256,
        "denominator": denominator,
        "measured": 0,
        "unavailable": denominator,
        "coverage": 0.0,
        "reproduceCommand": "PYTHONPATH=src/ai-core python src/ai-core/scripts/evaluate_v2_protocol.py --output docs/benchmarks/v2_evaluation_report_s7.json",
    }

    if not checkpoint_path.is_file():
        return {
            **base,
            "status": "NOT_TESTED_MISSING_CHECKPOINT",
            "reason": f"Checkpoint missing at {checkpoint_path}.",
            "splits": {},
        }

    actual_ckpt_sha = sha256_raw(checkpoint_path)
    if actual_ckpt_sha != EXPECTED_DR_CHECKPOINT_SHA256:
        return {
            **base,
            "status": "NOT_TESTED_CHECKPOINT_HASH_MISMATCH",
            "reason": f"Expected {EXPECTED_DR_CHECKPOINT_SHA256}, observed {actual_ckpt_sha}.",
            "splits": {},
        }

    needed = val_rows + test_rows + ood_rows
    missing_imgs = [r["image_path"] for r in needed if not (root / r["image_path"]).is_file()]
    if missing_imgs:
        return {
            **base,
            "status": "NOT_TESTED_MISSING_DATASET",
            "reason": f"Missing {len(missing_imgs)}/{len(needed)} HRF images locally.",
            "splits": {},
        }

    try:
        import torch  # noqa: F401
        import torchvision  # noqa: F401
    except ImportError as exc:
        return {
            **base,
            "status": "NOT_TESTED_MISSING_TORCH_DEPENDENCY",
            "reason": f"PyTorch/torchvision not installed in current environment: {exc}",
            "splits": {},
        }

    def run_split(split_rows: List[Dict[str, str]], compute_auc: bool) -> Dict[str, Any]:
        cases: List[Dict[str, Any]] = []
        y_true: List[int] = []
        y_prob: List[float] = []
        gradcam_ok = True
        for r in split_rows:
            rgb = np.asarray(Image.open(root / r["image_path"]).convert("RGB"), dtype=np.uint8)
            out = screen_dr_cnn(rgb, str(checkpoint_path), actual_ckpt_sha)
            prob = float(out["probability_dr"])
            explain = out["explainability"]
            if (
                not math.isfinite(prob)
                or not (0.0 <= prob <= 1.0)
                or explain.get("method") != "GRAD_CAM"
                or explain.get("geometry") != "IMAGENET_CENTER_CROP_224"
                or not explain.get("overlay_base64_png")
            ):
                gradcam_ok = False
            label = int(r["label"])
            y_true.append(label)
            y_prob.append(prob)
            cases.append(
                {
                    "patientId": r["patient_id"],
                    "category": r["category"],
                    "label": label,
                    "probabilityDr": round(prob, 6),
                }
            )
        summary: Dict[str, Any] = {
            "count": len(cases),
            "meanProbabilityDr": round(float(np.mean(y_prob)), 6) if y_prob else None,
            "minProbabilityDr": round(float(np.min(y_prob)), 6) if y_prob else None,
            "maxProbabilityDr": round(float(np.max(y_prob)), 6) if y_prob else None,
            "gradcamContractVerified": gradcam_ok,
            "cases": cases,
        }
        if compute_auc:
            summary["rocAuc"] = roc_auc_exact(y_true, y_prob)
            summary["accuracyAt05"] = round(
                float(np.mean([(1 if p >= 0.5 else 0) == y for p, y in zip(y_prob, y_true)])), 6
            )
        else:
            summary["rocAuc"] = None
            summary["note"] = "Glaucoma OOD cohort reported separately; excluded from DR vs Healthy AUC."
        return summary

    val_summary = run_split(val_rows, compute_auc=True)
    test_summary = run_split(test_rows, compute_auc=True)
    ood_summary = run_split(ood_rows, compute_auc=False)

    measured = test_summary["count"]
    return {
        **base,
        "status": "EVALUATED",
        "measured": measured,
        "unavailable": denominator - measured,
        "coverage": round(measured / denominator, 6) if denominator else 0.0,
        "splits": {
            "validation_dr_vs_h": val_summary,
            "test_dr_vs_h": test_summary,
            "glaucoma_ood_separate": ood_summary,
        },
    }


def evaluate_avrdb_inspire(root: Path = REPO_ROOT) -> Dict[str, Any]:
    manifest_rel = "docs/datasets/inspire_avr/manifest.csv"
    manifest_path = root / manifest_rel
    rows: List[Dict[str, str]] = []
    with manifest_path.open("r", encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
    denominator = len(rows)

    bench_summary: Dict[str, Any] = {}
    measured = 0
    unavailable = denominator
    if INSPIRE_BENCHMARK_PATH.is_file():
        ref = json.loads(INSPIRE_BENCHMARK_PATH.read_text(encoding="utf-8"))
        # Preserve local runtime coverage honestly (0/99 because automatic RRWNet weights are not local),
        # while citing the historical 29-case validation+test reference metrics verbatim.
        bio = ref.get("automated_biological_metrics", {})
        gt_cmp = ref.get("ground_truth_comparison_summary", {})
        perf = ref.get("performance_latency", {})
        bench_summary = {
            "referenceBenchmarkPath": "docs/benchmarks/inspire_avr_benchmark_v1.0.json",
            "referenceBenchmarkSha256": sha256_lf(INSPIRE_BENCHMARK_PATH),
            "referenceEvaluationCohortSize": int(ref.get("total_evaluated_samples", 0)),
            "referenceMetrics": {
                "meanAvr": bio.get("mean_avr"),
                "stdAvr": bio.get("std_avr"),
                "meanCraePx": bio.get("mean_crae_px"),
                "meanCrvePx": bio.get("mean_crve_px"),
                "meanGtAvr": gt_cmp.get("mean_gt_avr"),
                "meanAvrMae": gt_cmp.get("mean_avr_mae"),
                "meanAvrBias": gt_cmp.get("mean_avr_bias"),
                "meanVesselDice": gt_cmp.get("mean_vessel_dice"),
                "p50LatencyMs": perf.get("median_p50_ms"),
                "p95LatencyMs": perf.get("p95_ms"),
            },
        }

    return {
        "cohortId": "AVRDB100",
        "task": "zone_b_avr_measurement_and_abstention_coverage",
        "status": "REFERENCED_BENCHMARK_NO_LOCAL_AVR_WEIGHTS",
        "manifestPath": manifest_rel,
        "manifestSha256": sha256_lf(manifest_path),
        "denominator": denominator,
        "measured": measured,
        "unavailable": unavailable,
        "coverage": round(measured / denominator, 6) if denominator else 0.0,
        "unavailableIncludedInDenominator": True,
        "abstentionPolicy": {
            "returnsNullWithReason": True,
            "neverSubstitutesZeroOrLowRisk": True,
        },
        "note": (
            "Automatic RRWNet/ONNX weights are not bundled in Git; coverage preserves unavailable cases in denominator "
            "and historical INSPIRE_AVR reference metrics are cited without fabricating a new runtime score."
        ),
        **bench_summary,
    }


def evaluate_iowa40() -> Dict[str, Any]:
    return {
        "cohortId": "Iowa40",
        "task": "independent_two_observer_avr_agreement_benchmark",
        "status": "NOT_TESTED_MISSING_EXTERNAL_DATASET",
        "manifestPath": None,
        "manifestSha256": None,
        "denominator": 40,
        "measured": 0,
        "unavailable": 40,
        "coverage": 0.0,
        "calibrationAllowed": False,
        "redistributedInGit": False,
        "reason": "External 40-case University of Iowa two-observer table is not bundled in workspace/CI; reported honestly as NOT_TESTED.",
    }


def build_report(root: Path = REPO_ROOT, checkpoint_path: Path = DEFAULT_DR_CHECKPOINT) -> Dict[str, Any]:
    config_paths = [
        "src/ai-core/config/ai-frangi.yaml",
        "src/ai-core/config/ai-frangi-v1.3.yaml",
        "src/ai-core/config/ai-frangi-hrf.yaml",
    ]
    pinned_configs = {
        rel: sha256_lf(root / rel)
        for rel in config_paths
        if (root / rel).is_file()
    }
    manifest_paths = [
        "docs/datasets/chase_db1/manifest.csv",
        "docs/datasets/hrf/manifest.csv",
        "docs/datasets/inspire_avr/manifest.csv",
    ]
    pinned_manifests = {
        rel: sha256_lf(root / rel)
        for rel in manifest_paths
        if (root / rel).is_file()
    }

    cohorts = {
        "CHASE_DB1": evaluate_chase_db1(root),
        "HRF45": evaluate_hrf_dr(root, checkpoint_path=checkpoint_path),
        "AVRDB100": evaluate_avrdb_inspire(root),
        "Iowa40": evaluate_iowa40(),
    }

    return {
        "reportVersion": "2.0.0-s7",
        "taskId": "W7-M4-01",
        "jiraKey": "SCRUM-405",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "gitCommitSha": git_commit_sha(root),
        "environment": {
            "python": platform.python_version(),
            "platform": platform.platform(),
            "numpy": np.__version__,
        },
        "scope": "RESEARCH_ONLY_NOT_CLINICAL_CERTIFICATION",
        "clinicalValidation": False,
        "icdrGradingSupported": False,
        "functionalPipelineStatus": "VERIFIED_RESEARCH_ONLY",
        "scientificGateScrum373": {
            "jiraKey": "SCRUM-373",
            "status": "OPEN_NOT_PASSED",
            "autoCloseAllowed": False,
            "iowaGatePassed": False,
            "thresholdAbsBias": 0.02,
            "requiredCohort": "Iowa40 (40-case verified two-observer table)",
            "lastObservedBaselineBias": -0.2136,
            "lastObservedBaselineMae": 0.2335,
            "reason": (
                "Scientific agreement gate |bias| <= 0.02 on the 40-case Iowa two-observer table "
                "remains unpassed and is kept strictly separate from Sprint 7 functional delivery."
            ),
        },
        "rgbOnlyAudit": audit_rgb_only_inference(),
        "pinnedConfigSha256": pinned_configs,
        "pinnedManifestSha256": pinned_manifests,
        "cohorts": cohorts,
        "reproduceCommands": {
            "generateReport": "PYTHONPATH=src/ai-core python src/ai-core/scripts/evaluate_v2_protocol.py --output docs/benchmarks/v2_evaluation_report_s7.json",
            "verifyReport": "python src/ai-core/scripts/evaluate_v2_protocol.py --verify docs/benchmarks/v2_evaluation_report_s7.json",
            "verifyModelCardRegistry": "python src/ai-core/scripts/verify_model_card_v2_registry.py",
        },
    }


def verify_report(report_path: Path = DEFAULT_OUTPUT_PATH, root: Path = REPO_ROOT) -> List[str]:
    errors: List[str] = []
    if not report_path.is_file():
        return [f"Missing V2 evaluation report: {report_path}"]

    raw = report_path.read_text(encoding="utf-8")
    lowered = raw.lower()
    for phrase in FORBIDDEN_CLINICAL_CLAIMS:
        if phrase in lowered:
            errors.append(f"Forbidden claim detected in report: {phrase!r}")

    data = json.loads(raw)
    if data.get("clinicalValidation") is not False or data.get("icdrGradingSupported") is not False:
        errors.append("Report must declare clinicalValidation=false and icdrGradingSupported=false.")

    gate = data.get("scientificGateScrum373", {})
    if gate.get("status") != "OPEN_NOT_PASSED":
        errors.append("SCRUM-373 scientific gate must remain OPEN_NOT_PASSED.")
    if gate.get("autoCloseAllowed") is not False or gate.get("iowaGatePassed") is not False:
        errors.append("SCRUM-373 must keep autoCloseAllowed=false and iowaGatePassed=false.")
    if gate.get("thresholdAbsBias") != 0.02:
        errors.append("SCRUM-373 thresholdAbsBias must remain 0.02.")

    rgb_audit = data.get("rgbOnlyAudit", {})
    if rgb_audit.get("rgbOnlyInferenceVerified") is not True or rgb_audit.get("violations"):
        errors.append("RGB-only inference signature audit must pass with zero violations.")

    for rel, expected_sha in data.get("pinnedConfigSha256", {}).items():
        abs_path = root / rel
        if not abs_path.is_file():
            errors.append(f"Missing pinned config: {rel}")
        elif sha256_lf(abs_path) != expected_sha:
            errors.append(f"Pinned config SHA-256 mismatch for {rel}")

    for rel, expected_sha in data.get("pinnedManifestSha256", {}).items():
        abs_path = root / rel
        if not abs_path.is_file():
            errors.append(f"Missing pinned manifest: {rel}")
        elif sha256_lf(abs_path) != expected_sha:
            errors.append(f"Pinned manifest SHA-256 mismatch for {rel}")

    cohorts = data.get("cohorts", {})
    expected_cohorts = {"CHASE_DB1", "HRF45", "AVRDB100", "Iowa40"}
    if set(cohorts.keys()) != expected_cohorts:
        errors.append(f"Expected 4 separated cohorts {sorted(expected_cohorts)}, got {sorted(cohorts.keys())}")

    for cid, entry in cohorts.items():
        denom = entry.get("denominator")
        meas = entry.get("measured")
        unavail = entry.get("unavailable")
        cov = entry.get("coverage")
        status_val = str(entry.get("status", ""))
        if not isinstance(denom, int) or denom <= 0:
            errors.append(f"Cohort {cid} must have positive integer denominator.")
            continue
        if not isinstance(meas, int) or meas < 0 or meas > denom:
            errors.append(f"Cohort {cid} measured count out of bounds: {meas}/{denom}.")
            continue
        if unavail != denom - meas:
            errors.append(f"Cohort {cid} must keep unavailable ({unavail}) == denominator - measured ({denom - meas}).")
        expected_cov = round(meas / denom, 6)
        if not isinstance(cov, (int, float)) or abs(cov - expected_cov) > 1e-5:
            errors.append(f"Cohort {cid} coverage mismatch: {cov} != {expected_cov}")
        if cid == "Iowa40" and not status_val.startswith("NOT_TESTED_"):
            errors.append(f"Iowa40 without external dataset must be NOT_TESTED_*, got {status_val!r}")
        if status_val == "PASS":
            errors.append(f"Cohort {cid} must not use misleading generic PASS status.")

    hrf = cohorts.get("HRF45", {})
    if (
        hrf.get("notIcdrGrading") is not True
        or hrf.get("glaucomaOodExcludedFromTestAuc") is not True
        or hrf.get("gradcamGeometry") != "IMAGENET_CENTER_CROP_224"
    ):
        errors.append("HRF45 cohort must preserve notIcdrGrading, OOD separation, and IMAGENET_CENTER_CROP_224.")

    avr = cohorts.get("AVRDB100", {})
    if avr.get("unavailableIncludedInDenominator") is not True:
        errors.append("AVRDB100 must explicitly keep unavailable cases in coverage denominator.")

    return errors


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Sprint 7 W7-M4-01 V2 evaluation protocol and verifier.")
    parser.add_argument("--output", type=Path, default=None, help="Generate evaluation JSON report at this path.")
    parser.add_argument("--verify", type=Path, default=None, help="Verify an existing V2 evaluation JSON report.")
    parser.add_argument(
        "--dr-checkpoint",
        type=Path,
        default=DEFAULT_DR_CHECKPOINT,
        help="Path to DR ResNet34 checkpoint (optional; reports NOT_TESTED if absent).",
    )
    args = parser.parse_args(argv)

    if args.verify is not None:
        errors = verify_report(args.verify)
        if errors:
            for err in errors:
                print(f"FAIL: {err}", file=sys.stderr)
            return 1
        print(f"PASS: V2 evaluation report verified ({args.verify}). SCRUM-373 remains OPEN_NOT_PASSED.")
        return 0

    output_path = args.output or DEFAULT_OUTPUT_PATH
    report = build_report(checkpoint_path=args.dr_checkpoint)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    errors = verify_report(output_path)
    if errors:
        for err in errors:
            print(f"FAIL after generation: {err}", file=sys.stderr)
        return 1
    print(f"PASS: Generated and verified V2 evaluation report at {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
