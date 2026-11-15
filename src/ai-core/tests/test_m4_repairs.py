import base64
import io
from pathlib import Path
from unittest.mock import patch
from unittest.mock import Mock

import numpy as np
from PIL import Image
import pytest

from app.schemas.analysis import AnalysisRequest
from app.services.avr_service import calculate_avr, measure_distinct_vessel_calibers, compute_knudtson_crae
from app.services.frangi_service import FrangiConfigError, FrangiRuntimeConfig, load_frangi_config, process_frangi_analysis, fast_vectorized_frangi_2d
from app.services.image_preprocessor import PreprocessorConfig, process_hrf_frangi_vessel_segmentation
from tests.test_frangi_sprint3 import make_synthetic_fundus_png_b64


def test_six_distinct_widths_not_pixel_percentiles():
    mask = np.zeros((180, 130), dtype=bool)
    for y, width in zip(range(10, 170, 25), [4, 6, 8, 10, 12, 14]):
        mask[y:y + width, 10:120] = True
    widths = measure_distinct_vessel_calibers(mask, np.ones_like(mask))
    assert widths == pytest.approx([14, 12, 10, 8, 6, 4], abs=1)
    assert len(measure_distinct_vessel_calibers(mask[10:30], np.ones_like(mask[10:30]))) == 1


def test_knudtson_pairs_in_rounds():
    # Six equal calibers -> three .88*sqrt(2), then pair extremes and carry the middle.
    assert compute_knudtson_crae([10] * 6) == pytest.approx(0.88 * np.hypot(0.88 * np.hypot(0.88 * np.hypot(10, 10), 0.88 * np.hypot(10, 10)), 0.88 * np.hypot(10, 10)), abs=0.001)


def test_avr_abstains_without_validated_landmarks_and_rejects_bad_masks():
    rgb = np.zeros((80, 100, 3), dtype=np.uint8)
    result = calculate_avr(rgb)
    assert result['measurement_available'] is False and result['avr'] is None
    assert result['risk_available'] is False
    with pytest.raises(ValueError, match='dimensions'):
        calculate_avr(rgb, artery_gt_mask=np.zeros((2, 2)))


def test_reference_avr_measures_six_vessels_of_each_type_without_clinical_risk():
    rgb = np.zeros((400, 400, 3), dtype=np.uint8)
    arteries = np.zeros((400, 400), dtype=bool)
    veins = np.zeros_like(arteries)
    for y in range(110, 261, 30):
        arteries[y:y + 6, 290:355] = True
        veins[y + 12:y + 22, 290:355] = True
    result = calculate_avr(rgb, artery_gt_mask=arteries, vein_gt_mask=veins, optic_disc=(200, 200, 50))
    assert result['measurement_available'] is True
    assert len(result['artery_calibers_px']) == len(result['vein_calibers_px']) == 6
    assert result['artery_calibers_px'] == pytest.approx([6] * 6, abs=1)
    assert result['vein_calibers_px'] == pytest.approx([10] * 6, abs=1)
    assert result['avr'] > 0 and result['risk_available'] is False
    assert result['risk_level'] == 'RISK_UNAVAILABLE'


def test_hrf_proxy_is_used_by_runtime_and_preserves_contract_geometry():
    cfg = load_frangi_config(Path(__file__).resolve().parents[1] / 'config/ai-frangi-hrf.yaml')
    request = AnalysisRequest(request_id='5d2a9a13-2870-4622-88d3-349247bff843', image_base64=make_synthetic_fundus_png_b64())
    with patch('app.services.image_preprocessor.process_hrf_frangi_vessel_segmentation', wraps=process_hrf_frangi_vessel_segmentation) as pipeline:
        result = process_frangi_analysis(request, cfg)
    pipeline.assert_called_once()
    mask = Image.open(io.BytesIO(base64.b64decode(result.segmentation.mask_base64)))
    assert mask.size == (result.image_info.width, result.image_info.height)
    assert result.metrics.vessel_density == round(np.count_nonzero(np.asarray(mask)) / (mask.width * mask.height), 6)
    assert not result.risk_available and result.metrics.av_ratio is None
    assert result.config_version == 's5-hrf-opencv-clahe-1280-v1'


def test_hrf_cannot_reuse_unvalidated_heuristic_risk():
    with pytest.raises(FrangiConfigError):
        FrangiRuntimeConfig(preprocessing='hrf-clahe-proxy-v1', risk_available=True)


def test_hrf_rejects_mismatched_truth_and_bounds_portrait_proxy():
    rgb = np.zeros((250, 50, 3), dtype=np.uint8)
    with pytest.raises(ValueError):
        process_hrf_frangi_vessel_segmentation(rgb, ground_truth_mask=np.zeros((50, 50)))
    result = process_hrf_frangi_vessel_segmentation(rgb, PreprocessorConfig(max_processing_width=64))
    assert max(result['metrics']['processed_resolution']) <= 64
    assert result['mask'].shape == rgb.shape[:2]


def test_scoring_does_not_hide_missing_vessels_outside_estimated_fov():
    rgb = np.zeros((64, 64, 3), dtype=np.uint8)
    truth = np.zeros((64, 64), dtype=bool)
    truth[0, 0] = True
    result = process_hrf_frangi_vessel_segmentation(rgb, ground_truth_mask=truth, ground_truth_fov=np.ones_like(truth))
    assert result['metrics']['evaluation_fov_source'] == 'REFERENCE_MASK'
    assert result['metrics']['ground_truth_vessel_pixel_count'] == 1
    assert result['metrics']['dice'] == 0


def test_mask_normalization_does_not_assign_another_case(tmp_path):
    from scripts.prepare_inspire_avr import resolve_and_normalize_mask
    (tmp_path / 'IM000168--veins.jpg').write_bytes(b'wrong case')
    assert resolve_and_normalize_mask(tmp_path, 'IM000001', ['*--veins.*'], '--veins.jpg') is None
    assert not (tmp_path / 'IM000001--veins.jpg').exists()
    (tmp_path / 'IM000001--veins.jpg').write_bytes(b'previously renamed without proof')
    assert resolve_and_normalize_mask(tmp_path, 'IM000001', ['*--veins.*'], '--veins.jpg') is None


def test_grayscale_reference_masks_use_dark_vessels_on_white_background(tmp_path):
    from scripts.benchmark_inspire_avr import load_mask
    image = np.full((20, 20), 255, dtype=np.uint8)
    image[5:15, 10] = 0
    path = tmp_path / 'mask.png'
    Image.fromarray(image).save(path)
    assert np.array_equal(load_mask(str(path)), image == 0)


def test_windows_benchmark_stops_its_venv_child_process_tree():
    from scripts import stress_test_ai_core as runner
    process = Mock(pid=123)
    process.poll.return_value = None
    with patch.object(runner.sys, 'platform', 'win32'), patch.object(runner.subprocess, 'run') as run:
        runner.stop_server_process(process)
    assert run.call_args.args[0] == ['taskkill', '/PID', '123', '/T', '/F']
    process.wait.assert_called_once_with(timeout=5)


def test_hrf_float32_avoids_small_response_cancellation():
    image = np.ones((80, 100), dtype=np.float32)
    image[:, 48:52] = 0.7
    single = fast_vectorized_frangi_2d(image, sigmas=(1, 2, 3), gamma=15, dtype=np.float32)
    double = fast_vectorized_frangi_2d(image, sigmas=(1, 2, 3), gamma=15)
    assert single.dtype == np.float32 and double.dtype == np.float64
    assert single.max() > 0 and np.isfinite(single).all()
    assert np.allclose(single / single.max(), double / double.max(), atol=0.001)
