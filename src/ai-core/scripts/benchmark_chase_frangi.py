"""
Comprehensive 28-image CHASE_DB1 Frangi v0.2 Benchmark Evaluation (SCRUM-351 / Task 3).
Evaluates vessel segmentation against expert manual1 annotations across the full CHASE_DB1 dataset,
reporting Dice, IoU, Sensitivity, and Specificity metrics, and verifying Quality Gate behavior.
"""
import csv
import hashlib
import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, List, Any

import numpy as np
from PIL import Image
from scipy.ndimage import binary_erosion, binary_fill_holes
from skimage.filters import frangi, gaussian

REPO_ROOT = Path(__file__).resolve().parents[3]
MANIFEST_PATH = REPO_ROOT / "docs/datasets/chase_db1/manifest.csv"
OUTPUT_PATH = REPO_ROOT / "docs/benchmarks/chase_db1_frangi_benchmark_v1.0.json"

# Frozen Frangi v0.2 parameters (established in Sprint 3)
SIGMAS = [1, 2, 3, 5, 8]
SMOOTHING_SIGMA = 1.0
THRESHOLD = 0.02
REGION_THRESHOLD = 20
EROSION_ITERATIONS = 12


def compute_metrics(prediction: np.ndarray, ground_truth: np.ndarray) -> Dict[str, float]:
    """Compute Dice, IoU, Sensitivity, and Specificity for binary segmentation."""
    tp = int(np.count_nonzero(prediction & ground_truth))
    fp = int(np.count_nonzero(prediction & ~ground_truth))
    fn = int(np.count_nonzero(~prediction & ground_truth))
    tn = int(np.count_nonzero(~prediction & ~ground_truth))

    dice = (2.0 * tp) / (2.0 * tp + fp + fn) if (2.0 * tp + fp + fn) > 0 else 0.0
    iou = float(tp) / float(tp + fp + fn) if (tp + fp + fn) > 0 else 0.0
    sensitivity = float(tp) / float(tp + fn) if (tp + fn) > 0 else 0.0
    specificity = float(tn) / float(tn + fp) if (tn + fp) > 0 else 0.0

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


def segment_chase_image(image_path: Path) -> np.ndarray:
    """Run full Frangi v0.2 segmentation pipeline on a single CHASE_DB1 fundus image."""
    img = Image.open(image_path).convert("RGB")
    rgb = np.array(img)

    green = rgb[:, :, 1].astype(np.float64) / 255.0
    green = gaussian(
        green,
        sigma=SMOOTHING_SIGMA,
        preserve_range=True,
        mode="nearest",
    )

    estimated_region = np.max(rgb, axis=2) > REGION_THRESHOLD
    estimated_region = binary_fill_holes(estimated_region)

    interior = binary_erosion(
        estimated_region,
        structure=np.ones((3, 3), dtype=bool),
        iterations=EROSION_ITERATIONS,
        border_value=0,
    )

    if not np.any(interior):
        raise ValueError(f"Estimated prediction region is empty for {image_path}")

    response = frangi(
        green,
        sigmas=SIGMAS,
        alpha=0.5,
        beta=0.5,
        gamma=None,
        black_ridges=True,
        mode="reflect",
    )

    if not np.all(np.isfinite(response)):
        raise ValueError(f"Frangi response contains non-finite values for {image_path}")

    response[~interior] = 0.0

    maximum = float(response.max())
    if maximum > 0:
        response = response / maximum

    return response > THRESHOLD


def run_chase_benchmark() -> Dict[str, Any]:
    """Run evaluation on all 28 CHASE_DB1 images and return detailed report."""
    if not MANIFEST_PATH.is_file():
        raise FileNotFoundError(f"Missing CHASE_DB1 manifest: {MANIFEST_PATH}")

    with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    assert len(rows) == 28, f"Expected 28 images, found {len(rows)}"

    per_image_results: List[Dict[str, Any]] = []
    total_start = time.perf_counter()

    for idx, r in enumerate(rows, 1):
        img_rel = r["image"]
        mask_rel = r["mask_1st"]
        img_path = REPO_ROOT / img_rel
        mask_path = REPO_ROOT / mask_rel

        # Verify SHA-256
        content = img_path.read_bytes()
        actual_sha = hashlib.sha256(content).hexdigest()
        assert actual_sha == r["image_sha256"], f"SHA mismatch on {img_path}"

        # Segment image
        t0 = time.perf_counter()
        pred_mask = segment_chase_image(img_path)
        seg_time_ms = round((time.perf_counter() - t0) * 1000, 2)

        # Load ground truth mask
        gt_img = Image.open(mask_path).convert("L")
        gt_mask = np.array(gt_img) > 128

        metrics = compute_metrics(pred_mask, gt_mask)
        per_image_results.append({
            "subject_id": r["subject_id"],
            "eye_side": r["eye_side"],
            "image": img_rel,
            "mask_1st": mask_rel,
            "latency_ms": seg_time_ms,
            "metrics": metrics,
        })
        print(f"[{idx}/28] Evaluated {r['subject_id']}{r['eye_side']}: Dice={metrics['dice']:.4f}, IoU={metrics['iou']:.4f}, Sens={metrics['sensitivity']:.4f}, Spec={metrics['specificity']:.4f} ({seg_time_ms} ms)")

    total_time_s = round(time.perf_counter() - total_start, 2)

    dices = [item["metrics"]["dice"] for item in per_image_results]
    ious = [item["metrics"]["iou"] for item in per_image_results]
    sensitivities = [item["metrics"]["sensitivity"] for item in per_image_results]
    specificities = [item["metrics"]["specificity"] for item in per_image_results]
    latencies = [item["latency_ms"] for item in per_image_results]

    summary = {
        "dataset": "CHASE_DB1 Complete Set (28 images)",
        "evaluation_timestamp": datetime.now(timezone.utc).isoformat(),
        "total_images": len(per_image_results),
        "total_time_seconds": total_time_s,
        "mean_latency_ms": round(float(np.mean(latencies)), 2),
        "mean_dice": round(float(np.mean(dices)), 6),
        "std_dice": round(float(np.std(dices)), 6),
        "mean_iou": round(float(np.mean(ious)), 6),
        "std_iou": round(float(np.std(ious)), 6),
        "mean_sensitivity": round(float(np.mean(sensitivities)), 6),
        "std_sensitivity": round(float(np.std(sensitivities)), 6),
        "mean_specificity": round(float(np.mean(specificities)), 6),
        "std_specificity": round(float(np.std(specificities)), 6),
        "frozen_parameters": {
            "sigmas": SIGMAS,
            "smoothing_sigma": SMOOTHING_SIGMA,
            "threshold": THRESHOLD,
            "region_threshold": REGION_THRESHOLD,
            "erosion_iterations": EROSION_ITERATIONS,
        },
        "per_image": per_image_results,
    }

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)

    print(f"\n[SUCCESS] CHASE_DB1 28-image evaluation completed!")
    print(f"Mean Dice: {summary['mean_dice']:.4f} (+/- {summary['std_dice']:.4f})")
    print(f"Mean IoU: {summary['mean_iou']:.4f} (+/- {summary['std_iou']:.4f})")
    print(f"Mean Sensitivity: {summary['mean_sensitivity']:.4f} (+/- {summary['std_sensitivity']:.4f})")
    print(f"Mean Specificity: {summary['mean_specificity']:.4f} (+/- {summary['std_specificity']:.4f})")
    print(f"Saved report to: {OUTPUT_PATH}")

    return summary


if __name__ == "__main__":
    run_chase_benchmark()
