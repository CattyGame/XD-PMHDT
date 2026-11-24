"""Derive a private TLS-only Compose profile from a fully rendered local stack."""
import argparse
import copy
import json
import os
from pathlib import Path
import re

APIS=('api-gateway','identity-api','profile-api','analysis-api','billing-api','notification-api')
SERVICES=(*APIS,'analysis-worker','ai-core','postgres','redis','rabbitmq','azurite')


def enable(config, home):
    result=copy.deepcopy(config);services=result['services'];home=home.resolve()
    if not services['analysis-worker'].get('environment',{}).get('BlobStorage__ConnectionString'):
        raise ValueError('Explicit worker storage settings required before TLS conversion')
    infra=Path(__file__).resolve().parents[2]/'infra/security'
    for name in SERVICES:
        service=services[name]
        required=('ca.crt','ca-bundle.pem') if name=='analysis-worker' else ('server.crt','server.key','ca.crt','ca-bundle.pem')
        for file in required:
            if not (home/name/file).is_file():raise ValueError('Missing TLS certificate material for '+name)
        env=service.setdefault('environment',{})
        if any(k.startswith('Kestrel__Endpoints__') for k in env):raise ValueError('Existing Kestrel endpoint requires review')
        service.setdefault('volumes',[]).append({'type':'bind','source':str(home/name),
            'target':'/run/aura-tls','read_only':True,'bind':{'create_host_path':False}})
        env['SSL_CERT_FILE']='/run/aura-tls/ca-bundle.pem'
        for key,value in list(env.items()):
            if not isinstance(value,str):continue
            if key in ('ConnectionStrings__Postgres','ConnectionStrings__OutboxPublisher'):
                if re.search(r'(?:^|;)\s*(?:ssl\s*mode|root\s*certificate|trust\s*server\s*certificate)\s*=',value,re.I):
                    raise ValueError('Existing explicit TLS database setting requires review')
                env[key]=value.rstrip(';')+';SSL Mode=VerifyFull;Root Certificate=/run/aura-tls/ca.crt'
            elif key=='ConnectionStrings__Redis':
                if re.search(r'(?:^|,)\s*ssl\s*=',value,re.I):raise ValueError('Existing explicit TLS Redis setting requires review')
                env[key]=value+',ssl=true,sslHost=redis'
            elif key.endswith(('BaseUrl','Address','IdentityBaseUrl','PublicBlobEndpoint')) or key in ('Storage__ConnectionString','BlobStorage__ConnectionString'):
                env[key]=re.sub(r'http://('+('|'.join(SERVICES))+r')(?=[:/])',r'https://\1',value)
                if key in ('Storage__ConnectionString','BlobStorage__ConnectionString'):
                    env[key]=env[key].replace('DefaultEndpointsProtocol=http;','DefaultEndpointsProtocol=https;')
                if key in ('Analysis__Report__PublicApiBaseUrl','Storage__PublicBlobEndpoint'):
                    env[key]=re.sub(r'^http://','https://',env[key])
        if name in APIS:
            env.update(ASPNETCORE_URLS='https://+:8080',ASPNETCORE_HTTP_PORTS='',
                Kestrel__Certificates__Default__Path='/run/aura-tls/server.crt',
                Kestrel__Certificates__Default__KeyPath='/run/aura-tls/server.key',
                Kestrel__EndpointDefaults__SslProtocols='Tls12, Tls13')
    for name,prefix in [('analysis-api','RabbitMq'),('analysis-worker','RabbitMq'),('billing-api','BillingSettlement'),('notification-api','RabbitMQ')]:
        services[name]['environment'][prefix+'__UseTls']='true'
    services['analysis-worker']['environment']['AiClient__BaseUrl']='https://ai-core:8000'
    services['analysis-worker']['environment']['ProfileClient__BaseUrl']='https://profile-api:8080'
    services['analysis-worker']['environment'].setdefault('ServiceAuth__IdentityBaseUrl','https://identity-api:8080')
    services['notification-api']['environment'].update(NotificationDatabase__UseTls='true',NotificationDatabase__RootCertificate='/run/aura-tls/ca.crt')
    ai=services['ai-core'];ai['command']+=['--ssl-certfile','/run/aura-tls/server.crt','--ssl-keyfile','/run/aura-tls/server.key']
    ai['healthcheck']={'test':['CMD','python','-c',"import urllib.request;urllib.request.urlopen('https://ai-core:8000/health',timeout=5)"],
        'interval':'10s','timeout':'6s','retries':12,'start_period':'60s'}
    pg=services['postgres'];pg['command']=pg.get('command') or ['postgres']
    pg['command']+=['-c','ssl=on','-c','ssl_min_protocol_version=TLSv1.2','-c','ssl_cert_file=/run/aura-tls/server.crt',
        '-c','ssl_key_file=/var/lib/postgresql/server.key','-c','hba_file=/etc/postgresql/pg_hba_tls.conf']
    pg['entrypoint']=['/bin/sh','-c','set -eu; install -o postgres -g postgres -m 600 /run/aura-tls/server.key /var/lib/postgresql/server.key; exec docker-entrypoint.sh "$@"','--']
    pg['volumes'].append({'type':'bind','source':str(infra/'pg_hba_tls.conf'),'target':'/etc/postgresql/pg_hba_tls.conf','read_only':True})
    redis=services['redis'];redis['command']+=['--port','0','--tls-port','6379','--tls-cert-file','/run/aura-tls/server.crt',
        '--tls-key-file','/run/aura-tls/server.key','--tls-ca-cert-file','/run/aura-tls/ca.crt','--tls-auth-clients','no','--tls-protocols','TLSv1.2 TLSv1.3']
    redis['healthcheck']={'test':['CMD','redis-cli','-h','redis','--tls','--cacert','/run/aura-tls/ca.crt','ping'],
        'interval':'10s','timeout':'5s','retries':5}
    services['rabbitmq']['volumes'].append({'type':'bind','source':str(infra/'rabbitmq_tls.conf'),
        'target':'/etc/rabbitmq/rabbitmq.conf','read_only':True})
    services['azurite']['command']+=['--cert','/run/aura-tls/server.crt','--key','/run/aura-tls/server.key']
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config',type=Path)
    parser.add_argument('--certificates',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();repo=Path(__file__).resolve().parents[2]
    if any(path.resolve().is_relative_to(repo) for path in (args.config,args.certificates,args.output)):
        raise ValueError('Rendered config, private certificates and output must stay outside the repository')
    result=enable(json.loads(args.config.read_text(encoding='utf-8-sig')),args.certificates)
    # Exclusive creation prevents overwriting an environment currently used by a running workload.
    with os.fdopen(os.open(args.output,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'w',encoding='utf-8') as file:
        json.dump(result,file,indent=2)
    print('TLS profile written outside Git; validate Compose and actual trusted/negative handshakes before deployment')


if __name__=='__main__':main()
