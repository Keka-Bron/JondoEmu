# Unknown-packet telemetry queue

`bases/packet_telemetry.db` is a diagnostic SQLite database, separate from `auth.db` and
`world.db`.  It is created automatically when Jondo Server starts.  It contains no server state
needed to play; it may be copied for protocol investigation or deleted to reset diagnostics.

## What creates a row

The game dispatcher records three client-to-server categories:

| Classification | Initial status | Meaning |
|---|---|---|
| `Unhandled` | `New` | No server dispatch branch matched the packet. |
| `LegacyIgnored` | `New` | A legacy broad ignore path dropped it; it requires a handler or an evidence-backed reclassification. |
| `KnownNoReply` | `NoReplyObserved` | The action is known and captures/client behaviour establish that it requires no answer. It remains in the map but is not queued for automatic work. |
| `DecodeFailure` | `New` | A connection-server frame could not be decoded. |

Rows are deduplicated by protocol, direction, classification, envelope root, opcode and recursive
protobuf wire shape.  Repeated packets increase `Occurrences` and update `LastSeenUtc`; the first
frame/body is retained as a replay sample.  A row includes type URL/opcode, root and request ID,
wire signature, compact structural decode, hashes, full sample frame/payload, first/last times,
session correlation, account/character/map IDs, session phase, client version, error and review
fields.  The raw sample is local diagnostic data: do not attach it to public issues without
redacting it.

## Review command

Run from the emulator root:

```powershell
py tools/review_unknown_packets.py --new
py tools/review_unknown_packets.py --id 17
py tools/review_unknown_packets.py --id 17 --set-status Investigating --notes "Mapped to UI action …"
```

`--new` emits compact JSON without samples and is safe for routine polling.  `--id` includes the
hex replay sample, which is used only after the packet has been selected for analysis.

## Automatic review rule

The Codex heartbeat checks only `Status='New'`.  For each new row it must identify the client UI
action/schema/capture sequence, then add a scoped parser, validation, state mutation and S2C
response only when evidence establishes the behaviour.  It updates the row to `Implemented`,
`NoReplyObserved` or `Investigating` with notes, runs the build and republishes the server when it
changes code.  It must never invent an acknowledgement or mutate a handler solely from an opaque
packet sample.

## 21 August 2026 audit: non-implemented backlog

The live queue was reviewed as individual wire shapes, then collapsed here only to show the
implementation dependency.  There are **73** `Investigating` rows but only **41** opcodes:
`jjm` accounts for 32 payload shapes and `kef` for two.  All have a pinned 3.6.10.10 schema,
client owner/call-site evidence and an individual note in SQLite.  That is enough to parse a
future request safely, but it is not enough to fabricate the authoritative result of an action.

| Cluster | Queue rows / opcodes | What the evidence establishes | Missing proof before a server handler can be correct |
|---|---|---|---|
| Social / guild-application flow | 36: `jjm` (32 shapes), `jlh`, `jlk`, `jlu`, `knc` | `jlu` is sent by `GuildApplyUi.SendUpdateApplicationMessage` / Guild Directory; the other frames occur in the same client service area. | A complete create/edit/cancel application exchange, including the observed S2C result/error and the account/guild ownership rule. |
| Party finder | 6: `kxh`, `jmu`, `jpr`, `jop`, `kjx`, `kke` | The real request-id-bearing `kxh` precedes the party-finder sequence; the capture catalogue identifies response traffic (`jzw`, `jpx`). | A single ordered finder journey with decoded response payloads and request-id echoes, plus the listing persistence/visibility rules. |
| Professions | 3: `irl`, `kef` (2 shapes) | `irl` carries an `isu` job selection/configuration payload; the capture catalogue ties the flow to `isv`, `kdb`, `kcj`. | Job discovery/use/update round trip and the character/job database model.  A no-op would make the job UI lie about authoritative state. |
| Combat | 1: `lux` | The empty request carries a real request id; captures identify `ltd` as its response family. | A fight-state transition plus the exact `ltd` result payload and request-id echo.  Combat is not currently a supported vertical slice. |
| Chat preferences | 3: `mfe`, `mff`, `mfp` | Boolean, integer and string preference payload shapes are known and are confined to the chat-channel journey. | Whether each value is account- or character-scoped, validation/enums, and the S2C refresh/error behaviour. |
| Other UI/system clusters | 24: `hfr`, `hfv`, `hfz`, `hmo`, `hnn`, `hqq`, `hqx`, `hue`, `hvx`, `hzq`, `ibl`, `ifg`, `ipz`, `iqa`, `iql`, `iqq`, `irg`, `irj`, `iyc`, `jth`, `jwm`, `lrf`, `lrr`, `mdm` | Structural fields and obfuscated client service owners are known. | A targeted one-action capture with the adjacent S2C frames.  The current emulator trace cannot establish a reply from its own lack of implementation. |

The **15** former `NoReplyObserved` rows are now `Implemented`: they route through the explicit
`KnownNoReply` branch in `GameNodeProxy` and deliberately emit no S2C frame.  They are retained
as evidence so a future client version cannot silently change that contract.
