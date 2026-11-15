"""Legacy white-background mask helpers; active AVRDB evaluation is benchmark_avrdb.py."""
from pathlib import Path
from typing import Optional, Tuple
import numpy as np
from PIL import Image


def load_mask(path_str: str) -> Optional[np.ndarray]:
    if not path_str or not Path(path_str).exists():
        return None
    with Image.open(path_str) as image:
        arr = np.asarray(image)
    return np.mean(arr, axis=-1) < 240 if arr.ndim == 3 else arr < 240


def compute_overlap_metrics(pred: np.ndarray, gt: np.ndarray) -> Tuple[float, float]:
    inter = int(np.count_nonzero(pred & gt))
    p_cnt, g_cnt = int(np.count_nonzero(pred)), int(np.count_nonzero(gt))
    union = p_cnt + g_cnt - inter
    dice = 2.0 * inter / (p_cnt + g_cnt) if p_cnt + g_cnt else 0.0
    iou = inter / union if union else 0.0
    return round(dice, 4), round(iou, 4)


def run_inspire_avr_benchmark(*args, **kwargs):
    raise RuntimeError("Superseded inventory: use benchmark_avrdb.py with the verified AVRDB model and manifest.")


if __name__ == "__main__":
    run_inspire_avr_benchmark()
