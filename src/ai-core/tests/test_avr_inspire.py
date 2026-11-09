"""
AURA AI Core - Unit Tests for Artery/Vein Segmentation & AVR Service.
Sprint 5 - Task W5-M4-03 (SCRUM-373).
"""

from __future__ import annotations

import base64
from pathlib import Path
import numpy as np
import pytest
from PIL import Image

from app.services.avr_service import (
    AVRConfig,
    calculate_avr,
    compute_knudtson_crae,
    compute_knudtson_crve,
    detect_optic_disc_center,
    segment_arteries_veins,
)


@pytest.fixture
def synthetic_retina_with_od() -> np.ndarray:
    """Generate synthetic fundus (800x1200) with a circular bright optic disc and crossing vessels."""
    h, w = 800, 1200
    yy, xx = np.ogrid[:h, :w]
    cy, cx = h // 2, w // 2

    rgb = np.zeros((h, w, 3), dtype=np.uint8)
    fov = (np.hypot(yy - cy, xx - cx) <= min(h, w) * 0.46)
    rgb[fov, 0] = 175
    rgb[fov, 1] = 60
    rgb[fov, 2] = 20

    # Optic disc (bright oval at x=400, y=400)
    od_y, od_x = 400, 400
    od_mask = (np.hypot(yy - od_y, xx - od_x) <= 50)
    rgb[od_mask, 0] = 250
    rgb[od_mask, 1] = 220
    rgb[od_mask, 2] = 120

    # Arterioles (crossing Zone B: dist 100 to 150 from OD)
    # Horizontal arteriole
    rgb[od_y - 2 : od_y + 3, od_x + 90 : od_x + 160, 0] = 220
    rgb[od_y - 2 : od_y + 3, od_x + 90 : od_x + 160, 1] = 40
    rgb[od_y - 2 : od_y + 3, od_x + 90 : od_x + 160, 2] = 20

    # Venules (crossing Zone B)
    # Vertical venule
    rgb[od_y + 90 : od_y + 160, od_x - 3 : od_x + 4, 0] = 80
    rgb[od_y + 90 : od_y + 160, od_x - 3 : od_x + 4, 1] = 20
    rgb[od_y + 90 : od_y + 160, od_x - 3 : od_x + 4, 2] = 20

    return rgb


def test_knudtson_golden_vector_equal_calibers() -> None:
    """
    Kiểm chứng Golden Vector trên cặp khẩu kính đồng dạng [100.0, 100.0].
    Theo sinh lý võng mạc và công thức Knudtson et al. (2003):
    Tĩnh mạch bắt buộc phải lớn hơn Động mạch (CRVE > CRAE <=> AVR < 1.0).
    """
    calibers = [100.0, 100.0]
    crae = compute_knudtson_crae(calibers)
    crve = compute_knudtson_crve(calibers)

    # Đối chiếu giá trị số học chuẩn:
    assert crae == pytest.approx(124.451, abs=0.01)
    assert crve == pytest.approx(134.350, abs=0.01)
    assert crae < crve, "Lỗi nghiêm trọng: CRAE không được phép lớn hơn CRVE khi khẩu kính bằng nhau!"

    avr = crae / crve
    assert avr == pytest.approx(0.9263, abs=0.001)
    assert avr < 1.0


def test_knudtson_crae_and_crve_formulas() -> None:
    # Test pairing calculation
    artery_calibers = [15.0, 14.0, 12.0, 11.0, 10.0, 8.0]
    vein_calibers = [20.0, 18.0, 17.0, 16.0, 14.0, 12.0]

    crae = compute_knudtson_crae(artery_calibers)
    crve = compute_knudtson_crve(vein_calibers)

    assert crae > 0.0
    assert crve > 0.0
    assert crve > crae  # Veins are wider than arteries
    avr = crae / crve
    assert 0.40 <= avr < 1.0

    # Edge case: empty list
    assert compute_knudtson_crae([]) == 0.0
    assert compute_knudtson_crve([]) == 0.0


def test_detect_optic_disc_center(synthetic_retina_with_od: np.ndarray) -> None:
    od_res, contrast = detect_optic_disc_center(synthetic_retina_with_od)
    assert od_res is not None
    cx, cy, r_od = od_res
    # Center should be close to (400, 400)
    assert abs(cx - 400) < 30
    assert abs(cy - 400) < 30
    assert r_od > 20
    assert contrast > 0.10


def test_degraded_image_returns_risk_unavailable() -> None:
    """Degraded image (pure black or uniform grey) must return RISK_UNAVAILABLE without crashing or fabricating numbers."""
    black_img = np.zeros((500, 500, 3), dtype=np.uint8)
    res = calculate_avr(black_img)

    assert res["status"] == "WARNING"
    assert res["risk_available"] is False
    assert res["avr"] is None
    assert res["crae_px"] is None
    assert res["crve_px"] is None
    assert res["warning_code"] == "RISK_UNAVAILABLE"
    assert res["risk_level"] == "RISK_UNAVAILABLE"


def test_calculate_avr_synthetic_pipeline(synthetic_retina_with_od: np.ndarray) -> None:
    res = calculate_avr(synthetic_retina_with_od, generate_visualization=True)
    assert res["status"] in ["SUCCESS", "WARNING"]
    if res["status"] == "SUCCESS":
        assert res["risk_available"] is True
        assert res["avr"] is not None
        assert res["crae_px"] is not None
        assert res["crve_px"] is not None
        assert res["risk_level"] in ["NORMAL", "BORDERLINE", "HYPERTENSION_RISK"]
        assert res["visualization"]["has_annotated_image"] is True
        vis_b64 = res["visualization"]["annotated_base64_png"]
        data = base64.b64decode(vis_b64)
        assert data[:8] == b"\x89PNG\r\n\x1a\n"


def test_inspire_avr_real_case_if_present() -> None:
    case_path = Path("datasets/INSPIRE_AVR/IM000004/IM000004.JPG")
    if not case_path.exists():
        pytest.skip("INSPIRE_AVR dataset not present locally")

    rgb = np.asarray(Image.open(case_path))
    # Verify automated end-to-end prediction without GT masks
    res_auto = calculate_avr(rgb, artery_gt_mask=None, vein_gt_mask=None, generate_visualization=True)
    assert res_auto["status"] == "SUCCESS"
    assert res_auto["risk_available"] is True
    assert res_auto["avr"] is not None
    assert res_auto["crae_px"] > 0
    assert res_auto["crve_px"] > 0
    assert res_auto["visualization"]["annotated_base64_png"] is not None
