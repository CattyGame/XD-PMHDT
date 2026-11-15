"""Real HTTP acceptance over the frozen AVRDB test inventory; never clinical certification."""
from __future__ import annotations

import argparse
import base64
import csv
from concurrent.futures import ThreadPoolExecutor
import hashlib
import io
import json
import math
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'src/ai-core'))
from scripts.accept_m4_container import docker, cgroup
from app.auth.service_jwt import DEFAULT_AUDIENCE, DEFAULT_ISSUER, DEFAULT_REQUIRED_SCOPE
from app.services.avr_model import load_avr_model
from app.services.frangi_service import load_frangi_config, evaluate_frangi_quality
from tests.test_frangi_sprint3 import EXPECTED_CLIENT_ID, VALID_SUB, generate_deterministic_rsa_keypair, sign_rs256_jwt


def equivalent(widths, coefficient):
    """Independent arithmetic check of six reported widths, not independent clinical calipers."""
    if len(widths) != 6 or any(isinstance(x, bool) or not math.isfinite(x) or x <= 0 for x in widths):
        raise ValueError('Expected six finite positive vessel diameters.')
    widths = sorted(widths)
    while len(widths) > 1:
        next_round = [coefficient * math.sqrt(widths[i]**2 + widths[-1-i]**2)
                      for i in range(len(widths) // 2)]
        if len(widths) % 2:
            next_round.append(widths[len(widths) // 2])
        widths = sorted(next_round)
    return round(widths[0], 3)


def verify_response(body, size):
    if body['status'] != 'RESEARCH_ONLY' or body['risk_available'] is not False:
        raise ValueError('Research response must not claim clinical risk.')
    if body['model_metadata']['clinical_validation'] is not False:
        raise ValueError('Clinical validation must remain false.')
    masks = []
    for role in ('artery', 'vein'):
        raw = base64.b64decode(body['segmentation'][role + '_mask_base64_png'], validate=True)
        with Image.open(io.BytesIO(raw)) as mask:
            if mask.format != 'PNG' or mask.mode != 'L' or mask.size != size:
                raise ValueError('Mask must be native-geometry L PNG.')
            pixels = np.asarray(mask)
            if not set(np.unique(pixels)) <= {0, 255}:
                raise ValueError('Mask must be binary 0/255.')
            masks.append(pixels > 0)
    if np.any(masks[0] & masks[1]):
        raise ValueError('Artery and vein predictions must not overlap.')
    if 'overlay_base64_png' in body:
        with Image.open(io.BytesIO(base64.b64decode(body['overlay_base64_png'], validate=True))) as overlay:
            if overlay.format != 'PNG' or overlay.mode != 'RGB' or overlay.size != size:
                raise ValueError('Overlay must be native-geometry RGB PNG.')
    elif body['measurement_available']:
        raise ValueError('A measured case must provide the requested overlay.')
    if body['measurement_available'] is True:
        for name, coefficient, field in (('arteries', .88, 'crae_px'), ('veins', .95, 'crve_px')):
            branches = body['measurement'][name]
            widths = [b['diameter_px'] for b in branches]
            if body[field] != equivalent(widths, coefficient):
                raise ValueError('Incorrect Knudtson equivalent.')
            seen = set()
            for branch in branches:
                ids = branch['source_branch_ids']
                if branch['samples'] < 3 or not ids or len(set(ids)) != len(ids) or seen.intersection(ids):
                    raise ValueError('Duplicate vessel or insufficient cross-sections.')
                seen.update(ids)
        if not math.isfinite(body['avr']) or body['avr'] != body['crae_px'] / body['crve_px']:
            raise ValueError('AVR must equal the reported CRAE/CRVE without clamping.')
    elif (body['measurement_available'] is not False or any(body[k] is not None for k in ('avr', 'crae_px', 'crve_px'))
          or body['risk_level'] != 'RISK_UNAVAILABLE' or not body.get('unavailable_reason')):
        raise ValueError('Unavailable measurements require null values and an explicit reason.')
    return masks[0].astype(np.uint8) + 2 * masks[1].astype(np.uint8)


def confusion_matrix(prediction, reference):
    """Rows=reference, columns=prediction; include missed vessels, exclude unknown annotations."""
    if (prediction.shape != reference.shape or prediction.ndim != 2
            or not set(np.unique(prediction)) <= {0, 1, 2}
            or not set(np.unique(reference)) <= {0, 1, 2, 255}):
        raise ValueError('Invalid A/V label geometry or class.')
    eligible = reference != 255
    return np.bincount(3 * reference[eligible].astype(np.int64) + prediction[eligible], minlength=9).reshape(3, 3)


def latency_summary(records):
    summaries = {}
    for dataset in sorted({r['dataset'] for r in records}):
        rows = [r for r in records if r['dataset']==dataset]
        accepted = [r for r in rows if r['quality_accepted']]
        times = [r['http_seconds'] for r in accepted]
        summaries[dataset] = dict(total_cases=len(rows),quality_accepted=len(accepted),
            measurements=sum(r.get('measurement_available',False) for r in rows),
            p95_seconds=float(np.percentile(times,95)) if times else None,
            max_seconds=max(times) if times else None,
            quality_rejects=sum(not r['quality_accepted'] for r in rows),
            busy_responses=sum(r['http_status']==429 for r in rows),
            gate_met=bool(times) and all(r['http_status']==200 for r in accepted)
                and float(np.percentile(times,95))<=90 and max(times)<=120)
    return summaries


def soak_payload(request_id, image_base64):
    return dict(request_id=request_id,patient_id=None,modality='FUNDUS',mode='retina_vessels',
                include_mask=True,include_overlay=True,image_base64=image_base64)


def mixed_soak(base, container, key, image_base64, seconds, output, include_dr):
    modes=['avr','frangi'] + (['dr'] if include_dr else [])
    paths=dict(avr='/api/v1/research/avr',frangi='/api/v1/analyze',dr='/api/v1/research/dr-screening')
    began=time.perf_counter();records=[];telemetry=[];lock=threading.Lock()
    def worker(index):
        iteration=0
        while time.perf_counter()-began < seconds:
            mode=modes[(index+iteration)%len(modes)];iteration+=1
            now=int(time.time());request_id=str(uuid.uuid4())
            token=sign_rs256_jwt(key,header={'alg':'RS256','typ':'JWT','kid':key.key_id},payload={
                'iss':DEFAULT_ISSUER,'aud':DEFAULT_AUDIENCE,'sub':VALID_SUB,'client_id':EXPECTED_CLIENT_ID,
                'scope':DEFAULT_REQUIRED_SCOPE,'iat':now,'nbf':now,'exp':now+240})
            payload=soak_payload(request_id,image_base64)
            request=urllib.request.Request(base+paths[mode],data=json.dumps(payload).encode(),headers={
                'Content-Type':'application/json','Authorization':'Bearer '+token,'X-Correlation-Id':request_id})
            start=time.perf_counter();record=dict(mode=mode,request_id=request_id,client=index,status=None)
            try:
                try:response=urllib.request.urlopen(request,timeout=180)
                except urllib.error.HTTPError as error:response=error
                with response:
                    body=json.load(response);record['status']=response.status
                    if response.headers.get('X-Correlation-Id')!=request_id:raise ValueError('Correlation mismatch')
                if record['status']==200:
                    if body.get('request_id')!=request_id:raise ValueError('Request identity mismatch')
                    if mode=='frangi' and body.get('is_mock') is not False:raise ValueError('Mock response')
                    if mode!='frangi' and body.get('status')!='RESEARCH_ONLY':raise ValueError('Invalid research response')
                elif record['status']!=429 or body.get('error_code')!='RESEARCH_MODEL_BUSY':
                    raise ValueError('Unexpected HTTP outcome: '+str(record['status']))
            except Exception as error:record['error']=type(error).__name__+': '+str(error)[:160]
            record.update(seconds=time.perf_counter()-start,finished_seconds=time.perf_counter()-began)
            with lock:records.append(record)
            if record['status']==429:time.sleep(1)
    with ThreadPoolExecutor(max_workers=10) as pool:
        futures=[pool.submit(worker,i) for i in range(10)]
        while any(not f.done() for f in futures):
            telemetry.append(dict(elapsed_seconds=time.perf_counter()-began,**cgroup(container)))
            with lock:snapshot=dict(completed=len(records),errors=sum('error' in r for r in records))
            (output/'soak-progress.json').write_bytes(json.dumps(dict(**snapshot,telemetry=telemetry[-1])).encode())
            time.sleep(10)
        for future in futures:future.result()
    counts={mode:sum(r['mode']==mode and r['status']==200 and 'error' not in r for r in records) for mode in modes}
    report=dict(protocol='ten-client-round-robin-v2-one-second-busy-backoff',client_count=10,requested_seconds=seconds,
                seconds=time.perf_counter()-began,modes=modes,successful_inferences=counts,
                payload='One quality-accepted native AVRDB test image; masks/overlays on for all modes; no PHI',
                gate_met=seconds>=900 and all(counts.values()) and not any('error' in r for r in records),
                records=records,telemetry=telemetry)
    (output/'mixed-soak.json').write_bytes(json.dumps(report,indent=2,allow_nan=False).encode())
    return {k:v for k,v in report.items() if k not in ('records','telemetry')}


def acceptance(image, artifacts, output, inspire_root=None,soak_seconds=0,dr_checkpoint=None,soak_only=False):
    descriptor = artifacts / 'runtime.json'
    digest = hashlib.sha256(descriptor.read_bytes()).hexdigest()
    model = load_avr_model(str(descriptor), digest)
    if model['model_type'] != 'RRWNET_RITE':
        raise ValueError('This acceptance run requires the pinned RRWNet/PVBM artifacts.')
    if not math.isfinite(soak_seconds) or soak_seconds < 0:
        raise ValueError('Invalid mixed-mode duration.')
    if soak_only and (not soak_seconds or inspire_root is not None):
        raise ValueError('Soak-only requires positive duration and no Iowa sequential inventory.')
    manifest = ROOT / 'docs/datasets/avrdb/manifest.csv'
    from app.services.avr_rrwnet import AVRDB_MANIFEST_SHA256
    if hashlib.sha256(manifest.read_bytes()).hexdigest()!=AVRDB_MANIFEST_SHA256:
        raise ValueError('HTTP acceptance requires the frozen canonical AVRDB manifest.')
    rows = list(csv.DictReader(manifest.open(encoding='utf-8-sig')))
    if len(rows) != 100 or len({r['case_id'] for r in rows}) != 100 or len({r['group_id'] for r in rows}) != 99:
        raise ValueError('Expected canonical AVRDB100 manifest.')
    samples = [dict(dataset='avrdb-test',**r) for r in rows if r['split'] == 'test']
    if len(samples) != 15 or any(r['case_id'] in model['train_cases'] + model['validation_cases'] for r in samples):
        raise ValueError('Expected all fifteen test cases outside model development lists.')
    if inspire_root is not None:
        from scripts.benchmark_expert_references import load_inspire_reference
        iowa,reference=load_inspire_reference(inspire_root)
        samples += [dict(dataset='iowa40',case_id=r['case_id'],image_path=r['image'],
                         image_sha256=hashlib.sha256(r['image'].read_bytes()).hexdigest()) for r in iowa]
    output.mkdir(parents=True, exist_ok=False)
    runtime = [ROOT / 'src/ai-core/app/main.py', ROOT / 'src/ai-core/config/ai-frangi-v1.3.yaml',
               *sorted((ROOT / 'src/ai-core/app/services').rglob('*.py'))]
    runtime_hashes = {p.relative_to(ROOT / 'src/ai-core').as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in runtime}
    report = dict(status='RUNNING', scope='Complete selected inventories, real HTTP, 4 CPU/2 GiB; research only',
                  clinical_validation=False, descriptor_sha256=digest, manifest_sha256=hashlib.sha256(manifest.read_bytes()).hexdigest(),
                  runtime_source_sha256=runtime_hashes, harness_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  confusion_axes=['background', 'artery', 'vein'], records=[])
    report['dr_checkpoint_sha256']=hashlib.sha256(dr_checkpoint.read_bytes()).hexdigest() if dr_checkpoint else None
    config = load_frangi_config(ROOT / 'src/ai-core/config/ai-frangi-v1.3.yaml')
    if soak_only:
        for row in samples:
            with Image.open(ROOT / row['image_path']) as photo:
                accepted=evaluate_frangi_quality(np.asarray(photo.convert('RGB')),config=config).is_acceptable
            if accepted:
                samples=[row]
                break
        else:raise ValueError('No accepted image for a diagnostic soak.')
        report['scope']='DIAGNOSTIC_SOAK_ONE_IMAGE; not full sequential cohort acceptance'
    key = generate_deterministic_rsa_keypair(seed=20261114, bits=2048)
    with socket.socket() as temporary:
        temporary.bind(('127.0.0.1', 0))
        port = temporary.getsockname()[1]
    name = 'aura-avr-accept-' + uuid.uuid4().hex[:10]
    started = False
    try:
        dr_arguments=[]
        if dr_checkpoint is not None:
            dr_arguments=['--mount',f'type=bind,source={dr_checkpoint.resolve().as_posix()},target=/dr/model.pt,readonly',
                '-e','AURA_DR_CNN_CHECKPOINT=/dr/model.pt','-e','AURA_DR_CNN_SHA256='+hashlib.sha256(dr_checkpoint.read_bytes()).hexdigest()]
        docker('run', '--detach', '--name', name, '--label', 'aura.acceptance=s373', '--cpus', '4', '--memory', '2g',
               '--pids-limit', '128', '--read-only', '--tmpfs', '/tmp:rw,size=128m', '--publish', f'127.0.0.1:{port}:8000',
               '--mount', f'type=bind,source={artifacts.resolve().as_posix()},target=/models,readonly',
               '-e', 'AURA_AI_ENGINE=frangi', '-e', 'AURA_ALLOW_MOCK=false',
               '-e', 'AURA_FRANGI_CONFIG_PATH=/app/config/ai-frangi-v1.3.yaml',
               '-e', 'MALLOC_ARENA_MAX=2', '-e', 'ONEDNN_PRIMITIVE_CACHE_CAPACITY=128', '-e', 'LRU_CACHE_CAPACITY=128',
               '-e', 'MALLOC_MMAP_THRESHOLD_=131072', '-e', 'MALLOC_TRIM_THRESHOLD_=131072',
               '-e', f'AURA_SERVICE_AUTH_PUBLIC_KEY_PEM={key.public_pem}',
               '-e', 'AURA_AVR_MODEL_PATH=/models/runtime.json', '-e', 'AURA_AVR_MODEL_SHA256=' + digest,*dr_arguments, image)
        started = True
        base = f'http://127.0.0.1:{port}'
        for _ in range(120):
            try:
                with urllib.request.urlopen(base + '/ready', timeout=2) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(.5)
        else:
            raise RuntimeError('Container did not become ready.')
        report['image_id'] = json.loads(docker('inspect', name))[0]['Image']
        performance_keys = ('MALLOC_ARENA_MAX', 'MALLOC_MMAP_THRESHOLD_', 'MALLOC_TRIM_THRESHOLD_',
                            'ONEDNN_PRIMITIVE_CACHE_CAPACITY', 'LRU_CACHE_CAPACITY',
                            'OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS')
        report['runtime_environment'] = dict(item.split('=', 1) for item in json.loads(docker('inspect', name))[0]['Config']['Env']
                                             if item.split('=', 1)[0] in performance_keys)
        code = 'import hashlib,json,pathlib; names=' + repr(list(runtime_hashes)) + '; print(json.dumps({n:hashlib.sha256(pathlib.Path(n).read_bytes()).hexdigest() for n in names}))'
        if json.loads(docker('exec', name, 'python', '-c', code)) != runtime_hashes:
            raise ValueError('Docker runtime differs from the current source; rebuild the image.')
        soak_photo=None
        for row in samples:
            raw = (ROOT / row['image_path']).read_bytes()
            if hashlib.sha256(raw).hexdigest() != row['image_sha256']:
                raise ValueError('Image SHA mismatch: ' + row['case_id'])
            with Image.open(io.BytesIO(raw)) as source:
                rgb, size = np.asarray(source.convert('RGB')), source.size
            quality = evaluate_frangi_quality(rgb, config=config)
            now = int(time.time())
            token = sign_rs256_jwt(key, header={'alg': 'RS256', 'typ': 'JWT', 'kid': key.key_id},
                payload={'iss': DEFAULT_ISSUER, 'aud': DEFAULT_AUDIENCE, 'sub': VALID_SUB, 'client_id': EXPECTED_CLIENT_ID,
                         'scope': DEFAULT_REQUIRED_SCOPE, 'iat': now, 'nbf': now, 'exp': now + 240})
            request_id = str(uuid.uuid4())
            payload = dict(request_id=request_id, patient_id=None, modality='FUNDUS', include_mask=True,
                           include_overlay=True, image_base64=base64.b64encode(raw).decode('ascii'))
            request = urllib.request.Request(base + '/api/v1/research/avr', data=json.dumps(payload).encode(),
                headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token, 'X-Correlation-Id': request_id})
            began = time.perf_counter()
            record = dict(dataset=row['dataset'],case_id=row['case_id'],image_sha256=row['image_sha256'],
                          http_status=None,quality_accepted=quality.is_acceptable)
            report['records'].append(record)
            try:
                response = urllib.request.urlopen(request, timeout=180)
            except urllib.error.HTTPError as error:
                response = error
            except (OSError,urllib.error.URLError) as error:
                record.update(http_seconds=time.perf_counter()-began,error=type(error).__name__)
                raise
            with response:
                status, body = response.status, json.load(response)
                if response.headers.get('X-Correlation-Id') != request_id:
                    raise ValueError('Missing correlation ID.')
            record.update(http_status=status,http_seconds=time.perf_counter()-began)
            if quality.is_acceptable:
                if soak_photo is None:soak_photo=payload['image_base64']
                if status != 200 or body.get('request_id') != request_id or body.get('model_sha256') != digest:
                    raise ValueError('Invalid successful research response.')
                labels = verify_response(body, size)
                if row['dataset']=='avrdb-test':
                    reference_raw = (ROOT / row['labels_path']).read_bytes()
                    if hashlib.sha256(reference_raw).hexdigest() != row['labels_sha256']:
                        raise ValueError('Reference SHA mismatch.')
                    with Image.open(io.BytesIO(reference_raw)) as reference:
                        record['confusion_matrix'] = confusion_matrix(labels, np.asarray(reference)).tolist()
                record.update(measurement_available=body['measurement_available'], avr=body['avr'],
                              reason=body.get('unavailable_reason'))
            elif status != 422 or body.get('error_code') != 'LOW_QUALITY_IMAGE':
                raise ValueError('Low-quality input did not fail closed.')
            else:
                record.update(measurement_available=False, avr=None, reason='LOW_QUALITY_IMAGE')
            (output / (row['case_id'] + '.json')).write_bytes(json.dumps(body, allow_nan=False).encode())
            (output / 'progress.json').write_bytes(json.dumps(report, indent=2, allow_nan=False).encode())
            print(row['case_id'], status, record['reason'] or 'MEASURED', flush=True)
        matrices = [r['confusion_matrix'] for r in report['records'] if 'confusion_matrix' in r]
        report['confusion_matrix'] = np.sum(matrices, axis=0).tolist()
        report['confusion_scope'] = 'Only quality-accepted HTTP predictions; rows=reference, columns=prediction; 255 excluded.'
        report['coverage'] = dict(total_cases=len(samples), quality_accepted=sum(r['quality_accepted'] for r in report['records']),
                                  measurements=sum(r['measurement_available'] for r in report['records']))
        report['latency_by_dataset']=latency_summary(report['records'])
        report['cold_first_request']=report['records'][0]
        report['cold_first_inference']=next((r for r in report['records'] if r['http_status']==200),None)
        if soak_seconds:
            if not soak_photo:raise ValueError('No accepted image for the mixed-mode workload.')
            report['mixed_soak']=mixed_soak(base,name,key,soak_photo,soak_seconds,output,dr_checkpoint is not None)
        report['cgroup'] = cgroup(name)
        state = json.loads(docker('inspect', name))[0]
        if state['State']['OOMKilled'] or state['RestartCount'] or 'oom_kill 0' not in report['cgroup']['memory.events']:
            raise ValueError('Container OOM or restart.')
        if not report['coverage']['measurements']:
            raise ValueError('No actual six-vessel measurement demonstrated.')
        report['runtime_acceptance_v1']=dict(latency=all(r['gate_met'] for r in report['latency_by_dataset'].values()),
            memory_headroom=int(report['cgroup']['memory.peak'])<=.9*2*1024**3,
            complete_iowa_and_avrdb=inspire_root is not None and len(report['records'])==55,
            mixed_soak=report.get('mixed_soak',{}).get('gate_met',False))
        if any(hashlib.sha256((ROOT / 'src/ai-core' / p).read_bytes()).hexdigest() != digest
               for p, digest in runtime_hashes.items()):
            raise ValueError('Runtime source changed during acceptance.')
        report['status'] = 'FUNCTIONAL_HTTP_COMPLETE_RESEARCH_ONLY'
    except Exception as error:
        report.update(status='FAILED', error=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        (output / 'report.json').write_bytes((json.dumps(report, indent=2, allow_nan=False) + '\n').encode())
        if started:
            try:
                logs = subprocess.run(['docker', 'logs', name], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                      check=True, timeout=30)
                (output / 'server.log').write_bytes(logs.stdout)
            finally:
                docker('rm', '--force', name)
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', required=True)
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--inspire-root',type=Path)
    parser.add_argument('--soak-seconds',type=float,default=0)
    parser.add_argument('--dr-checkpoint',type=Path)
    parser.add_argument('--soak-only',action='store_true',help='One-image diagnostic soak; never claims complete test cohorts.')
    args = parser.parse_args()
    result = acceptance(args.image, args.artifacts, args.output,args.inspire_root,args.soak_seconds,args.dr_checkpoint,args.soak_only)
    print(result['status'], result['coverage'])
