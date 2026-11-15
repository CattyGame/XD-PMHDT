import base64
import copy
import io
import math

import numpy as np
from PIL import Image
import pytest

from scripts.accept_avr_container import confusion_matrix, equivalent, verify_response,latency_summary


def test_latency_keeps_unavailable_measurements_and_reports_datasets_separately():
    rows=[dict(dataset='iowa40',http_status=200,http_seconds=80.,quality_accepted=True,measurement_available=False),
          dict(dataset='iowa40',http_status=200,http_seconds=100.,quality_accepted=True,measurement_available=True),
          dict(dataset='avrdb-test',http_status=422,http_seconds=.1,quality_accepted=False,measurement_available=False)]
    report=latency_summary(rows)
    assert report['iowa40']['p95_seconds']==99 and report['iowa40']['gate_met'] is False
    assert report['iowa40']['measurements']==1 and report['iowa40']['quality_accepted']==2
    assert report['avrdb-test']['quality_rejects']==1 and report['avrdb-test']['p95_seconds'] is None


def png(array):
    output = io.BytesIO()
    Image.fromarray(array).save(output, format='PNG')
    return base64.b64encode(output.getvalue()).decode()


def response(measured=True):
    zero = np.zeros((12, 16), np.uint8)
    return dict(status='RESEARCH_ONLY', risk_available=False, model_metadata={'clinical_validation': False},
                measurement_available=measured, risk_level='RISK_UNAVAILABLE',
                segmentation={role + '_mask_base64_png': png(zero) for role in ('artery', 'vein')},
                overlay_base64_png=png(np.zeros((12, 16, 3), np.uint8)),
                measurement={name: [dict(diameter_px=100., samples=3, source_branch_ids=[i]) for i in range(6)]
                             for name in ('arteries', 'veins')},
                crae_px=174.843 if measured else None, crve_px=213.761 if measured else None,
                avr=174.843 / 213.761 if measured else None,
                unavailable_reason=None if measured else 'OPTIC_DISC_NOT_LOCALIZED')


def test_six_caliber_independent_arithmetic_and_no_ratio_clamp():
    # Six equal diameters reduce 6 -> 3 -> 2 -> 1, keeping the middle value in the odd round.
    for coefficient in (.88, .95):
        expected = round(coefficient**2 * 100 * math.sqrt(4 * coefficient**2 + 2), 3)
        assert equivalent([100.] * 6, coefficient) == expected
    body = response()
    assert verify_response(body, (16, 12)).shape == (12, 16)
    body['measurement']['arteries'] = [dict(b, diameter_px=200.) for b in body['measurement']['arteries']]
    body['crae_px'] = equivalent([200.] * 6, .88)
    body['avr'] = body['crae_px'] / body['crve_px']
    assert body['avr'] > 1
    verify_response(body, (16, 12))
    body['avr'] = .85
    with pytest.raises(ValueError, match='without clamping'):
        verify_response(body, (16, 12))


@pytest.mark.parametrize('widths', [[1.] * 5, [1.] * 5 + [float('nan')], [1.] * 5 + [-1], [1.] * 5 + [True]])
def test_arithmetic_rejects_missing_or_invalid_diameters(widths):
    with pytest.raises(ValueError):
        equivalent(widths, .88)


def test_confusion_matrix_keeps_misses_swaps_and_ignores_ambiguous_annotations():
    truth = np.array([[0, 1, 1], [2, 2, 255]], np.uint8)
    predicted = np.array([[1, 1, 0], [1, 2, 2]], np.uint8)
    np.testing.assert_array_equal(confusion_matrix(predicted, truth), [[0, 1, 0], [1, 1, 0], [0, 1, 1]])
    with pytest.raises(ValueError):
        confusion_matrix(predicted[:, :2], truth)
    predicted[0, 0] = 255
    with pytest.raises(ValueError):
        confusion_matrix(predicted, truth)


def test_response_validation_rejects_overlapping_masks_or_wrong_geometry():
    body = response(False)
    body['segmentation'] = {role + '_mask_base64_png': png(np.full((12, 16), 255, np.uint8))
                            for role in ('artery', 'vein')}
    with pytest.raises(ValueError, match='overlap'):
        verify_response(body, (16, 12))
    with pytest.raises(ValueError, match='native-geometry'):
        verify_response(response(False), (32, 24))


def test_unavailable_needs_null_and_reason_but_no_fabricated_overlay():
    body = response(False)
    del body['overlay_base64_png']
    verify_response(body, (16, 12))
    for change in ({'avr': 0}, {'unavailable_reason': None}, {'risk_level': 'LOW'}, {'risk_available': True}):
        with pytest.raises(ValueError):
            verify_response({**body, **change}, (16, 12))


def test_duplicate_sources_do_not_count_as_six_distinct_vessels():
    body = response()
    duplicate = copy.deepcopy(body)
    duplicate['measurement']['arteries'][1]['source_branch_ids'] = [0]
    with pytest.raises(ValueError, match='Duplicate vessel'):
        verify_response(duplicate, (16, 12))
