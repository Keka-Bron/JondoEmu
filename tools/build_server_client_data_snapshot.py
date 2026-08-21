#!/usr/bin/env python3
"""Create the version-pinned runtime game-data snapshot consumed by Jondo Server.

This copies only static, regenerable inputs. It deliberately excludes SQLite databases because
they contain player/account state in the current schema. The manifest is verified by Paths before
the server prefers this snapshot over legacy datos/ files.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION = "3.6.10.10"
DATA = ROOT / "datos"
OUTPUT = ROOT / "client_data" / VERSION / "server"
FILES = [
    "map_walkable_cells.json", "map_fight_cells.json", "character_xp.json", "breed_looks.json",
    "heads.json", "breed_stats.json", "item_sets.json", "item_effect_fields.json", "dungeons.json",
    "interactive_elements_3.6.10.10.json", "world_interactive_transitions_3.6.10.10.json",
    "waypoints.json", "zaap_overrides.json", "havenbag.json", "house_templates_3.6.10.10.json",
    "casas_mundo_3.6.10.10.json", "titles_ornaments.json", "cosmetics.json", "cosmetic_skins.json",
    "mounts.json", "npc_shops.json", "spell_variants.json", "world_etapa1_tras_elegir_personaje.bin",
    "world_etapa2_tras_confirmar.bin", "world_etapa3_mapa.bin",
]
NESTED = ["JsonFromDofusDude/jobs.json", "JsonFromDofusDude/skills.json", "JsonFromDofusDude/recipes.json"]

def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main() -> int:
    sources = [(name, DATA / name) for name in FILES]
    sources += [(name, DATA / name) for name in NESTED]
    missing = [name for name, source in sources if not source.is_file()]
    if missing:
        raise SystemExit("Missing required generated runtime inputs: " + ", ".join(missing))
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".server-snapshot-", dir=OUTPUT.parent) as temporary:
        staging = Path(temporary) / "server"
        records = []
        for name, source in sources:
            target = staging / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            records.append({"path": name.replace("\\", "/"), "bytes": source.stat().st_size, "sha256": digest(source)})
        manifest = {
            "schemaVersion": 1,
            "clientVersion": VERSION,
            "serverProtocolVersion": VERSION,
            "generatedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
            "purpose": "Static runtime inputs for Jondo Server. Never put player/account state here.",
            "files": records,
            "excluded": ["bases/*.db", "accounts", "characters", "inventory", "quest progress", "ownership", "telemetry"],
        }
        (staging / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        if OUTPUT.exists():
            shutil.rmtree(OUTPUT)
        staging.replace(OUTPUT)
    print(f"[+] {len(records)} static runtime files: {OUTPUT}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
