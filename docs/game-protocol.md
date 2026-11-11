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

For concrete cases with non-default population evidence, see
[`populated-unmapped-fields.md`](populated-unmapped-fields.md). It records captured
values and JondoEmu construction paths alongside the client-use findings and
remaining semantic-mapping gaps.

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

The logger, world-movement handlers and mapped entry boundary use the typed API.
Other gameplay and combat handlers continue through their existing packet paths
during migration.

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

## Game identification, selection and entry migration

`GameEntryDispatch` decodes identification, character-list/selection (including
first selection), sequence-number, context creation/readiness, map-information,
fight-map, force-selection continuation, weekly-reset and ping requests through
the owning session's codec. It retains the decoded frame and signed request UID.
Recognized invalid envelopes
close the connection rather than falling through to legacy parsers. Entry guards
require identification before selection and a selected, in-world character before
map/context initialization. Selection also checks the ticket's server. Tutorial
preference is decoded; the existing saved-map spawn policy is retained.

The force-selection event is constructed directly as `CharacterForceSelectionEvent`.
The client automatically answers with `CharacterForceSelectionReadyRequest`; the
server's existing policy selects the account's character whose fight can be rejoined.
This continuation is permitted after identification and before world entry.
`FightMapRequest` follows the same selected-character/in-world guard as other map
readiness requests, retaining the existing spectator/resume ordering and fight locks.

`GuildWeeklyResetDateRequest` replaces the provisional guild-mission request name.
Its handler sends `GuildWeeklyResetDateResponse` directly through the owning session
codec with the decoded request's exact signed UID. Reset dates remain the existing
Tuesday-05:00-UTC policy and ISO string format. The historical captured response is
filtered from map-entry replay, so it cannot supply an unsolicited UID -1 response.

`GameEntryService` is instantiated once per owning `GameSession`. It handles
decoded identification, character-list/selection, force-selection continuation,
sequence confirmation, context/map readiness and ping requests. Its result tells
the packet loop whether the request was handled, belongs to another handler, or
requires closing the connection. The service owns phase checks, old-session
replacement before character lookup, the welcome/list continuation decision,
ordinary-selection surrender versus fight restoration, and the one-time map
initialization flag. The legacy return-to-selection route delegates token sending,
world departure and lifecycle reset to the same service. Ordinary map/zaap/haven-bag
travel notifies the service to suppress entry replay.

The caller retains the existing session turn; the service does not acquire it
again. Its injected `IGameEntryBackend` adapts storage and existing feature handlers;
legacy calls run under a scoped session context. Initial state and its HAAPI/gift
continuation are constructed directly, retaining the live feature hooks between
them. No client confirmation is awaited between those stages. The
configured welcome notice is constructed directly as a `TextInformationEvent`.
Map initialization is separately triggered by sequence confirmation, with a
first-ping fallback. Subsequent pings only receive their pong and alteration tick.
Fight-map handling retains spectator, resume and preparation precedence and fight
locks; roleplay map actors are never sent into an active fight. Map-information
updates remain repeatable on travel. Initial and map sequences now construct their
messages directly; remaining feature hooks and welcome/character packet builders
still use compatibility paths.

### HAAPI identity and gift continuation

`GameEntryContinuationMessages` sends `HaapiSessionEvent`, then
`GameActionItemListEvent`, after the initial state, existing live hooks and
characteristics. Both use the owning session's codec and reuse the caller's turn.
Source, codec and transport failures propagate without retrying the continuation.
Sequence confirmation still triggers map initialization later.

Each `GameSession` allocates one positive process-local numeric HAAPI identity,
independent of its registry GUID. The invariant decimal string survives character
changes on that connection. August client `ebh.uzu` (RVA `0x4b7b010`), reached from
the `jby` receiver, calls `Int64.Parse`, stores that value and publishes a notification;
a GUID or empty placeholder would not satisfy that contract. This is local-server
identity policy rather than an official HAAPI-issued identifier.

`GiftService` grants due veteran rewards before reading the owning account's
`Gifts.Of` snapshot, in its oldest-first order. `GiftMessages` constructs titles,
item quantities/template IDs, empty preview effects and the existing gift redemption
IDs in `ItemUids`; it allocates no inventory UIDs. No gifts sends an empty replacement.
Stored descriptions remain in the database but unmapped content tag 6 is deliberately
omitted: the inspected client gift model consumes titles, previews and redemption IDs,
not that text. See [the gift-field finding](populated-unmapped-fields.md#gift-description-jtgjtfjte-tag-6).

The character-list send and live gift refresh share this factory/source policy.
Live refresh captures one account snapshot, encodes through each matching online
recipient's codec, and preserves the existing log-and-continue behavior after a
recipient delivery failure. Older synchronous welcome/character-list helpers encode
the same semantic snapshot into their compatibility frame lists. `GiftHandler.BuildList`
and its raw protobuf construction are removed.

The historical entry JSON reconstruction, all three reference blocks and the
`WorldEntryContent` loader have been deleted. Entry no longer loads a recording,
filters reference rows or learns metadata from captured entry packets.
Focused semantic, independent protocol and framed storage tests cover identity
lifetime/format, gift IDs/order/account isolation, empty refreshes, source and delivery
failures, grant/claim behavior and the continuation's identity-before-gifts ordering.

### Initial character state

`GameEntryInitialMessages` now owns thirty-two unconditional initial sends, a variable
equipped-set snapshot sequence and two optional sends: queue completion,
two haven-bag room dispatches, profession descriptions and experience, artisan
directory settings, arena player information, guild ranks, alignment rank, alterations,
server experience modifiers, haven-bag packs, entry experience gain, notifications,
character selection, inventory content, chat channel state, known zaaps and the current
calendar entry, initial warning and previous-connection notice, tag-storage refresh,
equipped sets, inventory weight, spouse status, storage kamas, advertised bonuses and
loading-complete, sequence number, capabilities, configuration, colour collection,
aura/ornament notifications and outfit look. It constructs semantic records directly
from server policy and character state and sends them through the owning session.
Rooms retain theme/room identity and dispatch action; artisan defaults retain the
base job's zero minimum; experience settings retain both 105% policy values and
the character's persisted limit, including zero.

The opening sequence is queue completion, rooms, zero experience gain, notifications,
selection success and arena information, followed directly by the inventory snapshot.
Direct tag-storage refresh and all current equipped-set updates follow it, before professions. Weight follows alignment
rank, then direct sequence-number notification. The initial
warning and optional previous-connection notice follow sequence number, then direct spouse
status and alterations; the calendar event is constructed after capabilities. After the second
room dispatch, storage kamas precedes direct configuration, then bonuses
and loading-complete precede the direct appearance tail. **The entire after-selection
sequence is constructed directly.** `SendAsync(GameSession)` takes neither
rows nor a legacy-send callback. There is no manifest loader. Production sends thirty-two unconditional direct initial events,
one `SetUpdateEvent` per represented equipped set, and two optional sends: calendar and
previous connection. With the calendar available, first visits send **33 + S** direct events
and returning visits **34 + S**, where **S** is the number of represented equipped sets.
The former `Rebuilt` branches are removed; legacy settings/room builders remain for unrelated gameplay handlers.

Captured `QuestObjectiveUpdatedEvent` (`ieg`) and `TitleSelectionErrorEvent` (`hii`)
rows supplied no shared entry initialization and have been deleted with the recording.
The obsolete runtime skip list is removed too. The selected character's journal
comes from `Quests.SendJournalAsync`.

`InitialPresentationMessages` completes the seven remaining message identities:

- `SequenceNumberEvent` is an explicit empty event immediately after weight.
- `CharacterCapabilitiesEvent` advertises all emblem-category bits (`-1`): guild
  creation/modification has no category-ownership restriction in Jondo. It does not
  reuse the recorded account's `12287` mask.
- `ConfigurationEvent` uses the live engines' token IDs, twelve-mission capacity,
  acknowledgment cap and contribution cost/rewards. Exclusion limits (three ranks,
  two categories) and the 86,400,000 ms activity delay are explicit client-facing
  server policy. The unused outer block and activity tag 7 are omitted.
- `CharacterAppearanceCollectionEvent` supplies the selected character's persisted
  palette. Indexed entity-look colours become RGB slots; missing slots retain `-1`,
  and repeated indices use the last assignment. There is one unlocked/total palette
  slot because Jondo stores the current palette, without a saved-palette shop/store.
  Palette tag 1 and character-colours tag 2 are unused and omitted.
- `OutfitAuraChangedEvent` and `OrnamentSelectedEvent` are empty typed notifications.
  Their former 206/135 values are unused and omitted; the shared legacy writers also
  stop emitting those scalars.
- `OutfitLookUpdatedEvent` supplies the character-derived `LookIdOf` identifier and
  a detached recursive `BreedLookTable.SemanticLook(..., paraLaVentana: true)` snapshot.
  It retains the existing preview policy, including mounted pet appearance, rather
  than confusing this notification with map actor updates. Its unused tag 3 is omitted.

The explicit project rule is **indeterminate = unused for outbound construction**;
see [the unused-field policy](populated-unmapped-fields.md#after-selection-omission-policy).
Send sites link there instead of adding compatibility payloads. Native audit outcomes
are retained as evidence provenance; no speculative field bindings were introduced.
The owner is scoped for all initial sources, each event uses its codec, and the caller's
turn is reused. Tests cover distinct settings/slot counts, recursive detachment, saved
palette refresh, signed masks, absence/empty presence, unknown-field omission, every
source/codec/transport failure boundary and real framed account isolation.

Entry experience gain is explicitly zero, not the character's accumulated experience.
Notifications carry an empty flag collection as Jondo's initial policy; no recorded
acknowledgement bitsets are reused. `CharacterSelectionMessages` builds the success
case from the selected character's identity, gender, current connection date and
resolved `EntityLook`. The unrecorded disconnection date stays empty and a zero level
retains its optional absence. `BreedLookTable` composes that typed appearance using
the same equipment, cosmetics, mount, pet, aura and mutation rules as legacy gameplay
consumers, which serialize the resolved model through a compatibility writer.
Selection never builds or decodes an intermediate legacy packet. Its `Rebuilt`
branch and `ConnectionProtocol.BuildCharacterSelectedSuccess` are removed.

`InventoryMessages` captures kamas and `session.State.EquipmentItems` directly into a
detached `InventoryContentEvent`, including an empty inventory. Item/effect order,
amulet position zero and the quantity minimum of one are preserved. Kamas and dice
numbers retain int64 width; item UIDs and narrower effect values use checked conversion.
Favorite remains false and storage quantities remain empty, matching the prior builder.
`ItemEffectMessages` shares the existing `EffectFields.Shape` policy with the remaining
legacy item serializers: internal/skipped effects are omitted, nonempty text wins,
action-only values remain unset, and selected integer zero or empty range/dice values
keep their cases. Item dates use Unix seconds, Paris time and the year offset of 1370.

Workshop open/close/refresh, commission close and marketplace refresh use the same
snapshot factory and owning session codec, retaining the surrounding message order.
Workshop boundaries accept an explicit session and scope legacy state access to it.
`ConnectionProtocol.BuildInventory` and the inventory `Rebuilt` branch are removed.
Map spells and shortcut bars are now constructed directly too; the entry rebuild helper is removed.
Semantic tests cover snapshot detachment, effect cases,
numeric widths, recipient isolation and source/codec/transport
failure propagation without replay or retry.

`AfterSelectionMessages` constructs seven outgoing event types at this stage. Its live
sources are the selected session's inventory, the `ItemSets` catalogue, template
`realWeight` values and `Bank.KamasOf(session.AccountId)`. All ten replaced reference
frames passed the lossless projection audit. No new protocol bindings were needed.

- Tag-storage refresh is explicitly empty; Jondo has no persisted tag model.
- Spouse status is explicitly false; Jondo has no marriage model. The recording's
  true flag is not a default for other characters.
- General advertised bonuses are explicitly empty until there is a supported feed.
  The reviewed `BonusType.Unknown0`–`Unknown36` values are opaque identities, not
  `Almanax.BonusType` IDs; no numeric translation is inferred.
- Sets use worn slots 0–15, ordered by slot then instance UID, and are emitted in
  set-ID order. Quantity is not a piece multiplier. Single-piece sets still appear
  with empty effects; multiple pieces select the exact catalogue tier, never a sum
  of lower tiers. This matches the existing sheet bonus policy. All set events are
  detached and constructed before the first set send. No equipment means no set
  events; there is no fabricated set-zero clearing event. The reviewed member is
  named `ObjectsUid`, but this flow carries equipped **template IDs**, as seen in
  the reference frames and the native player-set model. For the August build,
  `0x4c2f4e0`/`0x4c30740` retain tag 3 in `mh.dcwe`, and `0x638a560` keeps it alongside
  datacenter item-set templates; `0x4c3b880` searches the retained identifiers.
- Weight sums each owned item's template weight times its quantity, with the same
  minimum quantity of one as the inventory snapshot. Empty inventory means zero,
  with no stale login-inventory fallback. Capacity retains the existing policy:
  **1000 + 5 × (allocated strength + scrolled strength)**. Arithmetic is widened
  before multiplication, and both values are checked at the int32 boundary.
- Storage kamas is the owning account's bank balance, retaining int64 width. It is
  separate from inventory currency and the client's combined currency calculation
  (native storage receiver `0x4cb57e0`). The bank source retains its existing
  logged-zero database-read fallback; injected source errors propagate.
- Loading-complete remains at the original boundary before the appearance tail.
  It does not move to map initialization or after the live feature hooks.

Semantic tests cover source/codec/transport failures at every new delivery boundary,
detached multi-set snapshots, empty/populated states, arithmetic bounds, account
isolation, caller-owned turns and variable set multiplicity. Independent
protocol fixtures cover all seven events, nested presence, packed/unpacked IDs,
effect absence versus integer zero, every opaque bonus kind and raw unmapped-field
retention. Catalogue and framed database-backed regressions verify exact piece tiers,
current inventory weights, refreshed bank balances and per-session codecs.

`EntryInformationMessages` constructs the initial parameterless warning (error type,
ID 89) and optional previous-connection information directly. A missing previous visit
omits that event. A blank IP selects ID 193 with five parameters; a present IP selects
ID 152 and supplies the exact address as parameter six. Parameters remain year, month,
day, hour and minute with invariant zero-padding and the stored timestamp's offset.
Selection captures `session.State.PreviousVisit` before updating the stored connection,
so the notice describes the prior visit rather than the current connection.

The map's separate ID 197 notice is constructed by `MapEntryOpeningMessages` from live state. The
`WorldEntry.Rebuilt` information branch, its message-ID reader and
`ConnectionProtocol.BuildLastConnection` are removed. Semantic tests cover both visits,
IP forms, offset/culture handling, owned delivery, ordering
and source/codec/transport failures. Exact aggregate counts belong to entry-sequence
tests; profession, alteration and guild tests assert their own messages and relative
ordering rather than unrelated initial-message totals or absolute packet positions.

The map opening directly sends `PongEvent`, `GuildContributionLeftEvent`,
`TextInformationEvent`, `ContextDestroyEvent` and `ContextCreationEvent`, in that
order. `MapEntryOpeningMessages` snapshots the explicit session's character and
account state before sending: guildless characters have zero contributions;
guild members use their remaining weekly allowance. Information message 197
reports distinct online mutual-friend accounts, including zero, using the same
in-world presence policy as the friends list. The entry pong is quiet; context
creation explicitly selects roleplay or fight reconnection. The five captured
rows were retired with the reference reconstruction. Semantic
tests record event objects through a stub codec, without protobuf serialization.

`GameEntryMapMessages` then constructs the environment directly: roleplay sends
`LifePointsRegenBeginEvent` with the current Almanax-aware rate and an empty
`NpcsMapQuestStatusUpdateEvent` reset; both are omitted on fight reconnection.
The existing quest hooks still send live marks later. `MapCurrentEvent` uses the
resolved positive int64 entry map, including the arena on reconnection, with no
captured-map fallback. `ServerTimeMessages` shares the welcome's 120-minute offset
and current Unix-millisecond policy, with an injected map-entry clock. Reconnection
notices run explicitly immediately after time, before the semantic spell book and
both semantic shortcut bars. Those are followed by parameterless `CommunityChannelEvent`, twenty
session channel updates and `BakApiKeyEvent("")`: Jondo has no local API-key issuer.
The community scalar remains unmapped and is deliberately omitted; this is not a
lossless conversion of its captured payload or a global unused-field conclusion.
All six reference rows have been retired. Regeneration bookkeeping
updates only the explicit roleplay owner after the sequence completes. Semantic
tests cover values, ordering, numeric widths, clock refresh, reconnection, ownership
and source/codec/transport failures using a recording codec with opaque output and
no protocol decoding.

`SpellMessages` builds a detached `SpellsEvent` from the explicit session for map
entry and live level-up, command, scroll and mutation refreshes. Human spells retain
melee ID zero at grade one, then the existing `SpellTable.KnownFor` class/learned
selection and authorized administrator grants. Its `SpellLevel` values are grades,
not character levels or catalogue row IDs. Selected variants, locked-variant fallback
and grade unlocks continue to use the shared catalogue rules. One fresh account-role
check selects either saved learned spells or all administrator specials and grants.
Mutation equipment comes from the explicit owner's inventory and supplies a separate
mutant list while retaining the human book. Types remain the reviewed opaque
`Unknown1` (class/admin), `Unknown2` (melee/special), and `Unknown6` (mutation).
The user chose to omit the former writer's unmapped root boolean, previously true;
no damage-preview interpretation is established. The unmapped nested `SpellItem`
boolean is client-used but remains false, as in the former writer. See the
[field inventory](populated-unmapped-fields.md#spell-book-construction-policy).
Map entry sends the book after time/reconnection notices and before both shortcut
bars. The captured `hms` row is excluded before construction, its `WorldEntry.Rebuilt`
branch and `ConnectionProtocol.BuildSpellList` are removed. Live refreshes retain the missing-
catalogue guard, send through the owning codec and scope the semantic bar continuation
to that owner without taking another turn. Semantic tests use injected sources and
opaque recording codecs; spell-scroll and mutation regressions inspect semantic
snapshots. No reference-independence tests are added.

`ShortcutBarMessages` constructs both `ShortcutBarContentEvent` snapshots directly,
spell first and general second. The spell source retains `FightSpellLayout`'s saved
slots, variant/level filtering, newly unlocked spells, account-authorized learned/admin
spells and never-written versus deliberately cleared policy. First layouts are remembered
without close combat; melee remains a selected `ShortcutSpell(0)`. Active mutations
replace the normal bar with their ordered spells from slot zero, even when that list
is empty. Existing ambient gameplay helpers run under the explicit owner's context.
Level-up refresh and preset application share the same semantic spell-bar send.

The refreshed August import adds `ShortcutEmote.EmoteId`, `ShortcutSmiley.SmileyId`,
`ShortcutOutfit.OutfitUuid` and `ShortcutCosmeticObject.Enable`/`ObjectGid`. Cosmetic
enablement is independent of its template identifier. Existing item GID/UID, preset
UUID and spell bindings are retained; other unmapped extras follow the explicit
outbound omission policy. `ShortcutStorageCodec` parses each historical stored blob
and calls the generated common-message conversion directly, without constructing an
envelope or intermediate full-bar packet. The strict `ItemShortcuts.ReadForSnapshot`
query preserves slot order and propagates failures rather than publishing an empty
or partial bar; stored rows remain unchanged. Both bar snapshots are captured before
either is sent, including the empty general-bar replacement.

`GameEntryMapMessages.SendAsync` now accepts neither manifest rows nor a legacy
callback. Both reference blocks and their loader have been deleted;
`WorldEntry.Rebuilt`, `SpellBarLabel`, `ConnectionProtocol.BuildSpellBar` and
`ItemShortcuts.BuildBar` are removed.
Semantic tests use opaque recording codecs for all shortcut cases, independent
cosmetic values, saved/cleared/mutation layouts, owner isolation, snapshot consistency,
ordering and source/delivery failures.

`ProfessionMessages` derives a shared, sorted, distinct directory catalogue from
`JobManager`, `SkillManager` and recipe availability using the existing artisan
eligibility rule: base job, gathering, crafting or smithmagic. The base job stays
in directory settings with minimum zero and is excluded from profession descriptions
and experience. No fixed count or profession-ID list is read from the recording.
Untouched professions start at level 1; saved XP stays `long`; current/next floors
use `JobExperience`, with next floor zero at the level cap.

Description construction is shared with `WorkshopHandler.SendLevelUpAsync` and
uses the existing level-up policy for every skill: gathering minimum 1, maximum 1
while locked and `GatheringHandler.Ceiling` once unlocked, duration
`Resources.GatherTenths`, and craft probability 100. This deliberately replaces
the recorded character's skill quantities with current character/server values.
The level-up path retains its existing legacy envelope builder while consuming
the shared semantic description. Entry sends description, experience and directory
settings in that order. `WorldEntry.SendJobs()` and its replacement branch are removed.

At the existing live-hook position after the initial sequence,
`JobBookSubscriptionEvent` is sent directly from the owning session's listed
artisan jobs. False/unlisted entries are omitted; no event is sent if none are
listed. This preserves the existing hook's conditional behavior.

The August mapping audit and lossless reference check found no populated data
outside the semantic contract in the current profession description/experience
rows. Additional unmapped description/skill/subscription fields remain unmapped;
this is not a global unused-field classification.

`AlterationMessages` constructs `AlterationsEvent`, `Alteration` and `ObjectEffect`
directly from active state, catalogue definitions and one explicit UTC instant.
The initial snapshot is sent immediately before experience modifiers; its reference
row is excluded before payload construction and its `Rebuilt` branch is removed.
`AlterationHandler.ListAsync` and `AfterFightAsync` now accept the owning session.
An injected `AlterationSnapshotService` keeps the requested snapshot and post-fight
flow testable without world storage: expire timed entries, count the fight, send the
snapshot (including newly exhausted zero-count entries), refresh bonus slots, then
send expired/spent losses. The caller's characteristics refresh follows completion.
The service uses the existing session turn and scopes legacy feature hooks to the
owning session; it does not acquire a second turn.

Alteration construction preserves millisecond timestamps, explicit expiration-kind
mapping, reverse catalogue effect order, remaining/total counters, scalar fallback
order, truncated/clamped minutes, Paris calendar/DST handling and the Dofus year
offset. Missing definitions retain the alteration with no effects; empty snapshots
still travel. Construction does not expire entries or mutate source state.
Int32 effect values and dice components use checked conversions; dice minute totals
remain int64. Unrepresentable values fail construction rather than silently narrow.
Legacy gain/loss serializers consume the same semantic entry/effect builders.

`ObjectEffectCaseNone` explicitly represents an unset effect-value oneof. It differs
from `ObjectEffectCaseValueInt(0)` and a present-empty dice value. The binding uses
`noneType`, with no wire-schema or field-number change. The installed mapping hash
in provenance is updated; the original imported-source hash is retained.
The alteration reference row passed the lossless semantic-scope audit. Action-only
effects have dedicated semantic and protocol-presence regressions. This does not
classify unrelated unmapped data as unused.

`ArenaPlayerInformationMessages` directly constructs `ArenaUpdatePlayerInformationEvent`
and its rank/ranking records. `ArenaPlayerInformationService` supplies the selected
character's standings, level, penalty and an explicit UTC instant through an injected
backend. Initial entry sends it immediately after selection; captured emote additions
are excluded.
Its reference row is excluded before payload construction; the arena `Rebuilt` branch
and `KoliseoHandler.BuildRanks` are removed. The reference row passed the lossless
semantic-scope audit; the former builder also populated only mapped fields. Additional
unmapped arena fields remain outside this construction scope.

The conversion corrects three source-policy errors independently of mapping names:
`BanEndDate` uses the active `KoliseoOffers` penalty expiry rather than season start;
`BestLeagueId` uses `Standing.BestLeague`; nested `LeagueRanking.LeagueId` uses
`Standing.League`. Nested `Rating` now uses the actual current rating, including
placement ratings. All four arena modes are mapped explicitly. Placement counts,
daily/season fights and victories, signed `-1` leagues and present rankings are retained.
Historical best rating is not stored, and ladder position is not supplied by this
snapshot; both retain the former explicit zero/default policy.

The August native date helper `gji.bkla` (RVA `0x5660570`), called by the arena
information consumer, parses dates with invariant culture and round-trip styles,
throwing on invalid text. The server therefore uses `1970-01-01T00:00:00.000Z` as
its documented no-active-ban sentinel, not an empty string. An expiry equal to the
snapshot instant is expired. Active expiry strings retain UTC milliseconds and
culture-independent ISO formatting. This is a server construction policy, not a
recovered official default date.

`KoliseoHandler.ReturnAsync` now accepts the owning session and uses the same service.
It leaves that character's queue, sends typed player information, then the existing
correlated return response and queue-left notification in their measured order
(three frames within 80 ms in the historical capture). The exact signed request UID,
including zero, is retained; a call without a request still omits only the response.
The caller's existing session turn is reused. Backend, codec and transport failures
propagate without replaying or retrying the remaining sends. Semantic server tests
cover policy, session isolation, ordering and failures; independent protocol fixtures
cover all four modes, populated fields and absent/present-empty ranking messages.

`GuildRankMessages` constructs whole `GuildRanksEvent` snapshots from `GuildStore.Rank`
records: stored name/ID, `Flag` as `Modifiable`, integer rights, icon and display order.
Lists and rights are detached from mutable storage objects. Stored display order,
custom/localized names, duplicate rights and present-empty private/graphic blocks
are preserved. `GuildRankService` shares source and construction policy across
initial entry, founding, joining and management refreshes through an injected backend.

Initial entry sends actual guild ranks immediately after artisan directory settings;
unguilded characters receive the server's four-rank template. The conditional guild
entry hook repeats actual ranks before silent membership, header and members. This
retains both existing rank-send positions while correcting the former default-rank
send for guilded characters. A custom member rank ID is available before membership
processing; entry still avoids the joining popup. The captured rank row is excluded
before payload construction, including its recorder-specific leader name «rey».
The row passed the lossless scope audit, and the former production builder populated
only mapped fields, as checked against the August bootstrap mappings. This is a
construction-scope finding, not a classification of the other declared rank fields.

Founding reads the newly persisted guild ranks. Both joining paths send actual ranks
before joined/member state; direct joining accepts the owning session explicitly,
and asynchronous acceptance uses the recipient's codec even under the leader's context.
Rank-management opening and edit/reorder/rights/create/delete refreshes use the same
typed send after storage succeeds. Source, conversion, codec and transport failures
propagate without legacy retries. The existing session turn is reused.

`GuildRankRights` reads the historical packed-int32 `GuildRanks.Rights` blob in full
before returning values. Snapshots, `GuildStore.HasRight` and guild-chest checks share
this decoder: integer 128 is not permission 1 even though its varint contains byte 1.
Order, duplicates and full int32 values survive delivery. Truncated/malformed blobs
throw rather than yield partial rights; writes validate before persistence. Rank 1's
all-rights policy and the older-guild persisted-default fallback are retained.
The legacy `GuildProtocol.BuildRanks`/`BuildDefaultRanks` methods are removed.
Semantic regressions cover actual/default sources, both entry positions, joining,
mutations, permissions and failures; independent protocol fixtures pin historical
default/edited payloads and nested presence/packed-integer compatibility.

`GuildStateMessages` directly constructs guild identity/emblem, `GuildMembershipEvent`
and `GuildGeneralInformationEvent`. `GuildStateService` supplies hall ownership,
contribution totals, member counts and recipient lookup through an injected backend.
The conditional entry hook retains ranks → silent membership → header → member list
and is omitted for an unguilded character. Founding and both joining paths send
typed headers in their existing positions. The window's header still answers its
bare information request, after the window-open chest-tab response; it does not move
back into the earlier open-window burst that once left the experience bar at 0/0.

Identity uses the stored name, level and four distinct emblem values. The historical
`EmblemSymbolColor` storage property supplies the reviewed **background shape ID**;
`EmblemBackground` supplies background color and `EmblemSymbolRgb` supplies symbol
color. Guild identity and contribution totals use checked int32 conversions, while
experience and guild kamas remain int64. Contribution multiplication also checks for
overflow before a wrapped value can reach membership or the member list.

Membership keeps the existing `GuildStore.ContributedBy(characterId)` policy: the
persisted total of contributed guild kamas across stored contribution rows. It is
not replaced with weekly activity tokens or the inventory token stack. The reviewed
context placeholder is explicitly zero in silent entry/identity updates, matching
the former builder's omission. The protocol still represents nonzero historical
placeholder values without assigning them new gameplay meaning.

Headers now include `GuildHalls.ChoiceOf(guild.Id)?.HallId`, which the former builder
omitted: no choice is absent, and the hall ID is distinct from its theme. Founding
text, member count, stored experience/kamas and `GuildLevels` floors supply the other
values. At level 20 the next floor remains the top level's own floor, as the existing
curve defines. General score is explicitly zero because this model has no persisted
general-score source; raid scores and weekly activity points are different counters.

Rename/emblem broadcasts build each member's own membership, followed by a shared
immutable header. Join/leave/expulsion refreshes retain member-list-before-header
ordering. Contribution and mission milestone rewards send a fresh header only after
the existing reward notifications. Each recipient encodes through its own codec;
broadcasts capture the header once before awaiting sends. The existing turn is reused,
and source/conversion/codec/transport failures propagate without replay or retries.
`GuildProtocol.BuildMembership`, `BuildGuildInfo` and `GuildHandler.HeaderFrame` are
removed. The former builders populated only fields covered by the August semantic
contract; this audit does not classify their other declared fields as unused.

Injected server tests cover source policies, nested identity, numeric widths, level
boundaries, presence, snapshots, recipient isolation and failures. Socket/storage
regressions cover conditional entry, hall reloads, expulsion, member-specific rename/
emblem notifications and real contribution/mission milestone ordering. Historical
wire fixtures moved to the protocol suite, alongside optional hall zero/absence,
nested guild/social/emblem presence, int64 totals, nonzero score and context values.
These are conditional-hook migrations; the current initial sequence comprises
**32 unconditional direct sends + equipped-set snapshots + two optional sends**,
with no reference loader or recorded entry rows.

### Entry status, preferences and social notifications

Seven event types now use direct semantic construction across initial entry, map
initialization and their matching live paths:

- `AllianceMotdEvent` and `AllianceBulletinEvent` use `AllianceNoticeMessages` and
  injected `AllianceNoticeService`. Present information blocks retain exact text,
  author, int64 member IDs and int64 modification timestamps **in seconds**. Missing
  stored notices keep the empty-text/author, founding-timestamp fallback without
  persisting a row. Entry/join keeps MOTD then bulletin, with the original interleaved
  source-read/send boundary. Live edits retain permissions and refusal packets, save
  before broadcasting, capture one update and member list, and encode per online
  recipient. Failures stop subsequent sends without retries; delivery failure leaves
  the saved notice intact. `AllianceProtocol.BuildNotice` is removed.
- `ChatChannelsEnabledEvent` and `ChannelUpdateEvent` use `ChatChannelMessages` and
  session-owned subscription state. Defaults enable General, Team, Guild, Alliance,
  Group, Admin, Private, Information, Fight and Arena. Advertising/help/community
  feeds and the five reviewed opaque numeric slots start disabled. Those opaque
  slots remain representable when explicitly requested. The already-mapped
  `ChatChannelSubscriptionRequest` is handled by the typed, authenticated world-session
  boundary; active/disabled lists form a detached full snapshot, duplicates collapse
  and disabled wins overlaps. Values outside the reviewed enum fail before mutation.
  Outgoing state has no request UID, including after a zero or negative request UID.
  Subscription state survives delivery failure and remains session-scoped.
- The initial channel snapshot follows guild ranks. `GameEntryMapMessages` constructs
  twenty channel updates from that session's state between the semantic community
  and empty API-key events. All eleven recorded updates are excluded before payload
  construction. The fourteen channel/zaap/calendar reference frames passed the lossless
  scope audit. August client `hav.bngq` (RVA `0x586b440`) builds its channel mask by
  adding active entries then removing disabled ones; `hav.bngp` (`0x586aa60`) applies
  the individual channel/enable pair. These support the overlap/update policy.
- `ZaapKnownListEvent` uses `EntryEnvironmentMessages` at both initial and map entry.
  The live source retains the existing all-activated-and-reachable policy from
  `Interactives.DiscoveredZaapMaps`; it is not a new per-character discovery store.
  Detached snapshots preserve order, duplicates, empty lists and int64 map IDs.
  Eight single-map travel, house, haven-bag and fight hooks now construct the same
  event through their owning session's codec. `BuildDiscoveredZaaps` and
  `BuildMapDiscovered` are removed; the historical 45-map/182-byte fixture is in the
  protocol suite.
- `AlmanachCalendarDateEvent` comes from `Almanax.Today()?.Id`, using the existing
  server calendar and local-time clock rather than a day-of-year calculation or
  recorded constant. Missing data/day logs and omits the event. Source failures
  propagate. Midnight tests exercise the live manager's clock and calendar.
- `GuildMemberOnlineStatusEvent` uses injected `GuildPresenceService` after each
  recipient's existing roster update. It remains an opt-in login warning: self and
  offline sessions are excluded, disabled preferences suppress it, and logout sends
  only the roster update. Each recipient uses its own codec and existing turn;
  sequential failures stop delivery without retries. `BuildMemberConnection` is removed.

Semantic, source-failure and socket/storage regressions cover this batch. Independent
protocol fixtures pin all seven event aliases/payloads, notice presence/Unicode,
packed and unpacked lists, opaque channel slots, signed integer boundaries and the
subscription request's signed correlation IDs.

`AllianceRankMessages` directly constructs `AllianceRanksEvent` using the reviewed
`RankInformation` records: stored names/IDs, `Flag` as `Modifiable`, integer rights,
graphics and display order. Arrays are detached from mutable rank/right lists;
stored order, translation keys, custom names, duplicate/unknown rights and full
int32 values survive delivery. Private-information and graphics blocks remain
present even when their values are empty/zero. Outbound snapshots preserve stored
rights; management requests still apply their known-right and deduplication rules.

The injected `AllianceRankService` shares source and delivery policy across the four
outgoing list sites: entry, founding/joining, opening management and mutation
broadcasts. Entry sends ranks before silent membership; founding/joining retains
ranks, membership, information, joined notification, banner, redraw and notices.
`AllianceHandler.OnWorldEntryAsync` and `SendJoinedAsync` accept the owning session.
The welcome continuation also carries that session, and scopes remaining legacy
hooks to it. Application/invitation acceptance therefore encodes through the joining
recipient's codec and restores the caller's context, including on failures. Existing
session turns are reused rather than acquired recursively. An ungrouped character
receives no rank event; an existing pending application is still restored.

Create/edit/reorder/rights/remove operations broadcast only after successful storage.
Rank removal sends the replacement snapshot before affected members' rank updates.
Broadcasts capture one detached rank snapshot and member list before awaiting sends,
skip offline recipients, and encode through each recipient's codec. Sequential
source/codec/transport failures propagate and stop later sends without retries.
Alliances without stored ranks retain the catalogue-derived default template, including
all leader rights and empty lower-rank rights; this read fallback does not persist rows.
Malformed stored rights fail reading before encoding a partial snapshot.

`AllianceProtocol.BuildRanks` is removed. Its historical custom-rank payload and
former populated construction fields are fully covered by the reviewed August
contract. Independent protocol fixtures pin that payload, empty lists, independent
nested presence, packed/unpacked rights, duplicates and signed integer boundaries.
Injected tests cover snapshots, sources, audiences, codecs and failures. Socket/storage
regressions cover actual/default sources, entry/join ordering, founding, asynchronous
acceptance, management mutations/refusals, failed writes, malformed storage and context
cleanup. This delivery is tracked as conditional-hook coverage.

`EmoteOwnershipService` constructs the character's `EmotesEvent` directly from
`session.State.Emotes` and learns new attitudes through an injected catalogue/persistence
backend. Ownership keeps the existing union of the four starting attitudes, saved IDs
and server-authorized administrator catalogue grants. Snapshot IDs are sorted and
distinct; saved unknown IDs are retained, and an empty collection still sends a
snapshot. No capture IDs or payloads supply ownership.

Five captured `EmoteAddedEvent`s previously granted the recorder's attitudes during
initial entry, before the actual character's list arrived. These additions and the
already-skipped captured list have been deleted with the entry reconstruction. The
arena follows explicit character selection, and the profession boundary uses the final
consecutive set update, so neither depends on emote reference rows. All six rows
passed the lossless semantic-scope audit. The former live builders populated only
the mapped ID fields; additional wire-only learned-event strings remain outside
this construction scope.

The actual snapshot retains its map-initialization timing. `GameEntryMapInitialization`
owns the existing map, wardrobe, houses, journal, emotes, achievements, presets and
marks sequence through an injected backend. Legacy hooks run in the owning session's
scoped context, while the snapshot uses that session's codec directly. The existing
session turn and once-only map-initialization lifecycle remain in force. A failed
snapshot stops later hooks and restores the caller's context.

`Emotes.SendListAsync` and `LearnAsync` now take the owning `GameSession`. Quest,
achievement and attitude-item learning callers pass it explicitly. Learning validates
the catalogue entry and existing ownership, saves, updates the session's ownership,
then sends `EmoteAddedEvent`. Unknown/already-owned attitudes return false. Emote
database writes now rethrow after logging; a persistence failure cannot grant ownership
or announce success. After a successful save, codec/transport failures retain ownership
and propagate; another attempt cannot grant or notify twice. Attitude-item consumption
still follows successful notification; achievement result messages follow their emote
notifications. `EmoteProtocol.BuildList` and `BuildLearned` are removed, with historical
compatibility fixtures moved to the protocol suite.

The welcome sequence constructs stable acceptance, time, optional-feature,
capability, rights, trust, session-ready, subscription-duration and queue events
directly. Server settings use the session's launch language and retain their
original payload because field 7 remains unmapped.

The corrected August identities are `krs` = `ServerSessionReadyEvent` (the empty
DH-handshake trigger) and `mgz` = `AccountSubscriptionElapsedDurationUpdateEvent`.
The latter carries elapsed subscription **seconds**, not a catalogue marker.
The account backend stores subscription expiry, not elapsed subscribed time;
the server explicitly sends zero elapsed seconds rather than copying a captured
value or inferring duration from expiry. Trust status retains Jondo's existing
`Certified = true` policy.

`CharacterLook` now binds `LastConnectionDate` and `LastDisconnectionDate` as
strings. Selection supplies the connection boundary from the character's stored
`LastConnection`, refreshed when selection loads the character. No disconnection
boundary is persisted: that field remains empty instead of being filled with a
previous login or creation date. Empty strings retain the client's default-date
behavior. Character-list ordering still uses the existing last-connection value.

`EntityLook.SubEntities` preserves recursive rider, pet and aura looks with
binding-point index/category. The category enum binds all 13 reviewed protocol
values explicitly, including unknown values 9 through 14. Its stable ordinals
are independent of the non-contiguous wire values; 5 and 8 are not declared
protocol values. The reviewed UnderwaterBubbles label stays at wire 7, without
renumbering it to match a different native category convention.

Map information now includes obstacles, houses, cell colours, interactive
elements and skills, fight summaries/options and stated elements. `AgeBonus`
and skill `NameId` are nullable integers to preserve optional zero versus absence.
`MapCellColorValue` binds red/green/blue/alpha independently (August tags
4/2/1/5). `MapSpecificComplementaryInformationCaseNone` represents an unset
map-specific oneof; it differs from present-empty coordinates. No coordinates
or special-map payload are invented for an ordinary map.

`GameEntryMessages` is a transitional **lossless projection boundary**, not a
replacement for the remaining builders. It handles remaining feature-hook payloads,
character lists and the initial actor list; initial/map sequencers send semantic
events directly.
For each mapped event it decodes the stable object, re-encodes it and compares the
complete raw protobuf payload and envelope. Parsed-value comparison tolerates
field order, packed lists and omitted scalar defaults, but preserves nested
unknown fields, oneof/message presence and envelope extensions. Descriptor
traversal is used only to normalize Any payload serialization for that comparison;
conversion and dispatch remain generated. Only lossless projections go through
the owning session's typed send. Other packets retain their exact original bytes.
Transport failures are not caught as conversion failures and never trigger a resend.

The reviewed dates, subentities and supported map content can now pass this
boundary without loss. The November mapping refresh also supplies house-instance
fields, extended coordinates, social information, quests, achievements, presets,
wardrobe and the conditional entry dependencies. House sale, admin and door locks
remain separate values; prices preserve optional int64 presence. The bridge still
preserves unresolved nested data and type-only payload contents. Raw-only fields
include the extra appearance payload,
subentity string, map aggressive-monster candidate, colour component, fight string,
house members and current-map enum. Unmapped does not establish decoy status.
Remaining legacy feature builders still require migration to direct state-based
construction, with additional mapping coverage where needed. The manifest and its
loader have been retired; feature-hook migration is tracked separately.
The normal map's trailing `lva` is sent as `AreaFightModifierUpdateEvent`, rather
than introducing a semantic "actors complete" message. Existing sequencing and
the one-time map block, including the first-ping fallback, are retained.

`EntryCoverageTests` uses independent August wire fixtures for corrected identities,
dates, recursive subentities, enum numbers, map collections, RGBA ordering, optional
presence, house branches and unset versus present-empty map cases. `GameEntryTests`
covers typed request decoding (including signed UIDs), phase and single-use ticket
checks, welcome order/language, typed plain-map sends, nested-field preservation
and small independent entry payload fixtures with exact unprojected-byte preservation. Independent continuation/response
fixtures verify zero/negative UIDs, envelope-kind rejection, owning-codec delivery
and live weekly-reset response construction. Live-client entry and fight-rejoin
smoke tests remain necessary before claiming client-level compatibility.

`GameEntryServiceTests` checks post-selection ordering, session binding, welcome
notices and failures. `GameEntryLifecycleTests` exercises identification ordering,
normal/forced selection, phase rejection, per-session map initialization, duplicate
confirmations, ping fallback, travel, return/re-entry and fight-readiness routing
with injected backends and the real session codec, without a world database.

`GameEntryInitialMessagesTests` exercises the production initial-message sequencer
with independently specified populated/default semantic messages and collections,
both room dispatch positions, full message order, optional previous visits,
owning-codec/session isolation and transport failure propagation. The unrelated storage-backed legacy
builders and live feature hooks are isolated from these tests.

`ProfessionEntryTests` checks catalogue eligibility and base-job handling, default
and progressed XP (including exact boundaries, the level cap and totals beyond
int32), gathering/crafting cases, two-character session isolation, profession
ordering and the conditional subscription hook. Assertions use semantic messages
and collections, with catalogue-based initialization instead of a captured profession body.

`AlterationSnapshotTests` checks expiration kinds, timestamp widths, scalar/dice/date
effects, explicit absence, zero counters, reverse effect order, minute boundaries
and clamping, Paris winter/summer/DST dates, missing definitions and checked numeric
limits. Service tests cover per-character source/codec routing, a single clock value,
post-fight ordering under the existing turn and failure/context restoration.
Initial-entry tests retain snapshot-before-modifiers
ordering. `ObjectEffectPresenceTests` independently verifies absent, integer-zero
and present-empty dice wire presence. The existing general alteration-entry regression
now exercises the semantic sender and named state instead of a recorded protobuf.

`ArenaPlayerInformationTests` checks distinct current/best leagues, placement and
current ratings, unequal counters, active/expired/no-penalty dates, invariant date
formatting, owning-session delivery, initial ordering and return failure boundaries.
The general ladder regressions now assert semantic standings from the live source.
`ArenaInformationTests` independently pins populated wire fields, signed league
sentinels, all four mode numbers and absent/present-empty ranking presence.

Server `EmoteOwnershipTests` covers starting/saved/administrator ownership, unknown
saved IDs, sorted/detached snapshots, owning-session delivery, refusal and failure
boundaries, and the production map-hook sequence under the existing turn. Initial
entry regressions verify the arena/profession boundaries and absence of recorded
emote grants. Protocol `EmoteOwnershipTests` independently checks empty,
packed/unpacked and duplicate IDs, int32 learned IDs and the historical starting/banner
fixtures. General `EmoteLearningFlowTests` verifies item-consumption and achievement
result ordering over real sockets; persistence regressions force a database write
failure to verify that the production adapter propagates it.

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
