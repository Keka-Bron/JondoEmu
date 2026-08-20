#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
La auditoría de datos: qué hay en datos/ y en bases/, y si las piezas encajan.

Cuenta cada fichero de datos y cada tabla de world.db, y luego cruza las
referencias entre fuentes —el volcado del cliente (datos/*.json, world.db) y los
catalogos de dofusdude (item_sets)— para ver si algún id apunta a algo que no
existe. Todo lo que imprime está medido ahora mismo, no copiado de la
documentación.

Se corre tal cual:  python tools/auditoria_datos.py

Por qué no compara contra dofusdb.fr: su licencia (LPNC-IA 1.0, la página que
sirve la propia API) prohíbe expresamente alimentar a agentes de IA con sus
datos, así que esta auditoría se cruza con dofusdude —la fuente externa que el
emulador ya consume— y consigo misma. Véase docs/data-audit.md.
"""

import glob
import json
import os
import sqlite3
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATOS = os.path.join(ROOT, "datos")
WORLD = os.path.join(ROOT, "bases", "world.db")


def jload(name):
    with open(os.path.join(DATOS, name), encoding="utf-8") as fh:
        return json.load(fh)


def ids_de(diccionario):
    """Las claves de un diccionario de datos son ids de objeto, y vienen como texto."""
    return {int(k) for k in diccionario}


def main():
    # La consola de Windows viene en cp1252 y los rótulos llevan rayas que ahí no
    # existen: sin esto el primer print revienta antes de decir nada.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    if not os.path.exists(WORLD):
        print(f"[!] No hay {WORLD}; arranca el servidor una vez para que se extraiga.")
        sys.exit(1)
    con = sqlite3.connect(WORLD)
    q = lambda s: con.execute(s).fetchall()
    uno = lambda s: con.execute(s).fetchone()[0]

    print("── datos/*.json ──────────────────────────────────────────────")
    for ruta in sorted(glob.glob(os.path.join(DATOS, "*.json"))):
        dato = jload(os.path.basename(ruta))
        if isinstance(dato, dict):
            anidado = {k: len(v) for k, v in dato.items()
                       if isinstance(v, (list, dict)) and not k.startswith("_")}
            print(f"  {os.path.basename(ruta):32} {len(dato):>7,} claves"
                  + (f"   ({anidado})" if anidado else ""))
        else:
            print(f"  {os.path.basename(ruta):32} {len(dato):>7,} filas")

    print("\n── bases/world.db ─────────────────────────────────────────────")
    tablas = [r[0] for r in q("SELECT name FROM sqlite_master WHERE type='table' "
                               "AND name NOT LIKE 'sqlite_%' ORDER BY name")]
    for t in tablas:
        print(f"  {t:26} {uno(f'SELECT COUNT(*) FROM \"{t}\"'):>9,}")

    print("\n── Cruces entre fuentes ──────────────────────────────────────")
    plantillas = {r[0] for r in q("SELECT Id FROM ItemTemplates")}

    sets = jload("item_sets.json")
    refs = [i for s in sets.values() if isinstance(s, dict)
            for i in (s.get("items") or s.get("objetos") or [])]
    huerfanos = [i for i in refs if i not in plantillas]
    print(f"  item_sets -> {len(refs):,} referencias de objeto; huérfanas: {len(huerfanos):,}")

    for nombre, sub in (("cosmetics.json", "items"), ("mounts.json", None)):
        dato = jload(nombre)
        if sub is not None:
            dato = dato[sub]
        ids = ids_de(dato)
        fuera = ids - plantillas
        print(f"  {nombre:12} -> {len(ids):,} ids; sin plantilla: {len(fuera):,}")

    monstruos = {r[0] for r in q("SELECT Id FROM MonsterTemplates")}
    total = huerfanos_m = 0
    for (mj,) in q("SELECT MembersJson FROM MapMobs"):
        try:
            miembros = json.loads(mj or "[]")
        except json.JSONDecodeError:
            continue
        for m in miembros:
            i = m.get("monsterId") or m.get("id") or m.get("MonsterId")
            if i is None:
                continue
            total += 1
            if i not in monstruos:
                huerfanos_m += 1
    print(f"  MapMobs     -> {total:,} monstruos referenciados; sin plantilla: {huerfanos_m:,}")

    mapas = {r[0] for r in q("SELECT Id FROM MapTemplates")}
    malas = q("SELECT DungeonId, Position, MapId FROM DungeonRooms "
              "WHERE MapId NOT IN (SELECT Id FROM MapTemplates)")
    print(f"  DungeonRooms -> salas con mapa inexistente: {len(malas)}"
          + (f"  {malas}" if malas else ""))
    malos = q("SELECT MapId FROM NpcSpawns WHERE MapId NOT IN (SELECT Id FROM MapTemplates)")
    print(f"  NpcSpawns   -> spawns en mapa inexistente: {len(malos)}")

    hechizos = {r[0] for r in q("SELECT Id FROM Spells")}
    niveles_huerfanos = uno("SELECT COUNT(*) FROM SpellLevels "
                            "WHERE SpellId NOT IN (SELECT Id FROM Spells)")
    ids_huerfanos = [r[0] for r in q("SELECT DISTINCT SpellId FROM SpellLevels "
                                     "WHERE SpellId NOT IN (SELECT Id FROM Spells) LIMIT 10")]
    print(f"  SpellLevels -> {niveles_huerfanos:,} niveles de hechizos que no están en Spells"
          + (f"  (ej. {ids_huerfanos})" if niveles_huerfanos else ""))

    npc = uno("SELECT COUNT(DISTINCT NpcId) FROM NpcSpawns")
    print(f"  NPCs        -> {len(q('SELECT Id FROM NpcTemplates')):,} plantillas, {npc} colocados en el mundo")

    andenes = jload("map_walkable_cells.json")
    fuera_mapas = len({int(k) for k in andenes} - mapas)
    print(f"  walkable    -> {len(andenes):,} mapas con celdas; {fuera_mapas:,} no están en MapTemplates "
          "(instancias y mapas de cliente que el mundo no usa)")

    print("\n── Los oficios ────────────────────────────────────────────────")
    vacias = [t for t in ("Jobs", "Skills", "Recipes", "RecipeIngredients",
                          "SkillCraftableItems", "SkillModifiableItemTypes")
              if uno(f'SELECT COUNT(*) FROM "{t}"') == 0]
    dofusdude = os.path.join(DATOS, "JsonFromDofusDude")
    faltan = [f for f in ("jobs.json", "skills.json", "recipes.json")
              if not os.path.exists(os.path.join(dofusdude, f))]
    if vacias:
        print(f"  tablas vacías: {', '.join(vacias)}")
        print(f"  dumps de dofusdude ausentes en {dofusdude}: {', '.join(faltan) if faltan else '—'}")
        print("  (el servidor lo avisa en el arranque: «[Skills] Falta …; se usa el catálogo que ya hay en la base.»)")
    else:
        print("  oficios cargados.")

    con.close()


if __name__ == "__main__":
    main()
