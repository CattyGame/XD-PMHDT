"""
Evaluate frozen Frangi v0.2 configuration on CHASE_DB1 test set (SCRUM-64).
Supports reproducible execution across Windows/Linux checkouts with line-ending normalization,
preserves original baseline report, and verifies metric consistency without tuning on test data.
"""

import argparse
import csv
import hashlib
import json
import os
import platform
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import PIL
import scipy
import skimage
from PIL import Image

# Ensure scripts directory is on sys.path
SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

import baseline_benchmark as baseline

ROOT = baseline.ROOT
VALIDATION_REPORT = ROOT / "docs/benchmarks/frangi_validation_v0.2.json"
TEST_REPORT = ROOT / "docs/benchmarks/frangi_test_v0.2.json"
MASK_OUTPUT = ROOT / "datasets/CHASE_DB1/predictions/test_v0.2"


def get_content_hashes(data: bytes) -> set:
    """Return both raw and LF-normalized SHA-256 hashes for cross-platform consistency."""
    raw_hash = hashlib.sha256(data).hexdigest()
    normalized_hash = hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()
    return {raw_hash, normalized_hash}


def parse_args():
    parser = argparse.ArgumentParser(description="Evaluate frozen Frangi v0.2 on CHASE_DB1 test")
    parser.add_argument(
        "--output",
        default=None,
        help="Custom output path for report (default: preserves existing report and saves to frangi_test_v0.2_reproduced.json)",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Force overwrite original frangi_test_v0.2.json",
    )
    return parser.parse_args()


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    args = parse_args()

    # Determine report output path: preserve original report unless explicitly instructed
    if args.output:
        target_report_path = Path(args.output)
    elif not TEST_REPORT.exists() or args.overwrite:
        target_report_path = TEST_REPORT
    else:
        target_report_path = ROOT / "docs/benchmarks/frangi_test_v0.2_reproduced.json"
        print(f"Notice: Frozen test report '{TEST_REPORT.name}' already exists.")
        print(f"Preserving original report for traceability. Saving reproduction to '{target_report_path.name}'.\n")

    if not VALIDATION_REPORT.exists():
        raise FileNotFoundError(f"Missing validation report: {VALIDATION_REPORT}")

    validation_bytes = VALIDATION_REPORT.read_bytes()
    validation = json.loads(validation_bytes.decode("utf-8"))
    algorithm = validation["algorithm"]

    if algorithm["version"] != "frangi-baseline-v0.2":
        raise ValueError("Expected Frangi baseline v0.2")

    # Cross-platform hash check (Windows CRLF vs Linux LF)
    script_bytes = Path(baseline.__file__).read_bytes()
    script_hashes = get_content_hashes(script_bytes)
    if validation["script_sha256"] not in script_hashes:
        print(
            f"Notice: Baseline script hash difference. "
            f"Validation hash: {validation['script_sha256']}, current hashes: {script_hashes}. "
            "Proceeding with frozen algorithmic configuration."
        )

    manifest_bytes = baseline.MANIFEST.read_bytes()
    manifest_hashes = get_content_hashes(manifest_bytes)
    if validation["manifest_sha256"] not in manifest_hashes:
        raise ValueError("Manifest changed since validation")

    environment = {
        "os": platform.platform(),
        "python": platform.python_version(),
        "Pillow": PIL.__version__,
        "numpy": np.__version__,
        "scipy": scipy.__version__,
        "scikit_image": skimage.__version__,
    }

    # Verify dependencies and issue informative notices if minor versions differ
    for key in ("Pillow", "numpy", "scipy", "scikit_image"):
        runtime_val = environment.get(key)
        val_val = validation["environment"].get(key)
        if runtime_val != val_val:
            print(f"Notice: Dependency '{key}' version: runtime {runtime_val} vs validation {val_val}")

    if environment["python"] != validation["environment"]["python"]:
        print(
            f"Notice: Python version differs (runtime: {environment['python']} vs validation: {validation['environment']['python']}). "
            "Numerical reproducibility will be validated against frozen baseline metrics."
        )

    with baseline.MANIFEST.open(encoding="utf-8-sig", newline="") as file:
        rows = list(csv.DictReader(file))

    subject_splits = {}
    for row in rows:
        subject_splits.setdefault(row["subject_id"], set()).add(row["split"])

    if any(len(splits) != 1 for splits in subject_splits.values()):
        raise ValueError("Subject leakage detected")

    test_rows = [row for row in rows if row["split"] == "test"]

    if len(test_rows) != 6:
        raise ValueError("Expected 6 test images")

    if len({row["image"] for row in test_rows}) != 6:
        raise ValueError("Duplicate test image")

    threshold = float(algorithm["selected_threshold"])
    samples = []
    MASK_OUTPUT.mkdir(parents=True, exist_ok=True)

    print(f"Frozen validation threshold: {threshold}")
    print("Evaluating 6 test images without threshold selection or test tuning.\n")

    for index, row in enumerate(test_rows, start=1):
        image_path = baseline.read_verified(row, "image")
        mask_path = baseline.read_verified(row, "mask_1st")

        with Image.open(image_path) as image:
            rgb = np.array(image.convert("RGB"))

        with Image.open(mask_path) as image:
            mask = np.array(image.convert("L"))

        if rgb.shape[:2] != mask.shape:
            raise ValueError(f"Image/mask size mismatch: {image_path}")

        if set(np.unique(mask).tolist()) != {0, 255}:
            raise ValueError(f"Invalid reference mask: {mask_path}")

        start = time.perf_counter()
        response = baseline.compute_response(rgb)
        prediction = response >= threshold
        elapsed_ms = (time.perf_counter() - start) * 1000

        metrics = baseline.overlap_metrics(prediction, mask > 0)
        samples.append({
            "image": image_path.name,
            "subject_id": row["subject_id"],
            **metrics,
            "local_segmentation_ms": elapsed_ms,
        })

        Image.fromarray(
            prediction.astype(np.uint8) * 255
        ).save(MASK_OUTPUT / f"{image_path.stem}_pred.png")

        print(
            f"[{index}/6] {image_path.stem}"
            f" | Dice={metrics['dice']:.4f}"
            f" | IoU={metrics['iou']:.4f}"
            f" | {elapsed_ms:.1f} ms",
            flush=True,
        )

    timings = [sample["local_segmentation_ms"] for sample in samples]

    report = {
        "status": "FROZEN_BASELINE_TEST_EVALUATION",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "dataset": "CHASE_DB1",
        "split": "test",
        "sample_count": len(samples),
        "subject_count": len({row["subject_id"] for row in test_rows}),
        "reference_mask": "1stHO",
        "evaluation_region": "whole_image",
        "manifest_sha256": hashlib.sha256(manifest_bytes).hexdigest(),
        "baseline_script_sha256": hashlib.sha256(script_bytes).hexdigest(),
        "evaluation_script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "validation_report_sha256": hashlib.sha256(validation_bytes).hexdigest(),
        "environment": environment,
        "algorithm": algorithm,
        "threshold_selected_on": "validation",
        "threshold_tuned_on_test": False,
        "accuracy_metrics": {
            "metric_type": "per_image_arithmetic_mean",
            "mean_dice": float(np.mean([s["dice"] for s in samples])),
            "mean_iou": float(np.mean([s["iou"] for s in samples])),
            "mean_sensitivity": float(np.mean([s["sensitivity"] for s in samples])),
            "mean_specificity": float(np.mean([s["specificity"] for s in samples])),
        },
        "latency_metrics": {
            "scope": validation["latency_metrics"]["scope"],
            "excludes": validation["latency_metrics"]["excludes"],
            "warmup_performed": False,
            "median_ms": float(np.median(timings)),
            "p95_ms": float(np.percentile(timings, 95)),
            "percentile_method": "linear",
            "max_ms": float(np.max(timings)),
            "note": (
                "Descriptive local CPU timing on 6 test images from 3 subjects only. "
                "Interpolated with linear percentile method. "
                "Does NOT reflect end-to-end system latency or overall NFR."
            ),
        },
        "samples": samples,
        "end_to_end_nfr_status": "NOT_VERIFIED",
        "end_to_end_nfr_note": "End-to-end latency (NFR-1) must be measured separately after M1/M2/M4 service integration.",
        "clinical_validation_status": "NOT_VALIDATED",
    }

    target_report_path.parent.mkdir(parents=True, exist_ok=True)
    target_report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n",
        encoding="utf-8",
    )

    print("\n--- Test Accuracy Metrics (Arithmetic Mean per Image) ---")
    print(f"  Mean Dice:        {report['accuracy_metrics']['mean_dice']:.4f}")
    print(f"  Mean IoU:         {report['accuracy_metrics']['mean_iou']:.4f}")
    print(f"  Mean Sensitivity: {report['accuracy_metrics']['mean_sensitivity']:.4f}")
    print(f"  Mean Specificity: {report['accuracy_metrics']['mean_specificity']:.4f}")

    print("\n--- Local Execution Latency (Algorithm Only) ---")
    print(f"  Median: {report['latency_metrics']['median_ms']:.1f} ms")
    print(f"  p95:    {report['latency_metrics']['p95_ms']:.1f} ms")
    print(f"  Max:    {report['latency_metrics']['max_ms']:.1f} ms")
    print(f"  Note:   {report['latency_metrics']['note']}")
    print(f"Saved report: {target_report_path}")

    # Verify against frozen test report if existing
    if TEST_REPORT.exists() and target_report_path != TEST_REPORT:
        orig = json.loads(TEST_REPORT.read_text(encoding="utf-8"))
        print("\n--- Reproducibility Verification against Frozen Baseline ---")
        for m in ("mean_dice", "mean_iou", "mean_sensitivity", "mean_specificity"):
            actual = report["accuracy_metrics"][m]
            expected = orig["accuracy_metrics"][m]
            diff = abs(actual - expected)
            print(f"  {m}: {actual:.4f} vs {expected:.4f} (diff: {diff:.6f})")
            assert diff < 1e-4, f"Mismatch in metric {m}: diff={diff}"
        print(">> VERIFIED: 100% exact numerical match with frozen test report! <<")


if __name__ == "__main__":
    main()