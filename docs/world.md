# The world

What the emulator does once the client is in the game: maps, walking, zaaps, the haven bag,
characters, monsters, combat and the things you can click on a map.

Every figure below was measured from `bases/world.db`, from the files in `datos/`, or from the
`.pcapng` captures of real 3.6.10.10 sessions. Where something is guessed, or where the code and
the measurement disagree, it says so.

The capture folder had **250** files when these numbers were taken, and it grows as more sessions
are recorded, so anything counted over the whole corpus drifts upward. Re-measure before quoting a
capture count.

Two conventions used throughout:

* **Opcode direction.** `C` is client to server, `S` is server to client. A message on root field 1
  is a server push, field 2 a client request, field 3 a server answer. See `protocol.md`.
* **Cells.** A map is 560 cells, 14 across and 40 rows down, numbered left to right and top to
  bottom.

---

## 1. Maps and movement

### 1.1 What the emulator knows about a map

| Source | Rows | What it gives |
|---|---|---|
| `world.db` → `MapPositions` | 15,360 | id, x, y, sub-area, indoor/outdoor flag, name |
| `world.db` → `MapTemplates` | 15,360 | the raw record; only `m_flags` is read |
| `world.db` → `MapScrolls` | 17,353 | the four border neighbours, 69,410 of them non-zero |
| `world.db` → `SubAreaTemplates` | 562 | sub-area records; all 562 carry a `level` |
| `datos/map_walkable_cells.json` | 17,211 maps | cells you can stand on in roleplay |
| `datos/map_fight_cells.json` | 17,222 maps | cells you can stand on in a fight, and opaque cells |
| `world.db` → `MapMobs` | 38,744 groups | monster groups, on 12,907 distinct maps |

`MapManager.Initialize()` (`Jondo.Unity.Launcher/MapManager.cs`) loads the first five rows of that
table at startup. The last two are read elsewhere: `MapMobs` by `Managers/MobSpawnManager.cs` and
`SubAreaTemplates` by `Managers/Interactives.cs`.

Of the 15,360 maps in `MapPositions`, **15,355 have walkable-cell data**. The walkable file covers
17,211 map ids, so **1,856 of them are not in `MapPositions` at all** — the file comes from the
client's own map bundles, which ship more maps than the world tables list. Those extra ids are
harmless: nothing can route a player to a map `MapPositions` does not describe (see 1.4).

The two cell files are not redundant, and the reason is worth knowing:

* `map_walkable_cells.json` **trims the map borders on purpose**, so that monster groups are never
  placed on the edge. Total 2,987,103 cells, 173.6 per map on average, from 1 to 290.
* `map_fight_cells.json` keeps the whole map and adds the `los` flag. Total 4,645,830 cells, 269.8
  per map, plus opaque-cell lists for 15,142 maps. Combat and the arrival cell of a map change both
  use this one, because the border is exactly where somebody arriving from the next map lands.

### 1.2 Walking inside a map

Measured in the four `Movimiento/movimiento a mapa …` captures, one per direction. Handled by
`Handlers/WorldMoveHandler.cs`.

```
C  jrw { f1: map id, f2: the path, packed, each step = facing << 12 | cell }
S  jsj { f1: the cells, packed, f2: final facing, f5: whose movement }
```

The server writes down the last cell and the facing, saves the character, and echoes the path back.

It echoes the client's own keyframes rather than expanding the straight runs into every cell, which
is what the real server does. The client has already walked them, so it has nothing left to
interpolate. Answering at all matters: without the `jsj`, a character walking off the left edge
turned to face right in the instant before the screen faded, because facing 0 is what an actor falls
back to when nothing has told the client otherwise.

If the `jrw` names a map other than the one the session believes the character is on, it is logged
and ignored. Trusting it would let a stray message move the character anywhere.

### 1.3 Leaving a map

```
C  jqi {}                    reached the edge, may I leave?
S  jsq {}                    yes — on root field 3, the answer field
C  jqk { f2: the map it wants }
S  jsd { f2: who }           take the actor off the old map
S  jru { f2: the map }       load this one
S  lqu, hjk
C  jrh                       the map is loaded, who is on it?
S  jss, lva
```

`jsq` carries nothing but the request id. Without it the client never sends the `jqk` and the
character stands on the border for good.

`lqn` travels between `lqu` and `hjk` in the captures and is **not** sent here: its single field is
a number nobody has explained (197 on entering the world, 24 on a map change, 470 after a
characteristics reset), and inventing it is worse than leaving it out.

### 1.4 Which map is on the other side

The map id inside `jqk` is a **guess, not an instruction**. The client works it out by arithmetic on
the id it is standing on, and that only holds where the neighbour happens to be the next id along.
A real session standing at (5, −17) walking off the bottom asked for map 191105029, which does not
exist; the map below is 188745734. Echoing the guess back left the character pinned to the border,
and every `jrw` after that started from the same cell.

So the destination is resolved in three steps (`WorldMoveHandler.Neighbour`):

1. **The guess**, but only if it names a map that exists *and* sits on the square next door in the
   direction being walked. It is the only one of the three that can tell which of several maps
   sharing a coordinate the player means.
2. **`MapScrolls`**, the game's own neighbour list.
3. **The coordinates.** Where several maps share a square, outdoor beats indoor, the current
   sub-area beats another, and after that the id nearest the one being left — map ids are handed out
   in blocks, so a neighbour is usually numerically close.

And never to a map missing from `MapPositions`: the client cannot load one either, so the character
would sit on the border while the database claims it is somewhere that does not exist.

Which border is being crossed comes from the last cell of the `jrw`: column 13 right, column 0 left,
row ≤ 1 up, row ≥ 38 down. A corner belongs to two at once and sideways wins, because getting a side
wrong lands the character one row off and getting a vertical wrong lands it on the far side of the
map.

The arrival cell is fixed arithmetic, read off the captures:

| Direction | Cell change | Facing | Captured example |
|---|---|---|---|
| right | −13 | 0 | 405 → 392 |
| left | +13 | 4 | 322 → 335 |
| up | +532 | 6 | 23 → 555 |
| down | −532 | 2 | 542 → 10 |

If that cell cannot be stood on, the nearest one that can is used instead.

### 1.5 The neighbour table is much fuller, and much worse, than the code thinks

`WorldMoveHandler` and `DungeonManager` both say `MapScrolls` is filled in for 2,223 maps out of
15,360, and `WorldMoveHandler` adds that those hold 3,463 borders. **Those comments are out of
date.** The database that ships in
`datos/world.zip` holds 17,353 rows and 69,410 borders with a destination, extracted from the
client's own map bundles.

Measured over the 15,360 maps that are in `MapPositions`:

| | Count |
|---|---|
| Maps with a `MapScrolls` row | 15,360 — all of them |
| Maps with four written neighbours | 15,359 |
| Borders written from those maps | 61,438 |
| …whose destination is a map `MapPositions` describes | 24,750 (40.3 %) |
| Maps where **every** written neighbour is an id that does not exist | 7,519 (7,518 of them with four written) |
| Maps where every written neighbour does exist | 3,956 |

The unknown ids are not near-misses; they include values like 4, 6, 8 and 10. The bundles reference
map ids this version's world tables do not carry.

This has a consequence the code does not account for. Step 2 returns whatever is written without
checking that it exists, and every map but one has all four written, so **step 3 is now unreachable**
for every map in `MapPositions` bar 218105858, which is the single row with only two neighbours
written and can still fall through to the coordinates going left or up. `ChangeMapAsync` then refuses
to move to a map missing from the world data, which is correct on its own terms but leaves the
character on the border. In practice a map change works when the client's guess passes step 1, and
step 1 is now carrying the feature.

The harvested values are still in there and still right: standing at (5, −17), map 191105028, the
bottom neighbour reads 188745734, which is the value observed from the real server and not the one
the client guesses. The extractor never overwrites a value already present, so where a real server
has been watched, that value wins.

### 1.6 Autopilot

Autopilot needs no server support and gets none. It is the client walking by itself, and on the wire
it is the same cycle repeated. Measured on `Movimiento/ruta muy larga con autopilotaje-1.pcapng`:

```
C  186 jrw   186 jqi   172 jqk   185 jrh   185 kmv
S  186 jsq   185 jru   185 lqu   185 jss   152 jsd   212 jsj   257 lva
```

That is one long route: 185 maps loaded, 172 of them reached by walking off a border. The rest of
the cycle is exactly what a single manual map change sends, message for message.

The capture is not only that, and it is worth saying so: the same route also carries `izz`, `lvb`,
`kti`, `iwo`/`iwn`, `ivf` and a dozen other opcodes, because the player used interactive elements
along the way. None of them belongs to the walking itself. What matters is that autopilot adds
nothing: it works in the emulator for the same reason single map changes do, and it fails on the
same maps, since every hop goes through the resolution in 1.4.

### 1.7 Limits

* No pathfinding is validated. Whatever cells the `jrw` claims are accepted, so a modified client can
  walk through walls.
* The nearest-walkable-cell search measures distance as `(row, column)` Euclidean, not isometric.
  It is good enough to unstick an arrival, not to be exact.
* `MapChangeHandler.cs` still holds a full map-change and movement path on `jos` / `joi` / `jpp`.
  Those three opcodes appear **0 times** in the 250 captures: they belong to 3.6.4.3 and this client
  never sends them. That file is dead weight — except that it is also the only place that starts a
  fight (see 6).

---

## 2. Zaaps

`Managers/Interactives.cs`, `Handlers/ZaapTravelHandler.cs`, `datos/waypoints.json`,
`datos/interactive_elements.json`, `datos/zaap_overrides.json`.

### 2.1 Where they come from

`datos/waypoints.json` is the client's own `WaypointsDataRoot`, extracted by
`tools/extract_interactivos.py`. It holds **62 zaaps**, one per map and one per sub-area, of which
**47 are marked activated**.

Knowing a map has a zaap is not enough: the emulator has to say *which element on the map* it is, so
the client can put the click target on the right cell. The element ids come from
`interactive_elements.json` (9,840 maps, 46,309 elements), which is `m_interactionId` out of the
client's map bundles — the same number the real server puts in the `jss`.

The zaap is recognised by its drawing, and there are two. Cross-checking the 62 zaap maps against
their elements:

| Drawing (`gfx`) | Zaap maps carrying it | Of those, activated | Maps in the world carrying it |
|---|---|---|---|
| 301199 | 46 | 46 | 106 |
| 74685 | 15 | 0 | 27 |
| neither | 1 | 1 | — |

That split is exact and it is not a matter of old and new zones: **301199 is the drawing of the 46
activated waypoints and 74685 is the drawing of the 15 that are not**. The code treats the two as
interchangeable models of the same thing, and the captures suggest they are not — the single 74685
element that appears in a `jss` is declared with a different element type (7.2). It costs nothing
today, because the 15 are never offered as destinations, but the drawing is not the "newer zaap" the
comments call it.

### 2.2 The one that had to be written down by hand

The map without a recognisable zaap is **115083777, the Temple of Alliances at (13, 35)**. It has
four elements and none of them carries a zaap drawing:

| Element | `gfx` | Cell | Why it is not the zaap |
|---|---|---|---|
| 481519 | 41723 | 186 | the alliance-gem door. It is clicked in a real capture — the one recorded trying to enter without the gem — and the server answers `iwn` and then nothing: no `hjj`, no destination list |
| 481520 | 41724 | 123 | twin of the one above |
| 488131 | 13493 | 118 | that drawing appears on 139 maps, almost none of them with a zaap |
| 530219 | 31992 | 254 | that drawing appears on 5 maps, none of the others with a zaap |

The first two drawings each appear on exactly one map in the whole game, so neither can be
identified by comparison. The capture rules out 481519, which leaves **481520**. That is the whole
content of `datos/zaap_overrides.json`, and the file carries the reasoning inside it so the next
person does not have to redo it.

The alternative that was tried first was to declare **every** element on such a map as a zaap, so the
player could not get stuck. It worked, and it was a lie: it turned the temple doors into zaaps. Each
element has its own purpose and not all of them are travel.

### 2.3 Using one

Read from three captures — opening the list, travelling between two cities, and a zaapi:

```
C  iwo { f1: skill instance uid, f2: element }     clicked the zaap
S  iwn { f1: 1, f2: element, f4: skill, f5: who }  the element is in use
S  hjj { f2: this map, f3 (repeated): a destination }

C  hjc { f3: destination map }
S  jsd, jru, lqu, hjk        leave the old map, load the new one
S  ivf { f1: kamas left }
S  kld                       close the window — the client will not close it by itself
```

Each destination is `{ f1: zone level, f2: cost, f5: map, f6: sub-area }`. Over the twelve
destination lists in the 250 captures — 349 entries in all — every `f5` names a map `MapPositions`
describes, and `f6` is that map's sub-area in 327 of them. The destination you are already standing
on travels without `f2`, which is proto3 for zero.

**All 47 activated zaaps are offered**, every time. There is no discovery in this emulator: the
character has them all. A destination is only dropped if the map is missing from `MapPositions` or if
you could not leave it again — none of the 47 falls into either case today.

### 2.4 What travel costs

Invented, and marked as such in the code. The real server charges by distance and the far ones are
dearer, but the formula is not in any client data. All that can be read off the captures is the
spread: across the twelve destination lists the price runs from 10 to 1,600 kamas, and the two full
city lists alone run from 40 to 1,530. A zaapi is not priced this way at all — all 24 destinations
of the Bonta zaapi list cost 20 flat. The emulator uses:

```
cost = clamp(10 * (|Δx| + |Δy|), 10, 1000)
```

Same shape, same order of magnitude, not the same numbers. Kamas are checked before the trip and
subtracted after.

You arrive on the zaap's own cell, or the nearest walkable one if that cell cannot be stood on.

### 2.5 Zaapis, and other limits

* **Zaapis are not implemented.** They are not in `WaypointsDataRoot`, and the emulator only declares
  elements carrying one of the two zaap drawings, so a zaapi on a city map is not clickable at all.
  The exchange is identical to a zaap's — the Bonta zaapi capture was one of the three used to work
  out §2.3 — so what is missing is the destination table, not the protocol.
* **The guild-hall zaap is not declared either.** Drawing 37493, on three maps, is declared 114 / 16
  in the captures — the zaap pair exactly — and none of its maps is in `WaypointsDataRoot`, so the
  emulator ignores it. That is one more drawing that could be recognised without guessing anything.
* Nothing is charged for the zaap you are standing on, and nothing is charged for failing.
* No zaap is ever "discovered" or saved; there is nothing per-character to store.

---

## 3. The haven bag (*merkasako*)

The Spanish servers call the haven bag the **merkasako**, and so does the code
(`Managers/Merkasako.cs`, `Managers/HavenBagStore.cs`, `Handlers/MerkasakoHandler.cs`). It is the
same thing: the private space you can enter from anywhere.

### 3.1 The decors

Every haven-bag map lives in **sub-area 851**, and 46 of the 51 have no coordinates: they read
(0, 0) in `MapPositions`, because they are not in the world. The remaining five do carry
coordinates — 162792470, 162794518, 162795532, 162796564 and 162796566 — so sub-area 851 is the
test that identifies a haven-bag map, not the coordinates.

| | Count |
|---|---|
| Maps in sub-area 851 | 51 |
| Themes in `datos/havenbag.json` (client's `HavenBagThemes`) | 48 |
| Themes whose map really is in sub-area 851, and are therefore usable | 47 |
| Usable themes whose map has a zaap element | 40 |
| Sub-area 851 maps with a chest (`gfx` 12367) | 47 |
| Sub-area 851 maps with the lottery machine (`gfx` 51031) | 47 |
| Furniture entries in the catalogue | 4,083, in 43 families |

Theme 8 points at map 162793476, which is not in sub-area 851, so it is dropped at load time: a theme
whose map is not in the world would send the player nowhere.

Seven of the 47 usable themes (3, 9, 26, 35, 36, 39 and 50) carry no element with a zaap drawing, so
no zaap is declared there. From those decors the only way out is to change theme.

The lottery machine's drawing exists on 48 maps in the entire game, 47 of them haven bags. The chest
drawing is the same one houses use, and appears on 258 maps.

### 3.2 Going in, and changing decor

```
C  jbn { f2: whose bag }      the button and the H key
C  jbl { f1: theme }          change decor from inside
S  jsd, jru, lqu, hjk         same four as any map change
C  jrh
S  jss, jbu (furniture), jaz (permissions), lva
```

`jbn` carries a character because you can visit somebody else's. With one player on the server it
always resolves to your own, at whatever decor you left it on — stored in `HavenBag(CharacterId,
ThemeId)`.

You arrive on the decor's zaap cell, or the nearest walkable one if that cell cannot be stood on —
the same rule as any zaap trip. Permissions (`jaz`) go out empty: there is nobody to invite.

Leaving is a zaap trip like any other. The decors are not in the client's zaap table — they are
places you travel *from*, not *to* — so `Merkasako.ZaapOf` recognises the element by its drawing
without demanding a waypoint entry.

### 3.3 Furniture

```
C  jbv           open the placement mode
S  jbm
C  jbg { f2 (repeated): { f1: cell, f2: furniture, f3: rotation } }
C  jbk / jav / jaw            close
S  jbu (the room as it now stands), jba
```

The `jbg` arrives **split**: three in a row in the capture, carrying 40, 40 and 29 pieces. Together
they are the **whole room** and not a list of differences — 109 pieces, which is exactly what the
server's own `jbu` sends back afterwards. So the pieces are collected while the editor is open and
written in one go on close. Treating each `jbg` as the room and replacing on arrival would have left
only the last 29.

Saving replaces the room for that character *and that theme*: each decor has its own layout and its
own cells, so changing theme and coming back has to give the room back as it was left. A piece that
is not in the client's catalogue is dropped, because the client could not draw it and the room would
be left with an invisible blocking hole.

Stored in `HavenBagFurniture(CharacterId, ThemeId, Cell, TypeId, Orientation)`.

### 3.4 The chest

Built from the capture of a **house** chest, and that is a problem worth stating first: the message
order is right, the two numbers are not.

```
C  iwo { f2: element }                clicked
S  iwn { f2: element, f4: 104, f5: who }
S  kci { f1: 100, f3: 4 }             the chest opens
S  iwb { f1 (repeated): what is inside }

C  kcr { f1: how many, f2: item uid }
S  iua + itc + iun    taking out       S  itd + ium + iun    putting in
C  kla    S  khd                       close
```

The haven-bag chest does not answer with those numbers. Clicking the one inside a haven bag gets
`iwn { f4: 184 }` and `kci { f1: 2147483647, f3: 19 }`; a house chest gets 104 and `{100, 4}`, a
guild chest the same `{100, 4}`, the bank `{2147483647, 16}` and the bin `{100, 17}`. The emulator
sends the house-chest numbers wherever a chest is declared, including inside the bag. Nothing here
has been tested against the real client, so if the haven-bag chest misbehaves this is the first
thing to change. See 7.2 for the matching mistake in the `jss` declaration.

The direction of the move is **not in the `kcr`** — the client sends the same message both ways. It
is worked out from where the item currently is: the inventory has it, so it goes in; the chest has
it, so it comes out. `f1` is the quantity and arrives as −1 when the whole stack is dragged, which
the capture shows twice.

Which of the four item messages is which was read off the capture, where they arrive in threes:
`iua, itc, iun` and `itd, ium, iun`. Each group is one move, so the arrival and the departure of a
group are the two ends of the same trip — `itc` leaves the chest and `iua` arrives in the bag; `ium`
leaves the bag and `itd` arrives in the chest. Having those crossed is what made an item vanish from
where it left and not appear where it went until the chest was closed and reopened.

An item is in one place or the other, never both: `HavenBagChest` and `CharacterItems` are updated in
one transaction, uid, quantity and effects included, so an item stored and retrieved comes back
identical.

### 3.5 The lottery

The machine next to the chest. In the real game it is once a day; here it has **no limit**.

```
C  iwo { f2: element }
S  iwn { f2: element, f4: 184, f5: who }
S  jbs { f2: prize uid }        with a prize
S  iua                          the prize into the bag
S  iun                          weight
```

`jbs { f3: 1 }` is the refusal the real server sent for "you already used it today". It is never sent
here.

What comes out is a real equipment template picked at random from the 2,153 items of types 1, 9, 10,
11, 16 and 17 (rings, cloak, hat, belt, boots, amulet), given effects no real item has: +3 AP
(effect 111), +3 MP (128), 400–700 of a primary, 1,000–2,500 vitality. One or two of the impossible
ones plus two or three characteristics. It is signed with effect 988 — the client's "Crafted by: #4"
— so it displays as forge-magic work rather than as an anonymous item. Uids start at 950,000,000 so
they cannot collide with anything in the database.

### 3.6 No monsters inside

`MobSpawnManager.GenerateDynamicMobsForMap` returns an empty list for any haven-bag map, and
`MapMobs` holds **0 rows** for the 51 maps of sub-area 851. Both halves are needed: written groups
are returned before the generator ever runs, so an empty table alone would not be enough, and the
generator alone would not be either.

This was a real bug, not a precaution. The generator used to return a fixed list of piou ids for any
map in the world, so pious from Astrub spawned at the foot of the Frigost clepsydra tower — and
inside the haven bag.

---

## 4. Characters

### 4.1 Creation

`Handlers/CharacterCreationHandler.cs`, `DatabaseManager.CreateCharacter`.

```
C  kvz { f1 { f1: name, f2: head, f3: colours, f5: 26, f7: breed } }
S  kvb                 EMPTY — that is how yes is said
S  kvi                 the character list again, with the new one in it
```

That is the whole message, field for field, in both recorded creations. `f5` is 26 in both and
nothing explains it. The handler also reads **`f4` as the sex**, and no capture shows an `f4` at all:
two creations were recorded and neither needed the field, so the sex is the one part of this message
that is unconfirmed.

The same `kvb` carrying a reason means no: 1 refused, 2 name taken, 3 the character limit (that last
one from the capture of a failed creation). Colours arrive as six signed varints and −1 means
"whatever the breed brings"; the client sends all six as −1 when the palette is untouched, which is
what both captures show.

A new character gets:

| | Value | Why |
|---|---|---|
| Map | 154010884, Incarnam | the pre-tutorial starting point |
| Cell | 315, adjusted only if the map data marks it unwalkable | |
| Level | 1 | |
| Kamas | 0 | tutorial rewards must be earned |
| Each of the six characteristics | 0 | no scrolls or prior progression |
| Equipment | none | tutorial rewards must be earned |
| Quests / achievements | none | captured-account progress is not replayed |

The server used to create a boosted Astrub test character (one million kamas, 101 in every primary
statistic and a worn Adventurer set).  That seed is intentionally gone.  Existing characters are
not changed; this policy applies only to characters created after this version.  The Incarnam
tutorial itself still needs its own measured NPC/quest implementation before it can grant its
official items and rewards.

`kvk` is the random-name button: the client asks and expects the same message back with a name
inside. The generator follows the client's own naming rule 1,
`^([A-Z][a-z]+(\-[a-zA-Z][a-z]*){0,2})$`. Without an answer the dice button did nothing.

### 4.2 Characteristics

`Handlers/CharacteristicsHandler.cs`.

```
C  kum { one field per characteristic }
S  iun { f1: carried, f3: capacity }
S  kub                                the sheet again with the new numbers

C  kuh {}     the reset button
S  iun, kub
```

Field order came from the six `Caracteristicas/distribuir 5 puntos en …` captures, one per
characteristic: 1 intelligence, 2 chance, 3 vitality, 4 wisdom, 5 agility, 6 strength.

The number in each field is what the player **pays**, not what the characteristic gains: spreading
five points into every characteristic sends `{1:5, 2:5, 3:5, 4:15, 5:5, 6:5}` and the points left
drop by forty. Wisdom is the fifteen — it costs three points each.

What the captures could not settle, because the recorded character had just reset its sheet, is
whether those are increments or totals. A session of the real client settled it: four confirmations
in a row read as increments would mean the player bought vitality four times over, and read as
totals each one is simply the distribution as it stands. So the field is a **target**, and sending
the same message twice does nothing the second time — which matters, because the panel does repeat
itself.

Capital is computed, never stored: `5 × (level − 1)`. That way a character that levels up gets its
points without anything having to remember to hand them over. A request that asks for more than the
capital is refused whole, not partially: the client works the cost out itself first, so a total that
does not fit means the two sides disagree about the sheet, and taking half the points would make
that worse.

Carrying capacity is `1000 + 5 × strength`, confirmed by a capture where five points of strength
moved both the pods characteristic and the `iun` capacity by exactly twenty-five. What is being
carried always goes out as zero: nothing here weighs the inventory.

Reset is free. The capture shows the same kamas before and after.

### 4.3 Spells

`Managers/SpellTable.cs`, `Managers/SpellChoices.cs`, `Handlers/SpellHandler.cs`,
`datos/spell_variants.json`.

Spells come in **pairs** of a base and a variant, and a character carries one of each pair, not both.
`spell_variants.json` has 431 entries; one is dropped because the client itself marks a member with
`[!]` in its name (not in the game). That leaves **430 pairs**:

* 22 for each of the 19 breeds
* 12 common ones, filed under breed 19 — weapon mastery, the parchment summons and the like

So a character has at most **34 spells**, not 44. This is what settled it: in the capture a level-154
character received 36 entries in its `hms`, and they break down as 22 of its breed's 44 — exactly one
half of each pair — plus 9 common pairs and 5 spells that are in no pair at all. Sending both halves
is what left the spell bar empty.

A pair opens when the level reaches the first grade of either of its spells, read from `SpellLevels`
(34,823 rows over 17,291 distinct spells). The variant demands a higher level than the base in 427 of
the 430 pairs — in the other three, all of them common, both halves open at the same level — so until
you reach it the base travels whatever was chosen. A pair that opens no grade does not travel at all,
which is why a level 50 panel is shorter than a level 200 one.

The choice itself is the only part that is not client data, so it lives in `world.db`
(`CharacterSpellChoices`, `CharacterSpellBar`) and survives a restart.

```
C  hmt { f1: the spell wanted }
S  iuq        one per bar slot that held the old half, with the new one in it
S  hng        the new spell and the grade its level opens
```

One `iuq` per **slot**, not per change: a capture where the old spell sat in two bar slots produced
two of them.

### 4.4 Inventory and equipment

The inventory travels as `ivx`, built from `CharacterItems` (`ConnectionProtocol.BuildInventory`):

```
ivx: f3 (repeated) { f1: slot,
                     f5 { f1: template,
                          f2 (repeated) { <value>, f11: effect },
                          f3: quantity, f4: uid } }
```

Slots 0 to 15 are worn — amulet, weapon, two rings, belt, boots, hat, cloak, pet, the six dofus and
the shield — and 63 is the bag. Slot 0 is omitted by proto3 because zero is the amulet.

```
C  iuk { f1: how many, f2: uid, f3: destination slot }
S  ivq { f1: uid, f2: where it went }
S  lym { f1: 206 }, hie { f1: 2 }, hii { f1: 2 }
S  iun                                               worn items still weigh
```

`hie` and `hii` really are constant: 993 and 995 of them in the captures and every one carries 2.
`lym` is not. Of its 999, 971 are empty — proto3 zero — 22 carry 206, three carry 22 and three carry
171. The emulator always sends 206, which is the value the capture it was copied from happened to
have.

A missing `f3` means slot 0, not the bag. Defaulting it to the bag answered "back in the bag" every
time and made the amulet the one piece that could never be put on. One item per slot: whatever was
there goes back to the bag with its own `ivq` before the new one goes in.

Worn items feed the sheet. Their effects go into field 7 of each characteristic entry of the `kub`,
which is the field the client shows as coming from equipment. Set bonuses are included:
`Managers/ItemSets.cs` reads `datos/item_sets.json` and looks the bonus up by how many pieces of a
set are worn at once. Without them the sheet came out short by a fixed amount everywhere.

> Two class comments are stale here. `Managers/Equipment.cs` says set bonuses are not applied and
> that `item_sets.json` does not exist; the file ships and `Equipment` calls `ItemSets.BonusesFor`.
> `Handlers/EquipmentHandler.cs` says moving an item does not change the sheet; it does, now that the
> inventory comes from the database.

### 4.5 Experience

`datos/character_xp.json` is the client's `CharacterXpMappings`: **1,889 levels**, from 0 at level 1
to 5,555,424,000 at level 200. Level 2 is 110 and level 3 is 650, which match the end-of-fight
capture of a level-3 character exactly (floor 650, next 1,500).

No level cap is enforced. The table goes to 1,889 because the client's data does; the game's own
limit is not applied.

### 4.6 What entering the world actually does

Worth knowing before reading anything above too literally: the world entry is **replayed from a real
capture** (`Network/WorldEntry.cs`, `datos/world_etapa*.bin`), in three blocks the client
acknowledges one at a time. What is rebuilt from the database is the identity — `kva`, `kub`
(the sheet), `hms` (the spells), `ivx` (the inventory), `jru` (the map). Everything else still
describes the recorded account. Messages that would leak real names are dropped outright, and
`tools/leak.py` checks that none reach the wire.

---

## 5. Monsters

`Managers/MobSpawnManager.cs`, `Managers/Archimonsters.cs`, `world.db → Monsters, MonsterTemplates,
MapMobs`.

### 5.1 What ships

| | Count |
|---|---|
| Monsters | 5,134 |
| Written groups (`MapMobs`) | 38,744 |
| Maps with at least one written group | 12,907 |
| Maps with none | 2,453 |
| Sub-areas with at least one written group | 440 of the 532 in `MapPositions` |

Group sizes are spread evenly from 1 to 8, about 4,850 groups of each size.

### 5.2 Maps with no written groups

Those 2,453 maps get a generated set: 2 to 4 groups, 1 to 8 monsters each, on cells at least two
steps clear of anything unwalkable and away from the borders.

**Which** monsters is the part that was got wrong first, and the fix is the point of this section.
The generator used to return a fixed list — piou ids 491, 492, 493, 463 and the 234x — for any map in
the world. That put Astrub pious at the foot of the Frigost clepsydra tower and inside the haven bag.

Now the pool is **sampled from the map's own sub-area**: the distinct monsters appearing in the
written groups of the other maps of that sub-area. With 12,907 maps carrying written groups across
440 sub-areas, there is almost always something to sample. When there is nothing, **nobody spawns**,
which is better than spawning the wrong thing. The result is cached per sub-area.

### 5.3 Grades are clamped to 5

The grade that travels to the client is a small number. Over the 5,278 monster entries in the
captures it is 1 to 5 in all but 19, which carry a 6, and — this is the rule that actually holds —
it never once exceeds the number of grades the monster itself has. The data has monsters with far
more grades than the client is ever shown:

| Grades per monster | Monsters |
|---|---|
| 1 | 163 |
| 2 | 13 |
| 3 | 10 |
| **5** | **4,098** |
| 6 | 479 |
| 7 | 53 |
| 8 | 79 |
| 9 | 43 |
| 10 | 169 |
| 11 | 26 |
| 20 | 1 |

Picking a grade freely sent numbers the client would not resolve. It takes that badly *in silence*:
the group is drawn, but hovering over it shows nothing and the W key skips it. That is why only one
or two of a map's four groups ever showed their information. The clamp is applied in both places —
when written groups are read and when new ones are generated.

Clamping at 5 is stricter than the captures require, since grade 6 does travel. What it buys is the
rule that does hold: after the clamp, none of the 174,400 members written in `MapMobs` names a grade
its monster does not have, and the generator draws from `min(grades, 5)` so it cannot either.

### 5.4 Archmonsters

A monster is an archmonster when it is somebody else's `correspondingMiniBossId`. There are **306**
of them and all 306 declare the sub-areas they belong to.

The shipped data spreads them far too thickly:

* **15,442 of the 38,744 groups (39.9 %)** hold at least one
* up to **8** in a single group
* **5,802 maps** hold more than one

`Archimonsters.Thin` applies four rules as the groups are read — at most one per group, at most one
per map, one group in ten, and one of each anywhere in the world. Simulating it over the shipped
database gives **298 groups keeping an archmonster, 0.8 %**, and 298 of the 306 standing somewhere —
one each, no repeats. The one-of-each rule is the hard ceiling: the world can never hold more than
the 306 that exist.

The draw is derived from the group's id, not from `Random`, so a map looks the same after a restart.
Nothing is rewritten: the database keeps its 38,744 groups and they are thinned as they are read. An
archmonster that loses its place is **demoted, not deleted** — swapped for the ordinary monster it is
the rare version of — so the group keeps its size and its level.

### 5.5 On the map

Monster groups travel inside the `jss` (see 7). The shape is not the obvious one and getting it wrong
kept maps empty:

```
f4 { f1: 1
     f2 { f1 (repeated): underling { f1: id, f2: level, f3: look, f4: grade }
          f2:            leader    { f1: id, f2: level,           f4: grade } }
     f5: -1 }
f3 { f2: 3, f3: bones }        the group's look, NEXT to f4, not inside it
```

The group is **one** message, not one per monster. The leader appears once and without a look of its
own, because its look is the group's — that is the sprite drawn on the cell. Checked against nine
groups across the combat and movement captures; the count always came out as one leader plus however
many underlings. Sending one `f2` per monster directly under `f4` puts a varint where the client's
generated parser expects a submessage, and it throws away the entire `jss`: no monsters, no NPCs, and
no player either.

Two details that are easy to reverse: the level goes in `f2` and the grade in `f4` — 1 to 5 as this
emulator sends it, see 5.3 — and the group closes with `f5 = -1`.

### 5.6 Limits

* Groups do not move, and there is no aggression radius.
* Respawn is one group at a time, only after a fight is won, only on the map the fight came from.
* `MobSpawnManager.GetMobAtCell` treats a group as touched if it is within ±1 or ±14 of the target
  cell — a rough proximity check, not the real trigger.

---

## 6. Combat

**Read this section before trusting the combat code.** The engine exists and the protocol does not
match this client.

### 6.1 What is implemented

`Jondo.Unity.World/Fights/` (771 lines), `Jondo.Unity.World/Maps/MapGeometry.cs` (295 lines) and
`Handlers/FightHandler.cs` (2,324 lines):

* `FightInstance` — placement / ongoing / ended, two teams, placement cells, alternating turn order
  by initiative, round counter, turn and placement timers (30 s a turn), end-of-fight detection.
* `Fighter` — HP, AP, MP, initiative, the four elemental characteristics, power, critical, the five
  resistances, per-spell damage buffs with expiry.
* `DamageCalculator` — `base × (100 + stat + power) / 100 + flat`, then flat resistance, then
  percentage resistance, minimum 1.
* `MonsterAI` — picks the best spell it can pay for, moves into range, flees below 30 % HP, respects
  line of sight. There is deliberately **no fallback spell**: an invented one with range 1–6 was the
  real reason monsters never moved, since they always had the target in reach from where they stood.
* `MapGeometry` — the isometric grid, and the correct one. A cell has exactly **four** neighbours, so
  combat distance is `|dx| + |dy|`. The eight-neighbour version that was there first made monsters
  walk twice their MP, move diagonally, and score a real distance of 10 as 6.
* Rewards — experience is each monster's own `gradeXp`, loot is rolled against the real drop tables,
  kamas are `10 + 5 × level` per monster, and a level-up hands out 5 characteristic points.

The character's fight statistics are taken from the same source as the sheet the client is shown,
equipment included. They used to come from a formula over base vitality only, so the server believed
the character had 305 HP while the client displayed 514, and the character died "in the background"
with a full health bar on screen.

### 6.2 What does not work

The fight code speaks the **3.6.4.3 protocol**. A binary scan of all 250 captures for the literal
`type.ankama.com/<opcode>` string, over every opcode the combat path still builds or reads:

| Opcode still in the code | Occurrences in 250 captures |
|---|---|
| `jxx`, `jyk`, `jyz`, `jza`, `jwb`, `jub`, `jrb` | 0 |
| `jwf`, `krh`, `jtx`, `jvm`, `jut`, `jyf`, `jud`, `juc` | 0 |
| `kkp`, `kkm`, `kkq`, `jpf`, `joh`, `kkr`, `jpv`, `krb`, `lor`, `bvr` | 0 |
| `joi`, `jos`, `jpp`, `joo` | 0 |

Not one. Most of them are `FightHandler`'s own; the last row and a few others are the map-change and
transition path (`Handlers/MapChangeHandler.cs`, `Handlers/MapLoadHandler.cs`,
`Handlers/StatsHandler.cs`, `Network/GameNodeProxy.cs`, `Network/TransitionPacketsBuilder.cs`). They
are all the previous version of the protocol.

And the two the fight code reads as client requests travel the other way in reality. Measured on
`Combate/combate contra poutch nivel 50…pcapng`:

```
S->C  jtn 141   jwe 100   jxm 90   jwi 60   jto 60   jxw 31   jya 9
C->S  jti 27    jwh 7     jwz 4    jrw 5
```

`jwe` and `jxw` are **server pushes** (9,463 and 4,933 times across all captures, never from the
client). `FightHandler` waits for them as the client's turn-ready acknowledgement and pass-turn
button. The real client sends `jti`, `jwh` and `jwz`, which nothing here reads.

The two entry points into a fight are dead for the same reason:

* `MapChangeHandler.HandleMovementRequest` starts one when a `joi` path ends on a mob's cell. This
  client does not send `joi` — the 3.6.10.10 walk is `jrw`, and `WorldMoveHandler` has no collision
  check.
* `FightHandler.HandleFightOptionToggleRequest` starts one on `hoy`. In the captures `hoy` only ever
  goes server to client, ten times: seven in server- and character-selection captures and three when
  reconnecting to a fight already under way. The client never sends it, so the trigger never fires.

So: **a fight cannot start with the 3.6.10.10 client.** Monsters are drawn on the map, they carry
their real levels and grades and their tooltips work, and walking onto them does nothing.
`Managers/DungeonManager.cs` says the same thing in its own class comment — it loads 187 dungeons,
763 rooms and 159 entrance-and-exit maps and is called by nothing, waiting for combat to move to this
version of the protocol.

The root `README.md` opens by advertising "a functional PvM combat engine". Further down it is
franker: its combat section is headed "STILL MIGRATING FROM v 3.6.4.3 - CURRENTLY DISABLED FOR
SAFETY". The second statement is the true one. Against a 3.6.4.3 client the engine may well work;
against the client this emulator targets it cannot start.

### 6.3 Also unverified, even on its own terms

* Arena selection (`MapManager.ResolveArenaMapId`) pairs a roleplay map to an arena by trying id
  offsets +4, +6, +2, +8… and falling back to a deterministic pick. Verified for Astrub city (+6) and
  the tutorial maps (+4). There is no single rule for the whole game.
* Placement cells fall back to two hardcoded eight-cell lists when the arena's own walkable set does
  not contain them.
* Experience is handed out raw: no adjustment for the level gap between group and character, and no
  split across a team.
* The `f3.f9` experience block of the end-of-fight message is sent, but the fields around it were
  deduced from three captures at levels 1, 2 and 3 and several are constant 1 with no explanation.

---

## 7. Interactive elements

An interactive element is anything on a map you can click: a zaap, a door, a chest. The client
already knows where each one is and how it is drawn — that is in the map data. What it needs from the
server is which ones exist, with what number, and what skill they offer.

### 7.1 How they are declared

Inside the `jss`, after the actors and the sub-area, which is where the real capture puts them:

```
f11 { f1: 1, f4 { f1: skill instance uid, f2: skill }, f5: element, f6: type }
f15 { f1: state, f2: cell, f3: element }
```

Both are needed: `f11` says what exists and what can be done with it, `f15` says where it is and what
state it is in.

The element number is not invented — it is the `m_interactionId` from the client's own map data, and
that holds everywhere: all 1,851 `f15` entries in the 250 captures name an element that
`interactive_elements.json` lists for that same map. The cell agrees too, but only where the state
field is present: it matches in all 973 of those entries and in none of the 878 where state is
absent, where the server's cell sits a dozen or so away from the one in the map data. Nothing here
explains that, and the emulator only ever sends the state-present form.

The skill-instance uid is what the client sends back when it uses the element. The real server hands
out numbers with no visible pattern; here it is derived as `(element % 900000) + 10000` so it is
stable across sessions and nothing has to be stored.

### 7.2 What the emulator declares

Scanning the 695 `jss` messages in the captures (3,163 `f11` entries, 1,851 `f15` entries) gives the
`(skill, type)` pairs the real server uses. The right way to read them is by **drawing**, not by
pair, because an element id is not unique — 540419, for one, is four different elements on four
maps. Matched that way, one of the emulator's three declarations is right, one is right for half the
maps it is used on, and one is wrong:

| What the emulator declares | Pair | What the captures say for that drawing |
|---|---|---|
| Zaap, drawing 301199 | 114 / 16 | the same pair, 29 times. Drawing 37493, a guild-hall zaap the emulator does not recognise, gets it too, 6 times |
| Zaap, drawing 74685 | 114 / 16 | **114 / 359**, three times, on map 54162249. Type 16 is not attested for this drawing anywhere |
| Lottery machine, drawing 51031 | 184 / −1 | the same pair, 9 times |
| Haven-bag chest, drawing 12367 | 104 / 85 | **184 / −1**, nine times — the lottery's pair — and 105 / 85 twice on a house chest. Never 104 / 85, which in the captures belongs to the guild-hall chest, drawing 46581, 3 times |

Two corrections follow from that table. The chest inside the bag is declared with the
house-and-guild family of numbers and should not be; see 3.4 for the same mistake in the `iwn` and
`kci` that answer the click. And the 15 waypoints drawn with 74685 — none of them activated, see
2.1 — are given a zaap's element type when the one capture that shows one gives 359.

Skill 184 is a generic "use". It appears with many other types (−1 five hundred and thirty-six times,
316 seventy-three times, 232 twenty-three times, and a long tail), so the skill alone says nothing
about what an element is. Giving the lottery type 85 instead of −1 made the client call it "Chest"
and offer "Open", which is the machine next to it, not the machine itself.

`f15` state is always sent as 1. In the captures it is 1 in 973 entries and absent — proto3 zero — in
878.

### 7.3 Why nothing else is declared

`interactive_elements.json` holds 46,309 elements over 9,840 maps, and the emulator declares three
kinds. The reason is not laziness: **the element type is not in the client's data.** The client data
gives the id, the cell and the drawing; the type and the skill are the server's to supply. Declaring
a door without knowing what it does gets nowhere — the client would show a use option that leads
nothing to happen.

The three that are declared were identified by their drawing, which is the one property the client
data does carry. The lottery machine's pair and the ordinary zaap's were then confirmed against real
`jss` messages. The chest's was not, and it is wrong; the second zaap drawing's was not either, and
it is wrong too (7.2).

### 7.4 Limits

* Doors, workshops, resources, bins and every other clickable element are invisible to the server.
* Element state is always 1. Nothing here can show a chest as open or a door as locked.
* A map with a zaap whose element cannot be located declares none at all. Putting it on a made-up
  cell would leave the player clicking where there is nothing.

---

## 8. Summary of what is and is not there

| System | State |
|---|---|
| Map loading, actors, sub-area | Works. `jrh` → `jss` + `lva`, all measured |
| Walking, map change, autopilot | Works while the client's own guess is right. The written neighbour table is 60 % unusable and shadows the coordinate fallback (1.5) |
| Zaaps | Works for the 47 activated destinations. Cost formula invented. The 15 non-activated waypoints are declared with the wrong element type (2.1, 7.2) |
| Zaapis | Not implemented |
| Haven bag: entry, decor, furniture, chest, lottery | Works. The chest is declared and answered with the house-chest skill and type, not the pair the captures show for a haven-bag chest (3.4, 7.2) |
| Character creation, characteristics, spells, inventory, equipment | Works |
| Monsters on the map | Works: drawn, correct levels and grades, tooltips |
| Fights | **Cannot start.** The code speaks the previous protocol version |
| Dungeons | Data loaded (187 / 763 rooms), nothing calls it |
| Interactive elements other than zaap, chest and lottery | Not declared |
| NPCs | 2 spawns in the database |
| Other players | None. The emulator serves one character at a time |
