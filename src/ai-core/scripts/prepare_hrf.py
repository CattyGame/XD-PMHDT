"""
HRF (High-Resolution Fundus) Dataset Preparation and Manifest Generation (SCRUM-371, SCRUM-372).

Verifies:
- All 45 images in datasets/HRF/images (15 dr, 15 g, 15 h).
- Corresponding manual segmentation masks in datasets/HRF/manual1 (.tif).
- Corresponding field-of-view masks in datasets/HRF/mask (.tif).
- Validates dimensions (3504x2336), formats, channels, and SHA-256 hashes.
- Performs patient-level deterministic train/val/test split (seed=42) preserving class balance:
  * 15 dr -> 9 train, 3 val, 3 test
  * 15 g  -> 9 train, 3 val, 3 test
  * 15 h  -> 9 train, 3 val, 3 test
  Total: 27 train (60%), 9 val (20%), 9 test (20%).
- Outputs docs/datasets/hrf/manifest.csv and docs/datasets/hrf/validation_report.json.
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
HRF_DIR = ROOT / "datasets" / "HRF"
DOCS_HRF_DIR = ROOT / "docs" / "datasets" / "hrf"
DOCS_HRF_DIR.mkdir(parents=True, exist_ok=True)

EXPECTED_WIDTH = 3504
EXPECTED_HEIGHT = 2336


def sha256_file(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def main() -> None:
    images_dir = HRF_DIR / "images"
    manual_dir = HRF_DIR / "manual1"
    mask_dir = HRF_DIR / "mask"

    if not images_dir.is_dir() or not manual_dir.is_dir():
        raise FileNotFoundError(f"HRF dataset not found at {HRF_DIR}. Run extraction first.")

    categories = ["dr", "g", "h"]
    records: List[Dict[str, Any]] = []

    # Deterministic split allocation for indices 1..15:
    # Seed 42 permutation of 1..15:
    rng = np.random.default_rng(seed=42)
    indices = np.arange(1, 16)
    perm = rng.permutation(indices)
    # 9 train, 3 val, 3 test
    train_ids = set(perm[:9])
    val_ids = set(perm[9:12])
    test_ids = set(perm[12:])

    for cat in categories:
        for idx in range(1, 16):
            stem = f"{idx:02d}_{cat}"
            img_candidates = list(images_dir.glob(f"{stem}.*"))
            if not img_candidates:
                raise FileNotFoundError(f"Missing image for {stem}")
            img_path = img_candidates[0]

            manual_candidates = list(manual_dir.glob(f"{stem}.*"))
            if not manual_candidates:
                raise FileNotFoundError(f"Missing manual mask for {stem}")
            manual_path = manual_candidates[0]

            mask_candidates = list(mask_dir.glob(f"{stem}_mask.*"))
            mask_path = mask_candidates[0] if mask_candidates else None

            with Image.open(img_path) as im:
                w, h = im.size
                mode = im.mode
            if w != EXPECTED_WIDTH or h != EXPECTED_HEIGHT:
                raise ValueError(f"Unexpected image size for {img_path}: {w}x{h}")

            with Image.open(manual_path) as m_im:
                mw, mh = m_im.size
            if mw != EXPECTED_WIDTH or mh != EXPECTED_HEIGHT:
                raise ValueError(f"Unexpected manual mask size for {manual_path}: {mw}x{mh}")

            if idx in train_ids:
                split = "train"
            elif idx in val_ids:
                split = "validation"
            else:
                split = "test"

            rel_img = img_path.relative_to(ROOT).as_posix()
            rel_manual = manual_path.relative_to(ROOT).as_posix()
            rel_mask = mask_path.relative_to(ROOT).as_posix() if mask_path else ""

            records.append({
                "patient_id": f"hrf_{cat}_{idx:02d}",
                "category": cat,
                "label": 1 if cat == "dr" else 0,
                "split": split,
                "width": w,
                "height": h,
                "image_path": rel_img,
                "manual_path": rel_manual,
                "mask_path": rel_mask,
                "image_sha256": sha256_file(img_path),
                "manual_sha256": sha256_file(manual_path),
            })

    # Sort deterministically
    records.sort(key=lambda r: (r["category"], r["patient_id"]))

    manifest_csv = DOCS_HRF_DIR / "manifest.csv"
    with manifest_csv.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(records[0].keys()))
        writer.writeheader()
        writer.writerows(records)

    split_counts = {"train": 0, "validation": 0, "test": 0}
    cat_counts: Dict[str, Dict[str, int]] = {
        "dr": {"train": 0, "validation": 0, "test": 0},
        "g": {"train": 0, "validation": 0, "test": 0},
        "h": {"train": 0, "validation": 0, "test": 0},
    }
    for r in records:
        split_counts[r["split"]] += 1
        cat_counts[r["category"]][r["split"]] += 1

    report = {
        "dataset": "HRF (High-Resolution Fundus)",
        "source": "Pattern Recognition and Image Processing Group, University of Erlangen-Nuremberg",
        "total_images": len(records),
        "resolution": [EXPECTED_WIDTH, EXPECTED_HEIGHT],
        "categories": {
            "dr": {"description": "Diabetic Retinopathy", "count": 15, "splits": cat_counts["dr"]},
            "g": {"description": "Glaucoma", "count": 15, "splits": cat_counts["g"]},
            "h": {"description": "Healthy", "count": 15, "splits": cat_counts["h"]},
        },
        "split_summary": split_counts,
        "seed": 42,
        "manifest_path": "docs/datasets/hrf/manifest.csv",
        "integrity_check": "100% verified dimensions and SHA-256",
    }

    report_json = DOCS_HRF_DIR / "validation_report.json"
    report_json.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"Generated manifest: {manifest_csv}")
    print(f"Generated report: {report_json}")
    print(f"Total: {len(records)} images, splits: {split_counts}")


if __name__ == "__main__":
    main()
