"""
Unit test validating that OpenAPI schemas and Python models parse properly (SCRUM-61).
"""
import sys
import os
import json

from app.schemas import (
    AnalysisRequest,
    ImageInfo,
    VesselMetrics,
    RiskAssessment,
    SegmentationResult,
    AnalysisResponse,
    ErrorResponse,
    EyeSide,
    AnalysisMode,
    RiskLevel,
    AnalysisStatus,
)


def test_analysis_request_and_response():
    req = AnalysisRequest(
        request_id="req_test_001",
        patient_id="PAT-12345",
        eye_side=EyeSide.RIGHT,
        mode=AnalysisMode.RETINA_VESSELS,
        image_base64="fake_base64_data",
        include_mask=True,
        include_overlay=True,
    )
    assert req.request_id == "req_test_001"
    assert req.eye_side == EyeSide.RIGHT

    img_info = ImageInfo(
        width=512,
        height=512,
        channels=3,
        eye_side=req.eye_side,
    )

    metrics = VesselMetrics(
        vessel_density=0.155,
        tortuosity_index=1.12,
        av_ratio=0.68,
        fractal_dimension=1.45,
        branching_points=52,
    )

    risk = RiskAssessment(
        risk_score=0.28,
        risk_level=RiskLevel.LOW,
        confidence_score=0.91,
        indicators=["Mật độ mạch máu bình thường", "Chỉ số xoắn mạch ổn định"],
    )

    seg = SegmentationResult(
        mask_format="png_base64",
        mask_base64="mask_base64_string",
        overlay_base64="overlay_base64_string",
    )

    resp = AnalysisResponse(
        request_id=req.request_id,
        patient_id=req.patient_id,
        timestamp="2026-10-05T18:00:00Z",
        status=AnalysisStatus.SUCCESS,
        processing_time_ms=135.2,
        image_info=img_info,
        metrics=metrics,
        risk_assessment=risk,
        segmentation=seg,
    )

    assert resp.request_id == "req_test_001"
    assert resp.metrics.vessel_density == 0.155
    assert resp.risk_assessment.risk_level == RiskLevel.LOW
    assert resp.threshold_version == "v0.1"
    assert resp.config_version == "v0.1"
    assert resp.is_mock is True


def test_json_contract_file():
    # Find contracts relative to repo root
    current_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(current_dir)))
    json_path = os.path.join(repo_root, "contracts", "ai_service_openapi.json")
    assert os.path.exists(json_path), f"Missing {json_path}"
    with open(json_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    assert data["openapi"] == "3.0.3"
    assert "/health" in data["paths"]
    assert "/api/v1/analyze" in data["paths"]
