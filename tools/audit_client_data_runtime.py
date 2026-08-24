#!/usr/bin/env python3
"""Report whether the server-ready client_data snapshot is complete and integrity-checked."""
from __future__ import annotations

import hashlib
import json
import os
import sqlite3
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SNAPSHOT = ROOT / "client_data" / "3.6.10.10" / "server"
VERSION_ROOT = SNAPSHOT.parent
MECHANICS = VERSION_ROOT / "mechanics"
REQUIRED_CATALOGS = {
    "areasdataroot.json", "subareasdataroot.json", "mapsinformationdataroot.json",
    "mapscoordinatesdataroot.json", "monstersdataroot.json", "spellsdataroot.json",
    "spelllevelsdataroot.json", "effectsdataroot.json", "itemsdataroot.json", "itemtypesdataroot.json",
    "dungeonsdataroot.json", "npcsdataroot.json", "npcmessagesdataroot.json",
    "questsdataroot.json", "queststepsdataroot.json", "questobjectivesdataroot.json",
    "queststeprewardsdataroot.json", "skillsdataroot.json", "recipesdataroot.json",
}

def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main() -> int:
    manifest_path = SNAPSHOT / "manifest.json"
    if not manifest_path.is_file():
        print("FAIL: server snapshot manifest is missing. Run build_server_client_data_snapshot.py.")
        return 2
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    failures = []
    if manifest.get("clientVersion") != "3.6.10.10": failures.append("clientVersion mismatch")
    if manifest.get("serverProtocolVersion") != "3.6.10.10": failures.append("serverProtocolVersion mismatch")
    for item in manifest.get("files", []):
        path = SNAPSHOT / item["path"]
        if not path.is_file(): failures.append(f"missing {item['path']}")
        elif path.stat().st_size != item["bytes"]: failures.append(f"size mismatch {item['path']}")
        elif sha(path) != item["sha256"]: failures.append(f"hash mismatch {item['path']}")

    extraction = json.loads((VERSION_ROOT / "manifest.json").read_text(encoding="utf-8"))
    outputs = {x["output"] for x in extraction.get("catalogs", [])}
    if extraction.get("clientVersion") != "3.6.10.10" or extraction.get("worldExtracted") is not True:
        failures.append("raw extraction version/world marker mismatch")
    for name in sorted(REQUIRED_CATALOGS):
        if f"catalogs/{name}" not in outputs or not (VERSION_ROOT / "catalogs" / name).is_file():
            failures.append(f"missing required raw catalogue {name}")

    integrity_path = SNAPSHOT / "catalog_integrity.json"
    integrity = json.loads(integrity_path.read_text(encoding="utf-8")) if integrity_path.is_file() else {}
    integrity_entries = {x.get("path"): x for x in integrity.get("catalogs", []) if isinstance(x, dict)}
    if integrity.get("clientVersion") != "3.6.10.10":
        failures.append("catalogue integrity manifest version mismatch")
    for relative in sorted(outputs):
        item = integrity_entries.get(relative)
        path = VERSION_ROOT / relative
        if item is None:
            failures.append(f"catalogue has no integrity record {relative}")
        elif path.stat().st_size != item.get("bytes"):
            failures.append(f"size mismatch catalogue {relative}")
        elif sha(path) != item.get("sha256"):
            failures.append(f"hash mismatch catalogue {relative}")

    mechanics_manifest = json.loads((MECHANICS / "manifest.json").read_text(encoding="utf-8"))
    if mechanics_manifest.get("clientVersion") != "3.6.10.10": failures.append("mechanics version mismatch")
    for item in mechanics_manifest.get("entries", []):
        path = MECHANICS / item["file"]
        if not path.is_file(): failures.append(f"missing mechanic {item['file']}")
        elif path.stat().st_size != item.get("bytes"): failures.append(f"size mismatch mechanic {item['file']}")
        elif sha(path) != item.get("sha256"): failures.append(f"hash mismatch mechanic {item['file']}")

    coverage_path = MECHANICS / "incarnam" / "content-coverage.json"
    coverage = json.loads(coverage_path.read_text(encoding="utf-8")) if coverage_path.is_file() else {}
    if coverage.get("region", {}).get("clientAreaId") != 45:
        failures.append("Incarnam area-45 coverage is missing")

    bootstrap = SNAPSHOT / "world.zip"
    if bootstrap.is_file():
        with tempfile.TemporaryDirectory(prefix="jondo-bootstrap-audit-") as temporary:
            with zipfile.ZipFile(bootstrap) as archive:
                archive.extract("world.db", temporary)
            connection = sqlite3.connect(os.path.join(temporary, "world.db"))
            try:
                existing = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
                mutable = ("Characters", "CharacterAppearance", "CharacterItems", "CharacterSpellBar",
                           "CharacterSpellChoices", "CharacterWardrobe", "HavenBag", "HavenBagChest",
                           "HavenBagFurniture", "Houses")
                for table in mutable:
                    if table in existing and connection.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0]:
                        failures.append(f"player state leaked into bootstrap table {table}")
            finally:
                connection.close()
    if failures:
        print("FAIL: " + "; ".join(failures))
        return 2
    print(f"PASS: {len(manifest['files'])} version-pinned static runtime files, all {len(outputs)} raw catalogues ({len(REQUIRED_CATALOGS)} core), and {len(mechanics_manifest['entries'])} mechanic files verified.")
    print(f"Incarnam: {len(coverage['region']['mapIds'])} maps, {len(coverage['monsters'])} monsters, {len(coverage['dungeons'])} dungeon, {len(coverage['quests'])} relevant quests inventoried.")
    print("Boundary: bases/world.db is mutable state plus a version-tagged static index cache; client_data is the authoritative immutable input.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
