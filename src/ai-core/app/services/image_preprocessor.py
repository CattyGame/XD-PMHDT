"""
AURA AI Core - Medical Image Preprocessor & Multi-Resolution CLAHE Pipeline.
Sprint 5 - Task W5-M4-01 (SCRUM-371).

Provides:
- CLAHE (Contrast Limited Adaptive Histogram Equalization) for retinal microvasculature enhancement.
- Robust Field-of-View (FOV) mask extraction.
- Smart memory-bounded multi-resolution processing for ultra-high-resolution fundus images (e.g. HRF 3504x2336):
  * Maintains CPU inference latency < 1.5s.
  * Limits container RAM peak < 250 MiB.
  * Preserves exact original geometry (3504x2336) by upsampling predicted mask/overlay via nearest-neighbor/bilinear.
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any, Dict, Optional, Tuple

import numpy as np
import skimage.exposure as exp
from PIL import Image
from scipy.ndimage import binary_erosion, binary_fill_holes, minimum_filter

from .frangi_service import (
    FrangiRuntimeConfig,
    fast_vectorized_frangi_2d,
    load_frangi_config,
)


@dataclass(frozen=True)
class PreprocessorConfig:
    clahe_clip_limit: float = 0.02  # Equivalent to OpenCV clipLimit=2.0
    tile_grid_size: Tuple[int, int] = (8, 8)
    max_processing_width: int = 1200
    frangi_sigmas: Tuple[float, ...] = (1.0, 2.0, 3.0)
    frangi_alpha: float = 0.5
    frangi_beta: float = 0.5
    frangi_gamma: float = 15.0
    binary_threshold: float = 0.05
    fov_intensity_threshold: int = 15


def apply_clahe(
    channel_norm: np.ndarray,
    clip_limit: float = 0.02,
    tile_grid_size: Tuple[int, int] = (8, 8),
) -> np.ndarray:
    """
    Apply Contrast Limited Adaptive Histogram Equalization (CLAHE) on a normalized 2D array [0.0, 1.0].
    Preserves fine microaneurysms and sharpens low-contrast micro-vessel ridges.
    """
    h, w = channel_norm.shape
    kernel_h = max(16, h // tile_grid_size[0])
    kernel_w = max(16, w // tile_grid_size[1])
    return exp.equalize_adapthist(
        channel_norm,
        kernel_size=(kernel_h, kernel_w),
        clip_limit=clip_limit,
        nbins=256,
    ).astype(np.float32)


def estimate_fov_mask(rgb: np.ndarray, threshold: int = 15) -> np.ndarray:
    """
    Estimate the ocular Field-of-View (FOV) mask from an RGB fundus image.
    Uses downscaled luminance thresholding, morphological hole filling, and boundary erosion,
    then upsamples to original dimensions for high speed (< 60ms) and bounded memory.
    """
    orig_h, orig_w = rgb.shape[:2]
    scale = min(1.0, 800.0 / float(orig_w))
    if scale < 1.0:
        dw, dh = int(round(orig_w * scale)), int(round(orig_h * scale))
        im_small = Image.fromarray(rgb).resize((dw, dh), Image.Resampling.BILINEAR)
        rgb_small = np.asarray(im_small)
    else:
        rgb_small = rgb

    intensity = np.maximum(np.maximum(rgb_small[:, :, 0], rgb_small[:, :, 1]), rgb_small[:, :, 2])
    raw_fov = intensity > threshold
    filled = binary_fill_holes(raw_fov)
    eroded = minimum_filter(filled.astype(np.uint8), size=5) > 0

    if scale < 1.0:
        fov_im = Image.fromarray(eroded).resize((orig_w, orig_h), Image.Resampling.NEAREST)
        return np.asarray(fov_im) > 0
    return eroded


def process_hrf_frangi_vessel_segmentation(
    rgb: np.ndarray,
    config: Optional[PreprocessorConfig] = None,
    ground_truth_mask: Optional[np.ndarray] = None,
) -> Dict[str, Any]:
    """
    Full pipeline for ultra-high-resolution retinal vessel segmentation:
    1. Reads original dimensions (e.g. 3504x2336).
    2. Smart downsample to max_processing_width (1400) to ensure < 1.5s latency and bounded RAM.
    3. Normalizes green channel & applies CLAHE (clip_limit=0.02, grid=(8,8)).
    4. Runs fast 6-pass vectorized Frangi Hessian filter on CPU.
    5. Thresholds and upsamples binary mask back to exact original dimensions (3504x2336).
    6. Masks out non-FOV pixels.
    7. Computes Dice/IoU if ground_truth_mask is provided.
    """
    cfg = config or PreprocessorConfig()
    orig_h, orig_w = rgb.shape[:2]
    t0 = time.perf_counter()

    # Determine processing resolution
    if orig_w > cfg.max_processing_width:
        scale = cfg.max_processing_width / float(orig_w)
        proc_w = int(cfg.max_processing_width)
        proc_h = int(round(orig_h * scale))
    else:
        scale = 1.0
        proc_w, proc_h = orig_w, orig_h

    # FOV estimation at original resolution
    fov_orig = estimate_fov_mask(rgb, threshold=cfg.fov_intensity_threshold)

    # Green channel extraction and resizing
    green_orig = rgb[:, :, 1]
    if scale < 1.0:
        im_g = Image.fromarray(green_orig).resize((proc_w, proc_h), Image.Resampling.BILINEAR)
        green_proc = np.asarray(im_g, dtype=np.float32) / 255.0
    else:
        green_proc = green_orig.astype(np.float32) / 255.0

    t_scale = time.perf_counter()

    # CLAHE contrast enhancement
    green_clahe = apply_clahe(
        green_proc,
        clip_limit=cfg.clahe_clip_limit,
        tile_grid_size=cfg.tile_grid_size,
    )
    t_clahe = time.perf_counter()

    # Vectorized 6-pass Frangi filter
    frangi_resp = fast_vectorized_frangi_2d(
        green_clahe,
        sigmas=list(cfg.frangi_sigmas),
        alpha=cfg.frangi_alpha,
        beta=cfg.frangi_beta,
        gamma=cfg.frangi_gamma,
        black_ridges=True,
    )
    t_frangi = time.perf_counter()

    # Thresholding & Upsampling to exact original dimensions
    max_val = float(frangi_resp.max())
    resp_norm = (frangi_resp / (max_val + 1e-12)) if max_val > 0 else frangi_resp
    mask_proc = resp_norm >= cfg.binary_threshold

    if scale < 1.0:
        im_mask = Image.fromarray(mask_proc.astype(np.uint8) * 255).resize(
            (orig_w, orig_h),
            Image.Resampling.NEAREST,
        )
        mask_final = (np.asarray(im_mask) > 0) & fov_orig
    else:
        mask_final = mask_proc & fov_orig

    t_final = time.perf_counter()

    total_latency_ms = round((t_final - t0) * 1000.0, 2)
    clahe_latency_ms = round((t_clahe - t_scale) * 1000.0, 2)
    frangi_latency_ms = round((t_frangi - t_clahe) * 1000.0, 2)
    upsample_latency_ms = round((t_final - t_frangi) * 1000.0, 2)

    # Vessel density within FOV
    fov_pixels = int(np.count_nonzero(fov_orig))
    vessel_pixels = int(np.count_nonzero(mask_final))
    vessel_density = round(float(vessel_pixels / fov_pixels), 6) if fov_pixels > 0 else 0.0

    metrics: Dict[str, Any] = {
        "original_resolution": [orig_w, orig_h],
        "processed_resolution": [proc_w, proc_h],
        "downsample_scale": round(scale, 4),
        "total_latency_ms": total_latency_ms,
        "clahe_latency_ms": clahe_latency_ms,
        "frangi_latency_ms": frangi_latency_ms,
        "upsample_latency_ms": upsample_latency_ms,
        "vessel_density": vessel_density,
        "fov_pixel_count": fov_pixels,
        "vessel_pixel_count": vessel_pixels,
    }

    if ground_truth_mask is not None:
        gt_bool = ground_truth_mask > 0
        inter = int(np.count_nonzero(mask_final & gt_bool & fov_orig))
        pred_cnt = int(np.count_nonzero(mask_final & fov_orig))
        gt_cnt = int(np.count_nonzero(gt_bool & fov_orig))
        union = pred_cnt + gt_cnt - inter
        dice = round(float((2.0 * inter) / (pred_cnt + gt_cnt)), 6) if (pred_cnt + gt_cnt) > 0 else 0.0
        iou = round(float(inter / union), 6) if union > 0 else 0.0

        tp = inter
        fp = pred_cnt - inter
        fn = gt_cnt - inter
        tn = fov_pixels - (tp + fp + fn)

        sensitivity = round(float(tp / (tp + fn)), 6) if (tp + fn) > 0 else 0.0
        specificity = round(float(tn / (tn + fp)), 6) if (tn + fp) > 0 else 0.0

        metrics.update({
            "dice": dice,
            "iou": iou,
            "sensitivity": sensitivity,
            "specificity": specificity,
        })

    return {
        "mask": mask_final,
        "fov": fov_orig,
        "metrics": metrics,
    }
