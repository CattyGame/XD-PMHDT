import copy
import tempfile
import unittest
from pathlib import Path
import transport_tls


class TransportTlsTests(unittest.TestCase):
    def fixture(self, folder):
        names=transport_tls.SERVICES
        for name in names:
            target=folder/name;target.mkdir()
            for file in ('server.crt','server.key','ca.crt','ca-bundle.pem'):
                (target/file).write_text('fixture, not a usable certificate')
        services={name:{'environment':{},'volumes':[]} for name in names}
        services['postgres']['command']=['postgres','-c','max_connections=160']
        services['redis']['command']=['redis-server','--requirepass','fixture-only','--maxmemory','16mb']
        services['ai-core']['command']=['uvicorn','app.main:app','--host','0.0.0.0','--port','8000','--workers','1']
        services['azurite']['command']=['azurite-blob','--blobHost','0.0.0.0','--location','/data']
        for name in transport_tls.APIS:
            services[name]['environment']={'Services__Identity__BaseUrl':'http://identity-api:8080',
                'ConnectionStrings__Postgres':'Host=postgres;Password="quoted;value";Maximum Pool Size=16',
                'ConnectionStrings__Redis':'redis:6379,password=fixture-only'}
        services['analysis-api']['environment']['Storage__ConnectionString']='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=fixture-only;BlobEndpoint=http://azurite:10000/devstoreaccount1;'
        services['analysis-worker']['environment']['BlobStorage__ConnectionString']='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=worker-specific;BlobEndpoint=http://azurite:10000/devstoreaccount1;'
        services['api-gateway']['environment']['ReverseProxy__Clusters__analysis__Destinations__api__Address']='http://analysis-api:8080/'
        return {'services':services}

    def test_all_patient_transport_hops_require_tls_and_keep_pool_and_auth_settings(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);config=self.fixture(home);before=copy.deepcopy(config)
            result=transport_tls.enable(config,home)
            self.assertEqual(before,config)
            for name in transport_tls.APIS:
                env=result['services'][name]['environment']
                self.assertEqual('https://identity-api:8080',env['Services__Identity__BaseUrl'])
                self.assertIn('Password="quoted;value";Maximum Pool Size=16',env['ConnectionStrings__Postgres'])
                self.assertIn('SSL Mode=VerifyFull',env['ConnectionStrings__Postgres'])
                self.assertIn('ssl=true',env['ConnectionStrings__Redis'])
                self.assertEqual('Tls12, Tls13',env['Kestrel__EndpointDefaults__SslProtocols'])
            self.assertEqual('true',result['services']['analysis-worker']['environment']['RabbitMq__UseTls'])
            self.assertEqual('true',result['services']['billing-api']['environment']['BillingSettlement__UseTls'])
            self.assertEqual('true',result['services']['notification-api']['environment']['NotificationDatabase__UseTls'])
            self.assertIn('https://azurite:',result['services']['analysis-worker']['environment']['BlobStorage__ConnectionString'])
            self.assertIn('AccountKey=worker-specific;',result['services']['analysis-worker']['environment']['BlobStorage__ConnectionString'])
            self.assertEqual('https://profile-api:8080',result['services']['analysis-worker']['environment'].get('ProfileClient__BaseUrl'))
            self.assertEqual('https://identity-api:8080',result['services']['analysis-worker']['environment'].get('ServiceAuth__IdentityBaseUrl'))
            self.assertNotIn('--port',result['services']['redis']['command'][:2])
            redis=result['services']['redis']['command']
            self.assertEqual('0',redis[redis.index('--port')+1])
            self.assertIn('ssl=on',result['services']['postgres']['command'])
            self.assertEqual('https://analysis-api:8080/',result['services']['api-gateway']['environment']['ReverseProxy__Clusters__analysis__Destinations__api__Address'])

    def test_missing_certificate_fails_before_producing_a_partial_profile(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);config=self.fixture(home)
            (home/'postgres/server.key').unlink()
            with self.assertRaisesRegex(ValueError,'certificate'):
                transport_tls.enable(config,home)

    def test_implicit_worker_storage_cannot_silently_keep_plaintext_image_defaults(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);config=self.fixture(home)
            del config['services']['analysis-worker']['environment']['BlobStorage__ConnectionString']
            with self.assertRaisesRegex(ValueError,'worker storage'):
                transport_tls.enable(config,home)

    def test_explicit_conflicting_tls_or_camera_endpoint_requires_operator_review(self):
        with tempfile.TemporaryDirectory() as temporary:
            home=Path(temporary);config=self.fixture(home)
            config['services']['profile-api']['environment']['ConnectionStrings__Postgres']+=';SSL Mode=Disable'
            with self.assertRaisesRegex(ValueError,'explicit TLS'):
                transport_tls.enable(config,home)
            config=self.fixture_reset(config)
            config['services']['api-gateway']['environment']['Kestrel__Endpoints__PrivateHttp__Url']='http://+:8080'
            with self.assertRaisesRegex(ValueError,'endpoint'):
                transport_tls.enable(config,home)

    def fixture_reset(self, config):
        config['services']['profile-api']['environment']['ConnectionStrings__Postgres']='Host=postgres'
        return config


if __name__=='__main__':unittest.main()
