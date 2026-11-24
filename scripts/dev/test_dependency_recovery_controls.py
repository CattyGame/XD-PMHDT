import json
import unittest
import test_dependency_recovery as drill


class FaultDrillControls(unittest.TestCase):
    def test_shared_or_unlabelled_containers_are_rejected_before_mutation(self):
        for label in [None,'shared-environment']:
            def inspect(command):
                self.assertEqual(['docker','inspect','chosen'],command)
                return json.dumps([{'Id':'id','Config':{'Labels':{'aura.acceptance':label}}}])
            with self.assertRaises(ValueError):drill.require_fixture('chosen','owned-test',inspect)

    def test_exact_owned_label_resolves_immutable_container_id(self):
        self.assertEqual('immutable',drill.require_fixture('chosen','owned-test',lambda _:json.dumps([
            {'Id':'immutable','Config':{'Labels':{'aura.acceptance':'owned-test'}}}])))


if __name__=='__main__':unittest.main()
