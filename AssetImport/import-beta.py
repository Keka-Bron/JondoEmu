#!/usr/bin/env python3
"""Build an isolated Jondo seed from the 18 November (3.8.1.2 beta) client."""
import argparse
import hashlib
import json
import shutil
import sqlite3
import struct
import tempfile
import zipfile
from collections import defaultdict
from pathlib import Path

from november_assets import extract

ROOT = Path(__file__).resolve().parents[1]
VERSION = '3.8.1.2'
TUTORIAL_MAP, TUTORIAL_CELL = 241437185, 329
NEW_BREED = 21


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'), allow_nan=False)


def read_json(path):
    return json.loads(path.read_bytes())


def write_json(path, value):
    path.write_text(compact(value) + '\n', encoding='utf-8')


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def numbers(text):
    return [int(value) for value in text.split(',') if value]


def look(text, colors):
    if not (text.startswith('{') and text.endswith('}')):
        raise ValueError(f'Invalid breed look: {text!r}')
    bones, skins, _, scales = text[1:-1].split('|')
    if len(colors) != 6 or not all(0 <= c <= 0xffffff for c in colors):
        raise ValueError(f'Expected six RGB defaults in {text!r}, got {colors}')
    return dict(bones=int(bones), skins=numbers(skins), scales=numbers(scales), colors=colors)


def appearances(data, assets):
    breeds = assets['breeds']
    if len(breeds) != 20 or str(NEW_BREED) not in breeds:
        raise ValueError('Expected the November catalogue with 20 breeds including breed 21')
    looks = {bid: {sex: look(row[f'{sex}_look'], row[f'{sex}_colors']) for sex in ('male', 'female')}
             for bid, row in breeds.items()}
    stats = {bid: {stat: [band['values'][:2] for band in row[f'stats_points_for_{stat}']]
                   for stat in ('strength', 'intelligence', 'chance', 'agility', 'vitality', 'wisdom')}
             for bid, row in breeds.items()}
    for breed, profile in stats.items():
        for stat, bands in profile.items():
            if not bands or any(len(b) != 2 or b[1] <= 0 for b in bands) or bands[0][0] != 0:
                raise ValueError(f'Invalid characteristic bands for {breed}/{stat}')
    write_json(data / 'breed_looks.json', looks)
    write_json(data / 'breed_stats.json', stats)

    def rows(table):
        return {key: dict(skin=numbers(row['skins'])[0], breed=row['breed'], gender=row['gender'],
                          creation=int(bool(row['available_at_creation'])))
                for key, row in sorted(assets[table].items(), key=lambda item: int(item[0]))
                if row['skins']}

    heads, bodies = rows('heads'), rows('bodies')
    defaults = {}
    previous = read_json(data / 'heads.json')['defaults']
    for bid, sexes in looks.items():
        for gender, profile in enumerate(sexes.values()):
            candidates = [int(k) for k, v in heads.items()
                          if v['breed'] == int(bid) and v['gender'] == gender and v['creation']]
            if not candidates or not any(v['breed'] == int(bid) and v['gender'] == gender
                                         and v['creation'] and v['skin'] == profile['skins'][0]
                                         for v in bodies.values()):
                raise ValueError(f'Missing creation head or base body for {bid}/{gender}')
            key = f'{bid}_{gender}'
            ordered = sorted(candidates, key=lambda k: (assets['heads'][str(k)]['order'], k))
            defaults[key] = previous[key] if previous.get(key) in candidates else ordered[0]
    write_json(data / 'heads.json', dict(heads=heads, defaults=defaults))
    write_json(data / 'bodies.json', dict(bodies=bodies))
    return dict(breeds=len(looks), heads=len(heads), bodies=len(bodies))


def translations(path):
    data = path.read_bytes()
    count, = struct.unpack_from('<I', data, 3)
    end = 7 + count * 8
    if data[0] != 2 or not 0 < count or end > len(data):
        raise ValueError(f'Unexpected translation format: {path}')
    result = {}
    for key, offset in struct.iter_unpack('<II', data[7:end]):
        if key in result or not end <= offset < len(data):
            raise ValueError(f'Invalid translation entry {key}')
        length = 0
        for shift in range(0, 35, 7):
            b = data[offset]
            offset += 1
            length |= (b & 127) << shift
            if b < 128:
                break
        else:
            raise ValueError(f'Invalid translation length at {key}')
        if offset + length > len(data):
            raise ValueError(f'Truncated translation {key}')
        result[key] = data[offset:offset + length].decode('utf-8')
    return result


def merge_maps(data, db, assets, text):
    maps, info, scrolls = (assets[key] for key in ('maps', 'map_info', 'scrolls'))
    if len(info) != 15685 or len(maps) < 15000:
        raise ValueError(f'Unexpected November map coverage: {len(info)} metadata, {len(maps)} geometry')
    starter = maps[str(TUTORIAL_MAP)]
    if TUTORIAL_CELL not in starter['walk']:
        raise ValueError(f'Tutorial spawn {TUTORIAL_MAP}/{TUTORIAL_CELL} is not walkable')
    walk_path, fight_path = data / 'map_walkable_cells.json', data / 'map_fight_cells.json'
    places_path = data / 'map_placements_3.6.10.11.json'
    walk, fight = read_json(walk_path), read_json(fight_path)
    placements = read_json(places_path)['maps']
    old_positions = {str(row[0]): row[1:] for row in db.execute('SELECT MapId,Outdoor,Name FROM MapPositions')}
    old_scrolls = {str(row[0]): row[1:] for row in db.execute('SELECT * FROM MapScrolls')}
    for mid, geo in maps.items():
        walk[mid] = sorted(geo['walk'])
        fight[mid] = dict(f=sorted(geo['fight']), b=sorted(geo['opaque']))
        if geo['red'] and geo['blue']:
            placements[mid] = dict(red=geo['red'], blue=geo['blue'])
        else:
            placements.pop(mid, None)
    write_json(walk_path, walk)
    write_json(fight_path, fight)
    write_json(places_path, dict(source=VERSION, maps=placements))

    for mid, row in info.items():
        key = int(mid)
        if key != row['id']:
            raise ValueError(f'Unexpected map dictionary id {mid}: {row["id"]}')
        x, y, sub = (row[k] for k in ('pos_x', 'pos_y', 'sub_area_id'))
        # Existing Outdoor/Name values also carry Jondo's authored classifications and names.
        # For a new map use only the world's unambiguous outdoor classification.
        outdoor, name = old_positions.get(mid, (int(row['world_map'] == 1), None))
        if not name:
            name = text.get(row['name_id'], '')
        template = dict(id=key, m_flags=row['flags'], nameId=row['name_id'], posX=x, posY=y,
                        subAreaId=sub, tacticalModeTemplateId=row['tactical_mode_template_id'], worldMap=row['world_map'])
        db.execute('INSERT OR REPLACE INTO MapTemplates (Id,SubAreaId,Data) VALUES (?,?,?)', (key, sub, compact(template)))
        db.execute('INSERT OR REPLACE INTO MapPositions VALUES (?,?,?,?,?,?)', (key, x, y, sub, outdoor, name))
        db.execute('INSERT OR REPLACE INTO MapSubareas VALUES (?,?)', (key, sub))
    for mid in maps.keys() | scrolls.keys():
        if mid not in info:
            continue
        table = scrolls.get(mid, {})
        geo = maps.get(mid, {}).get('neighbors', [0] * 4)
        previous = old_scrolls.get(mid, [0] * 4)
        directions = ('right', 'bottom', 'left', 'top')
        # Prefer positive target geometry/table destinations; retain prior captured routes
        # only where both client sources have no destination and the map still exists.
        destinations = [next((n for n in (g, table.get(f'{d}_map_id', 0), p)
                              if n is not None and n > 0 and n != int(mid) and str(n) in info), 0)
                        for d, g, p in zip(directions, geo, previous)]
        db.execute('INSERT OR REPLACE INTO MapScrolls VALUES (?,?,?,?,?)', (int(mid), *destinations))
    return dict(map_metadata=len(info), map_geometry=len(maps), placements=len(placements))


def camel(value):
    first, *rest = value.split('_')
    return first + ''.join(word.title() for word in rest)


def spell_effect(row):
    value = {camel(k): v for k, v in row.items() if k not in ('action_id', 'flags', 'zone_descr')}
    return dict(value, effectId=row['action_id'], m_flags=row['flags'],
                 zoneDescr={camel(k): v for k, v in row['zone_descr'].items()})


def spell_template(value):
    """SpellTemplates.Data uses Unity field names and {"Array": [...]} at every depth."""
    if isinstance(value, list):
        return {'Array': [spell_template(item) for item in value]}
    if isinstance(value, dict):
        return {('m_flags' if key == 'flags' else camel(key)): spell_template(item)
                for key, item in value.items()}
    return value


def add_breed_spells(data, db, assets):
    # Keep August ranks and scripts for existing classes/monsters. Import the new
    # breed's complete base/variant pairs and their exact November spell-level IDs.
    pairs = sorted((row for row in assets['spell_variants'].values() if row['breed_id'] == NEW_BREED),
                   key=lambda row: row['id'])
    if not pairs or any(len(row['spell_ids']) != 2 for row in pairs):
        raise ValueError('Missing complete breed 21 spell pairs')
    spell_ids = list(dict.fromkeys(spell for row in pairs for spell in row['spell_ids']))
    ranks = defaultdict(list)
    for level in assets['spell_levels'].values():
        ranks[level['spell_id']].append(level)
    if not any(level['min_player_level'] <= 1 for spell in spell_ids for level in ranks[spell]):
        raise ValueError('No level-one spells for breed 21')
    for spell in spell_ids:
        row = assets['spells'][str(spell)]
        if not ranks[spell]:
            raise ValueError(f'No ranks for breed 21 spell {spell}')
        db.execute('INSERT INTO Spells (Id,NameId,DescriptionId,IconId,TypeId) VALUES (?,?,?,?,?)',
                   (spell, row['name_id'], row['description_id'], row['icon_id'], row['type_id']))
        db.execute('INSERT INTO SpellTemplates (Id,NameId,Data) VALUES (?,?,?)',
                   (spell, row['name_id'], compact(spell_template(row))))
        for level in ranks[spell]:
            flags = level['flags']
            db.execute('''INSERT INTO SpellLevels (Id,SpellId,Grade,MinPlayerLevel,APCost,MinRange,MaxRange,
                CastInLine,MaxCastPerTurn,MaxCastPerTarget,EffectsJson,Flags,CastInDiagonal,CastTestLos,
                NeedFreeCell,NeedTakenCell,HideEffects,CriticalHitProbability,InitialCooldown,GlobalCooldown,
                MinCastInterval,MaxStack,SpellBreed,StatesCriterion,CriticalEffectsJson)
                VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)''',
                (level['id'], spell, level['grade'], level['min_player_level'], level['ap_cost'],
                 level['min_range'], level['range'], int(bool(flags & 1)), level['max_cast_per_turn'],
                 level['max_cast_per_target'], compact([spell_effect(r) for r in level['effects']]), flags,
                 *(int(bool(flags & bit)) for bit in (2, 4, 8, 16, 128)), level['critical_hit_probability'],
                 level['initial_cooldown'], level['global_cooldown'], level['min_cast_interval'], level['max_stack'],
                 level['spell_breed'], level['states_criterion'], compact([spell_effect(r) for r in level['critical_effect']])))
    db.execute('INSERT INTO SpellVariants VALUES (?,?)', (NEW_BREED, compact(spell_ids)))
    path = data / 'spell_variants.json'
    variant_table = read_json(path)
    current = variant_table['references']['RefIds']
    current.extend({'data': dict(id=row['id'], breedId=NEW_BREED, spellIds={'Array': row['spell_ids']})}
                   for row in pairs)
    write_json(path, variant_table)
    return dict(breed_21_spells=len(spell_ids), breed_21_pairs=len(pairs))


def stage(client, output):
    streaming = client / 'Dofus_Data/StreamingAssets'
    version = dict(line.split('=', 1) for line in (streaming / 'version').read_text().splitlines() if '=' in line)
    if version.get('Version') != VERSION or not version.get('BuildDate', '').startswith('2026-11-18'):
        raise ValueError(f'Expected November beta {VERSION}: {client}')
    if output.exists():
        raise ValueError(f'Refusing to replace an existing data root: {output}')
    if not output.parent.is_dir():
        raise ValueError(f'Create the parent directory first: {output.parent}')
    print(f'Extracting {VERSION} assets from {client}', flush=True)
    with tempfile.TemporaryDirectory(prefix='.beta-import-', dir=output.parent) as directory:
        work = Path(directory)
        content = streaming / 'Content'
        assets = extract(content, NEW_BREED)
        target = work / 'root'
        data = target / 'datos'
        shutil.copytree(ROOT / 'datos', data, ignore=shutil.ignore_patterns('tmp', 'world.zip'))
        shutil.copytree(ROOT / 'content', target / 'content')
        shutil.copy2(ROOT / '.jondo-root', target / '.jondo-root')
        counts = appearances(data, assets)
        text = translations(content / 'I18n/es.bin')
        seed = ROOT / 'datos/world.zip'
        with zipfile.ZipFile(seed) as archive, archive.open('world.db') as src, (work / 'world.db').open('wb') as dst:
            shutil.copyfileobj(src, dst)
        with sqlite3.connect(work / 'world.db') as db:
            db.execute('PRAGMA journal_mode = DELETE')
            with db:
                counts.update(merge_maps(data, db, assets, text))
                counts.update(add_breed_spells(data, db, assets))
                # Keep baseline-only keys for catalogues deliberately carried forward.
                db.executemany('INSERT OR REPLACE INTO Translations (Key,Text) VALUES (?,?)', text.items())
                counts['november_translations'] = len(text)
            if db.execute('PRAGMA quick_check').fetchall() != [('ok',)]:
                raise ValueError('Staged database failed SQLite quick_check')
        db.close()
        with zipfile.ZipFile(data / 'world.zip', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            archive.write(work / 'world.db', 'world.db')
        (target / 'bases').mkdir()
        write_json(target / 'beta-assets.json', dict(target=VERSION, version=VERSION,
                   build_date=version['BuildDate'], baseline_world_sha256=digest(seed), counts=counts))
        # Reserve the name before moving the staged input. Never replace a beta save.
        output.mkdir()
        target.rename(output)
        print(f'Staged {output}: {compact(counts)}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True, help='November client installation directory')
    parser.add_argument('--output', type=Path, default=ROOT / 'host-artifacts' / 'beta-data' / VERSION)
    args = parser.parse_args()
    try:
        stage(args.client.resolve(), args.output.resolve())
    except (ImportError, OSError, ValueError, IndexError, KeyError, sqlite3.Error) as exc:
        parser.exit(1, f'Beta asset import failed: {exc}\n')
