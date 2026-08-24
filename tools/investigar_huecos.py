# -*- coding: utf-8 -*-
"""Investiga los otros cuatro huecos de la auditoría contra los datos del cliente:
los mapas de las mazmorras 144/157, los niveles de hechizo huérfanos, los mapas
con celdas de combate pero sin celdas caminables, y si el cliente sabe algo del
emplazamiento de NPCs."""
import json
import os
import sqlite3

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.3.16f1"

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLIENTE = r"C:\Users\rapha\AppData\Local\Ankama\Dofus-dofus3\Dofus_Data\StreamingAssets\Content\Data"


def filas(bundle):
    """Las RefIds de TODOS los MonoBehaviours del bundle: algunos van troceados en
    varios objetos (mapscoordinates, por ejemplo), así que no basta con el primero."""
    env = UnityPy.load(os.path.join(CLIENTE, bundle))
    todas = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        arbol = obj.read_typetree()
        refs = arbol.get("references")
        if isinstance(refs, dict) and isinstance(refs.get("RefIds"), list):
            todas.extend(f.get("data", {}) for f in refs["RefIds"] if isinstance(f.get("data"), dict))
    return todas


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    import sys as _s

    print("── 1. Los mapas de las mazmorras rotas ─────────────────────")
    coords = filas("data_assets_mapscoordinatesdataroot.asset.bundle")
    ids_cliente = {c.get("id") for c in coords}
    print(f"   mapscoordinates: {len(ids_cliente):,} mapas en el cliente")
    for m in (232784389, 232785413, 232786435, 232787459):
        esta = m in ids_cliente
        detalle = next((c for c in coords if c.get("id") == m), None)
        print(f"   {m}: {'EN EL CLIENTE ' + json.dumps(detalle)[:140] if esta else 'no existe en el cliente'}")

    print("\n── 2. Niveles de hechizo huérfanos ─────────────────────────")
    hechizos = filas("data_assets_spellsdataroot.asset.bundle")
    ids_hechizos = {h.get("id") for h in hechizos}
    print(f"   spells del cliente: {len(ids_hechizos):,}")
    con = sqlite3.connect(os.path.join(RAIZ, "bases", "world.db"))
    huerfanos = [r[0] for r in con.execute(
        "SELECT DISTINCT SpellId FROM SpellLevels WHERE SpellId NOT IN (SELECT Id FROM Spells)")]
    en_cliente = [h for h in huerfanos if h in ids_hechizos]
    print(f"   niveles huérfanos: {len(huerfanos)}; de esos, el hechizo existe en el cliente: {len(en_cliente)}")
    if huerfanos[:10]:
        print(f"   ejemplos: {huerfanos[:10]}")
        # ¿qué pinta tienen esos hechizos en el cliente?
        for h in huerfanos[:4]:
            d = next((x for x in hechizos if x.get("id") == h), None)
            print(f"   hechizo {h} en cliente: {json.dumps(d, ensure_ascii=False)[:150] if d else '—'}")

    print("\n── 3. Mapas con combate pero sin celdas caminables ─────────")
    walk = {int(k) for k in json.load(open(os.path.join(RAIZ, "datos", "map_walkable_cells.json"), encoding="utf-8"))}
    fight = {int(k) for k in json.load(open(os.path.join(RAIZ, "datos", "map_fight_cells.json"), encoding="utf-8"))}
    sin_andar = sorted(fight - walk)
    print(f"   {len(sin_andar)} mapas: {sin_andar}")
    for m in sin_andar[:6]:
        d = next((c for c in coords if c.get("id") == m), None)
        print(f"   {m}: {'coords ' + json.dumps(d)[:100] if d else 'sin coordenadas en el cliente'}")

    print("\n── 4. Qué sabe el cliente de los NPCs ──────────────────────")
    npcs = filas("data_assets_npcsdataroot.asset.bundle")
    print(f"   npcs del cliente: {len(npcs):,}")
    if npcs:
        print(f"   primera: {json.dumps(npcs[0], ensure_ascii=False)[:220]}")
        # ¿algún campo de mapa/celda?
        claves = set()
        for n in npcs[:50]:
            claves.update(n.keys())
        print(f"   campos: {sorted(claves)}")
    con.close()


if __name__ == "__main__":
    import sys
    main()
