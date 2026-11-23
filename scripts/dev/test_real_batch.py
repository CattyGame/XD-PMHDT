"""Explicit live acceptance: real image admission, bounded uploads and terminal outcomes."""
import argparse
import concurrent.futures
import hashlib
import json
import math
import os
import platform
from pathlib import Path
import ssl
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timezone


def save_evidence(path, evidence):
    path.parent.mkdir(parents=True,exist_ok=True)
    pending=path.with_suffix('.pending')
    pending.write_text(json.dumps(evidence,indent=2),encoding='utf-8')
    pending.replace(path)


def firebase_token():
    file=os.environ.get('AURA_TEST_FIREBASE_TOKEN_FILE')
    value=json.loads(Path(file).read_text(encoding='utf-8'))['idToken'] if file else os.environ.get('AURA_TEST_FIREBASE_TOKEN')
    if not isinstance(value,str) or not value or len(value)>16384 or any(c.isspace() for c in value):
        raise ValueError('Private Firebase test token is missing or invalid')
    return value


def latency_summary(samples):
    if any(not isinstance(x,(int,float)) or not math.isfinite(x) or x<0 for x in samples):
        raise ValueError('Latency samples must be finite and nonnegative')
    warm=sorted(samples[1:])
    return {'count':len(samples),'coldMs':samples[0] if samples else None,
            'warm':{'count':len(warm),**{f'p{p}Ms':warm[math.ceil(len(warm)*p/100)-1] if warm else None for p in (50,95,99)}}}


def classify_real_result(detail):
    status=detail.get('processingStatus')
    if status in ('FAILED','LOW_QUALITY'):
        code=(detail.get('error') or {}).get('code')
        if not code:raise ValueError('Terminal failure needs an explicit reason')
        return {'measurement':'QUALITY_FAILURE' if code in ('LOW_QUALITY','ERR_IMAGE_LOW_QUALITY') else 'PIPELINE_FAILURE','errorCode':code}
    result=detail.get('result')
    if status!='COMPLETED' or not isinstance(result,dict) or result.get('isMock') is not False:
        raise ValueError('Real acceptance requires completed non-mock results')
    avr=(result.get('metrics') or {}).get('avRatio')
    research=(result.get('research') or {}).get('avr') or {}
    if avr is None:
        if research.get('measurement_available') is not False or not research.get('unavailable_reason'):
            raise ValueError('Unavailable AVR requires explicit availability and reason')
        if result.get('riskAvailable') is not False or result.get('riskLevel') is not None:
            raise ValueError('Unavailable research AVR cannot silently become low risk')
        measurement='UNAVAILABLE'
    else:
        if isinstance(avr,bool) or not isinstance(avr,(int,float)) or not math.isfinite(avr) or avr<=0:
            raise ValueError('Measured AVR must be finite and positive')
        if research and research.get('measurement_available') is not True:raise ValueError('Contradictory AVR availability')
        measurement='MEASURED'
    latency=result.get('processingTimeMs')
    if not isinstance(latency,(int,float)) or not math.isfinite(latency) or latency<0:raise ValueError('Missing processing duration')
    return {'measurement':measurement,'processingTimeMs':latency,'modelVersion':result.get('modelVersion'),
            'configVersion':result.get('configVersion'),'thresholdVersion':result.get('thresholdVersion'),
            'unavailableReason':research.get('unavailable_reason')}


def retry_busy_upload(operation,deadline):
    while True:
        try:return operation()
        except urllib.error.HTTPError as error:
            if error.code not in (409,429):raise
            try:body=json.loads(error.read(8193))
            except (ValueError,OSError):raise error
            if not isinstance(body,dict):raise error
            problem=body.get('error',{})
            if not isinstance(problem,dict):raise error
            if problem.get('retryable') is not True or error.code==409 and problem.get('code')!='ERR_REQUEST_IN_PROGRESS':raise error
            try:delay=float(error.headers.get('Retry-After','1'))
            except ValueError:raise error
            if not math.isfinite(delay) or not 0<delay<=60 or time.monotonic()+delay>=deadline:raise error
            time.sleep(delay)


def request(base, token, path, method="GET", body=None, content_type="application/json", key=None):
    headers = {"Authorization": "Bearer " + token, "Content-Type": content_type}
    if key:
        headers["Idempotency-Key"] = key
    req = urllib.request.Request(base + path, data=body, method=method, headers=headers)
    # Never forward bearer credentials through a redirect.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args):
            return None
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    with opener.open(req, timeout=120) as response:
        content = response.read()
        return json.loads(content) if content else None


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--url", required=True)
    p.add_argument("--patient", help="Fallback patient GUID when --manifest is not supplied")
    p.add_argument("--clinic-context-id", type=uuid.UUID, help="Clinic selector verified by the API authority on every request")
    p.add_argument("--manifest", type=Path, help="JSON array of {fileName, patientId, expectedStatus} records")
    p.add_argument("--resume-batch-id", help="Resume an existing parent batch by re-reading its server inventory")
    p.add_argument("--stop-after-uploads", type=int, help="Stop before finalize after N uploads to exercise resume")
    p.add_argument("--expected-succeeded", type=int)
    p.add_argument("--expected-failed", type=int)
    p.add_argument("--postgres-container", help="Optional migrated PostgreSQL container for financial reconciliation")
    p.add_argument("--require-financial-reconciliation", action="store_true")
    p.add_argument("--images", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--count", type=int, default=100)
    p.add_argument("--timeout", type=int, default=1800)
    p.add_argument("--containers", nargs="+", required=True)
    p.add_argument('--expected-workers',type=int,choices=[1,4])
    p.add_argument('--require-real-results',action='store_true',help='Read every terminal result and check AVR/null/provenance')
    p.add_argument('--allow-quality-failures',action='store_true',help='Allow only explicit low-quality terminal failures, still reconcile their release')
    args = p.parse_args()
    if args.allow_quality_failures and not args.require_real_results:raise ValueError('Quality acceptance requires real per-image result verification')
    token = firebase_token()
    if args.require_financial_reconciliation and not args.postgres_container:
        raise ValueError("--require-financial-reconciliation requires --postgres-container")
    url = urllib.parse.urlparse(args.url)
    if not token or not (url.scheme == "https" or url.scheme == "http" and url.hostname in ("localhost", "127.0.0.1")) or url.query or url.fragment or url.username:
        raise ValueError("Explicit Firebase token and HTTPS (or loopback HTTP) origin required")
    if args.patient:
        uuid.UUID(args.patient)
    if args.clinic_context_id is not None and args.clinic_context_id.int == 0:
        raise ValueError("Clinic context cannot be the empty UUID")
    if not 1 <= args.count <= 100:
        raise ValueError("count must be 1..100")
    files = sorted(f for f in args.images.iterdir() if f.suffix.lower() in (".png", ".jpg", ".jpeg"))
    if len(files) != args.count:
        raise ValueError("Dataset must contain exactly count real images")
    manifest = {}
    if args.manifest:
        records = json.loads(args.manifest.read_text(encoding="utf-8"))
        if not isinstance(records, list):
            raise ValueError("Manifest must be a JSON array")
        for record in records:
            name = record.get("fileName")
            patient_id = str(uuid.UUID(record.get("patientId", "")))
            expected = record.get("expectedStatus", "SUCCEEDED").upper()
            if expected not in ("SUCCEEDED", "FAILED") or name in manifest:
                raise ValueError("Manifest filenames must be unique and expectedStatus must be SUCCEEDED or FAILED")
            availability=record.get('expectedAvrAvailable')
            if availability is not None and type(availability) is not bool:raise ValueError('Expected AVR availability must be boolean')
            manifest[name] = {"patientId": patient_id, "expectedStatus": expected,
                              'expectedAvrAvailable':availability,'expectedErrorCode':record.get('expectedErrorCode')}
        if set(manifest) != {f.name for f in files}:
            raise ValueError("Manifest filenames must match the image directory exactly")
    elif not args.patient:
        raise ValueError("Specify --patient or --manifest")
    if (args.expected_succeeded is None) != (args.expected_failed is None):
        raise ValueError("Specify both expected outcome counts or neither")
    expected_succeeded = args.expected_succeeded if args.expected_succeeded is not None else args.count
    expected_failed = args.expected_failed if args.expected_failed is not None else 0
    if min(expected_succeeded, expected_failed) < 0 or expected_succeeded + expected_failed != args.count:
        raise ValueError("Expected success and failure counts must add up to count")
    if manifest and sum(x["expectedStatus"] == "SUCCEEDED" for x in manifest.values()) != expected_succeeded:
        raise ValueError("Expected counts disagree with the per-image manifest")
    if expected_failed and not manifest:
        raise ValueError("Partial failure acceptance requires a per-image manifest")
    if args.stop_after_uploads is not None and not 1 <= args.stop_after_uploads < args.count:
        raise ValueError("stop-after-uploads must be between 1 and count-1")
    items = []
    hashes = set()
    for f in files:
        if not 0 < f.stat().st_size <= 10 * 1024 * 1024:
            raise ValueError("Image outside per-image byte limit")
        digest = hashlib.sha256(f.read_bytes()).hexdigest()
        if digest in hashes:
            raise ValueError("Dataset contains duplicate bytes; use distinct images")
        hashes.add(digest)
        item = manifest.get(f.name, {"patientId": args.patient, "expectedStatus": "SUCCEEDED"})
        items.append(dict(itemId=str(uuid.uuid4()), patientId=item["patientId"], fileName=f.name,
                          sha256=digest, modality="FUNDUS", eyeSide="unknown",
                          expectedStatus=item["expectedStatus"],expectedAvrAvailable=item.get('expectedAvrAvailable'),
                          expectedErrorCode=item.get('expectedErrorCode')))
    evidence = {"status": "RUNNING", "declared": len(items), "samples": [],
                'observedFromUtc':datetime.now(timezone.utc).isoformat(),
                'harnessSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                'elapsedScope':'Resumed observation only' if args.resume_batch_id else 'Admission through reconciliation',
                'resumedExistingBatch':bool(args.resume_batch_id),
                "clinicContextId": str(args.clinic_context_id) if args.clinic_context_id else None,
                "manifest": items,"apiSamples":[],"inputManifestSha256":hashlib.sha256(json.dumps(
                    [{k:i[k] for k in ('fileName','sha256','patientId','expectedStatus','expectedAvrAvailable','expectedErrorCode')} for i in items],sort_keys=True).encode()).hexdigest(),
                'hardware':{'platform':platform.platform(),'logicalCpuCount':os.cpu_count()},
                'uptimeScope':'This workload observation window only; no annual availability inference',
                'qualityAcceptancePolicy':'Known low-quality failures allowed' if args.allow_quality_failures else 'Exact predeclared terminal counts'}
    started = time.monotonic()
    next_checkpoint=started
    base = args.url.rstrip("/")
    def api(path, method="GET", body=None, content_type="application/json", key=None):
        if args.clinic_context_id:
            path += "?" + urllib.parse.urlencode({"clinicContextId": str(args.clinic_context_id)})
        for attempt in range(4):
            called=time.monotonic();code=None
            try:return request(base, firebase_token(), path, method, body, content_type, key)
            except urllib.error.HTTPError as error:
                code=error.code
                if method!='GET' or code!=503 or attempt==3 or time.monotonic()+2>=started+args.timeout:raise
            except Exception:code=0;raise
            finally:evidence['apiSamples'].append({'method':method,'durationMs':(time.monotonic()-called)*1000,
                                                  'succeeded':code is None,'httpErrorStatus':code})
            time.sleep(2)
    def sample():
        nonlocal next_checkpoint
        result = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{json .}}", *args.containers], capture_output=True, text=True, check=True)
        rows = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        if len(rows) != len(args.containers):
            raise RuntimeError("Missing container resource samples")
        evidence["samples"].append(rows)
        for row in rows:
            percent=float(row["MemPerc"].rstrip("%"))
            if percent>=80:
                peaks=evidence.setdefault('resourceViolations',{})
                peaks[row['Name']]=max(peaks.get(row['Name'],0),percent)
        if time.monotonic()>=next_checkpoint:
            evidence['lastObservedUtc']=datetime.now(timezone.utc).isoformat()
            save_evidence(args.output,evidence)
            next_checkpoint=time.monotonic()+30
    try:
        sample()
        if args.resume_batch_id:
            batch = str(uuid.UUID(args.resume_batch_id))
            inventory = api(f"/api/v1/analysis-upload-batches/{batch}")
            server_items = {entry["sha256"].lower(): entry for entry in inventory["items"]}
            if len(server_items) != len(items) or set(server_items) != hashes:
                raise RuntimeError("Resume inventory does not match the selected image bytes")
            for item in items:
                server = server_items[item["sha256"]]
                if server["patientId"] != item["patientId"] or server["fileName"] != item["fileName"]:
                    raise RuntimeError("Resume inventory patient or filename does not match the manifest")
                item["itemId"] = server["itemId"]
                item["uploadStatus"] = server["uploadStatus"]
        else:
            inventory = api("/api/v1/analysis-upload-batches", "POST", json.dumps({"items": [{k:v for k,v in item.items() if not k.startswith('expected')} for item in items]}).encode(), key=str(uuid.uuid4()))
            batch = str(uuid.UUID(inventory["batchId"]))
            for item, admitted in zip(items, inventory["items"]):
                if item["itemId"] != admitted["itemId"] or item["sha256"] != admitted["sha256"]:
                    raise RuntimeError("Admission inventory changed the declared item identity")
                item["uploadStatus"] = admitted["uploadStatus"]
        evidence["batchId"] = batch
        if len(inventory["items"]) != len(items) or any(i["uploadStatus"] not in ("DECLARED", "STAGED", "UNKNOWN", "ACCEPTED") for i in items):
            raise RuntimeError("Admission rejected one or more images")
        def upload(pair):
            item, file = pair
            boundary = "aura-" + uuid.uuid4().hex
            mime = "image/png" if file.suffix.lower() == ".png" else "image/jpeg"
            body = (f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="image{file.suffix.lower()}"\r\nContent-Type: {mime}\r\n\r\n'.encode()
                    + file.read_bytes() + f"\r\n--{boundary}--\r\n".encode())
            return retry_busy_upload(lambda:api(f"/api/v1/analysis-upload-batches/{batch}/items/{item['itemId']}",
                "PUT",body,"multipart/form-data; boundary="+boundary,item["itemId"]),started+args.timeout)
        pending = [(item, file) for item, file in zip(items, files) if item["uploadStatus"] == "DECLARED"]
        if args.stop_after_uploads is not None:
            pending = pending[:args.stop_after_uploads]
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            futures = [executor.submit(upload, pair) for pair in pending]
            while not all(f.done() for f in futures):
                sample()
                time.sleep(1)
            evidence["uploaded"] = []
            for (item, _), future in zip(pending, futures):
                receipt = future.result()
                item["uploadStatus"] = receipt.get("uploadStatus", "STAGED")
                evidence["uploaded"].append(receipt["itemId"])
        if args.stop_after_uploads is not None:
            refreshed = api(f"/api/v1/analysis-upload-batches/{batch}")
            evidence["inventoryAfterInterruption"] = {
                "accepted": sum(i["uploadStatus"] == "ACCEPTED" for i in refreshed["items"]),
                "staged": sum(i["uploadStatus"] == "STAGED" for i in refreshed["items"]),
                "declared": sum(i["uploadStatus"] == "DECLARED" for i in refreshed["items"])}
            evidence["status"] = "INTERRUPTED"
            evidence["resumeBatchId"] = batch
            return
        still_declared = [i for i in items if i["uploadStatus"] == "DECLARED"]
        if still_declared:
            raise RuntimeError("Some items remain unaccepted; re-read inventory and resume before finalize")
        accepted = api(f"/api/v1/analysis-upload-batches/{batch}/finalize", "POST", b"{}", key=str(uuid.uuid4()))
        expected_by_analysis = {}
        expected_by_name = {item["fileName"]: item["expectedStatus"] for item in items}
        accepted_names = [item["fileName"] for item in accepted["items"] if item.get("analysisId")
                          and item.get("processingStatus") in ("QUEUED","DUPLICATE")]
        if len(accepted_names) != args.count or set(accepted_names) != set(expected_by_name):
            raise RuntimeError("Finalize receipts do not match every declared image exactly once")
        for receipt in accepted["items"]:
            if receipt.get("analysisId"):
                expected_by_analysis[receipt["analysisId"]] = expected_by_name[receipt["fileName"]]
        if len(expected_by_analysis) != args.count:
            raise RuntimeError("Finalize did not accept exactly one analysis per declared image")
        while time.monotonic() - started < args.timeout:
            sample()
            result = api(f"/api/v1/batches/{batch}")
            evidence["outcome"] = result
            summary = result["summary"]
            if summary["total"] == len(items) and summary["active"] == 0:
                if summary['duplicate'] or summary['succeeded']+summary['failed']!=args.count or (not args.allow_quality_failures and
                        (summary["succeeded"] != expected_succeeded or summary["failed"] != expected_failed)):
                    raise RuntimeError("Terminal outcome counts do not match the acceptance manifest")
                break
            time.sleep(2)
        else:
            raise TimeoutError("Pipeline did not reach terminal outcomes")
        states = json.loads(subprocess.run(["docker", "inspect", *args.containers], capture_output=True, text=True, check=True).stdout)
        evidence['runtime']=[{'containerId':s.get('Id'),'imageId':s.get('Image'),
            'service':s.get('Config',{}).get('Labels',{}).get('com.docker.compose.service'),
            'memoryLimitBytes':s.get('HostConfig',{}).get('Memory'),
            'nanoCpus':s.get('HostConfig',{}).get('NanoCpus'),
            'cpusetCpus':s.get('HostConfig',{}).get('CpusetCpus'),
            'inferenceMaxWaitersEnv':next((entry.partition('=')[2] for entry in s.get('Config',{}).get('Env',[])
                                         if entry.startswith('AURA_AI_MAX_WAITERS=')),None),
            'gcHeapHardLimitEnv':next((entry.partition('=')[2] for entry in s.get('Config',{}).get('Env',[])
                                     if entry.startswith('DOTNET_GCHeapHardLimit=')),None),
            'gcHeapHardLimitPercentEnv':next((entry.partition('=')[2] for entry in s.get('Config',{}).get('Env',[])
                                            if entry.startswith('DOTNET_GCHeapHardLimitPercent=')),None),
            'threads':{k:v for entry in s.get('Config',{}).get('Env',[]) for k,_,v in [entry.partition('=')]
                       if k in ('OMP_NUM_THREADS','OPENBLAS_NUM_THREADS','MKL_NUM_THREADS')}} for s in states]
        if any(s["State"]["OOMKilled"] or s["State"]["ExitCode"] == 137 or not s["State"]["Running"] for s in states):
            raise RuntimeError("Container stopped or was OOMKilled")
        if args.expected_workers is not None:
            actual=sum(s.get('Config',{}).get('Labels',{}).get('com.docker.compose.service')=='analysis-worker' for s in states)
            if actual!=args.expected_workers:raise RuntimeError('Actual worker replica count disagrees with workload declaration')
            evidence['workerReplicas']=actual
        if args.require_real_results:
            details=[]
            evidence['acceptanceViolations']=[]
            expected_by_name={i['fileName']:i for i in items}
            for receipt in accepted['items']:
                detail=api('/api/v1/analyses/'+receipt['analysisId'])
                row=classify_real_result(detail);expected=expected_by_name[receipt['fileName']]
                if args.allow_quality_failures:
                    if row['measurement']=='PIPELINE_FAILURE':
                        evidence['acceptanceViolations'].append({'analysisId':receipt['analysisId'],'code':row['errorCode']})
                    expected_by_analysis[receipt['analysisId']]='FAILED' if row['measurement'] in ('QUALITY_FAILURE','PIPELINE_FAILURE') else 'SUCCEEDED'
                if expected.get('expectedAvrAvailable') is not None and (row['measurement']=='MEASURED')!=expected['expectedAvrAvailable']:
                    raise RuntimeError('Measurement availability disagrees with workload manifest')
                if expected.get('expectedErrorCode') and row.get('errorCode')!=expected['expectedErrorCode']:
                    raise RuntimeError('Failure reason disagrees with workload manifest')
                details.append({'analysisId':receipt['analysisId'],**row})
            evidence['resultDetails']=details
            evidence['measurementCoverage']={k:sum(r['measurement']==k for r in details) for k in
                ('MEASURED','UNAVAILABLE','QUALITY_FAILURE','PIPELINE_FAILURE')}
            evidence['measurementCoverage']['denominator']=len(items)
            durations=[r['processingTimeMs'] for r in details if 'processingTimeMs' in r]
            evidence['aiProcessingDuration']={'allResults':latency_summary([0]+durations)['warm'],
                'coldWarmClassification':'NOT_MEASURED: manifest order is not inference execution order',
                'scope':'Stored processing duration; queue/network time excluded'}
        evidence["status"] = "PASS_PIPELINE_ONLY"
        if args.postgres_container:
            sql = f"""
                SELECT i.id::text, r.id::text, r.state,
                       max(l.id::text) FILTER (WHERE l.entry_type='RESERVE'),
                       max(l.id::text) FILTER (WHERE l.entry_type='CAPTURE'),
                       max(l.id::text) FILTER (WHERE l.entry_type='RELEASE'),
                       max(l.amount) FILTER (WHERE l.entry_type='RESERVE'),
                       max(l.amount) FILTER (WHERE l.entry_type='CAPTURE'),
                       max(l.amount) FILTER (WHERE l.entry_type='RELEASE'),
                       w.id::text, w.balance,
                       (SELECT COALESCE(SUM(x.amount),0) FROM billing.credit_ledger_entries x WHERE x.wallet_id=w.id)
                FROM analysis.images i
                LEFT JOIN billing.credit_reservations r ON r.analysis_id=i.id AND r.tenant_id=i.tenant_id
                LEFT JOIN billing.credit_ledger_entries l ON l.reservation_id=r.id
                LEFT JOIN billing.credit_wallets w ON w.id=r.wallet_id
                WHERE i.batch_id='{batch}'::uuid
                GROUP BY i.id,r.id,r.state,w.id,w.balance ORDER BY i.id;
            """
            # Settlement is asynchronous; analysis completion is not a financial ACK.
            while True:
                result = subprocess.run(["docker", "exec", args.postgres_container, "psql", "-X", "-v", "ON_ERROR_STOP=1", "-A", "-t", "-F", "\t", "-U", "aura_dev", "-d", "aura_dev", "-c", sql], capture_output=True, text=True, check=True)
                ledger = []
                discrepancies = []
                for line in result.stdout.splitlines():
                    fields = line.split("\t")
                    if len(fields) != 12:
                        raise RuntimeError("Unexpected ledger query output")
                    image_id, reservation_id, state, reserve_id, capture_id, release_id, reserve_amount, capture_amount, release_amount, wallet_id, balance, ledger_balance = fields
                    expected = expected_by_analysis.get(image_id)
                    row = {"analysisId": image_id, "reservationId": reservation_id or None, "state": state or None,
                           "reserveId": reserve_id or None, "captureId": capture_id or None, "releaseId": release_id or None,
                           "reserveAmount": int(reserve_amount) if reserve_amount else None,
                           "captureAmount": int(capture_amount) if capture_amount else None,
                           "releaseAmount": int(release_amount) if release_amount else None,
                           "expectedStatus": expected,
                           "walletId": wallet_id or None, "walletBalance": int(balance) if balance else None,
                           "netLedgerBalance": int(ledger_balance) if ledger_balance else None}
                    ledger.append(row)
                    if row["walletBalance"] is not None and row["walletBalance"] != row["netLedgerBalance"]:
                        discrepancies.append({"walletId": wallet_id, "walletBalance": row["walletBalance"], "ledgerBalance": row["netLedgerBalance"]})
                    expected_state = "CAPTURED" if expected == "SUCCEEDED" else "RELEASED"
                    expected_amounts = (-1, 0, None) if expected_state == "CAPTURED" else (-1, None, 1)
                    actual_amounts = (row["reserveAmount"], row["captureAmount"], row["releaseAmount"])
                    if not row["reserveId"] or row["state"] != expected_state or actual_amounts != expected_amounts or (expected_state == "CAPTURED" and (not row["captureId"] or row["releaseId"])) or (expected_state == "RELEASED" and (not row["releaseId"] or row["captureId"])):
                        discrepancies.append({"analysisId": image_id, "expectedState": expected_state, "actualState": row["state"]})
                evidence["financialLedger"] = ledger
                evidence["financialDiscrepancies"] = discrepancies
                if any(row["analysisId"] not in expected_by_analysis for row in ledger):
                    raise RuntimeError("Ledger contains an analysis outside the accepted manifest")
                complete = len(ledger) == args.count and {row["analysisId"] for row in ledger} == set(expected_by_analysis) and not discrepancies and all(row["reservationId"] and row["state"] in ("CAPTURED", "RELEASED") for row in ledger)
                if complete:
                    break
                if time.monotonic() - started >= args.timeout:
                    raise TimeoutError("Financial settlement did not converge before the acceptance deadline")
                sample()
                time.sleep(2)
            evidence["status"] = "PASS"
        if evidence.get('resourceViolations'):
            raise RuntimeError('Observed memory >=80%; terminal outcomes and settlement retained')
        if evidence.get('acceptanceViolations'):
            raise RuntimeError('Infrastructure failures are not accepted quality failures; complete diagnostics retained')
    except Exception as error:
        evidence["status"] = "FAIL"
        evidence["errorType"] = type(error).__name__
        raise
    finally:
        evidence["elapsedSeconds"] = time.monotonic() - started
        evidence['apiLatency']=latency_summary([r['durationMs'] for r in evidence['apiSamples']])
        evidence['apiLatency']['coldInterpretation']='First observed request only; process/model cold start not inferred'
        evidence['apiErrorRate']=sum(not r['succeeded'] for r in evidence['apiSamples'])/max(1,len(evidence['apiSamples']))
        evidence['observedToUtc']=datetime.now(timezone.utc).isoformat()
        save_evidence(args.output,evidence)


if __name__ == "__main__":
    main()
