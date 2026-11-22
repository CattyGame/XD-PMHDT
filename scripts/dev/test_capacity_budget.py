import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('capacity_budget',Path(__file__).with_name('capacity_budget.py'))
budget=importlib.util.module_from_spec(spec);spec.loader.exec_module(budget)


class CapacityBudgetTests(unittest.TestCase):
    def config(self):
        services={name:{'environment':{'ConnectionStrings__Postgres':'Host=db;Password="test;Maximum Pool Size=999";Maximum Pool Size=16'},
            'mem_limit':'512m'} for name in ['identity-api','profile-api','analysis-api','billing-api']}
        services['analysis-api']['environment'].update(Analysis__ConcurrentUploads='4',Outbox__Enabled='true',
            ConnectionStrings__OutboxPublisher='Host=db;Maximum Pool Size=8')
        services['notification-api']={'environment':{'NotificationDatabase__MaximumPoolSize':'16'},'mem_limit':'512m'}
        services['analysis-worker']={'environment':{'ConnectionStrings__Postgres':'Host=db;Maximum Pool Size=12'},
            'deploy':{'replicas':4,'resources':{'limits':{'memory':'1g'}}}}
        services['postgres']={'command':['postgres','-c','max_connections=160'],'mem_limit':'1g'}
        return {'services':services}

    def test_counts_secondary_outbox_pool_and_four_replicas(self):
        result=budget.evaluate(self.config())
        self.assertEqual(136,result['maximumApplicationConnections'])
        self.assertEqual('PASS',result['status'])

    def test_rejects_hidden_default_pool_and_replica_overcommit(self):
        cfg=self.config();cfg['services']['identity-api']['environment']['ConnectionStrings__Postgres']='Host=db'
        self.assertEqual('FAIL',budget.evaluate(cfg)['status'])
        cfg=self.config();cfg['services']['analysis-worker']['deploy']['replicas']=8
        self.assertEqual('FAIL',budget.evaluate(cfg)['status'])

    def test_admission_needs_headroom_and_every_service_needs_memory_limit(self):
        cfg=self.config();cfg['services']['analysis-api']['environment']['Analysis__ConcurrentUploads']='8'
        self.assertEqual('FAIL',budget.evaluate(cfg)['status'])
        cfg=self.config();del cfg['services']['profile-api']['mem_limit']
        self.assertEqual('FAIL',budget.evaluate(cfg)['status'])

    def test_memory_budget_counts_replica_limits_and_requires_host_headroom(self):
        cfg=self.config()
        result=budget.evaluate(cfg,host_memory_bytes=8*1024**3)
        self.assertEqual(7680*1024**2,result['memoryBudget']['maximumContainerBytes'])
        self.assertEqual('FAIL',result['status'])
        self.assertEqual('PASS',budget.evaluate(cfg,host_memory_bytes=10*1024**3)['status'])
        self.assertEqual('NOT_TESTED',budget.evaluate(cfg)['memoryBudget']['status'])


if __name__=='__main__':unittest.main()
