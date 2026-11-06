import unittest
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from pvz_ai.session_view import SessionView


class SessionViewTests(unittest.TestCase):
    def test_shovel_availability_and_unknown_diagnostics_survive_compact_and_delta(self):
        view = SessionView()
        status = {'enabled': True, 'reason': 'ready', 'manager_ready': True, 'config_ready': True,
                  'shovel_show': False, 'battle_running': True, 'native_can_use': True}
        result = view.render({'ok': True, 'kind': 'full', 'snapshot_id': 'running', 'state': {
            'encoding': 'shared-json-v1', 'definitions': {}, 'root': {'shovel_enabled': True, 'shovel_status': status}}})
        self.assertTrue(result['state']['shovel_enabled'])
        self.assertEqual(result['state']['shovel_status'], status)
        unavailable = {**status, 'enabled': False, 'reason': 'manager_unavailable', 'manager_ready': False,
                       'config_ready': False, 'shovel_show': None, 'native_can_use': None}
        result = view.render({'ok': True, 'kind': 'delta', 'snapshot_id': 'unavailable', 'base_snapshot_id': 'running',
                              'changes': {'shovel_enabled': False, 'shovel_status': unavailable}, 'removed_fields': []})
        self.assertEqual(result['state']['shovel_status'], unavailable)
        self.assertFalse(result['state']['shovel_enabled'])
        self.assertIsNone(result['state']['shovel_status']['native_can_use'])

    def test_selection_capacity_and_locks_survive_full_compact_delta_and_exit(self):
        view = SessionView()
        selection = {'selecting_cards': True, 'selection_capacity': 10, 'chosen_cards': ['Fixed'],
                     'chosen_card_details': [{'content_id': 'Fixed', 'locked': True}],
                     'selection_edit_enabled': True, 'selection_can_add': True, 'selection_can_start': True}
        reply = {'ok': True, 'kind': 'full', 'snapshot_id': 'pick', 'state': {
            'encoding': 'shared-json-v1', 'definitions': {}, 'root': selection}}
        self.assertEqual(view.render(reply)['state'], selection)
        result = view.render({'ok': True, 'kind': 'delta', 'snapshot_id': 'updated', 'base_snapshot_id': 'pick',
                              'changes': {'selection_capacity': 12}, 'removed_fields': []})
        self.assertEqual(result['state']['selection_capacity'], 12)
        self.assertEqual(result['state']['chosen_card_details'], selection['chosen_card_details'])
        result = view.render({'ok': True, 'kind': 'delta', 'snapshot_id': 'exit', 'base_snapshot_id': 'updated',
                              'changes': {'selection_capacity': None, 'selecting_cards': False},
                              'removed_fields': ['chosen_cards', 'chosen_card_details', 'selection_edit_enabled',
                                                 'selection_can_add', 'selection_can_start']})
        self.assertIsNone(result['state']['selection_capacity'])
        self.assertNotIn('chosen_card_details', result['state'])
        self.assertIsNone(SessionView().render({'ok': True, 'state': {'selection_capacity': None}})['state']['selection_capacity'])

    def test_compact_and_delta_are_decoded_without_fabricating_empty_board(self):
        view = SessionView()
        wire = {'ok': True, 'kind': 'full', 'snapshot_id': 'a', 'state': {'encoding': 'shared-json-v1', 'definitions': {}, 'root': {'scene': 'TowerDefense', 'running': False, 'mode': 'realtime', 'cells': None, 'entities': None}}}
        rendered = view.render(wire)
        self.assertEqual(rendered['state']['mode'], 'realtime')
        self.assertFalse(rendered['state']['running'])
        self.assertNotIn('plants', rendered['state'])
        self.assertIn('cells', rendered['omitted_state_fields'])
        self.assertIn('entities', rendered['omitted_state_fields'])
        view.render({'ok': True, 'state': {'sun': 99}})
        rendered = view.render({'ok': True, 'kind': 'delta', 'snapshot_id': 'b', 'base_snapshot_id': 'a', 'removed_fields': [], 'changes': {'sun': 50}})
        self.assertEqual(rendered['state']['sun'], 50)
        self.assertEqual(rendered['state']['scene'], 'TowerDefense')
        self.assertEqual(wire['state']['root']['cells'], None)
        with self.assertRaises(ValueError):
            view.render({'ok': True, 'kind': 'delta', 'base_snapshot_id': 'wrong'})

    def test_support_plants_and_signed_ids_are_kept(self):
        result = SessionView().render({'ok': True, 'state': {'cards': [{'id': -1, 'content_id': 'PlantPot', 'cost': 0, 'available': True}], 'entities': [{'kind': 'plant', 'content_id': 'PlantPot', 'column': 1, 'row': 1, 'hp': 100, 'dead': False}]}})
        self.assertEqual(result['state']['cards'][0][:2], [-1, 'PlantPot'])
        self.assertEqual(result['state']['plants'][0][0], 'PlantPot')

    def test_falling_source_unknown_cost_and_release_state_survive_terminal_view(self):
        result = SessionView().render({'ok': True, 'state': {'cards': [{'id': -22, 'content_id': 'PlantSeaNut', 'cost': None, 'available': False, 'source': 'falling_card'}], 'card_selection': {'status': 'clear', 'native_object_id': None}}})
        self.assertEqual(result['state']['cards'][0], [-22, 'PlantSeaNut', None, None, False, 'falling_card'])
        self.assertEqual(result['state']['card_selection']['status'], 'clear')


if __name__ == '__main__': unittest.main()
