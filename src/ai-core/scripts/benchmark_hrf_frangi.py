"""
HRF Dataset Vessel Segmentation Benchmark Runner (SCRUM-371 / W5-M4-01).

Measures:
- Evaluates the optimized CLAHE + multi-resolution Frangi 2D pipeline on the HRF test split (3504x2336).
- Computes Dice, IoU, Sensitivity, Specificity against expert ground truth (manual1).
- Tracks p50, p95, mean, and max CPU latency.
- Tracks peak working set RAM (MiB) and tracemalloc allocations.
- Exports report to docs/benchmarks/frangi_hrf_benchmark_v1.0.json.
"""

from __future__ import annotations

import argparse
import csv
import gc
import json
import os
import platform
import sys
import time
import tracemalloc
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List

import numpy as np
import PIL
import scipy
import skimage
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[3]
AI_CORE_DIR = Path(__file__).resolve().parents[1]
if str(AI_CORE_DIR) not in sys.path:
    sys.path.insert(0, str(AI_CORE_DIR))

from app.services.image_preprocessor import (
    PreprocessorConfig,
    process_hrf_frangi_vessel_segmentation,
)

DEFAULT_OUTPUT_JSON = ROOT / "docs" / "benchmarks" / "frangi_hrf_benchmark_v1.0.json"
MANIFEST_PATH = ROOT / "docs" / "datasets" / "hrf" / "manifest.csv"


def get_process_rss_mb() -> float:
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes

        class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
            _fields_ = [
                ("cb", wintypes.DWORD),
                ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t),
            ]

        counters = PROCESS_MEMORY_COUNTERS()
        counters.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
        handle = ctypes.windll.kernel32.GetCurrentProcess()
        if ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return round(float(counters.WorkingSetSize) / (1024.0 * 1024.0), 2)
    return 0.0


def run_benchmark(output_path: Path = DEFAULT_OUTPUT_JSON, max_samples: int = 0) -> Dict[str, Any]:
    if not MANIFEST_PATH.is_file():
        raise FileNotFoundError(f"HRF manifest not found at {MANIFEST_PATH}. Run prepare_hrf.py first.")

    with MANIFEST_PATH.open("r", encoding="utf-8-sig", newline="") as f:
        rows = [r for r in csv.DictReader(f) if r["split"] == "test"]

    if max_samples > 0:
        rows = rows[:max_samples]

    print(f"Loaded {len(rows)} HRF test samples for benchmark.")

    config = PreprocessorConfig(
        clahe_clip_limit=0.02,
        tile_grid_size=(8, 8),
        max_processing_width=1400,
        frangi_sigmas=(1.0, 2.0, 3.0),
        binary_threshold=0.05,
    )

    samples: List[Dict[str, Any]] = []
    latencies_ms: List[float] = []
    clahe_latencies_ms: List[float] = []
    frangi_latencies_ms: List[float] = []
    dices: List[float] = []
    ious: List[float] = []
    sensitivities: List[float] = []
    specificities: List[float] = []
    rss_peaks: List[float] = []

    gc.collect()
    tracemalloc.start()
    baseline_rss = get_process_rss_mb()

    # Warmup on first sample
    if rows:
        warm_img = np.asarray(Image.open(ROOT / rows[0]["image_path"]).convert("RGB"), dtype=np.uint8)
        process_hrf_frangi_vessel_segmentation(warm_img, config=config)
        del warm_img
        gc.collect()

    for idx, row in enumerate(rows, start=1):
        img_p = ROOT / row["image_path"]
        manual_p = ROOT / row["manual_path"]

        with Image.open(img_p) as im:
            rgb = np.asarray(im.convert("RGB"), dtype=np.uint8)
        with Image.open(manual_p) as mim:
            gt_mask = np.asarray(mim.convert("L"), dtype=np.uint8) > 0

        gc.collect()
        rss_before = get_process_rss_mb()

        result = process_hrf_frangi_vessel_segmentation(
            rgb,
            config=config,
            ground_truth_mask=gt_mask,
        )

        rss_after = get_process_rss_mb()
        peak_rss = max(rss_before, rss_after)
        rss_peaks.append(peak_rss)

        m = result["metrics"]
        latencies_ms.append(m["total_latency_ms"])
        clahe_latencies_ms.append(m["clahe_latency_ms"])
        frangi_latencies_ms.append(m["frangi_latency_ms"])
        dices.append(m["dice"])
        ious.append(m["iou"])
        sensitivities.append(m["sensitivity"])
        specificities.append(m["specificity"])

        sample_record = {
            "patient_id": row["patient_id"],
            "category": row["category"],
            "image": Path(row["image_path"]).name,
            "original_resolution": m["original_resolution"],
            "processed_resolution": m["processed_resolution"],
            "downsample_scale": m["downsample_scale"],
            "total_latency_ms": m["total_latency_ms"],
            "clahe_latency_ms": m["clahe_latency_ms"],
            "frangi_latency_ms": m["frangi_latency_ms"],
            "upsample_latency_ms": m["upsample_latency_ms"],
            "vessel_density": m["vessel_density"],
            "dice": m["dice"],
            "iou": m["iou"],
            "sensitivity": m["sensitivity"],
            "specificity": m["specificity"],
            "peak_rss_mb": peak_rss,
        }
        samples.append(sample_record)
        print(
            f"[{idx}/{len(rows)}] {row['patient_id']} ({row['category']}): "
            f"Latency={m['total_latency_ms']}ms (CLAHE: {m['clahe_latency_ms']}ms, Frangi: {m['frangi_latency_ms']}ms), "
            f"Dice={m['dice']:.4f}, IoU={m['iou']:.4f}, Sens={m['sensitivity']:.4f}, Spec={m['specificity']:.4f}"
        )

    heap_current, heap_peak = tracemalloc.get_traced_memory()
    tracemalloc.stop()

    report: Dict[str, Any] = {
        "benchmark_name": "AURA_HRF_FRANGI_2D_V0_2_BENCHMARK",
        "task": "SCRUM-371 / W5-M4-01",
        "timestamp_utc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "dataset": "HRF (High-Resolution Fundus Image Database)",
        "split": "test",
        "sample_count": len(samples),
        "environment": {
            "os": platform.platform(),
            "python": platform.python_version(),
            "cpu_cores": os.cpu_count() or 1,
            "numpy": np.__version__,
            "scipy": scipy.__version__,
            "scikit_image": skimage.__version__,
            "pillow": PIL.__version__,
        },
        "configuration": {
            "clahe_clip_limit": config.clahe_clip_limit,
            "tile_grid_size": list(config.tile_grid_size),
            "max_processing_width": config.max_processing_width,
            "frangi_sigmas": list(config.frangi_sigmas),
            "frangi_alpha": config.frangi_alpha,
            "frangi_beta": config.frangi_beta,
            "frangi_gamma": config.frangi_gamma,
            "binary_threshold": config.binary_threshold,
        },
        "accuracy_metrics": {
            "mean_dice": round(float(np.mean(dices)), 6),
            "median_dice": round(float(np.median(dices)), 6),
            "mean_iou": round(float(np.mean(ious)), 6),
            "median_iou": round(float(np.median(ious)), 6),
            "mean_sensitivity": round(float(np.mean(sensitivities)), 6),
            "mean_specificity": round(float(np.mean(specificities)), 6),
        },
        "runtime_latency_ms": {
            "sla_target_ms": 1500.0,
            "mean_ms": round(float(np.mean(latencies_ms)), 2),
            "median_p50_ms": round(float(np.median(latencies_ms)), 2),
            "p95_ms": round(float(np.percentile(latencies_ms, 95)), 2),
            "max_ms": round(float(np.max(latencies_ms)), 2),
            "mean_clahe_ms": round(float(np.mean(clahe_latencies_ms)), 2),
            "mean_frangi_ms": round(float(np.mean(frangi_latencies_ms)), 2),
            "meets_sla_under_1_5s": bool(float(np.percentile(latencies_ms, 95)) < 1500.0),
        },
        "memory_telemetry": {
            "baseline_rss_mb": baseline_rss,
            "mean_rss_mb": round(float(np.mean(rss_peaks)), 2),
            "peak_rss_mb": round(float(np.max(rss_peaks)), 2),
            "tracemalloc_peak_heap_mb": round(heap_peak / (1024.0 * 1024.0), 2),
        },
        "samples": samples,
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"\nSaved benchmark report to: {output_path}")
    print(
        f"Latency: Median(p50)={report['runtime_latency_ms']['median_p50_ms']}ms, "
        f"p95={report['runtime_latency_ms']['p95_ms']}ms, Max={report['runtime_latency_ms']['max_ms']}ms "
        f"(SLA < 1500ms: {report['runtime_latency_ms']['meets_sla_under_1_5s']})"
    )
    print(
        f"Accuracy: Mean Dice={report['accuracy_metrics']['mean_dice']}, "
        f"Mean IoU={report['accuracy_metrics']['mean_iou']}, "
        f"Mean Sens={report['accuracy_metrics']['mean_sensitivity']}, "
        f"Mean Spec={report['accuracy_metrics']['mean_specificity']}"
    )
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description="HRF Vessel Segmentation Benchmark")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_JSON)
    parser.add_argument("--max-samples", type=int, default=0)
    args = parser.parse_args()

    run_benchmark(output_path=args.output, max_samples=args.max_samples)


if __name__ == "__main__":
    main()
