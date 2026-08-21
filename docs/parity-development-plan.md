# Dofus 3.6.10.10 client/server parity plan

## Purpose and boundary

The goal is behavioural parity with the pinned **Dofus 3.6.10.10** client: every supported
journey can begin in a fresh launcher, mutate authoritative state, survive reconnect and restart,
and end cleanly.  A message name existing in a `.proto` is not parity.  It must have a defined
direction, session state, validation, persistence rule, outgoing state transition and an
automated observation that the current client accepts it.

This is a version-pinned project.  Ankama rotates every three-letter message name between some
versions, so all evidence, generated files and recordings below carry the client version and the
hashes of `GameAssembly.dll` and `global-metadata.dat`.  Nothing discovered for 3.6.10.10 is
silently treated as valid for another client.

## What is already known

| Subject | Measured baseline | Source of truth |
|---|---:|---|
| Game protocol shapes | 2,169 messages, 6,186 fields, 550 enums | `datos/protocolo_3.6.10.10.proto` |
| Connection protocol shapes | 37 messages, 92 fields, 19 enums | `datos/protocolo_conexion_3.6.10.10.proto` |
| Existing real-session evidence | 242 captures, 103,808 reassembled frames | `docs/protocol.md` and captures |
| Server static opcode references | 877 inventory rows: 349 sends, 74 request reads, 10 replies, 326 discarded references | `datos/opcodes_emulador_3.6.10.10.tsv` |
| Client symbol/action index | 2,169 protocol entries, UI call-site references | `datos/indice_3.6.10.10.json` |
| Client game-data baseline | 21,748 items, 17,113 spells, 5,134 monsters, 15,360 maps | `docs/data-audit.md` |

The static-server row count is deliberately **not** an implementation count.  One opcode may be
sent from several locations and one row may be a logging table or dead code.  Phase 1 makes a
canonical one-row-per-opcode inventory before measuring coverage.

### Generated baseline — 21 August 2026

The first version-pinned ledger now lives in
[`datos/parity/3.6.10.10/`](../datos/parity/3.6.10.10/).  Generate it with
`py tools/generate_parity_ledger.py`; `--check` fails when the generated files no longer match the
schema, client action index or audited/current source inventory.  Its baseline is **2,206** client
schema messages, **998** audited/current server references and **2,255** joined rows.  Of those,
**144** have a reachable C2S mapping and **2,111** remain shape-known; only **49** retain a named
UI ownership hint.  The generator deliberately calls none of them client-verified.

The current scan supplements the audited constant inventory with literal type URLs and
`ReadPayload`/`Push`/`Answer` calls from the present Server project, so new handlers such as
`iul`/`iuv` immediately enter the matrix.  Compiler-aware `Op.*` alias resolution remains a
Phase 1 task; its absence is visible in `inventory_origin`, not hidden by a compatibility count.

### Confirmed fixes that define the standard

1. Launcher identity and per-launch game tickets are now separate values.  A game-ticket refresh
can no longer overwrite the launcher token saved in the account list.  Saved accounts created
before this migration need one final sign-in/re-add if their old token had already been overwritten.
2. `iuz` is `UIActionBar.ClearBarAction` for spell bar `1`.  The server now deletes the character's
slots, records an explicit empty-bar state in `CharacterSpellBarState`, and sends an empty `itg`
(`ShortcutBarContentMessage`) back.  Absence of rows no longer recreates defaults after reconnect.
3. `iul` is the context-menu removal request `{ f1: bar, f2: slot }`; the server persists a slot
removal and replaces the visible `itg` content.  It was previously swallowed by a generic
"ignored fight packet" path.
4. `kqq` is logout/back navigation.  Recorded traffic is `kqq` → `kqr`, followed 213 ms later by
the client closing the game socket and opening the connection-server handshake.  The observed
post-logout failure was token rejection, not missing `kqr`.  The launcher status poll is now
asynchronous, so server shutdown cannot block the WinForms message loop.

## Deliverables: the parity ledger

Create a generated, reviewable ledger under `datos/parity/3.6.10.10/`; it must be committed along
with the generator, rather than maintained as prose alone.

| File | One row represents | Minimum columns |
|---|---|---|
| `client_messages.tsv` | Every Game and Connection `.proto` message | opcode, schema fingerprint, fields, enum refs, client assembly, readable owner/action, likely direction, phase, confidence |
| `server_messages.tsv` | Every executable send/read/reply/registration | opcode, direction, source file:line, handler/builder, envelope root, request-id rule, validation, state mutation, database transaction, outgoing messages |
| `observations.tsv` | Every capture or local reproducible interaction | scenario, frame order, direction, opcode, decoded fields, payload hash, preceding/following messages, client version, evidence path |
| `parity_matrix.tsv` | One client message/opcode | client meaning, C2S/S2C/both, evidence grade, server status, owner, data dependency, test id, next action |
| `data_matrix.tsv` | A game-data domain or foreign key | client asset/bundle, imported table/file, records, missing refs, loader, version/hash, validation command |

`parity_matrix.tsv` statuses are: `unseen`, `shape-known`, `observed`, `mapped`, `implemented`,
`replayed`, `client-verified`, `not-needed`, `blocked`.  `not-needed` requires an explanation and
the code path that proves it; it must never mean "unhandled but ignored".  Each claim gets one
evidence grade: **A** captured and replayed, **B** client code/action index plus local exercise,
**C** structural schema inference, **D** hypothesis.  No D-grade message can be called compatible.

## Work streams and order

### 0. Freeze a reproducible reference

1. Write a `client-manifest.json` with Dofus version, executable paths, hashes, Cpp2IL version,
   locale and the emulator commit.  Store no accounts, tokens or player traffic in it.
2. Regenerate both `.proto` files with `Jondo.Unity.ProtocolBuilder` from the installed client's
   Cpp2IL output; fail CI if a regeneration differs from the checked-in schema without an explicit
   version bump.
3. Turn every current `world_etapa*.bin`, packet trace and manual scenario into a named fixture
   with scrubbed identifiers.  Preserve ordering and root envelope fields, not just inner payloads.
4. Run the existing database audit and record its output in `data_matrix.tsv`.  Fix or explicitly
   quarantine currently known data gaps: empty professions imports, 53 NPC placements, two dungeon
   room map references, 379 orphan spell-level references, map 0 and 11 fight-only maps.

**Exit:** a clean machine can build, initialize the database, start server/launcher, run the
protocol self-tests and reproduce each fixture without reaching Ankama.

### 1. Inventory and compare before adding features

1. Replace the current textual opcode scan with a deterministic source analyser.  It must find
   `Op.*`, literal type URLs, `Push`, `Answer`, frame writes, payload reads, dispatch branches and
   registry registrations; resolve aliases; merge duplicates; label dead/log-only references.
2. Generate `server_messages.tsv` from that analyser and compile its referenced paths.  A source
   edit cannot manually alter the ledger.
3. Join it with protocol shapes and the client action index.  Produce a report for: shape-known but
   unused, server-sent with unknown schema, C2S observed with no handler, S2C expected but unsent,
   wrong envelope root, request/reply ID omission, and messages currently discarded.
4. Make dispatch explicit.  Unknown C2S traffic must log opcode, decoded fields, session phase and
   a redacted payload hash, then count it.  Intentional no-ops are registered with a reason;
   broad `Contains`/ignore families cannot hide an opcode such as `iul` again.
5. Add a narrow typed decoder layer for shared protobuf primitives and keep byte builders covered
   by exact frame tests.  Generated schemas provide shape; they do not establish semantics.

**Exit:** every one of the 2,206 protocol messages is present exactly once in the parity matrix and
every executable server interaction is tied to a matrix row.

### 2. Recover client semantics efficiently

Do not attempt a blind "whole GameAssembly decompile" as the primary method.  The IL2CPP Cpp2IL
managed DLLs contain signatures but no usable method bodies; a 100+ MB native decompile would
create vast unauditable output while still not reveal runtime ordering.  Instead:

1. Use `indice_3.6.10.10.json`, readable Core/UI method names, state-machine/lambda names and
   the generated protocol type registry to attach each message to a UI feature or service.
2. For each feature cluster, instrument JondoFix at the UI action and TCP send/receive boundary.
   Log the action name, opcode, structured fields, envelope root, request ID and redacted session
   correlation ID.  Instrument only targeted clusters; do not retain tokens or chat content.
3. Record golden journeys against an authorised reference environment when available; otherwise
   exercise the local client and use client action/capture ordering as B-grade evidence.  Decode
   captures into `observations.tsv` and build replay tests from them.
4. Use native metadata/Ghidra only for ambiguous, high-value message clusters after the action
   index and traffic fail to disambiguate them.  Save function address, metadata token, hypothesis
   and proof in the ledger so the investigation is repeatable.
5. Resolve messages by dependency: a feature cannot be declared complete until every prerequisite
   state transition, data lookup, server push and client retry is accounted for.

**Exit:** all messages used by the next feature milestone have A/B evidence and a documented
state-machine sequence; C/D evidence remains visibly queued rather than guessed into production.

### 3. Implement in player-visible vertical slices

Implement one slice end-to-end: parse → validate → transaction/cache mutation → outgoing update →
reconnect recovery → replay test.  Prioritise in this order.

| Milestone | Includes | Acceptance scenario |
|---|---|---|
| A. Session and launcher | account persistence, scoped launcher/game tokens, HAAPI/Zaap, chat one-shot auth, logout/back/reconnect, server-down launcher behaviour | restart launcher without re-adding; close game and return/reconnect; stop server then close launcher normally |
| B. Character and world entry | creation/list/select, maps/actors, inventory/equipment, spell book, shortcut bars, cosmetics | enter world twice; changes to equipment and bar survive server restart; no default bar resurrection after clear |
| C. UI state and personal data | all shortcut types, sets, inventory actions, quests/achievements, settings/notebooks | add/move/remove/clear actions redraw immediately and remain correct after reconnect |
| D. Navigation and map content | map changes, movement, interactives, NPC placement/dialog/shop, zaaps, houses/haven bag | travel through a recorded route, use each interactive, recover safely on map change failure |
| E. Combat | start/join/ready/turn/move/cast/effects/summons/loot/leave/reconnect | deterministic script runs to victory and defeat with byte/order assertions for each phase |
| F. Multiplayer/social/economy | map visibility, chat, parties/guilds, exchange, market, trades, PvP | two independent clients see consistent authority; disconnect/reconnect leaves no ghost state |
| G. Long-tail systems | professions, mounts, cosmetics, dungeons, achievements, events/admin tools | each system has a persisted happy path and its invalid/rollback path |

Each milestone has a defined "unsupported" boundary.  Do not send fake success: send the
protocol's observed error/result if known; otherwise keep the action unavailable at the local UI
surface and log a ledger gap.

### 4. Make data parity deliberate

1. Treat client bundles as the version authority for templates, map geometry, spell levels,
   interactive elements and cosmetics.  Use compatible external Dofus 3 catalogues only as
   supplementary imports, with source/version/licence recorded.
2. Create importers that are idempotent and transactional.  A schema migration has a version,
   backup/rollback path and foreign-key validation; it does not silently invent default content.
3. Add database ownership rules: account-wide values, character values, map/world state and
   session-only caches must each have an explicit lifetime.  Test two characters and two accounts
   in one server process to detect leakage.
4. Run cross-reference checks in CI: all item/set/cosmetic/monster/map/spell/NPC/recipe links,
   map geometry availability, dungeon room maps and client/server template IDs.  Report unmatched
   IDs with enough context to repair the source importer.
5. Measure payload size after data imports.  World-entry/inventory frames must remain below the
   client 131,072-byte maximum, with a safety margin and a deterministic failure strategy.

**Exit:** every implemented feature declares the precise data set it consumes, all foreign keys
validate, and any missing game data becomes a visible compatibility gap rather than an empty UI.

### 5. Verification, release gates and ongoing updates

1. Unit-test primitive encoding, field signedness, oneofs, envelope root and request IDs.  Keep
   byte-for-byte golden tests for handshake, character entry, map transition, bar operations,
   combat transition and logout.
2. Add a headless replay harness that feeds C2S fixtures into an isolated session/database and
   compares emitted ordered S2C frames after normalising random GUIDs/timestamps.
3. Add integration smoke tests that start the real server process, launch two synthetic clients,
   kill one connection and assert cleanup in database and `SessionRegistry`.
4. Add manual release scripts for the actual Unity client: launcher restart, saved account launch,
   character select, world entry, clear/remove/add shortcut, game-menu close, server termination
   while launcher stays responsive.  Attach the trace automatically on failure.
5. Release only when every message exercised by release scenarios is `client-verified`, no
   unregistered message is discarded, and the data audit plus protocol self-tests pass.

Unsupported traffic is collected by the durable queue documented in
[`unknown-packet-queue.md`](unknown-packet-queue.md).  The automatic review loop consumes only
actionable `New` rows; it never converts a raw, unclassified frame directly into a fake reply.

For an upstream client update: freeze current evidence, regenerate schemas and action index, check
whether opcode names are preserved, produce the diff/mapping with ProtocolBuilder, mark all changed
matrix rows as unverified, then replay the release suite before shipping that new version.

## Immediate next backlog

1. Restart the newly published server, reconnect, and record the `iuz`, `iul` and `iuv` round
   trips in `observations.tsv`.  The expected S2C state replacement is `itg` with `f2=1`; after
   clear it has no entries.
2. Extend the generated source scan into a compiler-aware `Op.*` alias analyser, including frame
   builders, envelope roots and request-id rules; keep the current ledger as the deterministic
   baseline until then.
3. Add dedicated replay fixtures for account restart, `kqq` → `kqr` → connection handshake, and
   shortcut add/remove/clear/reconnect.
4. Complete the data-audit repairs in dependency order: professions import visibility, NPC map
   placements and invalid dungeon/map references before building their corresponding gameplay.
5. Work the world-entry matrix first; it is the shared dependency for inventory, shortcuts,
   movement, map content and every later gameplay feature.

## Measures of progress

Report progress by verified player journeys and evidence grade, not by raw opcode count.  A useful
weekly report includes the number of matrix rows by status, the number of A/B-confirmed rows in the
active milestone, replay pass rate, unknown C2S count per journey, database-reference errors,
largest emitted frame and the exact client hash.  That exposes real compatibility movement without
claiming that 2,169 schema shapes have 2,169 known meanings.
