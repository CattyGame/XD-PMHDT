"""Multiscale RGB pixel MLP for research A/V segmentation; JSON weights, no pickle."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from .vision_runtime import cv2
import numpy as np
from PIL import Image

FEATURE_VERSION = "rgb-dog-25-v1"
MAX_EDGE = 752


def image_features(rgb: np.ndarray):
    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.dtype != np.uint8:
        raise ValueError("Expected uint8 RGB photograph.")
    height, width = rgb.shape[:2]
    scale = min(1.0, MAX_EDGE / max(height, width))
    size = (max(1, round(width * scale)), max(1, round(height * scale)))
    small = np.asarray(Image.fromarray(rgb).resize(size, Image.Resampling.BILINEAR), dtype=np.float32) / 255
    background = cv2.GaussianBlur(small, (0, 0), 15)
    features = [small[:, :, index] for index in range(3)]
    total = np.maximum(small.sum(axis=2), 0.05)
    features.extend(small[:, :, index] / total for index in range(3))
    features.extend(np.log(np.maximum(background[:, :, i], 0.02) / np.maximum(small[:, :, i], 0.02)) for i in range(3))
    for sigma in (1, 3, 7, 15):
        blur = cv2.GaussianBlur(small, (0, 0), sigma)
        features.extend((blur - small)[:, :, index] for index in range(3))
    green = small[:, :, 1]
    average = cv2.GaussianBlur(green, (0, 0), 3)
    deviation = np.sqrt(np.maximum(cv2.GaussianBlur(green * green, (0, 0), 3) - average * average, 0))
    features.extend([deviation, background[:, :, 0], background[:, :, 1], background[:, :, 2]])
    matrix = np.stack(features, axis=-1).astype(np.float32)
    return matrix, (small.max(axis=2) > 0.08), scale


def load_avr_model(path: str, expected_sha256: str):
    raw = Path(path).read_bytes()
    if hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise ValueError("AVR model SHA-256 mismatch.")
    model = json.loads(raw)
    if isinstance(model, dict) and model.get('model_type') == 'RRWNET_RITE':
        from .avr_rrwnet import verify_rrwnet_artifact
        model = verify_rrwnet_artifact(model, path)
        model['sha256'] = expected_sha256
        return model
    if (not isinstance(model, dict) or model.get("feature_version") != FEATURE_VERSION
            or model.get("model_type") != "PIXEL_MLP_RGB_DOG" or model.get("dataset_id") != "AVRDB"):
        raise ValueError("Unsupported AVR classifier.")
    if not isinstance(model.get("layers"), list) or not model["layers"] or any(not isinstance(layer, dict) for layer in model["layers"]):
        raise ValueError("Invalid AVR layers.")
    width = 25
    for layer in model["layers"]:
        weights, bias = np.asarray(layer["weight"], dtype=np.float32), np.asarray(layer["bias"], dtype=np.float32)
        if weights.ndim != 2 or weights.shape[0] != width or bias.shape != (weights.shape[1],):
            raise ValueError("Invalid AVR layer geometry.")
        if not np.isfinite(weights).all() or not np.isfinite(bias).all():
            raise ValueError("Nonfinite AVR model parameters.")
        width = weights.shape[1]
    mean, std = np.asarray(model["mean"], np.float32), np.asarray(model["std"], np.float32)
    threshold = model["vessel_threshold"]
    if (not model["layers"] or width != 3 or mean.shape != (25,) or std.shape != (25,)
            or not np.isfinite(mean).all() or not np.isfinite(std).all() or np.any(std <= 0)
            or not isinstance(threshold, (int, float)) or not np.isfinite(threshold) or not 0 < threshold < 1):
        raise ValueError("Invalid AVR class/normalization parameters.")
    model["sha256"] = expected_sha256
    return model


def pixel_probabilities(matrix, model):
    shape = matrix.shape[:2]
    flat = matrix.reshape(-1, 25)
    output = np.empty((len(flat), 3), dtype=np.float32)
    mean, std = np.asarray(model["mean"], np.float32), np.asarray(model["std"], np.float32)
    for start in range(0, len(flat), 65536):
        x = (flat[start:start + 65536] - mean) / std
        for index, layer in enumerate(model["layers"]):
            x = x @ np.asarray(layer["weight"], np.float32) + np.asarray(layer["bias"], np.float32)
            if index < len(model["layers"]) - 1:
                np.maximum(x, 0, out=x)
        x -= x.max(axis=1, keepdims=True)
        np.exp(x, out=x)
        output[start:start + len(x)] = x / x.sum(axis=1, keepdims=True)
    if not np.isfinite(output).all():
        raise ValueError("Nonfinite AVR classifier output.")
    return output.reshape(*shape, 3)


def segment_with_model(rgb, model):
    if model.get('model_type') == 'RRWNET_RITE':
        from .avr_rrwnet import segment_rrwnet
        return segment_rrwnet(rgb, model)
    features, fov, _ = image_features(rgb)
    probabilities = pixel_probabilities(features, model)
    vessel = (probabilities[:, :, 1:].sum(axis=2) >= model["vessel_threshold"]) & fov
    labels = np.where(probabilities[:, :, 1] >= probabilities[:, :, 2], 1, 2).astype(np.uint8)
    labels[~vessel] = 0
    height, width = rgb.shape[:2]
    labels = np.asarray(Image.fromarray(labels).resize((width, height), Image.Resampling.NEAREST))
    artery_probability = probabilities[:, :, 1] / np.maximum(probabilities[:, :, 1:].sum(axis=2), 1e-8)
    artery_probability = np.asarray(Image.fromarray(artery_probability).resize((width, height), Image.Resampling.BILINEAR))
    return labels, artery_probability
