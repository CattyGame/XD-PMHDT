"""
Tests for CHASE_DB1 28-image Frangi benchmark and quality gate (SCRUM-351 / Task 3).
"""
import io
import uuid
import base64
import numpy as np
import pytest
from PIL import Image

from app.services.frangi_service import (
    FrangiRuntimeConfig,
    LowQualityImageError,
    process_frangi_analysis,
)
from app.schemas.analysis import AnalysisRequest


def test_quality_gate_rejects_dark_image_without_assigning_low_risk():
    """Verify that a black or heavily degraded image is rejected with LowQualityImageError (SCRUM-351)."""
    buf = io.BytesIO()
    Image.new("RGB", (256, 256), (0, 0, 0)).save(buf, format="PNG")
    b64 = base64.b64encode(buf.getvalue()).decode("ascii")

    req = AnalysisRequest(
        request_id=str(uuid.uuid4()),
        image_base64=b64,
        include_mask=True,
        include_overlay=True,
    )
    with pytest.raises(LowQualityImageError) as exc_info:
        process_frangi_analysis(req, FrangiRuntimeConfig(risk_available=False))

    report = exc_info.value.report
    assert report.is_acceptable is False
    assert len(report.failed_checks) > 0
    assert any("REGION" in check or "LUMINANCE" in check.upper() for check in report.failed_checks)


def test_benchmark_chase_script_import_and_metrics_helper():
    """Verify metrics calculation logic produces valid Dice, IoU, Sens, Spec."""
    import sys
    from pathlib import Path
    script_dir = Path(__file__).resolve().parents[1] / "scripts"
    if str(script_dir) not in sys.path:
        sys.path.insert(0, str(script_dir))

    from benchmark_chase_frangi import compute_metrics

    # Perfect overlap
    gt = np.array([[1, 0], [1, 1]], dtype=bool)
    pred = np.array([[1, 0], [1, 1]], dtype=bool)
    m = compute_metrics(pred, gt)
    assert m["dice"] == 1.0
    assert m["iou"] == 1.0
    assert m["sensitivity"] == 1.0
    assert m["specificity"] == 1.0

    # Partial overlap
    pred2 = np.array([[1, 1], [0, 0]], dtype=bool)
    m2 = compute_metrics(pred2, gt)
    assert 0.0 < m2["dice"] < 1.0
    assert 0.0 < m2["iou"] < 1.0
