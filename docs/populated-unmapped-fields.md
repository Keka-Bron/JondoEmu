# Non-default fields outside semantic mappings

Evidence snapshot: **10 November 2026**. Game profile **3.6.10.10**, using the
**11-08-2026** wire identities. Client patches 3.6.10.10 and 3.6.10.11 share this
Game profile. Tags, aliases and native evidence below belong to that build.
Mapping status updated **11 November 2026**; capture/population evidence retains
its original snapshot date. Welcome-settings omission policy and native-use finding
updated **12 November 2026**. Guild-hall selection boolean classification and
installed customization-field coverage updated **13 November 2026**.
Quest-journal construction policy updated **16 November 2026**.
Prism-list construction policy updated **16 November 2026**.
Wardrobe construction policy updated **17 November 2026**.
Guild-roster/startup-social construction policy updated **17 November 2026**.

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
The after-selection fields and welcome-settings extra listed below are omitted,
without a raw compatibility send. Native audit outcomes are retained as evidence
provenance, not as a requirement to keep sending those values. Raw parsing still
preserves historical data for inspection.

## After-selection omission policy

The following fields are **unused for outbound construction**, by the project's
11 November 2026 decision to treat indeterminate values as unused:

| Owner | Omitted field | Construction |
|---|---|---|
| OrnamentSelectedEvent / `hif` | Tag **1**, int32; captured **135** | Empty typed event; shared legacy ornament writer also emits no value. |
| ConfigurationEvent / `krj` | Tag **1**, message; captured `{1:30, 2:5, 3:300}` | Construct only the mapped guild configuration. |
| GuildActivityConfiguration / `krh.krg.krf` | Tag **7**, message | Construct only the six mapped activity settings. |
| CharacterColors / `lvs` | Tag **2**, int32 | Supply unlocked/total slots and palettes only. |
| ColorPalette / `lvd` | Tag **1**, repeated int32 | Supply the mapped tag-2 RGB/inheritance values only. |
| OutfitLookUpdatedEvent / `lxc` | Tag **3**, EntityLook | Supply the owned identifier and mapped tag-2 look only. |

`OutfitAuraChangedEvent` / `lym` tag **1** left this list on 18 November 2026: it is
mapped as `AuraEmoteId` and carries the character's main aura, absent for none. Seen in
game, the client reads it as the aura «Activer automatiquement mon aura de face» shows:
with the value omitted, facing the screen never showed one.

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

## Welcome-settings omission policy

`ConnectionProtocol.SendWelcomeAsync` constructs `ServerSettingsEvent` directly
through the owning session's codec: arena leave-ban time **30**, `Classical` server
type, free autopilot enabled, mono-account disabled, community **0**, and the
session's launch language. Community is not a subscription or character-limit marker.

The former `BuildHoy` wrote **200** to unmapped int32 tag **7** unconditionally.
The **12 November 2026** decision explicitly omits this field from welcome settings,
extending the indeterminate-field outbound omission policy to this send. Direct
construction does not preserve that historical value or assign it a semantic name.

MCP bootstrap and native classification were rechecked against **jondo-11-08-2026**:
tag 7 / `fnaz`, property **193271**, backing field **263162** remains **indeterminate**.
No usage witness was found, with incomplete native caller coverage. Omission is an
outbound policy decision, not proof that the field is globally unused. Real-client
welcome/selection acceptance remains to be checked.

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

### Guild-hall selection boolean: `jqj`, tag 1

**Unused by client for Jondo's outbound construction**, by the **13 November 2026**
decision. Omit this boolean from new semantic sends; it is not an outstanding
field-remapping task. Preserve the selected-empty guild-hall selection branch.

- **Wire path:** `MapComplementaryInformationEvent` / `jss`, tag **4** →
  `MapComplementaryGuildInformation` / `jry`, branch **3** → `jqj.fupf`, boolean
  tag **1**, property **198365** in the August build.
- **Historical code population:** the removed `GuildProtocol.BuildHallVisit` wrote tag 1 as **true** when
  `own == false && mayChoose == true`; false is omitted. `mayChoose` is the local
  builder's label, not an established semantic field name.
- **Client finding:** the inspected map handler **158876 / `0x4cfbb70`** sets
  guild-hall state **3** from the selected branch without reading the boolean.
  `MapInfoUI.OnClickBtnGuildHall` **139141 / `0x6366640`** enables "Choose guild hall"
  from that state, independently of the boolean. No gameplay/UI use was found;
  the references found are generated protobuf operations and accessors.
- **Evidence boundary:** native classification remains **indeterminate**, with
  incomplete native-call coverage. "Unused" is Jondo's explicit handling decision
  based on the inspected client flow, not proof of global non-use. No boolean
  meaning, polarity or cross-version field correspondence is inferred.

The sibling `jrn` payload's three rendering fields are now mapped in Jondo; see
[guild-hall customization correspondence](game-protocol.md#upstream-guild-hall-customization-mapping).

## 2. Populated scalar payloads with type-only recognition

These message identities are named, but the listed payload fields are not
semantically mapped. Recognizing a message type does not establish consumption
or meaning of its scalar.

| Message / wire identity | Field | Non-default population evidence | Client-use finding |
|---|---|---|---|
| OrnamentSelectedEvent / `hif` | Tag **1**, int32 | **Documented capture measurement:** **135** in the retired entry reconstruction. The typed event encodes to an empty payload. | **Unused/omit** under the [after-selection policy](#after-selection-omission-policy). Native finding: no positive business consumer; indeterminate caller coverage. |
| CommunityChannelEvent / `hou` | Tag **1**, int32 | **Documented capture measurement:** **2** in the retired entry reconstruction; the 15 November emulator reply carried **1**. | **Omitted** at both entry and request-driven owner sends. Renewed MCP classification remains indeterminate, with no use witness and incomplete native caller coverage. |
| TitleSelectionErrorEvent / `hii` | Tag **1**, enum | **Documented capture measurement:** numeric value **2** in the retired entry reconstruction. | A registered handler exists, but inspection did not establish a message-rooted load of this enum into logging/gameplay state. |
| TitleSelectedEvent / `hid` | Tag **1**, int32 | **Historical code:** the retired `BuildTitleUpdated` wrote a nonzero title ID unless `Wardrobe.None` (0). The 15 November emulator capture carried **77**. This is local producer/traffic evidence, not an official-server capture value. | **Omitted** under the [wardrobe construction policy](#wardrobe-construction-policy). Renewed MCP review remains indeterminate, with no positive business-use witness. |

The `hii` enum declares values 0/1/2, but their meanings are not established here.
For `hou`, neither acknowledgement semantics nor a `community` field mapping is
established. Incomplete native-call coverage prevents a global non-use conclusion
for these scalar payloads.

Map entry and the `hos`-driven owner reply construct `CommunityChannelEvent()` directly
and omit tag 1. This is an explicit type-only outbound policy; the native-use finding
remains indeterminate and no field name or acknowledgement meaning is inferred.

**Handling:** apply the omission policy above rather than retaining ornament
values in compatibility sends; the aura is mapped (`AuraEmoteId`, see above). `hii` value 2 is not successful initialization
state; no title-selection error is generated at initial entry. Do not introduce
scalar names solely from the surrounding message name or builder parameter.

## 3. Non-default data with unresolved client-use or field semantics

| Owner / field | Non-default population evidence | Current finding |
|---|---|---|
| ServerSettingsEvent / `hoy`, tag **7**, int32 | **Historical code:** the removed `BuildHoy` wrote **200** unconditionally. | **Omitted** under the [welcome-settings policy](#welcome-settings-omission-policy). Native finding remains indeterminate; this is not an active remap task or a client-unused classification. |
| SocialEmblem / `lld`, tag **4**, int32, in alliance identity | **Code:** `AllianceProtocol.Emblem` writes stored `AllianceStore.Alliance.EmblemBackgroundShape` when nonzero. Creation/modification readers can persist that slot. This is conditional code evidence, not an official-capture population claim. | The August contract represents tags 1/2/3/5; tag 4 remains unmapped. Renewed MCP classification is indeterminate with incomplete native caller coverage. Direct map actor construction omits it under the map construction policy below; standalone alliance serializers retain their own construction paths. |
| QuestFinished / `idr.idq.idp`, tag **1**, int32; root path `3[*] → 1` | **Historical code:** the removed `QuestProtocol.BuildJournal` wrote **1** for each finished quest. Its capture description recorded the same shape; historical payload fixtures are retained in [`QuestStateContractTests`](../Jondo.Unity.Protocol.Game.Tests/QuestStateContractTests.cs). | **Omitted** under the [quest-journal construction policy](#quest-journal-construction-policy). Native finding remains indeterminate; the count-like interpretation is not a reviewed binding. |
| ConfigurationEvent / `krj`, tag **1**, message | **Documented capture measurement:** the retired entry reconstruction contained the non-empty message `{1: 30, 2: 5, 3: 300}`. | **Unused/omit** under the [after-selection policy](#after-selection-omission-policy). Native finding remains indeterminate. The sibling guild configuration at tag 2 is constructed from server policy. |
| InfiniteDreamMonster / `izg.izf.ize`, tag **3**, int32; root path `3[*] → 3` | **Historical code:** the retired bestiary writer populated it from the fighter's level. | Omitted from the typed snapshot. The inspected native conversion did not establish its individual use/meaning. |

The separate root scalar `izg.17` is resolved: the reviewed exports bind `InfiniteDreamStateEvent.Life` (November `jaq.19`), populated from `sueno.Arena`.

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

### Fight placement: announcement and join summary

**Code evidence:** Before the participant placement board was migrated, its
[raw builders](../Jondo.Unity.Server/Network/FightProtocol.cs) populated the
following fields on Game profile `3.6.10.10` (also used by `3.6.10.11`):

| Owner / field | Historical Jondo write | August property ID |
|---|---|---|
| FightStartingEvent / `kam`, tag **3**, int64 | `BuildFightAnnounced` wrote `fight.DefenderLeaderId` on every placement announcement. For a monster fight, this was the group's contextual ID. | `199577` |
| FightJoinEvent / `kaa`, tag **2**, bool | `BuildDuelSummary` wrote `true` for duels. `BuildFightSummary` omitted the tag for timed placement. | `199527` |
| FightJoinEvent / `kaa`, tag **3**, bool | Both `BuildDuelSummary` and `BuildFightSummary` wrote `true`. | `199528` |

**Current policy:** The typed participant board omits all three fields. The
`jondo-11-08-2026` native audit found no use witness for any of them, but caller
coverage was incomplete. Their client use is **indeterminate**; the audit does
not establish that omitting the historical values is harmless.

### Fight join refusal

**Capture and code:** In the August dungeon party-search capture, frame 856,
`FightChallengeJoinRefuseEvent` (`jxs`) carries the named fighter's ID in tag 1
(`int64`, property `199219`) and the party restriction reason in tag 2 (16).
`FightJoinProtocol.BuildJoinRefused` wrote both. The typed path sends only the
reviewed `TeamLimitedByMainCharacter` reason to the requester. Omitting the ID
changes outbound behavior: `jondo-11-08-2026` found no native use witness, but
caller coverage is incomplete, so client use is indeterminate. These tag and
property numbers are not evidence for the beta profile.

## Quest-journal construction policy

The **16 November 2026** journal/followed/marks migration applies the existing
indeterminate-field omission rule. `QuestMessages` constructs the mapped quest
IDs, step IDs and objective pending flags from current character state. Objective
declaration order is retained; dialog parameters are empty and completion messages
remain absent, matching the former journal builder's inputs.

- `QuestFinished` tag **1** / `fowh`, property **194504**, is omitted. The retired
  writer's constant **1** is historical population evidence, not an established
  count field. Its recorded native finding remains **indeterminate**, with no
  positive business-use witness; this migration does not claim global non-use.
- `QuestsEvent` tag **2** / `fown`, `QuestActive` tag **1** / `fpal`, and
  `QuestActiveDetails` tag **3** / `fpah` remain unset. The retired journal writer
  did not populate them. No new names or compatibility fields are introduced.
- Finished IDs are supplied as `QuestFinished` entries. The mapped
  `ReinitializedDoneQuestsId` list remains empty under the existing server policy.
  Standard protobuf encoding omits an empty packed list instead of reproducing
  the old writer's explicit `22 00`; an empty journal event is still sent.

The maintained contracts and these unmapped fields were rechecked through MCP for
**jondo-11-08-2026**. This is direct construction under the documented policy, not
a lossless reproduction of the legacy finished-entry bytes. Independent fixtures
separate historical raw retention from intentional semantic omissions and empty
packed-list normalization. Real-client journal/tracker acceptance remains pending.

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

## Map snapshot construction policy

Direct roleplay map snapshots and typed actor appearances/refreshes—including
standalone collector/prism placement, restoration and conquest redraws—construct
only the reviewed semantic fields. In particular:

- `SocialEmblem` / `lld` tag **4**, property **203185**, remains indeterminate:
  renewed `jondo-11-08-2026` MCP classification found no positive business-use witness
  and reported incomplete native caller coverage. The source column's historical
  `EmblemBackgroundShape` label is not a new semantic name. Direct map actors omit
  this field; tag **2** remains the reviewed background shape supplied by the existing
  `EmblemSymbolColor` column, independently of that omitted slot.
- `MapComplementaryHavenBagInformation` / `jqd` tag **3**, property **198342**, also
  remains indeterminate under the same MCP check. The legacy `HavenBagInfo` writer
  populated it from `Merkasako.Rooms`. Direct map snapshots omit it. Tag **4**,
  property **198343**, has the same indeterminate classification and was not populated
  by that writer; no type-only child or gameplay value is invented for it.
- The reviewed haven-bag values are **theme at tag 1** and **room ID at tag 2**.
  Direct snapshots supply the actual theme and current owner's room, correcting the
  legacy writer's reversed arguments. Owner ID/name/level retain their own presence,
  including an absent character-basic complement.

These are explicit outbound omission/construction decisions for the migrated map
path, not proven-unused native classifications or claims of lossless legacy-byte
reproduction. They follow the handoff's omission rule for indeterminate values;
population alone does not reopen a field-remapping task. The direct world prism list
also follows this emblem policy. Other alliance, incremental prism, equipment/crystal
and haven-bag feature serializers remain separate migration work.

## Prism-list construction policy

`PrismHandler.OnWorldEntryAsync` constructs `PrismListEvent` / `ife` directly from
current prism/alliance stores through `PrismMessages`, using the owning session's
codec. It deliberately omits `PrismLocalizedInformation` / `lhm` tag **5** (`gbbt`,
property **202746**, backing field **277721**) under the indeterminate-field policy.

- **Historical local population:** `PrismProtocol.Information` writes the alliance's
  UUID at tag 5 for world lists and other prism information. This is producer evidence,
  not a semantic name or proof that the client consumes this duplicate string.
- **Native review:** the **16 November 2026** `jondo-11-08-2026` MCP bootstrap confirms
  that tag 5 remains unmapped. `code.identify_decoys` found no business-use witness and
  reported incomplete native caller coverage; its classification is **indeterminate**.
- **Construction decision:** omit the localized tag-5 string, with no raw fallback.
  Retain the reviewed alliance UID under `PrismInformation.alliance_information`.
  Nested alliance emblems use reviewed tag 2 and omit indeterminate tag 4 under the
  map snapshot policy above. This is an omission decision, not globally proven non-use
  or a claim of lossless legacy-byte reproduction.

Foreign views omit both owner-only crystal fields. Own views retain optional zero
remaining crystals and a present crystal → inventory → empty item when no crystal is
equipped; those nested wrappers are not omitted with the duplicate UUID. Equipped
crystals use semantic item effects and checked int32 synthetic item UIDs. Other
unmapped list/crystal fields were not populated by the retired world-list path and
remain unset; no gameplay meaning is inferred for them.

## Wardrobe construction policy

The **17 November 2026** wardrobe/cosmetic migration constructs `hhy`, `lwt`, `lyt`,
`lym`, `hid`, `hif` and `lxc` directly at the owning entry/announcement send sites.
It follows the existing indeterminate-field omission rule and corrects one source
assignment using the reviewed August contract:

- `Outfit` / `lzc` tag **11** is **`head_id`**, property **204662**, backing field
  **280676**, offset **92**. The former writer's `outfit.Number` was a server sorting
  ordinal, not a head identifier. Direct outfits supply the live character head or
  saved head, falling back to the character head when the saved head is nonpositive.
  The still-legacy window/action adapters share this same semantic outfit factory.
  `Number` continues to determine store/list ordering only.
- MCP bootstrap for **jondo-11-08-2026** reconfirmed the head mapping. Native
  conversion `Core.lk.biw` at **`0x622fc60`** loads `lzc.feol` into its outfit state;
  P-code records the offset-92 load at **`0x622ff2a`**. This corroborates client use
  and does not establish a separate ordinal field.
- `TitleSelectedEvent` / `hid` tag **1** (`flzt`, property **192563**, backing field
  **262071**) remains unmapped. Renewed `code.identify_decoys` found no use witness
  and reported incomplete native caller coverage: **indeterminate**, not proven
  global non-use. Send an empty semantic notification under the omission rule,
  including when a title is worn. The mapped outfit title and actor title option
  remain populated from actual character state.
- The existing aura/ornament notification and extra-look omissions remain: empty
  `lym` / `hif`, and only owned identifier plus mapped look for `lxc`. `Outfit` tag 4
  and cosmetic inventory tags 1/2 were not populated by the retired list builders
  and remain unset. No new semantic fields or bindings are introduced.

Outfits retain worn-first/store order, the current-outfit copy, UTC-formatted stored
creation date at `LastModified`, body/idle fallback, plain RGB palette values,
recursive appearance looks, hidden slots and garment templates. Zero source aura,
title and ornament values remain absent optional fields; optional skin values are
not invented. Missing palette data stays absent. No lossless reproduction of the
historical ordinal/title-scalar bytes is claimed.

Cosmetics retain account unlock/collection order, effects and minimum quantity one,
with checked int32 item UIDs. Empty lists are still replacements. Storage read
errors retain each store's existing log-and-partial/empty policy; propagated source,
codec and own-send failures stop subsequent delivery. Targeted real-client wardrobe
and cosmetic acceptance and a fresh traffic capture remain pending.

## Guild-roster and startup-social construction policy

The **17 November 2026** migration constructs `jgu`, `hou`, `kqg` and `koj` directly,
with no raw projection/fallback. The **11-08-2026** export supplies the `GuildMember`
bindings for achievement points, history (weekly/total guildatons), mood smiley,
haven-bag sharing and notes. Present-empty history/note/status wrappers are retained.
The historical guild-member tag **3** constant **3** remains indeterminate and is
omitted. Tag **4** (`MoodSmileyId`) has no roster source and stays at the protobuf
default. Note tag 3 stays unset too.

Direct rosters also correct the legacy writer's social-field assignments using the
reviewed `SocialMember` contract: actual **account at tag 4**, **gender at tag 9**,
**join date at tag 10**, and actual offline/roleplay/fight **state at tag 3**.
This deliberately does not reproduce the reversed date/account or offline-as-gender
bytes. Incremental guild updates/cards remain separate legacy consumers.

Community tag **1** remains unmapped (property **193247**, backing **263124**).
Renewed classification found no business witness and incomplete caller coverage;
omit it in the request-driven notification as at entry, without inferring an
acknowledgement meaning or claiming global non-use.

Friend and acquaintance lists use the same export's semantic contracts. The
source's populated fields are supplied directly. Bound fields without a source
value are absent or default; the remaining unmapped fields stay raw-only. Targeted
real-client acceptance and a new capture remain pending.

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
