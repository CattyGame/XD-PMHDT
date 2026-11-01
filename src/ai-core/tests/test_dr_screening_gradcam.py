"""
AURA AI Core - Unit Tests for DR Screening & Grad-CAM Service.
Sprint 5 - Task W5-M4-02 (SCRUM-372).
"""

from __future__ import annotations

import base64
from pathlib import Path
import numpy as np
import pytest
from PIL import Image

from app.services.dr_screening_service import (
    DISCLAIMER_TEXT,
    extract_features_from_rgb,
    generate_gradcam_heatmap,
    render_gradcam_overlay_b64,
    screen_diabetic_retinopathy,
)


@pytest.fixture
def synthetic_fundus_with_lesions() -> np.ndarray:
    """Generate synthetic fundus with small lesions."""
    h, w = 500, 750
    yy, xx = np.ogrid[:h, :w]
    cy, cx = h // 2, w // 2
    fov_mask = (np.hypot(yy - cy, xx - cx) <= min(h, w) * 0.45)

    rgb = np.zeros((h, w, 3), dtype=np.uint8)
    rgb[fov_mask, 0] = 185
    rgb[fov_mask, 1] = 65
    rgb[fov_mask, 2] = 28

    # Simulate exudate (bright spot)
    rgb[cy - 20 : cy - 10, cx + 50 : cx + 60, :] = [240, 230, 180]
    # Simulate microaneurysm (dark focal spot on green, high red)
    rgb[cy + 40 : cy + 45, cx - 60 : cx - 55, :] = [180, 10, 10]

    return rgb


def test_extract_features_from_rgb(synthetic_fundus_with_lesions: np.ndarray) -> None:
    feats = extract_features_from_rgb(synthetic_fundus_with_lesions)
    assert isinstance(feats, np.ndarray)
    assert len(feats) == 13
    assert not np.isnan(feats).any()
    assert feats[0] > 0.0  # mean_r


def test_generate_gradcam_heatmap(synthetic_fundus_with_lesions: np.ndarray) -> None:
    heatmap, hotspots = generate_gradcam_heatmap(synthetic_fundus_with_lesions)
    assert heatmap.shape == synthetic_fundus_with_lesions.shape[:2]
    assert heatmap.min() >= 0.0
    assert heatmap.max() <= 1.0
    assert isinstance(hotspots, list)


def test_render_gradcam_overlay_b64(synthetic_fundus_with_lesions: np.ndarray) -> None:
    heatmap, _ = generate_gradcam_heatmap(synthetic_fundus_with_lesions)
    b64_str = render_gradcam_overlay_b64(synthetic_fundus_with_lesions, heatmap)
    assert isinstance(b64_str, str)
    assert len(b64_str) > 100
    # Decodable PNG
    data = base64.b64decode(b64_str)
    assert data[:8] == b"\x89PNG\r\n\x1a\n"


def test_screen_diabetic_retinopathy_pipeline(synthetic_fundus_with_lesions: np.ndarray) -> None:
    res = screen_diabetic_retinopathy(synthetic_fundus_with_lesions, generate_overlay=True)
    assert res["status"] == "SUCCESS"
    assert res["screening_result"] in ["REFERABLE_DR", "LOW_RISK"]
    assert 0.0 <= res["probability_dr"] <= 1.0
    assert 0.0 <= res["confidence"] <= 1.0
    # Check Master Audit disclaimer compliance
    assert "NOT an ICDR 5-stage clinical diagnosis" in res["disclaimer"]
    assert res["explainability"]["has_overlay_image"] is True
    assert res["explainability"]["overlay_base64_png"] is not None


def test_screen_real_hrf_image_if_present() -> None:
    sample_path = Path("datasets/HRF/images/01_dr.JPG")
    if not sample_path.exists():
        pytest.skip("HRF dataset not extracted locally")

    rgb = np.asarray(Image.open(sample_path))
    res = screen_diabetic_retinopathy(rgb, generate_overlay=True)
    assert res["status"] == "SUCCESS"
    assert res["screening_result"] == "REFERABLE_DR"
    assert res["probability_dr"] > 0.50
    assert res["explainability"]["overlay_base64_png"] is not None
