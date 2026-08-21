#!/usr/bin/env python3
"""Report whether the server-ready client_data snapshot is complete and integrity-checked."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SNAPSHOT = ROOT / "client_data" / "3.6.10.10" / "server"

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
    for item in manifest.get("files", []):
        path = SNAPSHOT / item["path"]
        if not path.is_file(): failures.append(f"missing {item['path']}")
        elif path.stat().st_size != item["bytes"]: failures.append(f"size mismatch {item['path']}")
        elif sha(path) != item["sha256"]: failures.append(f"hash mismatch {item['path']}")
    if failures:
        print("FAIL: " + "; ".join(failures))
        return 2
    print(f"PASS: {len(manifest['files'])} version-pinned static runtime files verified.")
    print("Boundary: current world.db still contains legacy static template tables alongside player data; do not delete it until that schema is split.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
