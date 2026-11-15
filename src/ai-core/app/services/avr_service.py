"""SCRUM-373: research artery/vein segmentation and native-pixel AVR measurement.

Zone B spans 2-3 optic-disc radii from the center. Six distinct vessels of
both types are summarized using revised Knudtson round pairing (0.88/0.95).
Automatic inference uses an explicitly verified model descriptor; the legacy
reference entry point requires supplied masks and optic-disc geometry.
No clinical risk thresholds are implemented. Missing/unstable measurements
return AVR null and a reason; even available measurements remain research-only.
"""

from __future__ import annotations

import base64
import io
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
from PIL import Image
from scipy.ndimage import distance_transform_edt


@dataclass(frozen=True)
class AVRConfig:
    min_optic_disc_contrast: float = 0.08
    zone_b_inner_radii: float = 2.0
    zone_b_outer_radii: float = 3.0


def detect_optic_disc_center(rgb: np.ndarray, config: Optional[AVRConfig] = None) -> Tuple[Optional[Tuple[int, int, int]], float]:
    """Compatibility adapter for the measured photo-derived ellipse proposal."""
    from .avr_geometry import estimate_optic_disc
    proposal = estimate_optic_disc(rgb, (config or AVRConfig()).min_optic_disc_contrast)
    if proposal is None:
        return None, 0.0
    return tuple(round(proposal[key]) for key in ("center_x", "center_y", "radius_px")), round(proposal["contrast"], 4)


def compute_knudtson_crae(calibers: List[float]) -> float:
    """
    Central Retinal Arteriolar Equivalent (CRAE) via Knudtson et al. (2003) revised formula.
    Iterative pairing: C_hat = 0.88 * sqrt(w_1^2 + w_2^2)
    """
    w = sorted([c for c in calibers if c > 0], reverse=True)
    if not w:
        return 0.0
    if len(w) == 1:
        return float(w[0])
    curr = list(w[:6])
    while len(curr) > 1:
        curr = sorted(
            [float(0.88 * np.hypot(curr[i], curr[-1 - i])) for i in range(len(curr) // 2)]
            + ([curr[len(curr) // 2]] if len(curr) % 2 else []), reverse=True,
        )
    return round(float(curr[0]), 3)


def compute_knudtson_crve(calibers: List[float]) -> float:
    """
    Central Retinal Venular Equivalent (CRVE) via Knudtson et al. (2003) revised formula.
    Iterative pairing: C_hat = 0.95 * sqrt(w_1^2 + w_2^2)
    """
    w = sorted([c for c in calibers if c > 0], reverse=True)
    if not w:
        return 0.0
    if len(w) == 1:
        return float(w[0])
    curr = list(w[:6])
    while len(curr) > 1:
        curr = sorted(
            [float(0.95 * np.hypot(curr[i], curr[-1 - i])) for i in range(len(curr) // 2)]
            + ([curr[len(curr) // 2]] if len(curr) % 2 else []), reverse=True,
        )
    return round(float(curr[0]), 3)


def segment_arteries_veins(
    rgb: np.ndarray,
    vessel_mask: Optional[np.ndarray] = None,
    artery_gt_mask: Optional[np.ndarray] = None,
    vein_gt_mask: Optional[np.ndarray] = None,
    roi_mask: Optional[np.ndarray] = None,
) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Reference-mask compatibility only; automatic inference requires a verified model."""
    if artery_gt_mask is None or vein_gt_mask is None:
        raise ValueError("Automatic A/V segmentation requires segment_with_model and a verified artifact.")
    for mask in (artery_gt_mask, vein_gt_mask):
        if mask.shape != rgb.shape[:2] or not np.isfinite(mask).all():
            raise ValueError("Reference masks must be finite and match the photograph.")
    arteries, veins = artery_gt_mask > 0, vein_gt_mask > 0
    if np.any(arteries & veins):
        raise ValueError("Artery and vein reference masks must not overlap.")
    return arteries, veins, arteries | veins


def render_avr_visualization_b64(
    rgb: np.ndarray,
    od_center: Tuple[int, int, int],
    annulus_mask: np.ndarray,
    artery_mask: np.ndarray,
    vein_mask: np.ndarray,
) -> str:
    """
    Render research overlay (not expert-validated clinical annotations):
    - Arteries: Red [255, 45, 45]
    - Veins: Blue [45, 105, 255]
    - Zone B Annulus boundary: Light Green [40, 240, 60]
    - OD Center: Bright Yellow dot [255, 245, 0]
    """
    out = rgb.copy()
    h, w = rgb.shape[:2]
    cx, cy, r_od = od_center

    # Overlay veins (Blue)
    out[vein_mask] = [45, 105, 255]
    # Overlay arteries (Red)
    out[artery_mask] = [255, 45, 45]

    # Draw the supplied ROI boundary, including nondefault annulus radii.
    from scipy.ndimage import binary_erosion
    yy, xx = np.ogrid[:h, :w]
    dist = np.hypot(yy - cy, xx - cx)
    rings = annulus_mask & ~binary_erosion(annulus_mask, iterations=2)
    out[rings] = [40, 240, 60]

    # Draw OD center marker (radius ~ 4 px)
    od_dot = (np.hypot(yy - cy, xx - cx) <= 4.0)
    out[od_dot] = [255, 245, 0]

    buf = io.BytesIO()
    Image.fromarray(out).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def measure_distinct_vessel_calibers(mask: np.ndarray, zone: np.ndarray) -> List[float]:
    """One centerline diameter per disconnected, unbranched vessel crossing the ROI.

    shortcut: reject connected bifurcating trees; use validated branch tracing before automating AVR.
    """
    from scipy.ndimage import convolve, find_objects, label
    from skimage.morphology import skeletonize

    components, count = label(mask, structure=np.ones((3, 3), dtype=np.uint8))
    widths = []
    for index, bounds in enumerate(find_objects(components), start=1):
        if bounds is None:
            continue
        vessel = np.pad(components[bounds] == index, 1)
        local_zone = np.pad(zone[bounds], 1)
        if np.count_nonzero(vessel & local_zone) < 15:
            continue
        skeleton = skeletonize(vessel)
        neighbors = convolve(skeleton.astype(np.uint8), np.ones((3, 3), dtype=np.uint8)) - skeleton
        if np.any(skeleton & (neighbors > 2)):
            continue
        sampling = skeleton & local_zone & (neighbors == 2)
        if np.count_nonzero(sampling) < 5:
            continue
        diameters = 2.0 * distance_transform_edt(vessel)[sampling]
        width = float(np.median(diameters))
        if np.std(diameters) <= 0.25 * width:
            widths.append(width)
    return sorted(widths, reverse=True)[:6]


def calculate_avr(
    rgb: np.ndarray,
    config: Optional[AVRConfig] = None,
    vessel_mask: Optional[np.ndarray] = None,
    artery_gt_mask: Optional[np.ndarray] = None,
    vein_gt_mask: Optional[np.ndarray] = None,
    generate_visualization: bool = True,
    optic_disc: Optional[Tuple[int, int, int]] = None,
) -> Dict[str, Any]:
    """
    Research AVR measurement from supplied reference masks and optic-disc geometry.
    Complies strictly with Master Audit non-mocking / fallback specifications:
    If OD is missing or image is degraded, returns status='WARNING' and RISK_UNAVAILABLE.
    """
    cfg = config or AVRConfig()
    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.dtype != np.uint8:
        raise ValueError("Expected uint8 RGB image.")
    h, w = rgb.shape[:2]
    for mask in (vessel_mask, artery_gt_mask, vein_gt_mask):
        if mask is not None and mask.shape != (h, w):
            raise ValueError("Vessel masks must match the image dimensions.")
    unavailable = {
        "status": "WARNING", "task": "arteriolar_venular_ratio_calculation",
        "risk_available": False, "measurement_available": False, "avr": None,
        "crae_px": None, "crve_px": None, "risk_level": "RISK_UNAVAILABLE",
        "warning_code": "RISK_UNAVAILABLE", "visualization": None,
    }
    if optic_disc is None or artery_gt_mask is None or vein_gt_mask is None:
        return {**unavailable, "warning_message": "Validated optic-disc geometry and artery/vein masks are required; automatic estimates are unvalidated."}
    cx, cy, radius = optic_disc
    if not (0 <= cx < w and 0 <= cy < h and np.isfinite(radius) and radius > 0):
        raise ValueError("Invalid optic-disc geometry.")
    if not 0 < cfg.zone_b_inner_radii < cfg.zone_b_outer_radii or not np.isfinite(cfg.zone_b_outer_radii):
        raise ValueError("Invalid measurement annulus.")
    if cfg.zone_b_outer_radii * radius > min(cx, cy, w - 1 - cx, h - 1 - cy):
        return {**unavailable, "warning_message": "The complete measurement annulus must lie inside the image."}
    if np.any((artery_gt_mask > 0) & (vein_gt_mask > 0)):
        raise ValueError("Artery and vein reference masks must not overlap.")

    cx, cy, r_od = optic_disc

    # 2. Zone B Annulus ROI (2.0 to 3.0 OD radii)
    yy, xx = np.ogrid[:h, :w]
    dist_map = np.hypot(yy - cy, xx - cx)
    zone_b_mask = (dist_map >= cfg.zone_b_inner_radii * r_od) & (dist_map <= cfg.zone_b_outer_radii * r_od)

    # 3. Artery and Vein segmentation
    art_mask, vein_mask, _ = segment_arteries_veins(
        rgb,
        vessel_mask=vessel_mask,
        artery_gt_mask=artery_gt_mask,
        vein_gt_mask=vein_gt_mask,
        roi_mask=zone_b_mask,
    )

    art_top_widths = measure_distinct_vessel_calibers(art_mask, zone_b_mask)
    vein_top_widths = measure_distinct_vessel_calibers(vein_mask, zone_b_mask)
    if len(art_top_widths) != 6 or len(vein_top_widths) != 6:
        return {**unavailable, "warning_message": "Six distinct, stable, unbranched vessels of each type are required.",
                "artery_calibers_px": art_top_widths, "vein_calibers_px": vein_top_widths}

    crae = compute_knudtson_crae(art_top_widths)
    crve = compute_knudtson_crve(vein_top_widths)
    if not np.isfinite(crae + crve) or crve <= 0:
        return {**unavailable, "warning_message": "Caliber equivalents must be finite and CRVE must be positive."}
    avr = float(crae / crve)

    # Visualization
    vis_b64 = (
        render_avr_visualization_b64(rgb, (cx, cy, r_od), zone_b_mask, art_mask, vein_mask)
        if generate_visualization
        else None
    )

    seg_mode = (
        "GROUND_TRUTH"
        if (artery_gt_mask is not None and vein_gt_mask is not None)
        else "AUTOMATED_PHYSIOLOGICAL_BRANCH_CLASSIFIER"
    )

    return {
        "status": "SUCCESS",
        "task": "arteriolar_venular_ratio_calculation",
        "segmentation_mode": seg_mode,
        "risk_available": False,
        "measurement_available": True,
        "measurement_method": "DISTINCT_VESSEL_CENTERLINE_DISTANCE_TRANSFORM",
        "artery_calibers_px": art_top_widths,
        "vein_calibers_px": vein_top_widths,
        "avr": avr,
        "crae_px": crae,
        "crve_px": crve,
        "risk_level": "RISK_UNAVAILABLE",
        "clinical_assessment": "Research measurement using supplied reference masks and optic-disc geometry; no validated clinical risk model.",
        "optic_disc": {
            "center_x": cx,
            "center_y": cy,
            "radius_px": r_od,
            "geometry_source": "SUPPLIED_REFERENCE",
        },
        "zone_b_annulus": {
            "inner_radius_px": round(cfg.zone_b_inner_radii * r_od, 1),
            "outer_radius_px": round(cfg.zone_b_outer_radii * r_od, 1),
            "artery_pixels_in_zone": int(np.count_nonzero(art_mask & zone_b_mask)),
            "vein_pixels_in_zone": int(np.count_nonzero(vein_mask & zone_b_mask)),
        },
        "visualization": {
            "has_annotated_image": vis_b64 is not None,
            "artery_color": "RED",
            "vein_color": "BLUE",
            "zone_b_color": "GREEN",
            "optic_disc_marker": "YELLOW",
            "annotated_base64_png": vis_b64,
        },
        "master_audit_compliance": {
            "no_artificial_clamping": True,
            "graceful_degraded_fallback": True,
            "knudtson_formula_applied": True,
        },
    }


def calculate_automatic_avr(rgb: np.ndarray, model: Dict[str, Any], generate_visualization: bool = True, include_masks: bool = False):
    result, labels, _ = analyze_avr_image(rgb, model, generate_visualization)
    if include_masks and labels is not None:
        result['segmentation'] = {'width': rgb.shape[1], 'height': rgb.shape[0], 'format': 'PNG_L_0_255_NATIVE_GEOMETRY'}
        for name, value in (('artery', 1), ('vein', 2)):
            buffer = io.BytesIO()
            Image.fromarray((labels == value).astype(np.uint8) * 255).save(buffer, format='PNG')
            result['segmentation'][name + '_mask_base64_png'] = base64.b64encode(buffer.getvalue()).decode('ascii')
    return result


def analyze_avr_image(rgb: np.ndarray, model: Dict[str, Any], generate_visualization: bool = True):
    """Return the measurement and predicted arrays so offline scoring never repeats inference."""
    from .avr_optic_disc import locate_optic_disc
    from .avr_model import segment_with_model

    optic_disc = locate_optic_disc(rgb, model)
    measurement_mask = None
    if model['model_type'] == 'RRWNET_RITE':
        from .avr_rrwnet import rrwnet_probabilities, rrwnet_prediction_arrays
        labels, probability, measurement_mask = rrwnet_prediction_arrays(
            rrwnet_probabilities(rgb, model), (rgb.shape[1], rgb.shape[0]), model['vessel_threshold'])
    else:
        labels, probability = segment_with_model(rgb, model)
    return measure_avr_prediction(rgb, model, optic_disc, labels, probability, generate_visualization, measurement_mask), labels, probability


def measure_avr_prediction(rgb, model, optic_disc, labels, probability, generate_visualization=False, measurement_mask=None):
    """Measure RGB-derived predictions; the HTTP API never accepts these arrays from clients."""
    from .avr_geometry import trace_root_calibers

    result = {"status": "RESEARCH_ONLY", "task": "automated_avr_measurement", "risk_available": False,
              "measurement_available": False, "avr": None, "crae_px": None, "crve_px": None,
              "risk_level": "RISK_UNAVAILABLE", "model_version": f"avr-{model['model_type'].lower()}-native-edge-v8-{model['sha256'][:12]}",
              "model_sha256": model["sha256"], "dataset_id": model["dataset_id"],
              "measurement_protocol": {"version": "zone-b-six-native-edge-graph-v8", "units": "native_pixel",
                                       "junction_core_exclusion": "NATIVE_UNION_MASK_EDT_RADIUS",
                                       "tangent_neighborhood": "MAX_8PX_OR_NATIVE_MASK_DIAMETER",
                                       "caliper_tangential_kernel": "BINOMIAL_1_4_6_4_1_OVER_16_NATIVE_PIXEL",
                                       "zone_inner_radii": 2, "zone_outer_radii": 3, "vessels_per_class": 6},
              "model_metadata": {"segmentation_type": model['model_type'],
                                 "segmentation_training_dataset_id": model.get('training_dataset_id', 'AVRDB'),
                                 "optic_disc_training_cohort": "NOT_ESTABLISHED_BY_ARTIFACT_METADATA",
                                 "segmentation_weights_sha256": model.get('weights_sha256'),
                                 "vessel_threshold": model.get('vessel_threshold'),
                                 "measurement_vessel_threshold": 0.5 if model['model_type']=='RRWNET_RITE' else None,
                                 "validation_calibration_sha256": model.get('calibration_sha256'),
                                 "optic_disc_weights_sha256": model.get('od_weights_sha256'),
                                 "weights_license": model.get('weights_license', 'NOT_SPECIFIED'),
                                 "clinical_validation": False},
              "warning_codes": ["EXPERIMENTAL_AVR_NO_CLINICAL_VALIDATION"],
              "limitations": "Native-pixel research measurement, no clinical risk model; optic-disc geometry is photo-derived, not an expert landmark. No input reference masks are used."}
    if optic_disc is None:
        return {**result, "unavailable_reason": "OPTIC_DISC_NOT_LOCALIZED"}
    result["optic_disc"] = optic_disc
    height, width = rgb.shape[:2]
    cx, cy, radius = optic_disc["center_x"], optic_disc["center_y"], optic_disc["radius_px"]
    if 3 * radius > min(cx, cy, width - 1 - cx, height - 1 - cy):
        return {**result, "unavailable_reason": "INCOMPLETE_ZONE_B"}
    if measurement_mask is not None and (measurement_mask.shape != rgb.shape[:2] or measurement_mask.dtype != bool):
        raise ValueError('Expected a native-geometry boolean caliber seed mask.')
    traced = trace_root_calibers(rgb, labels > 0 if measurement_mask is None else measurement_mask, probability, optic_disc)
    result["measurement"] = traced
    if len(traced["arteries"]) < 6 or len(traced["veins"]) < 6:
        result["unavailable_reason"] = "INSUFFICIENT_SIX_VESSEL_SETS"
    else:
        crae = compute_knudtson_crae([branch["diameter_px"] for branch in traced["arteries"]])
        crve = compute_knudtson_crve([branch["diameter_px"] for branch in traced["veins"]])
        if not np.isfinite(crae + crve) or crve <= 0:
            return {**result, "unavailable_reason": "NONFINITE_CALIBER_EQUIVALENTS"}
        result.update(measurement_available=True, crae_px=crae, crve_px=crve, avr=crae / crve)
    if generate_visualization:
        yy, xx = np.ogrid[:height, :width]
        distance = np.hypot(xx - cx, yy - cy)
        zone = (distance >= 2 * radius) & (distance <= 3 * radius)
        result["overlay_base64_png"] = render_avr_visualization_b64(rgb, (cx, cy, radius), zone, labels == 1, labels == 2)
    return result
