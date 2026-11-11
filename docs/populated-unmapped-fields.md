# Non-default fields outside semantic mappings

Evidence snapshot: **10 November 2026**. Game profile **3.6.10.10**, using the
**11-08-2026** wire identities. Client patches 3.6.10.10 and 3.6.10.11 share this
Game profile. Tags, aliases and native evidence below belong to that build.
Mapping status updated **11 November 2026**; capture/population evidence retains
its original snapshot date.

This inventory records fields with **proven non-default population** and an
unused, unresolved or incomplete semantic-mapping status. Each entry has a
capture, JondoEmu documentation or JondoEmu code source. Population means a
nonzero number, a true boolean, non-empty text, or a populated collection/message
containing non-default data.

**Capture** means the checked-in reconstruction or a cited measurement of
official-server traffic. **Code** means a JondoEmu construction path; its condition
is stated explicitly and does not claim an official-server observation or a send
on every login. Values seen in one capture are observations, not initialization
constants for every account.

**Outbound construction policy:** indeterminate fields are treated as unused.
The after-selection fields listed below are omitted, without a raw compatibility
send. Native audit outcomes are retained as evidence provenance, not as a requirement
to keep sending those values. Raw parsing still preserves historical data for inspection.

## After-selection omission policy

The following fields are **unused for outbound construction**, by the project's
11 November 2026 decision to treat indeterminate values as unused:

| Owner | Omitted field | Construction |
|---|---|---|
| OutfitAuraChangedEvent / `lym` | Tag **1**, optional int32; captured **206** | Empty typed event; shared legacy aura writer also emits no value. |
| OrnamentSelectedEvent / `hif` | Tag **1**, int32; captured **135** | Empty typed event; shared legacy ornament writer also emits no value. |
| ConfigurationEvent / `krj` | Tag **1**, message; captured `{1:30, 2:5, 3:300}` | Construct only the mapped guild configuration. |
| GuildActivityConfiguration / `krh.krg.krf` | Tag **7**, message | Construct only the six mapped activity settings. |
| CharacterColors / `lvs` | Tag **2**, int32 | Supply unlocked/total slots and palettes only. |
| ColorPalette / `lvd` | Tag **1**, repeated int32 | Supply the mapped tag-2 RGB/inheritance values only. |
| OutfitLookUpdatedEvent / `lxc` | Tag **3**, EntityLook | Supply the owned identifier and mapped tag-2 look only. |

`InitialPresentationMessages` and the relevant send/build sites link to this section.
After-selection construction reads no manifest rows, copies no captured extras and
has no legacy-send callback. Its whole `afterCharacter` block is excluded before
reference payload construction, including unknown future aliases. The ignored
quest-objective updates, title-selection error and recorded cosmetic inventory do
not schedule initial sends either.

The renewed August-build MCP audit classified properties **204601, 192571, 201227,
201206, 204297, 204236 and 204465** as indeterminate: no use witness with incomplete
native caller coverage. That is the recorded native finding; **unused/omit** is the
explicit outbound policy applied to it. No new semantic names or bindings were
invented for these fields. Unmigrated feature evidence below remains historical
documentation rather than a requirement to add compatibility paths.

## 1. Populated but unused in the inspected client flow

### Gift description: `jtg.jtf.jte`, tag 6

Stable owner: **GameActionItemContent**. Wire type: `string`. From
**GameActionItemListEvent** / `jtg`, the payload path is
`3[*] → 1 → 6`: gift group, content, description-like string.

- **Historical capture:** the removed `afterConfirm` gift list contained non-empty
  tag-6 strings, including the second and third gift groups. The block is retired
  rather than retained as a production source.
- **Historical code:** `GiftHandler.BuildList` wrote `gift.Description` through
  `StrIfNotEmpty(6, ...)`. That raw builder is removed. `GiftMessages` now supplies
  only titles, item previews and redemption IDs from current account gifts;
  descriptions remain stored but are omitted from semantic sends.
- **Client finding:** the registered gift receiver consumes content title and
  item previews, plus group redemption IDs. It constructs a `GameActionObject`
  holding those values rather than retaining the original content message. The
  inspected conversion/UI paths do not consume the tag-6 description.
- **Decision:** this field is explicitly excluded from client-used semantic
  remapping. Its exact semantic name need not be invented. The finding is bounded
  to the inspected gift flow, not a global decoy classification.

The non-empty historical text does not reopen this field as a semantic-remap gap
or require a compatibility member in newly constructed messages.

## 2. Populated scalar payloads with type-only recognition

These message identities are named, but the listed payload fields are not
semantically mapped. Recognizing a message type does not establish consumption
or meaning of its scalar.

| Message / wire identity | Field | Non-default population evidence | Client-use finding |
|---|---|---|---|
| OutfitAuraChangedEvent / `lym` | Tag **1**, optional int32 | **Documented capture measurement:** **206** in the retired entry reconstruction. **Historical code:** `BuildAura` emitted the field when `aura != 0`; it now returns an empty payload. | **Unused/omit** under the [after-selection policy](#after-selection-omission-policy). Native finding: no positive business consumer; indeterminate caller coverage. |
| OrnamentSelectedEvent / `hif` | Tag **1**, int32 | **Documented capture measurement:** **135** in the retired entry reconstruction. `BuildOrnamentUpdated` now returns an empty payload. | **Unused/omit** under the [after-selection policy](#after-selection-omission-policy). Native finding: no positive business consumer; indeterminate caller coverage. |
| CommunityChannelEvent / `hou` | Tag **1**, int32 | **Documented capture measurement:** **2** in the retired entry reconstruction. | Parser registration is known. The deeper generic/native investigation did not establish an ordinary business subscriber; global non-use is not proved. |
| TitleSelectionErrorEvent / `hii` | Tag **1**, enum | **Documented capture measurement:** numeric value **2** in the retired entry reconstruction. | A registered handler exists, but inspection did not establish a message-rooted load of this enum into logging/gameplay state. |
| TitleSelectedEvent / `hid` | Tag **1**, int32 | **Code:** [`BuildTitleUpdated`](../Jondo.Unity.Server/Network/ConnectionProtocol.cs#L2585-L2595) returns an empty payload for `Wardrobe.None`, otherwise writes `titleId`. [`Wardrobe.None` is 0](../Jondo.Unity.Server/Managers/Wardrobe.cs#L21-L24), so the populated branch supplies a nonzero value. This row is code evidence, not a quoted official-capture value. | Type-only recognition. No positive business reader for the scalar was established. |

The `hii` enum declares values 0/1/2, but their meanings are not established here.
For `hou`, neither acknowledgement semantics nor a `community` field mapping is
established. Incomplete native-call coverage prevents a global non-use conclusion
for these scalar payloads.

Map entry now constructs `CommunityChannelEvent()` directly and omits the recorded
tag-1 value. This is an explicit type-only outbound policy; the native-use finding
remains indeterminate and no field name or acknowledgement meaning is inferred.

**Handling:** apply the omission policy above rather than retaining aura/ornament
values in compatibility sends. `hii` value 2 is not successful initialization
state; no title-selection error is generated at initial entry. Do not introduce
scalar names solely from the surrounding message name or builder parameter.

## 3. Non-default data with unresolved client-use or field semantics

| Owner / field | Non-default population evidence | Current finding |
|---|---|---|
| ServerSettingsEvent / `hoy`, tag **7**, int32 | **Code:** [`BuildHoy`](../Jondo.Unity.Server/Network/ConnectionProtocol.cs#L227-L235) writes **200** unconditionally. | Unmapped. The [welcome integration note](game-protocol.md#game-identification-selection-and-entry-migration) records why delivery retains its raw path; this is not an active remap task or a client-unused classification. |
| SocialEmblem / `lld`, tag **4**, int32, in alliance identity | **Code:** `AllianceProtocol.Emblem` writes stored `AllianceStore.Alliance.EmblemBackgroundShape` when nonzero. Creation/modification readers can persist that slot. This is conditional code evidence, not an official-capture population claim. | The August `SocialEmblem` contract represents tags 1/2/3/5; tag 4 remains unmapped. Historical storage labels do not establish its meaning. A typed alliance identity conversion would need to retain this populated slot before replacing that construction path. |
| QuestFinished / `idr.idq.idp`, tag **1**, int32; root path `3[*] → 1` | **Code:** [`QuestProtocol.BuildJournal`](../Jondo.Unity.Server/Network/QuestProtocol.cs#L86-L89) writes **1** for each finished quest. The [same file's capture description](../Jondo.Unity.Server/Network/QuestProtocol.cs#L36-L64) records that finished-entry shape. | Indeterminate. A count-like interpretation is a historical lead, not a reviewed binding. ID-only conversion does not retain this value. |
| ConfigurationEvent / `krj`, tag **1**, message | **Documented capture measurement:** the retired entry reconstruction contained the non-empty message `{1: 30, 2: 5, 3: 300}`. | **Unused/omit** under the [after-selection policy](#after-selection-omission-policy). Native finding remains indeterminate. The sibling guild configuration at tag 2 is constructed from server policy. |
| InfiniteDreamStateEvent / `izg`, tag **17**, int32 | **Code:** [`BuildDreamState`](../Jondo.Unity.Server/Network/DreamProtocol.cs#L429-L433) uses `VarIfNotZero(17, sueno.Arena)`. [`Dreams.StartingArenas`](../Jondo.Unity.Server/Managers/Dreams.cs#L251) returns **1** for the supported starting-arena branch, and [new-dream initialization](../Jondo.Unity.Server/Managers/Dreams.cs#L1078-L1096) stores that result in `Arena`. | Populated raw-only. The builder's life/arena interpretation has not completed the semantic evidence gate. |
| InfiniteDreamMonster / `izg.izf.ize`, tag **3**, int32; root path `3[*] → 3` | **Documentation:** [`Dreams.DreamerLevel`](../Jondo.Unity.Server/Managers/Dreams.cs#L2403-L2409) describes captured monster level **303** alongside dreamer level 353. **Code:** [`BeastEntry`](../Jondo.Unity.Server/Network/DreamProtocol.cs#L269-L278) writes `beast.Level`, supplied by [`DreamHandler.BestiaryOf`](../Jondo.Unity.Server/Handlers/DreamHandler.cs#L658-L675). | The builder's proposed level field remains unmapped; the inspected native conversion did not establish its individual use/meaning. |

The quest investigation found no positive native-use witness for QuestFinished
tag 1 / `fowh`; its count-like interpretation remains a historical lead. Other
quest extras also remain raw-only: QuestsEvent optional int64 tag 2 / `fown`,
QuestActive int32 tag 1 / `fpal`, and QuestActiveDetails int32 tag 3 / `fpah`.
These are historical native findings, not grounds to infer semantics from wire shape.

For configuration, the mapped guild settings at root tag 2 have positive client
use. The independently populated outer tag-1 branch and extra nested guild-activity
tag 7 are **unused/omitted** under the after-selection policy.

Outfit tag 4 / `gdxi` and cosmetic-inventory tags 1/2 / `gdnx`/`gdny` had
indeterminate native findings in the historical audit. These findings do not
require compatibility members in new construction. Mapped optional title,
ornament, aura and skin values still distinguish absence from present zero.

These population/compatibility records document historical evidence and the
explicit outbound construction policies above. They do not designate outstanding
remap tasks.

**Handling:** the after-selection fields are omitted under the explicit policy above;
no compatibility path is introduced for them. Other rows retain historical producer
and native findings for their feature scopes. Builder labels such as `Arena` and
`Level` do not independently prove protocol semantics.

## Spell-book construction policy

The `SpellsEvent` / `hms` conversion explicitly omits the unmapped root boolean at
tag **2** (`fmsf`, property node **193043**) under the user's decision. The former
`ConnectionProtocol.BuildSpellList` populated it unconditionally with `Var(2, 1)`;
this is historical local-producer evidence. Bounded August native analysis found no
positive business-use witness and reported incomplete native caller coverage. Its
status remains **indeterminate**, rather than proven unused. The historical
damage-preview hypothesis is not an established semantic meaning.

`SpellItem` / `hmw` also has an unmapped boolean at tag **2** (`fmsq`, property node
**193054**). It has a positive native-use witness through `Core.bf.nra` at RVA
**`0x497a280`**. The former spell-list writer never populated it, so current direct
construction retains its default **false**; no true-valued capability is claimed.
Both findings are build-local to **jondo-11-08-2026**, verified through MCP bootstrap,
field references and decoy classification. `SpellMessages` constructs only the
reviewed human/mutant lists, IDs, grades and opaque `SpellType` variants.

## Keeping the inventory reproducible

The capture-derived entry reconstruction has been deleted with its loader.
Measurements retained above are documented historical capture evidence, rather
than links to a current runtime or test fixture. In that reconstruction, the outer
`payload.n = 2` was not another field of the named protocol payload. Paths in this
document start inside the message body. Repeated-entry markers such as `[*]` mean
each element, not a wire tag.

For each addition, record a non-default value or an explicit conditional
construction path, link the population source, and cite the client-use finding
separately. Preserve whether the evidence is a checked-in capture reconstruction,
a documented capture measurement, or local code. Do not turn a code-derived value
into an official-server capture claim.

Stable projection and raw wire parsing have different retention guarantees; see
[`game-protocol.md`](game-protocol.md#using-the-codec). Recording one of these cases
does not itself change replay, construction, omission policy or protocol bindings.
