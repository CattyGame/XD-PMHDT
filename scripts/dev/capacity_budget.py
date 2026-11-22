"""Check the rendered full-stack Compose pool budget without printing connection strings."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def pool(connection):
    # Consume quoted values whole: a semicolon in a password is not another setting.
    values={m[1].strip().lower().replace(' ',''):m[2].strip().strip('"\'')
            for m in re.finditer(r'''(?:^|;)\s*([^=;]+)=\s*("(?:[^"]|"")*"|'(?:[^']|'')*'|[^;]*)''',connection)}
    size=int(values.get('maximumpoolsize',values.get('maxpoolsize','100')))
    if size<1 or values.get('pooling','true').lower()!='true':raise ValueError('Invalid pool settings')
    return size, 'maximumpoolsize' in values or 'maxpoolsize' in values


def evaluate(config,headroom=16):
    services=config['services'];issues=[];rows=[]
    command=services['postgres'].get('command',[])
    if isinstance(command,list):command=' '.join(command)
    match=re.search(r'\bmax_connections=(\d+)\b',command)
    maximum=int(match[1]) if match else 100
    for name in ['identity-api','profile-api','analysis-api','billing-api','notification-api','analysis-worker']:
        service=services[name];env=service.get('environment',{})
        replicas=int(service.get('deploy',{}).get('replicas',1))
        if replicas<1:raise ValueError('Replica count must be positive')
        limits=service.get('deploy',{}).get('resources',{}).get('limits',{})
        if not (service.get('mem_limit') or limits.get('memory')):issues.append(name+': memory limit missing')
        pools=[]
        if name=='notification-api':
            value=env.get('NotificationDatabase__MaximumPoolSize')
            if value is None:issues.append(name+': explicit pool limit missing')
            pools.append(int(value or 100))
        else:
            connections=[env['ConnectionStrings__Postgres']]
            if name=='analysis-api' and str(env.get('Outbox__Enabled','false')).lower()=='true':
                connections.append(env['ConnectionStrings__OutboxPublisher'])
            for connection in connections:
                size,explicit=pool(connection);pools.append(size)
                if not explicit:issues.append(name+': implicit default pool is not budgeted')
        if any(p<1 for p in pools):raise ValueError('Pool size must be positive')
        if name=='analysis-api' and pools[0]<2*int(env.get('Analysis__ConcurrentUploads',4))+4:
            issues.append(name+': insufficient admission/dependency headroom')
        rows.append(dict(service=name,replicas=replicas,poolsPerProcess=pools,maximumConnections=replicas*sum(pools)))
    total=sum(row['maximumConnections'] for row in rows)
    if total+headroom>maximum:issues.append('Application pools plus operational headroom exceed max_connections')
    return dict(status='FAIL' if issues else 'PASS',maximumApplicationConnections=total,
                operationalHeadroom=headroom,postgresMaxConnections=maximum,services=rows,issues=issues,
                scope='Configuration upper bound; verify actual process/replica counts and memory under load separately')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config',type=Path,help='docker compose config --format json output, kept outside Git')
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();raw=args.config.read_bytes();result=evaluate(json.loads(raw.decode('utf-8-sig')))
    result['configurationSha256']=hashlib.sha256(raw).hexdigest()
    args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result));return 0 if result['status']=='PASS' else 1


if __name__=='__main__':raise SystemExit(main())
