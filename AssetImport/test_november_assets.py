"""Synthetic Unity type trees; these tests do not require a client install."""
import runpy
import unittest
from pathlib import Path

from november_assets import map_geometry, normalized, table_rows

importer = runpy.run_path(str(Path(__file__).with_name('import-beta.py')))


class NovemberAssetsTests(unittest.TestCase):
    def test_table_joins_by_reference_id_not_position(self):
        root = {'objectsById': {'m_keys': [21, 2], 'm_values': [{'rid': 8}, {'rid': 6}]},
                'references': {'RefIds': [{'rid': 6, 'data': 'second'}, {'rid': 8, 'data': 'first'}]}}
        self.assertEqual(table_rows(root), {'21': 'first', '2': 'second'})

    def test_cell_eligibility_and_border_destination_order(self):
        def cell(number, **fields):
            return dict(cellNumber=number, **({'mov': 1, 'los': 1, 'farmCell': 0,
                        'havenbagCell': 0, 'red': 0, 'blue': 0} | fields))

        row = dict(id=12, topNeighbourId=1, leftNeighbourId=2, bottomNeighbourId=3, rightNeighbourId=4,
                   cellsData=[cell(88, red=1), cell(89, farmCell=1), cell(90, havenbagCell=1),
                              cell(0), cell(91, mov=0, los=0, blue=1)])
        self.assertEqual(map_geometry(row), dict(walk=[88, 90], fight=[88, 0], opaque=[91],
                                                red=[88], blue=[91], neighbors=[4, 3, 2, 1]))
        row['cellsData'].append(cell(88))
        with self.assertRaisesRegex(ValueError, 'duplicate cell'):
            map_geometry(row)

    def test_nested_effect_uses_serialized_action_id(self):
        source = dict(m_flags=79, baseEffectId=22, actionId=138, diceNum=7,
                      zoneDescr=dict(cellIds=[10], forcedDirection=-1))
        self.assertEqual(importer['spell_effect'](normalized(source)), dict(m_flags=79, baseEffectId=22,
                         effectId=138, diceNum=7, zoneDescr=dict(cellIds=[10], forcedDirection=-1)))

    def test_spell_template_matches_nested_seed_arrays_and_field_names(self):
        source = dict(m_flags=1, boundScriptUsageData=[dict(scriptId=17, spellLevels=[])],
                      basePreviewZoneDescr=dict(cellIds=[3, 4]))
        self.assertEqual(importer['spell_template'](normalized(source)), {
            'm_flags': 1,
            'boundScriptUsageData': {'Array': [{'scriptId': 17, 'spellLevels': {'Array': []}}]},
            'basePreviewZoneDescr': {'cellIds': {'Array': [3, 4]}}})


if __name__ == '__main__':
    unittest.main()
