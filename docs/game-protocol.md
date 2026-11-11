# Typed Game protocol and mapped live logging

Game uses protocol **3.6.10.10**. Clients **3.6.10.10 and 3.6.10.11 use the exact
same Game protocol**, so both use this single profile. `JondoVersion` is the Game
wire project's MSBuild selector, defaulting to `3.6.10.10`; unsupported profile
selections fail the build. `GameCodec.JondoVersion` exposes the same identity at
runtime. The profile name identifies the protocol, not the launcher version or
the precise client patch installed.

## Layers

| Project / file | Responsibility |
|---|---|
| `Jondo.Unity.Protocol.Game/Messages.cs` | Stable semantic records, enums, and explicit oneof case records |
| `Jondo.Unity.Protocol.Game/IGameCodec.cs` | Payload bases, `GameFrame`, and unframed codec contract |
| `Jondo.Unity.Protocol.Game.Wire/Schemas/game-3.6.10.10.proto` | Complete normalized wire schema |
| `Jondo.Unity.Protocol.Game.Wire/Mappings/game-3.6.10.10.json` | Semantic C# bindings and envelope roles |
| `Jondo.Unity.ProtocolGenerator` | Descriptor/Roslyn validation and direct conversion/dispatch generation |
| `Jondo.Unity.Server/Network/GameSession.cs` | Typed single-client sends through an injected Game codec |
| `Jondo.Unity.Server/Network/GameMapAudience.cs` | Explicit map-instance event audiences and shared encoded broadcast |
| `Jondo.Unity.Server/Network/WorldMovementDispatch.cs` | Narrow typed world-movement request dispatch |
| `Jondo.Unity.Server/Diagnostics/GameTrafficFormatter.cs` | Best-effort typed live-log presentation |

The imported schema contains 2,169 messages. The semantic mapping covers 533
payload/common message types, 65 enums and 1,374 payload/common fields, plus the
four Game envelope roles. JondoEmu's stable
C# API defines semantic names, required/nullable members and explicit oneof case
records. Nullable message members and explicit `None` cases preserve absence
where the semantic contract permits it. These models are maintained as source,
separately from wire generation.

`Grpc.Tools` generates complete protobuf classes and a descriptor set from the
schema alone. The shared generator then validates the stable reference assembly
and emits direct typed conversions, envelope adapters, and Any dispatch. All
generated C#/descriptors stay in MSBuild's intermediate directory. Runtime
conversion does not load mapping JSON or use reflection-based dispatch.

## Using the codec

```csharp
IGameCodec codec = new GameCodec();
byte[] envelope = codec.Encode(new GameFrame(
    new IdentificationRequest("example-ticket", "en"), Uid: -1));
GameFrame decoded = codec.Decode(envelope);
```

The codec consumes/returns protobuf envelope bytes **without the TCP length
prefix**. Requests and responses require a signed `int` UID, including zero;
events require no UID. Envelope roles, field tags and payload aliases come from
the pinned inputs. A payload in the wrong envelope kind fails conversion.

For inspection, `GameCodec.DecodeWire` returns the raw envelope,
`ReadEnvelope` returns kind/UID/Any metadata, and `DecodeRawPayload` decodes any
schema-defined payload, including messages without a semantic binding.

Wire parsing uses standard Google.Protobuf behavior, including unknown-field
retention. Unbound wire fields and whole oneofs remain raw-only without ignore
lists; encoding leaves them at protobuf defaults/unset cases unless explicitly
defaulted. Unmapped does not imply unused or decoy. Semantic conversion rejects
missing required values and unmapped values/branches within mapped enums/oneofs.
Explicit `None` preserves oneof absence; it is distinct from
a selected zero-valued branch. `defaultOnAbsence`, `ignoredStableMembers`, and
`defaultOneofs` are deliberate construction policies, validated at generation.
All stable members and cases still require bindings or explicit construction
policies; relaxing wire coverage does not relax stable coverage.
Stable enum ordinals are independent of wire numbers. Stable projection omits
unmapped fields and is intentionally not a lossless wire round trip.

## Live logging

`NetworkMessage.LogTrafficEnriched` formats a scrubbed diagnostic copy through
the new codec. A successful line includes the reviewed message name, semantic
fields, enum names, oneof case discriminators, and UID when present. Mapped fields
are limited to 2,048 characters plus an ellipsis for the live view. Newlines in
strings are JSON-escaped.

Unknown payloads and failed conversions fall back to the existing compact
raw-field representation. A reviewed name is retained when available even if
the body cannot be converted. The existing raw traffic file and its redaction/
enablement behavior remain in place; no additional disk log is introduced.
Mapped-log failure is isolated from outbound raw capture.

The logger and world-movement handlers use the typed API. Other gameplay and
combat handlers continue through their existing packet paths during migration.

## Session and map messaging

Handlers send stable objects; codec selection, envelope encoding and transport
framing belong behind the session API:

```csharp
await session.SendAsync(new PongEvent(Quiet: true));
await session.SendAsync(new MapMovementConfirmedResponse(), uid);

var map = session.Map;
await map.SendAsync(new GameMapMovementEvent(cells, session.CharacterId, cautious));
await map.SendAsync(someEvent, exceptSessionId: session.Id);
```

`SendAsync(GameEvent)` constructs an event envelope with no UID.
`SendAsync(GameResponse, int uid)` requires the caller's exact signed correlation
ID. A map audience accepts only events. Existing `SendAsync(byte[])` remains the
unframed-packet transport boundary for legacy handlers, and typed sends reuse it
for logging, length framing, socket serialization and write timeouts.

`GameSession` accepts an `IGameCodec`; ordinary production sessions share the
pinned default codec. No handler needs to choose an obfuscated payload alias.
With the current single Game profile, a map broadcast encodes once and shares
the resulting bytes across a snapshot of recipients.

`session.Map` captures the map ID plus haven-bag ownership/house identity at the
time the property is read. It remains that audience after the source session
moves or the ambient `SessionContext` changes. Recipients are selected at send
time from sessions currently in the world in that map instance. The sender is
included unless excluded explicitly. There is no distance-based visibility
filter. An optional `fightId` filter is explicit; omission retains the existing
map-wide semantics. This audience never relies on the caller's AsyncLocal state.

Broadcast sends run concurrently through each target's transport, without taking
their gameplay locks. A thrown send failure is isolated to that recipient and
does not unregister it. The returned count is the number of nonthrowing send
calls, not client acknowledgements. Awaiting the broadcast waits for all target
sends, including any write timeout.

## First handler migration: world movement

`WorldMovementDispatch` recognizes only `MapMovementRequest`,
`MapMovementCancelRequest`, and `MapMovementConfirmRequest` using generated alias
metadata. Under the existing packet/session turn, it verifies world entry and
decodes through the owning session's codec. Recognized malformed/wrong-envelope
requests are consumed without falling back to the legacy parser. Backend or
handler failures are not reclassified as decode failures.

World handlers accept the session and stable values. They retain map-ID checking,
key-cell decoding, orientation/pending-arrival state, cancellation walkability
checks, and database saves. The map receives a `GameMapMovementEvent` including
the mover; only after that broadcast completes do party followers receive their
separate position update (even on another map), followed by conquest checks.
`Cautious` is propagated from the request. Protobuf parsing handles both packed
and unpacked key cells.

Arrival is acknowledged with `MapMovementConfirmedResponse` on the response
envelope using the original UID, before a pending movement is consumed and any
floor passage runs. Deferred party/fight joining remains after arrival handling.
Combat bypasses this adapter and retains its lock-owning dispatcher, raw movement
builder and legacy confirmation entry point; map-change packets are also still
handled by their existing code.

The mapped movement event intentionally omits the old builder's wire-only field
2, formerly described as facing. The reviewed `3.6.10.10` mapping binds cells,
character ID and cautious movement; it does not bind this field. Native inspection
found path-derived directions (`eoo.bakd`, RVA `0x4ea76f0`), but field 2's usage
classification remains indeterminate. Server-side orientation is still saved.
Do not add an invented semantic facing binding to reproduce the legacy bytes.
Client animation/final-facing smoke testing remains useful alongside automated
wire/state checks.

## Importing source inputs

Normal builds need only .NET 10 and the checked-in inputs. For an explicit import,
use Python 3.10+ with a schema file and a mapping in JondoEmu's C# binding format:

```sh
python3 Jondo.Unity.Protocol.Game.Wire/Import/import-game.py \
  --schema /path/to/game.proto \
  --mapping /path/to/game.mapping.json \
  --version 3.6.10.10
```

`--destination` optionally selects an output Game wire project directory; the
default is this checkout's `Jondo.Unity.Protocol.Game.Wire`. Inputs are explicit
files with no required directory layout. The mapping format is the one in
`Mappings/game-3.6.10.10.json`: stable/generated namespaces, conversion class and
exception, `messages`, `enums`, the four `gameEnvelope` roles, and optional
`wireAliases`. Fields/cases name existing C# constructor members and record types.
See the [shared mapping contract](connection-protocol.md#mapping-contract) for
field, case and enum bindings. Stable-model edits are a separate source change.

The importer accepts Game's proto3 schema subset (messages, enums, maps, oneofs,
optional/repeated fields and field attributes). It checks wire references and
policies before writing, flattens dotted declarations with collision detection,
normalizes the Any type spelling, and escapes reserved C# type identities.
It also accepts the already normalized checked-in schema. `wireAliases` retains
original Any/dispatch identities, including dotted names. Tags, wire types,
cardinality, presence, enum numbers and oneofs are preserved; unsupported schema
syntax is rejected rather than silently removed.

`Import/game-3.6.10.10.provenance.json` records the protocol version, project-relative
schema/mapping paths, and SHA-256 hashes of the input and installed artifacts.
Input validation finishes before writes; publication of the several files is
sequential. Build the wire project afterwards for authoritative descriptor and
stable C# type validation. Importing a new version does not register it as a
supported runtime profile; that requires an explicit profile/build change.

## Build and verification

```sh
dotnet build Jondo.Unity.Protocol.Game.Wire/Jondo.Unity.Protocol.Game.Wire.csproj
dotnet test Jondo.Unity.Protocol.Game.Tests/Jondo.Unity.Protocol.Game.Tests.csproj
dotnet test Jondo.Unity.Protocol.Connection.Tests/Jondo.Unity.Protocol.Connection.Tests.csproj
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
python3 Jondo.Unity.Protocol.Game.Wire/Import/test_import.py
```

`--configuration Release --artifacts-path host-artifacts` is supported. Game
tests run in Windows/Linux CI and the server's publish test gate. Tests include
independent 3.6.10.10 envelope/payload fixtures, rotated synthetic envelope tags
and aliases, signed UIDs, raw unknown-field retention, maps/bytes/presence,
explicit defaults, nested-case formatting, redaction, and raw fallback without
changing bytes delivered to legacy handlers. Session/movement tests cover signed
response IDs, recipient inclusion/exclusion, nonlocal delivery and ordering,
explicit house/haven-bag instance isolation, malformed request rejection,
world/combat routing separation, pending-arrival consumption and cancellation.
