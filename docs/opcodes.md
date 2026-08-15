# Opcodes

Every message in 3.6.10.10 is a protobuf `Any` whose type URL is `type.ankama.com/` plus three
lowercase letters. Those three letters are the only name a message has: the client ships no
schema, no registry and no strings that would tell you what `lys` or `jru` means. This document is
the catalogue that was rebuilt from the outside.

See `protocol.md` for the framing itself — the varint length prefix, the `Any` envelope, and the
three root fields (1 = server push, 2 = client request, 3 = server answer).

---

## 1. How this catalogue was built

Two independent sweeps, then the cross.

**Sweep A — the code.** Every `.cs` file in the repository was scanned for three-letter opcodes:
inside `type.ankama.com/` URIs, inside `Push(...)`, `Answer(...)` and `ReadPayload(...)` calls, and
in the dispatch chain of `GameNodeProxy`. For each hit the sweep kept the file and line, the
surrounding documentation comment, and whether the server pushes it, answers with it, or reads it
from the client. **234 opcodes.**

**Sweep B — the wire.** The 242 `.pcapng` files under `Wireshark captures from real game`
(416 MiB, 31 topic folders) were reassembled at TCP level and every envelope decoded, counting
occurrences per direction and per capture file. **669 opcodes.**

Reproduce sweep B with the tool in the repository:

```
py tools/pcap.py "<capture>.pcapng"            # opcode timeline for one capture
py tools/pcap.py "<capture>.pcapng" --raw lys  # hex of those messages
```

### The four numbers

| | Count | What it means |
|---|---:|---|
| Seen on the wire | **669** | Everything the real 3.6.10.10 client and server exchange across the 242 captures. This is the size of the protocol. |
| In both sweeps | **128** | Named by the code *and* observed on the wire. The upper bound on what is implemented. |
| Captures only | **541** | The game uses it; the emulator has never heard of it. This is the roadmap, section 4. |
| Code only | **106** | Named in the code but never seen on the wire in this version. Leftovers from 3.6.4.3 and scan artefacts, section 5. |

234 = 128 + 106. 669 = 128 + 541.

### Two corrections to those numbers

Both were found while writing this document, and both matter if you use the figures.

**128 is an upper bound, not a count of working features.** Of those 128, **106** sit on a code
path a real 3.6.10.10 client can actually reach. The other **22** exist only inside branches keyed
on messages this client never sends — the 3.6.4.3 world-loading and combat handshakes. They are
listed in section 3.

**128 also undercounts by three.** The code sweep looks for opcode literals in the shapes listed
above; `iua`, `itd` and `itc` reach the wire through constants (`ChestHandler.ArrivesInBag`,
`ArrivesInChest`) and a variable passed to `Push`, so the sweep missed them. They are implemented,
they are in the captures, and they are in the tables below.

A plain `grep '"[a-z][a-z][a-z]"'` over the sources would not have fixed this. It finds 37 more
capture opcodes in the C# text, and most are not opcodes in that context: ten are the privacy drop
list (section 6), seven belong to a separate market-scanner tool under `tools/`, three are labels
in a lookup table, and `"kro"` is a syllable in the random-name generator of
`CharacterCreationHandler` that happens to collide with a real opcode the client sends 14 times.

---

## 2. What is implemented

**111 opcodes**, grouped by area: the 106 of the 128 that sit on a reachable code path, the three
the code sweep missed (`iua`, `itd`, `itc`), and two that are handled but appear in no capture
(`iuw`, `lxj`, both flagged in place). Columns:

- **Dir** — direction as measured in the captures. `S→C` server to client, `C→S` client to server.
  `S→C (f3)` means the message travels on root field 3, the answer field, echoing the request id.
- **Wire** — occurrences in the 242 captures, and how many capture files it appears in. Every count
  in this document was recomputed from the capture set with `tools/pcap.py`, so a few differ by a
  handful from older tallies; the set of 669 opcodes is the same either way.
- **Payload** — the shape, as measured. `f4` is protobuf field 4. Proto3 omits zero-valued fields,
  which is why several messages arrive with a field missing rather than set to zero.

Rows whose meaning column reads *not established* are sent or read because a capture puts them
there, with the values the capture carries. Nothing has been invented to fill them in.

### 2.1 Authentication and the welcome burst

The client reconnects to port 5555 with the ticket it was handed after picking a server. The
server answers with a fixed burst that ends in the character list. Order matters: it is replayed in
the order of the capture.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `kqz` | C→S | 10 / 9 files | Presents the single-use ticket. Binds the session to an account and a server; an unknown ticket closes the connection. | `GameNodeProxy.HandleTicketPresentation` | `f2: ticket` |
| `krt` | C→S | 7 / 6 files | Arrives together with `kqz`. Expects nothing back. | `GameNodeProxy` (empty branch) | — |
| `kra` | S→C | 10 / 9 files | Opens the burst. | `ConnectionProtocol.BuildWelcomeBurst` | empty |
| `lqu` | S→C | 735 / 91 files | Clock sync. Also sent on every map change. | `BuildWelcomeBurst`, `BuildMapClock` | `f1: 120` (sync rate), `f2: server clock, unix ms` |
| `hoy` | S→C | 9 / 8 files | Game-server hello. `f5` is deliberately absent: it appears in none of the three startup captures. | `ConnectionProtocol.BuildHoy` | `f1: 30, f2: 1, f3: 1, f6: language, f7: 200` |
| `kqu` | S→C | 11 / 9 files | Features enabled on the server, as opaque ids copied from the capture. | `BuildWelcomeBurst` | `f1: packed [3, 7, 13, 20, 23, 105, 124, 125, 126, 136, 143, 145, 150]` |
| `mgq` | S→C | 9 / 8 files | *Not established.* | `BuildWelcomeBurst` | `f1: 1, f2: 1, f3: 1` |
| `mgt` | S→C | 9 / 8 files | *Not established.* | `BuildWelcomeBurst` | `f2: {}` (empty submessage) |
| `hpd` | S→C | 9 / 8 files | *Not established.* | `BuildWelcomeBurst` | `f1: 1` |
| `krs` | S→C | 9 / 8 files | *Not established.* | `BuildWelcomeBurst` | empty |
| `mgz` | S→C | 6 / 6 files | Identifier of the content catalogue. Opaque: the client only compares it against itself. | `BuildWelcomeBurst` | `f1: 304672615` |
| `kqp` | S→C | 24 / 6 files | Sent three times in a row, with three different payloads. Meaning *not established*. | `BuildWelcomeBurst` | `f1:1, f2:1` then `f1:1` then empty |
| `kvi` | S→C | 10 / 8 files | The account's characters on the chosen server. | `BuildCharactersList` | `f1 (rep) { f1 { f2: name, f3: level, f4 { f2 { f3: sex } }, f6: look, f7: breed }, f2: characterId }` |
| `kvd` | S→C | 4 / 3 files | Closes the character list. Empty, immediately behind `kvi` in the real burst. It was not being sent, and it is the leading candidate for the dead create-character button: the client received the list and nothing saying the list was complete. | `BuildWelcomeBurst` | empty |
| `jtg` | S→C | 11 / 7 files | Catalogue of gift items on the account. Sent empty — our accounts own none. | `BuildGiftCatalogue` | `f3 (rep) { f1 { f2: name, f3 {…item…}, f6 {…description…} }, f2: id }` |
| `kqq` | C→S | 3 / 3 files | The client is going back to the character or server screen. | `GameNodeProxy` | — |
| `kqr` | S→C | 3 / 3 files | Answer to `kqq`. The client then closes the connection itself and redoes the handshake. | `GameNodeProxy.BuildKqrPayload` | `f1: session guid, f4: 1` |

`kvc` (client to server, 10 times across 9 files) and `krv` (9 across 8) appear at this point in the
captures as **client** messages. An earlier version of this emulator echoed them back at the
client; they are deliberately not in the burst.

### 2.2 Character creation and selection

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `kvz` | C→S | 2 / 2 files | Create a character. | `CharacterCreationHandler.CreateAsync` | read for name, breed, sex, look |
| `kvb` | S→C | 2 / 2 files | Result of the creation. Empty on success; `f2` carries the refusal reason. | `CharacterCreationHandler` | empty, or `f2: reason` |
| `kvk` | S→C | 8 / 2 files | A suggested character name (the dice button). | `CharacterCreationHandler.SuggestNameAsync` | `f1: name` |
| `kvw` | C→S | 2 / 2 files | Select a character. The id is checked against the session's account: the client picks it, so it cannot be trusted. | `CharacterSelectionHandler` | read for character id |
| `kvl` | C→S | 1 / 1 file | Same step straight after a successful creation — the client sends `kvl` behind `kvi` and enters the world without passing through the list. | `CharacterSelectionHandler` | read for character id |
| `kva` | S→C | 7 / 6 files | "You are now playing this character." Without it the client sits on the character screen with the hourglass up. Rebuilt from the database, never replayed. | `BuildCharacterSelectedSuccess` | `f1 { f1 { f1: details, f2: characterId } }` |
| `kpa` | C→S | 1 / 1 file | Character-list request. Dispatched, but seen once in 242 captures. | `CharacterSelectionHandler.HandleCharacterListRequest` | — |

`kqu` is **not** a character-list request in 3.6.10.10, whatever older tables say: the server
pushes it inside the welcome burst and the client never sends it.

### 2.3 World entry and the map block

World entry is not one burst. The real server sends a block, waits for the client to confirm, and
carries on, three times over:

```
client kvw  ->  block 1: character, stats, quests, almanax…   (330 messages)
client lqc  ->  block 2: the four big catalogues              (4 messages)
client kqo  ->  block 3: the map                              (29 messages)
```

Sending it all at once does not work; the client has not asked for the map yet and discards it.

The blocks are replayed from the capture, because there is no schema for 330 messages. What is
**replaced** is identity: those messages are rebuilt from the database before going out. That is
why the table below shows opcodes the server pushes being *read* by the emulator — it is reading
its own stored capture to decide what to substitute, not reading the client.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `lqc` | C→S | 77 / 56 files | "Block 1 digested." The real server waits for this before sending block 2. | `GameNodeProxy` | — |
| `jru` | S→C | 719 / 86 files | "Load this map." Sending it twice makes the client reload the world in a loop. | `BuildLoadMap`, `WorldEntry.SendMapAsync` | `f2: map id` |
| `hjk` | S→C | 7 / 6 files | Travels with `jru` on every map change. | `BuildMapDiscovered` | `f1: packed [map id]` |
| `jrh` | C→S | 690 / 86 files | "Who is on this map?" Unanswered, the map draws empty: no avatar, no NPCs, no monsters. | `GameNodeProxy` | — |
| `jss` | S→C | 692 / 87 files | The actors. `f6` is not decoration: with a zero subarea the client throws inside `MapInfoUI.SetInfoFromSubarea` and loses the map name, the coordinates and the minimap marker. Actor type comes from which field appears inside `f2.f1` — `f5` player, `f7` NPC, `f4` monster group; NPCs and groups use negative contextual ids. | `BuildMapActors` | `f2: map id, f6: subarea id, f5 (rep) { f1 { f1: cell, f2: facing }, f2 {…what it is…}, f3: contextual id }` |
| `lva` | S→C | 1071 / 88 files | "That is every actor." Empty, immediately behind `jss`. Without it the client never counts the map as loaded: it waits about two seconds, asks again with `knm`, `kno`, `kny`, and starts over. | `BuildActorsComplete` | empty |
| `kub` | S→C | 188 / 54 files | The character sheet. The container field is **not** the same for every characteristic and getting it wrong kills the whole sheet with a `NullReferenceException` in the client's own log. Which id uses which container is read off the captured `kub`, not written down. Sent twice — once with the character, once with the map; the client keeps the second. | `BuildCharacteristics`, `WorldEntry.ContainerOf` | `f2 { f1: where the next level starts, f7: where this one started, f8: experience held, f10: kamas, f11 (rep) { f1: id, <container> {…} } }` — containers: `f4 {f2: base, f3: parchments, f7: equipment}` for most, `f5 {f1: base, f5: bonus}` for 1 and 23, `f2 {f2: value}` for 29, 47 and 96 |
| `irq` | S→C | 93 / 11 files | Jobs. The id list is kept (it is game data); the captured progress is thrown away and every job goes out at level 1. | `WorldEntry.ResetJobs` | `f1 (rep) { f1: job id, f3: level, f4/f5: experience }` |
| `hms` | S→C | 8 / 6 files | The spells the character has, each at the grade its level opens. | `BuildSpellList` | `f1 (rep) { f1: grade, f3: spell id, f4: 1 }` |
| `ivx` | S→C | 68 / 27 files | The inventory, built from the database. Slot omitted when zero, because zero is the amulet. | `BuildInventory` | `f3 (rep) { f1: slot, f5 { f1: template, f2 (rep) {…value…, f11: effect}, f3: quantity, f4: uid } }` |
| `itg` | S→C | 19 / 7 files | The shortcut bars. The server sends **two**, and nothing inside the message says which is which: a slot holding a spell carries `f6`, one holding an item carries `f9`. The trailing bare `f2` is the bar type — the spell bar carries `f2: 1`, the item bar omits it (type zero). | `BuildSpellBar`, `WorldEntry.Rebuilt` | `f1 (rep) { f2: slot, f6 { f2: spell id } }, f2: 1` |

The three experience fields of `kub` are not in the order they look. `f1` is the threshold of the
**next** level, `f7` the floor of the current one, `f8` what the character actually holds, so
`f7 <= f8 <= f1`. A character that has just been created settles it: `f1` is 110 with `f7` and `f8`
absent, and 110 is exactly the threshold of level 2. Reading `f1` as the experience held and `f8`
as the threshold — the obvious guess — hands the client an experience above the target it is being
told to reach, and the bar comes up full with "next level in 0 XP" at every level.

### 2.4 Movement

The four cardinal map-change captures under `Movimiento` — one per edge — spell the whole exchange
out. Nothing here is guessed.

```
C  jrw    walk inside a map        S  jsj
C  jqi    reached the edge         S  jsq   (root field 3)
C  jqk    the map it wants         S  jsd, jru, lqu, hjk
C  kmv, jrh                        S  jss, lva
```

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `jrw` | C→S | 1038 / 121 files | Walk to a path of cells. Each step packs the facing in the bits above the cell. The map id is checked against the session and a mismatch is ignored — trusting it would let a stray message move the character anywhere. | `WorldMoveHandler.ConfirmMovementAsync` | `f1: map id, f2: packed path, each entry (facing << 12) \| cell` |
| `jsj` | S→C | 2505 / 178 files | The movement confirmed. Skipping it leaves the actor at facing zero, which is why a character walking off the left edge turned to face right as the screen faded. | `BuildActorMoved` | `f1: packed cells, f2: facing, f5: contextual id` |
| `jqi` | C→S | 834 / 114 files | "I am at the edge, may I leave?" | `WorldMoveHandler.AllowMapExitAsync` | empty |
| `jsq` | S→C (f3) | 834 / 114 files | Go ahead. Carries nothing but the echoed request id, on root field 3. Without it the client never sends `jqk` and the character stands on the border for good. | `ConnectionProtocol.Answer` | empty, id echoed |
| `jqk` | C→S | 430 / 22 files | The map it wants. Landing cell and facing are worked out from the exit side: a map is 14 cells across as a diamond, so leaving sideways moves the cell by 13 and vertically by 532 — all four measured off the captures. | `WorldMoveHandler.ChangeMapAsync` | `f2: map id` |
| `jsd` | S→C | 450 / 36 files | Take an actor off the map. Its own move to another map counts. | `BuildActorLeft` | `f2: contextual id` |

`lqn` goes out between `lqu` and `hjk` in every captured map change and is **not** sent here: its
single field is 197 on entering the world, 24 on changing map and 470 after a characteristics
reset, and no reading of those three numbers has held up. Inventing it is worse than omitting it.

### 2.5 Inventory and equipment

```
C  iuk { f1: quantity, f2: uid, f3: destination }
S  ivq, lym, hie, hii, iun, jsn, lxc, kub
```

Positions come from the captures and from a session of the real client: 0 amulet, 2–5 rings and
belt, 6 hat, 8 pet or mount, 12–14 dofus, 63 bag.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `iuk` | C→S | 52 / 19 files | Move an item to a slot or back to the bag. Position **zero is the amulet, not the bag** — proto3 drops the field, so an amulet arrives as nothing but a uid. Defaulting that to the bag is why the amulet was the one piece that could never be worn. | `EquipmentHandler.MoveAsync` | `f1: quantity, f2: uid, f3: position` |
| `ivq` | S→C | 65 / 20 files | Where an item ended up. One slot holds one item: whatever was there is evicted to the bag with its own `ivq` first. | `EquipmentHandler` | `f1: uid, f2: position` |
| `lym` | S→C | 135 / 50 files | Carries the aura in the appearance flow (`f1: aura id`, empty for none). On every equip and unequip capture it carries the constant 206; that value's meaning is *not established*. | `EquipmentHandler`, `AppearanceHandler.AuraAsync` | `f1: 206` on equip; `f1: aura id` or empty for the aura |
| `hie` | S→C | 129 / 48 files | *Not established.* Constant in every equip and unequip capture. | `EquipmentHandler` | `f1: 2` |
| `hii` | S→C | 131 / 49 files | *Not established.* Likewise. | `EquipmentHandler` | `f1: 2` |
| `iun` | S→C | 351 / 74 files | Pods. Identified by arithmetic, not by name: five points of strength moved `f3` by exactly 25 and characteristic 40 by the same 25, and five pods per point of strength is the game's own rule. | `BuildPods` | `f1: carried, f3: capacity` |
| `iuw` | C→S | **not in captures** | Destroy an item. The client removes nothing on its own; unanswered, the item stays. Observed in the emulator's own traffic log against the real client, not in the pcapng set. | `DestroyItemHandler.DestroyAsync` | `f2 { f2: uid, f3: quantity }` |
| `ium` | S→C | 26 / 14 files | An item leaves the bag. | `BuildItemGone` | `f1: uid` |
| `iua` | S→C | 82 / 26 files | An item arrives in the bag, with everything: template, effects, quantity. | `BuildItemArrived` (field 3) | `f3 { f1: bag, f5 { f1: template, f2 (rep): effects, f3: quantity, f4: uid } }` |
| `itd` | S→C | 8 / 4 files | An item arrives in the chest. | `BuildItemArrived` (field 1) | `f1 { f1: bag, f5 { … } }` |
| `itc` | S→C | 12 / 4 files | An item leaves the chest. | `BuildItemGone` | `f1: uid` |

The four transfer messages are paired from the house-chest capture, where they travel in groups of
three: `iua, itc, iun` and `itd, ium, iun`. Each group is one movement, so the arrival and the
departure of a group are the two ends of the same trip. They were crossed for a while, and the
symptom was exact: the item vanished from where it left and did not appear where it arrived until
the chest was closed and reopened.

### 2.6 Chest and lottery (haven bag interactives)

Both are clicked with the same `iwo` as a zaap, so the element id decides which one it is.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `kci` | S→C | 6 / 5 files | The chest opens. Both values constant in the capture; the 100 looks like the slot count. | `BuildStorageOpened` | `f1: 100, f3: 4` |
| `iwb` | S→C | 7 / 5 files | What is inside. Same shape as the inventory, with the bag as the position of everything — nothing is worn inside a chest. | `BuildStorageContent` | `f1 (rep) { f1: 63, f5 { … } }` |
| `kcr` | C→S | 87 / 14 files | Move an item. The **direction does not travel**: it is deduced from where the item is. `f1` arrives as -1 when the whole stack is dragged. | `ChestHandler.MoveAsync` | `f1: quantity, f2: uid` |
| `kla` | C→S | 62 / 43 files | The dialog close button. Empty, and the client waits: the window does not close until the server says so. | `GameNodeProxy` → chest or zaap | empty |
| `khd` | S→C | 41 / 33 files | The chest closed. | `BuildStorageClosed` | `f3: 11` |
| `jbs` | S→C | 2 / 2 files | The lottery machine's answer. From two captures: one with a prize in `f2`, one refused with `f3: 1`. | `BuildLotteryResult` | `f2: prize uid` or `f3: reason` |

### 2.7 Characteristics

```
C  kum { one field per characteristic }    S  iun, kub
C  kuh {}  (reset)                         S  iun, kub
```

The field number to characteristic mapping is read off the six point-distribution captures under
`Caracteristicas` — one per characteristic, five points into each:
1 intelligence, 2 chance, 3 vitality, 4 wisdom, 5 agility, 6 strength.

The value is what the player **pays in total**, not what it gains and not an increment. Four
confirmations in a row from a real client session settle it: read as increments the character
bought vitality four times over, read as totals every number lands where the player put it. That
makes the message idempotent, which is the property that matters — the panel repeats itself.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `kum` | C→S | 7 / 7 files | Spend points. A total that does not fit the character's capital is refused whole, not partially: the client computes the cost itself before asking, so a disagreement means the two sheets differ. | `CharacteristicsHandler.SpendAsync` | `f1..f6: points spent in total on that characteristic` |
| `kuh` | C→S | 2 / 2 files | Reset. The capture charges nothing for it — the kamas are identical before and after — so no fee is taken here either. | `CharacteristicsHandler.ResetAsync` | empty |

### 2.8 Spells and the shortcut bars

Spells come in pairs and the character holds one half. Measured on four real variant swaps.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `hmt` | C→S | 111 / 10 files | Swap a spell for its variant, from the panel or from the bar. | `SpellHandler.HandleVariantAsync` | `f1: wanted spell id` |
| `iuq` | S→C | 147 / 10 files | One per bar slot that held the old half — a swap capture produced two of them because the spell sat in two slots, which is how we know it is one per slot and not one per swap. Sent **before** `hng`. | `BuildShortcutChanged` | `f2 { f2: slot, f6 { f2: spell id } }, f3: bar` |
| `hng` | S→C | 123 / 11 files | The new spell and the grade the character's level opens. | `BuildSpellSwapped` | `f2: spell id, f3: grade` |
| `itz` | C→S | 38 / 6 files | Edit one slot of a shortcut bar. Also written to the database — otherwise the bar is rebuilt identically each session and anything the player places is lost on exit. | `GameNodeProxy.RememberShortcut` | `f2 { f2: slot, f6 { f2: spell id } }, f3: bar` |
| `ivk` | S→C | 40 / 7 files | The echo: the very same entry the client sent. | `GameNodeProxy` | identical to the `itz` payload |

### 2.9 Zaaps

Read from three real captures — opening the list, one long trip, and a zaapi.

```
C  iwo { f1: skill instance uid, f2: element }    S  iwn, hjj
C  hjc { f3: destination map }                    S  jsd, jru, lqu, hjk, ivf, kld
```

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `iwo` | C→S | 220 / 81 files | Clicked an interactive element. The zaap, the chest and the lottery all arrive here. | `ZaapTravelHandler.UseAsync` | `f1: skill instance uid, f2: element id` |
| `iwn` | S→C | 402 / 102 files | "That element is in use." `f2` is the **element**, not the skill instance: crossing `iwo` and `iwn` in the same capture shows the client sending both numbers and the server returning the second. Sending the first marks an element that does not exist as busy. | `BuildElementInUse` | `f1: 1, f2: element id, f4: skill, f5: who` |
| `hjj` | S→C | 12 / 11 files | The destination list. `f6` was checked against `MapPositions` on all 25 entries of the capture and matches every one. The destination you are already standing on travels without `f2`, which in proto3 is zero: going where you already are costs nothing. | `BuildZaapList` | `f2: map of the open zaap, f3 (rep) { f1: area level, f2: cost, f5: map, f6: subarea }` |
| `hjc` | C→S | 11 / 10 files | Destination chosen. | `ZaapTravelHandler.TravelAsync` | `f3: destination map` |
| `ivf` | S→C | 38 / 22 files | Kamas left after paying. | `BuildKamas` | `f1: kamas` |
| `kld` | S→C | 91 / 35 files | "Close the dialog." The client does **not** close the zaap window by itself. It appears twice in the captures with the same value — on arrival just before `jss`, and as the answer to the empty `kla` — so `f1` is a fixed reason and not something to compute. | `BuildDialogClosed` | `f1: 10` |

Travel cost is ours. The real server scales it by distance (170 to 1080 across the capture, far
destinations dearer) but the formula is in no client data file, so this one reproduces the shape
and the range without claiming to be the original.

### 2.10 Haven bag (merkasako)

*Merkasako* is the Spanish name of the haven bag, and the one this codebase uses throughout —
`MerkasakoHandler`, `Managers.Merkasako`. It is the same thing.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `jbn` | C→S | 9 / 5 files | The haven bag button, and the H key. Carries a character because you can visit somebody else's. | `MerkasakoHandler.EnterFromOutsideAsync` | `f2: whose bag` |
| `jbl` | C→S | 4 / 1 file | Change room theme from inside. | `MerkasakoHandler.ChangeThemeAsync` | `f1: theme` |
| `jbv` | C→S | 1 / 1 file | Open the furniture placement mode. | `MerkasakoHandler.OpenEditorAsync` | empty |
| `jbm` | S→C | 1 / 1 file | Placement mode acknowledged. | `MerkasakoHandler` | empty |
| `jbg` | C→S | 3 / 1 file | A slice of the room. It arrives **split** — three in a row in the capture — and each slice carries the **whole** room, not a diff. Saving each slice separately would have the first one erase what the other two carry, so they are collected and written once on close. | `MerkasakoHandler.CollectFurniture` | `f2 (rep) { f1: cell, f2: furniture, f3: orientation }` |
| `jbk` `jav` `jaw` | C→S | 1 / 1 file each | Close the placement mode. All three arrive together on accept. | `MerkasakoHandler.CloseEditorAsync` | empty |
| `jba` | S→C | 2 / 1 file | Placement mode closed. | `MerkasakoHandler` | empty |
| `jbu` | S→C | 10 / 7 files | The furniture in the room, expected behind the map. Same shape as `jbg` but on `f1` instead of `f2`. | `BuildHavenBagFurniture` | `f1 (rep) { f1: cell, f2: furniture, f3: orientation }` |
| `jaz` | S→C | 14 / 7 files | Sent with the furniture, between `jss` and `lva`. Meaning *not established*. | `MerkasakoHandler` | empty |

### 2.11 The appearance window (cosmetics)

This window works on a **draft**, and respecting that is what makes it behave. While the player is
fiddling the server sends **only** `lxc`, which is the panel's own preview and nobody else sees it.
`jsn` — the message that redraws the character on the map — does not go out until Save. Checked
across the fourteen captures that end in a save.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `lyk` | C→S | 29 / 27 files | Open the window. No answer of its own. | `AppearanceHandler.OpenAsync` | empty |
| `lyy` | C→S | 29 / 27 files | Ask for the window state. | `AppearanceHandler.SendStateAsync` | `f1: character uuid` |
| `lxo` | S→C (f3) | 29 / 27 files | The window state. `f7` is the same uuid the preview `lxc` carries and `f12` its same look — that is how the panel knows the reply is its own. | `BuildAppearanceState` | `f1: 1, f3 { f3: when, f5: breed, f7: preview uuid, f8: 3, f10: title, f11: level, f12: look, f15: -1, f16: ornament, f17 (rep) { f1: slot, f2 { f2: garment } } }` |
| `lys` | C→S | 2994 / 20 files | Wear a garment and let the server pick the slot. Accepts a variant, which is what the living objects use to imitate one garment or another. | `AppearanceHandler.WearAsync` | `f1: item, f2: variant` |
| `lwz` | S→C (f3) | 2994 / 20 files | The slot the server chose. | `AppearanceHandler` | `f1: 1, f3: slot` |
| `lyf` | C→S | 98 / 19 files | Put in, or clear, a named slot. No variant. With no item it empties the slot. | `AppearanceHandler.AssignAsync` | `f2: item, f3: slot` |
| `lyj` | S→C (f3) | 98 / 19 files | Acknowledgement. | `AppearanceHandler` | `f3: 1` |
| `lxg` | C→S | 6 / 2 files | Show or hide what is in a slot. With `f3` set, that slot's skin disappears from the next `lxc`; without it, it comes back. The garment is not removed, it stops being drawn. | `AppearanceHandler.ToggleAsync` | `f1: slot, f3: 1` to hide |
| `lxk` | S→C (f3) | 6 / 2 files | Acknowledgement. | `AppearanceHandler` | `f1: 1` |
| `lxw` | C→S | 5 / 2 files | The aura. | `AppearanceHandler.AuraAsync` | `f2: aura` |
| `lwx` | S→C (f3) | 5 / 2 files | Acknowledgement. | `AppearanceHandler` | `f1: 1` |
| `lxc` | S→C | 3234 / 57 files | "Your look changed" — the preview. `f1` is a uuid, identical across a session and different per character. Where the client learns it has not been found; here it is derived from the character id so that it is at least consistent. | `BuildLookChanged` | `f1: uuid, f2: the new look` |
| `jsn` | S→C | 728 / 130 files | "This actor changed" — the whole actor block, with cell, id and the new look. This is what the client actually redraws from; `lxc` alone updated the inventory doll and left the figure on the map wearing the old mount. | `BuildActorRefreshed` | `f1 { the actor block }` |

`lxs` — the Save button — is in section 2.12, because saving commits the title and the ornament
together with the look.

### 2.12 Titles and ornaments

Same draft mechanic. Note the fields do **not** line up: the title travels in `f1` of `lze`, the
ornament in `f2` of `lwm`. Both accept an **empty** message, which is how "none" is expressed —
not a zero inside.

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `lze` | C→S | 559 / 4 files | Pick a title. Touches the draft only. | `WardrobeHandler.ChooseTitleAsync` | `f1: title`, or empty for none |
| `lxa` | S→C (f3) | 559 / 4 files | Acknowledgement. | `WardrobeHandler` | `f2: 1` |
| `lwm` | C→S | 172 / 4 files | Pick an ornament. | `WardrobeHandler.ChooseOrnamentAsync` | `f2: ornament`, or empty for none |
| `lyv` | S→C (f3) | 172 / 4 files | Acknowledgement. | `WardrobeHandler` | `f1: 1` |
| `lxs` | C→S | 27 / 22 files | **Save.** This is where the draft becomes what is worn, and where the look reaches the rest of the map. | `WardrobeHandler.SaveAsync` | empty |
| `lyu` | S→C (f3) | 27 / 22 files | Save acknowledged. | `WardrobeHandler` | `f1: 1` |
| `hid` | S→C | 2 / 2 files | The title now worn. **Empty** means none — not a zero inside. | `BuildTitleUpdated` | `f1: title`, or empty |
| `hif` | S→C | 4 / 4 files | The ornament now worn, same rule. | `BuildOrnamentUpdated` | `f1: ornament`, or empty |
| `hhy` | S→C | 8 / 6 files | What the account **owns**. The client already carries the whole catalogue; anything not in this list is drawn greyed out. Sent once, on world entry. A freshly created character's `hhy` arrives with zero bytes. | `BuildTitlesOwned` | `f1: packed titles, f2: packed ornaments` |
| `lyt` | S→C | 7 / 6 files | The saved wardrobe outfits. **Mandatory**: without it the cosmetics window plays its sound and never draws, dying in `CosmeticUi.DisplayOutfit` on a null reference because it has no outfit to show. Seen in the client's own `Player.log`. | `BuildOutfits` | `f1 (rep): each saved outfit, f2: the one worn` |

### 2.13 Chat

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `ktm` | C→S | 16 / 5 files | A line the player typed. | `GameNodeProxy` | `f2: text, f3: channel` |
| `kti` | S→C | 1940 / 143 files | The line coming back. With one player on the server there is nobody else to hand it to, which is also what the real server does with your own lines — and what makes them appear in the window. Channels, from the capture that walks through all of them: 0 general (omitted, being zero), 1 team, 2 guild, 3 alliance, 4 party, 5 trade, 6 recruitment, and 9, 11, 16, 18, 19 for the rest. | `BuildChatLine` | `f3: timestamp, f4: who, f5: character, f6: account, f7: text, f8: {}, f9: channel` |

A private message is a different message, `ktb`, and it carries its recipient. Not implemented.

### 2.14 Heartbeat

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `kqo` | C→S | 2890 / 235 files | The heartbeat, every five seconds for as long as the client is in the world. The most frequent client message in the whole capture set and present in 235 of the 242 files. | `GameNodeProxy` | — |
| `kqy` | S→C | 2881 / 234 files | The answer, and nothing else. Twenty-four in a row 5,000 ms apart in the tutorial capture, each followed by exactly one `kqy`. The frame comes out byte for byte like the captured one: `1d 0a 1b 0a 19 0a 13 type.ankama.com/kqy 12 02 08 01`. Note it travels on root field 1, not the answer field. | `BuildHeartbeatAnswer` | `f1: 1` |

Answering `kqo` with the map block is what made the client reload the world in a loop: the block
carries `jru`, and `jru` means "load this map". The block now goes out on the first `kqo` of an
entry (or on `lqc`, whichever comes first) and the heartbeat gets its own answer from then on.

### 2.15 NPC dialogue — partial

| Opcode | Dir | Wire | What it does | Handler | Payload |
|---|---|---|---|---|---|
| `lxh` | C→S | 3 / 1 file | Leave a dialogue. The only NPC message the 3.6.10.10 client is observed to send. | `NpcHandler.HandleLeaveDialogRequest` | empty |
| `lxj` | S→C | **not in captures** | What the emulator answers `lxh` with. Unverified — this opcode appears in none of the 242 captures. | `NpcHandler` | `f1: 5` |

`ilr` and `kjl` are dispatched as client requests by `NpcHandler` but the captures show both going
**server to client** only (2 and 1 occurrences). Those branches never fire. See section 3.

---

## 3. Implemented, but on a dead code path

These 22 are inside the 128, yet the branch that emits or consumes them is keyed on a message the
3.6.10.10 client never sends. Most belong to the 3.6.4.3 world-loading handshake (`kkn`, `lpj`,
`hmv`, `ibt`, `loy`) or to the old combat dispatch. Several are builders in
`TransitionPacketsBuilder` that nothing calls at all — of its 48 public builders, **34 are never
invoked** from anywhere in the solution. The fourteen that are called are all reached from
`GameNodeProxy` or `FightHandler`, and every one of those call sites is itself a 3.6.4.3 branch.

| Opcode | Wire | Where it is in the code | Why it is dead |
|---|---|---|---|
| `jto` | S→C 7324 / 23 files | `GameNodeProxy` character-list branch | The **server** sends `jto`; the client never does. Only `kpa` reaches that branch. |
| `jwe` | S→C 9463 / 23 files | `FightHandler` dispatch | Server-sent. Used as a client trigger. |
| `jxw` | S→C 4933 / 23 files | `FightHandler` dispatch | Same. |
| `jya` | S→C 3962 / 20 files | `FightHandler` emits it | Only reachable from the fight dispatch above. |
| `jyj` | S→C 178 / 21 files | `FightHandler` | Same. |
| `jyg` | S→C 36 / 22 files | `FightHandler` | Same. |
| `krp` | S→C 14 / 14 files | `FightHandler` | Same. |
| `joq` | S→C 1 / 1 file | `FightHandler`, `MapChangeHandler` | Both callers are 3.6.4.3 paths. |
| `jwm` | C→S 7 / 1 file | `FightHandler` builds it as a server message | Direction inverted: the client sends it. |
| `hnk` | S→C 243 / 9 files | `TransitionPacketsBuilder.BuildHnkMessage` | Never called; the `hmv` branch uses a raw payload and `hmv` never arrives. |
| `ilc` | S→C 5 / 3 files | `BuildIlcMessage` | Only called from the `kkn` branch. |
| `isf` | S→C 7 / 2 files | `StatsHandler.BuildIsfPacket` | Reachable only from `krc` and from `isi`, neither of which arrives. |
| `hhf` | S→C 1 / 1 file | `InventoryHandler` | Only reachable from `isi`, which never arrives. |
| `kku` | S→C 2 / 2 files | `InventoryHandler` | Same. |
| `luy` | C→S 1 / 1 file | `InventoryHandler`, `BuildLuyMessage` | Built as a server message; the client sends it. Builder never called. |
| `kdx` | C→S 10 / 1 file | `BuildKdxMessage` | Never called. Built as a server message; the client sends it. |
| `izh` | C→S 8 / 7 files | `BuildIzhMessage` | Never called. Built as a server message; the client sends it. |
| `izu` | S→C 2 / 2 files | `BuildIzuMessage` | Never called. |
| `koj` | S→C 28 / 7 files | `BuildKojMessage` | Never called. `koj` is, however, live in the drop list — see section 6. |
| `itn` | S→C 17 / 7 files | `GameNodeProxy` answers it with `itw` | Server-sent; used as a client trigger. `itw` appears in no capture. |
| `ilr` | S→C 2 / 1 file | `NpcHandler` | Server-sent; used as a client trigger. |
| `kjl` | S→C 1 / 1 file | `NpcHandler` | Same. |

Four of these — `luy`, `kdx`, `izh`, `jwm` — are messages the **client actually sends** that the
emulator has back to front, building them as server pushes. They are cheap wins: the direction is
already known, only the handling is missing.

---

## 4. What is missing: the 541

Grouped by where they show up. The capture folder names are descriptions of what was being done,
so an opcode confined to one folder belongs to that feature. **307 of the 541 appear in exactly one
capture folder**; the other 234 are spread across several.

### 4.1 The ones you would notice first

Not the loudest, but the most widespread — present in a large share of all 242 files, which means
they belong to ordinary sessions rather than to one feature.

| Opcode | Dir | Wire | Files | What is known |
|---|---|---|---:|---|
| `kmu` | S→C | 399 | 90 | Present in 25 of the 31 folders. Nothing established. |
| `kmv` | C→S | 727 | 88 | Arrives with `jrh` on every map load and expects nothing back — the emulator already ignores it silently. |
| `iom` | S→C | 283 | 67 | Declared in `OpcodeRegistry` as `MapInformationsRequest`, but the captures show the **server** sending it. The name is a leftover guess. |
| `kmb` | S→C | 172 | 62 | Goes out with `jsn` when a look is saved. Measured: 49 `kmb` while mounted and none otherwise. Not sent; look changes still reach the map through `jsn`. |
| `lqg` `lqt` | S→C | 77, 76 | 56, 55 | Travel with `lqf` (C→S, 76 in 55 files). All three together are present in 54 files across 19 folders. |
| `lqn` | S→C | 213 | 53 | Between `lqu` and `hjk` on every map change. Its single field is 197 / 24 / 470 in three different situations and has not been explained; deliberately not sent. |
| `hpm` | S→C | 103 | 35 | Nothing established. |
| `izz` | S→C | 635 | 29 | Nothing established. |

### 4.2 Combat — the largest hole

Fights are not implemented against this client at all. `jtn`, `jwe` and `jxw` appear in **exactly
the same 23 capture files, and every one of those 23 is a recording that contains a fight** — 12
`Combate` files, Hipermago, Zurkarak (2), Steamer, the jalatós dungeon, two Sueños Infinitos, the
tax-collector attack under Gremio, two Movimiento files that end in a fight, and the tutorial.

| Opcode | Dir | Wire | Files |
|---|---|---:|---:|
| `jtn` | S→C | 14,728 | 23 |
| `jwi` | S→C | 7,324 | 23 |
| `jxm` | S→C | 6,556 | 23 |
| `jti` | C→S | 1,275 | 22 |
| `jxc` | S→C | 739 | 23 |
| `jzc` | S→C | 538 | 23 |
| `jxh` | S→C | 514 | 23 |
| `jwz` | C→S | 513 | 23 |
| `jyt` | S→C | 503 | 20 |
| `jwh` | C→S | 431 | 19 |
| `kmk` | S→C | 264 | 22 |

`jtn` alone is 14,728 messages — more than any other opcode in the entire capture set. **100
unimplemented opcodes appear in those 23 files and in none of the other 219**, so a fight is worth
that many messages on its own. The existing `FightHandler` is 3.6.4.3 code
speaking `joi`, `jos`, `jpp`, `jud`, `juc`, `jvm`, `joo` — none of which appears anywhere in the
242 captures.

### 4.3 By feature

Exclusive opcodes only: each of these appears in that capture folder and nowhere else, so the
attribution is unambiguous. `S`/`C` marks the direction; the number is total occurrences.

| Area | Exclusive opcodes | Messages | Busiest |
|---|---:|---:|---|
| **Guild** (`Gremio`) | 71 | 193 | `jii` 21C, `jml` 14C, `jci` 10S, `jmf` 8S, `jlx` 8C |
| **Combat** (`Combate`, beyond §4.2) | 52 | 95 | `hpu` 6C, `lux` 5C, `ltd` 5S, `kwb` 5C |
| **Party finder** (`Busqueda grupo`) | 30 | 85 | `jzw` 12S, `jpx` 8S, `jmu` 8C, `jpr` 5C |
| **Interactives** (`Interactivos varios`) | 20 | 51 | `kgl` 12S, `kgp` 9S, `kcu` 4S, `hhk` 4S |
| **Equipment sets** (`Conjuntos`) | 20 | 86 | `iic` 17S, `ify` 17C, `ija` 11S, `igy` 11C |
| **Infinite Dreams** (`Sueños Infinitos`) | 19 | 145 | `izg` 40S, `ixg` 14S, `iyx` 12C, `iyb` 12S |
| **Professions** (`Oficios`) | 19 | 532 | `isv` 191S, `kdb` 114S, `kcj` 114C, `irl` 30C |
| **Movement extras** (`Movimiento`) | 13 | 78 | `iej` 38S, `hir` 10C, `him` 10S, `jqt` 5S |
| **Connection extras** | 12 | 78 | `idz` 21S, `idw` 16C, `ieg` 12S, `kwd` 8C |
| **Houses** (`Casas`) | 8 | 21 | `kia` 4S, `khv` 4C, `khu` 4S |
| **Trading** (`Intercambio`) | 7 | 19 | `ket` 5S, `keq` 4S, `kfz` 3S |
| **Parties** (`Grupos`) | 7 | 8 | `ime` 2C, `imy` 1S |
| **Shop** (`Tienda`) | 6 | 30 | `jwj` 13S, `ipb` 7S, `ipa` 7C |
| **Chat channels** (`Chats`) | 6 | 6 | `mgb`, `mfx`, `mfp`, `mfo`, `mff`, `mfe` |
| **Appearance extras** | 5 | 8 | `lya` 3S, `hih` 2S |
| **Emotes** | 4 | 30 | `hov` 19C, `hoc` 7S, `hor` 2C, `hns` 2S |
| **Friends** (`Amigos`) | 3 | 3 | `lqw` 1C, `lqq` 1S, `lqe` 1S |
| **Haven bag** (`Merkasako`) | 1 | 2 | `jau` 2C |
| **Characteristics** (`Caracteristicas`) | 1 | 2 | `kts` 2C |
| **Alliances** (`Alianzas`) | 1 | 1 | `hks` 1S |
| **Dungeons** (`Mazmorras`) | 1 | 1 | `jxs` 1S |
| **Steamer** | 1 | 1 | `iuv` 1C |

These 22 areas account for all 307 exclusive opcodes; the table is the whole list, not a selection.

Reading it as a roadmap: **guild** is the biggest single untouched feature by opcode count — 71
exclusive opcodes, and 168 unimplemented opcodes appear somewhere in its 11 captures. **Combat** is
the biggest by volume. **Professions** deliver the most traffic per opcode: `isv` (191 S→C),
`kdb` (114 S→C) and `kcj` (114 C→S) are 419 messages between them, and each lives in exactly two
capture files — a small, well-bounded exchange to decode.

The **emote** group is the cheapest thing on this list: four opcodes and 30 messages in total, with
`hov` (client to server, 19 occurrences in one capture) doing most of the talking.

---

## 5. Named in the code but never seen: the 106

These appear in the sources and in **none** of the 242 captures. They split by whether the dispatch
chain still tests them, with one theme cutting across both halves.

**Twenty-nine are still dispatched.** They are tested by name in `GameNodeProxy`'s if/else chain,
so they are live branches that a 3.6.10.10 client will never trigger — dead weight that still runs
on every packet. Fifteen of them key a branch of their own, and those are the ones worth listing:

| Opcode | Handler | Note |
|---|---|---|
| `kkn` | `GameNodeProxy` → 3.6.4.3 initialisation burst | Sends `kkp`, `kkm`, `krb`, `ilc`, `joh`, `lor`, `kri`, `hmd`, `itp`. |
| `ibt` | `GameNodeProxy` → final burst | Sends `icg` ×3, `ith`, `klt`, `klp`. |
| `hmv` | `GameNodeProxy` | Sends `hnk` and the `kqm` list. |
| `lpj` | `GameNodeProxy` | Sends `lpe`. |
| `loy` | `GameNodeProxy` | Sends hardcoded `lok` and `jdj` frames. |
| `kod` | `GameNodeProxy` | The 3.6.4.3 ping. Answers `kns`. Replaced by `kqo`/`kqy`. |
| `jte` | `GameNodeProxy` | Answers with a hardcoded `jto` frame. |
| `kkr` | `MapLoadHandler` | Old map-load request. |
| `jos` `jpp` | `MapChangeHandler` | Old map change and movement confirm. Replaced by `jqk` and `jrw`. |
| `isi` | `InventoryHandler` | Old item move. Replaced by `iuk`. |
| `krc` | `StatsHandler` | Old stats upgrade. Replaced by `kum`. |
| `kqn` | `ChatHandler` | Old chat. Replaced by `ktm`. |
| `jxx` | `FightHandler` | Old fight dispatch. |
| `iuw` | `DestroyItemHandler` | **Not a leftover.** Observed against the real client in the emulator's own traffic log, just not in the pcapng set. Genuinely implemented. |

The other fourteen — `igx`, `ise`, `joi`, `jqf`, `jrb`, `jtk`, `jub`, `jyk`, `jyz`, `jza`, `knx`,
`kpc`, `ksl`, `ksx` — carry no branch of their own. They ride along inside the `||` chains of the
branches above: `ise`, `jtk` and `knx` share the authentication branch, `kpc` and `ksx` the
character-list branch with `jto` and `kpa`, `ksl` the selection branch with `kvw`, `jqf` and `igx`
the `kkr` branch, and `joi`, `jrb`, `jub`, `jyk`, `jyz` and `jza` the fight branch with `jxx`.
Removing a whole branch therefore retires more than the one opcode that names it.

**Seventy-seven are never dispatched.** They are constants, builders nothing calls, protobuf
descriptors in `TypeRegistry`, and entries in the label table of `NetworkMessage.cs`. `bcy`, `bvr`
and `csm` come from `CommandHandler` (admin commands, not client protocol). `kof` is tested only in
`PcapParser`, which reads capture files and is not part of the live dispatch. `xxx` is not an opcode
at all — it is the placeholder in a documentation comment reading `type.ankama.com/xxx`.

**Everything the 3.6.4.3 combat engine speaks is here**: `joi`, `joo`, `jud`, `juc`, `jvm`, `jox`,
`joh`, `jpf`, `kkq`, `kkz`, `lsy`, `igs`, `lor`, `kkm`, `kkp`, `kri`, `krb`. Seventeen opcodes, an
entire combat implementation, all of it addressed to a protocol version this client does not speak.

---

## 6. Recognised on purpose, and deliberately not sent

Not everything the emulator knows about is meant to travel. World entry replays a real capture, and
these messages carry the recorded account's data. They are matched by opcode and dropped:

| Opcode | Wire | What it carries |
|---|---:|---|
| `kqg` | 28 | The contact list. |
| `jhe` | 7 | The guild. |
| `jhh` | 18 | The guild again — founding date, level, member count. |
| `jhk` | 2 | The guild name, spelled out. |
| `hol` | 4 | Spouse and guild. |
| `jgu` | 13 | The spouse, with its look. |
| `ihb` | 7 | Fourteen saved outfits, each with a look. |
| `koj` | 28 | Twenty Ankama accounts with id, nickname and tag: `f2 { f2: account id, f4 { f1: nickname, f2: tag }, f5: 3 }`. |
| `ife` | 9 | Alliances, by name and tag. |
| `jjs` | 10 | A player stall on the map, with the account behind it. |
| `jaa` | 5 | The same, in its own message. |
| `hhy` | 8 | Replaced, not dropped — the emulator sends the playing account's own titles and ornaments. |
| `lyt` | 7 | Replaced likewise, with the playing character's own outfit. |

`jhh` and `jhk` were travelling until recently, and the client's own `Player.log` shows the cost: a
`NullReferenceException` on each, from the same handler. It makes sense — they describe a guild
whose own message (`jhe`) is not being sent, so there is nothing for them to attach to. Dropping
them removed two of the client's six crashes and one more real name from the wire — the guild's,
spelled out in `jhk`.

`ivi` used to be in this list labelled "the inventory". It is not: 9,694 pairs of id and value, ids
from 44 to 34,352, values into the hundreds of millions. It looks like the account's statistics
counters. Mislabelling is easy here, which is the point of the next section.

---

## 7. Do not trust `datos/dofus3_mappings.json`

That file maps 93 type URLs to human names such as `AlignmentSubAreaUpdate` and
`ChatChannelsReadMessage`. It predates 3.6.10.10 and it is wrong often enough to be dangerous.

**Only 16 of its 93 opcodes appear anywhere in the 242 captures.** The other 77 — `knx`, `kof`,
`lor`, `hnp`, `knr`, `kkn`, `joi`, `jos`, `jpp`, `kri`, `krb`, `loy` and the rest — are not part of
this version's protocol at all.

Of the 16 that do appear, six carry a name that a *server* would send while the captures show the
**client** sending them, and two of those are demonstrably something else entirely:

| Opcode | The file says | The captures and the code say |
|---|---|---|
| `lxs` | `AlignmentSubAreaUpdate` | Client to server, 27 times in 22 files. It is the **Save button of the appearance window**. An alignment update would be a server push. |
| `kqo` | `ChatChannelsReadMessage` | Client to server, 2,890 times in 235 of 242 files, exactly every five seconds, each one answered by `kqy` and nothing else. It is the **heartbeat**. |
| `koj` | `HavenBagStatusMessage` | Server to client. Carries twenty Ankama account ids with nicknames and tags. Nothing to do with haven bags. |
| `izh` | `AlmanaxDateMessage` | Client to server, 8 times. |
| `kdx` | `AccountCapabilitiesMessage` | Client to server, 10 times. |
| `luy` | `JobDescriptionMessage` | Client to server, once. |
| `imd` | `InventoryWeightMessage` | Client to server, once. A weight report would be a server push. |

The same stale names have leaked into the `case` labels of
`Jondo.Unity.Launcher/Protocol/NetworkMessage.cs`, which is why `lxs` is described there as
`AlignmentSubAreaUpdate (PvP and sub-area alignment)`. Those labels are logging decoration; they
are not evidence, and this document does not use them.

The general rule: **a name in that file is a hypothesis from a previous protocol version.** Check
it against a capture before believing it.

---

## 8. Checking an opcode yourself

1. **Direction and volume.** Run `py tools/pcap.py` over the relevant capture folder and count.
   Direction settles more than it looks: a message the client sends cannot be a status push,
   whatever it is named.
2. **Which captures.** An opcode confined to one topic folder belongs to that feature. An opcode in
   90 of the 242 files belongs to the session, not to a feature.
3. **Correlate.** Take one capture, look at what arrives immediately before and after. `iwn`'s `f2`
   was identified purely by lining it up against the `iwo` that provoked it in the same capture.
4. **Test against the client.** `py tools/cliente_falso.py` talks to the emulator without opening
   the game; the real client's `Player.log` names the message and the handler when it throws, which
   is how the `kub` container rule and the `lyt` requirement were both found.
5. **Write down what you measured, not what you inferred.** If a field's meaning does not survive
   three captures, leave it out. `lqn` is not sent by this emulator for exactly that reason.
