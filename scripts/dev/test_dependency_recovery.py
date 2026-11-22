"""SCRUM-401 live fault drill; only explicitly labelled disposable containers."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.parse
import uuid

from test_real_batch import request, save_evidence


def require_fixture(container, expected_label, inspect):
    obj=json.loads(inspect(['docker','inspect',container]))[0]
    if not expected_label or obj['Config'].get('Labels',{}).get('aura.acceptance')!=expected_label:
        raise ValueError('Refusing to interrupt a container outside the explicit acceptance fixture')
    return obj['Id']


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url',required=True)
    parser.add_argument('--batch-id',type=uuid.UUID,required=True)
    parser.add_argument('--fixture-label',required=True)
    for service in ['postgres','redis','rabbitmq']:parser.add_argument('--'+service,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();origin=urllib.parse.urlsplit(args.url)
    if (origin.scheme!='http' or origin.hostname not in ['localhost','127.0.0.1'] or origin.path not in ['', '/']
            or origin.username or origin.query or origin.fragment):
        raise ValueError('Fault drill requires an explicit loopback fixture origin')
    token=os.environ['AURA_TEST_FIREBASE_TOKEN']
    def docker(command):return subprocess.check_output(command,text=True,stderr=subprocess.PIPE)
    services={name:require_fixture(getattr(args,name),args.fixture_label,docker) for name in ['redis','rabbitmq','postgres']}
    if len(set(services.values()))!=3:raise ValueError('Expected three independent dependencies')
    path='/api/v1/batches/'+str(args.batch_id)
    def read():return request(args.url.rstrip('/'),token,path)
    def recovered():
        deadline=time.monotonic()+120
        while True:
            try:
                value=read()
                if value['summary']['total']<1:raise ValueError('Empty batch is not a recovery test')
                return value['summary']
            except (urllib.error.URLError,TimeoutError):
                if time.monotonic()>=deadline:raise
                time.sleep(2)
    evidence={'status':'RUNNING','startedAt':datetime.now(timezone.utc).isoformat(),
              'fixtureLabel':args.fixture_label,'batchId':str(args.batch_id),'checks':[],
              'scope':'Real dependency interruptions and batch reads; settlement/replay and FCM require their separate gates'}
    try:
        evidence['before']=recovered()
        for service,cid in services.items():
            entry={'dependency':service};started=time.monotonic()
            try:
                docker(['docker','stop','-t','15',cid])
                try:
                    summary=read()['summary'];entry['duringStatus']=200
                    if service=='postgres':raise AssertionError('Database outage must fail closed')
                    if summary['total']!=evidence['before']['total']:raise AssertionError('Batch inventory changed')
                except urllib.error.HTTPError as error:
                    entry['duringStatus']=error.code
                    if service!='postgres' or error.code!=503:raise
            finally:
                docker(['docker','start',cid])
                entry['after']=recovered()
                entry['recoverySeconds']=time.monotonic()-started
                evidence['checks'].append(entry)
                save_evidence(args.output,evidence)
        docker(['docker','exec',services['redis'],'redis-cli','FLUSHDB'])
        evidence['afterCacheDeletion']=recovered()
        # Match the deployed namespace rather than accepting a healthy-but-empty cache.
        keys=docker(['docker','exec',services['redis'],'redis-cli','--scan','--pattern','*'+str(args.batch_id)+'*']).splitlines()
        evidence['rebuiltCacheKeyCount']=len(keys)
        if not keys:raise AssertionError('PostgreSQL read did not rebuild the deleted batch cache')
        evidence['status']='PASS'
    except Exception as error:
        evidence.update(status='FAIL',errorType=type(error).__name__)
        raise
    finally:
        evidence['completedAt']=datetime.now(timezone.utc).isoformat()
        save_evidence(args.output,evidence)


if __name__=='__main__':main()
