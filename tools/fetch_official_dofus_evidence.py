#!/usr/bin/env python3
"""Capture minimal, auditable metadata from an approved official Dofus page.

The tool deliberately stores metadata and a digest, not a copied article.  Official pages are
evidence for a dated gameplay requirement; the installed 3.6.10.10 client and verified traffic
remain the source for IDs, packets and database mutations.
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
REGISTRY_PATH = ROOT / "datos" / "official_dofus_sources_3.6.10.10.json"


class _MetadataParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.title_parts: list[str] = []
        self.in_title = False
        self.canonical: str | None = None
        self.description: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {key.lower(): value or "" for key, value in attrs}
        if tag.lower() == "title":
            self.in_title = True
        if tag.lower() == "link" and values.get("rel", "").lower() == "canonical":
            self.canonical = values.get("href") or None
        if tag.lower() == "meta" and values.get("name", "").lower() == "description":
            self.description = values.get("content") or None

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() == "title":
            self.in_title = False

    def handle_data(self, data: str) -> None:
        if self.in_title:
            self.title_parts.append(data)

    @property
    def title(self) -> str:
        return " ".join(" ".join(self.title_parts).split())


def _load_registry() -> dict:
    return json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))


def _source_by_id(registry: dict, source_id: str) -> dict:
    for source in registry["sources"]:
        if source["id"] == source_id:
            return source
    raise ValueError(f"Unknown source id: {source_id}")


def _validate_url(url: str, source: dict) -> urllib.parse.ParseResult:
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "https" or not parsed.hostname:
        raise ValueError("Only absolute HTTPS URLs are accepted.")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("Credentials, query strings and fragments are not allowed in evidence URLs.")
    if parsed.hostname.lower() not in {host.lower() for host in source["allowedHosts"]}:
        raise ValueError(f"Host {parsed.hostname!r} is not approved for {source['id']}.")
    return parsed


def _fetch(url: str, source: dict) -> tuple[str, int, bytes]:
    request = urllib.request.Request(url, headers={"User-Agent": "JondoEmu/official-evidence"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            final_url = response.geturl()
            _validate_url(final_url, source)
            body = response.read(8 * 1024 * 1024 + 1)
            if len(body) > 8 * 1024 * 1024:
                raise ValueError("Official evidence page exceeds the 8 MiB safety limit.")
            return final_url, response.status, body
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"HTTP {error.code} for {url}") from error


def _safe_slug(value: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")
    return slug[:80] or "official-page"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="source id from datos/official_dofus_sources_3.6.10.10.json")
    parser.add_argument("--url", required=True, help="specific official HTTPS page to record")
    parser.add_argument("--tag", action="append", default=[], help="feature tag (repeatable)")
    parser.add_argument("--note", default="", help="short non-copyrighted implementation note")
    args = parser.parse_args()

    registry = _load_registry()
    source = _source_by_id(registry, args.source)
    _validate_url(args.url, source)
    final_url, status, body = _fetch(args.url, source)
    decoded = body.decode("utf-8", errors="replace")
    parser_html = _MetadataParser()
    parser_html.feed(decoded)
    retrieved = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    canonical = urllib.parse.urljoin(final_url, parser_html.canonical) if parser_html.canonical else final_url
    output_dir = ROOT / "client_data" / registry["pinnedClientVersion"] / "official" / "evidence"
    output_dir.mkdir(parents=True, exist_ok=True)
    record = {
        "schemaVersion": 1,
        "sourceId": source["id"],
        "url": args.url,
        "canonicalUrl": canonical,
        "retrievedUtc": retrieved,
        "httpStatus": status,
        "title": html.unescape(parser_html.title),
        "description": html.unescape(parser_html.description or ""),
        "contentSha256": hashlib.sha256(body).hexdigest(),
        "contentBytes": len(body),
        "featureTags": sorted(set(args.tag)),
        "implementationNote": args.note,
        "implementationStatus": "EvidenceOnly",
        "sourcePolicy": source["versionRule"],
    }
    path = output_dir / f"{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}_{_safe_slug(parser_html.title or source['id'])}.json"
    path.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(path.relative_to(ROOT).as_posix())
    print(json.dumps(record, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, RuntimeError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2)
