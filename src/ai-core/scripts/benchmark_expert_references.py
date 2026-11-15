"""Frozen research evaluation against Iowa AVR observers or HRF optic-disc observers."""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import json
import math
from pathlib import Path
import platform
import re
import sys
import time

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'src/ai-core'))
from app.services.avr_model import load_avr_model
from app.services.avr_optic_disc import locate_optic_disc
from app.services.avr_service import analyze_avr_image
from app.services.frangi_service import evaluate_frangi_quality, load_frangi_config


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def agreement(predicted, reference):
    p, r = np.asarray(predicted, dtype=float), np.asarray(reference, dtype=float)
    if p.ndim != 1 or r.shape != p.shape or not np.isfinite(p).all() or not np.isfinite(r).all():
        raise ValueError('Expected paired finite one-dimensional measurements.')
    if not len(p):
        return dict(pairs=0, mae=None, bias=None, rmse=None, median_absolute_error=None,
                    difference_sd=None, limits_of_agreement_95=None)
    error = p - r
    bias = float(np.mean(error))
    sd = float(np.std(error, ddof=1)) if len(p) > 1 else None
    return dict(pairs=len(p), mae=float(np.mean(np.abs(error))), bias=bias,
                rmse=float(np.sqrt(np.mean(error**2))), median_absolute_error=float(np.median(np.abs(error))),
                difference_sd=sd, limits_of_agreement_95=[bias - 1.96 * sd, bias + 1.96 * sd] if sd is not None else None)


def load_inspire_reference(root):
    root = Path(root)
    reference = root / 'GT/GT.txt'
    lines = reference.read_text(encoding='utf-8-sig').splitlines()
    if not lines or lines[0].split() != ['AVR', 'obs', '1', 'AVR', 'obs', '2']:
        raise ValueError('Expected the Iowa two-observer AVR reference header.')
    rows = {}
    for line in lines[1:]:
        fields = line.split()
        if not fields:
            continue
        if len(fields) != 3 or not re.fullmatch(r'image([1-9]|[1-3][0-9]|40)\.jpg', fields[0]):
            raise ValueError('Invalid Iowa case/reference row.')
        name = fields[0]
        values = [float(x) for x in fields[1:]]
        if name in rows or any(not math.isfinite(x) or x <= 0 for x in values):
            raise ValueError('Duplicate case or invalid AVR reference.')
        rows[name] = dict(case_id=Path(name).stem, image=root / 'org' / name,
                          observer_1=values[0], observer_2=values[1], mean_avr=sum(values) / 2)
    expected = {f'image{i}.jpg' for i in range(1, 41)}
    images = {p.name for p in (root / 'org').glob('*.jpg') if not p.name.startswith('._')}
    if set(rows) != expected or images != expected:
        raise ValueError('Expected all 40 Iowa photos and exactly matching observer references.')
    return [rows[f'image{i}.jpg'] for i in range(1, 41)], reference


def hrf_case_id(value):
    match = re.fullmatch(r'(0[1-9]|1[0-5])_?(dr|g|h)', str(value))
    if match is None:
        raise ValueError('Invalid HRF observer case ID.')
    return match[1] + '_' + match[2]


def load_hrf_reference(root, reference):
    import xlrd

    root, reference = Path(root), Path(reference)
    book = xlrd.open_workbook(reference)
    if book.sheet_names() != ['Expert A', 'Expert B']:
        raise ValueError('Expected the two original HRF observer sheets.')
    header = ['image', 'Pap. Center x', 'Pap. Center y', 'vessel orig. x', 'vessel orig. y', 'disk diameter']
    expected = {f'{i:02d}_{group}' for i in range(1, 16) for group in ('dr', 'g', 'h')}
    experts = {}
    for sheet in book.sheets():
        if sheet.nrows != 46 or sheet.ncols != 6 or sheet.row_values(0) != header:
            raise ValueError('Unexpected HRF observer table dimensions/header.')
        entries = {}
        for i in range(1, sheet.nrows):
            name, x, y, _, _, diameter = sheet.row_values(i)
            name = hrf_case_id(name)
            if name in entries or any(isinstance(v, bool) or not isinstance(v, (float, int))
                                      or not math.isfinite(v) for v in (x, y, diameter)):
                raise ValueError('Duplicate HRF case or nonfinite geometry.')
            if not 0 <= x < 3504 or not 0 <= y < 2336 or not 0 < diameter < 2336:
                raise ValueError('HRF reference geometry outside native image.')
            entries[name] = dict(center_x=float(x), center_y=float(y), radius_px=float(diameter) / 2)
        if set(entries) != expected:
            raise ValueError('Missing HRF observer cases.')
        experts[sheet.name] = entries
    images = {p.stem: p for p in (root / 'images').iterdir() if p.suffix.lower() == '.jpg'}
    if set(images) != expected:
        raise ValueError('Expected all 45 HRF images.')
    rows = []
    for name in sorted(expected):
        a, b = (experts[observer][name] for observer in ('Expert A', 'Expert B'))
        mean = {field: (a[field] + b[field]) / 2 for field in a}
        distance = math.hypot(a['center_x'] - b['center_x'], a['center_y'] - b['center_y'])
        rows.append(dict(case_id=name, image=images[name], expert_a=a, expert_b=b, mean_geometry=mean,
                         observer_center_distance_px=distance,
                         reference_flags=['OBSERVER_CENTER_DISAGREEMENT_GT_MEAN_RADIUS'] if distance > mean['radius_px'] else []))
    return rows, reference


def summarize_avr(records):
    measured = [r for r in records if r['measurement_available']]
    predicted = [r['predicted_avr'] for r in measured]
    return dict(total_cases=len(records), quality_accepted=sum(r['quality_accepted'] for r in records),
                measurements=len(measured), coverage=len(measured) / len(records),
                error_sign='prediction minus reference; errors conditional on available measurements',
                versus_mean_observers=agreement(predicted, [r['reference']['mean_avr'] for r in measured]),
                versus_observer_1=agreement(predicted, [r['reference']['observer_1'] for r in measured]),
                versus_observer_2=agreement(predicted, [r['reference']['observer_2'] for r in measured]),
                interobserver_all_cases=agreement([r['reference']['observer_1'] for r in records],
                                                [r['reference']['observer_2'] for r in records]))


def summarize_od(records):
    found = [r for r in records if r['prediction'] is not None]
    summaries = {}
    for observer in ('expert_a', 'expert_b', 'mean_geometry'):
        distances = [math.hypot(r['prediction']['center_x'] - r['reference'][observer]['center_x'],
                                r['prediction']['center_y'] - r['reference'][observer]['center_y']) for r in found]
        summaries[observer] = dict(cases=len(found),
            center_error_px=dict(mean=float(np.mean(distances)), median=float(np.median(distances)),
                                 p95=float(np.percentile(distances, 95)), maximum=max(distances)) if distances else None,
            radius_error_px=agreement([r['prediction']['radius_px'] for r in found],
                                      [r['reference'][observer]['radius_px'] for r in found]))
    return dict(total_cases=len(records), localized_cases=len(found), quality_accepted=sum(r['quality_accepted'] for r in records),
                coverage=len(found) / len(records), versus_observers=summaries,
                reference_flagged_cases=[r['case_id'] for r in records if r['reference']['reference_flags']],
                reference_policy='Retain both observers and flagged cases, no correction or exclusion; mean geometry is descriptive.',
                coordinate_origin='Not specified in workbook; coordinates used as supplied, no undocumented one-pixel shift.')


def benchmark(dataset, root, reference_path, model_path, model_sha256, output, prediction_cache=None):
    root, output = Path(root), Path(output)
    if output.exists() or output.with_suffix('.partial.json').exists():
        raise ValueError('Use a new output path; existing evidence is not overwritten.')
    if dataset == 'inspire-avr':
        rows, reference = load_inspire_reference(root)
        expected_size = (2392, 2048)
    else:
        rows, reference = load_hrf_reference(root, reference_path)
        expected_size = (3504, 2336)
    source_paths = [Path(__file__), ROOT / 'src/ai-core/scripts/calibrate_avr_vessel_threshold.py', ROOT / 'src/ai-core/config/ai-frangi-v1.3.yaml',
                    *sorted((ROOT / 'src/ai-core/app/services').rglob('*.py'))]
    source_hashes = {p.relative_to(ROOT).as_posix(): sha256(p) for p in source_paths}
    reference_sha = sha256(reference)
    images_sha = {row['case_id']: sha256(row['image']) for row in rows}
    if len(set(images_sha.values())) != len(rows):
        raise ValueError('Duplicate images in the evaluation inventory.')
    if dataset == 'hrf-od':
        manifest = list(csv.DictReader((ROOT / 'docs/datasets/hrf/manifest.csv').open(encoding='utf-8-sig')))
        expected_hashes = {Path(r['image_path']).stem: r['image_sha256'] for r in manifest}
        if images_sha != expected_hashes:
            raise ValueError('HRF image inventory differs from the frozen project manifest.')
    model = load_avr_model(str(model_path), model_sha256)
    if model['model_type'] != 'RRWNET_RITE':
        raise ValueError('This protocol evaluates the pinned RRWNet/PVBM research pipeline.')
    import torch
    torch.set_num_threads(2)
    config = load_frangi_config(ROOT / 'src/ai-core/config/ai-frangi-v1.3.yaml')
    versions = {name: importlib.metadata.version(name) for name in ('numpy', 'Pillow', 'scipy', 'torch', 'onnxruntime', 'xlrd')}
    report = dict(status='RUNNING', evaluation_dataset=dataset, started_at_utc=datetime.now(timezone.utc).isoformat(),
                  model_sha256=model_sha256, segmentation_weights_sha256=model['weights_sha256'],
                  od_weights_sha256=model['od_weights_sha256'], reference_file_sha256=reference_sha,
                  runtime_source_sha256=source_hashes, environment=dict(python=platform.python_version(),
                  os=platform.platform(), device='CPU', torch_threads=2, library_versions=versions),
                  independent_observer_reference_available=True, clinical_validation=False,
                  clinical_acceptance='NOT_ASSESSED; no prespecified clinical error/coverage acceptance thresholds',
                  holdout_independence='NOT_ESTABLISHED; OD training cohort is not established by artifact metadata',
                  scope='AVR follows API quality admission; HRF OD is component evaluation on all 45 photos, including quality rejects.',
                  timing_scope='Local function time, not HTTP; cached neural inference excluded where cache_hit is true; original neural timing recorded separately.',
                  records=[])
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        for row in rows:
            with Image.open(row['image']) as photo:
                if photo.size != expected_size:
                    raise ValueError('Unexpected native image geometry: ' + row['case_id'])
                rgb = np.asarray(photo.convert('RGB'))
            quality = evaluate_frangi_quality(rgb, config=config)
            record = dict(case_id=row['case_id'], image_sha256=images_sha[row['case_id']],
                          native_size=list(expected_size), quality_accepted=quality.is_acceptable,
                          quality=quality.to_details_dict(), reference={k: v for k, v in row.items() if k != 'image'})
            began = time.perf_counter()
            if dataset == 'inspire-avr':
                if quality.is_acceptable:
                    if prediction_cache is None:
                        prediction, _, _ = analyze_avr_image(rgb, model, generate_visualization=False)
                    else:
                        from scripts.calibrate_avr_vessel_threshold import cached_probabilities
                        from app.services.avr_rrwnet import rrwnet_prediction_arrays
                        from app.services.avr_service import measure_avr_prediction
                        probabilities, record['neural_cache'] = cached_probabilities(rgb, model, prediction_cache)
                        labels, probability, seeds = rrwnet_prediction_arrays(probabilities, (rgb.shape[1], rgb.shape[0]), model['vessel_threshold'])
                        prediction = measure_avr_prediction(rgb, model, locate_optic_disc(rgb, model), labels, probability, measurement_mask=seeds)
                    record.update(measurement_available=prediction['measurement_available'], predicted_avr=prediction['avr'],
                                  crae_px=prediction['crae_px'], crve_px=prediction['crve_px'],
                                  reason=prediction.get('unavailable_reason'), optic_disc=prediction.get('optic_disc'),
                                  measurement=prediction.get('measurement'))
                else:
                    record.update(measurement_available=False, predicted_avr=None, crae_px=None, crve_px=None,
                                  reason='LOW_QUALITY_IMAGE')
            else:
                record['prediction'] = locate_optic_disc(rgb, model)
                record['reason'] = None if record['prediction'] else 'OPTIC_DISC_NOT_LOCALIZED'
            record['seconds'] = time.perf_counter() - began
            report['records'].append(record)
            output.with_suffix('.partial.json').write_bytes(json.dumps(report, indent=2, allow_nan=False).encode())
            print(dataset, row['case_id'], record['reason'] or 'MEASURED', flush=True)
        report['summary'] = summarize_avr(report['records']) if dataset == 'inspire-avr' else summarize_od(report['records'])
        from collections import Counter
        report['outcomes'] = dict(Counter(r['reason'] or 'MEASURED' for r in report['records']))
        report['changed_source_files'] = [p for p, digest in source_hashes.items() if sha256(ROOT / p) != digest]
        if report['changed_source_files'] or sha256(reference) != reference_sha:
            raise ValueError('Source or observer reference changed during evaluation.')
        if any(sha256(row['image']) != images_sha[row['case_id']] for row in rows):
            raise ValueError('Evaluation images changed during inference.')
        load_avr_model(str(model_path), model_sha256)
        report['status'] = 'EVALUATION_COMPLETE_NOT_CLINICAL_CERTIFICATION'
    except Exception as error:
        report.update(status='FAILED', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        report['finished_at_utc'] = datetime.now(timezone.utc).isoformat()
        output.write_bytes((json.dumps(report, indent=2, allow_nan=False) + '\n').encode())
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dataset', choices=('inspire-avr', 'hrf-od'), required=True)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--reference', type=Path, help='Original HRF optic_disk_centers.xls; unused for Iowa GT.txt')
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prediction-cache', type=Path)
    args = parser.parse_args()
    if args.dataset == 'hrf-od' and args.reference is None:
        parser.error('--reference is required for hrf-od')
    result = benchmark(args.dataset, args.root, args.reference, args.model, args.sha256, args.output, args.prediction_cache)
    print(json.dumps(result['summary'], indent=2))
