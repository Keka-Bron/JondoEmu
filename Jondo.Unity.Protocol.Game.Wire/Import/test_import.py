import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('game_import', Path(__file__).with_name('import-game.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

VERSION = '3.6.10.10'
SOURCE = '''syntax = "proto3";
import "google/protobuf/any.proto";
message outer.root { oneof route { outer.event notice = 1; outer.request ask = 2; outer.response reply = 3; } }
message outer.event { google.protobuf.wellknowntypes.Any content = 1; }
message outer.request { google.protobuf.wellknowntypes.Any content = 1; int32 id = 2; }
message outer.response { google.protobuf.wellknowntypes.Any content = 1; int32 id = 2; }
message outer.example {
  int32 count = 1;
  outer.mode state = 2;
  optional string unused = 3;
  repeated sint64 ids = 4 [packed = false];
  map<int32, int> entries = 5;
  oneof extra { int detail = 6; bool flag = 7; }
}
message int { bytes data = 1; }
enum outer.mode { ZERO = 0; ACTIVE = 3; NEGATIVE = -3; }
'''


def mapping():
    return {
        'stableNamespace': 'Jondo.Unity.Protocol.Game',
        'generatedNamespace': 'Jondo.Unity.Protocol.Game.Wire',
        'className': 'GameBindings', 'conversionException': 'GameConversionException',
        'messages': {'outer.example': {'name': 'ExampleRequest', 'kind': 'request',
                                      'fields': {'count': 'Count', 'state': 'State'}}},
        'enums': {'outer.mode': {'name': 'ExampleState', 'values': {'ZERO': 'None', 'ACTIVE': 'Active'}}},
        'gameEnvelope': {
            'Message': {'alias': 'outer.root', 'fields': {'notice': 'event', 'ask': 'request', 'reply': 'response'}},
            'Event': {'alias': 'outer.event', 'fields': {'content': 'content'}},
            'Request': {'alias': 'outer.request', 'fields': {'content': 'content', 'id': 'uid'}},
            'Response': {'alias': 'outer.response', 'fields': {'content': 'content', 'id': 'uid'}}},
        'wireAliases': {'outer.example': 'public_example'}
    }


def inputs(root, bindings=None):
    schema, bindings_file = root / 'input.proto', root / 'bindings.json'
    schema.write_text(SOURCE, encoding='utf-8')
    bindings_file.write_text(json.dumps(mapping() if bindings is None else bindings), encoding='utf-8')
    return schema, bindings_file


class ImportTests(unittest.TestCase):
    def test_normalization_preserves_structure_presence_and_attributes(self):
        result = module.Schema(SOURCE).normalized()
        self.assertIn('message int_', result)
        self.assertIn('optional string unused = 3', result)
        self.assertIn('repeated sint64 ids = 4 [ packed = false ]', result)
        self.assertIn('map<int32, int_> entries = 5', result)
        self.assertIn('oneof extra', result)
        self.assertIn('int_ detail = 6', result)
        self.assertIn('google.protobuf.Any content = 1', result)
        self.assertIn('NEGATIVE = -3', result)
        self.assertEqual(result, module.Schema(result).normalized())

    def test_flat_collisions_duplicate_mapping_keys_and_unsupported_syntax_fail(self):
        with self.assertRaisesRegex(ValueError, 'collision'):
            module.Schema('message a.x {} message b.x {}').normalized()
        with self.assertRaisesRegex(ValueError, 'collision'):
            module.Schema('message int {} message int_ {}').normalized()
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            json.loads('{"messages": {}, "Messages": {}}', object_pairs_hook=module.unique)
        with self.assertRaises(ValueError):
            module.Schema('syntax = "proto2";')
        with self.assertRaises(ValueError):
            module.Schema('option java_package = "other";')

    def test_explicit_file_import_maintains_models_and_normalizes_mapping_references(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            schema, bindings_file = inputs(root)
            destination = root / 'wire'
            model = root / 'Jondo.Unity.Protocol.Game/Messages.cs'
            model.parent.mkdir()
            model.write_text('// independently maintained source', encoding='utf-8')
            module.import_sources(schema, bindings_file, destination, VERSION)
            result = json.loads((destination / f'Mappings/game-{VERSION}.json').read_text())
            self.assertEqual({'count': 'Count', 'state': 'State'}, result['messages']['example']['fields'])
            self.assertNotIn('ignoredWireFields', result['messages']['example'])
            self.assertNotIn('int_', result['messages'])
            self.assertEqual('root', result['gameEnvelope']['Message']['alias'])
            self.assertEqual('event_', result['gameEnvelope']['Event']['alias'])
            self.assertEqual('public_example', result['wireAliases']['example'])
            self.assertEqual('int', result['wireAliases']['int_'])
            installed_schema = destination / f'Schemas/game-{VERSION}.proto'
            raw = module.Schema(installed_schema.read_text())
            self.assertEqual(7, len(raw.messages['example']))
            self.assertIn('int_', raw.messages)
            self.assertEqual('// independently maintained source', model.read_text())

            provenance = json.loads((destination / f'Import/game-{VERSION}.provenance.json').read_text())
            self.assertEqual(VERSION, provenance['protocolVersion'])
            self.assertEqual(hashlib.sha256(schema.read_bytes()).hexdigest(), provenance['sourceSchemaSha256'])
            self.assertEqual(hashlib.sha256(bindings_file.read_bytes()).hexdigest(), provenance['sourceMappingSha256'])
            for kind in ('schema', 'mapping'):
                path = Path(provenance[kind])
                self.assertFalse(path.is_absolute())
                self.assertEqual(hashlib.sha256((destination / path).read_bytes()).hexdigest(), provenance[kind + 'Sha256'])
            self.assertNotIn(str(root), json.dumps(provenance))

            # Already normalized files are valid inputs; aliases/case bindings are not lost.
            other = root / 'second-output'
            module.import_sources(installed_schema, destination / f'Mappings/game-{VERSION}.json', other, VERSION)
            self.assertEqual(installed_schema.read_bytes(), (other / f'Schemas/game-{VERSION}.proto').read_bytes())
            self.assertEqual(result, json.loads((other / f'Mappings/game-{VERSION}.json').read_text()))

    def test_invalid_mapping_leaves_all_previous_outputs_intact(self):
        for mutation in ('field', 'case', 'policy', 'alias'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                bindings = mapping()
                message = bindings['messages']['outer.example']
                if mutation == 'field':
                    message['fields']['missing'] = 'Count'
                elif mutation == 'case':
                    message['oneofs'] = {'extra': {'member': 'Extra', 'cases': {'count': {'type': 'ExampleCase'}}}}
                elif mutation == 'policy':
                    message['unexpectedPolicy'] = True
                else:
                    bindings['wireAliases']['int'] = 'public_example'
                schema, bindings_file = inputs(root, bindings)
                destination = root / 'wire'
                previous = [destination / f'Schemas/game-{VERSION}.proto', destination / f'Mappings/game-{VERSION}.json',
                            destination / f'Import/game-{VERSION}.provenance.json']
                for path in previous:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text('previous input')
                with self.assertRaises(ValueError):
                    module.import_sources(schema, bindings_file, destination, VERSION)
                for path in previous:
                    self.assertEqual('previous input', path.read_text())

    def test_versions_must_be_numeric_protocol_identifiers(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            schema, bindings_file = inputs(root)
            for version in ('2026-01-01', '../escape', 'latest', '3.6.10'):
                with self.subTest(version=version), self.assertRaisesRegex(ValueError, 'Protocol version'):
                    module.import_sources(schema, bindings_file, root / 'wire', version)
            self.assertFalse((root / 'wire').exists())

    def test_checked_in_profile_checksums_match_the_maintained_artifacts(self):
        project = Path(__file__).resolve().parents[1]
        provenance = json.loads((project / f'Import/game-{VERSION}.provenance.json').read_text())
        self.assertEqual(VERSION, provenance['protocolVersion'])
        for kind in ('schema', 'mapping'):
            self.assertEqual(hashlib.sha256((project / provenance[kind]).read_bytes()).hexdigest(), provenance[kind + 'Sha256'])


if __name__ == '__main__':
    unittest.main()
