#!/usr/bin/env python3
"""Create a version-pinned, read-only Dofus 3 client-data snapshot.

The emulator must never silently turn Unity client assets into live game state.
This tool therefore writes a separate ``client_data/<version>/`` staging tree:

* ``catalogs/`` contains raw static DataRoot rows, one JSON file per source bundle;
* ``world/`` contains the evidence-backed maps/interactives and house-template extracts;
* ``manifest.json`` records the exact client version, source hashes, row counts and failures.

Server features may import a catalog only after their protocol and game-state behaviour has been
validated.  The snapshot is intentionally not a database migration and contains no account,
character, owner, or other mutable player data.

Usage::

    py tools/extract_client_snapshot.py
    py tools/extract_client_snapshot.py --client C:\\...\\Dofus-dofus3
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

import UnityPy


ROOT = Path(__file__).resolve().parents[1]
UNITY_VERSION = "6000.3.16f1"
EXPECTED_VERSION = "3.6.10.10"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def streaming_assets(client: Path) -> Path:
    for candidate in (client, client / "Dofus_Data" / "StreamingAssets"):
        if (candidate / "Content" / "Data").is_dir() and (candidate / "version").is_file():
            return candidate.resolve()
    raise RuntimeError(f"Dofus_Data/StreamingAssets was not found below {client}")


def client_version(assets: Path) -> str:
    for line in (assets / "version").read_text(encoding="utf-8-sig").splitlines():
        key, separator, value = line.partition("=")
        if separator and key.strip().lower() == "version":
            return value.strip()
    raise RuntimeError("The client version file does not contain Version=...")


def json_safe(value: Any) -> Any:
    """Convert UnityPy values to ordinary deterministic JSON values."""

    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    if isinstance(value, bytes):
        return {"encoding": "hex", "value": value.hex()}
    if isinstance(value, list):
        return [json_safe(item) for item in value]
    if isinstance(value, tuple):
        return [json_safe(item) for item in value]
    if isinstance(value, dict):
        return {str(key): json_safe(item) for key, item in value.items()}
    return str(value)


def root_rows(bundle: Path) -> list[dict[str, Any]] | None:
    environment = UnityPy.load(str(bundle))
    candidates: list[list[dict[str, Any]]] = []
    for obj in environment.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        refs = tree.get("references", {}).get("RefIds")
        if isinstance(refs, list):
            rows = [json_safe(row) for row in refs if isinstance(row, dict) and "data" in row]
            candidates.append(rows)

    if not candidates:
        return None
    # A DataRoot has one row-holder. If Unity emits an unrelated empty component too, retain the
    # actual catalog rather than concatenating unrelated records.
    return max(candidates, key=len)


def write_json(path: Path, document: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as output:
        json.dump(document, output, ensure_ascii=False, indent=2, sort_keys=True)
        output.write("\n")


def extract_catalogs(assets: Path, destination: Path, version: str) -> tuple[list[dict[str, object]], list[dict[str, str]]]:
    bundles = sorted((assets / "Content" / "Data").glob("data_assets_*dataroot.asset.bundle"))
    entries: list[dict[str, object]] = []
    failures: list[dict[str, str]] = []
    for index, bundle in enumerate(bundles, start=1):
        logical_name = bundle.name.removeprefix("data_assets_").removesuffix(".asset.bundle")
        try:
            rows = root_rows(bundle)
            if rows is None:
                failures.append({"source": bundle.name, "reason": "no references.RefIds catalog"})
                continue
            output = destination / "catalogs" / f"{logical_name}.json"
            write_json(output, {
                "schemaVersion": 1,
                "clientVersion": version,
                "unityVersion": UNITY_VERSION,
                "source": f"Dofus_Data/StreamingAssets/Content/Data/{bundle.name}",
                "sourceSha256": sha256(bundle),
                "rowCount": len(rows),
                "rows": rows,
            })
            entries.append({
                "name": logical_name,
                "source": bundle.name,
                "output": output.relative_to(destination).as_posix(),
                "rowCount": len(rows),
                "sourceSha256": sha256(bundle),
            })
        except Exception as exc:  # Keep the snapshot useful when one client bundle changes shape.
            failures.append({"source": bundle.name, "reason": str(exc)})
        if index % 25 == 0 or index == len(bundles):
            print(f"[catalogs] {index}/{len(bundles)} bundles, {len(entries)} extracted, {len(failures)} skipped", flush=True)
    return entries, failures


def run_world_extractors(client: Path, destination: Path) -> None:
    commands = [
        [sys.executable, str(ROOT / "tools" / "extraer_casas.py"), "--client", str(client),
         "--output", str(destination / "world" / "house_templates.json")],
        [sys.executable, str(ROOT / "tools" / "extraer_transiciones_mundo.py"), "--client", str(client),
         "--elements-output", str(destination / "world" / "interactive_elements.json"),
         "--transitions-output", str(destination / "world" / "interactive_transitions.json")],
    ]
    for command in commands:
        subprocess.run(command, cwd=ROOT, check=True)


def main() -> int:
    local = Path(os.environ.get("LOCALAPPDATA", ""))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, default=local / "Ankama" / "Dofus-dofus3")
    parser.add_argument("--output-root", type=Path, default=ROOT / "client_data")
    parser.add_argument("--skip-world", action="store_true", help="extract DataRoot catalogs only")
    args = parser.parse_args()

    UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
    assets = streaming_assets(args.client)
    version = client_version(assets)
    if version != EXPECTED_VERSION:
        raise RuntimeError(
            f"This extractor is validated for {EXPECTED_VERSION}; client reports {version}. "
            "Review the schema/counts before importing a new version."
        )

    final = args.output_root / version
    # Build elsewhere first. A cancelled extraction never leaves a seemingly-valid partial snapshot.
    args.output_root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=f".{version}.staging-", dir=args.output_root) as temp:
        staging = Path(temp) / version
        catalogs, failures = extract_catalogs(assets, staging, version)
        if not args.skip_world:
            run_world_extractors(args.client, staging)
        manifest = {
            "schemaVersion": 1,
            "clientVersion": version,
            "unityVersion": UNITY_VERSION,
            "sourceRoot": "Dofus_Data/StreamingAssets",
            "purpose": "Read-only extraction staging. Import only after protocol and server-state validation.",
            "catalogCount": len(catalogs),
            "catalogs": catalogs,
            "skippedBundles": failures,
            "worldExtracted": not args.skip_world,
        }
        write_json(staging / "manifest.json", manifest)
        write_json(staging / "README.json", {
            "doNot": [
                "Do not edit these files as live game state.",
                "Do not assume a static catalog supplies protocol messages, placement, ownership, quests, or mechanics.",
            ],
            "use": "Create a reviewed server importer and regression checks for each feature before use.",
        })
        if final.exists():
            import shutil
            shutil.rmtree(final)
        staging.replace(final)

    print(f"[+] Snapshot ready: {final}")
    print(f"[+] {len(catalogs)} static catalogs; {len(failures)} skipped bundles")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
