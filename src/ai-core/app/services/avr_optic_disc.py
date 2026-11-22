"""Pinned PVBM optic-disc ONNX inference; predictions are not expert reference landmarks."""
import numpy as np
from PIL import Image
from functools import lru_cache

from .vision_runtime import cv2

OD_REVISION = 'cf1f116d6b9898aa7b2aa3b075e071d3460e3a6e'
OD_SHA256 = 'f72f6c9ea2ade798b80539de77fa610bb6553bb0fd7fcb78d1e841982cae0b5f'


@lru_cache(maxsize=1)
def _optic_disc_session(path, digest):
    # load_avr_model verifies the pinned digest on every request before reuse.
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    options.enable_cpu_mem_arena = False
    options.enable_mem_pattern = False
    return ort.InferenceSession(path, options, providers=['CPUExecutionProvider'])


def locate_optic_disc(rgb, model):
    if model.get('model_type') != 'RRWNET_RITE':
        from .avr_geometry import estimate_optic_disc
        return estimate_optic_disc(rgb)
    session = _optic_disc_session(model['od_weights_path'], model['od_weights_sha256'])
    small = np.asarray(Image.fromarray(rgb).resize((512, 512), Image.Resampling.BICUBIC), dtype=np.float32) / 255
    normalized = (small - np.array([0.485, 0.456, 0.406], np.float32)) / np.array([0.229, 0.224, 0.225], np.float32)
    logits = session.run(None, {session.get_inputs()[0].name: normalized.transpose(2, 0, 1)[None]})[0]
    return optic_disc_geometry(rgb.shape[:2], logits)


def optic_disc_geometry(image_shape, logits):
    if logits.shape != (1, 4, 512, 512) or not np.isfinite(logits).all():
        raise ValueError('Invalid optic-disc model output.')
    mask = np.asarray(Image.fromarray((logits[0, 0] > 0).astype(np.uint8)).resize(
        (image_shape[1], image_shape[0]), Image.Resampling.NEAREST))
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None
    contour = max(contours, key=cv2.contourArea)
    area = cv2.contourArea(contour)
    if not 0.0005 <= area / (image_shape[0] * image_shape[1]) <= 0.05 or len(contour) < 5:
        return None
    (_, _), axes, _ = cv2.fitEllipse(contour)
    circularity = 4 * np.pi * area / max(cv2.arcLength(contour, True)**2, 1)
    if min(axes) / max(axes) < 0.4 or circularity < 0.25:
        return None
    (cx, cy), radius = cv2.minEnclosingCircle(contour)
    return dict(center_x=float(cx), center_y=float(cy), radius_px=float(radius),
                method='LUNETV2_ODC_ONNX_PVBM_MIN_ENCLOSING_CIRCLE', weights_sha256=OD_SHA256,
                source_revision=OD_REVISION, expert_landmark_validated=False,
                reference_type='MODEL_PREDICTION_NOT_EXPERT_REFERENCE')
