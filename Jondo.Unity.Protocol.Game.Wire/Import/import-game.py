#!/usr/bin/env python3
"""Import an explicit proto3 schema and JondoEmu C# binding mapping.

This maintenance command is independent of normal MSBuild generation. Stable
C# models remain maintained source; the subsequent build validates their types.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path


def wire_name(name):
    alias = name.split('.')[-1]
    keywords = set('abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while'.split())
    return alias + '_' if alias in keywords else alias


def unique(pairs):
    result, seen = {}, set()
    for key, value in pairs:
        if key.casefold() in seen:
            raise ValueError(f'Duplicate mapping key: {key}')
        seen.add(key.casefold())
        result[key] = value
    return result


class Schema:
    """The schema subset used by Game: messages, enums, maps, oneofs and presence.

    Dotted declarations are flattened with collision checks. Unsupported syntax
    is rejected rather than silently discarded. Field attributes are retained.
    """
    def __init__(self, text):
        text = re.sub(r'//[^\n]*', '', text)
        self.tokens = re.findall(r'"[^"\n]*"|[A-Za-z_][\w.]*|-?\d+|[^\s]', text)
        self.i = 0
        self.messages, self.enums = {}, {}
        self.imports = []
        self.package = ''
        while self.i < len(self.tokens):
            token = self.take()
            if token == 'syntax':
                self.expect('=')
                self.expect('"proto3"')
                self.expect(';')
            elif token == 'package':
                self.package = self.take()
                self.expect(';')
            elif token == 'option':
                self.expect('csharp_namespace')
                self.expect('=')
                self.expect('"Jondo.Unity.Protocol.Game.Wire.Messages"')
                self.expect(';')
            elif token == 'import':
                self.imports.append(self.take())
                self.expect(';')
            elif token in ('message', 'enum'):
                self.declaration(token)
            else:
                raise ValueError(f'Unsupported schema token {token}')

    def take(self):
        if self.i == len(self.tokens):
            raise ValueError('Unexpected end of schema')
        value = self.tokens[self.i]
        self.i += 1
        return value

    def expect(self, value):
        actual = self.take()
        if actual != value:
            raise ValueError(f'Expected {value}, got {actual}')

    def declaration(self, kind, parent=''):
        name = self.take()
        if parent and '.' not in name:
            name = parent + '.' + name
        self.expect('{')
        if name in self.messages or name in self.enums:
            raise ValueError(f'Duplicate declaration {name}')
        fields = (self.messages if kind == 'message' else self.enums)[name] = []
        while True:
            token = self.take()
            if token == '}':
                break
            if token in ('message', 'enum'):
                self.declaration(token, name)
            elif token == 'oneof':
                group = self.take()
                self.expect('{')
                while True:
                    token = self.take()
                    if token == '}':
                        break
                    fields.append(self.field(token, group))
            elif kind == 'enum':
                self.expect('=')
                fields.append((token, self.take()))
                self.expect(';')
            else:
                fields.append(self.field(token, None))

    def field(self, token, group):
        label = token if token in ('optional', 'repeated') else ''
        ty = self.take() if label else token
        if ty == 'map':
            self.expect('<')
            key = self.take()
            self.expect(',')
            value = self.take()
            self.expect('>')
            ty = f'map<{key}, {value}>'
        name = self.take()
        self.expect('=')
        tag = self.take()
        attrs = []
        while True:
            token = self.take()
            if token == ';':
                break
            attrs.append(token)
        return dict(name=name, type=ty, label=label, tag=tag, attrs=' '.join(attrs), group=group)

    def resolve(self, alias, declarations):
        alias = alias.lstrip('.')
        if self.package and alias.startswith(self.package + '.'):
            alias = alias[len(self.package) + 1:]
        if alias in declarations:
            return alias
        matches = [name for name in declarations if wire_name(name) == alias]
        if len(matches) != 1:
            raise ValueError(f'Unknown or ambiguous wire type: {alias}')
        return matches[0]

    def normalized(self):
        declarations = self.messages | self.enums
        aliases = {name: wire_name(name) for name in declarations}
        if len(set(aliases.values())) != len(aliases):
            raise ValueError('Flat protobuf name collision')
        def typename(name):
            if name in ('google.protobuf.wellknowntypes.Any', 'google.protobuf.Any'):
                return 'google.protobuf.Any'
            if name.startswith('map<'):
                return 'map<' + ', '.join(typename(x.strip()) for x in name[4:-1].split(',')) + '>'
            if name in {'double', 'float', 'int32', 'int64', 'uint32', 'uint64', 'sint32', 'sint64',
                        'fixed32', 'fixed64', 'sfixed32', 'sfixed64', 'bool', 'string', 'bytes'}:
                return name
            return aliases[self.resolve(name, declarations)]
        lines = ['syntax = "proto3";', 'option csharp_namespace = "Jondo.Unity.Protocol.Game.Wire.Messages";']
        lines += [f'import {x};' for x in self.imports]
        for name, fields in self.enums.items():
            lines += [f'enum {aliases[name]} {{'] + [f'  {n} = {v};' for n, v in fields] + ['}']
        for name, fields in self.messages.items():
            lines.append(f'message {aliases[name]} {{')
            for group in dict.fromkeys(f['group'] for f in fields):
                if group:
                    lines.append(f'  oneof {group} {{')
                for f in fields:
                    if f['group'] == group:
                        lines.append(f"    {f['label']} {typename(f['type'])} {f['name']} = {f['tag']} {f['attrs']};")
                if group:
                    lines.append('  }')
            lines.append('}')
        return '\n'.join(lines) + '\n'


def check_keys(value, allowed):
    if not isinstance(value, dict) or set(value) - set(allowed):
        raise ValueError(f'Unknown mapping keys; expected only {", ".join(allowed)}')


def normalize_mapping(schema, mapping):
    """Check wire references; Roslyn/descriptor validation remains the build's job."""
    check_keys(mapping, ('stableNamespace', 'generatedNamespace', 'className', 'conversionException',
                         'messages', 'enums', 'gameEnvelope', 'wireAliases'))
    expected = {'stableNamespace': 'Jondo.Unity.Protocol.Game',
                'generatedNamespace': 'Jondo.Unity.Protocol.Game.Wire',
                'className': 'GameBindings', 'conversionException': 'GameConversionException'}
    if any(mapping.get(key) != value for key, value in expected.items()):
        raise ValueError('Expected a Jondo Game C# binding mapping; see docs/game-protocol.md')

    def fields_for(alias):
        name = schema.resolve(alias, schema.messages)
        return name, {f['name']: f for f in schema.messages[name]}

    messages = {}
    for alias, binding in mapping['messages'].items():
        check_keys(binding, ('name', 'kind', 'fields', 'oneofs', 'ignoredStableMembers', 'defaultOnAbsence', 'defaultOneofs'))
        name, fields = fields_for(alias)
        if set(binding.get('fields', {})) - fields.keys() or set(binding.get('defaultOnAbsence', [])) - fields.keys():
            raise ValueError(f'Unknown mapped wire field in {alias}')
        for field in binding.get('fields', {}):
            if fields[field]['group']:
                raise ValueError(f'{alias}.{field} must be bound through its oneof')
        groups = {f['group'] for f in fields.values() if f['group']}
        oneofs, defaults = binding.get('oneofs', {}), binding.get('defaultOneofs', {})
        if (set(oneofs) | set(defaults)) - groups or set(oneofs) & set(defaults):
            raise ValueError(f'Unknown or conflicting oneof policies in {alias}')
        for group, oneof in oneofs.items():
            check_keys(oneof, ('member', 'noneType', 'cases'))
            for field, case in oneof.get('cases', {}).items():
                if field not in fields or fields[field]['group'] != group:
                    raise ValueError(f'Unknown oneof branch in {alias}.{group}: {field}')
                check_keys(case, ('type', 'member'))
        for group, branch in defaults.items():
            if branch not in fields or fields[branch]['group'] != group:
                raise ValueError(f'Invalid default branch in {alias}.{group}')
        if wire_name(name) in messages:
            raise ValueError(f'Duplicate message binding: {name}')
        messages[wire_name(name)] = binding

    enums = {}
    for alias, binding in mapping.get('enums', {}).items():
        check_keys(binding, ('name', 'values'))
        name = schema.resolve(alias, schema.enums)
        if set(binding['values']) - {n for n, _ in schema.enums[name]}:
            raise ValueError(f'Unknown mapped enum value in {alias}')
        if wire_name(name) in enums:
            raise ValueError(f'Duplicate enum binding: {name}')
        enums[wire_name(name)] = binding

    envelopes = mapping['gameEnvelope']
    if set(envelopes) != {'Message', 'Request', 'Response', 'Event'}:
        raise ValueError('Expected all four Game envelope roles')
    for binding in envelopes.values():
        check_keys(binding, ('alias', 'fields'))
        name, fields = fields_for(binding['alias'])
        if set(binding['fields']) - fields.keys():
            raise ValueError(f'Unknown mapped envelope field in {name}')
        binding['alias'] = wire_name(name)

    aliases = {wire_name(name): name for name in schema.messages if wire_name(name) != name}
    for alias, original in mapping.get('wireAliases', {}).items():
        name = schema.resolve(alias, schema.messages)
        if not isinstance(original, str) or not re.fullmatch(r'[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*', original):
            raise ValueError(f'Invalid wire alias: {original}')
        aliases[wire_name(name)] = original
    identities = [aliases.get(wire_name(name), wire_name(name)) for name in schema.messages]
    if len(set(identities)) != len(identities):
        raise ValueError('Duplicate original wire aliases')
    return {**mapping, 'messages': messages, 'enums': enums, 'wireAliases': aliases}


def import_sources(schema_path, mapping_path, destination, version):
    if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+', version):
        raise ValueError('Protocol version must have four numeric components, e.g. 3.6.10.10')
    schema_bytes, mapping_bytes = schema_path.read_bytes(), mapping_path.read_bytes()
    schema = Schema(schema_bytes.decode('utf-8'))
    normalized = schema.normalized()
    mapping = normalize_mapping(schema, json.loads(mapping_bytes, object_pairs_hook=unique))
    bindings = json.dumps(mapping, indent=2) + '\n'
    schema_name, mapping_name = f'Schemas/game-{version}.proto', f'Mappings/game-{version}.json'
    provenance = {'protocolVersion': version, 'schema': schema_name, 'mapping': mapping_name,
                  'schemaSha256': hashlib.sha256(normalized.encode()).hexdigest(),
                  'mappingSha256': hashlib.sha256(bindings.encode()).hexdigest(),
                  'sourceSchemaSha256': hashlib.sha256(schema_bytes).hexdigest(),
                  'sourceMappingSha256': hashlib.sha256(mapping_bytes).hexdigest()}
    files = {destination / schema_name: normalized, destination / mapping_name: bindings,
             destination / f'Import/game-{version}.provenance.json': json.dumps(provenance, indent=2) + '\n'}
    # Read and validate both inputs before replacing any output. Stable model
    # source is not an import output. Paths in provenance are project-relative.
    for path, text in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8', newline='\n')
    print(f'Imported Game {version}: {len(schema.messages)} wire messages, {len(mapping["messages"])} mapped payload/common types.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--schema', type=Path, required=True, help='Input proto3 schema')
    parser.add_argument('--mapping', type=Path, required=True, help='JondoEmu C# binding mapping JSON')
    parser.add_argument('--version', required=True, help='Game protocol version, e.g. 3.6.10.10')
    parser.add_argument('--destination', type=Path, default=Path(__file__).resolve().parents[1], help='Game wire project directory')
    args = parser.parse_args()
    try:
        import_sources(args.schema, args.mapping, args.destination, args.version)
    except (OSError, ValueError, KeyError, TypeError) as ex:
        parser.exit(1, f'Game import error: {ex}\n')
