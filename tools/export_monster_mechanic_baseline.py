#!/usr/bin/env python3
"""Export the client-derived monster baseline into the editable mechanics directory.

This is not a guide scraper.  It exports the already version-pinned client import: monster IDs,
name IDs, grades, resistances, spell IDs, and observed roleplay map/group placement.  Encounter
rules remain separate Draft/Verified monster-mechanic-map files because server-only mechanics are
not present in static client data.
"""

from __future__ import annotations

import json
import sqlite3
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATABASE = ROOT / "bases" / "world.db"
OUTPUT = ROOT / "client_data" / "3.6.10.10" / "mechanics" / "monsters" / "client-baseline.json"


def value_array(value: object) -> list[object]:
    if isinstance(value, dict):
        nested = value.get("Array", [])
        return nested if isinstance(nested, list) else []
    return value if isinstance(value, list) else []


def main() -> int:
    if not DATABASE.exists():
        raise SystemExit(f"missing database: {DATABASE}")
    with sqlite3.connect(DATABASE) as db:
        spell_profiles: dict[int, list[dict[str, object]]] = defaultdict(list)
        for row in db.execute(
            "SELECT SpellId, Grade, APCost, MinRange, MaxRange, CastTestLos, CastInLine, MaxCastPerTurn, MaxCastPerTarget FROM SpellLevels"
        ):
            spell_id, grade, ap, min_range, max_range, los, in_line, per_turn, per_target = row
            spell_profiles[spell_id].append({
                "grade": grade, "apCost": ap, "minRange": min_range, "maxRange": max_range,
                "requiresLineOfSight": los is None or los == 1, "castInLine": in_line == 1,
                "maxCastPerTurn": per_turn, "maxCastPerTarget": per_target,
            })
        locations: dict[int, set[int]] = defaultdict(set)
        group_sizes: dict[int, Counter[int]] = defaultdict(Counter)
        for map_id, members_json in db.execute("SELECT MapId, MembersJson FROM MapMobs"):
            try:
                members = json.loads(members_json)
            except json.JSONDecodeError:
                continue
            if not isinstance(members, list):
                continue
            size = len(members)
            for member in members:
                if isinstance(member, dict) and isinstance(member.get("id"), int) and member["id"] > 0:
                    locations[member["id"]].add(map_id)
                    group_sizes[member["id"]][size] += 1

        records: list[dict[str, object]] = []
        for monster_id, name_id, grades_json, spells_json in db.execute(
            "SELECT Id, NameId, Grades, Spells FROM Monsters ORDER BY Id"
        ):
            try:
                grades = value_array(json.loads(grades_json))
                spell_ids = [int(x) for x in value_array(json.loads(spells_json)) if isinstance(x, int) and x > 0]
            except (json.JSONDecodeError, TypeError, ValueError):
                grades, spell_ids = [], []
            normalized_grades = []
            for grade in grades:
                if not isinstance(grade, dict):
                    continue
                normalized_grades.append({
                    "grade": grade.get("grade"), "level": grade.get("level"),
                    "lifePoints": grade.get("lifePoints"), "actionPoints": grade.get("actionPoints"),
                    "movementPoints": grade.get("movementPoints"), "neutralResistance": grade.get("neutralResistance"),
                    "earthResistance": grade.get("earthResistance"), "fireResistance": grade.get("fireResistance"),
                    "waterResistance": grade.get("waterResistance"), "airResistance": grade.get("airResistance"),
                })
            records.append({
                "clientMonsterId": monster_id,
                "nameTextId": name_id,
                "spellIds": spell_ids,
                "spellProfiles": {str(spell_id): spell_profiles.get(spell_id, []) for spell_id in spell_ids},
                "grades": normalized_grades,
                "roleplayPlacement": {
                    "mapIds": sorted(locations.get(monster_id, set())),
                    "groupSizeOccurrences": {str(k): v for k, v in sorted(group_sizes.get(monster_id, Counter()).items())},
                },
                "mechanicStatus": "NoEncounterRuleImported",
            })

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    document = {
        "schemaVersion": 1,
        "id": "client-monster-baseline-3.6.10.10",
        "kind": "monster-baseline-catalog",
        "status": "Verified",
        "sourceUrl": "https://www.dofus.com/en/mmorpg",
        "sourceKind": "installed-client-extraction",
        "clientVersion": "3.6.10.10",
        "generatedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "records": records,
        "notes": "Base stats, spells and observed roleplay locations only. Encounter rules must be separate evidence-backed monster-mechanic-map entries.",
    }
    OUTPUT.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"exported {len(records)} monsters to {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
