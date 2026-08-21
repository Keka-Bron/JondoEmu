# Packet telemetry investigation rules

The telemetry database is a backlog of *observations*, not a protocol map.  An
entry becomes `Implemented` only after the server has a validated parser,
authorization/state handling where relevant, and an observed or client-proven
S2C result.  A C2S type name is not enough to invent a reply.

Run these commands from the emulator root:

```powershell
py tools/review_unknown_packets.py --new
py tools/review_unknown_packets.py --summary
py tools/review_unknown_packets.py --investigating
py tools/review_unknown_packets.py --id <id>
```

`--new` remains the safe automation queue.  `--investigating` is deliberately
oldest-first so established evidence debt is reviewed instead of being hidden
behind newer traffic.  `--summary` exposes both status counts and the
highest-volume unresolved opcode families.

## Current evidence-backed priorities

| Opcode | What client inspection proves | Missing proof before a handler |
| --- | --- | --- |
| `hov` | Self-emote selection; payload field 2 is the selected emote id. | A reference trace for one emote: actor id, S2C animation event(s), order, and whether no reply is valid. |
| `khl` | Emoticon request; field 1 is an `EmoticonsDataRoot` id. | Reference result/error and actor-state update. |
| `ipz` + `iqa` | A paired client sequence carrying the same UI context id. | The source UI/action and its S2C trace; no domain mutation is established. |
| `irl` | Nested `isu` settings/action payload. | The originating UI action and adjacent S2C frames. |
| `jjm` | A 17-field C2S request family, seen in multiple shapes. | Isolated action capture plus S2C trace for each distinct wire signature. |

These rows have been inspected against the pinned 3.6.10.10 schema and Cpp2IL
call sites.  They are kept as `BlockedEvidence`/`Investigating` instead of
being turned into guessed no-ops, because a wrong roleplay, account, or combat
mutation is worse than an explicit compatibility gap.

Known map synchronization `kmv` is already handled as an evidence-backed
no-reply packet in `GameNodeProxy`; its historic telemetry row is therefore not
an unimplemented map feature.

## Handler checklist

1. Identify the exact client UI/action and fields from the pinned client.
2. Capture that action in isolation with every adjacent S2C frame.
3. Compare the client receiver and an existing nearby server handler.
4. Implement bounded parsing, character/account/map authorization, persistence
   only if the action changes state, and the observed S2C result.
5. Add/update `docs/opcodes.md`, then mark the concrete telemetry row
   `Implemented`.  If it needs no reply, mark `NoReplyObserved` only when the
   capture proves that.
