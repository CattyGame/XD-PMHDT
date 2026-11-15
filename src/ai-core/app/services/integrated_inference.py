"""Opt-in application pipeline; research measurements never become clinical risk."""
import os
import hashlib
import re
import time
from pathlib import Path
from dataclasses import replace

from ..schemas.analysis import AnalysisResponse
from .frangi_service import process_frangi_analysis, decode_and_validate_fundus_image


class IntegratedModelUnavailable(RuntimeError):
    pass


def configured_artifacts():
    values = tuple(os.getenv(key, "").strip() for key in (
        "AURA_AVR_MODEL_PATH", "AURA_AVR_MODEL_SHA256", "AURA_DR_CNN_CHECKPOINT", "AURA_DR_CNN_SHA256"))
    avr_path, avr_sha, dr_path, dr_sha = values
    if (not avr_path or not dr_path or not re.fullmatch(r"[a-f0-9]{64}", avr_sha)
            or not re.fullmatch(r"[a-f0-9]{64}", dr_sha)
            or not Path(avr_path).is_file() or not Path(dr_path).is_file()):
        raise IntegratedModelUnavailable("Both verified AVR and DR artifacts must be configured.")
    try:
        for path, expected in ((avr_path, avr_sha), (dr_path, dr_sha)):
            with open(path, "rb") as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != expected:
                    raise IntegratedModelUnavailable("Integrated research artifact hash mismatch.")
    except OSError as error:
        raise IntegratedModelUnavailable("Integrated research artifacts cannot be read.") from error
    return values


def process_integrated_analysis(request, config):
    started = time.perf_counter()
    avr_path, avr_sha, dr_path, dr_sha = configured_artifacts()
    # Finish Frangi first so its large temporary arrays do not coexist with neural inference.
    research_config = replace(config, risk_available=False,
        config_version=config.config_version + "-integrated-research-v1")
    response = process_frangi_analysis(request, config=research_config)
    rgb, _, _ = decode_and_validate_fundus_image(request.image_base64, config)
    try:
        from .avr_model import load_avr_model
        from .avr_service import calculate_automatic_avr
        from .dr_cnn_service import screen_dr_cnn

        avr = calculate_automatic_avr(rgb, load_avr_model(avr_path, avr_sha),
                                      generate_visualization=True, include_masks=False)
        dr = screen_dr_cnn(rgb, dr_path, dr_sha)
        if avr["model_sha256"] != avr_sha or dr["checkpoint_sha256"] != dr_sha:
            raise ValueError("Research result does not match the configured artifact.")
        payload = response.model_dump()
        payload["metrics"]["av_ratio"] = avr["avr"]
        payload["research"] = {
            "avr": {key: avr.get(key) for key in ("measurement_available", "avr", "crae_px", "crve_px",
                                                    "unavailable_reason", "model_version", "model_sha256")},
            "dr": {"probability_dr": dr["probability_dr"], "model_version": dr["model_version"],
                   "checkpoint_sha256": dr["checkpoint_sha256"],
                   "gradcam_method": dr["explainability"]["method"],
                   "gradcam_geometry": dr["explainability"]["geometry"]},
        }
        payload["segmentation"]["av_overlay_base64"] = avr.get("overlay_base64_png")
        payload["segmentation"]["gradcam_base64"] = dr["explainability"]["overlay_base64_png"]
        if not avr["measurement_available"]:
            payload["warnings"].append({"code": "AVR_UNAVAILABLE",
                "message": "Chưa đủ điều kiện tính AVR: " + avr["unavailable_reason"]})
        payload["warnings"].append({"code": "RESEARCH_RESULTS", "message":
            "AVR và DR là kết quả nghiên cứu. Grad-CAM dùng crop 224×224, không phải mask tổn thương trên toàn ảnh."})
        payload["status"] = "WARNING"
        payload["limitations"] += " Native-pixel AVR V8 and binary HRF DR/Grad-CAM research; no clinical validation or ICDR grading."
        payload["processing_time_ms"] = round((time.perf_counter() - started) * 1000, 2)
        return AnalysisResponse.model_validate(payload)
    except (ImportError, OSError, ValueError, RuntimeError, KeyError, TypeError, IndexError) as error:
        raise IntegratedModelUnavailable("Verified integrated research inference is unavailable.") from error
