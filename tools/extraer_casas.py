# -*- coding: utf-8 -*-
"""Extract the official house-template catalog from an installed Dofus client.

The catalog contains *types* (price, name, appearance and room count). Exterior
door placement, ownership and destinations are server state and deliberately do
not get invented here.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import UnityPy

UNITY_VERSION = "6000.3.16f1"
DEFAULT_CLIENT = Path.home() / "AppData/Local/Ankama/Dofus-dofus3"
ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = ROOT / "datos/house_templates_3.6.10.10.json"

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION


def read_rows(bundle: Path) -> list[dict]:
    environment = UnityPy.load(str(bundle))
    for obj in environment.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        refs = tree.get("references", {}).get("RefIds")
        if isinstance(refs, list):
            return [row["data"] for row in refs if isinstance(row, dict) and "data" in row]
    raise RuntimeError(f"{bundle.name} has no references.RefIds catalog")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--client", type=Path, default=DEFAULT_CLIENT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    data_dir = args.client / "Dofus_Data/StreamingAssets/Content/Data"
    bundle = data_dir / "data_assets_housesdataroot.asset.bundle"
    if not bundle.is_file():
        raise SystemExit(f"[!] Missing client bundle: {bundle}")

    rows = sorted(read_rows(bundle), key=lambda row: int(row["typeId"]))
    required = {
        "typeId", "defaultPrice", "nameId", "descriptionId", "gfxId", "roomCount"
    }
    for row in rows:
        missing = required.difference(row)
        if missing:
            raise RuntimeError(f"house type {row.get('typeId')} lacks {sorted(missing)}")

    version_file = args.client / "Dofus_Data/StreamingAssets/version"
    client_version = "3.6.10.10"
    if version_file.is_file():
        for line in version_file.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("Version="):
                client_version = line.split("=", 1)[1].strip()

    result = {
        "clientVersion": client_version,
        "source": "Dofus_Data/StreamingAssets/Content/Data/data_assets_housesdataroot.asset.bundle",
        "note": "Official static templates only; placements, destinations and owners are server state.",
        "houses": rows,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"[+] {args.output}: {len(rows)} official house templates")


if __name__ == "__main__":
    main()
