"""Select a vessel threshold on AVRDB validation only; never fit against Iowa AVR references."""
from __future__ import annotations

import argparse
import csv
import hashlib
import importlib.metadata
import inspect
import json
import shutil
from pathlib import Path
import sys
import time

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'src/ai-core'))
from app.services.avr_model import load_avr_model
from app.services.avr_rrwnet import enhance_rrwnet, rrwnet_probabilities, rrwnet_labels, AVRDB_MANIFEST_SHA256, CALIBRATION_CASES
from scripts.benchmark_avrdb import score_segmentation

THRESHOLDS = tuple(round(i / 20, 2) for i in range(1, 11))
INFERENCE_SOURCE = (ROOT / 'src/ai-core/app/services/third_party/rrwnet/model.py').read_bytes()
INFERENCE_SOURCE += (inspect.getsource(enhance_rrwnet) + inspect.getsource(rrwnet_probabilities)).encode()


def cached_probabilities(rgb, model, cache_dir):
    versions = {p: importlib.metadata.version(p) for p in ('numpy', 'Pillow', 'scipy', 'torch', 'safetensors')}
    metadata = dict(image_pixels_sha256=hashlib.sha256(rgb.tobytes()).hexdigest(), native_shape=list(rgb.shape),
                    weights_sha256=model['weights_sha256'], source_sha256=hashlib.sha256(INFERENCE_SOURCE).hexdigest(),
                    versions=versions, device='cpu', torch_threads=2)
    import torch
    if torch.get_num_threads() != 2:
        raise ValueError('Offline probability cache requires two Torch threads.')
    encoded = json.dumps(metadata, sort_keys=True)
    path = Path(cache_dir) / (hashlib.sha256(encoded.encode()).hexdigest() + '.npz')
    hit = path.exists()
    if hit:
        with np.load(path, allow_pickle=False) as stored:
            if str(stored['metadata']) != encoded:
                raise ValueError('Prediction cache identity mismatch.')
            probability, seconds = stored['probabilities'], float(stored['inference_seconds'])
    else:
        began = time.perf_counter()
        probability = rrwnet_probabilities(rgb, model)
        seconds = time.perf_counter() - began
    scale = min(1., 584 / max(rgb.shape[:2]))
    expected = (max(1, round(rgb.shape[0] * scale)), max(1, round(rgb.shape[1] * scale)), 3)
    if (probability.shape != expected or not np.isfinite(probability).all()
            or np.any(probability < 0) or np.any(probability > 1) or not np.isfinite(seconds) or seconds < 0):
        raise ValueError('Invalid raw probability cache.')
    if not hit:
        path.parent.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(path, probabilities=probability, metadata=np.array(encoded), inference_seconds=seconds)
    return probability, dict(cache_path=str(path), cache_hit=hit, original_inference_seconds=seconds, **metadata)


def select_threshold(candidates):
    eligible = [x for x in candidates if x['av_accuracy_on_reference_support'] >= .60]
    # Prefer Dice, then the more conservative threshold; report failure if neither gate is achieved.
    return max(eligible or candidates, key=lambda x: (x['vessel_dice'], x['threshold']))


def export_artifact(model_path, digest, report_path, destination):
    """Export a separate verified artifact; preserve the original baseline."""
    load_avr_model(str(model_path), digest)
    report = json.loads(report_path.read_bytes())
    if report.get('base_model_sha256') != digest:
        raise ValueError('Calibration report belongs to a different baseline descriptor.')
    model = json.loads(model_path.read_bytes())
    model.update(vessel_threshold=report['selected_threshold'], calibration_file='calibration.json',
                 measurement_vessel_threshold=0.5,
                 calibration_sha256=hashlib.sha256(report_path.read_bytes()).hexdigest(),
                 manifest_sha256=report['manifest_sha256'], validation_cases=report['validation_cases'])
    destination.mkdir(parents=True, exist_ok=False)
    for name in ('model.safetensors', 'lunetv2_odc.onnx', 'WEIGHTS_LICENSE_CC_BY_NC_4.0.txt'):
        shutil.copyfile(model_path.parent / name, destination / name)
    shutil.copyfile(report_path, destination / 'calibration.json')
    descriptor = destination / 'runtime.json'
    descriptor.write_bytes((json.dumps(model, indent=2, allow_nan=False)+'\n').encode())
    exported_digest = hashlib.sha256(descriptor.read_bytes()).hexdigest()
    load_avr_model(str(descriptor), exported_digest)
    return exported_digest


def calibrate(model_path, digest, cache_dir, output):
    if output.exists():
        raise ValueError('Use a new calibration output path.')
    model = load_avr_model(str(model_path), digest)
    if model['model_type'] != 'RRWNET_RITE':
        raise ValueError('Expected the pinned RRWNet model.')
    import torch
    torch.set_num_threads(2)
    manifest = ROOT / 'docs/datasets/avrdb/manifest.csv'
    if hashlib.sha256(manifest.read_bytes()).hexdigest() != AVRDB_MANIFEST_SHA256:
        raise ValueError('Expected the frozen canonical AVRDB manifest.')
    rows = list(csv.DictReader(manifest.open(encoding='utf-8-sig')))
    selected = [r for r in rows if r['split'] == 'validation']
    if len(rows) != 100 or len(selected) != 15 or {r['case_id'] for r in selected} != CALIBRATION_CASES:
        raise ValueError('Expected canonical AVRDB100 with fifteen validation cases.')
    by_threshold = {t: [] for t in THRESHOLDS}
    cache_records = []
    for row in selected:
        for field in ('image', 'labels'):
            if hashlib.sha256((ROOT / row[field + '_path']).read_bytes()).hexdigest() != row[field + '_sha256']:
                raise ValueError('Calibration dataset hash mismatch.')
        with Image.open(ROOT / row['image_path']) as image:
            rgb = np.asarray(image.convert('RGB'))
        probabilities, record = cached_probabilities(rgb, model, cache_dir)
        with Image.open(ROOT / row['labels_path']) as image:
            reference = np.asarray(image)
        for threshold in THRESHOLDS:
            prediction, artery_probability = rrwnet_labels(probabilities, (rgb.shape[1], rgb.shape[0]), threshold)
            by_threshold[threshold].append(score_segmentation(prediction, artery_probability, reference))
        cache_records.append(dict(case_id=row['case_id'], **record))
        print(row['case_id'], 'VALIDATION_ONLY', flush=True)
    candidates = [dict(threshold=t, **{key:float(np.mean([r[key] for r in records])) for key in
                  ('vessel_dice', 'av_accuracy_on_reference_support', 'conditional_av_accuracy')})
                  for t, records in by_threshold.items()]
    winner = select_threshold(candidates)
    report = dict(algorithm='rrwnet-vessel-validation-dice-v1', dataset_id='AVRDB',
                  manifest_sha256=hashlib.sha256(manifest.read_bytes()).hexdigest(),
                  base_weights_sha256=model['weights_sha256'], base_model_sha256=digest,
                  validation_cases=[r['case_id'] for r in selected], test_cases_used=False, iowa_references_used=False,
                  threshold_grid=list(THRESHOLDS), selection='max validation Dice among support>=0.60, otherwise max Dice; tie higher threshold',
                  selected_threshold=winner['threshold'], validation_gate_met=winner['vessel_dice']>=.65 and winner['av_accuracy_on_reference_support']>=.60,
                  candidates=candidates, cache_records=cache_records,
                  calibration_script_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes((json.dumps(report, indent=2, allow_nan=False)+'\n').encode())
    return report


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--cache-dir', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--artifact-dir', type=Path, required=True)
    args=parser.parse_args()
    print(json.dumps(calibrate(args.model,args.sha256,args.cache_dir,args.output),indent=2))
    print('artifact_sha256', export_artifact(args.model,args.sha256,args.output,args.artifact_dir))
