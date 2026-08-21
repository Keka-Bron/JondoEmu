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
    parser.add_argument("--set-status", metavar="STATUS", help="set the status for --id")
    parser.add_argument("--notes", help="replace investigation notes when setting a status")
    args = parser.parse_args()

    if args.set_status and not args.id:
        parser.error("--set-status requires --id")
    if args.notes and not args.id:
        parser.error("--notes requires --id")

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
                print(json.dumps(record(row, include_sample=True), indent=2))
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
