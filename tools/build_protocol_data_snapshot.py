#!/usr/bin/env python3
"""Copy extracted protocol schema/index into a versioned client_data protocol catalogue."""

from __future__ import annotations

import hashlib
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VERSION = "3.6.10.10"
SOURCE = ROOT / "datos"
TARGET = ROOT / "client_data" / VERSION / "protocol"
FILES = {
    f"indice_{VERSION}.json": "opcode-index.json",
    f"protocolo_{VERSION}.proto": "game-protocol.proto",
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    TARGET.mkdir(parents=True, exist_ok=True)
    entries = []
    for source_name, target_name in FILES.items():
        source = SOURCE / source_name
        if not source.exists():
            raise SystemExit(f"missing extracted protocol asset: {source}")
        target = TARGET / target_name
        shutil.copy2(source, target)
        entries.append({"path": target_name, "bytes": target.stat().st_size, "sha256": sha256(target)})
    manifest = {
        "schemaVersion": 1, "clientVersion": VERSION,
        "generatedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "files": entries,
        "notes": "Schema/index drive decoding and telemetry investigation. They do not generate state-changing handlers."
    }
    (TARGET / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"protocol snapshot: {TARGET.relative_to(ROOT)} ({len(entries)} extracted files)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
