"""Extrae la tabla de experiencia por nivel del personaje desde los bundles del cliente.

  data_assets_characterxpmappingsdataroot.asset.bundle
     objectsById : nivel -> referencia
     references  : { rid, data: { experiencePoints } }

Genera character_xp.json = { "nivel": puntosDeExperienciaAcumulados, ... }
"""
import warnings, json
warnings.simplefilter('ignore')
import UnityPy
UnityPy.config.FALLBACK_UNITY_VERSION = '2022.3.20f1'

SRC = r'C:\Jondo\DofusClient\Dofus_Data\StreamingAssets\Content\Data\data_assets_characterxpmappingsdataroot.asset.bundle'
OUT = r'C:\Jondo\Jondo Unity Emulator\character_xp.json'

env = UnityPy.load(SRC)
tabla = {}
for obj in env.objects:
    if obj.type.name != 'MonoBehaviour':
        continue
    try:
        t = obj.read_typetree()
    except Exception:
        continue
    ids = t.get('objectsById') or {}
    keys, vals = ids.get('m_keys'), ids.get('m_values')
    refs = {r['rid']: r for r in (t.get('references') or {}).get('RefIds', [])}
    if not keys or not vals:
        continue
    for lvl, v in zip(keys, vals):
        r = refs.get(v.get('rid'))
        if not r:
            continue
        xp = (r.get('data') or {}).get('experiencePoints')
        if xp is None:
            continue
        # A partir de cierto nivel el valor se dispara a 8.7e18 (centinela de "sin tope").
        if xp > 1e17:
            continue
        tabla[int(lvl)] = int(xp)

niveles = sorted(tabla)
print(f'niveles con datos: {len(niveles)} (de {niveles[0]} a {niveles[-1]})')
for l in (1, 2, 3, 10, 50, 51, 100, 200):
    if l in tabla:
        print(f'   nivel {l:3d} -> {tabla[l]:,} XP')

with open(OUT, 'w') as fh:
    json.dump({str(k): tabla[k] for k in niveles}, fh, separators=(',', ':'))
print('escrito en', OUT)
