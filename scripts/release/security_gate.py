"""SCRUM-398: scan the explicit candidate, redact secrets, and retain actionable findings."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import subprocess

TRIVY = 'aquasec/trivy@sha256:8f6bb947dbc528902e11f2f5be52f7b519f6a39d5d148f41166f2040499768fb'


def redact(value):
    if isinstance(value, list): return [redact(v) for v in value]
    if isinstance(value, dict):
        return {k: '[REDACTED]' if k in ('Match', 'Code') else redact(v) for k, v in value.items()}
    return value


def evaluate(reports, exceptions, today):
    if not reports: return {'status': 'NOT_TESTED', 'blocking': [], 'exceptions': []}
    blocking, waived = [], []
    for report in reports:
        if not isinstance(report.get('Results'), list): raise ValueError('Missing scanner Results')
        for result in report['Results']:
            for kind in ('Vulnerabilities', 'Secrets'):
                for finding in result.get(kind) or []:
                    if kind == 'Vulnerabilities' and finding.get('Severity') not in ('HIGH', 'CRITICAL'): continue
                    row = dict(id=finding.get('VulnerabilityID', finding.get('RuleID')), target=result['Target'],
                               package=finding.get('PkgName', ''), version=finding.get('InstalledVersion', ''),
                               kind=kind, severity=finding.get('Severity'), fixedVersion=finding.get('FixedVersion'),
                               startLine=finding.get('StartLine'))
                    match = None
                    for exception in exceptions:
                        try: valid = dt.date.fromisoformat(exception.get('expires', '')) >= today
                        except ValueError: valid = False
                        if (valid and exception.get('owner') and exception.get('reason')
                            and all(exception.get(k) == row[k] for k in ('id', 'target', 'package', 'version'))):
                            match = exception; break
                    if match: waived.append(dict(finding=row, exception=match))
                    else: blocking.append(row)
    return {'status': 'FAIL' if blocking else 'PASS_WITH_EXCEPTIONS' if waived else 'PASS',
            'blocking': blocking, 'exceptions': waived}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--image', action='append', default=[], help='Candidate image pinned by @sha256:')
    parser.add_argument('--image-archive', action='append', default=[], type=Path, help='docker image save archive of a local candidate')
    parser.add_argument('--exceptions', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    if not (source / 'AURA.sln').is_file(): parser.error('Explicit AURA source root required')
    if any('@sha256:' not in image for image in args.image): parser.error('Pin candidate image digests')
    reports = []
    evidence = {'status': 'NOT_TESTED', 'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
                'scanner': TRIVY, 'scope': 'Source/lockfiles and explicitly listed images; not host volume encryption',
                'images': args.image, 'imageArchives': []}
    try:
        evidence['commit'] = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
        evidence['workingTree'] = subprocess.check_output(['git', '-C', str(source), 'status', '--porcelain'], text=True).splitlines()
        evidence['inputsSha256'] = {str(p.relative_to(source)).replace('\\', '/'): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in [source/'infra/security/trivy-secret.yaml', source/'src/web/aura-web/package-lock.json',
                      source/'src/ai-core/requirements.lock']}
        base = ['docker', 'run', '--rm', '-v', 'aura-trivy-cache:/root/.cache/trivy',
                '--mount', f'type=bind,source={source},target=/source,readonly', TRIVY]
        commands = [base + ['fs', '--scanners', 'vuln,secret', '--include-dev-deps', '--format', 'json', '--quiet',
                           '--secret-config', '/source/infra/security/trivy-secret.yaml',
                           '--skip-dirs', '/source/.git,/source/datasets,/source/src/web/aura-web/node_modules,**/bin,**/obj,**/test-results,**/playwright-report', '/source']]
        commands += [base + ['image', '--image-src', 'remote', '--scanners', 'vuln', '--format', 'json', '--quiet', image]
                     for image in args.image]
        for archive in args.image_archive:
            archive=archive.resolve()
            with archive.open('rb') as stream: digest=hashlib.file_digest(stream,'sha256').hexdigest()
            evidence['imageArchives'].append({'file':archive.name,'sha256':digest})
            commands.append(base[:base.index(TRIVY)] + ['--mount',f'type=bind,source={archive},target=/candidate.tar,readonly',TRIVY,
                'image','--input','/candidate.tar','--scanners','vuln','--format','json','--quiet'])
        for command in commands:
            result = subprocess.run(command, capture_output=True, timeout=1800)
            if result.returncode: raise RuntimeError('Scanner failed; report is not acceptance evidence')
            reports.append(redact(json.loads(result.stdout)))
        exceptions = json.loads(args.exceptions.read_text(encoding='utf-8')) if args.exceptions else []
        evidence.update(evaluate(reports, exceptions, dt.datetime.now(dt.timezone.utc).date()))
    except Exception as error:
        evidence.update(status='NOT_TESTED', errorType=type(error).__name__)
        raise
    finally:
        evidence['reports'] = reports
        evidence['completedAt'] = dt.datetime.now(dt.timezone.utc).isoformat()
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    print(json.dumps({'status': evidence['status'], 'blockingCount': len(evidence['blocking']),
                      'exceptionCount': len(evidence['exceptions']), 'report': str(args.output)}))
    return 0 if evidence['status'] == 'PASS' else 1


if __name__ == '__main__': raise SystemExit(main())
