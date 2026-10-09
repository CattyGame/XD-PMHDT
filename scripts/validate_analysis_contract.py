"""Validate Analysis REST/event/job contracts and UTF-8 LF-normalized baseline hashes."""
from pathlib import Path
import copy
import hashlib
import json
import re
import sys
import warnings
import yaml
from jsonschema import Draft202012Validator, FormatChecker, RefResolver
from openapi_schema_validator import OAS30Validator
from openapi_spec_validator import validate

ROOT = Path(__file__).resolve().parents[1]
CONTRACTS = ROOT / 'contracts'

def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def main():
    doc = yaml.safe_load((CONTRACTS / 'analysis-openapi.yaml').read_text(encoding='utf-8-sig'))
    validate(doc)
    if doc['info']['version'] != '0.6.0':
        raise ValueError('Expected Analysis REST version 0.6.0')
    schemas = doc['components']['schemas']
    with warnings.catch_warnings():
        warnings.simplefilter('ignore', DeprecationWarning)
        resolver = RefResolver.from_schema(doc)
    def rest_validator(name):
        return OAS30Validator(schemas[name], resolver=resolver, format_checker=FormatChecker())
    event_schema = read_json(CONTRACTS / 'analysis-events.schema.json')
    job_schema = read_json(CONTRACTS / 'analysis-job.schema.json')
    Draft202012Validator.check_schema(event_schema)
    Draft202012Validator.check_schema(job_schema)
    event_validator = Draft202012Validator(event_schema, format_checker=FormatChecker())
    job_validator = Draft202012Validator(job_schema, format_checker=FormatChecker())
    rest_count = event_count = job_count = embedded_count = 0
    for path in sorted((CONTRACTS / 'examples').glob('*.json')):
        value = read_json(path)
        if path.name.startswith('event-'):
            event_validator.validate(value); event_count += 1
        elif path.name == 'job-run.json':
            job_validator.validate(value); job_count += 1
        else:
            if path.name.startswith('analysis-'): name = 'Analysis'
            elif path.name.startswith('error-'): name = 'Error'
            elif path.name.startswith('review-') and path.name.endswith('-request.json'): name = 'ReviewRequest'
            else: name = {'batch-200.json':'Batch', 'create-analysis-202.json':'AnalysisAccepted', 'file-access-200.json':'FileAccess'}[path.name]
            rest_validator(name).validate(value); rest_count += 1
            if name == 'Batch':
                counts = {k:0 for k in schemas['ProcessingStatus']['enum']}
                for item in value['items']: counts[item['processingStatus']] += 1
                summary = value['summary']
                if counts != summary['byStatus'] or sum(counts.values()) != summary['total']:
                    raise ValueError('Invalid batch count summary')
                if summary['total'] != sum(summary[k] for k in ['active','succeeded','failed','duplicate']):
                    raise ValueError('Invalid batch category totals')
    for operations in doc['paths'].values():
        for method, operation in operations.items():
            if method not in ['get','post','put','patch','delete','options','head']: continue
            contents = list(operation.get('requestBody',{}).get('content',{}).values())
            for response in operation.get('responses',{}).values():
                if '$ref' in response:
                    with resolver.resolving(response['$ref']) as resolved: response = resolved
                contents.extend(response.get('content',{}).values())
            for content in contents:
                if 'schema' not in content: continue
                validator = OAS30Validator(content['schema'],resolver=resolver,format_checker=FormatChecker())
                examples = [content['example']] if 'example' in content else []
                examples.extend(e['value'] for e in content.get('examples',{}).values() if 'value' in e)
                for example in examples: validator.validate(example); embedded_count += 1
    review = rest_validator('ReviewRequest')
    for reason in [None, '', '   ', '\n\t']:
        request = {'isApproved':False,'baseVersion':0,'correctedRiskLevel':'HIGH'}
        if reason is not None: request['reason'] = reason
        if review.is_valid(request): raise ValueError('Invalid correction reason accepted')
    review.validate({'isApproved':True,'baseVersion':0})
    review.validate({'isApproved':False,'baseVersion':0,'correctedRiskLevel':'HIGH','reason':'Valid correction'})
    if review.is_valid({'isApproved':True,'baseVersion':0,'correctedRiskLevel':'HIGH'}):
        raise ValueError('Confirmation accepted correction field')
    modality = OAS30Validator(schemas['CreateAnalysisRequest']['properties']['modality'])
    modality.validate('FUNDUS')
    if modality.is_valid('OCT'): raise ValueError('OCT accepted')
    invalid_job = read_json(CONTRACTS/'examples/job-run.json')
    invalid_job['image_base64'] = 'forbidden'
    if job_validator.is_valid(invalid_job): raise ValueError('Job accepted binary field')
    invalid_event = read_json(CONTRACTS/'examples/event-completed.json')
    invalid_event['schemaVersion'] = '0.5'
    if event_validator.is_valid(invalid_event): raise ValueError('Old event schemaVersion accepted')
    hash_count = 0
    for line in (CONTRACTS/'BASELINE-W1-v0.2.md').read_text(encoding='utf-8-sig').splitlines():
        match = re.fullmatch(r'\| `([^`]+)` \| `([a-f0-9]{64})` \|',line)
        if not match: continue
        path = ROOT / match[1]
        actual = hashlib.sha256(path.read_bytes().replace(b'\r\n',b'\n')).hexdigest()
        if actual != match[2]: raise ValueError('Baseline hash mismatch: '+match[1])
        hash_count += 1
    if not hash_count: raise ValueError('No baseline hash entries found')
    print('PASS: OpenAPI 0.6.0')
    print(f'PASS: examples REST={rest_count}, event={event_count}, job={job_count}')
    print(f'PASS: embedded example checks={embedded_count}')
    print('PASS: negative checks for review, OCT, binary job and old event version')
    print(f'PASS: LF-normalized SHA-256 hashes={hash_count}')
    print('Contract validation only; backend, permissions, NiFi and end-to-end NFR are not verified.')

if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'FAIL: {error}',file=sys.stderr)
        sys.exit(1)
