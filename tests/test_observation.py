import unittest
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from pvz_ai.observation import decode_shared, apply_observation
from pvz_ai.mcp_server import handle


class ObservationTests(unittest.TestCase):
    def test_decode_and_delta_preserve_null_and_remove_only_named_fields(self):
        packed = {'encoding': 'shared-json-v1', 'root': {'a': {'$shared_ref': 'x'}, 'unknown': None}, 'definitions': {'x': [1, 2]}}
        baseline, sid = apply_observation({'ok': True, 'kind': 'full', 'snapshot_id': 'one', 'state': packed})
        self.assertEqual(baseline, {'a': [1, 2], 'unknown': None})
        after, _ = apply_observation({'ok': True, 'kind': 'delta', 'base_snapshot_id': 'one', 'snapshot_id': 'two', 'removed_fields': ['a'], 'changes': {'new_unknown': None}}, baseline, sid)
        self.assertEqual(after, {'unknown': None, 'new_unknown': None})
        self.assertEqual(baseline['a'], [1, 2])
        with self.assertRaises(ValueError):
            apply_observation({'ok': True, 'kind': 'delta', 'base_snapshot_id': 'wrong'}, baseline, sid)

    def test_literal_reference_is_not_interpreted(self):
        self.assertEqual(decode_shared({'encoding': 'shared-json-v1', 'root': {'$shared_literal': {'$shared_ref': 'literal'}}, 'definitions': {}}), {'$shared_ref': 'literal'})

    def test_purchase_parameters_are_required_before_any_dispatch(self):
        class Client:
            def __init__(self): self.calls = []
            def request(self, op, **kwargs): self.calls.append((op, kwargs)); return {'ok': True}
        client = Client()
        def call(name, args): return handle({'id': 1, 'method': 'tools/call', 'params': {'name': name, 'arguments': args}}, client)['result']
        self.assertTrue(call('pvz_shop_prepare', {'shop_id': 's'})['isError'])
        self.assertTrue(call('pvz_shop_budget', {'budget_id': 'b', 'max_total': -1, 'allowed_effect_keys': ['Upgrade']})['isError'])
        self.assertEqual(client.calls, [])
        args = {'shop_id': 's', 'item_id': 'i', 'expected_stage': 0, 'expected_price': 50, 'max_spend': 50, 'transaction_id': 'once', 'budget_id': 'b'}
        self.assertFalse(call('pvz_shop_prepare', args)['isError'])
        self.assertEqual(client.calls, [('shop_prepare', args)])


if __name__ == '__main__': unittest.main()
