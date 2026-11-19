"""Read the November client's embedded Unity type trees into Jondo's staging inputs."""
import re


def table_rows(root):
    index = root['objectsById']
    refs = {r['rid']: r['data'] for r in root['references']['RefIds']}
    rows = {str(key): refs[value['rid']] for key, value in
            zip(index['m_keys'], index['m_values'], strict=True)}
    if len(rows) != len(index['m_keys']):
        raise ValueError(f'Duplicate key in {root["m_Name"]}')
    return rows


def read_table(directory, name):
    import UnityPy
    path = directory / f'data_assets_{name}dataroot.asset.bundle'
    if not path.is_file():
        raise ValueError(f'Missing table bundle: {path}')
    root, = (obj for obj in UnityPy.load(str(path)).objects if obj.type.name == 'MonoBehaviour')
    return table_rows(root.read_typetree())


def map_geometry(row):
    cells = dict(walk=[], fight=[], opaque=[], red=[], blue=[])
    seen = set()
    for cell in row['cellsData']:
        n = cell['cellNumber']
        if not 0 <= n < 560 or n in seen:
            raise ValueError(f'Map {row["id"]}: invalid or duplicate cell {n}')
        seen.add(n)
        if cell['mov'] and not cell['farmCell']:
            if 2 <= n % 14 <= 11 and 6 <= n // 14 <= 34:
                cells['walk'].append(n)
            if not cell['havenbagCell']:
                cells['fight'].append(n)
        if not cell['los']:
            cells['opaque'].append(n)
        for team in ('red', 'blue'):
            if cell[team]:
                cells[team].append(n)
    cells['neighbors'] = [row[d + 'NeighbourId'] for d in ('right', 'bottom', 'left', 'top')]
    return cells


def read_maps(directory, expected):
    import UnityPy
    maps = {}
    for path in sorted(directory.glob('mapdata_assets_*.bundle')):
        for name, pointer in UnityPy.load(str(path)).container.items():
            match = re.fullmatch(r'map_(\d+)\.asset', name)
            if not match:
                continue
            key = match[1]
            row = pointer.deref().read_typetree()
            if key in maps or key != str(row['id']):
                raise ValueError(f'Duplicate or mismatched map {name} in {path}')
            maps[key] = map_geometry(row)
    if maps.keys() != expected:
        raise ValueError('Map geometry IDs differ from the November metadata catalogue')
    return maps


def normalized(value):
    """Stage v1 uses snake-case row keys and `flags` for Unity's `m_flags`."""
    if isinstance(value, list):
        return [normalized(item) for item in value]
    if isinstance(value, dict):
        return {('flags' if k == 'm_flags' else re.sub(r'(?<!^)([A-Z])', r'_\1', k).lower()): normalized(v)
                for k, v in sorted(value.items())}
    return value


def extract(content, breed):
    import UnityPy
    # November's bundles omit the editor version. This is the version recorded
    # in this client's globalgamemanagers, not the August client's version.
    UnityPy.config.FALLBACK_UNITY_VERSION = '6000.3.16f1'
    data = content / 'Data'
    assets = {'schema_version': 1}
    for key, table in [('breeds', 'breeds'), ('heads', 'heads'), ('bodies', 'bodies'),
                       ('map_info', 'mapsinformation'), ('scrolls', 'mapscrollactions')]:
        assets[key] = normalized(read_table(data, table))
    variants = {k: v for k, v in read_table(data, 'spellvariants').items() if v['breedId'] == breed}
    spells = {s for row in variants.values() for s in row['spellIds']}
    assets['spell_variants'] = normalized(variants)
    assets['spells'] = normalized({k: v for k, v in read_table(data, 'spells').items() if int(k) in spells})
    assets['spell_levels'] = normalized({k: v for k, v in read_table(data, 'spelllevels').items() if v['spellId'] in spells})
    assets['maps'] = read_maps(content / 'Map/Data', assets['map_info'].keys())
    return assets
