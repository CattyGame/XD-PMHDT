"""
Unit test for the mock inference service (Module M4).
"""
import sys
import os

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, PROJECT_ROOT)

from ai_service.app.schemas.common import EyeSide, AnalysisMode, RiskLevel, AnalysisStatus
from ai_service.app.schemas.analysis import AnalysisRequest
from ai_service.app.services.mock_service import process_mock_analysis

def test_mock_analysis_generation():
    req = AnalysisRequest(
        request_id="req_unit_test_001",
        patient_id="PAT-9999",
        eye_side=EyeSide.LEFT,
        mode=AnalysisMode.RETINA_VESSELS,
        image_base64="sample_test_base64",
        include_mask=True,
        include_overlay=True
    )

    resp = process_mock_analysis(req)

    assert resp.request_id == "req_unit_test_001"
    assert resp.status == AnalysisStatus.SUCCESS
    assert resp.processing_time_ms > 0
    assert resp.image_info.eye_side == EyeSide.LEFT

    # Metrics range checks
    assert 0.05 <= resp.metrics.vessel_density <= 0.35
    assert 1.0 <= resp.metrics.tortuosity_index <= 2.0
    assert 0.5 <= resp.metrics.av_ratio <= 1.0
    assert resp.metrics.branching_points > 0

    # Risk checks
    assert resp.risk_assessment.risk_level in [RiskLevel.LOW, RiskLevel.MODERATE, RiskLevel.HIGH]
    assert 0.0 <= resp.risk_assessment.risk_score <= 1.0
    assert len(resp.risk_assessment.disclaimer) > 10

    # Segmentation checks
    assert resp.segmentation is not None
    assert resp.segmentation.mask_base64 is not None
    assert resp.segmentation.overlay_base64 is not None

    print("✓ test_mock_analysis_generation passed successfully")

if __name__ == "__main__":
    test_mock_analysis_generation()
    print("\nAll mock service tests passed!")
