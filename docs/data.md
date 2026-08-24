# Data

Where every number the emulator serves comes from.

The server holds almost no game data of its own. Dofus ships its catalogues inside the client, so
most of what `datos/` contains was pulled out of the client's own asset bundles and reshaped into
something a C# process can read at startup without a Unity runtime. The rest — the handful of
things the client does *not* know, because the real server tells it — was measured on the wire from
captured sessions.

Two rules shaped the layout:

- **The repository root stays almost empty.** Whoever downloads this should see the `.exe` and
  little else. Data goes in `datos/`, databases in `bases/`. `Jondo.Unity.Launcher/Paths.cs`
  resolves every file, looking in `datos/`, then `bases/`, then the root, so a half-moved install
  still starts.
- **Nothing that names a real person ships.** The captures come from real accounts. Anything that
  carries account data is either scrubbed or excluded — see [What is not published](#what-is-not-published-and-why).

---

## `datos/`

27 files, 67,040,313 bytes (63.9 MB): 22 json, 4 bin and one zip.

"Read by" is the class that loads the file at startup; the path always comes from a `Paths`
property, never from a literal. Four json files and one bin sit in the folder but are resolved by
nothing — they are working references, and `.gitignore` keeps them local.

| File | Bytes | What it holds | Read by | Built by |
|---|---:|---|---|---|
| `world.zip` | 25,319,824 | One entry: `world.db`, 251,518,976 bytes deflated ~10:1 | `DatabaseManager.Initialize` via `Paths.WorldZip` | zipped by hand from `bases/world.db` |
| `map_fight_cells.json` | 20,682,578 | 17,222 maps. `f` = cells you can stand on in a fight (`mov=1`, `nonWalkableDuringFight=0`), `b` = cells that break line of sight (`los=0`) | `MapManager` via `Paths.FightCellsJson` | `extract_fight_cells.py` |
| `map_walkable_cells.json` | 15,123,423 | 17,211 maps → walkable cells, map borders trimmed on purpose so monsters spawn inland | `MapManager` via `Paths.WalkableCellsJson` | `extract_all_map_walkable.py` (not in `tools/`) |
| `map_neighbours.json` | 2,729,541 | 17,353 maps → the map on each side plus the cells you can leave from on that side | nothing (it is written into `world.db`) | `extract_map_neighbours.py` |
| `interactive_elements.json` | 1,519,398 | 9,840 maps → what can be clicked, as `{e: element id, c: cell, g: graphic}` | `Interactives` via `Paths.InteractiveElementsJson` | `extract_interactivos.py` |
| `effects.json` | 576,361 | A dump of the client's `EffectsDataRoot`. Same size as `dofus3_data/effects.json` but not the same bytes: 33,136 differ, all of them Unity `rid` reference ids, so it is a separate dump of the same asset | nothing (`world.db.Effects` is used instead) | dumped from the client |
| `item_sets.json` | 152,013 | 929 sets: their pieces, and the bonus for each piece count | `ItemSets` via `Paths.ItemSetsJson` | `extract_item_sets.py` |
| `havenbag.json` | 146,718 | 48 haven bag themes with their map ids, and 4,083 pieces of furniture | `Merkasako` via `Paths.HavenBagJson` (*merkasako* is the Spanish name for the haven bag; the class and the script kept it) | `extract_merkasako.py` |
| `world_entering_3_6_10_10.bin` | 133,185 | The raw world-entry burst as captured: 363 frames, 69 distinct opcodes | nothing | raw capture, kept for reference |
| `spell_variants.json` | 102,089 | The client's `SpellVariantsDataRoot`: base/variant spell pairs per breed | `SpellTable` via `Paths.SpellVariantsJson` | copied from `dofus3_data/` |
| `world_etapa3_mapa.bin` | 90,935 | World entry block 3, the map: 31 frames, 17 opcodes | `WorldEntry` via `Paths.WorldStageMap` | carved from the capture, then `sanear_world.py` |
| `cosmetic_skins.json` | 84,322 | The look of each cosmetic, measured on the wire: 1,603 skins, 55 variants, 194 weapon slots, 61 variant slots, 242 pets, plus mounts, auras, titles and ornaments | `Cosmetics` via `Paths.CosmeticSkinsJson` | `extraer_apariencias.py --guardar` |
| `cosmetics.json` | 80,437 | The cosmetic catalogue: 12 item types, 2,420 items, 182 appearance entries | `Cosmetics` via `Paths.CosmeticsJson` | from the client dump; `completar_cosmeticos.py` patches the gaps |
| `world_etapa1_tras_elegir_personaje.bin` | 64,510 | World entry block 1, after the character is picked: 322 frames, 42 opcodes | `WorldEntry` via `Paths.WorldStageAfterCharacter` | carved from the capture, then `sanear_world.py` |
| `heads.json` | 56,345 | 638 character heads (id → skin, breed, gender) and the 38 defaults, one per breed and sex | `HeadTable` via `Paths.HeadsJson` | `extract_heads.py` |
| `equipment_skins.json` | 11,079 | The look of **real** gear, not cosmetics: 286 skins (116 hats, 90 capes, 79 shields), 98 pets, 22 mounts. Measured by equipping items one at a time, not through the appearance window. Nothing reads it yet — it is what a cosmetic would have to *replace* instead of append. See `appearances.md` §17 | nothing yet | `extraer_equipo_real.py --guardar` |
| `dungeons.json` | 51,371 | 187 dungeons: rooms, entrance map, exit map, level bands | `DungeonManager` via `Paths.DungeonsJson` | `extract_dungeons.py` |
| `character_xp.json` | 38,990 | 1,889 levels → accumulated experience | `ExperienceTable` via `Paths.CharacterXpJson` | `extract_character_xp.py` |
| `mounts.json` | 35,924 | 520 mounts, indexed by the certificate item that grants them: bones, colors, scale | `Mounts` via `Paths.MountsJson` | `extract_monturas.py` |
| `characteristics.json` | 14,927 | 122 characteristic ids → name, upgradable, visible, order, category | nothing | `extract_characteristics.py` |
| `breed_stats.json` | 12,781 | 19 breeds × 6 characteristics → what a point costs in each band | `BreedStatCost` via `Paths.BreedStatsJson` | `extract_breed_stats.py` |
| `dofus3_mappings.json` | 9,524 | 93 `type.ankama.com/<opcode>` → a name someone assigned while reversing; 3 also carry field renames | nothing | no script |
| `breed_looks.json` | 4,314 | 19 breeds × male/female → bones, skins, scales, six default colors | `BreedLookTable` via `Paths.BreedLooksJson` | `extract_breed_looks.py` |
| `waypoints.json` | 3,553 | The 62 zaaps with their map and sub-area | `Interactives` via `Paths.WaypointsJson` | `extract_interactivos.py` |
| `titles_ornaments.json` | 2,660 | 539 title ids and 167 ornament ids | `Titles` via `Paths.TitlesOrnamentsJson` | `extract_titulos.py` |
| `world_etapa2_tras_confirmar.bin` | 2,348 | World entry block 2: 2 frames, `jby` (40 B, meaning not established) and `jtg` (2,306 B), the account's gift-item catalogue | `WorldEntry` via `Paths.WorldStageAfterConfirm` | carved from the capture, then `sanear_world.py` |
| `item_effect_fields.json` | 1,226 | 121 effect ids → which protobuf field of the `ivx` entry carries the value | `EffectFields` via `Paths.EffectFieldsJson` | measured from a capture; no script |
| `zaap_overrides.json` | 1,016 | Maps the client's waypoint table calls a zaap but whose element cannot be recognised by its graphic. One entry today: map `115083777` → element `481520`, picked by elimination among four elements, none of which carries a zaap graphic. The file writes down why, so the next person can change one number instead of redoing the work | `Interactives` via `Paths.ZaapOverridesJson` | written by hand |

Three notes on that table.

**`map_walkable_cells.json` and `map_fight_cells.json` overlap but are not interchangeable.** Both
come from the same client map bundles. The first trims columns 0–1 and 12–13 and rows 0–5 and
35–39 so that spawned monsters land inland instead of on a map edge, and it says nothing about
sight. The second keeps the whole grid and adds the `los` flag. `MapManager` loads both and says so
in a comment, because loading only the first is what once put fights on a shrunken board.

**Four generators in that last column are not in `tools/` right now.** `Paths.cs` records
`extract_fight_cells.py` and `extract_character_xp.py`; `Managers/Titles.cs` records
`extract_titulos.py`; `extract_all_map_walkable.py` is named by nothing in the code at all.
`extract_titulos.py` has no copy anywhere here. The other three survive only in an old working
branch, and those copies predate `tools/rutas.py`: they hardcode `C:\Jondo\...` for both the client
bundles and the output, so they write outside `datos/` and would need their paths fixed before they
could be run. The files all four produce are already in `datos/`, so nothing is blocked, but
regenerating any of them means repairing or rewriting the script first.

**`item_effect_fields.json` has no generator by design.** It is not in the client at all. Each item
effect puts its value in a *different* protobuf field of the `ivx` entry, and the field is the type
tag, not a slot. Six forms, and the file uses all six: `f4` a plain number (94 effects), `f5` a
min/max range (10), `f6` value plus dice (10), `f1` a string (3), `f2` a date (2), and no field at
all (2). `EffectFields.Shape` sends the first three and skips the strings and the dates, because
those are labels the real server writes onto a finished item and nothing here finishes items.
Writing a varint into `f5` is a wire-type error, not a different value — the client
looks for a submessage, finds none, and drops the parameters. That table was learned by reading 609
items out of a real inventory message, so it lives as data and is stated as measured.

---

## The three world-entry blocks

Entering the world is not one burst. The real server sends a block, waits for the client, and only
then continues:

| Cue | Server sends | File | Frames |
|---|---|---|---:|
| `kvw`, the client picks a character | character, stats, quests, almanax… | `world_etapa1_tras_elegir_personaje.bin` | 322 |
| nothing — it follows block 1 | `jby`, then the gift-item catalogue `jtg` | `world_etapa2_tras_confirmar.bin` | 2 |
| `lqc`, "block 1 digested" | the map | `world_etapa3_mapa.bin` | 31 |

Two of those differ from the recorded session, and both differences are deliberate.

**Block 2 does not wait.** In the capture the client asks for it with `lqc`. It sends that `lqc`
here too, but only after digesting block 1, by which time the catalogues have been sitting unused.
So block 2 goes out on the heels of block 1.

**Block 3 goes out on `lqc`, not on `kqo`.** `kqo` is the heartbeat — the client sends it every five
seconds for as long as it is in the world, 2,821 times across the captures, and the server answers
each one with a bare `kqy`. Treating it as a request for the map cost 4.8 seconds of a client
standing in a world it did not know the map of, and sending the block twice made it reload in a
loop, because the block carries `jru` and `jru` means "load this map". The `kqo` path survives only
as a fallback for clients that never send `lqc`; `tools/cliente_falso.py` is one.

That is why block 3 is its own file: it has to go out exactly once, on its own cue. Blocks 1 and 2
are two files because that is how the capture was cut.

The bytes are the real ones. Without a schema for every message that is the only way to get this
far. `WorldEntry.Rebuilt` is the list of frames the emulator builds from the database instead of
replaying, and it is meant to grow: `kva` (which character you are), `irq` (the jobs, which arrived
maxed out), `hms` (the spells), `ivx` (the inventory) and `itg` (both shortcut bars — the spell one
rebuilt, the item one sent empty). `jru` is rewritten too, to carry the map you are standing on.
Everything else still describes the captured account.

### What was taken out, and why

The recorded burst describes a real account and everyone who happened to be nearby: the contact
list, twenty Ankama accounts with nickname and tag, alliances by name, the guild, the spouse, the
saved outfits, a player's stall with the account behind it. Shipping the file as recorded ships all
of that.

`WorldEntry.NotReplayed` already refused to forward those 14 message families, so the emulator's
output was already clean. But the data was still *inside the file*, and the file is what gets
published. `tools/sanear_world.py` drops those frames and leaves every other byte untouched. Same
list as `NotReplayed`, deliberately — if one is added there it goes here too. The traffic on the
wire is identical before and after; what changed is that the data no longer travels in the
repository.

Measured on the files as they stand:

| File | Frames with a sensitive opcode |
|---|---|
| `world_entering_3_6_10_10.bin` (raw, not published) | 13, across 11 families: `kub`×2, `jhh`×2, `jhe`, `jhk`, `ife`, `jgu`, `jaa`, `lyt`, `hhy`, `ihb`, `jjs` |
| `world_etapa1` / `world_etapa2` / `world_etapa3` | none |

`py tools/sanear_world.py --ver` re-runs the check and reports nothing to remove, which is the
result you want.

`kub` is the one exception in that list: it is not dropped, it is replaced. The captured `kub` is a
level-154 character sheet, and the emulator builds its own from the database.

---

## The databases

Two SQLite files in `bases/`. They are the only things the emulator writes.

### `world.db` — 38 application tables after startup

Distributed compressed as `datos/world.zip`. On startup `DatabaseManager.Initialize` checks whether
`bases/world.db` exists *and* has an `ItemTemplates` table; if either is false it unpacks the zip
into `bases/`. That second condition matters: a stale zero-byte or half-built database would
otherwise be accepted and the server would come up with no items.

Nine tables are the client's catalogues. The emulator never creates, inserts, updates or deletes
them — in the C# they only ever appear in `SELECT`s, and they exist solely because they ship inside
the zip. (One offline tool does write to `ItemTemplates`; see the regeneration section.)

| Table | Rows | What it is |
|---|---:|---|
| `Translations` | 339,175 | Text id → string. This build carries the Spanish dump: of the 338,990 rows whose key also exists in the client's language files, 338,952 match `es.json` byte for byte (99.99%) against 57,163 for `en.json` (16.9%) |
| `ItemEffects` | 66,294 | Effect rows referenced by item templates, by `Rid` |
| `ItemTemplates` | 21,748 | Every item: id, name id, type, and the raw `Data` blob |
| `SpellTemplates` | 17,114 | Raw spell data |
| `NpcTemplates` | 6,468 | NPCs |
| `MonsterTemplates` | 5,134 | Monsters, raw |
| `MapTemplates` | 15,360 | Per-map data blob, joined against `MapPositions` when maps load |
| `Effects` | 872 | The effect catalogue: category, dice usage, percent flag, priority |
| `SubAreaTemplates` | 562 | Sub-areas |

The other 29 are created by the emulator with `CREATE TABLE IF NOT EXISTS`, so a database that
predates a feature picks it up on the next start. World data first, then what a player accumulates.
Row counts are from the copy inside `world.zip`; the player tables grow as you play.

| Table | Rows | Created in | Purpose |
|---|---:|---|---|
| `MapScrolls` | 17,353 | `DatabaseManager` | Map → the map on each of the four sides |
| `MapPositions` | 15,360 | `DatabaseManager` | Map → x, y, sub-area, indoor/outdoor, name |
| `MapSubareas` | 15,359 | `DatabaseManager` | Map → sub-area, used to pick which monsters spawn |
| `MapMobs` | 38,744 | `DatabaseManager` | Placed monster groups: map, cell, members |
| `Monsters` | 5,134 | `DatabaseManager` | Monster look, grades and spells, flattened for lookup |
| `Subareas` | 562 | `DatabaseManager` | Sub-area → the monsters allowed in it |
| `Spells` / `SpellLevels` | 17,113 / 34,823 | `DatabaseManager` | Spell headers, and one row per spell and grade |
| `SpellVariants` | 20 | `DatabaseManager` | Breed → its base/variant spell pairs |
| `Dungeons` / `DungeonRooms` | 187 / 763 | `DatabaseManager` | Dungeons and their rooms |
| `NpcSpawns` | 2 | `DatabaseManager` | Where an NPC stands |
| `Servers` | 14 | `DatabaseManager` | The server list. One is joinable; the rest show up greyed so the screen looks populated without promising worlds that do not exist |
| `Characters` | 2 | `DatabaseManager` | The characters, with position, stats, look and server |
| `CharacterItems` | 1,749 | `DatabaseManager` | Inventory: uid, item id, quantity, position, effects |
| `CharacterSpellChoices` | 7 | `DatabaseManager` | Which half of each spell pair the player picked — the only spell fact that is not the client's |
| `CharacterSpellBar` | 41 | `DatabaseManager` | Which spell sits in which shortcut slot |
| `CharacterWardrobe` | 1 | `Managers/Wardrobe.cs` | Equipped title and ornament |
| `CharacterAppearance` | 7 | `Managers/Wardrobe.cs` | Equipped cosmetics per slot, and whether each is hidden |
| `HavenBag` | 1 | `Managers/HavenBagStore.cs` | Which haven bag theme the character uses |
| `HavenBagFurniture` | 0 | `Managers/HavenBagStore.cs` | Furniture placed in the room |
| `HavenBagChest` | 3 | `Managers/HavenBagStore.cs` | The haven bag chest contents |
| `Jobs` / `Skills` | 23 / 368 | `DatabaseManager` | Profession and interactive-skill catalogues imported from the 3.6 dofusdude JSON |
| `SkillCraftableItems` / `SkillModifiableItemTypes` | 5,693 / 17 | `DatabaseManager` | Normalized variable-length skill capabilities |
| `Recipes` / `RecipeIngredients` | 4,858 / 24,532 | `DatabaseManager` | Craft results and their ordered ingredients |
| `InteractiveTeleports` | 3,815 | `Managers/TeleportManager.cs` | Teleport candidates from Giny and WorldGraph, rebuilt from JSON on every start; only validated `Enabled=1` rows reach the registry |

`sqlite_sequence` is SQLite's own bookkeeping and is not counted in the 38.

The shipped database is already fully populated. That matters because `DatabaseManager` contains two
seeding paths (`EnsureMobsSeeded`, `EnsureSpellsSeeded`) that read `dofus3_data/`, and
`dofus3_data/` is not published. They are guarded by `SELECT COUNT(*)` on `MapMobs` and `Spells`
respectively, both non-zero in the shipped file, so on a normal install they never run and the
missing folder is never noticed.

Both databases run `PRAGMA journal_mode=WAL`, which is why `-wal` and `-shm` files appear next to
them.

### `auth.db` — 16,384 bytes, 1 table

Created from nothing on first run; it is not shipped. One table:

```
Accounts(Id, Login, Password, Nickname, GameToken)
```

Two test accounts are seeded with `INSERT OR IGNORE` on every start, so deleting one brings it back.
`GameToken` is rewritten on every launcher
login and is what the game server checks when the client connects to port 5555. Logins are matched
with a parameterised query against a `^[a-zA-Z0-9_@.-]{3,32}$` pattern, and five failures from one
IP lock that IP out for 60 seconds.

Passwords are stored in clear. This is a local emulator for a game client with no real accounts
behind it, and saying so plainly is better than implying a security property that is not there.

---

## Regenerating `datos/` from scratch

Everything in `datos/` traces back to two sources: the client's asset bundles and the captures.

**The client.** `Cliente 3.6.10.10\Dofus_Data\StreamingAssets\Content\` holds 205 bundles under
`Data\` — 204 of them one per `*DataRoot` catalogue, plus one bundle of scripts — and 577 under
`Map\Data\`, of which 569 are map geometry.
`dofus3_data/` is that content already unpacked to json: 84 files, 457,424,740 bytes (436.2 MB) —
75 raw Unity typetree dumps of the `*DataRoot` objects, the 5 language files as `{"entries": {id:
text}}`, and 4 multilingual `MAPPED_*.json` exports.

Some `tools/` scripts read `dofus3_data/`; the rest open the `.bundle` files directly with
**UnityPy** (`UnityPy.config.FALLBACK_UNITY_VERSION = '2022.3.20f1'`).

**The captures.** 242 `.pcapng` files. Only three things in `datos/` come from them, because they
are the three things the client does not know: the item-to-look table (`cosmetic_skins.json`), the
effect field map (`item_effect_fields.json`) and the world entry blocks.

Splitting a capture into the three `world_etapa*.bin` blocks is done by `extraer_world.py`, which
`Paths.cs` names but which is not in `tools/`. `sanear_world.py` is the second half of that job: it
takes the blocks and strips the frames that carry account data. If you re-cut the blocks from a new
capture, run it before anything else touches them.

### The order

Python is invoked as `py`, never `python`. Every generator but `extract_heads.py` resolves its own
paths through `tools/rutas.py`, so they take no path arguments and can be run from anywhere.
`extract_heads.py` derives its own paths instead, which is why it writes to the wrong folder — see
below.

```
# 1. From the client bundles, straight into datos/
py tools/extract_breed_looks.py          -> breed_looks.json
py tools/extract_breed_stats.py          -> breed_stats.json
py tools/extract_characteristics.py      -> characteristics.json
py tools/extract_heads.py                -> heads.json, but in the repository root; move it
py tools/extract_dungeons.py             -> dungeons.json
py tools/extract_merkasako.py            -> havenbag.json
py tools/extract_monturas.py             -> mounts.json
py tools/extract_interactivos.py         -> interactive_elements.json + waypoints.json

# 2. From the dofus3_data dump
py tools/extract_item_sets.py            -> item_sets.json

# 3. Into world.db. The first two only report without --aplicar; the third writes by default
#    and its look-only flag is --ver. extract_map_neighbours.py writes its json either way.
py tools/extract_map_neighbours.py --aplicar   -> map_neighbours.json, then MapScrolls
py tools/cosechar_mapas.py --aplicar           -> MapScrolls, filled from the captures
py tools/completar_cosmeticos.py               -> ItemTemplates + cosmetics.json

# 4. From the captures
py tools/extraer_apariencias.py <capture.pcapng> --guardar   -> cosmetic_skins.json
py tools/sanear_world.py --ver                               -> what would still be stripped
py tools/sanear_world.py --salida datos                      -> rewrite the world_etapa*.bin clean

# 5. Repack, once world.db is right (PowerShell)
Compress-Archive -Path bases\world.db -DestinationPath datos\world.zip -Force
```

The zip must hold `world.db` at its top level, with no folder around it. `DatabaseManager` calls
`ZipFile.ExtractToDirectory` on `bases\`, which preserves entry paths — an entry nested in a folder
lands in `bases\<folder>\world.db` and is never found.

`extract_characteristics.py` and `extract_dungeons.py` open `world.db` as well, because the names
they need live in `Translations`. Run them after the database exists.

`extract_heads.py` is the odd one out: it writes `heads.json` to the repository root, not to
`datos/`. `Paths.Resolve` searches `datos/`, then `bases/`, then the root, so the emulator still
finds it — but move it into `datos/` to match everything else.

Two of these will not overwrite a value that is already set. `extract_map_neighbours.py` and
`cosechar_mapas.py` only fill holes; if the client's table and the harvested value disagree they
report it and change nothing, because a contradiction means one of the two sources is wrong and
that has to be looked at by hand rather than resolved by whoever ran last.

Everything else in `datos/` is either taken from the dump — `spell_variants.json` is byte-identical
to `dofus3_data/spell_variants.json`, `effects.json` is a separate dump of the same asset — or
written by hand (`zaap_overrides.json`, `item_effect_fields.json`).

---

## What is not published, and why

Five things are deliberately excluded, for five different reasons.

**`bases/` — size, and it rebuilds itself.** `world.db` is 240 MB uncompressed. It ships as
`datos/world.zip` at 24.1 MB and unpacks on first run. `auth.db` is 16 KB of accounts you create
yourself; there is nothing to distribute.

**`dofus3_data/` — 436 MB, and nothing needs it at run time.** It is the raw client dump. Only
`tools/` reads it, and only to regenerate `datos/`. Anyone who wants it can produce it from their
own client install, which is also the only defensible place for it to come from.

**`tools/` — not needed to run the emulator.** 22 Python scripts that build `datos/` and audit
traffic, plus a small C# market scanner. Keeping them out is what makes it obvious that `datos/` is
the interface: if the emulator needed a script at run time, that would be a bug in `Paths.cs`.

**`logs/` — real people.** 117,327,178 bytes across 8 files. `gameserver_traffic.log` alone is
110,951,526 bytes: every frame in and out, hex and ASCII. It carries whatever the client typed and
whatever the replayed capture bytes contain, which is why `tools/leak.py` exists. The `.csv` map
dumps go with them. This one is not a size decision. It is the same rule as the captures, and
`.gitignore` says so bluntly: *"Nunca. Llevan nombres de cuentas reales."* — *Never. They carry
real account names.*

**The captures — the same, at the source.** 242 `.pcapng` files, 257,711,968 bytes (245.8 MB), of
real sessions on the official servers. They are the evidence behind every measured number in this
documentation, and they contain other people's names, chat and account tags. The documents quote
what was measured from them; they never quote their contents.

Also excluded: five files inside `datos/` that no `Paths` property resolves
(`world_entering_3_6_10_10.bin`, `characteristics.json`, `dofus3_mappings.json`, `effects.json`,
`map_neighbours.json`). They are working references. `world_entering_3_6_10_10.bin` is the strongest
case — it is the unsanitised burst, and it is exactly what `sanear_world.py` exists to clean.

`dofus3_mappings.json` is worth one caution if you find a copy. Of its 93 opcodes, 16 appear in the
242 captures. Some of the rest belong to the login and server-list phase, which is encrypted and so
cannot show up in a plaintext capture, but the file is a reversing scratchpad, not a verified table.
Nothing reads it. Do not treat its names as facts; `docs/opcodes.md` is the table that was checked.

---

## The 131,072-byte ceiling

The client refuses any single message over **131,072 bytes** and says so in its own log:

```
EXCEPTION (TcpConnectionLayer:395) - System.ArgumentException:
Message size (…) exceeds maximum allowed size (131072).
```

It is not a soft limit. The frame is dropped, so a message that goes over does not arrive smaller —
it does not arrive at all.

Only one message the emulator sends can grow into that: the inventory, `ivx`, which travels
**whole, in one frame**. There is no paging. So the size of a character's inventory is a hard
protocol constraint, not a gameplay choice. (`ivi`, the account's statistics counters, is the next
biggest at 87,845 bytes, but it is replayed from the capture verbatim and never grows.)

### The measurements

| Source | Entries | `ivx` payload | Bytes per entry |
|---|---:|---:|---:|
| Real server, largest `ivx` across the 242 captures | 616 | 20,691 | 33.6 |
| This emulator, largest `ivx` in its own traffic log | 1,725 | 96,980 | 56.2 |

The emulator's entries are the bigger of the two. The likely reason is that it writes fuller effect
lists than the captured account happened to carry, but that has not been checked item by item, so
treat 56.2 as measured and the explanation as unconfirmed. On top of the payload the envelope adds
36 bytes — `type.ankama.com/ivx`, the protobuf tags around it and the varint length prefix. That
96,980-byte payload left the server as 97,016 bytes on the wire. Noise at this scale, but the
ceiling applies to the frame, not to the payload.

### The budget

`tools/dotar_apariencias.py` is the script that fills a character with cosmetics, and it is the one
place where this constraint is arithmetic instead of a warning:

```python
TOPE_CLIENTE   = 131072   # what the client accepts
MARGEN         = 12000    # headroom
BYTES_POR_OBJETO = 69     # conservative, measured
```

```
room = (131072 - 12000) // 69 = 1725 items
```

The script subtracts what the character already owns before dividing, so the total it aims at is the
same 1,725 either way. And 1,725 is exactly the entry count in the largest `ivx` the emulator has
actually sent: that frame came out at 97,013 bytes, 34,059 under the ceiling. The 69-byte figure is
deliberately pessimistic against the 56.2 measured, and the 12,000-byte margin sits on top of that.
Two layers of slack, because being wrong here costs the player the whole inventory, not the last
few items.

The catalogue has 2,420 cosmetics. They do not fit, and never will: at 69 bytes each they would
need 166,980 bytes, 1.3× the ceiling. So the script spends its budget on purpose. Items whose look
the emulator can actually resolve go in first, since those are the ones that will be visible; the
rest is filled by round-robin across item types. Filling by item id instead — the obvious way —
produced a bag full of hats and not one cape, because ids cluster by type.

If you need more items than that, the fix is not a bigger buffer. It is paging `ivx`, which the
protocol as measured does not do.

---

## Checking any of this yourself

```
py tools/sanear_world.py --ver               what would still be stripped from the entry blocks
py tools/leak.py                             every readable string the server has sent, by opcode
py tools/pcap.py <capture.pcapng>            opcode timeline of a capture
py tools/cliente_falso.py                    talk to the emulator without opening the game
```

`leak.py` is the one to run after touching anything that replays capture bytes. It does not look
for names it was told about in advance — the previous version did, and that is why twenty accounts
with nickname and tag travelled in a `koj` for a while with nobody noticing. It now sweeps every
readable string out of everything the server sends and groups it by message. Real names are
unmistakable once you see them together.
