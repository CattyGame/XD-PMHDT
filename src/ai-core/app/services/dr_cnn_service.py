"""Optional ResNet34 research inference; never falls back to logistic/saliency."""
from __future__ import annotations

import hashlib
from functools import lru_cache
from pathlib import Path

import numpy as np
from PIL import Image

from .dr_screening_service import render_saliency_overlay_b64


@lru_cache(maxsize=2)
def _load_verified_dr_model(resolved_path: str, verified_digest: str):
    """Cache verified immutable ResNet34 weights by (path, verified_sha256) to avoid per-request reload churn."""
    _ = verified_digest
    import torch
    from torchvision.models import resnet34

    checkpoint = torch.load(Path(resolved_path), map_location="cpu", weights_only=True)
    if checkpoint["metadata"]["architecture"] != "resnet34" or checkpoint["metadata"]["task"] != "hrf_dr_vs_healthy":
        raise ValueError("Unsupported CNN checkpoint task or architecture.")
    model = resnet34(weights=None)
    model.fc = torch.nn.Linear(model.fc.in_features, 1)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    model.eval()
    return model, checkpoint["metadata"]


def gradient_cam(model, tensor):
    import torch

    activations = []
    hook = model.layer4.register_forward_hook(lambda _m, _i, output: activations.append(output))
    try:
        with torch.enable_grad():
            logits = model(tensor.requires_grad_(True))
            gradients = torch.autograd.grad(logits[0, 0], activations[0])[0]
            cam = (gradients.mean(dim=(2, 3), keepdim=True) * activations[0]).sum(dim=1).relu()[0]
            cam = cam / cam.max().clamp_min(1e-12)
        return float(torch.sigmoid(logits[0, 0]).detach()), cam.detach().cpu().numpy()
    finally:
        hook.remove()


def screen_dr_cnn(rgb: np.ndarray, checkpoint_path: str, expected_sha256: str):
    from torchvision.models import ResNet34_Weights

    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.dtype != np.uint8:
        raise ValueError("Expected uint8 RGB image.")
    path = Path(checkpoint_path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != expected_sha256:
        raise ValueError("CNN checkpoint SHA-256 does not match the configured artifact.")
    model, metadata = _load_verified_dr_model(str(path.resolve()), digest)
    tensor = ResNet34_Weights.IMAGENET1K_V1.transforms()(Image.fromarray(rgb)).unsqueeze(0)
    probability, heatmap = gradient_cam(model, tensor)
    if not np.isfinite(probability) or not np.isfinite(heatmap).all():
        raise ValueError("CNN produced a nonfinite prediction or gradient map.")
    # The pretrained transform center-crops 224 pixels after resizing the shorter edge to 256.
    # Keep the overlay in that same crop; stretching it over the whole retina mislocates evidence.
    crop = tensor[0].detach().permute(1, 2, 0).numpy()
    crop = np.clip((crop * [0.229, 0.224, 0.225] + [0.485, 0.456, 0.406]) * 255, 0, 255).astype(np.uint8)
    heatmap = np.asarray(Image.fromarray(heatmap).resize((224, 224), Image.Resampling.BILINEAR))
    return {
        "status": "RESEARCH_ONLY", "risk_available": False,
        "task": "hrf_dr_vs_healthy", "probability_dr": probability,
        "model_version": f"resnet34-{digest[:16]}", "checkpoint_sha256": digest,
        "model_metadata": metadata,
        "explainability": {"method": "GRAD_CAM", "target": "DR_LOGIT",
                           "geometry": "IMAGENET_CENTER_CROP_224", "heatmap": heatmap.tolist(),
                           "overlay_base64_png": render_saliency_overlay_b64(crop, heatmap)},
        "limitations": "Experimental HRF binary research model; not referable-DR grading, ICDR, glaucoma classification, or a clinical diagnosis.",
    }
