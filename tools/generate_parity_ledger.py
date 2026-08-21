#!/usr/bin/env python3
"""Generate the version-pinned client/server protocol parity ledger.

This is deliberately a conservative inventory tool.  The game schema establishes message shape,
the Cpp2IL action index establishes only a client ownership hint, and the audited server-opcode TSV
establishes static server references.  None of those sources by itself proves a round trip works,
so the generated matrix never promotes a row to ``implemented`` or ``client-verified``.

Manual evidence belongs in observations.tsv next to this generated output.  Keep that file out of
this generator so a refresh cannot erase packet captures or local test notes.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


VERSION = "3.6.10.10"
ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "datos"
OUTPUT = DATA / "parity" / VERSION
GAME_PROTO = DATA / f"protocolo_{VERSION}.proto"
CONNECTION_PROTO = DATA / f"protocolo_conexion_{VERSION}.proto"
CLIENT_INDEX = DATA / f"indice_{VERSION}.json"
SERVER_INVENTORY = DATA / f"opcodes_emulador_{VERSION}.tsv"
SERVER_SOURCE = ROOT / "Jondo.Unity.Server"

MESSAGE_START = re.compile(r"^\s*message\s+([A-Za-z_]\w*)\s*\{")
FIELD = re.compile(
    r"^\s*(?:(repeated|optional|required)\s+)?([.A-Za-z_]\w*(?:\s*<[^;=]+>)?)\s+"
    r"([A-Za-z_]\w*)\s*=\s*(\d+)(?:\s*\[[^]]*\])?\s*;"
)
OPCODE = re.compile(r"^[a-z]{3}$")
TYPE_URL = re.compile(r'type\.ankama\.com/([a-z]{3})')
LITERAL_CALL = re.compile(
    r'(ReadPayload|Push|Answer|BuildGameNodePacket)\s*\([^\n"]*"([a-z]{3})"'
)


@dataclass(frozen=True)
class MessageShape:
    protocol: str
    name: str
    fingerprint: str
    fields: str


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def normalise_block(lines: Iterable[str]) -> str:
    return "\n".join(line.strip() for line in lines if line.strip())


def parse_proto(path: Path, protocol: str) -> list[MessageShape]:
    """Read every proto message without needing protoc or generated C# source."""
    result: list[MessageShape] = []
    lines = path.read_text(encoding="utf-8").splitlines()
    name: str | None = None
    start = 0
    depth = 0

    for number, line in enumerate(lines):
        if name is None:
            match = MESSAGE_START.match(line)
            if match is None:
                continue
            name = match.group(1)
            start = number
            depth = line.count("{") - line.count("}")
            continue

        depth += line.count("{") - line.count("}")
        if depth > 0:
            continue

        block = lines[start:number + 1]
        fields: list[str] = []
        nested_depth = 0
        for field_line in block[1:-1]:
            nested_depth += field_line.count("{") - field_line.count("}")
            # Nested types are part of the message fingerprint but not the outer wire schema.
            if nested_depth != 0:
                continue
            field = FIELD.match(field_line)
            if field is None:
                continue
            cardinality, field_type, field_name, field_number = field.groups()
            prefix = (cardinality + " ") if cardinality else ""
            fields.append(f"{field_number}:{prefix}{field_type} {field_name}")

        result.append(MessageShape(
            protocol=protocol,
            name=name,
            fingerprint=sha256_bytes(normalise_block(block).encode("utf-8")),
            fields="; ".join(fields),
        ))
        name = None
        depth = 0

    if name is not None:
        raise ValueError(f"Unclosed message {name!r} in {path}")
    return result


def load_client_index() -> dict[str, dict]:
    data = json.loads(CLIENT_INDEX.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"Expected object at root of {CLIENT_INDEX}")
    return data


def client_evidence(index: dict[str, dict], opcode: str) -> tuple[str, str, str, str]:
    entry = index.get(opcode, {})
    sightings = entry.get("Sightings", []) if isinstance(entry, dict) else []
    contexts = entry.get("Context", []) if isinstance(entry, dict) else []
    if not isinstance(sightings, list):
        sightings = []
    if not isinstance(contexts, list):
        contexts = []

    methods: list[str] = []
    assemblies: list[str] = []
    has_named_action = False
    for sighting in sightings:
        if not isinstance(sighting, dict):
            continue
        method = str(sighting.get("Method", "")).strip()
        assembly = str(sighting.get("Assembly", "")).strip()
        if method and method not in methods:
            methods.append(method)
        if assembly and assembly not in assemblies:
            assemblies.append(assembly)

        # Most entries are only an obfuscated codec reference (``eql::bazg``), which proves
        # schema reachability but says nothing about a player action.  The named UI ownership
        # retained by Cpp2IL is stronger evidence and is the only automatic B-grade promotion.
        if "UI" in method or "UI" in str(sighting.get("Owner", "")):
            has_named_action = True

    owner = " | ".join(methods[:4])
    context = " | ".join(str(item).strip() for item in contexts[:2] if str(item).strip())
    if any("UI" in str(item) for item in contexts):
        has_named_action = True
    confidence = "B" if has_named_action else "C"
    return owner, context, ", ".join(assemblies), confidence


def resolve_inventory_path(recorded: str) -> str:
    """Retain the audited path but point old Launcher paths at today's Server project."""
    candidate = ROOT / recorded
    if candidate.exists():
        return recorded.replace("\\", "/")
    migrated = recorded.replace("Jondo.Unity.Launcher/", "Jondo.Unity.Server/")
    if (ROOT / migrated).exists():
        return migrated
    return recorded.replace("\\", "/")


def read_csharp_source(path: Path) -> str:
    """Source files in the legacy tree are a mix of UTF-8 and Windows-1252."""
    data = path.read_bytes()
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1252")


def load_server_inventory() -> list[dict[str, str]]:
    with SERVER_INVENTORY.open(encoding="utf-8", newline="") as handle:
        raw = list(csv.DictReader(handle, delimiter="\t"))
    expected = {"opcode", "uso", "fichero", "linea", "contexto", "nota"}
    if not raw or set(raw[0]) != expected:
        raise ValueError(f"Unexpected columns in {SERVER_INVENTORY}")

    result: list[dict[str, str]] = []
    for row in raw:
        opcode = (row.get("opcode") or "").strip()
        if not OPCODE.fullmatch(opcode):
            continue
        result.append({
            "opcode": opcode,
            "usage": (row.get("uso") or "").strip(),
            "file": resolve_inventory_path((row.get("fichero") or "").strip()),
            "line": (row.get("linea") or "").strip(),
            "context": (row.get("contexto") or "").strip(),
            "note": (row.get("nota") or "").strip(),
            "origin": "audited-inventory",
        })
    result.extend(discover_current_server_references())

    # The curated inventory and literal scan intentionally overlap.  Deduplicate exact source
    # locations while retaining both when a refactored file moved a known call to a new line.
    deduplicated: dict[tuple[str, str, str, str], dict[str, str]] = {}
    for row in result:
        key = (row["opcode"], row["usage"], row["file"], row["line"])
        existing = deduplicated.get(key)
        if existing is None or row["origin"] == "current-source":
            deduplicated[key] = row
    return list(deduplicated.values())


def discover_current_server_references() -> list[dict[str, str]]:
    """Find literal live references added after the last audited TSV refresh.

    Constants such as ``Op.Kqo`` still come from the audited inventory because resolving every
    C# alias is a separate compiler-aware task.  This narrow scanner closes the dangerous gap for
    direct type URLs and ReadPayload/Push/Answer calls, which are where newly added handlers tend
    to first appear.
    """
    rows: list[dict[str, str]] = []
    seen: set[tuple[str, str, str, str]] = set()
    for path in sorted(SERVER_SOURCE.rglob("*.cs")):
        relative = path.relative_to(ROOT).as_posix()
        for number, raw_line in enumerate(read_csharp_source(path).splitlines(), start=1):
            stripped = raw_line.strip()
            if not stripped or stripped.startswith("//") or stripped.startswith("*"):
                continue
            found: list[tuple[str, str]] = []
            for method, opcode in LITERAL_CALL.findall(raw_line):
                found.append((opcode, {
                    "ReadPayload": "read",
                    "Push": "push",
                    "Answer": "answer",
                    "BuildGameNodePacket": "push",
                }[method]))
            for opcode in TYPE_URL.findall(raw_line):
                # A type URL in a Contains expression is the dispatch branch.  In any other
                # executable expression it is a server push unless a more specific call above
                # already classified it.
                usage = "despacho" if '.Contains("type.ankama.com/' in raw_line else "push"
                found.append((opcode, usage))

            for opcode, usage in found:
                key = (opcode, usage, relative, str(number))
                if key in seen:
                    continue
                seen.add(key)
                rows.append({
                    "opcode": opcode,
                    "usage": usage,
                    "file": relative,
                    "line": str(number),
                    "context": stripped,
                    "note": "Current-source literal scan; semantic verification still required.",
                    "origin": "current-source",
                })
    return rows


def server_protocol(opcode: str, shapes: dict[tuple[str, str], MessageShape]) -> str:
    game = ("Game", opcode) in shapes
    connection = ("Connection", opcode) in shapes
    if game and connection:
        return "Ambiguous"
    if connection:
        return "Connection"
    return "Game" if game else "Unknown"


def server_direction(usage: str) -> str:
    return {
        "push": "S2C/push",
        "answer": "S2C/answer",
        "read": "C2S/read",
        "despacho": "C2S/dispatch",
        "constante": "Static",
        "descartado": "Discarded",
    }.get(usage, "Unknown")


def phase_for_file(path: str) -> str:
    lower = path.lower()
    if "connection" in lower or "haapi" in lower or "zaap" in lower:
        return "Connection"
    if "character" in lower or "worldentry" in lower:
        return "Character/world entry"
    if "fight" in lower:
        return "Fight"
    if "map" in lower or "move" in lower or "zaaptravel" in lower:
        return "World/map"
    if "inventory" in lower or "equipment" in lower or "spell" in lower:
        return "Personal data"
    if "chat" in lower:
        return "Chat/social"
    return "Unclassified"


def write_tsv(rows: Iterable[dict[str, str]], columns: list[str]) -> bytes:
    from io import StringIO

    buffer = StringIO(newline="")
    writer = csv.DictWriter(buffer, fieldnames=columns, delimiter="\t", lineterminator="\n",
                            extrasaction="raise")
    writer.writeheader()
    for row in rows:
        writer.writerow({column: row.get(column, "") for column in columns})
    return buffer.getvalue().encode("utf-8")


def build_outputs() -> dict[Path, bytes]:
    shapes = parse_proto(GAME_PROTO, "Game") + parse_proto(CONNECTION_PROTO, "Connection")
    by_shape = {(shape.protocol, shape.name): shape for shape in shapes}
    index = load_client_index()
    inventory = load_server_inventory()

    client_rows: list[dict[str, str]] = []
    for shape in sorted(shapes, key=lambda item: (item.protocol, item.name)):
        owner, context, assemblies, confidence = client_evidence(index, shape.name)
        client_rows.append({
            "protocol": shape.protocol,
            "opcode": shape.name,
            "schema_fingerprint": shape.fingerprint,
            "field_schema": shape.fields,
            "client_owner_action": owner,
            "client_context": context,
            "client_assemblies": assemblies,
            "likely_direction": "",
            "phase": "",
            "evidence_grade": confidence,
        })

    server_rows: list[dict[str, str]] = []
    for entry in sorted(inventory, key=lambda item: (item["opcode"], item["usage"], item["file"], item["line"])):
        server_rows.append({
            "protocol": server_protocol(entry["opcode"], by_shape),
            "opcode": entry["opcode"],
            "direction": server_direction(entry["usage"]),
            "usage": entry["usage"],
            "source_file": entry["file"],
            "source_line": entry["line"],
            "source_context": entry["context"],
            "audit_note": entry["note"],
            "phase_hint": phase_for_file(entry["file"]),
            "inventory_origin": entry["origin"],
        })

    inventory_by_opcode: dict[str, list[dict[str, str]]] = defaultdict(list)
    for entry in inventory:
        if entry["usage"] != "descartado":
            inventory_by_opcode[entry["opcode"]].append(entry)

    # The matrix covers each generated schema message plus any server-only opcode.  A server-only
    # opcode is deliberately left with protocol Unknown rather than guessing it belongs to Game.
    matrix_keys = set(by_shape)
    for opcode in inventory_by_opcode:
        protocol = server_protocol(opcode, by_shape)
        if protocol == "Ambiguous":
            for candidate in ("Game", "Connection"):
                matrix_keys.add((candidate, opcode))
        else:
            matrix_keys.add((protocol, opcode))

    matrix_rows: list[dict[str, str]] = []
    for protocol, opcode in sorted(matrix_keys):
        shape = by_shape.get((protocol, opcode))
        relevant = inventory_by_opcode.get(opcode, [])
        owner, context, _, client_grade = client_evidence(index, opcode)
        uses = sorted({entry["usage"] for entry in relevant})
        directions = sorted({server_direction(entry["usage"]) for entry in relevant})

        if not relevant:
            status = "shape-known" if shape else "unseen"
            next_action = "Locate client feature and capture a journey."
        elif any(usage in {"read", "despacho"} for usage in uses):
            status = "mapped"
            next_action = "Replay and verify validation, persistence and S2C ordering."
        else:
            status = "shape-known"
            next_action = "Verify the send against a client observation; no handler is inferred."

        # A server branch plus a generic obfuscated codec reference is still only structural
        # evidence.  Promote automatically only when the client index retained named UI/action
        # ownership; packet capture/replay is still required for a client-verified row.
        evidence = "B" if client_grade == "B" else "C"

        matrix_rows.append({
            "protocol": protocol,
            "opcode": opcode,
            "schema_fingerprint": shape.fingerprint if shape else "",
            "field_schema": shape.fields if shape else "",
            "client_owner_action": owner,
            "client_context": context,
            "server_directions": ", ".join(directions),
            "server_references": str(len(relevant)),
            "server_status": status,
            "evidence_grade": evidence,
            "next_action": next_action,
        })

    inputs = {
        path.relative_to(ROOT).as_posix(): sha256_file(path)
        for path in (GAME_PROTO, CONNECTION_PROTO, CLIENT_INDEX, SERVER_INVENTORY)
    }
    summary = {
        "schema_version": 1,
        "client_version": VERSION,
        "generator": "tools/generate_parity_ledger.py",
        "generator_sha256": sha256_file(Path(__file__)),
        "inputs_sha256": inputs,
        "counts": {
            "client_messages": len(client_rows),
            "server_references": len(server_rows),
            "matrix_rows": len(matrix_rows),
            "matrix_by_status": {
                status: sum(1 for row in matrix_rows if row["server_status"] == status)
                for status in sorted({row["server_status"] for row in matrix_rows})
            },
            "matrix_by_evidence": {
                grade: sum(1 for row in matrix_rows if row["evidence_grade"] == grade)
                for grade in sorted({row["evidence_grade"] for row in matrix_rows})
            },
        },
        "notes": [
            "Generated rows are deliberately conservative: this ledger does not assert client acceptance.",
            "Add capture/replay evidence in observations.tsv; do not edit generated TSV files.",
        ],
    }

    readme = """# Generated 3.6.10.10 parity ledger

Generate or verify this directory from the repository root:

```powershell
py tools/generate_parity_ledger.py
py tools/generate_parity_ledger.py --check
```

`client_messages.tsv` is the complete reconstructed protobuf shape inventory plus the Cpp2IL
client-action hints. `server_messages.tsv` merges the audited static server-opcode inventory with
a current-source scan for literal type URLs and payload calls; it retains discarded/dead references
so they cannot disappear silently. `parity_matrix.tsv` is their
conservative join. A `mapped` row means only that a reachable C2S dispatch/read exists; it is not a
claim of gameplay parity. Only captured/replayed scenarios can move a row to `implemented` and then
`client-verified`.

Keep manually recorded, scrubbed journey evidence in `observations.tsv` (not generated by this
tool). The unknown-packet database is complementary evidence, not a replacement for a replay.
"""

    return {
        OUTPUT / "client_messages.tsv": write_tsv(client_rows, [
            "protocol", "opcode", "schema_fingerprint", "field_schema", "client_owner_action",
            "client_context", "client_assemblies", "likely_direction", "phase", "evidence_grade",
        ]),
        OUTPUT / "server_messages.tsv": write_tsv(server_rows, [
            "protocol", "opcode", "direction", "usage", "source_file", "source_line",
            "source_context", "audit_note", "phase_hint", "inventory_origin",
        ]),
        OUTPUT / "parity_matrix.tsv": write_tsv(matrix_rows, [
            "protocol", "opcode", "schema_fingerprint", "field_schema", "client_owner_action",
            "client_context", "server_directions", "server_references", "server_status",
            "evidence_grade", "next_action",
        ]),
        OUTPUT / "manifest.json": (json.dumps(summary, indent=2, sort_keys=True) + "\n").encode("utf-8"),
        OUTPUT / "README.md": readme.encode("utf-8"),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if generated output is stale")
    args = parser.parse_args()

    outputs = build_outputs()
    stale: list[Path] = []
    for path, content in outputs.items():
        if not path.exists() or path.read_bytes() != content:
            stale.append(path)

    if args.check:
        if stale:
            for path in stale:
                print(f"STALE: {path.relative_to(ROOT)}")
            return 1
        print(f"Parity ledger is current ({len(outputs)} generated files).")
        return 0

    for path, content in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
    print(f"Generated {len(outputs)} ledger files in {OUTPUT.relative_to(ROOT)}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
