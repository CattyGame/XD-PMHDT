"""Measure concurrent authorized reads; synthetic population is distinct from real AI workload."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import math
from pathlib import Path
import subprocess
import threading
import time
import urllib.error
import uuid

from test_real_batch import firebase_token, request, save_evidence


def measure(base, ids, workers, count, warmup, read=request):
    if not ids or not 1<=workers<=100 or count<1 or warmup<0:raise ValueError('Invalid reader workload')
    barrier=threading.Barrier(workers)
    def run(index):
        durations=[];errors=[];warm_errors=0;cold=None
        for n in range(warmup+count):
            if n==warmup:barrier.wait()
            identity=ids[(index+n)%len(ids)];start=time.perf_counter();error=None
            try:
                value=read(base,firebase_token(),'/api/v1/analyses/'+identity)
                if value.get('analysisId')!=identity:raise ValueError('Resource mismatch')
            except Exception as ex:
                error={'httpStatus':ex.code if isinstance(ex,urllib.error.HTTPError) else None,'type':type(ex).__name__}
            elapsed=(time.perf_counter()-start)*1000
            if cold is None:cold=elapsed
            if n<warmup:
                warm_errors+=int(error is not None)
            else:
                durations.append(elapsed)
                if error is not None:errors.append(error)
        return {'reader':index,'coldMs':cold,'warmupErrors':warm_errors,'durationsMs':durations,'errors':errors}
    started=time.perf_counter()
    with ThreadPoolExecutor(max_workers=workers) as pool:rows=list(pool.map(run,range(workers)))
    samples=sorted(x for row in rows for x in row['durationsMs']);failed=sum(len(row['errors']) for row in rows)
    return {'status':'FAIL' if failed or any(row['warmupErrors'] for row in rows) else 'PASS',
        'scope':'Authenticated public read correctness and measured latency; not AI accuracy or whole-sprint acceptance',
        'concurrentReaders':workers,'authenticatedPrincipals':1,'measuredRequests':len(samples),'failedRequests':failed,
        'warmupPerReader':warmup,'latencyMs':{f'p{p}':samples[math.ceil(len(samples)*p/100)-1] for p in (50,95,99)},
        'elapsedSeconds':time.perf_counter()-started,'readers':rows}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url',required=True);parser.add_argument('--analysis-ids',type=Path,required=True)
    parser.add_argument('--postgres-container',required=True);parser.add_argument('--fixture-label',required=True)
    parser.add_argument('--database',required=True)
    parser.add_argument('--readers',type=int,default=20);parser.add_argument('--requests-per-reader',type=int,default=100)
    parser.add_argument('--warmup-per-reader',type=int,default=10);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    evidence={'status':'NOT_TESTED','populationKind':'SYNTHETIC_METADATA_FIXTURE'}
    try:
        state=json.loads(subprocess.check_output(['docker','inspect',args.postgres_container]))[0]
        if state.get('Config',{}).get('Labels',{}).get('aura.acceptance')!=args.fixture_label:
            raise ValueError('Wrong disposable fixture')
        population=subprocess.check_output(['docker','exec',args.postgres_container,'psql','-X','-U','aura_dev',
            '-d',args.database,'-At','-c','SELECT (SELECT count(*) FROM identity.accounts),(SELECT count(*) FROM analysis.images);'],text=True)
        users,analyses=map(int,population.strip().split('|'))
        if users<2000 or analyses<10000:raise ValueError('Required 2000-user/10000-analysis population is missing')
        raw=args.analysis_ids.read_bytes();ids=[str(uuid.UUID(x)) for x in json.loads(raw)]
        evidence.update(measure(args.url.rstrip('/'),ids,args.readers,args.requests_per_reader,args.warmup_per_reader))
        evidence.update(population={'accountRecords':users,'analysisMetadataRecords':analyses},
            manifestSha256=hashlib.sha256(raw).hexdigest(),harnessSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            populationScope='Background metadata are synthetic test records, not 10000 real AI inferences or expert labels')
    except Exception as ex:
        evidence.update(status='FAIL',errorType=type(ex).__name__)
        raise
    finally:save_evidence(args.output,evidence)
    print(json.dumps({key:value for key,value in evidence.items() if key!='readers'}))
    return 0 if evidence['status']=='PASS' else 1


if __name__=='__main__':raise SystemExit(main())
