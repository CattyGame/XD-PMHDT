"""Evaluate frozen AVRDB research inference; reference labels never enter the runtime call."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import inspect
import importlib.metadata
import os
import platform
from datetime import datetime, timezone
from pathlib import Path
import sys
import time

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'src/ai-core'))
from app.services.avr_model import load_avr_model, segment_with_model
from app.services.avr_service import measure_avr_prediction
from app.services.avr_optic_disc import locate_optic_disc
from scripts.avr_mask_reference import compare_mask_reference, summarize_mask_bias
from app.services.frangi_service import load_frangi_config, evaluate_frangi_quality


def score_segmentation(predicted, probability, reference, region=None):
    eligible = reference != 255
    if region is not None:
        eligible &= region
    truth = (reference > 0) & eligible
    vessel = (predicted > 0) & eligible
    support = int(truth.sum())
    return dict(reference_vessel_pixels=support,
                vessel_dice=float(2 * np.count_nonzero(truth & vessel) / max(1, support + vessel.sum())),
                av_accuracy_on_reference_support=float(np.count_nonzero((predicted == reference) & truth) / max(1, support)),
                conditional_av_accuracy=float(np.mean(((probability >= 0.5) == (reference == 1))[truth])) if support else None)


def evaluate(manifest, model_path, model_sha256, output, split='all', prediction_cache=None):
    started_at_utc = datetime.now(timezone.utc).isoformat()
    quality_path = ROOT / 'src/ai-core/config/ai-frangi-v1.3.yaml'
    quality_config = load_frangi_config(quality_path)
    source_paths = [Path(__file__), ROOT / 'src/ai-core/scripts/avr_mask_reference.py'] + [
        ROOT / 'src/ai-core/app/services' / name for name in
        ('avr_geometry.py', 'avr_model.py', 'avr_service.py', 'avr_rrwnet.py', 'avr_optic_disc.py', 'third_party/rrwnet/model.py', 'frangi_service.py')] + [quality_path]
    source_sha256 = {path.relative_to(ROOT).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest() for path in source_paths}
    model = load_avr_model(str(model_path), model_sha256)
    if model['model_type'] == 'RRWNET_RITE':
        import torch
        torch.set_num_threads(2)
    if model['model_type'] == 'RRWNET_RITE':
        from app.services.avr_rrwnet import enhance_rrwnet, rrwnet_probabilities, segment_rrwnet, rrwnet_labels, rrwnet_prediction_arrays
        network_source = (ROOT / 'src/ai-core/app/services/third_party/rrwnet/model.py').read_bytes()
        backend_source = network_source + ''.join(inspect.getsource(fn) for fn in
                                                (enhance_rrwnet, rrwnet_probabilities, segment_rrwnet, rrwnet_labels, rrwnet_prediction_arrays)).encode()
    else:
        backend_source = (ROOT / 'src/ai-core/app/services/avr_model.py').read_bytes()
    backend_hash = hashlib.sha256(backend_source).hexdigest()
    segmentation_identity = model_sha256
    manifest_sha256 = hashlib.sha256(manifest.read_bytes()).hexdigest()
    if model['model_type'] != 'RRWNET_RITE' and model['manifest_sha256'] != manifest_sha256:
        raise ValueError('Evaluation manifest differs from the model training manifest.')
    rows = list(csv.DictReader(manifest.open(encoding='utf-8-sig')))
    if (len(rows) != 100 or len({r['case_id'] for r in rows}) != 100
            or len({r['group_id'] for r in rows}) != 99
            or any(r.get('dataset_id') != 'AVRDB' or r.get('dataset_doi') != '10.17632/3csr652p9y.2' for r in rows)):
        raise ValueError('Expected the publisher AVRDB100 manifest with 99 unique photograph groups.')
    groups = {}
    for row in rows:
        if row['group_id'] in groups and groups[row['group_id']] != row['split']:
            raise ValueError('Duplicate photograph leakage between splits.')
        groups[row['group_id']] = row['split']
    records = []
    for row in rows:
        if split != 'all' and row['split'] != split:
            continue
        if row['split'] == 'test' and row['case_id'] in model['train_cases'] + model['validation_cases']:
            raise ValueError('Test case present in model development cases.')
        for role in ('image', 'labels'):
            raw = (ROOT / row[f'{role}_path']).read_bytes()
            if hashlib.sha256(raw).hexdigest() != row[f'{role}_sha256']:
                raise ValueError(f'Dataset SHA mismatch: {row["case_id"]} {role}')
        with Image.open(ROOT / row['image_path']) as image:
            rgb = np.asarray(image.convert('RGB'))
        if rgb.shape != (1000, 1504, 3):
            raise ValueError('Image geometry differs from the pinned AVRDB cohort.')
        quality = evaluate_frangi_quality(rgb, config=quality_config)
        started = time.perf_counter()
        cached = False
        measurement_mask = None
        key = hashlib.sha256((segmentation_identity + row['image_sha256'] + backend_hash).encode()).hexdigest()
        # shortcut: cache assumes pinned dependencies; use a fresh directory after dependency upgrades.
        cache_path = prediction_cache / (key + '.npz') if prediction_cache else None
        if prediction_cache is not None and model['model_type'] == 'RRWNET_RITE':
            from scripts.calibrate_avr_vessel_threshold import cached_probabilities
            probabilities, cache_record = cached_probabilities(rgb, model, prediction_cache)
            predicted, probability, measurement_mask = rrwnet_prediction_arrays(probabilities, (rgb.shape[1], rgb.shape[0]), model['vessel_threshold'])
            cached = cache_record['cache_hit']
            inference_seconds = cache_record['original_inference_seconds']
        elif cache_path is not None and cache_path.is_file():
            with np.load(cache_path, allow_pickle=False) as stored:
                predicted, probability = stored['labels'], stored['artery_probability']
                inference_seconds = float(stored['inference_seconds'])
            if (predicted.shape != rgb.shape[:2] or probability.shape != predicted.shape
                    or set(np.unique(predicted)) - {0, 1, 2} or not np.isfinite(probability).all()
                    or np.any(probability < 0) or np.any(probability > 1)):
                raise ValueError('Invalid cached prediction.')
            cached = True
        else:
            if model['model_type']=='RRWNET_RITE':
                predicted, probability, measurement_mask = rrwnet_prediction_arrays(
                    rrwnet_probabilities(rgb,model),(rgb.shape[1],rgb.shape[0]),model['vessel_threshold'])
            else:
                predicted, probability = segment_with_model(rgb, model)
            inference_seconds = time.perf_counter() - started
            if cache_path is not None:
                cache_path.parent.mkdir(parents=True, exist_ok=True)
                np.savez_compressed(cache_path, labels=predicted, artery_probability=probability,
                                    inference_seconds=inference_seconds)
        measurement_started = time.perf_counter()
        result = measure_avr_prediction(rgb, model, locate_optic_disc(rgb, model), predicted, probability, measurement_mask=measurement_mask)
        measurement_seconds = time.perf_counter() - measurement_started
        seconds = time.perf_counter() - started
        # Independent scoring after the runtime call; never feed expert labels into inference.
        with Image.open(ROOT / row['labels_path']) as image:
            reference = np.asarray(image)
        scores = score_segmentation(predicted, probability, reference)
        roi_scores = None
        if result.get('optic_disc') and result.get('unavailable_reason') != 'INCOMPLETE_ZONE_B':
            disc = result['optic_disc']
            yy, xx = np.ogrid[:rgb.shape[0], :rgb.shape[1]]
            radial = np.hypot(xx - disc['center_x'], yy - disc['center_y'])
            zone = (radial >= 2 * disc['radius_px']) & (radial <= 3 * disc['radius_px'])
            roi_scores = score_segmentation(predicted, probability, reference, zone)
        reference_comparison = compare_mask_reference(reference, result.get('measurement', {}))
        record = dict(case_id=row['case_id'], split=row['split'], seconds=seconds,
                      http_quality_accepted=quality.is_acceptable,
                      http_quality_details=quality.to_details_dict(),
                      http_measurement_available=quality.is_acceptable and result['measurement_available'],
                      cached_prediction=cached, original_inference_seconds=inference_seconds,
                      measurement_seconds=measurement_seconds,
                      **scores, roi_scores=roi_scores,
                      mask_derived_reference=reference_comparison,
                      **result)
        records.append(record)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.with_suffix('.partial.json').write_text(json.dumps(dict(records=records), indent=2, allow_nan=False), encoding='utf-8')
        print(row['case_id'], row['split'], result.get('unavailable_reason', 'MEASURED'), flush=True)
    if not records:
        raise ValueError('No evaluation cases selected.')
    summaries = {}
    for selected in ('train', 'validation', 'test'):
        cases = [r for r in records if r['split'] == selected]
        if not cases:
            continue
        summaries[selected] = dict(cases=len(cases), measurements=sum(r['measurement_available'] for r in cases),
                                   http_quality_accepted_cases=sum(r['http_quality_accepted'] for r in cases),
                                   http_measurements=sum(r['http_measurement_available'] for r in cases),
                                   vessel_dice=float(np.mean([r['vessel_dice'] for r in cases])),
                                   conditional_av_accuracy=float(np.mean([r['conditional_av_accuracy'] for r in cases])),
                                   av_accuracy_on_reference_support=float(np.mean([r['av_accuracy_on_reference_support'] for r in cases])),
                                   p95_seconds=float(np.percentile([r['seconds'] for r in cases], 95)))
        summaries[selected]['mask_caliber_bias'] = summarize_mask_bias([r['mask_derived_reference'] for r in cases])
        errors = [r['mask_derived_reference']['matched_cross_section_diameter_mae_px'] for r in cases
                  if r['mask_derived_reference']['matched_cross_section_diameter_mae_px'] is not None]
        avr_errors = [r['mask_derived_reference']['conditional_avr_absolute_error'] for r in cases
                      if r['mask_derived_reference']['conditional_avr_absolute_error'] is not None]
        summaries[selected].update(mask_reference_diameter_cases=len(errors),
                                   matched_mask_diameter_macro_mae_px=float(np.mean(errors)) if errors else None,
                                   conditional_mask_reference_avr_cases=len(avr_errors),
                                   conditional_mask_reference_avr_mae=float(np.mean(avr_errors)) if avr_errors else None)
        summaries[selected].update(cached_prediction_cases=sum(r['cached_prediction'] for r in cases),
                                   recorded_inference_p95_seconds=float(np.percentile([r['original_inference_seconds'] for r in cases], 95)),
                                   measurement_p95_seconds=float(np.percentile([r['measurement_seconds'] for r in cases], 95)))
        roi_cases = [r['roi_scores'] for r in cases if r['roi_scores'] and r['roi_scores']['reference_vessel_pixels'] > 0]
        summaries[selected].update(roi_scored_cases=len(roi_cases),
                                   roi_mean_vessel_dice=float(np.mean([r['vessel_dice'] for r in roi_cases])) if roi_cases else None,
                                   roi_mean_conditional_av_accuracy=float(np.mean([r['conditional_av_accuracy'] for r in roi_cases])) if roi_cases else None,
                                   roi_mean_av_accuracy_on_reference_support=float(np.mean([r['av_accuracy_on_reference_support'] for r in roi_cases])) if roi_cases else None)
    versions = {}
    for package in ('numpy', 'scipy', 'Pillow', 'opencv-python-headless', 'torch', 'safetensors', 'onnxruntime'):
        try:
            versions[package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            versions[package] = 'NOT_INSTALLED'
    report = dict(dataset_id='AVRDB', dataset_doi='10.17632/3csr652p9y.2',
                  started_at_utc=started_at_utc, finished_at_utc=datetime.now(timezone.utc).isoformat(),
                  environment=dict(os=platform.platform(), python=platform.python_version(),
                                   logical_cpus=os.cpu_count(), device='cpu', library_versions=versions),
                  model_sha256=model_sha256, manifest_sha256=manifest_sha256,
                  prediction_backend_sha256=backend_hash,
                  timing_note='Cached rows exclude fresh neural inference; original inference timing is stored separately. Not an HTTP benchmark.',
                  scope=f'Offline diagnostic inference on {len(records)} selected images (split={split}), including quality rejects; http_measurements applies the API quality gate. Training scores are not generalization.',
                  patient_independence='NOT_ESTABLISHED; only exact duplicate photographs are grouped',
                  roi_evaluation_note='Same automatic OD prediction defines ROI; ROI scores do not validate OD location or clinical calipers.',
                  clinical_validation=False, expert_scalar_avr_reference_available=False,
                  avr_mae=None, diameter_mae=None,
                  release_status='RESEARCH_ONLY_NOT_CLINICALLY_ACCEPTED',
                  limitations='No expert optic-disc landmarks, native vessel calipers or expert scalar AVR table supplied; no clinical error claims.',
                  source_sha256=source_sha256,
                  changed_source_files=[name for name, digest in source_sha256.items()
                                        if hashlib.sha256((ROOT / name).read_bytes()).hexdigest() != digest],
                  summaries=summaries, records=records)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n', encoding='utf-8')
    print(json.dumps(summaries, indent=2))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', type=Path, default=ROOT / 'docs/datasets/avrdb/manifest.csv')
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--split', choices=('all', 'train', 'validation', 'test'), default='all')
    parser.add_argument('--prediction-cache', type=Path)
    args = parser.parse_args()
    evaluate(args.manifest, args.model, args.sha256, args.output, args.split, args.prediction_cache)
