#!/usr/bin/env python3
"""Build an Incarnam-only static coverage contract from the pinned client catalogues.

The output proves which maps, monsters, spells, dungeon rooms, quests and quest-start NPC
templates exist in the installed client. It deliberately does not invent NPC spawn placement,
dialogue branches, quest mutations, or encounter-only rules: those are server-owned evidence and
are reported as missing coverage until captured and implemented.
"""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VERSION = "3.6.10.10"
CATALOGS = ROOT / "client_data" / VERSION / "catalogs"
OUTPUT = ROOT / "client_data" / VERSION / "mechanics" / "incarnam" / "content-coverage.json"
AREA_ID = 45


def rows(name: str) -> list[dict[str, object]]:
    path = CATALOGS / name
    document = json.loads(path.read_text(encoding="utf-8"))
    if document.get("clientVersion") != VERSION or not isinstance(document.get("rows"), list):
        raise SystemExit(f"incompatible normalized catalogue: {path}")
    return [row["data"] for row in document["rows"] if isinstance(row, dict) and isinstance(row.get("data"), dict)]


def main() -> int:
    subareas = sorted((x for x in rows("subareasdataroot.json") if x.get("areaId") == AREA_ID), key=lambda x: int(x["id"]))
    map_ids = sorted({int(map_id) for subarea in subareas for map_id in subarea.get("mapIds", []) if isinstance(map_id, int)})
    map_set = set(map_ids)
    monster_ids = sorted({int(monster_id) for subarea in subareas for monster_id in subarea.get("monsters", []) if isinstance(monster_id, int)})
    monster_set = set(monster_ids)

    monsters_by_id = {int(x["id"]): x for x in rows("monstersdataroot.json") if isinstance(x.get("id"), int)}
    levels_by_spell: dict[int, list[dict[str, object]]] = {}
    for level in rows("spelllevelsdataroot.json"):
        spell_id = level.get("spellId")
        if isinstance(spell_id, int):
            levels_by_spell.setdefault(spell_id, []).append(level)

    # This is an executable-coverage declaration, not a guess at behavior. These four effect
    # families are present on Incarnam monsters but do not yet have complete native semantics in
    # the generic fight engine. Every other effect below was checked against the current generic
    # damage/movement/summon/heal/stat handlers.
    effect_gaps = {
        94: ("Partial", "Fire life-steal damage executes, but healing the caster still lacks a verified native response."),
        150: ("Missing", "Invisibility needs visibility, targeting, and actor-update protocol semantics."),
        401: ("Missing", "Start-of-turn glyph needs a persistent cell entity, trigger lifecycle, and native packets."),
        1133: ("Missing", "Damage per AP used needs a verified AP-consumption window and end-turn damage response."),
    }
    monster_records = []
    region_effect_ids: set[int] = set()
    for monster_id in monster_ids:
        monster = monsters_by_id.get(monster_id)
        if monster is None:
            raise SystemExit(f"Incarnam references missing monster {monster_id}")
        spell_ids = [int(x) for x in monster.get("spells", []) if isinstance(x, int)]
        effect_ids = sorted({
            int(effect["effectId"])
            for spell_id in spell_ids
            for level in levels_by_spell.get(spell_id, [])
            for effect in level.get("effects", [])
            if isinstance(effect, dict) and isinstance(effect.get("effectId"), int)
        })
        region_effect_ids.update(effect_ids)
        gaps = [effect_id for effect_id in effect_ids if effect_id in effect_gaps]
        monster_records.append({
            "clientMonsterId": monster_id,
            "nameTextId": monster.get("nameId", 0),
            "spellIds": spell_ids,
            "effectIds": effect_ids,
            "gradeCount": len(monster.get("grades", [])) if isinstance(monster.get("grades"), list) else 0,
            "runtimeMode": "GenericClientSpellAi" if not gaps else "GenericClientSpellAiWithDeclaredGaps",
            "unimplementedEffectIds": gaps,
            "encounterRuleCoverage": "GenericEffectsOnly",
        })

    dungeons = []
    for dungeon in rows("dungeonsdataroot.json"):
        room_ids = [int(x) for x in dungeon.get("mapIds", []) if isinstance(x, int)]
        if dungeon.get("id") in {x.get("dungeonId") for x in subareas} or map_set.intersection(room_ids):
            dungeons.append({
                "clientDungeonId": dungeon.get("id"),
                "nameTextId": dungeon.get("nameId"),
                "entranceMapId": dungeon.get("entranceMapId"),
                "exitMapId": dungeon.get("exitMapId"),
                "roomMapIds": room_ids,
                "bossMonsterIds": dungeon.get("bosses", []),
                "runtimeCoverage": "RoomsAndGenericFightsOnly",
                "specialRuleCoverage": "MissingCapturedServerRules",
            })
    dungeons.sort(key=lambda x: int(x["clientDungeonId"]))

    quests_all = rows("questsdataroot.json")
    objectives_by_id = {int(x["id"]): x for x in rows("questobjectivesdataroot.json") if isinstance(x.get("id"), int)}
    steps_by_id = {int(x["id"]): x for x in rows("queststepsdataroot.json") if isinstance(x.get("id"), int)}
    start_quest_ids = {
        int(quest["id"])
        for quest in quests_all
        if isinstance(quest.get("id"), int)
        and any(isinstance(pos, dict) and pos.get("mapId") in map_set for pos in quest.get("startPosition", []))
    }
    # Follow quest chains whose objectives are explicitly located on an Incarnam map.
    objective_quest_ids: set[int] = set()
    for quest in quests_all:
        if not isinstance(quest.get("id"), int):
            continue
        for step_id in quest.get("stepIds", []):
            step = steps_by_id.get(step_id)
            if not step:
                continue
            if any(objectives_by_id.get(objective_id, {}).get("mapId") in map_set for objective_id in step.get("objectiveIds", [])):
                objective_quest_ids.add(int(quest["id"]))
                break
    quest_ids = sorted(start_quest_ids | objective_quest_ids)
    quests_by_id = {int(x["id"]): x for x in quests_all if isinstance(x.get("id"), int)}
    npc_ids = sorted({
        int(pos["npcId"])
        for quest_id in quest_ids
        for pos in quests_by_id[quest_id].get("startPosition", [])
        if isinstance(pos, dict) and pos.get("mapId") in map_set and isinstance(pos.get("npcId"), int)
    })
    npc_by_id = {int(x["id"]): x for x in rows("npcsdataroot.json") if isinstance(x.get("id"), int)}

    document = {
        "schemaVersion": 1,
        "id": f"incarnam-content-coverage-{VERSION}",
        "kind": "region-content-coverage",
        "status": "Verified",
        "sourceUrl": "https://www.dofus.com/en/mmorpg",
        "sourceKind": "installed-client-extraction",
        "clientVersion": VERSION,
        "generatedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "region": {"clientAreaId": AREA_ID, "subAreaIds": [int(x["id"]) for x in subareas], "mapIds": map_ids},
        "monsters": monster_records,
        "dungeons": dungeons,
        "quests": [{
            "clientQuestId": quest_id,
            "nameTextId": quests_by_id[quest_id].get("nameId"),
            "stepIds": quests_by_id[quest_id].get("stepIds", []),
            "startPositions": quests_by_id[quest_id].get("startPosition", []),
            "runtimeCoverage": "StaticDefinitionOnly",
        } for quest_id in quest_ids],
        "questStartNpcs": [{
            "clientNpcId": npc_id,
            "nameTextId": npc_by_id.get(npc_id, {}).get("nameId", 0),
            "look": npc_by_id.get(npc_id, {}).get("look", ""),
            "spawnCoverage": "RequiresServerEvidence",
        } for npc_id in npc_ids],
        "combatEffectCoverage": [{
            "effectId": effect_id,
            "runtimeCoverage": effect_gaps.get(effect_id, ("Executable", "Handled by the generic data-driven effect engine."))[0],
            "notes": effect_gaps.get(effect_id, ("Executable", "Handled by the generic data-driven effect engine."))[1],
        } for effect_id in sorted(region_effect_ids)],
        "coverage": {
            "clientMapAndCellData": "VersionPinned",
            "clientInteractives": "VersionPinned",
            "monsterStatsAndSpellDefinitions": "VersionPinned",
            "genericCombatEffects": "PartiallyExecutable",
            "dungeonRoomTopology": "VersionPinned",
            "npcTemplates": "VersionPinned",
            "npcSpawnPlacementAndDialogBranches": "MissingServerEvidence",
            "questStateTransitionsAndRewards": "MissingServerHandlers",
            "dungeonAndBossSpecialRules": "MissingCapturedServerRules",
        },
        "blockingMechanicGaps": [{"effectId": effect_id, "coverage": status, "missingProof": notes}
                                 for effect_id, (status, notes) in sorted(effect_gaps.items())],
        "notes": "A client bundle is authoritative for static IDs, maps, cells, looks and spell definitions. It is not authoritative for server-owned spawn placement, dialogue routing, quest mutations or special encounter logic; those remain explicitly incomplete instead of guessed.",
    }
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"exported {len(map_ids)} maps, {len(monster_records)} monsters, {len(dungeons)} dungeons, {len(quest_ids)} quests, and {len(npc_ids)} quest-start NPC templates")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
