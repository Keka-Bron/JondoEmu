#!/usr/bin/env python3
"""Hash every normalized catalogue in the pinned installed-client extraction.

The extraction manifest identifies the source bundles, but its sourceSha256 values do not hash
the generated JSON files themselves.  This companion manifest is included in the signed server
snapshot so the runtime can reject a truncated or edited catalogue before importing it.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VERSION = "3.6.10.10"
VERSION_ROOT = ROOT / "client_data" / VERSION
OUTPUT = VERSION_ROOT / "server" / "catalog_integrity.json"


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def main() -> int:
    extraction = json.loads((VERSION_ROOT / "manifest.json").read_text(encoding="utf-8"))
    if extraction.get("clientVersion") != VERSION or extraction.get("worldExtracted") is not True:
        raise SystemExit("The installed-client extraction is incomplete or has the wrong version.")

    records: list[dict[str, object]] = []
    for entry in extraction.get("catalogs", []):
        relative = entry.get("output")
        if not isinstance(relative, str) or not relative.startswith("catalogs/") or ".." in relative:
            raise SystemExit(f"Unsafe catalogue output in extraction manifest: {relative!r}")
        path = VERSION_ROOT / relative
        if not path.is_file():
            raise SystemExit(f"Missing extracted catalogue: {relative}")
        records.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest(path)})

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    document = {"schemaVersion": 1, "clientVersion": VERSION, "catalogs": records}
    OUTPUT.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"[+] hashed {len(records)} normalized catalogues: {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
