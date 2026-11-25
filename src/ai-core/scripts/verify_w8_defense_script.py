"""Sprint 8 W8-M4-03 (SCRUM-425): Verify 3-minute defense script & measured vs. unavailable cases.

Checks:
1. Live execution of measure_avr_prediction() on deterministic measured vs. unavailable scenes
   (Zone B 6 arteries + 6 veins revised Knudtson vs. insufficient vessels -> null + reason).
2. Speech transcript length (~3 minutes spoken) and alignment with source-code terminology.
3. Embedded AnalysisResponse (OpenAPI 1.3.0) JSON cases validate strictly against Pydantic schemas.
4. Negative controls: rejects clinical ICDR grading claims or substituting avr=0 / risk=LOW.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np
from PIL import Image, ImageDraw

REPO_ROOT = Path(__file__).resolve().parents[3]
AI_CORE_DIR = REPO_ROOT / "src" / "ai-core"
if str(AI_CORE_DIR) not in sys.path:
    sys.path.insert(0, str(AI_CORE_DIR))

from app.schemas.analysis import AnalysisResponse  # noqa: E402
from app.services.avr_service import measure_avr_prediction  # noqa: E402

DEFAULT_DOC_PATH = REPO_ROOT / "docs" / "ai" / "w8_m4_3min_defense_script_and_cases.md"

_SPEECH_RE = re.compile(
    r"<!--\s*SPEECH_TRANSCRIPT_START\s*-->(.*?)<!--\s*SPEECH_TRANSCRIPT_END\s*-->",
    re.DOTALL,
)

FORBIDDEN_SPEECH_CLAIMS = (
    "icdr 5-stage grading pass",
    "chẩn đoán icdr 5 mức",
    "thay thế bác sĩ",
    "đã được chứng nhận lâm sàng",
    "clinically certified",
    "scrum-373 đã đạt",
    "scrum-373 pass",
)

REQUIRED_TERMS = (
    "frangi-v0.2",
    "resnet34",
    "imagenet_center_crop_224",
    "grad_cam",
    "zone b",
    "knudtson",
    "measurement_available",
    "unavailable_reason",
    "avr_unavailable",
    "scrum-373",
    "open_not_passed",
)


def _extract_tagged_json(text: str, marker: str) -> Dict[str, Any]:
    pattern = re.compile(
        rf"<!--\s*{re.escape(marker)}\s*-->\s*```json\s*(.*?)\s*```",
        re.DOTALL,
    )
    match = pattern.search(text)
    if not match:
        raise ValueError(f"Missing tagged JSON block <!-- {marker} -->.")
    return json.loads(match.group(1))


def _build_radial_avr_scene(artery_count: int = 6, vein_count: int = 6) -> Dict[str, Any]:
    size = 512
    cx, cy, r_od = 256, 256, 40
    bg = np.full((size, size, 3), 185, dtype=np.uint8)
    label_img = Image.new("L", (size, size), 0)
    mask_img = Image.new("L", (size, size), 0)
    draw_lbl = ImageDraw.Draw(label_img)
    draw_msk = ImageDraw.Draw(mask_img)

    # Place disconnected unbranched vessels across Zone B (2R=80 .. 3R=120 px)
    total_slots = 12
    artery_angles = [i * (360.0 / total_slots) for i in range(0, total_slots, 2)][:artery_count]
    vein_angles = [i * (360.0 / total_slots) for i in range(1, total_slots, 2)][:vein_count]

    for deg in artery_angles:
        rad = math.radians(deg)
        p0 = (cx + 68.0 * math.cos(rad), cy + 68.0 * math.sin(rad))
        p1 = (cx + 136.0 * math.cos(rad), cy + 136.0 * math.sin(rad))
        draw_lbl.line([p0, p1], fill=1, width=6)
        draw_msk.line([p0, p1], fill=255, width=6)

    for deg in vein_angles:
        rad = math.radians(deg)
        p0 = (cx + 68.0 * math.cos(rad), cy + 68.0 * math.sin(rad))
        p1 = (cx + 136.0 * math.cos(rad), cy + 136.0 * math.sin(rad))
        draw_lbl.line([p0, p1], fill=2, width=10)
        draw_msk.line([p0, p1], fill=255, width=10)

    labels = np.asarray(label_img, dtype=np.uint8)
    mask = np.asarray(mask_img, dtype=np.uint8) > 0
    rgb = bg.copy()
    rgb[mask] = 65
    prob = np.where(labels == 1, 0.92, np.where(labels == 2, 0.08, 0.50)).astype(np.float32)
    model = {
        "model_type": "RRWNET_RITE",
        "sha256": "32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485",
        "dataset_id": "AVRDB",
        "training_dataset_id": "RITE",
        "weights_license": "CC-BY-NC-4.0",
        "vessel_threshold": 0.5,
    }
    od = {"center_x": cx, "center_y": cy, "radius_px": r_od, "contrast": 0.35}
    return measure_avr_prediction(
        rgb=rgb,
        model=model,
        optic_disc=od,
        labels=labels,
        probability=prob,
        generate_visualization=False,
        measurement_mask=mask,
    )


def _ensure_trace_calibers_importable() -> None:
    """Allow importing trace_root_calibers in minimal local venvs lacking cv2 (unused by trace_root_calibers)."""
    try:
        import cv2  # noqa: F401
    except ImportError:
        import types

        sys.modules.setdefault("app.services.vision_runtime", types.SimpleNamespace(cv2=None))


def run_live_avr_case_pair() -> Dict[str, Any]:
    """Run real measure_avr_prediction on both a measurable 6A+6V scene and an unavailable scene."""
    _ensure_trace_calibers_importable()
    measured = _build_radial_avr_scene(artery_count=6, vein_count=6)
    unavailable = _build_radial_avr_scene(artery_count=3, vein_count=3)
    return {"measured": measured, "unavailable": unavailable}


def verify_defense_doc(
    doc_path: Path = DEFAULT_DOC_PATH,
    root: Path = REPO_ROOT,
) -> List[str]:
    """Verify the 3-minute defense script length, terminology, and typed 1.3.0 case pairs."""
    _ = root
    errors: List[str] = []
    if not doc_path.is_file():
        return [f"Missing defense script document: {doc_path}"]

    text = doc_path.read_text(encoding="utf-8")
    m_speech = _SPEECH_RE.search(text)
    if not m_speech:
        errors.append("Missing <!-- SPEECH_TRANSCRIPT_START --> ... <!-- SPEECH_TRANSCRIPT_END --> block.")
    else:
        speech = m_speech.group(1).strip()
        words = [w for w in re.split(r"\s+", speech) if w and not w.startswith("[")]
        if not (300 <= len(words) <= 550):
            errors.append(f"3-minute speech word count out of expected range [300, 550]: {len(words)} words.")
        low_speech = speech.lower()
        for phrase in FORBIDDEN_SPEECH_CLAIMS:
            if phrase in low_speech:
                errors.append(f"Forbidden misleading claim in speech transcript: {phrase!r}")
        for term in REQUIRED_TERMS:
            if term not in low_speech:
                errors.append(f"Required source-aligned term missing from speech transcript: {term!r}")

    low_all = text.lower()
    for phrase in FORBIDDEN_SPEECH_CLAIMS:
        if phrase in low_all:
            errors.append(f"Forbidden misleading claim in defense document: {phrase!r}")

    # Validate both embedded AnalysisResponse examples against Pydantic 1.3.0 contract
    for tag, expect_available in (("CASE_MEASURED_JSON", True), ("CASE_UNAVAILABLE_JSON", False)):
        try:
            payload = _extract_tagged_json(text, tag)
            parsed = AnalysisResponse.model_validate(payload)
        except Exception as exc:
            errors.append(f"{tag} failed AnalysisResponse validation: {exc}")
            continue

        if parsed.is_mock is not False or parsed.risk_available is not False or parsed.risk_assessment is not None:
            errors.append(f"{tag} must enforce is_mock=false, risk_available=false, risk_assessment=null.")
        if parsed.research is None:
            errors.append(f"{tag} must populate typed research payload.")
            continue
        if parsed.research.dr.gradcam_method != "GRAD_CAM" or parsed.research.dr.gradcam_geometry != "IMAGENET_CENTER_CROP_224":
            errors.append(f"{tag} must keep GRAD_CAM + IMAGENET_CENTER_CROP_224.")

        avr = parsed.research.avr
        if expect_available:
            if avr.measurement_available is not True or avr.avr is None or avr.avr <= 0:
                errors.append("CASE_MEASURED_JSON must have measurement_available=true and positive avr.")
            if parsed.metrics.av_ratio != avr.avr:
                errors.append("CASE_MEASURED_JSON metrics.av_ratio must match research.avr.avr.")
        else:
            if avr.measurement_available is not False or avr.avr is not None or avr.crae_px is not None or avr.crve_px is not None:
                errors.append("CASE_UNAVAILABLE_JSON must have measurement_available=false and all AVR calibers null.")
            if not avr.unavailable_reason:
                errors.append("CASE_UNAVAILABLE_JSON must specify non-empty unavailable_reason.")
            if parsed.metrics.av_ratio is not None:
                errors.append("CASE_UNAVAILABLE_JSON metrics.av_ratio must be null.")
            if not any(w.code == "AVR_UNAVAILABLE" for w in parsed.warnings):
                errors.append("CASE_UNAVAILABLE_JSON warnings must include AVR_UNAVAILABLE.")

    # Execute live AVR geometry check
    live = run_live_avr_case_pair()
    if live["measured"].get("measurement_available") is not True or live["measured"].get("avr") is None:
        errors.append("Live measurable AVR scene failed to produce measurement_available=true.")
    if live["unavailable"].get("measurement_available") is not False or live["unavailable"].get("avr") is not None:
        errors.append("Live unavailable AVR scene failed abstention invariant.")
    if not live["unavailable"].get("unavailable_reason"):
        errors.append("Live unavailable AVR scene missed unavailable_reason.")

    return errors


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Verify Sprint 8 W8-M4-03 defense script and live case pair.")
    parser.add_argument("--verify", action="store_true", help="Verify defense script and run live case pair.")
    parser.add_argument("--doc", type=Path, default=DEFAULT_DOC_PATH, help="Path to defense script markdown.")
    parser.add_argument("--print-live", action="store_true", help="Print live AVR measured vs. unavailable outputs.")
    args = parser.parse_args(argv)

    if args.print_live:
        print(json.dumps(run_live_avr_case_pair(), indent=2, ensure_ascii=False))
        return 0

    errors = verify_defense_doc(doc_path=args.doc, root=REPO_ROOT)
    if errors:
        for err in errors:
            print(f"FAIL: {err}", file=sys.stderr)
        return 1
    print("PASS: W8-M4-03 3-minute defense script, source terminology, and measured/unavailable cases verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
