#!/usr/bin/env python3
"""Refresh byte counts and hashes for the reviewed mechanics manifest entries."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MECHANICS = ROOT / "client_data" / "3.6.10.10" / "mechanics"
MANIFEST = MECHANICS / "manifest.json"


def main() -> int:
    document = json.loads(MANIFEST.read_text(encoding="utf-8"))
    for entry in document.get("entries", []):
        path = MECHANICS / entry["file"]
        if not path.is_file():
            raise SystemExit(f"Missing reviewed mechanic file: {entry['file']}")
        entry["bytes"] = path.stat().st_size
        entry["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
    MANIFEST.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"[+] refreshed {len(document.get('entries', []))} mechanic hashes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
