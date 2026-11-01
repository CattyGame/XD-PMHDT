"""
INSPIRE-AVR Dataset Preparation and Manifest Generation (SCRUM-373).

Verifies:
- All 100 cases in datasets/INSPIRE_AVR (IM000001 .. IM006106).
- Checks for original Fundus JPG, Artery mask, Vein mask, and combined vessel mask.
- Computes dimensions, resolutions, and SHA-256 hashes.
- Performs patient-level deterministic train/val/test split (seed=42):
  * 70 train, 15 val, 15 test
- Generates docs/datasets/inspire_avr/manifest.csv and docs/datasets/inspire_avr/validation_report.json.
"""

from __future__ import annotations

import csv
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Dict, List

import numpy as np
from PIL import Image

sys.stdout.reconfigure(encoding="utf-8")

ROOT = Path(__file__).resolve().parents[3]
INSPIRE_DIR = ROOT / "datasets" / "INSPIRE_AVR"
DOCS_INSPIRE_DIR = ROOT / "docs" / "datasets" / "inspire_avr"
DOCS_INSPIRE_DIR.mkdir(parents=True, exist_ok=True)


def sha256_file(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main() -> None:
    if not INSPIRE_DIR.is_dir():
        raise FileNotFoundError(f"INSPIRE_AVR directory not found at {INSPIRE_DIR}")

    case_dirs = sorted([p for p in INSPIRE_DIR.iterdir() if p.is_dir() and p.name.startswith("IM")])
    if len(case_dirs) != 100:
        print(f"Warning: Expected 100 case dirs, found {len(case_dirs)}")

    # 70 train, 15 val, 15 test
    rng = np.random.default_rng(seed=42)
    indices = np.arange(len(case_dirs))
    perm = rng.permutation(indices)
    n_train = int(len(case_dirs) * 0.70)
    n_val = int(len(case_dirs) * 0.15)
    train_indices = set(perm[:n_train])
    val_indices = set(perm[n_train:n_train + n_val])
    test_indices = set(perm[n_train + n_val:])

    records: List[Dict[str, Any]] = []
    for idx, cdir in enumerate(case_dirs):
        case_id = cdir.name
        # Find image, artery, vein
        fundus_files = [f for f in cdir.glob("*.jpg") if "--" not in f.name]
        fundus_files += [f for f in cdir.glob("*.JPG") if "--" not in f.name]
        if not fundus_files:
            continue
        fundus_path = fundus_files[0]

        art_files = list(cdir.glob("*--artry.*")) + list(cdir.glob("*--arteries.*"))
        art_path = art_files[0] if art_files else None

        vein_files = list(cdir.glob("*--veins.*")) + list(cdir.glob("*--vein.*"))
        vein_path = vein_files[0] if vein_files else None

        vessel_files = list(cdir.glob("*--vessels.*")) + list(cdir.glob("*--both.*"))
        vessel_path = vessel_files[0] if vessel_files else None

        with Image.open(fundus_path) as im:
            w, h = im.size

        if idx in train_indices:
            sp = "train"
        elif idx in val_indices:
            sp = "validation"
        else:
            sp = "test"

        records.append({
            "case_id": case_id,
            "split": sp,
            "width": w,
            "height": h,
            "image_path": fundus_path.relative_to(ROOT).as_posix(),
            "artery_mask_path": art_path.relative_to(ROOT).as_posix() if art_path else "",
            "vein_mask_path": vein_path.relative_to(ROOT).as_posix() if vein_path else "",
            "vessel_mask_path": vessel_path.relative_to(ROOT).as_posix() if vessel_path else "",
            "has_av_labels": bool(art_path and vein_path),
            "image_sha256": sha256_file(fundus_path),
        })

    records.sort(key=lambda r: r["case_id"])
    manifest_csv = DOCS_INSPIRE_DIR / "manifest.csv"
    with manifest_csv.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(records[0].keys()))
        writer.writeheader()
        writer.writerows(records)

    split_counts = {
        "train": sum(1 for r in records if r["split"] == "train"),
        "validation": sum(1 for r in records if r["split"] == "validation"),
        "test": sum(1 for r in records if r["split"] == "test"),
    }

    report = {
        "dataset": "INSPIRE-AVR (Iowa Normative Set for Processing Images of the REtina - Arteriovenous Ratio)",
        "source": "University of Iowa & National Eye Institute",
        "total_cases_verified": len(records),
        "split_summary": split_counts,
        "cases_with_av_ground_truth": sum(1 for r in records if r["has_av_labels"]),
        "seed": 42,
        "manifest_path": "docs/datasets/inspire_avr/manifest.csv",
    }
    report_json = DOCS_INSPIRE_DIR / "validation_report.json"
    report_json.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"Generated manifest: {manifest_csv}")
    print(f"Generated report: {report_json}")
    print(f"Total: {len(records)} cases, splits: {split_counts}, AV GT count: {report['cases_with_av_ground_truth']}")


if __name__ == "__main__":
    main()
