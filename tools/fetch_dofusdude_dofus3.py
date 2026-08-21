#!/usr/bin/env python3
"""Download the documented DofusDude Dofus 3 static catalogues into a pinned snapshot.

The service is supplementary evidence only.  It is deliberately limited to the endpoints exposed
by https://docs.dofusdu.de/dofus3/v1/: item families, sets, mounts and Almanax.  It does not
claim to provide NPC placement/dialogues, quest state, dungeon mechanics, or protocol traffic.

Before accepting any response this tool checks the service's ``/dofus3/v1/meta/version`` against
the pinned local-client version.  It writes no server database and never alters player state.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import tempfile
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
API = "https://api.dofusdu.de/dofus3/v1"
EXPECTED_VERSION = "3.6.10.10"
CATALOGUES = {
    "equipment": "items/equipment/all",
    "resources": "items/resources/all",
    "consumables": "items/consumables/all",
    "quest_items": "items/quest/all",
    "cosmetics": "items/cosmetics/all",
    "sets": "sets/all",
    "mounts": "mounts/all",
}


def get_json(url: str) -> tuple[dict[str, Any], bytes]:
    request = urllib.request.Request(url, headers={"Accept": "application/json", "User-Agent": "JondoEmu-data-audit/1.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        raw = response.read()
        if response.status != 200:
            raise RuntimeError(f"{url} returned HTTP {response.status}")
    document = json.loads(raw)
    if not isinstance(document, dict):
        raise RuntimeError(f"{url} returned a non-object JSON document")
    return document, raw


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, ensure_ascii=False, indent=2, sort_keys=True)
        output.write("\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", type=Path, default=ROOT / "client_data")
    parser.add_argument("--version", default=EXPECTED_VERSION)
    parser.add_argument("--locale", choices=("en", "fr", "all"), default="all",
                        help="catalogue locale to fetch (default: both en and fr)")
    args = parser.parse_args()
    if args.version != EXPECTED_VERSION:
        raise SystemExit(f"This importer is validated for {EXPECTED_VERSION}, not {args.version}.")

    meta, _ = get_json(f"{API}/meta/version")
    source_version = str(meta.get("version", ""))
    if source_version != args.version:
        raise SystemExit(
            f"DofusDude reports {source_version or 'no version'}; expected {args.version}. "
            "Do not mix this response with the pinned client snapshot."
        )

    locales = ("en", "fr") if args.locale == "all" else (args.locale,)
    destination_root = args.output_root / args.version / "dofusdude"
    destination_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".dofusdude-staging-", dir=destination_root) as temporary:
        staging_root = Path(temporary)
        completed: list[tuple[str, Path]] = []
        for locale in locales:
            staging = staging_root / locale
            manifest: dict[str, Any] = {
                "schemaVersion": 1,
                "source": "https://docs.dofusdu.de/dofus3/v1/",
                "apiBase": API,
                "clientVersion": args.version,
                "locale": locale,
                "apiMeta": meta,
                "downloadedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
                "catalogues": [],
                "limitations": [
                    "Static supplementary data only; it is not server-owned player state.",
                    "The documented API does not expose NPC placements or dialogue trees, quest progression, dungeon scripts, or network protocol messages.",
                ],
            }
            for name, relative_url in CATALOGUES.items():
                url = f"{API}/{locale}/{relative_url}"
                document, raw = get_json(url)
                # The response key is stable in the documented schema (items, sets or mounts). Keep
                # the API response intact rather than guessing a universal row shape.
                rows = next((value for value in document.values() if isinstance(value, list)), None)
                if rows is None:
                    raise RuntimeError(f"{url} has no list catalogue")
                write_json(staging / f"{name}.json", document)
                manifest["catalogues"].append({
                    "name": name,
                    "url": url,
                    "output": f"{name}.json",
                    "rowCount": len(rows),
                    "sha256": hashlib.sha256(raw).hexdigest(),
                })
                print(f"[+] {locale}/{name}: {len(rows):,} rows", flush=True)
            write_json(staging / "manifest.json", manifest)
            completed.append((locale, staging))

        # Do not replace a locale until its complete version-checked staging snapshot exists.
        import shutil
        for locale, staging in completed:
            final = destination_root / locale
            if final.exists():
                shutil.rmtree(final)
            staging.replace(final)

    print(f"[+] DofusDude snapshot(s) ready: {destination_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
