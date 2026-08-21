#!/usr/bin/env python3
"""Create a reviewable Draft mechanic map from one Dofus pour les Noobs URL.

This intentionally captures only source metadata.  It does not copy guide prose, infer a combat
algorithm, or activate a server rule.  Fill the evidence and client IDs after a 3.6.10.10 capture.
"""

from __future__ import annotations

import argparse
import json
import re
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MECHANICS = ROOT / "client_data" / "3.6.10.10" / "mechanics"


class TitleParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.in_title = False
        self.parts: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag.lower() == "title":
            self.in_title = True

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() == "title":
            self.in_title = False

    def handle_data(self, data: str) -> None:
        if self.in_title:
            self.parts.append(data)

    @property
    def title(self) -> str:
        return " ".join(" ".join(self.parts).split())


def slug(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")[:80] or "mechanic"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--kind", choices=("dungeon-mechanic-map", "monster-mechanic-map", "quest-mechanic-map"),
                        default="dungeon-mechanic-map")
    parser.add_argument("--id", help="stable local mechanic id; default derives from the page title")
    args = parser.parse_args()

    parsed = urllib.parse.urlparse(args.url)
    if parsed.scheme != "https" or parsed.hostname not in {"www.dofuspourlesnoobs.com", "dofuspourlesnoobs.com"}:
        raise SystemExit("--url must be an HTTPS Dofus pour les Noobs page.")
    if parsed.query or parsed.fragment or parsed.username or parsed.password:
        raise SystemExit("URLs with credentials, query strings, or fragments are not accepted.")
    request = urllib.request.Request(args.url, headers={"User-Agent": "JondoEmu-mechanic-index/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status != 200:
            raise SystemExit(f"HTTP {response.status}")
        final = response.geturl()
        final_parsed = urllib.parse.urlparse(final)
        if final_parsed.scheme != "https" or final_parsed.hostname not in {"www.dofuspourlesnoobs.com", "dofuspourlesnoobs.com"}:
            raise SystemExit("redirect left the approved source host")
        raw = response.read(4 * 1024 * 1024 + 1)
        if len(raw) > 4 * 1024 * 1024:
            raise SystemExit("page exceeds 4 MiB safety limit")

    title_parser = TitleParser()
    title_parser.feed(raw.decode("utf-8", errors="replace"))
    mechanic_id = args.id or slug(title_parser.title)
    path = MECHANICS / "dungeons" / f"{slug(mechanic_id)}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        raise SystemExit(f"refusing to overwrite existing mechanic: {path}")
    value = {
        "schemaVersion": 1,
        "id": mechanic_id,
        "kind": args.kind,
        "status": "Draft",
        "sourceUrl": final,
        "sourceRetrievedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "sourceTitle": title_parser.title,
        "clientVersion": "3.6.10.10",
        "dungeon": {"clientDungeonId": 0, "name": "", "roomMapIds": []},
        "encounters": [],
        "rules": [],
        "evidence": {
            "guideSummary": "Fill with a concise paraphrase; do not paste guide content.",
            "clientCatalogReferences": [],
            "capturedProtocolReferences": [],
            "acceptanceTests": []
        },
        "safety": {"requiresMeasuredHandler": True, "neverInventPackets": True, "playerStateWrites": []}
    }
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(path.relative_to(ROOT).as_posix())
    print("Add its relative path to mechanics/manifest.json after review.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
