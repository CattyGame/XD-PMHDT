"""CHASE_DB1 Frangi baseline v0.2: validation threshold selection."""

import csv
import hashlib
import json
import platform
import time
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import PIL
import scipy
import skimage
from PIL import Image
from scipy.ndimage import binary_erosion, binary_fill_holes
from skimage.filters import frangi, gaussian


ROOT = Path(__file__).resolve().parents[3]
MANIFEST = ROOT / "docs/datasets/chase_db1/manifest.csv"
OUTPUT = ROOT / "docs/benchmarks"
MASK_OUTPUT = ROOT / "datasets/CHASE_DB1/predictions/validation_v0.2"

THRESHOLDS = [0.005, 0.01, 0.02, 0.03, 0.05, 0.08, 0.10, 0.15]
SIGMAS = [1, 2, 3, 5, 8]
SMOOTHING_SIGMA = 1.0
REGION_THRESHOLD = 20
EROSION_ITERATIONS = 12


def read_verified(row, key):
    path = ROOT / row[key]
    content = path.read_bytes()
    digest = hashlib.sha256(content).hexdigest()

    if digest != row[f"{key}_sha256"]:
        raise ValueError(f"SHA-256 mismatch: {path}")

    return path


def overlap_metrics(prediction, truth):
    tp = int(np.count_nonzero(prediction & truth))
    fp = int(np.count_nonzero(prediction & ~truth))
    fn = int(np.count_nonzero(~prediction & truth))
    tn = int(np.count_nonzero(~prediction & ~truth))

    return {
        "dice": 2 * tp / (2 * tp + fp + fn),
        "iou": tp / (tp + fp + fn),
        "sensitivity": tp / (tp + fn),
        "specificity": tn / (tn + fp),
        "tp": tp,
        "fp": fp,
        "fn": fn,
        "tn": tn,
    }


def compute_response(rgb):
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
        raise ValueError("Estimated prediction region is empty")

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
        raise ValueError("Frangi response contains non-finite values")

    response[~interior] = 0.0

    maximum = float(response.max())
    if maximum > 0:
        response = response / maximum

    return response


def main():
    with MANIFEST.open(encoding="utf-8-sig", newline="") as file:
        rows = list(csv.DictReader(file))

    subject_splits = {}
    for row in rows:
        subject_splits.setdefault(row["subject_id"], set()).add(row["split"])

    if any(len(splits) != 1 for splits in subject_splits.values()):
        raise ValueError("Subject leakage detected in manifest")

    validation = [row for row in rows if row["split"] == "validation"]

    if len(validation) != 6:
        raise ValueError("Expected exactly 6 validation images")

    if len({row["image"] for row in validation}) != len(validation):
        raise ValueError("Duplicate validation image in manifest")

    candidates = {threshold: [] for threshold in THRESHOLDS}
    cached = []

    print("CHASE_DB1 — Frangi baseline v0.2")
    print("Split: validation only")
    print("Evaluation: whole image")
    print("Test images will not be evaluated.\n")

    for index, row in enumerate(validation, start=1):
        image_path = read_verified(row, "image")
        mask_path = read_verified(row, "mask_1st")

        with Image.open(image_path) as image:
            rgb = np.array(image.convert("RGB"))

        with Image.open(mask_path) as image:
            mask = np.array(image.convert("L"))

        if rgb.shape[:2] != mask.shape:
            raise ValueError(f"Image/mask size mismatch: {image_path}")

        if set(np.unique(mask).tolist()) != {0, 255}:
            raise ValueError(f"Invalid reference mask: {mask_path}")

        truth = mask > 0

        start = time.perf_counter()
        response = compute_response(rgb)
        feature_ms = (time.perf_counter() - start) * 1000

        name = image_path.stem
        cached.append((name, response))

        for threshold in THRESHOLDS:
            start = time.perf_counter()
            prediction = response >= threshold
            threshold_ms = (time.perf_counter() - start) * 1000

            metrics = overlap_metrics(prediction, truth)

            candidates[threshold].append({
                "image": image_path.name,
                "subject_id": row["subject_id"],
                **metrics,
                "local_segmentation_ms": feature_ms + threshold_ms,
            })

        print(
            f"[{index}/{len(validation)}] {name}: "
            f"feature processing {feature_ms:.1f} ms",
            flush=True,
        )

    summaries = []

    for threshold, samples in candidates.items():
        summaries.append({
            "threshold": threshold,
            "mean_dice": float(np.mean([s["dice"] for s in samples])),
            "mean_iou": float(np.mean([s["iou"] for s in samples])),
        })

    best = max(summaries, key=lambda item: item["mean_dice"])
    threshold = best["threshold"]
    samples = candidates[threshold]
    timings = [sample["local_segmentation_ms"] for sample in samples]

    MASK_OUTPUT.mkdir(parents=True, exist_ok=True)

    for name, response in cached:
        prediction = (response >= threshold).astype(np.uint8) * 255
        Image.fromarray(prediction).save(MASK_OUTPUT / f"{name}_pred.png")

    report = {
        "status": "VALIDATION_THRESHOLD_SELECTION",
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        "dataset": "CHASE_DB1",
        "split": "validation",
        "sample_count": len(validation),
        "reference_mask": "1stHO",
        "evaluation_region": "whole_image",
        "manifest_sha256": hashlib.sha256(MANIFEST.read_bytes()).hexdigest(),
        "script_sha256": hashlib.sha256(
            Path(__file__).read_bytes()
        ).hexdigest(),
        "environment": {
            "os": platform.platform(),
            "python": platform.python_version(),
            "Pillow": PIL.__version__,
            "numpy": np.__version__,
            "scipy": scipy.__version__,
            "scikit_image": skimage.__version__,
        },
        "algorithm": {
            "name": "frangi",
            "version": "frangi-baseline-v0.2",
            "input": "green_channel_divided_by_255",
            "smoothing_sigma": SMOOTHING_SIGMA,
            "smoothing_boundary_mode": "nearest",
            "prediction_region": {
                "method": "max_rgb_threshold_fill_holes_erode",
                "max_rgb_threshold": REGION_THRESHOLD,
                "erosion_structure": "3x3_ones",
                "erosion_iterations": EROSION_ITERATIONS,
                "uses_reference_labels": False,
                "is_official_fov_mask": False,
            },
            "sigmas": SIGMAS,
            "alpha": 0.5,
            "beta": 0.5,
            "gamma": None,
            "black_ridges": True,
            "boundary_mode": "reflect",
            "response_normalization": "divide_by_max_after_region_suppression",
            "resize": False,
            "postprocessing": "region_suppression_and_threshold",
            "selected_threshold": threshold,
            "selection_metric": "highest_mean_validation_dice",
            "tie_break": "first_threshold_in_ascending_candidate_list",
        },
        "threshold_candidates": summaries,
        "accuracy_metrics": {
            f"mean_{metric}": float(np.mean([s[metric] for s in samples]))
            for metric in ("dice", "iou", "sensitivity", "specificity")
        },
        "latency_metrics": {
            "scope": (
                "green extraction, smoothing, region estimation, "
                "Frangi, normalization, threshold"
            ),
            "excludes": (
                "disk I/O, SHA-256 verification, metrics, PNG output, "
                "API, queue, database"
            ),
            "warmup_performed": False,
            "median_ms": float(np.median(timings)),
            "p95_ms": float(np.percentile(timings, 95)),
            "percentile_method": "linear",
            "max_ms": float(np.max(timings)),
            "note": (
                "Descriptive timing on 6 images; "
                "not a stable performance estimate or end-to-end latency."
            ),
        },
        "samples": samples,
        "test_evaluated": False,
        "end_to_end_nfr_status": "NOT_VERIFIED",
    }

    OUTPUT.mkdir(parents=True, exist_ok=True)
    report_path = OUTPUT / "frangi_validation_v0.2.json"

    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
        + "\n",
        encoding="utf-8",
    )

    print("\nThreshold comparison:")
    for item in summaries:
        print(
            f"  threshold={item['threshold']:.3f}"
            f" | Dice={item['mean_dice']:.4f}"
            f" | IoU={item['mean_iou']:.4f}"
        )

    print("\nSelected threshold:", threshold)
    print(json.dumps(report["accuracy_metrics"], indent=2))
    print("Saved report:", report_path)
    print("Saved prediction masks:", MASK_OUTPUT)


if __name__ == "__main__":
    main()