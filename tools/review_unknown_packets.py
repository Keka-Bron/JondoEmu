#!/usr/bin/env python3
"""Read or triage the emulator's durable unknown-packet queue.

The server owns writes to bases/packet_telemetry.db.  This command is intentionally
small and dependency-free so a human or a Codex heartbeat can inspect the queue
without parsing an unstructured console log.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATABASE = ROOT / "bases" / "packet_telemetry.db"


def connect() -> sqlite3.Connection:
    if not DATABASE.exists():
        raise FileNotFoundError(
            f"Telemetry database does not exist yet: {DATABASE}. Start Jondo Server once first."
        )
    connection = sqlite3.connect(DATABASE)
    connection.row_factory = sqlite3.Row
    return connection


def record(row: sqlite3.Row, include_sample: bool) -> dict[str, object]:
    item = {
        "id": row["Id"],
        "status": row["Status"],
        "classification": row["Classification"],
        "protocol": row["Protocol"],
        "direction": row["Direction"],
        "opcode": row["Opcode"],
        "type_url": row["TypeUrl"],
        "envelope_root": row["EnvelopeRoot"],
        "request_id": row["RequestId"],
        "wire_signature": row["WireSignature"],
        "decoded_summary": row["DecodedSummary"],
        "first_seen_utc": row["FirstSeenUtc"],
        "last_seen_utc": row["LastSeenUtc"],
        "occurrences": row["Occurrences"],
        "account_id": row["AccountId"],
        "character_id": row["CharacterId"],
        "map_id": row["MapId"],
        "phase": row["Phase"],
        "client_version": row["ClientVersion"],
        "frame_sha256": row["FrameSha256"],
        "payload_sha256": row["PayloadSha256"],
        "last_error": row["LastError"],
        "notes": row["Notes"],
        "handler_hint": row["HandlerHint"],
    }
    if include_sample:
        item["sample_frame_hex"] = bytes(row["SampleFrame"]).hex()
        item["sample_payload_hex"] = bytes(row["SamplePayload"]).hex()
    return item


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--new", action="store_true", help="list actionable queue rows (the default)")
    group.add_argument("--all", action="store_true", help="list every observed unsupported/no-reply shape")
    group.add_argument(
        "--investigating",
        action="store_true",
        help="list rows that need client/reference-server evidence, oldest first",
    )
    group.add_argument(
        "--summary",
        action="store_true",
        help="print durable queue counts by status and highest-volume unresolved opcode",
    )
    parser.add_argument("--id", type=int, help="show one queue row; includes raw replay samples")
    parser.add_argument(
        "--context",
        action="store_true",
        help="with --id, include the closest chronological C2S/S2C telemetry events from its session",
    )
    parser.add_argument(
        "--context-events",
        type=int,
        default=24,
        help="maximum events returned by --context (default: 24)",
    )
    parser.add_argument("--set-status", metavar="STATUS", help="set the status for --id")
    parser.add_argument("--notes", help="replace investigation notes when setting a status")
    args = parser.parse_args()

    if args.set_status and not args.id:
        parser.error("--set-status requires --id")
    if args.notes and not args.id:
        parser.error("--notes requires --id")
    if args.context and not args.id:
        parser.error("--context requires --id")
    if args.context_events < 1:
        parser.error("--context-events must be positive")

    try:
        with connect() as connection:
            if args.set_status or args.notes:
                cursor = connection.execute(
                    "UPDATE UnknownPackets SET Status = COALESCE(?, Status), Notes = COALESCE(?, Notes) WHERE Id = ?",
                    (args.set_status, args.notes, args.id),
                )
                if cursor.rowcount != 1:
                    raise LookupError(f"No telemetry packet with id {args.id}")
                connection.commit()

            if args.id:
                row = connection.execute("SELECT * FROM UnknownPackets WHERE Id = ?", (args.id,)).fetchone()
                if row is None:
                    raise LookupError(f"No telemetry packet with id {args.id}")
                result = record(row, include_sample=True)
                if args.context:
                    # Timeline storage was introduced after the first telemetry captures.  A
                    # helpful empty result is preferable to making triage fail against an older
                    # database; the next live session will populate it automatically.
                    exists = connection.execute(
                        "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ObservedPacketEvents'"
                    ).fetchone()
                    if exists:
                        session = row["SessionCorrelation"]
                        seen = row["LastSeenUtc"]
                        if session and seen:
                            events = connection.execute(
                                "SELECT Sequence, SeenUtc, Protocol, Direction, Opcode, TypeUrl, "
                                "WireSignature, DecodedSummary, AccountId, CharacterId, MapId, "
                                "Phase, ClientVersion, FrameSha256, PayloadSha256 "
                                "FROM ObservedPacketEvents WHERE SessionCorrelation = ? "
                                "ORDER BY ABS(julianday(SeenUtc) - julianday(?)), Sequence DESC LIMIT ?",
                                (session, seen, args.context_events),
                            ).fetchall()
                            # Nearest-first is useful for SQL selection but difficult to read as
                            # a protocol journey.  Present the retained window chronologically.
                            result["context_events"] = [dict(event) for event in reversed(events)]
                        else:
                            result["context_events"] = []
                    else:
                        result["context_events"] = []
                print(json.dumps(result, indent=2))
                return 0

            if args.summary:
                statuses = connection.execute(
                    "SELECT Status, COUNT(*) AS rows, COALESCE(SUM(Occurrences), 0) AS occurrences "
                    "FROM UnknownPackets GROUP BY Status ORDER BY Status"
                ).fetchall()
                unresolved = connection.execute(
                    "SELECT Opcode, Status, COUNT(*) AS rows, COALESCE(SUM(Occurrences), 0) AS occurrences "
                    "FROM UnknownPackets WHERE Status IN ('New', 'Investigating', 'BlockedEvidence') "
                    "GROUP BY Opcode, Status ORDER BY occurrences DESC, rows DESC, Opcode LIMIT 20"
                ).fetchall()
                print(json.dumps({
                    "status_counts": [dict(row) for row in statuses],
                    "highest_volume_unresolved": [dict(row) for row in unresolved],
                }, indent=2))
                return 0

            if args.all:
                where = ""
                order = "LastSeenUtc DESC, Id DESC"
            elif args.investigating:
                where = "WHERE Status IN ('Investigating', 'BlockedEvidence')"
                # Oldest first prevents the recurring review from repeatedly looking only at
                # the newest capture while the established evidence backlog never moves.
                order = "FirstSeenUtc ASC, Id ASC"
            else:
                where = "WHERE Status = 'New'"
                order = "LastSeenUtc DESC, Id DESC"
            rows = connection.execute(
                f"SELECT * FROM UnknownPackets {where} ORDER BY {order}"
            ).fetchall()
            print(json.dumps([record(row, include_sample=False) for row in rows], indent=2))
            return 0
    except (FileNotFoundError, LookupError) as exc:
        print(str(exc), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
