"""Evaluate frozen Frangi v0.2 configuration on CHASE_DB1 test."""

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

import baseline_benchmark as baseline


ROOT = baseline.ROOT
VALIDATION_REPORT = ROOT / "docs/benchmarks/frangi_validation_v0.2.json"
TEST_REPORT = ROOT / "docs/benchmarks/frangi_test_v0.2.json"
MASK_OUTPUT = ROOT / "datasets/CHASE_DB1/predictions/test_v0.2"


def main():
    if TEST_REPORT.exists():
        raise ValueError(
            "Test report already exists. Keep it for traceability; "
            "do not overwrite or tune the algorithm using test results."
        )

    validation_bytes = VALIDATION_REPORT.read_bytes()
    validation = json.loads(validation_bytes.decode("utf-8"))
    algorithm = validation["algorithm"]

    if algorithm["version"] != "frangi-baseline-v0.2":
        raise ValueError("Expected Frangi baseline v0.2")

    script_hash = hashlib.sha256(
        Path(baseline.__file__).read_bytes()
    ).hexdigest()

    if script_hash != validation["script_sha256"]:
        raise ValueError(
            "Baseline script changed since validation. "
            "Revalidate before evaluating test."
        )

    manifest_hash = hashlib.sha256(
        baseline.MANIFEST.read_bytes()
    ).hexdigest()

    if manifest_hash != validation["manifest_sha256"]:
        raise ValueError("Manifest changed since validation")

    environment = {
        "os": platform.platform(),
        "python": platform.python_version(),
        "Pillow": PIL.__version__,
        "numpy": np.__version__,
        "scipy": scipy.__version__,
        "scikit_image": skimage.__version__,
    }

    for key in ("python", "Pillow", "numpy", "scipy", "scikit_image"):
        if environment[key] != validation["environment"][key]:
            raise ValueError(f"Environment changed: {key}")

    with baseline.MANIFEST.open(
        encoding="utf-8-sig", newline=""
    ) as file:
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
    print("Evaluating 6 test images without threshold selection.\n")

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
        "manifest_sha256": manifest_hash,
        "baseline_script_sha256": script_hash,
        "evaluation_script_sha256": hashlib.sha256(
            Path(__file__).read_bytes()
        ).hexdigest(),
        "validation_report_sha256": hashlib.sha256(
            validation_bytes
        ).hexdigest(),
        "environment": environment,
        "algorithm": algorithm,
        "threshold_selected_on": "validation",
        "threshold_tuned_on_test": False,
        "accuracy_metrics": {
            f"mean_{metric}": float(np.mean([s[metric] for s in samples]))
            for metric in ("dice", "iou", "sensitivity", "specificity")
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
                "Descriptive timing on 6 images from 3 subjects; "
                "not a stable performance estimate or end-to-end latency."
            ),
        },
        "samples": samples,
        "end_to_end_nfr_status": "NOT_VERIFIED",
        "clinical_validation_status": "NOT_VALIDATED",
    }

    TEST_REPORT.parent.mkdir(parents=True, exist_ok=True)
    TEST_REPORT.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
        + "\n",
        encoding="utf-8",
    )

    print("\nTest results:")
    print(json.dumps(report["accuracy_metrics"], indent=2))
    print(json.dumps(report["latency_metrics"], indent=2))
    print("Saved report:", TEST_REPORT)


if __name__ == "__main__":
    main()