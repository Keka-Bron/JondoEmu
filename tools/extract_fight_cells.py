"""Extrae, para cada mapa, las casillas transitables EN COMBATE y las que cortan la
linea de vision. Sale del mismo sitio que map_walkable_cells.json (los bundles del
cliente), pero sin el recorte de bordes que aquel aplica para colocar mobs.

  f : casillas donde se puede estar/andar en combate (mov=1 y nonWalkableDuringFight=0)
  b : casillas que BLOQUEAN la linea de vision (los=0)
"""
import os, sys, warnings, json
warnings.simplefilter('ignore')
import UnityPy
UnityPy.config.FALLBACK_UNITY_VERSION = '2022.3.20f1'

BUNDLE_DIR = r'C:\Jondo\DofusClient\Dofus_Data\StreamingAssets\Content\Map\Data'
OUT = r'C:\Jondo\Jondo Unity Emulator\map_fight_cells.json'

files = [os.path.join(BUNDLE_DIR, f) for f in os.listdir(BUNDLE_DIR)
         if f.endswith('.bundle') and 'mapdata' in f]
print(f'{len(files)} bundles', flush=True)

out = {}
for i, path in enumerate(files):
    try:
        env = UnityPy.load(path)
        for obj in env.objects:
            if obj.type.name != 'MonoBehaviour':
                continue
            try:
                t = obj.read_typetree()
            except Exception:
                continue
            name = str(t.get('m_Name', ''))
            if not (name.startswith('map_') and 'mapData' in t):
                continue
            mid = name[4:]
            if not mid.isdigit():
                continue
            cells = t['mapData'].get('cellsData', [])
            walk, block = [], []
            for c in cells:
                n = c.get('cellNumber', 0)
                if c.get('mov', 0) == 1 and c.get('nonWalkableDuringFight', 0) == 0:
                    walk.append(n)
                if c.get('los', 1) == 0:
                    block.append(n)
            if walk:
                out[mid] = {'f': walk, 'b': block}
    except Exception as e:
        print(f'  fallo en {os.path.basename(path)}: {e}', file=sys.stderr)
    if (i + 1) % 50 == 0:
        print(f'  {i+1}/{len(files)} bundles, {len(out)} mapas', flush=True)

with open(OUT, 'w') as fh:
    json.dump(out, fh, separators=(',', ':'))
print(f'listo: {len(out)} mapas -> {OUT}', flush=True)
