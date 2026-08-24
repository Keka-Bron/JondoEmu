#!/usr/bin/env python3
"""Create the version-pinned runtime game-data snapshot consumed by Jondo Server.

This copies only static, regenerable inputs. The world bootstrap is sanitized by
build_static_world_bootstrap.py before inclusion, so it contains no character/player rows. The
manifest is verified fail-closed by Paths before any gameplay service starts.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import sys
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
    "waypoints.json", "havenbag.json", "house_templates_3.6.10.10.json",
    "casas_mundo_3.6.10.10.json", "titles_ornaments.json", "cosmetics.json", "cosmetic_skins.json",
    "mounts.json", "npc_shops.json", "spell_variants.json", "world_etapa1_tras_elegir_personaje.bin",
    "world_etapa2_tras_confirmar.bin", "world_etapa3_mapa.bin", "anomalias_3.6.10.10.json",
    "zaapis_3.6.10.10.json", "caracteristicas_kub.json", "invocaciones_duracion.json",
    "mascoturas.json", "monturas_colores.json",
]
NESTED = ["JsonFromDofusDude/jobs.json", "JsonFromDofusDude/skills.json", "JsonFromDofusDude/recipes.json"]
VERSION_OWNED = [
    "world_interactive_returns_3.6.10.10.json", "zaap_overrides.json", "world.zip",
    "catalog_integrity.json", "map_scrolls.json",
    "effect_runtime_semantics.json",
]

def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main() -> int:
    subprocess.run([sys.executable, str(ROOT / "tools" / "build_static_world_bootstrap.py")], check=True)
    subprocess.run([sys.executable, str(ROOT / "tools" / "build_catalog_integrity_manifest.py")], check=True)
    sources = [(name, DATA / name) for name in FILES]
    sources += [(name, DATA / name) for name in NESTED]
    sources += [(name, OUTPUT / name) for name in VERSION_OWNED]
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
            "purpose": "Static runtime inputs and a player-free bootstrap cache for Jondo Server. Never put player/account state here.",
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
