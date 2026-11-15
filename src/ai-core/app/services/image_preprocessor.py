"""
AURA AI Core - Medical Image Preprocessor & Multi-Resolution CLAHE Pipeline.
Sprint 5 - Task W5-M4-01 (SCRUM-371).

Provides:
- CLAHE (Contrast Limited Adaptive Histogram Equalization) for retinal microvasculature enhancement.
- Robust Field-of-View (FOV) mask extraction.
- Multi-Resolution Proxy Processing for ultra-high-resolution fundus images (e.g. HRF 3504x2336):
  * Evaluates CLAHE + 6-pass vectorized Frangi Hessian filtering at proxy resolution (1280x853)
    and restores exact original geometry (3504x2336) via Nearest-Neighbor upsampling.
  * Latency and memory must be measured on the deployment hardware.
  * Computes vascular graph topology continuity metrics (connected components & primary tree ratio).
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any, Dict, Optional, Tuple

import numpy as np
from .vision_runtime import cv2
from PIL import Image
from scipy.ndimage import binary_fill_holes, label, minimum_filter

from .frangi_service import (
    fast_vectorized_frangi_2d,
)



@dataclass(frozen=True)
class PreprocessorConfig:
    clahe_clip_limit: float = 2.0
    tile_grid_size: Tuple[int, int] = (8, 8)
    max_processing_width: int = 1280
    frangi_sigmas: Tuple[float, ...] = (1.0, 2.0, 3.0)
    frangi_alpha: float = 0.5
    frangi_beta: float = 0.5
    frangi_gamma: float = 15.0
    binary_threshold: float = 0.04
    fov_intensity_threshold: int = 15

    def __post_init__(self) -> None:
        if not np.isfinite(self.clahe_clip_limit) or self.clahe_clip_limit <= 0 or not 0 < self.binary_threshold < 1:
            raise ValueError("CLAHE clip limit must be positive; segmentation threshold must be within (0, 1).")
        if self.max_processing_width < 32 or len(self.tile_grid_size) != 2 or any(n <= 0 for n in self.tile_grid_size):
            raise ValueError("Invalid proxy size or CLAHE grid.")
        if not self.frangi_sigmas or any(not np.isfinite(s) or s <= 0 for s in self.frangi_sigmas):
            raise ValueError("Frangi scales must be positive and finite.")


def apply_clahe(
    channel_norm: np.ndarray,
    clip_limit: float = 2.0,
    tile_grid_size: Tuple[int, int] = (8, 8),
) -> np.ndarray:
    """
    Apply Contrast Limited Adaptive Histogram Equalization (CLAHE) on a normalized 2D array [0.0, 1.0].
    Preserves fine microaneurysms and sharpens low-contrast micro-vessel ridges.
    """
    if channel_norm.ndim != 2 or not np.isfinite(channel_norm).all() or np.any((channel_norm < 0) | (channel_norm > 1)):
        raise ValueError("CLAHE expects a finite normalized 2D channel.")
    source = np.rint(channel_norm * 255).astype(np.uint8)
    return cv2.createCLAHE(clipLimit=clip_limit, tileGridSize=tile_grid_size).apply(source).astype(np.float32) / 255.0


def estimate_fov_mask(rgb: np.ndarray, threshold: int = 15) -> np.ndarray:
    """
    Estimate the ocular Field-of-View (FOV) mask from an RGB fundus image.
    Uses downscaled luminance thresholding, morphological hole filling, and boundary erosion,
    then upsamples to original dimensions for high speed (< 60ms) and bounded memory.
    """
    orig_h, orig_w = rgb.shape[:2]
    scale = min(1.0, 800.0 / float(max(orig_w, orig_h)))
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


def compute_vessel_topology_metrics(binary_mask: np.ndarray) -> Dict[str, Any]:
    """
    Evaluate vascular network topology continuity on the segmented binary vessel mask:
    - connected_components: Number of 8-connected vascular subgraphs.
    - largest_component_ratio: Fraction of vessel pixels connected to the primary vascular tree.
    """
    if not np.any(binary_mask):
        return {
            "connected_components": 0,
            "largest_component_ratio": 0.0,
        }
    lbl, num_components = label(binary_mask, structure=np.ones((3, 3), dtype=np.int32))
    if num_components == 0:
        return {
            "connected_components": 0,
            "largest_component_ratio": 0.0,
        }
    counts = np.bincount(lbl.ravel())[1:]
    total_px = int(counts.sum())
    largest_px = int(counts.max()) if len(counts) > 0 else 0
    largest_ratio = round(float(largest_px / total_px), 4) if total_px > 0 else 0.0
    return {
        "connected_components": int(num_components),
        "largest_component_ratio": largest_ratio,
    }


def process_hrf_frangi_vessel_segmentation(
    rgb: np.ndarray,
    config: Optional[PreprocessorConfig] = None,
    ground_truth_mask: Optional[np.ndarray] = None,
    ground_truth_fov: Optional[np.ndarray] = None,
) -> Dict[str, Any]:
    """
    Multi-Resolution Proxy Processing pipeline for ultra-high-resolution retinal vessel segmentation:
    1. Reads original dimensions (e.g. HRF 3504x2336).
    2. Downscales green channel to proxy resolution (max_processing_width, e.g. 1280x853)
       to bound the processing array dimensions; benchmark latency and RAM separately.
    3. Applies OpenCV CLAHE (clipLimit=2.0, grid=(8,8)) and returns float32 [0,1].
    4. Runs fast 6-pass vectorized Frangi Hessian filter on CPU.
    5. Evaluates vascular topology continuity on the segmented proxy graph.
    6. Thresholds and restores binary mask to exact original geometry (3504x2336) via Nearest-Neighbor upsampling.
    7. Masks out non-FOV pixels and computes Dice/IoU/Sensitivity/Specificity if ground_truth_mask is provided.
    """
    cfg = config or PreprocessorConfig()
    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.dtype != np.uint8 or min(rgb.shape[:2]) < 1:
        raise ValueError("Expected a nonempty uint8 RGB image.")
    if ground_truth_mask is not None and ground_truth_mask.shape != rgb.shape[:2]:
        raise ValueError("Ground truth dimensions must match the original image.")
    if ground_truth_fov is not None and ground_truth_fov.shape != rgb.shape[:2]:
        raise ValueError("Reference FOV dimensions must match the original image.")
    orig_h, orig_w = rgb.shape[:2]
    t0 = time.perf_counter()

    # Determine proxy processing resolution
    if max(orig_w, orig_h) > cfg.max_processing_width:
        scale = cfg.max_processing_width / float(max(orig_w, orig_h))
        proc_w = max(1, int(round(orig_w * scale)))
        proc_h = max(1, int(round(orig_h * scale)))
    else:
        scale = 1.0
        proc_w, proc_h = orig_w, orig_h

    # FOV estimation at original resolution
    fov_orig = estimate_fov_mask(rgb, threshold=cfg.fov_intensity_threshold)

    # Green channel extraction and resizing to proxy resolution
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
        dtype=np.float32,
    )
    t_frangi = time.perf_counter()

    # Thresholding & Nearest-Neighbor Upsampling to exact original dimensions
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

    # Vascular graph topology continuity verification
    topology = compute_vessel_topology_metrics(mask_proc)

    total_latency_ms = round((t_final - t0) * 1000.0, 2)
    clahe_latency_ms = round((t_clahe - t_scale) * 1000.0, 2)
    frangi_latency_ms = round((t_frangi - t_clahe) * 1000.0, 2)
    upsample_latency_ms = round((t_final - t_frangi) * 1000.0, 2)

    # Vessel density within FOV
    fov_pixels = int(np.count_nonzero(fov_orig))
    vessel_pixels = int(np.count_nonzero(mask_final))
    vessel_density = round(float(vessel_pixels / fov_pixels), 6) if fov_pixels > 0 else 0.0

    metrics: Dict[str, Any] = {
        "processing_mode": "MULTI_RESOLUTION_PROXY" if scale < 1.0 else "FULL_RESOLUTION",
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
        "topology_connected_components": topology["connected_components"],
        "topology_largest_component_ratio": topology["largest_component_ratio"],
    }

    if ground_truth_mask is not None:
        evaluation_fov = (ground_truth_fov if ground_truth_fov.dtype == bool else ground_truth_fov > 0) if ground_truth_fov is not None else fov_orig
        gt_bool = ground_truth_mask if ground_truth_mask.dtype == bool else ground_truth_mask > 0
        inter = int(np.count_nonzero(mask_final & gt_bool & evaluation_fov))
        pred_cnt = int(np.count_nonzero(mask_final & evaluation_fov))
        gt_cnt = int(np.count_nonzero(gt_bool & evaluation_fov))
        union = pred_cnt + gt_cnt - inter
        dice = round(float((2.0 * inter) / (pred_cnt + gt_cnt)), 6) if (pred_cnt + gt_cnt) > 0 else 0.0
        iou = round(float(inter / union), 6) if union > 0 else 0.0

        tp = inter
        fp = pred_cnt - inter
        fn = gt_cnt - inter
        tn = int(np.count_nonzero(evaluation_fov)) - (tp + fp + fn)

        sensitivity = round(float(tp / (tp + fn)), 6) if (tp + fn) > 0 else 0.0
        specificity = round(float(tn / (tn + fp)), 6) if (tn + fp) > 0 else 0.0

        metrics.update({
            "dice": dice,
            "iou": iou,
            "sensitivity": sensitivity,
            "specificity": specificity,
            "ground_truth_vessel_pixel_count": gt_cnt,
            "evaluation_fov_source": "REFERENCE_MASK" if ground_truth_fov is not None else "ESTIMATED_FOV",
        })

    metrics["total_latency_ms"] = round((time.perf_counter() - t0) * 1000.0, 2)
    return {
        "mask": mask_final,
        "fov": fov_orig,
        "metrics": metrics,
    }
