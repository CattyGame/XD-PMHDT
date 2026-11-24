import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import urllib.error

import reader_capacity as readers


class ReaderCapacityTests(unittest.TestCase):
    def test_every_reader_must_validate_the_requested_resource(self):
        with patch.object(readers, 'firebase_token', return_value='test-only'):
            result=readers.measure('http://localhost', ['one','two'], 2, 3, 1,
                lambda base, token, path: {'analysisId':path.rsplit('/',1)[1]})
            self.assertEqual('PASS',result['status'])
            self.assertEqual(6,result['measuredRequests'])
            self.assertEqual(2,len(result['readers']))
            bad=readers.measure('http://localhost', ['one'], 2, 3, 0,
                lambda *args: {'analysisId':'another-tenant-resource'})
            self.assertEqual('FAIL',bad['status'])
            self.assertEqual(6,bad['failedRequests'])

    def test_unauthorized_responses_never_count_as_success(self):
        def denied(*args):raise urllib.error.HTTPError('http://localhost',403,'denied',{},None)
        with patch.object(readers,'firebase_token',return_value='test-only'):
            result=readers.measure('http://localhost',['one'],1,2,0,denied)
        self.assertEqual('FAIL',result['status'])
        self.assertEqual(2,result['failedRequests'])
        self.assertEqual(403,result['readers'][0]['errors'][0]['httpStatus'])


if __name__=='__main__':unittest.main()
