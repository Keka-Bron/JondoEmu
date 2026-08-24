#!/usr/bin/env python3
"""Export a packet's canonical sample plus chronological occurrence context for investigation."""

from __future__ import annotations

import argparse
import json
import sqlite3
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATABASE = ROOT / "bases" / "packet_telemetry.db"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--id", required=True, type=int)
    parser.add_argument("--out", type=Path, help="output JSON path; defaults to stdout")
    args = parser.parse_args()
    with sqlite3.connect(DATABASE) as db:
        db.row_factory = sqlite3.Row
        packet = db.execute("SELECT * FROM UnknownPackets WHERE Id = ?", (args.id,)).fetchone()
        if packet is None:
            raise SystemExit(f"packet {args.id} does not exist")
        occurrences = db.execute(
            "SELECT Sequence, SeenUtc, AccountId, CharacterId, MapId, SessionCorrelation, Phase, RequestId, "
            "PayloadSha256, DecodedSummary, LastError FROM PacketOccurrences WHERE PacketId = ? ORDER BY Sequence",
            (args.id,),
        ).fetchall()
    value = {key: packet[key] for key in packet.keys() if key not in {"SampleFrame", "SamplePayload"}}
    value["sample_frame_hex"] = bytes(packet["SampleFrame"]).hex()
    value["sample_payload_hex"] = bytes(packet["SamplePayload"]).hex()
    value["occurrences_chronological"] = [dict(row) for row in occurrences]
    encoded = json.dumps(value, indent=2)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(encoded + "\n", encoding="utf-8")
        print(args.out)
    else:
        print(encoded)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
