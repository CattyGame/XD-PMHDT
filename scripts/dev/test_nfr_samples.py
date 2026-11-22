import math
import io
import json
import urllib.error
from unittest.mock import patch
import unittest
import tempfile
from pathlib import Path
import test_real_batch as batch


class NfrSamplesTests(unittest.TestCase):
    def test_checkpoint_is_readable_and_keeps_incomplete_status(self):
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory)/'evidence.json'
            batch.save_evidence(output,{'status':'RUNNING','declared':100})
            self.assertEqual('RUNNING',json.loads(output.read_text())['status'])
            batch.save_evidence(output,{'status':'FAIL','declared':100})
            self.assertEqual('FAIL',json.loads(output.read_text())['status'])
            self.assertFalse(output.with_suffix('.pending').exists())
    def test_only_explicit_busy_response_is_retried_with_same_operation(self):
        def busy(code):return urllib.error.HTTPError('http://localhost',409,'busy',{},io.BytesIO(json.dumps(
            {'error':{'code':code,'retryable':True}}).encode()))
        calls=[]
        def operation():
            calls.append(1)
            if len(calls)==1:raise busy('ERR_REQUEST_IN_PROGRESS')
            return 'accepted'
        with patch.object(batch.time,'sleep'):
            self.assertEqual('accepted',batch.retry_busy_upload(operation,batch.time.monotonic()+30))
        self.assertEqual(2,len(calls))
        with self.assertRaises(urllib.error.HTTPError):
            batch.retry_busy_upload(lambda:(_ for _ in ()).throw(busy('ERR_IDEMPOTENCY_KEY_REUSED')),batch.time.monotonic()+30)
    def test_cold_is_separate_and_p95_is_nearest_rank(self):
        value=batch.latency_summary([900]+list(range(1,101)))
        self.assertEqual(900,value['coldMs'])
        self.assertEqual(95,value['warm']['p95Ms'])
        self.assertEqual(100,value['warm']['count'])
        self.assertIsNone(batch.latency_summary([9])['warm']['p95Ms'])
        with self.assertRaises(ValueError):batch.latency_summary([math.nan])

    def test_unavailable_is_counted_and_never_interpreted_as_zero_or_low_risk(self):
        detail={'processingStatus':'COMPLETED','result':{'isMock':False,'processingTimeMs':8,
            'metrics':{'avRatio':None},'riskAvailable':False,'riskLevel':None,
            'research':{'avr':{'measurement_available':False,'unavailable_reason':'NOT_ENOUGH_VESSELS'}}}}
        self.assertEqual('UNAVAILABLE',batch.classify_real_result(detail)['measurement'])
        detail['result']['metrics']['avRatio']=0
        with self.assertRaises(ValueError):batch.classify_real_result(detail)

    def test_mock_missing_and_nonterminal_results_cannot_pass_real_gate(self):
        for detail in [{'processingStatus':'PROCESSING'}, {'processingStatus':'COMPLETED','result':{'isMock':True}}]:
            with self.assertRaises(ValueError):batch.classify_real_result(detail)
        self.assertEqual('QUALITY_FAILURE',batch.classify_real_result({'processingStatus':'FAILED',
            'error':{'code':'LOW_QUALITY'}})['measurement'])

    def test_canonical_low_quality_status_is_a_terminal_quality_failure(self):
        self.assertEqual('QUALITY_FAILURE',batch.classify_real_result({'processingStatus':'LOW_QUALITY',
            'error':{'code':'ERR_IMAGE_LOW_QUALITY'}})['measurement'])


if __name__=='__main__':unittest.main()
