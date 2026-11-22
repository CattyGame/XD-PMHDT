"""Pinned RRWNet RITE inference, research/noncommercial weights; no online runtime downloads."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
from functools import lru_cache

import numpy as np
from PIL import Image
from scipy.ndimage import binary_erosion, gaussian_filter
from skimage.morphology import disk

WEIGHTS_SHA256 = "32d93fa770f2f1c52359cc30ae524346043443b972b108974dbf524db8104485"
MODEL_REVISION = "9b61e9042e7693f2538e01be9b15c68a3656718f"
AVRDB_MANIFEST_SHA256 = '0481a7cad9b3a534067cc5facdaa85551eef4d6177c2c02fcfe13871d390f51f'
CALIBRATION_CASES = frozenset(('IM000004', 'IM001154', 'IM001155', 'IM001218', 'IM001219', 'IM001246',
    'IM002742', 'IM004516', 'IM004539', 'IM004564', 'IM005132', 'IM005321', 'IM005489', 'IM005523', 'IM005833'))


def verify_rrwnet_artifact(model, descriptor_path):
    from .avr_optic_disc import OD_SHA256, OD_REVISION
    if (model.get('dataset_id') != 'AVRDB' or model.get('training_dataset_id') != 'RITE'
            or model.get('weights_sha256') != WEIGHTS_SHA256 or model.get('model_revision') != MODEL_REVISION
            or model.get('weights_license') != 'CC-BY-NC-4.0' or model.get('research_only') is not True):
        raise ValueError('Unsupported RRWNet provenance/license.')
    if model.get('weights_file') != 'model.safetensors':
        raise ValueError('Expected local pinned model.safetensors beside the descriptor.')
    if (model.get('iterations') != 5 or model.get('max_width') != 584
            or model.get('preprocessing') != 'author-enhancement-v1'
            or model.get('measurement_vessel_threshold', 0.5) != 0.5):
        raise ValueError('RRWNet inference parameters differ from the pinned protocol.')
    if model.get('calibration_file') is not None:
        verify_vessel_calibration(model, descriptor_path)
    elif model.get('vessel_threshold') != 0.5 or model.get('calibration_sha256') is not None:
        raise ValueError('Non-default threshold requires a verified validation calibration.')
    weights = Path(descriptor_path).resolve().parent / 'model.safetensors'
    with weights.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if digest != WEIGHTS_SHA256:
        raise ValueError('RRWNet weights SHA-256 mismatch.')
    model['weights_path'] = str(weights)
    if (model.get('optic_disc_method') != 'LUNETV2_ODC_ONNX'
            or model.get('od_weights_file') != 'lunetv2_odc.onnx'
            or model.get('od_weights_sha256') != OD_SHA256 or model.get('od_model_revision') != OD_REVISION):
        raise ValueError('Missing or unpinned optic-disc artifact.')
    od_weights = Path(descriptor_path).resolve().parent / 'lunetv2_odc.onnx'
    with od_weights.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != OD_SHA256:
            raise ValueError('Optic-disc weights SHA-256 mismatch.')
    model['od_weights_path'] = str(od_weights)
    return model


def verify_vessel_calibration(model, descriptor_path):
    if model.get('calibration_file') != 'calibration.json':
        raise ValueError('Expected local calibration.json beside the descriptor.')
    with (Path(descriptor_path).resolve().parent / 'calibration.json').open('rb') as stream:
        raw = stream.read(1_000_001)
    if len(raw) > 1_000_000 or hashlib.sha256(raw).hexdigest() != model.get('calibration_sha256'):
        raise ValueError('Calibration SHA-256 mismatch or oversized report.')
    report = json.loads(raw)
    if not isinstance(report, dict):
        raise ValueError('Expected a calibration report object.')
    cases = report.get('validation_cases', [])
    if (report.get('algorithm') != 'rrwnet-vessel-validation-dice-v1' or report.get('dataset_id') != 'AVRDB'
            or report.get('base_weights_sha256') != WEIGHTS_SHA256
            or report.get('manifest_sha256') != AVRDB_MANIFEST_SHA256
            or model.get('manifest_sha256') != AVRDB_MANIFEST_SHA256
            or not isinstance(cases, list) or any(not isinstance(case, str) for case in cases)
            or len(cases) != 15 or set(cases) != CALIBRATION_CASES or model.get('validation_cases') != cases
            or report.get('test_cases_used') is not False or report.get('iowa_references_used') is not False):
        raise ValueError('Calibration provenance must use only the frozen AVRDB validation cases.')
    grid = [round(i / 20, 2) for i in range(1, 11)]
    candidates = report.get('candidates', [])
    if report.get('threshold_grid') != grid or not isinstance(candidates, list) or len(candidates) != len(grid):
        raise ValueError('Unexpected validation calibration grid.')
    for threshold, row in zip(grid, candidates):
        if not isinstance(row, dict) or row.get('threshold') != threshold or any(not isinstance(row.get(k), (int, float))
                or isinstance(row[k], bool) or not np.isfinite(row[k]) or not 0 <= row[k] <= 1
                for k in ('vessel_dice', 'av_accuracy_on_reference_support')):
            raise ValueError('Invalid validation calibration scores.')
    eligible = [r for r in candidates if r['av_accuracy_on_reference_support'] >= .60]
    winner = max(eligible or candidates, key=lambda r: (r['vessel_dice'], r['threshold']))
    if (report.get('selected_threshold') != winner['threshold']
            or model.get('vessel_threshold') != winner['threshold']):
        raise ValueError('Threshold differs from the reproducible validation selection.')


def enhance_rrwnet(rgb):
    """Author preprocessing (MIT), with explicit constant-image rejection."""
    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.dtype != np.uint8 or min(rgb.shape[:2]) < 1:
        raise ValueError('Expected a nonempty uint8 RGB photograph.')
    height, width = rgb.shape[:2]
    scale = min(1.0, 584 / max(height, width))
    size = (max(1, round(width * scale)), max(1, round(height * scale)))
    small = np.asarray(Image.fromarray(rgb).resize(size, Image.Resampling.BILINEAR))
    fov = binary_erosion(small.max(axis=2) > 20, disk(5))
    image = small.astype(np.float64) / 255
    zoom = Image.fromarray(small).resize((int(small.shape[1] * 1.15), int(small.shape[0] * 1.15)), Image.Resampling.BICUBIC)
    x, y = zoom.width // 2 - small.shape[1] // 2, zoom.height // 2 - small.shape[0] // 2
    zoomed = np.asarray(zoom)[y:y + small.shape[0], x:x + small.shape[1]].astype(np.float64) / 255
    composed = np.where(fov[:, :, None], image, zoomed)
    enhanced = composed - gaussian_filter(composed, sigma=(10, 10, 0))
    enhanced[~fov] = 0
    deviation = enhanced.std()
    if not np.isfinite(deviation) or deviation <= 1e-8:
        raise ValueError('No usable contrast for RRWNet preprocessing.')
    enhanced /= deviation
    low, high = enhanced.min(), enhanced.max()
    enhanced = (enhanced - low) / (high - low)
    enhanced[~fov] = 0
    return enhanced.astype(np.float32), fov


@lru_cache(maxsize=1)
def _rrwnet_network(path, digest):
    import torch
    from safetensors.torch import load_model
    from .third_party.rrwnet.model import RRWNet
    network = RRWNet().eval()
    load_model(network, path, strict=True, device='cpu')
    network.to(memory_format=torch.channels_last)
    return network


def rrwnet_probabilities(rgb, model):
    import torch

    enhanced, fov = enhance_rrwnet(rgb)
    height, width = enhanced.shape[:2]
    dy, dx = 32 - height % 32, 32 - width % 32
    top, left = dy // 2, dx // 2
    padded = np.pad(enhanced, ((top, dy - top), (left, dx - left), (0, 0)))
    # Artifact hashes remain verified by load_avr_model before every inference.
    network = _rrwnet_network(model['weights_path'], model['weights_sha256'])
    tensor = torch.from_numpy(padded.transpose(2, 0, 1).copy())[None].to(memory_format=torch.channels_last)
    with torch.inference_mode():
        probabilities = torch.sigmoid(network(tensor)[-1])[0].permute(1, 2, 0).numpy()
    probabilities = probabilities[top:top + height, left:left + width].copy()
    if not np.isfinite(probabilities).all():
        raise ValueError('Nonfinite RRWNet predictions.')
    probabilities[~fov] = 0
    return probabilities


def segment_rrwnet(rgb, model):
    probabilities = rrwnet_probabilities(rgb, model)
    return rrwnet_labels(probabilities, (rgb.shape[1], rgb.shape[0]), model['vessel_threshold'])


def rrwnet_prediction_arrays(probabilities, size, vessel_threshold):
    """Calibrate output support without changing the author's fixed 0.5 caliber seeds."""
    labels, probability = rrwnet_labels(probabilities, size, vessel_threshold)
    seeds = (probabilities[:, :, 2] >= 0.5).astype(np.uint8)
    measurement_mask = np.asarray(Image.fromarray(seeds).resize(size, Image.Resampling.NEAREST)) > 0
    return labels, probability, measurement_mask


def rrwnet_labels(probabilities, size, vessel_threshold):
    """Shared native-geometry decoding for runtime and validation-only calibration."""
    if (probabilities.ndim != 3 or probabilities.shape[2] != 3
            or not np.isfinite(probabilities).all() or np.any(probabilities < 0) or np.any(probabilities > 1)
            or isinstance(vessel_threshold, bool) or not 0 < vessel_threshold < 1
            or len(size) != 2 or any(not isinstance(x, int) or x < 1 for x in size)):
        raise ValueError('Invalid RRWNet probability geometry, threshold or output size.')
    labels = np.where(probabilities[:, :, 0] >= probabilities[:, :, 1], 1, 2).astype(np.uint8)
    labels[probabilities[:, :, 2] < vessel_threshold] = 0
    probability = probabilities[:, :, 0] / np.maximum(probabilities[:, :, :2].sum(axis=2), 1e-8)
    return (np.asarray(Image.fromarray(labels).resize(size, Image.Resampling.NEAREST)),
            np.asarray(Image.fromarray(probability).resize(size, Image.Resampling.BILINEAR)))
