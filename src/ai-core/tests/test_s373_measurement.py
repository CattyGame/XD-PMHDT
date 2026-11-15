import hashlib
import json

import numpy as np
import pytest

from app.services.avr_geometry import estimate_optic_disc, trace_root_calibers
from app.services.avr_model import load_avr_model
from app.services.avr_rrwnet import enhance_rrwnet, verify_rrwnet_artifact
from scripts.avr_mask_reference import mask_cross_section, compare_mask_reference


def test_native_caliper_rejects_pixel_noise_without_changing_known_width():
    from app.services.avr_geometry import native_edge_diameter
    green = np.full((240, 240), 0.70, np.float32)
    green[:, 112:128] = 0.60
    green += np.random.default_rng(20261111).normal(0, 0.06, green.shape).astype(np.float32)
    widths = [native_edge_diameter(green, float(y), 119.5, np.array([0., 1.]), 8.)
              for y in range(20, 220)]
    assert all(width is not None for width in widths)
    assert np.mean(np.abs(np.asarray(widths) - 16)) < 1.0


def test_native_caliper_requires_support_for_the_whole_tangential_kernel():
    from app.services.avr_geometry import native_edge_diameter
    green = np.full((100, 100), 0.70, np.float32)
    green[:, 42:58] = 0.30
    assert native_edge_diameter(green, 1., 49.5, np.array([0., 1.]), 8.) is None


def test_disc_localization_does_not_prefer_black_fov_border():
    yy, xx = np.ogrid[:400, :520]
    image = np.zeros((400, 520, 3), np.uint8)
    image[(xx - 260)**2 + (yy - 200)**2 <= 175**2] = 120
    image[(xx - 160)**2 + (yy - 240)**2 <= 25**2] = 160
    proposal = estimate_optic_disc(image)
    assert proposal is not None
    assert np.hypot(proposal['center_x'] - 160, proposal['center_y'] - 240) < 5
    assert 20 <= proposal['radius_px'] <= 31


def test_short_valid_root_branch_has_enough_cross_sections():
    mask = np.zeros((260, 260), bool)
    mask[128:133, 185:202] = True
    image = np.full((260, 260, 3), 180, np.uint8)
    image[mask] = 60
    result = trace_root_calibers(image, mask, np.ones(mask.shape), dict(center_x=130, center_y=130, radius_px=30))
    assert len(result['arteries']) == 1
    vessel = result['arteries'][0]
    assert vessel['samples'] >= 3
    assert abs(vessel['diameter_px'] - 5) <= 0.75
    assert len(vessel['cross_sections']) == vessel['samples']


def test_native_tracing_normalizes_pil_boolean_storage():
    from PIL import Image
    mask = np.zeros((260, 260), bool)
    mask[128:133, 185:202] = True
    pil_mask = np.asarray(Image.fromarray(mask))
    assert not pil_mask.flags.writeable
    image = np.full((260,260,3),180,np.uint8);image[mask]=60
    result = trace_root_calibers(image,pil_mask,np.ones(mask.shape),dict(center_x=130,center_y=130,radius_px=30))
    assert len(result['arteries']) == 1
    assert result['arteries'][0]['diameter_px'] == pytest.approx(5,abs=.75)


@pytest.mark.parametrize('shape', [(300, 700), (700, 300)])
def test_rrwnet_preprocessing_bounds_both_axes_and_keeps_fov_black(shape):
    yy, xx = np.indices(shape)
    image = np.zeros((*shape, 3), np.uint8)
    radius = min(shape) * 0.45
    fov = (yy - shape[0] / 2)**2 + (xx - shape[1] / 2)**2 < radius**2
    image[fov] = np.stack((80 + xx[fov] % 70, 60 + yy[fov] % 60, np.full(fov.sum(), 40)), axis=1)
    enhanced, predicted_fov = enhance_rrwnet(image)
    assert max(enhanced.shape[:2]) <= 584
    assert np.isfinite(enhanced).all() and enhanced.min() >= 0 and enhanced.max() <= 1
    assert np.count_nonzero(enhanced[~predicted_fov]) == 0


def test_rrwnet_preprocessing_rejects_non_rgb_or_unusable_contrast():
    with pytest.raises(ValueError):
        enhance_rrwnet(np.zeros((40, 40), np.uint8))
    with pytest.raises(ValueError):
        enhance_rrwnet(np.zeros((40, 40, 3), np.uint8))


def test_rrwnet_does_not_accept_unpinned_or_commercially_mislabelled_weights(tmp_path):
    model = dict(model_type='RRWNET_RITE', dataset_id='AVRDB', research_only=True, weights_license='MIT')
    path = tmp_path / 'descriptor.json'
    path.write_text(json.dumps(model), encoding='utf-8')
    with pytest.raises(ValueError, match='provenance/license'):
        load_avr_model(str(path), hashlib.sha256(path.read_bytes()).hexdigest())


@pytest.mark.parametrize('width', [4, 8, 16])
def test_mask_reference_measures_known_width_without_using_rgb_algorithm(width):
    labels = np.zeros((100, 100), np.uint8)
    labels[:, 50 - width // 2:50 + width // 2] = 1
    section = dict(center_y=50., center_x=49.5, normal=[0., 1.], diameter_px=float(width))
    diameter, reason = mask_cross_section(labels, section, 1)
    assert reason == 'MATCHED' and abs(diameter - width) < 0.01
    assert mask_cross_section(labels, section, 2)[1] == 'REFERENCE_CLASS_OR_LOCATION_MISMATCH'
    report = compare_mask_reference(labels, {'arteries': [dict(branch_id=1, cross_sections=[section] * 3)]})
    assert report['matched_cross_section_diameter_mae_px'] == pytest.approx(0)
    assert report['conditional_reference_avr'] is None and report['independent_expert_calipers'] is False


@pytest.mark.parametrize('width', [96, 192])
def test_mask_reference_finds_wide_vessels_without_a_fixed_scan_cap(width):
    labels = np.zeros((200, 300), np.uint8)
    labels[:, 150 - width // 2:150 + width // 2] = 1
    section = dict(center_y=100., center_x=149.5, normal=[0., 1.])
    diameter, reason = mask_cross_section(labels, section, 1)
    assert reason == 'MATCHED' and diameter == pytest.approx(width, abs=.01)


def test_mask_reference_near_border_only_requires_the_actual_vessel_edges():
    labels = np.zeros((100, 100), np.uint8)
    labels[:, 2:8] = 1
    diameter, reason = mask_cross_section(labels, dict(center_y=50., center_x=4.5, normal=[0., 1.]), 1)
    assert reason == 'MATCHED' and diameter == pytest.approx(6, abs=.01)
    labels[:, :8] = 1
    assert mask_cross_section(labels, dict(center_y=50., center_x=4.5, normal=[0., 1.]), 1)[1] == 'IMAGE_BOUNDARY'


def test_mask_reference_rejects_ambiguous_annotation_and_invalid_normal():
    labels = np.full((100, 100), 255, np.uint8)
    section = dict(center_y=50., center_x=50., normal=[0., 1.])
    assert mask_cross_section(labels, section, 1)[1] == 'AMBIGUOUS_REFERENCE'
    with pytest.raises(ValueError, match='unit'):
        mask_cross_section(labels, {**section, 'normal': [0., 0.]}, 1)


def test_mask_reference_does_not_treat_unknown_annotation_as_a_vessel_wall():
    labels = np.zeros((100, 100), np.uint8)
    labels[:, 45:55] = 1
    labels[:, 55:58] = 255
    assert mask_cross_section(labels, dict(center_y=50., center_x=49.5, normal=[0., 1.]), 1)[1] == 'AMBIGUOUS_REFERENCE'


def test_mask_reference_rejects_a_zero_width_tangent_contact():
    labels = np.zeros((100, 100), np.uint8)
    labels[50, 50] = 1
    diameter, reason = mask_cross_section(labels, dict(center_y=50., center_x=49.5, normal=[1., 0.]), 1)
    assert diameter is None and reason == 'NO_TWO_REFERENCE_EDGES'


@pytest.mark.parametrize('artery_width,vein_width', [(4., 8.), (6., 16.)])
def test_mask_bias_separates_common_scaling_from_arterial_error(artery_width, vein_width):
    labels = np.zeros((100, 100), np.uint8)
    labels[10:30, 46:54] = 1
    labels[60:80, 42:58] = 2
    measurement = {}
    for name, y, diameter in [('arteries', 20., artery_width), ('veins', 70., vein_width)]:
        section = dict(center_y=y, center_x=49.5, normal=[0., 1.], diameter_px=diameter)
        measurement[name] = [dict(branch_id=i, cross_sections=[section] * 3) for i in range(6)]
    report = compare_mask_reference(labels, measurement)
    assert report['per_class']['arteries']['mean_relative_branch_error'] == pytest.approx(artery_width / 8 - 1)
    assert report['per_class']['veins']['mean_relative_branch_error'] == pytest.approx(vein_width / 16 - 1)
    effect = report['conditional_equivalents']
    assert effect['total_avr_difference'] == pytest.approx(effect['arterial_effect'] + effect['venous_effect'] + effect['interaction_effect'])
    if artery_width == 4:
        assert effect['total_avr_difference'] == pytest.approx(0, abs=1e-4)
    else:
        assert effect['arterial_effect'] < 0 and effect['venous_effect'] == 0


def test_mask_bias_keeps_unmatched_branches_in_coverage():
    labels = np.zeros((100, 100), np.uint8)
    labels[:, 46:54] = 1
    good = dict(center_y=50., center_x=49.5, normal=[0., 1.], diameter_px=6.)
    bad = {**good, 'center_x': 20.}
    report = compare_mask_reference(labels, {'arteries': [dict(branch_id=1, cross_sections=[good] * 3),
                                                         dict(branch_id=2, cross_sections=[bad] * 3)]})
    stats = report['per_class']['arteries']
    assert stats['selected_branches'] == 2 and stats['matched_branches'] == 1 and stats['unmatched_branches'] == 1
    assert stats['selected_sections'] == 6 and stats['matched_sections'] == 3
    assert report['conditional_equivalents'] is None


def test_mask_bias_summary_uses_equal_image_weights_and_reports_missing_cases():
    from scripts.avr_mask_reference import summarize_mask_bias
    labels = np.zeros((100, 100), np.uint8)
    labels[:, 46:54] = 1
    section = dict(center_y=50., center_x=49.5, normal=[0., 1.], diameter_px=8.)
    many = compare_mask_reference(labels, {'arteries': [dict(branch_id=i, cross_sections=[section] * 3) for i in range(6)]})
    single = compare_mask_reference(labels, {'arteries': [dict(branch_id=1, cross_sections=[{**section, 'diameter_px': 6.}] * 3)]})
    missing = compare_mask_reference(labels, {})
    summary = summarize_mask_bias([many, single, missing])
    artery = summary['per_class']['arteries']
    assert artery['total_cases'] == 3 and artery['cases_with_matches'] == 2
    assert artery['selected_branches'] == artery['matched_branches'] == 7
    assert artery['case_macro_mean_signed_branch_error_px'] == pytest.approx(-1)
    assert summary['per_class']['veins']['cases_with_matches'] == 0
    assert summary['conditional_equivalents']['cases_with_six_matched_each'] == 0


def test_artifact_download_checks_hash_size_and_preserves_existing_files(tmp_path, monkeypatch):
    import io
    from scripts.fetch_avr_rrwnet import verified_download
    monkeypatch.setattr('urllib.request.urlopen', lambda *args, **kwargs: io.BytesIO(b'weights'))
    digest = hashlib.sha256(b'weights').hexdigest()
    target = tmp_path / 'model.safetensors'
    verified_download('https://publisher.invalid/artifact', target, digest, 7)
    assert target.read_bytes() == b'weights'
    verified_download('https://publisher.invalid/artifact', target, digest, 7)
    with pytest.raises(ValueError, match='overwrite'):
        verified_download('https://publisher.invalid/artifact', target, '0' * 64, 7)
    with pytest.raises(ValueError, match='SHA-256'):
        verified_download('https://publisher.invalid/artifact', tmp_path / 'bad', '0' * 64, 7)
    with pytest.raises(ValueError, match='budget'):
        verified_download('https://publisher.invalid/artifact', tmp_path / 'too-big', digest, 6)
    assert list(tmp_path.iterdir()) == [target]


def test_crossing_continuations_are_not_counted_as_extra_vessels():
    artery = np.zeros((360, 360), bool)
    vein = np.zeros_like(artery)
    artery[178:183, 240:321] = True
    vein[130:231, 278:283] = True
    mask = artery | vein
    image = np.full((360, 360, 3), 180, np.uint8)
    image[mask] = 60
    result = trace_root_calibers(image, mask, artery.astype(np.float32),
                                 dict(center_x=180, center_y=180, radius_px=40))
    assert len(result['arteries']) == len(result['veins']) == 1
    assert len(result['veins'][0]['source_branch_ids']) == 2


def test_caliper_samples_stay_outside_the_physical_crossing_core(monkeypatch):
    artery=np.zeros((360,360),bool);vein=np.zeros_like(artery)
    artery[178:183,240:321]=True;vein[130:231,275:286]=True
    sampled=[]
    def measure(green,y,x,normal,radius):sampled.append((y,x));return 5.
    monkeypatch.setattr('app.services.avr_geometry.native_edge_diameter',measure)
    result=trace_root_calibers(np.full((360,360,3),180,np.uint8),artery|vein,artery.astype(np.float32),
                              dict(center_x=180,center_y=180,radius_px=40))
    assert sampled and result['arteries'] and result['veins']
    assert not any(178<=y<=182 and 275<=x<=285 for y,x in sampled)
    assert any(b['excluded_junction_samples'] for role in ('arteries','veins') for b in result[role])


def test_native_tangent_uses_enough_support_to_avoid_coarse_mask_staircase_aliasing(monkeypatch):
    from PIL import Image
    yy,xx=np.indices((600,600));angle=np.deg2rad(20)
    longitudinal=(xx-300)*np.cos(angle)+(yy-300)*np.sin(angle)
    transverse=-(xx-300)*np.sin(angle)+(yy-300)*np.cos(angle)
    truth=(np.abs(transverse)<10)&(longitudinal>70)&(longitudinal<210)
    mask=np.array(Image.fromarray(truth).resize((100,100),Image.Resampling.NEAREST).resize((600,600),Image.Resampling.NEAREST))
    normals=[]
    def measure(green,y,x,normal,radius):normals.append(normal);return 20.
    monkeypatch.setattr('app.services.avr_geometry.native_edge_diameter',measure)
    trace_root_calibers(np.full((600,600,3),180,np.uint8),mask,np.ones(mask.shape),dict(center_x=300,center_y=300,radius_px=60))
    exact=np.array([-np.cos(angle),np.sin(angle)])
    errors=np.degrees(np.arccos(np.clip(np.abs(np.asarray(normals)@exact),0,1)))
    assert len(normals)>=3 and np.percentile(errors,95)<=8.


@pytest.mark.parametrize('junction_x,expected', [(205, 1), (180, 2)])
def test_bifurcation_inside_zone_is_one_vessel_but_parent_outside_zone_allows_daughters(junction_x, expected):
    from scipy.ndimage import binary_dilation
    mask = np.zeros((260, 260), bool)
    mask[130, 150:junction_x + 1] = True
    for offset in range(0, 50):
        x = junction_x + offset
        if x < 260:
            mask[130 - offset, x] = mask[130 + offset, x] = True
    mask = binary_dilation(mask, iterations=2)
    image = np.full((260, 260, 3), 180, np.uint8)
    image[mask] = 60
    result = trace_root_calibers(image, mask, np.ones(mask.shape), dict(center_x=130, center_y=130, radius_px=30))
    assert len(result['arteries']) == expected


def test_pinned_od_model_uses_first_of_four_channels_and_native_geometry():
    from app.services.avr_optic_disc import optic_disc_geometry
    yy, xx = np.ogrid[:512, :512]
    logits = np.full((1, 4, 512, 512), -5., np.float32)
    logits[0, 0][(xx - 170)**2 + (yy - 280)**2 <= 25**2] = 5
    logits[0, 1] = 5
    disc = optic_disc_geometry((1000, 1504), logits)
    assert disc is not None and disc['expert_landmark_validated'] is False
    assert abs(disc['center_x'] - 170 * 1504 / 512) < 5
    assert abs(disc['center_y'] - 280 * 1000 / 512) < 5
    assert 70 < disc['radius_px'] < 78
    assert optic_disc_geometry((1000, 1504), np.full_like(logits, -1)) is None
    assert optic_disc_geometry((1000, 1504), np.ones_like(logits)) is None
    with pytest.raises(ValueError, match='output'):
        optic_disc_geometry((1000, 1504), logits[:, :1])


def test_avr_exports_separate_native_artery_vein_masks_even_when_measurement_unavailable():
    import io
    import base64
    from unittest.mock import patch
    from PIL import Image
    from app.services.avr_service import calculate_automatic_avr
    image = np.zeros((40, 60, 3), np.uint8)
    labels = np.zeros((40, 60), np.uint8)
    labels[5:9, 8:30] = 1
    labels[20:27, 8:30] = 2
    with patch('app.services.avr_service.analyze_avr_image', return_value=({'avr': None}, labels, None)):
        result = calculate_automatic_avr(image, {}, generate_visualization=False, include_masks=True)
    for name, value in (('artery', 1), ('vein', 2)):
        with Image.open(io.BytesIO(base64.b64decode(result['segmentation'][name + '_mask_base64_png']))) as mask:
            assert mask.mode == 'L' and mask.size == (60, 40)
            np.testing.assert_array_equal(np.asarray(mask), (labels == value).astype(np.uint8) * 255)
    assert result['avr'] is None


def test_legacy_helper_cannot_silently_fallback_to_unverified_heuristics():
    from app.services.avr_service import segment_arteries_veins
    with pytest.raises(ValueError, match='verified artifact'):
        segment_arteries_veins(np.zeros((40, 60, 3), np.uint8))


def test_benchmark_rejects_wrong_cohort_before_any_inference(tmp_path, monkeypatch):
    from scripts import benchmark_avrdb
    manifest = tmp_path / 'wrong.csv'
    manifest.write_text('case_id,group_id,dataset_id,dataset_doi\n', encoding='utf-8')
    monkeypatch.setattr(benchmark_avrdb, 'load_avr_model', lambda *args: dict(
        model_type='PIXEL_MLP_RGB_DOG', manifest_sha256=hashlib.sha256(manifest.read_bytes()).hexdigest()))
    with pytest.raises(ValueError, match='publisher AVRDB100'):
        benchmark_avrdb.evaluate(manifest, tmp_path / 'model', 'a' * 64, tmp_path / 'report.json')
    assert not (tmp_path / 'report.json').exists()


def test_roi_scores_remain_separate_from_whole_image_and_count_misses():
    from scripts.benchmark_avrdb import score_segmentation
    reference = np.array([[1, 2], [1, 255]], np.uint8)
    predicted = np.array([[1, 0], [2, 2]], np.uint8)
    probability = np.array([[0.9, 0.1], [0.1, 0.1]])
    whole = score_segmentation(predicted, probability, reference)
    roi = score_segmentation(predicted, probability, reference, np.array([[True, True], [False, False]]))
    assert whole['reference_vessel_pixels'] == 3 and whole['av_accuracy_on_reference_support'] == pytest.approx(1 / 3)
    assert roi['av_accuracy_on_reference_support'] == 0.5 and roi['conditional_av_accuracy'] == 1
    assert roi['vessel_dice'] == pytest.approx(2 / 3)
