#!/usr/bin/env python3
"""Create the versioned empty-world bootstrap without copying player state.

The historical datos/world.zip contains useful static indexes but also test characters and their
inventory. This tool deletes every mutable player table from a temporary copy and writes only the
sanitized bootstrap into client_data. It never opens or modifies bases/world.db.
"""

from __future__ import annotations

import sqlite3
import shutil
import tempfile
import zipfile
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "datos" / "world.zip"
OUTPUT = ROOT / "client_data" / "3.6.10.10" / "server" / "world.zip"
MAP_SCROLLS_OUTPUT = OUTPUT.with_name("map_scrolls.json")
MUTABLE_TABLES = (
    "HavenBagFurniture", "HavenBagChest", "HavenBag", "CharacterWardrobe",
    "CharacterSpellChoices", "CharacterSpellBar", "CharacterItems",
    "CharacterAppearance", "Characters", "Houses",
)


def main() -> int:
    if not SOURCE.is_file():
        raise SystemExit(f"missing historical bootstrap: {SOURCE}")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="jondo-static-world-") as temporary:
        directory = Path(temporary)
        with zipfile.ZipFile(SOURCE) as archive:
            archive.extract("world.db", directory)
        database = directory / "world.db"
        connection = sqlite3.connect(database)
        try:
            existing = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            scroll_rows = [
                {"mapId": row[0], "rightMapId": row[1], "bottomMapId": row[2],
                 "leftMapId": row[3], "topMapId": row[4]}
                for row in connection.execute(
                    "SELECT MapId, RightMapId, BottomMapId, LeftMapId, TopMapId FROM MapScrolls ORDER BY MapId"
                )
            ]
            for table in MUTABLE_TABLES:
                if table in existing:
                    connection.execute(f'DELETE FROM "{table}"')
            connection.commit()
            for table in MUTABLE_TABLES:
                if table in existing:
                    count = connection.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0]
                    if count:
                        raise SystemExit(f"failed to empty mutable table {table}")
            connection.execute("VACUUM")
        finally:
            connection.close()
        info = zipfile.ZipInfo("world.db", date_time=(1980, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with zipfile.ZipFile(OUTPUT, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            with database.open("rb") as source, archive.open(info, "w", force_zip64=True) as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
        MAP_SCROLLS_OUTPUT.write_text(json.dumps({
            "schemaVersion": 1,
            "clientVersion": "3.6.10.10",
            "sourceKind": "versioned-static-world-index",
            "rows": scroll_rows,
        }, separators=(",", ":")) + "\n", encoding="utf-8")
    print(f"wrote player-free static bootstrap: {OUTPUT.relative_to(ROOT)}")
    print(f"wrote {len(scroll_rows)} map-scroll rows: {MAP_SCROLLS_OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
