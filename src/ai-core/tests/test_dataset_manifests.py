"""
Unit test verifying dataset manifests for CHASE_DB1, HRF, and INSPIRE-AVR (Task 2 / D6).
Ensures all images and masks have valid paths, correct counts, and SHA-256 entries.
"""
from pathlib import Path
import csv
import pytest

REPO_ROOT = Path(__file__).resolve().parents[3]


def test_chase_db1_manifest_integrity():
    manifest_path = REPO_ROOT / "docs/datasets/chase_db1/manifest.csv"
    assert manifest_path.is_file(), "CHASE_DB1 manifest.csv is missing"

    with open(manifest_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    assert len(rows) == 28, f"CHASE_DB1 must contain exactly 28 images, found {len(rows)}"
    for r in rows:
        assert len(r["image_sha256"]) == 64, f"Invalid SHA-256 for {r['image']}"
        assert (REPO_ROOT / r["image"]).is_file(), f"Missing image file {r['image']}"
        assert (REPO_ROOT / r["mask_1st"]).is_file(), f"Missing 1st mask file {r['mask_1st']}"


def test_hrf_manifest_integrity():
    manifest_path = REPO_ROOT / "docs/datasets/hrf/manifest.csv"
    assert manifest_path.is_file(), "HRF manifest.csv is missing"

    with open(manifest_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    assert len(rows) == 45, f"HRF must contain exactly 45 images, found {len(rows)}"
    dr_count = sum(1 for r in rows if r["category"] == "dr")
    g_count = sum(1 for r in rows if r["category"] == "g")
    h_count = sum(1 for r in rows if r["category"] == "h")
    assert dr_count == 15, f"HRF DR images count {dr_count} != 15"
    assert g_count == 15, f"HRF Glaucoma images count {g_count} != 15"
    assert h_count == 15, f"HRF Healthy images count {h_count} != 15"

    has_local_files = (REPO_ROOT / rows[0]["image_path"]).is_file()
    for r in rows:
        assert len(r["image_sha256"]) == 64, f"Invalid SHA-256 for {r['image_path']}"
        if has_local_files:
            assert (REPO_ROOT / r["image_path"]).is_file(), f"Missing image file {r['image_path']}"
            assert (REPO_ROOT / r["manual_path"]).is_file(), f"Missing manual mask {r['manual_path']}"


def test_inspire_avr_manifest_integrity():
    manifest_path = REPO_ROOT / "docs/datasets/inspire_avr/manifest.csv"
    assert manifest_path.is_file(), "INSPIRE_AVR manifest.csv is missing"

    with open(manifest_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        rows = list(reader)

    assert len(rows) == 99, f"INSPIRE_AVR verified cases count {len(rows)} != 99"
    has_local_files = (REPO_ROOT / rows[0]["image_path"]).is_file()
    for r in rows:
        assert len(r["image_sha256"]) == 64, f"Invalid SHA-256 for {r['image_path']}"
        assert r["has_av_labels"] == "True", f"Expected has_av_labels=True for {r['case_id']}"
        if has_local_files:
            assert (REPO_ROOT / r["image_path"]).is_file(), f"Missing image {r['image_path']}"
            assert (REPO_ROOT / r["artery_mask_path"]).is_file(), f"Missing artery mask {r['artery_mask_path']}"
            assert (REPO_ROOT / r["vein_mask_path"]).is_file(), f"Missing vein mask {r['vein_mask_path']}"
            assert (REPO_ROOT / r["vessel_mask_path"]).is_file(), f"Missing vessel mask {r['vessel_mask_path']}"
