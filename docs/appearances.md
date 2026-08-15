# Appearances

How a character is drawn, and how the item-to-look table was rebuilt from the wire.

This is the most heavily measured part of the emulator, and the part with no public reference
anywhere. Dofus does **not** ship the item-to-look table in its client data. The server sends it.
So it was measured, one garment at a time, against real captures.

Everything below can be checked. Code paths are named, data files are named, and every figure was
recomputed from `datos/`, from `dofus3_data/` or from the 242 captures while this document was
written. Where the measurement is weak, it says so.

---

## 1. What the client knows and what it does not

The client's own item dump (`dofus3_data/items.json`, 66,323 references) gives every item an id, a
name, a level and a **type id**. Twelve item types carry `categoryId = 5` in
`dofus3_data/item_types.json`, and those twelve are exactly the appearance families:

| type id | client name | items |
|---:|---|---:|
| 113 | Living object | 61 |
| 199 | Costume | 92 |
| 246 | Ceremonial hat | 464 |
| 247 | Ceremonial cape | 357 |
| 248 | Ceremonial shield | 524 |
| 249 | Ceremonial pet | 242 |
| 250 | Ceremonial petsmount | 151 |
| 251 | Ceremonial weapon | 194 |
| 252 | Miscellaneous ceremonial item | 49 |
| 299 | Shoulder pad | 121 |
| 300 | Wings | 44 |
| 324 | Ceremonial mount | 121 |
| | **total** | **2,420** |

That table is `datos/cosmetics.json`, built by `tools/completar_cosmeticos.py` from the client dump.
It holds type, level and appearance id per item.

What it does **not** hold is the number the renderer actually needs: the **skin** the garment adds
to the character, or the **bones** a pet or a mount replaces. Those never appear in the client data.
They arrive over TCP 5555 in the look block, and the only way to learn them is to make the server
say them.

"Ceremonial" is the client's own English word for these items. This document keeps it, and keeps
"petsmount" for type 250 — also the client's word — for the item that is a mount and a pet at once.

---

## 2. The discovery that made a full sweep possible

**The appearance window returns the look of a garment you do not own.**

That is the whole reason a catalogue-wide sweep exists. Proof, from the hats capture:

- One `lyk` (open window) and one `lyy` → one `lxo` (window state). A single window session.
- **468** `lys` requests inside it, covering **465** distinct item ids: all 464 catalogued
  ceremonial hats, plus id `15742`, which the server answers for although it is not in the
  catalogue.
- **Zero** `lxs`. Nothing was ever saved. The character walked away with the same look it had.

Nothing in that session tells the server what the account owns — there is no message that does —
and the server answered all 468 requests anyway, including one for an id that is not even in the
catalogue. It answers `lys` for any item id it recognises. That turns "what does item N look like?"
into a request you can issue 2,994 times.

Across the 242 captures the appearance window produced **2,994** `lys` and exactly **2,994** `lwz`
replies. Every request was answered.

---

## 3. The window works on a draft

This is the single most important behaviour to copy, and the easiest to get wrong.

While the window is open the server replies **only with `lxc`** — the panel preview. Nobody else on
the map sees anything. Only `lxs` (the Save button) turns the draft into the real look and
broadcasts it.

| direction | message | meaning | server replies |
|---|---|---|---|
| C → S | `lyk` | open the window | nothing of its own |
| C → S | `lyy { f1: uuid }` | ask for the window state | `lxo` |
| C → S | `lys { f1: item, f2: variant }` | wear it, server picks the slot | `lxc` + `lwz { f1: 1, f3: slot }` |
| C → S | `lyf { f2: item, f3: slot }` | wear it in a named slot | `lxc` + `lyj { f3: 1 }` |
| C → S | `lyf { f3: slot }` | empty that slot | `lxc` + `lyj { f3: 1 }` |
| C → S | `lxg { f1: slot, f3: 1 }` | hide that slot | `lxc` + `lxk { f1: 1 }` |
| C → S | `lxg { f1: slot }` | show it again | `lxc` + `lxk { f1: 1 }` |
| C → S | `lxs` (empty) | **save** | `jsn`, `kmb`, `lxc` pushes, then `lyu { f1: 1 }` |
| C → S | `lze { f1: title }` | pick a title (empty = none) | `lxa { f2: 1 }` |
| C → S | `lwm { f2: ornament }` | pick an ornament (empty = none) | `lyv { f1: 1 }` |
| C → S | `lxw { f2: aura }` | pick an aura (empty = none) | `lym { f1: aura }` push + `lwx { f1: 1 }` |

Handlers: `Jondo.Unity.Launcher/Handlers/AppearanceHandler.cs` and
`Jondo.Unity.Launcher/Handlers/WardrobeHandler.cs`.

Two ways to put a garment on, and they are not interchangeable. `lys` lets the **server** decide the
slot and hands it back in the `lwz`, and it carries a variant — which living objects need. `lyf`
names the slot itself and has no variant. The title and the ornament sit in the same draft and are
committed by the same `lxs`; that is why `AppearanceHandler.SaveAsync` just calls
`WardrobeHandler.SaveAsync`.

Traced on a real save (hat on, save, cape off, save), the server's stream reads:

```
lxo(f3)  ...  lxc(f1)  lwz(f3)            <- lys, draft only
lym hie hii jsn kmb lxc(f1)  lyu(f3)      <- lxs, the world finds out
lxc(f1)  lyj(f3)                          <- lyf, draft only
lym hie hii jsn kmb lxc(f1)  lyu(f3)      <- lxs
```

`kmb` is the look broadcast, and **no draft edit produces one**. The hat sweep issued 468 `lys` in a
single window session and broadcast nothing: its only two `kmb` sit in the bundle the server pushes
before the window is even opened, ahead of the `lxo`. Inside the window, `kmb` only ever follows an
`lxs`.

It is not exclusive to the appearance window, though. `kmb` is the general broadcast for a look that
really changed, and it also goes out on entering the world and on equipping real gear: of the 172
`kmb` in the 242 captures, 101 are in captures that contain no `lxs` at all. The half of the rule
that matters here is the draft half — 3,234 `lxc` against 172 `kmb`. If an implementation pushes
`kmb` on every draft edit, every other player in range sees the wearer flicker through every item
they try on.

Counts over the 242 captures, both directions:

| message | client | server | files |
|---|---:|---:|---:|
| `lyk` / `lyy` / `lxo` | 29 / 29 / 0 | 0 / 0 / 29 | 27 |
| `lys` / `lwz` | 2,994 / 0 | 0 / 2,994 | 20 |
| `lyf` / `lyj` | 98 / 0 | 0 / 98 | 19 |
| `lxg` / `lxk` | 6 / 0 | 0 / 6 | 2 |
| `lxs` / `lyu` | 27 / 0 | 0 / 27 | 22 |
| `lze` / `lxa` | 559 / 0 | 0 / 559 | 4 |
| `lwm` / `lyv` | 172 / 0 | 0 / 172 | 4 |
| `lxw` / `lwx` | 5 / 0 | 0 / 5 | 2 |
| `lxc` | 0 | 3,234 | 57 |
| `kmb` | 0 | 172 | 62 |
| `hhy` / `lyt` | 0 | 8 / 7 | 6 |

---

## 4. Pairing replies: there are no request ids

The client sends request id **−1** on every frame. Checked on the wings sweep: all 58 client frames
go out on root field 2 with `f2 = 0xFFFFFFFFFFFFFFFF`, and all 46 answers come back on root field 3
echoing the same −1. Server pushes use root field 1 and carry no id at all.

So a reply cannot be matched to its request by id. It has to be matched **by order**, and the server
does preserve order. Every tool here relies on that, and so does the emulator's own `RequestId`
echo. See `docs/protocol.md` for the framing.

---

## 5. The look block

One nested protobuf message, used for the character, for the mount and for every sub-entity.
Layout confirmed against the character-selection and appearance captures
(`Jondo.Unity.Launcher/Managers/BreedLookTable.cs`):

| field | contents |
|---|---|
| `f1` | colours, packed varints, colour index in the high byte |
| `f2` | `3` in 5,295 of the 5,306 look blocks decoded. The eleven exceptions carry 8, 10 or 19 and all sit on bones 1 or 2, in fight and bank captures — never on a drawn character |
| `f3` | bones id |
| `f5` | scales, packed |
| `f6` | skins, packed |
| `f7` | sub-entity, repeated: `{ f1: a look, f4: binding point }` |

`lxc` carries `{ f1: a uuid string, f2: the look }`. `kmb` carries `{ f1: the look, f2: entity id }`.

Scanning the look inside all 3,406 `lxc` and `kmb` messages in the 242 captures, only **three**
binding points ever appear, and all three hang directly off the root:

| binding point | what hangs there | occurrences |
|---:|---|---:|
| 1 | pet | 565 |
| 2 | rider | 1,319 |
| 6 | aura | 16 |

Binding point 2 is the one that flips the whole structure. **On foot the root is the character. On a
mount the root is the mount, and the character becomes the sub-entity at binding point 2.** Every
tool that reads a character's skins has to check for that first, otherwise it reads the mount's.

---

## 6. What each family actually changes

They do not all work the same way, and that is the finding that took the longest. Measured by
replaying every sweep capture through `tools/extraer_apariencias.py` and diffing the whole flattened
look between consecutive tests:

| sweep | tests | items | what moved | no change |
|---|---:|---:|---|---:|
| hats | 468 | 465 | character skin list (`f6`) | 1 |
| capes | 362 | 357 | character skin list | 1 |
| shields | 525 | 523 | character skin list | 39 |
| costumes | 93 | 92 | character skin list | 1 |
| wings | 44 | 44 | character skin list | 0 |
| shoulder pads | 133 | 121 | character skin list | 11 |
| pets | 244 | 242 | sub-entity at binding point 1: bones, sometimes scale, sometimes colours | 0 |
| petsmounts | 150 | 150 | **root** bones, sometimes scale, sometimes colours — never a skin | 0 |
| mounts | 121 | 121 | **root** bones, scale, colours **and a skin** | 0 |
| living objects | 622 | 61 | character skin list, per variant | 179 |
| weapons | 203 | 194 | **nothing at all** | 203 |

Every sweep covers its whole family: the hat sweep tried all 464 catalogued hats, the cape sweep all
357, the shoulder-pad sweep all 121, and so on. The one exception is petsmount `22257`, which the
petsmount sweep missed and an earlier single-item capture supplies.

Read that column by column:

**Skin families** — hats, capes, shields, costumes, wings, shoulder pads. They push one number into
the character's `f6`. Three of the measured ones push **two**: cape `18579` → `[1791, 3494]`, shield
`13240` → `[1730, 1791]`, costume `18525` → `[3493, 4568]`. That is why the stored value is a list
and not a scalar.

In the real game the skin **replaces** the equipped item's skin rather than adding to it. Measured
directly in the hide/show capture — the character's skin list before and after putting on a
ceremonial cape:

```
before  [110, 5312, 4992, 5209, 3637, 3652, 5045, 5042, 5109]
after   [110, 5312, 4992, 5209, 5044, 3652, 5045, 5042, 5109]
                                 ^^^^ 3637 -> 5044, in place
```

**Pets** hang a whole sub-entity off binding point 1 with their own bones, and often their own scale
and colours. They never touch the character's skins.

**Petsmounts and mounts** command the **root**, because the root is the mount. A petsmount changes
its bones (and maybe scale and colours) and stops there — of the 151 measured, **none** carries a
skin. A ceremonial mount does the same and adds its own root skin — of the 121 measured, **all 121**
carry one. That asymmetry is exactly the difference between the two types in the data.

**Weapons carry no look at all**, and this is not a failed measurement. All **218** `lxc` messages in
the weapons sweep are **byte-for-byte identical**. Nor does saving change it: a second, smaller
weapons capture presses Save after nine `lyf`, and the `kmb` that goes out carries the same skin list
as every `lxc` before it. The client draws the weapon itself from the item, and only while animating.
All the server has to remember is slot → item.

**Living objects** are the awkward family: one item imitates a different garment depending on the
**variant** chosen, so both the skin and the slot are per `(item, variant)`, not per item.

**Miscellaneous ceremonial items** (type 252, 49 items) have no look either, and the client data says
why — see the next section.

---

## 7. Slots, and the hidden effect that predicts them

Slot numbers are what the server returns in `lwz { f3 }`. They were measured, but they can also be
**derived from the client data**, which is how the whole table was cross-checked.

Effect **1179** is hidden — `showInTooltip = 0`, `hideValueInTooltip = 1` in
`dofus3_data/effects.json` — and its description template is `Compatible with: #1`. Its value is the
**type id of the real item the garment imitates**. There are 2,603 instances of it in the client's
item file, and all 2,603 sit on appearance items; nothing else in the game carries it:

| family | 1179 values | resolves to |
|---|---|---|
| ceremonial hat | 16 | Hat |
| ceremonial cape | 17 | Cloak |
| ceremonial shield | 82 | Shield |
| ceremonial pet | 18 | Pet |
| ceremonial petsmount | 121, 311 | Petsmount, Mounts |
| ceremonial mount | 121 + 311 (6 items), or 121 + 331 + 332 + 333 (115 items) | Petsmount, Mounts, Dragoturkey, Seemyool, Rhineetle |
| ceremonial weapon | one of ten weapon type ids | see below |
| miscellaneous | 1, 9, 10, 11 | Amulet, Ring, Belt, Boots |

**It is not the universal rule.** Living objects (113), costumes (199), shoulder pads (299) and
wings (300) carry no 1179 at all. Living objects use effect **973** instead, with the same meaning;
costumes, shoulder pads and wings need nothing, because their own type already maps to exactly one
slot. One ceremonial shield, id `12131`, has no effects whatsoever.

The cross-check is clean. Every one of the 194 ceremonial weapons maps its 1179 value to the slot
measured on the wire, with no collisions:

| slot | 1179 value | weapon type | items |
|---:|---:|---|---:|
| 13 | 2 | Bow | 16 |
| 14 | 3 | Wand | 16 |
| 15 | 4 | Staff | 34 |
| 16 | 5 | Dagger | 19 |
| 17 | 22 | Scythe | 4 |
| 18 | 19 | Axe | 18 |
| 19 | 271 | Lance | 5 |
| 20 | 7 | Hammer | 22 |
| 21 | 8 | Shovel | 16 |
| 22 | 6 | Sword | 44 |

And all 61 living objects map their effect-973 value to a single measured slot, identical across all
of that item's variants:

| effect 973 value | type | measured slot | living objects |
|---:|---|---:|---:|
| 1 | Amulet | 0 | 1 |
| 9 | Ring | 1 | 1 |
| 10 | Belt | 3 | 2 |
| 11 | Boots | 4 | 2 |
| 16 | Hat | 10 | 16 |
| 17 | Cloak | 9 | 15 |
| 82 | Shield | 12 | 9 |
| 199 | Costume | 23 | 6 |
| 299 | Shoulder Pad | 25 | 8 |
| 300 | Wings | 24 | 1 |

Putting both together, the complete measured slot table:

| slot | garment | mimicked type | measured from |
|---:|---|---|---|
| 0 | amulet | 1 Amulet | 1 living object |
| 1 | ring | 9 Ring | 1 living object |
| 2 | second ring | 9 Ring | 1 living object, 2 replies |
| 3 | belt | 10 Belt | 2 living objects |
| 4 | boots | 11 Boots | 2 living objects |
| 5 | mount | 121 / 311 | 151 petsmounts + 121 mounts |
| 6, 7, 8 | — | — | **never observed** |
| 9 | cape | 17 Cloak | 357 capes + 15 living objects |
| 10 | hat | 16 Hat | 464 hats + 16 living objects |
| 11 | pet | 18 Pet | 242 pets |
| 12 | shield | 82 Shield | 524 shields + 9 living objects |
| 13–22 | weapons | ten weapon types | 194 weapons |
| 23 | costume | 199 Costume | 92 costumes + 6 living objects |
| 24 | wings | 300 Wings | 44 wings + 1 living object |
| 25 | shoulder pads | 299 Shoulder Pad | 121 shoulder pads + 8 living objects |

Slot 2 is the second ring, and it barely showed up: 2 `lwz` out of 2,994, in two different captures,
both for the single ring-imitating living object and both in the same circumstance. That object
normally lands in slot 1. When the same item and variant is tried twice in a row, slot 1 is already
taken and the server puts the second copy in slot 2. Slots 6, 7 and 8 never appeared at all. Note that 8 is the *equipment* slot for a real mount or pet
(`Mounts.Slot`), which is a different numbering — do not mix the two.

`Cosmetics.SlotOf` puts the measured value first (per variant, then per item) and only falls back to
a per-type table. That order matters: a type-based guess is right for nine families and wrong for
weapons and living objects, which are precisely the two that need it.

This also explains the 49 unmeasured miscellaneous items. Their 1179 values are 1, 9, 10 and 11 —
amulet, ring, belt, boots — the four slots that are never drawn on a character. The same holds for
the six living objects that mimic those slots: they are the only six of the 61 with no skin
recorded. There is nothing to measure.

---

## 8. The measurement method

`tools/extraer_apariencias.py`. The recipe, and the two decisions that make it work.

**Recording.** With Wireshark running, open the appearance window in the real client and try
garments on one at a time in the draft. Do not save. Ownership is irrelevant.

**Reading.** Each try is one `lys { item, variant }`, answered by one `lxc` (the draft look) and one
`lwz { 1, slot }`. Requests and replies are matched **by order**, since the request id is always −1.
The script refuses to output anything if the counts do not line up — if the number of `lys` and the
number of replies disagree, order-based pairing is meaningless and it aborts rather than emit a
shifted table.

**Decision 1 — diff against the previous test, not against a fixed baseline.** The draft
*accumulates*: a hat stays on while the next hat is tried. Comparing every test against the look the
window opened with would report the hat plus everything already on. Comparing against the
immediately preceding test isolates exactly what that one garment did. It also removes the need to
know which slot is being filled.

The starting point is the last stray `lxc` before the first try — the one the server pushes when the
window opens. Without it the first garment has nothing to compare against and is lost.

**Decision 2 — do not look for skins, look for what moved.** The script flattens the whole look
(root plus sub-entities, keyed by binding point) into a path → value dictionary and diffs it. That is
what turned up the fact that pets move `sub1`, petsmounts move the root, and weapons move nothing.
Hard-coding "read `f6`" would have produced a table that was silently wrong for four families.

```
hat, cape, shield, costume, wings, shoulder pads   one skin in the character's f6
petsmount, mount                                   the bones of the root
pet                                                a sub-entity at binding point 1
living object                                      a skin, but the slot depends on the
                                                   variant and can be almost any of them
weapon                                             nothing
```

**The weak spot, stated plainly.** When two consecutive garments in the same slot share a skin, the
look does not change and the diff is empty. The script then records the value already in that slot.
It cannot tell "the same skin" from "the server sent nothing new". This affected:

| sweep | tests with an empty diff |
|---|---:|
| shields | 39 |
| living objects | 179 |
| shoulder pads | 11 |
| hats, capes, costumes | 1 each |

For the living objects, 127 of the 179 are the six items that mimic amulet, ring, belt and boots —
their 120 `(item, variant)` pairs, a few of them tried more than once — where there is genuinely
nothing to see. The shields are the least certain part of the whole table: 37 of the 39 ended up on
skin `1256`, which 39 catalogued shields now share. Overall,
43 skin values are shared by more than one item of the same type, covering 136 items.

**Running it.**

```
py tools/extraer_apariencias.py <capture.pcapng>                 show what would come out
py tools/extraer_apariencias.py <capture.pcapng> --volcado x.json write the raw analysis
py tools/extraer_apariencias.py <capture.pcapng> --guardar        merge into cosmetic_skins.json
```

`--guardar` refuses to write if any item produced two different skins across the capture. Silent
disagreement is worse than no data.

---

## 9. Coverage

Recomputed from `datos/cosmetic_skins.json` crossed with `datos/cosmetics.json`:

| type | items | measured | coverage | tables it lands in |
|---|---:|---:|---:|---|
| Living object | 61 | 61 | 100% | `variants`, `slotsVariante` |
| Costume | 92 | 92 | 100% | `skins` |
| Ceremonial hat | 464 | 464 | 100% | `skins` |
| Ceremonial cape | 357 | 357 | 100% | `skins` |
| Ceremonial shield | 524 | 524 | 100% | `skins` |
| Ceremonial pet | 242 | 242 | 100% | `pets` |
| Ceremonial petsmount | 151 | 151 | 100% | `mounts` |
| Ceremonial weapon | 194 | 194 | 100% | `slots` |
| Miscellaneous ceremonial item | 49 | 0 | 0% | — |
| Shoulder pad | 121 | 121 | 100% | `skins` |
| Wings | 44 | 44 | 100% | `skins` |
| Ceremonial mount | 121 | 121 | 100% | `mounts` |
| **total** | **2,420** | **2,371** | **98.0%** | |

The only gap is the 49 miscellaneous items, and section 7 explains why it is not really a gap.

---

## 10. `datos/cosmetic_skins.json`, field by field

Nine tables plus a comment block. Keys are item ids as strings.

| key | rows | contents |
|---|---:|---|
| `skins` | 1,603 | item → skin, or a list of skins. The garment pushes these into the character's `f6`. |
| `variants` | 55 | living object → `{ variant → skin }`. |
| `slots` | 194 | item → slot. Weapons only: all 194 share one type id, so the slot cannot come from the type. |
| `slotsVariante` | 61 | living object → `{ variant → slot }`. 543 pairs in total. |
| `pets` | 242 | item → sub-entity for binding point 1. |
| `mounts` | 272 | item → root override. 151 petsmounts + 121 mounts. |
| `auras` | 3 | aura id → bones. |
| `titles` | 476 | title ids the real server accepted. |
| `ornaments` | 166 | ornament ids the real server accepted. |

`skins` holds one entry that is not in the catalogue, id `15742`. The window answered for it, so it
was kept.

`pets` and `mounts` share a value shape:

| field | meaning | present in `pets` | present in `mounts` |
|---|---|---:|---:|
| `b` | bones | 242 | 272 |
| `s` | scale | 96 | 182 |
| `p` | root skin | 0 | 121 (all 121 ceremonial mounts, no petsmount) |
| `c` | colours, hex, or the string `portador` | 47 | 110 |

Two traps encoded in that table:

- **A missing scale means the default, not zero.** Scales travel as a packed repeated field, where a
  real zero would be written out explicitly. Absent means "leave it alone".
- **`c: "portador"`** (Spanish for *wearer*) means the sub-entity repeats the **character's own**
  colours byte for byte. It is not a palette belonging to the pet. 44 of the 242 measured pets do
  this, and `BreedLookTable.AddPets` copies the wearer's colours for them.

The mount colours are stored exactly as measured. Part of those bytes are the capture character's
own colours, so on a different character they may not be faithful. That caveat is in the file's own
comment block and it is repeated here because it is easy to trust the data more than it deserves.

`variants` has 423 `(item, variant)` skin pairs against 543 `(item, variant)` slot pairs. The
difference is exactly 120 — the six never-drawn living objects at 20 variants each.

The variant is not carried in a uid field, because the window never sends an inventory uid — it
sends the template id. `AppearanceHandler` therefore composes one: `uid = item * 1000 + variant`,
and `BreedLookTable.VarianteDe` takes it apart again. Ugly, but it keeps the variant alive through
the database round trip without a schema change.

---

## 11. Titles and ornaments

Two separate things shown in the same window. The title is the text under the name; the ornament is
the frame around it. One of each, or none.

```
C -> S  lze { f1: title }      empty = none      S -> C  lxa { f2: 1 }
C -> S  lwm { f2: ornament }   empty = none      S -> C  lyv { f1: 1 }
C -> S  lxs                                      S -> C  hid, hif, jsn, lxc  +  lyu { f1: 1 }
```

The field numbers do not match: the title travels in `f1` of `lze`, the ornament in `f2` of `lwm`.
Both accept an **empty message** as "none" — not a zero inside. The pushes that announce the result
follow the same rule: every `hid` seen is either `08b501` (title 181) or empty, and every `hif` is
one of `0867`, `0812`, `088701` (ornaments 103, 18, 135) or empty.

`hhy` is the "what you own" push, sent once on entering the world: `{ f1: packed title ids,
f2: packed ornament ids }`. Eight of them were captured: six carried a few dozen ids for established
characters — 62 titles and 28 ornaments in five of them, 56 and 22 in the sixth — and the two that
follow a brand-new character through creation and the tutorial were **empty**.

The emulator sends the whole catalogue instead — `datos/titles_ornaments.json`, **539** titles and
**167** ornaments — so everything can be tried. `Titles.HasTitle` / `HasOrnament` then reject
anything outside it, plus zero, which always means "none".

The sweep captures measured **476** distinct titles and **166** distinct ornaments actually accepted
by the real server (497 `lze` and 169 `lwm` requests, with repeats). All 476 and all 166 are inside
the offered lists; 63 offered titles and 1 offered ornament were never tried. `Cosmetics` checks that
containment at startup and warns if a regenerated `titles_ornaments.json` ever drops one — a title
that exists but cannot be equipped is worse than a missing title.

---

## 12. Outfits (`lyt`), and why the client dies without them

`lyt` carries the saved wardrobe outfits: repeated `f1`, one per outfit, and `f2` for the one
currently worn. The emulator sends one outfit and repeats it as the worn one
(`ConnectionProtocol.BuildOutfits`).

It has to be sent at login. Without it the cosmetics window plays its open sound and never draws:
the client's own log points at `ColorSet..ctor` receiving a null list, inside the `lyt` handler.

The reason is a colour-encoding difference that is easy to miss. Colours inside a **look** carry a
colour index in the high byte. Colours inside an **outfit** are bare RGB. Measured on the same real
`lxo`:

```
outfit colours   00e1b99d 00b4a1bb 00740237 00ee060d 001c6b6b 00936999
look   colours   01dc143c 02fffff0 03fffff0 04766443 07fbf105 0820a916
```

The two lists are not the same colours, and that is worth stating plainly: the outfit block always
carries six bare values, while the look's list is indexed and sparse — across the 48 bodies the
indices present were `1,2,3,4`, `1,2,3,4,7,8`, `7,8`, `1..6`, `1..8` or nothing at all. Only 4 of
the 48 have a look whose first six values equal the outfit's six. Whatever the second list means,
the encoding rule holds in every sample: outfit bare, look indexed.

Send indexed colours in the outfit block and the client cannot build its `ColorSet`. That is what
`BreedLookTable.PlainColors` exists for. It takes the six colours the emulator would put in the
look and masks the index off — enough for the client, and the only part of this that is settled.

The outfit body is the same block that goes inside `lxo` (`ConnectionProtocol.AppearanceBody`), which
is why the two are built by one function. Field census over the **48** bodies present in the 242
captures (29 inside `lxo`, 19 inside `lyt`):

| field | observed | reading |
|---|---|---|
| `f1 { f2 }` | packed colours, no index, 6 values, 48/48 | the outfit's colours. Bare RGB in every sample, against an always-indexed list in the look. |
| `f3` | 30-byte string, 48/48 | ISO-8601 UTC timestamp |
| `f5` | 26 (×40), 66 (×5), 39 (×2), 14 (×1) | **unconfirmed.** The emulator sends the breed id here, but breed ids only run 1–20, so this is not the breed. |
| `f7` | 36-byte string, 48/48 | uuid, the same one the preview `lxc` carries — that is how the panel recognises the reply as its own |
| `f8` | 3 (×43), 25 (×5) | kind. 3 on the live appearance, 25 on the five stored outfits. |
| `f9` | 206, 171, 22 | **the aura id.** Same three ids seen in `lxw`/`lym`. The emulator does not send this field. |
| `f10` | 181 | title. Matches the `hid` payload in the same session. |
| `f11` | 396, 163, 161, 421, 81 | **unconfirmed.** The emulator sends the character level; values above 200 rule that out. |
| `f12` | the look block | the look |
| `f13` | 7 bytes, only on the five stored outfits | plain ASCII text: the outfit's name. All five carry the same default label, so nothing here says whether a renamed outfit changes it. |
| `f15` | −1 (×33), 1 (×5) | pairs with `f8`: −1 on the live appearance, 1 on stored outfits |
| `f16` | 18 (×9), 135 (×9), 15 (×2), 103 (×1) | ornament. Three of the four also turn up as `hif` payloads; 15 never does, so it is the only one resting on the field alone. |
| `f17` (repeated) | `{ f1: slot, f2 { f2: item } }` | one per worn garment, 440 entries in total |

The emulator derives the `f7` uuid from the character id so it is stable and unique per character.
It sends `f5` and `f11` as breed and level because that is the best guess available; nothing depends
on them being right, and the table above records that they are not confirmed.

Because `BuildOutfits` emits the same body twice, the emulator's saved outfit goes out with
`f8 = 3` / `f15 = -1` — the live-appearance pair — rather than the `25` / `1` the real server uses
for a stored outfit. The client accepts it, so this is noted rather than fixed.

---

## 13. Mount and pet exclude each other — but only on the map

Measured across all 242 captures, decoding the look in every `lxc` and `kmb`:

| message | character | pet attached | count |
|---|---|---|---:|
| `kmb` | mounted | **no** | **74** |
| `kmb` | mounted | yes | **0** |
| `kmb` | on foot | yes | 22 |
| `kmb` | on foot | no | 76 |
| `lxc` | mounted | yes | **541** |
| `lxc` | mounted | no | 704 |

Zero out of 74. Thirty-seven different captures contain a mounted look with a pet in the preview, and
not one of them ever broadcasts it.

So: **mounted, the appearance pet shows in the panel preview but never goes out to the map.** A real
mount and a real pet share equipment slot 8, so the two cannot coexist in the world — but the panel
still draws the pet, so you can see what you picked. On foot both appear in both.

`BreedLookTable.BuildLook` takes a `paraLaVentana` flag ("for the window") for exactly this, and it
is the only thing that flag changes.

---

## 14. Hide and show

The eye toggle. The garment stays equipped; it just stops being drawn. That is different from taking
it off, because showing it again brings the same garment back.

```
lxg { f1: slot, f3: 1 }   hide
lxg { f1: slot }          show
```

Raw payloads from the hide/show capture: `08091801` (hide slot 9), `0809` (show slot 9), `080a1801`
(hide slot 10), `080a` (show slot 10).

The effect on the character's skin list in the following `lxc`:

```
cape on           [110, 5312, 4992, 5209, 5044, 3652, 5045, 5042, 5109]
hide slot 9       [110, 5312, 4992, 5209,       3652, 5045, 5042, 5109]   5044 gone
show slot 9       [110, 5312, 4992, 5209, 5044, 3652, 5045, 5042, 5109]
hide slot 10      [110, 5312,       5209, 5044, 3652, 5045, 5042, 5109]   4992 gone
show slot 10      [110, 5312, 4992, 5209, 5044, 3652, 5045, 5042, 5109]
```

Stored as a `Hidden` column on `CharacterAppearance`, added with an `ALTER TABLE` because the table
already existed without it. Putting a new garment into a slot resets `Hidden` to 0 — what you just
picked is meant to be visible.

---

## 15. Auras

```
C -> S  lxw { f2: aura }   empty = none
S -> C  lym { f1: aura }   push, empty = none
S -> C  lwx { f1: 1 }      answer
```

Aura ids seen on the wire: 22, 171, 206. Their bones show up separately, as a sub-entity at binding
point 6 inside `kmb` — 16 occurrences across the captures, with bones 169, 170, 4829 and 5138.
Pairing the `lym` id with the `kmb` bones is where `cosmetic_skins.json`'s `auras` table came from:
`22 → 170`, `171 → 4829`, `206 → 5138`. Bones 169 belongs to an aura that was never swept.

The aura id also travels in `f9` of the appearance body inside `lxo` and `lyt` — the same three ids,
which is how it was identified.

**Known gap.** `AppearanceHandler.AuraAsync` echoes the id in `lym` and stops there; it does not
attach binding point 6 to the look, and it does not fill `f9`. `Cosmetics.AuraBones` is loaded and
currently unused. The wearer sees their aura because the client resolves the id locally; other
players would not.

---

## 16. Where the emulator deliberately differs from the real server

| behaviour | real server | here | why |
|---|---|---|---|
| garment skin | **replaces** the equipped item's skin | **appends** to the skin list | the emulator does not yet do the removal. The real items' skins are now measured — see section 17 — so this can be closed; until it is, the result is identical on screen as long as nothing is worn underneath. |
| `hhy` | only what the account owns | the whole catalogue (539 + 167) | a single-player emulator with nothing unlocked has nothing to show |
| aura | also a sub-entity at binding point 6 in `kmb` | `lym` id only | not implemented; see section 15 |
| `hie` / `hii` | pushed alongside look updates, always `{ f1: 2 }` (129 and 131 occurrences, never any other value) | pushed on equip and unequip (`EquipmentHandler`), not on an appearance save | constant with no established meaning. Copied where a capture shows them, not guessed at elsewhere. |
| `lxo` / `lyt` `f9` | carries the current aura id | not sent | found while writing this document; see section 12 |
| miscellaneous items (49) | presumably resolved | not in the tables | they mimic amulet, ring, belt and boots, which are never drawn |

The state lives in two SQLite tables created by `Wardrobe.Initialize`: `CharacterWardrobe`
(title, ornament) and `CharacterAppearance` (slot, uid, item, hidden), keyed per character so
everything survives the session.

One practical limit when filling an inventory with garments: the client drops any message over
**131,072 bytes** — its own log says so — and the inventory goes out in a single `ivx` at roughly 69
bytes per item. All 2,420 garments would come to about 167 KB, well past the cap, and the client
would end up with **no** inventory at all. `tools/dotar_apariencias.py` therefore works to a budget:
measured garments first, then round-robin by type so the bag holds some of everything.

---

## 17. The real items' own skins

Everything above is about cosmetics. The pieces they cover — an ordinary hat, cape or shield — have
skins of their own, and until now those were the missing half: the emulator appends a cosmetic skin
instead of replacing the real one because it had nothing to replace.

They are measured now, by a different route. Cosmetics are read through the appearance window, which
previews without owning. Real gear cannot be previewed, so it has to be **equipped**, and that is a
different exchange:

```
C→S  iuk { f1: 1, f2: uid, f3: slot }     equip this item
S→C  ivq { f1: uid, f2: slot }            the item moved into that slot
S→C  lxc                                  and here is the new look
```

Everything needed sits in the server's own stream, so no cross-direction pairing is involved: an
`ivq` into a slot below 63 (63 is the bag) names the item, and the next `lxc` carries the result.
Diff that look against the previous one and the added skin belongs to that item. The `uid` is
translated to an item id through the session's inventory, where each entry reads
`f3 { f1: slot, f5 { f1: item, f4: uid } }`.

The source is a set of captures taken on a tournament server, where gear is free, equipping 433
items one at a time. **429 resolved, 99.1 %:**

| | resolved | what changes |
|---|---:|---|
| Hats (type 16) | 116 | one skin on the character |
| Capes (type 17) | 90 | one skin |
| Shields (type 82) | 79 | one skin |
| Pets | 98 | sub-entity bones at binding point 1 |
| Mounts and petmounts | 22 | root bones |

The result is in `datos/equipment_skins.json`, same shape as the cosmetic table: `skins`, `mounts`,
`pets`. Four shields did not move the look at all and are left out rather than guessed at.

**Two checks, one of them decisive.** The 286 items carrying a skin produced **286 distinct skins** —
a clean one-to-one map, no collisions to explain away.

Better than that, 46 skin values appear in *both* tables, and every pair is the same object twice:

| skin | cosmetic | real item |
|---:|---|---|
| 5631 | Escudo **destrozado** del Monte Puaj | Escudo del Monte Puaj |
| 53 | Escudo de Sidimote **Abollado** | Escudo de Sidimote |
| 52 | El Corazón Partido **y Rompido** | El Corazón Partido |
| 45 | Escudo del Báwbawo **Explotado** | Escudo del Báwbawo |

The cosmetic versions are the battered variants of real shields, and they carry the real shield's
skin. Those two tables were measured by different methods, in different sessions, on different
servers, against different characters — and they agree on 46 values. That is the strongest
confirmation either table has.

A cross-check against the older `sin apariencias equipar…` captures was attempted and came back
**inconclusive**, not confirming: those captures barely contain the `ivq` → `lxc` pattern, and the
one match carries a whole look rather than a difference. It is recorded here so nobody repeats it
expecting an answer.

Regenerate with `py tools/extraer_equipo_real.py --guardar`.

---

## 18. Repeating the measurement

The method is the point of this document. To extend the table to a family that is not covered, or to
redo it on a newer client version:

1. Start Wireshark on the client's TCP 5555. Traffic is in the clear.
2. Open the appearance window in the real client. One session, one `lyk` / `lyy`.
3. Try the garments on one at a time in the draft. Do **not** press Save — `lxs` broadcasts and adds
   noise, and nothing needs to be applied for the server to answer.
4. Ownership does not matter. That is the whole trick.
5. `py tools/extraer_apariencias.py <capture.pcapng>` to see what came out, then `--guardar` to merge
   it into `datos/cosmetic_skins.json`.
6. Read the "no change" count in the output. Those rows inherited a value instead of measuring one.
   If it is large, re-record with the garments in a different order so that neighbours in the same
   slot differ.

`py tools/pcap.py <capture.pcapng>` prints the raw opcode timeline if the extractor's pairing check
fails and you need to see why.
