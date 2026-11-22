import math
import unittest
import test_real_batch as batch


class NfrSamplesTests(unittest.TestCase):
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


if __name__=='__main__':unittest.main()
