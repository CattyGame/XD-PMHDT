"""Contract/orchestration checks; synthetic model outputs are test fixtures only."""
import base64
import hashlib
import io
import json
import uuid
from pathlib import Path

import numpy as np
import pytest
import jsonschema
from PIL import Image

from app.schemas.analysis import AnalysisRequest, AnalysisResponse
from app.services.frangi_service import FrangiRuntimeConfig, LowQualityImageError


def baseline():
    return AnalysisResponse(request_id=str(uuid.uuid4()), timestamp="2026-11-14T01:00:00Z",
        processing_time_ms=10, image_info={"width":64,"height":64},
        metrics={"vessel_density":0.2}, risk_available=False,
        warnings=[{"code":"RISK_UNAVAILABLE","message":"No clinical risk."}],
        segmentation={"mask_base64":"fixture","overlay_base64":"fixture"})


@pytest.fixture
def pipeline(monkeypatch, tmp_path):
    from app.services import integrated_inference as integration
    from app.services import avr_model, avr_service, dr_cnn_service
    for key, value in {"AURA_AVR_MODEL_PATH":"avr.json", "AURA_DR_CNN_CHECKPOINT":"dr.pt"}.items():
        path = tmp_path/value
        path.write_bytes(b"synthetic-unit-test-artifact")
        monkeypatch.setenv(key,str(path))
        monkeypatch.setenv("AURA_AVR_MODEL_SHA256" if key.endswith("PATH") else "AURA_DR_CNN_SHA256",hashlib.sha256(path.read_bytes()).hexdigest())
    monkeypatch.setattr(integration,"process_frangi_analysis",lambda *a,**k:baseline())
    monkeypatch.setattr(avr_model,"load_avr_model",lambda path,digest:{"sha256":digest})
    digest=hashlib.sha256(b"synthetic-unit-test-artifact").hexdigest()
    avr={"measurement_available":True,"avr":0.67,"crae_px":13.4,"crve_px":20.0,
         "model_version":"avr-v8-test","model_sha256":digest,"overlay_base64_png":"native-av-overlay"}
    monkeypatch.setattr(avr_service,"calculate_automatic_avr",lambda *a,**k:avr)
    monkeypatch.setattr(dr_cnn_service,"screen_dr_cnn",lambda *a,**k:{"probability_dr":0.61,
        "model_version":"resnet34-test","checkpoint_sha256":digest,
        "explainability":{"method":"GRAD_CAM","geometry":"IMAGENET_CENTER_CROP_224","overlay_base64_png":"crop-overlay"}})
    output=io.BytesIO();Image.fromarray(np.full((64,64,3),100,np.uint8)).save(output,format="PNG")
    request=AnalysisRequest(request_id=str(uuid.uuid4()),image_base64=base64.b64encode(output.getvalue()).decode())
    return integration, request, avr


def test_baseline_wire_does_not_grow_optional_research_fields():
    value=baseline().model_dump(mode="json")
    assert "research" not in value
    assert set(value["segmentation"])=={"mask_format","mask_base64","overlay_base64"}


def test_integrated_result_has_measured_avr_independent_dr_and_crop_geometry(pipeline):
    integration,request,_=pipeline
    result=integration.process_integrated_analysis(request,FrangiRuntimeConfig())
    assert result.metrics.av_ratio==0.67
    assert result.research.avr.crae_px==13.4
    assert result.research.dr.probability_dr==0.61
    assert result.research.dr.gradcam_geometry=="IMAGENET_CENTER_CROP_224"
    assert result.segmentation.gradcam_base64=="crop-overlay"
    assert result.segmentation.av_overlay_base64=="native-av-overlay"
    assert result.risk_available is False and result.risk_assessment is None
    assert result.is_mock is False


def test_missing_avr_is_null_with_reason_and_does_not_block_dr(pipeline):
    integration,request,avr=pipeline
    avr.update(measurement_available=False,avr=None,crae_px=None,crve_px=None,unavailable_reason="INSUFFICIENT_SIX_VESSEL_SETS")
    result=integration.process_integrated_analysis(request,FrangiRuntimeConfig())
    assert result.metrics.av_ratio is None
    assert result.research.avr.measurement_available is False
    assert result.research.avr.unavailable_reason=="INSUFFICIENT_SIX_VESSEL_SETS"
    assert any(w.code=="AVR_UNAVAILABLE" and "INSUFFICIENT_SIX_VESSEL_SETS" in w.message for w in result.warnings)
    assert result.research.dr.probability_dr==0.61


@pytest.mark.parametrize("available", [True, False])
@pytest.mark.parametrize("boundary", ["pydantic", "json_schema"])
def test_research_cannot_claim_clinical_risk(pipeline, available, boundary):
    integration, request, avr = pipeline
    if not available:
        avr.update(measurement_available=False, avr=None, crae_px=None, crve_px=None,
                   unavailable_reason="INSUFFICIENT_SIX_VESSEL_SETS")
    payload = integration.process_integrated_analysis(request, FrangiRuntimeConfig()).model_dump(mode="json")
    payload.update(risk_available=True, risk_assessment={"risk_level":"LOW", "risk_score":0.1,
        "confidence_score":0.9, "indicators":["unit-test"], "disclaimer":"Synthetic test risk"})
    payload["metrics"].update(tortuosity_index=1.1, branching_points=2)
    if boundary == "pydantic":
        with pytest.raises(ValueError, match="research.*risk"):
            AnalysisResponse.model_validate(payload)
    else:
        schema = json.loads((Path(__file__).resolve().parents[3] / "contracts/ai-response-1.3.schema.json").read_text(encoding="utf-8"))
        with pytest.raises(jsonschema.ValidationError):
            jsonschema.Draft202012Validator(schema).validate(payload)


def test_integrated_profile_does_not_inherit_heuristic_risk(pipeline,monkeypatch):
    integration,request,_=pipeline
    observed=[]
    def frangi(*args,config):
        observed.append(config)
        return baseline()
    monkeypatch.setattr(integration,"process_frangi_analysis",frangi)
    original=FrangiRuntimeConfig(risk_available=True)
    integration.process_integrated_analysis(request,original)
    assert observed[0].risk_available is False
    assert observed[0].config_version.endswith("-integrated-research-v1")
    assert original.risk_available is True


def test_incomplete_config_is_technical_failure_not_a_fabricated_result(pipeline,monkeypatch):
    integration,request,_=pipeline
    monkeypatch.delenv("AURA_DR_CNN_SHA256")
    with pytest.raises(integration.IntegratedModelUnavailable):
        integration.process_integrated_analysis(request,FrangiRuntimeConfig())


def test_readiness_checks_integrated_artifact_hashes(pipeline,monkeypatch):
    from app import main
    from fastapi import HTTPException
    monkeypatch.setenv("AURA_AI_ENGINE","frangi")
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED","true")
    monkeypatch.setattr(main,"load_frangi_config",lambda:FrangiRuntimeConfig())
    monkeypatch.setattr(main,"load_verifier_from_env",lambda:object())
    assert main.ready().status=="ready"
    assert main.ready().config_version.endswith("-integrated-research-v1")
    assert main.root()["capabilities"]["avr_available"] is True
    monkeypatch.setenv("AURA_DR_CNN_SHA256","0"*64)
    with pytest.raises(HTTPException) as error: main.ready()
    assert error.value.status_code==503


def test_quality_rejection_stops_before_optional_models(pipeline,monkeypatch):
    integration,request,_=pipeline
    def reject(*a,**k):raise LowQualityImageError("quality rejected",None)
    monkeypatch.setattr(integration,"process_frangi_analysis",reject)
    with pytest.raises(LowQualityImageError):integration.process_integrated_analysis(request,FrangiRuntimeConfig())


def test_primary_route_selects_integrated_processor_only_when_explicit(monkeypatch):
    from app import main
    monkeypatch.setenv("AURA_AI_ENGINE","frangi")
    monkeypatch.setenv("AURA_INTEGRATED_RESEARCH_ENABLED","true")
    monkeypatch.setattr(main,"process_integrated_analysis",lambda *a,**k:"INTEGRATED",raising=False)
    monkeypatch.setattr(main,"process_frangi_analysis",lambda *a,**k:"BASELINE")
    request=AnalysisRequest(request_id=str(uuid.uuid4()),image_base64="AQ==")
    assert main.analyze_retina(request,FrangiRuntimeConfig())=="INTEGRATED"
    monkeypatch.delenv("AURA_INTEGRATED_RESEARCH_ENABLED")
    assert main.analyze_retina(request,FrangiRuntimeConfig())=="BASELINE"
