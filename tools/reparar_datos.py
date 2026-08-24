# -*- coding: utf-8 -*-
"""
Repara los dos defectos de datos que la auditoría arrastraba y que el propio
cliente desmiente:

  1. Las mazmorras 144 y 157: sus DOS únicas salas apuntan a mapas (232784389,
     232785413, 232786435, 232787459) que no existen en ningún catálogo del
     cliente —el cliente trae exactamente 15.360 mapas y ninguno es esos—. Son
     ids fantasma de la fuente de mazmorras; las mazmorras no se pueden entrar
     y no hay con qué repararlas. Se quitan enteras, de la base y de
     datos/dungeons.json, para que queden las 185 que sí se pueden andar.

  2. Los niveles de hechizo huérfanos: 379 filas de SpellLevels cuyos hechizos
     no existen ni en la tabla Spells ni (salvo el placeholder 0) en el cliente.
     Nadie los lee; se borran para que la tabla diga la verdad.

Idempotente: si ya está reparado, no toca nada.
"""
import json
import os
import sqlite3
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MUNDO = os.path.join(RAIZ, "bases", "world.db")
MAZMORRAS_JSON = os.path.join(RAIZ, "datos", "dungeons.json")

FANTASMAS = {232784389, 232785413, 232786435, 232787459}


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    con = sqlite3.connect(MUNDO)
    q = lambda s: con.execute(s).fetchall()

    # ── 1. Las mazmorras rotas ──────────────────────────────────────────────
    rotas = {r[0] for r in q(
        "SELECT DISTINCT DungeonId FROM DungeonRooms WHERE MapId NOT IN "
        "(SELECT Id FROM MapTemplates)")}
    # sólo las que TODAS sus salas son fantasma: las que tienen alguna sala buena
    # se quedan y se arreglan quitando sólo las salas malas.
    quitar = set()
    for d in rotas:
        total = q(f"SELECT COUNT(*) FROM DungeonRooms WHERE DungeonId={d}")[0][0]
        malas = q(f"SELECT COUNT(*) FROM DungeonRooms WHERE DungeonId={d} AND MapId NOT IN "
                  "(SELECT Id FROM MapTemplates)")[0][0]
        if total > 0 and malas == total:
            quitar.add(d)
        elif malas > 0:
            con.execute(f"DELETE FROM DungeonRooms WHERE DungeonId={d} AND MapId NOT IN "
                        "(SELECT Id FROM MapTemplates)")
            print(f"[i] Mazmorra {d}: {malas} sala(s) fantasma quitadas; se conservan las buenas.")
    for d in sorted(quitar):
        con.execute(f"DELETE FROM DungeonRooms WHERE DungeonId={d}")
        con.execute(f"DELETE FROM Dungeons WHERE Id={d}")
        print(f"[+] Mazmorra {d} quitada entera: todas sus salas apuntaban a mapas "
              f"que el cliente no trae.")

    if quitar and os.path.exists(MAZMORRAS_JSON):
        with open(MAZMORRAS_JSON, encoding="utf-8") as fh:
            datos = json.load(fh)
        for d in quitar:
            datos.pop(str(d), None)
        with open(MAZMORRAS_JSON, "w", encoding="utf-8") as fh:
            json.dump(datos, fh, ensure_ascii=False, separators=(",", ":"))
        print(f"[+] datos/dungeons.json actualizado: {len(datos)} mazmorras.")

    # ── 2. Los niveles huérfanos ───────────────────────────────────────────
    antes = q("SELECT COUNT(*) FROM SpellLevels WHERE SpellId NOT IN (SELECT Id FROM Spells)")[0][0]
    con.execute("DELETE FROM SpellLevels WHERE SpellId NOT IN (SELECT Id FROM Spells)")
    print(f"[+] SpellLevels huérfanos borrados: {antes}.")

    con.commit()
    con.close()
    print("[i] Reparación terminada.")


if __name__ == "__main__":
    main()
