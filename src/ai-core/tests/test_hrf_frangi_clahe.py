"""
AURA AI Core - Unit Tests for HRF Frangi & CLAHE Pipeline.
Sprint 5 - Task W5-M4-01 (SCRUM-371).
"""

from __future__ import annotations

import os
from pathlib import Path
import numpy as np
import pytest
from PIL import Image

from app.services.image_preprocessor import (
    PreprocessorConfig,
    apply_clahe,
    estimate_fov_mask,
    process_hrf_frangi_vessel_segmentation,
)


@pytest.fixture
def synthetic_fundus() -> np.ndarray:
    """Generate a synthetic fundus image (1000x1500) with a circular FOV and synthetic vessel lines."""
    h, w = 1000, 1500
    yy, xx = np.ogrid[:h, :w]
    cy, cx = h // 2, w // 2
    r_fov = min(h, w) * 0.45
    fov_mask = (np.hypot(yy - cy, xx - cx) <= r_fov)

    # Background retina (warm orange-red)
    rgb = np.zeros((h, w, 3), dtype=np.uint8)
    rgb[fov_mask, 0] = 180
    rgb[fov_mask, 1] = 60
    rgb[fov_mask, 2] = 25

    # Add dark vessels on green channel
    # Horizontal vessel
    rgb[cy - 2 : cy + 3, cx - 200 : cx + 200, 1] = 20
    # Vertical vessel
    rgb[cy - 200 : cy + 200, cx - 2 : cx + 3, 1] = 20

    return rgb


def test_estimate_fov_mask(synthetic_fundus: np.ndarray) -> None:
    fov = estimate_fov_mask(synthetic_fundus, threshold=15)
    assert fov.shape == synthetic_fundus.shape[:2]
    assert fov.dtype == bool
    assert np.count_nonzero(fov) > 0
    # Outer corners must be False (non-FOV)
    assert not fov[0, 0]
    assert not fov[0, -1]
    assert not fov[-1, 0]
    assert not fov[-1, -1]
    # Center must be True
    h, w = synthetic_fundus.shape[:2]
    assert fov[h // 2, w // 2]


def test_apply_clahe_contrast_enhancement() -> None:
    # Flat gradient array
    arr = np.linspace(0.1, 0.4, 256 * 256, dtype=np.float32).reshape(256, 256)
    enhanced = apply_clahe(arr, clip_limit=0.02, tile_grid_size=(8, 8))
    assert enhanced.shape == arr.shape
    assert enhanced.dtype == np.float32
    assert enhanced.min() >= 0.0
    assert enhanced.max() <= 1.0
    # Enhanced array should have higher standard deviation (higher contrast)
    assert np.std(enhanced) >= np.std(arr)


def test_process_hrf_frangi_pipeline(synthetic_fundus: np.ndarray) -> None:
    config = PreprocessorConfig(max_processing_width=800)
    result = process_hrf_frangi_vessel_segmentation(synthetic_fundus, config=config)

    assert "mask" in result
    assert "fov" in result
    assert "metrics" in result

    mask = result["mask"]
    metrics = result["metrics"]

    # Resolution preserved to original dimensions
    assert mask.shape == synthetic_fundus.shape[:2]
    assert metrics["original_resolution"] == [1500, 1000]
    assert metrics["processed_resolution"] == [800, int(round(1000 * 800 / 1500))]
    assert metrics["total_latency_ms"] > 0
    assert metrics["total_latency_ms"] < 2500  # Well within bounds
    assert metrics["vessel_density"] > 0.0


def test_hrf_dataset_file_benchmark_if_present() -> None:
    """If HRF image exists on disk, run a test segmentation to verify SLA."""
    hrf_img_path = Path("datasets/HRF/images/01_dr.JPG")
    if not hrf_img_path.exists():
        pytest.skip("HRF dataset not extracted locally")

    rgb = np.asarray(Image.open(hrf_img_path))
    gt_path = Path("datasets/HRF/manual1/01_dr.tif")
    gt = np.asarray(Image.open(gt_path)) if gt_path.exists() else None

    cfg = PreprocessorConfig(max_processing_width=1200)
    res = process_hrf_frangi_vessel_segmentation(rgb, config=cfg, ground_truth_mask=gt)

    metrics = res["metrics"]
    assert metrics["original_resolution"] == [3504, 2336]
    assert metrics["total_latency_ms"] < 1500.0  # Pass SLA < 1.5s
    if gt is not None:
        assert metrics["dice"] > 0.15
        assert metrics["specificity"] > 0.95
