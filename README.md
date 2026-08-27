High-performance server emulator for **Dofus 3 Unity (Client 3.6.10.11)** written in C# (**.NET 10**), with decoupled modular projects, a SQLite data layer, a playable PvM combat engine driven entirely by client data, and a world editor.

> ⚠️ **Runs against Dofus 3 clients 3.6.10.11 and 3.6.10.10.** Ankama renames every protobuf message to three random letters on some patches, which is what breaks compatibility with newer clients. There is a toolchain here for surviving that — see [Surviving the next patch](#-surviving-the-next-patch). It does not make the emulator version-agnostic; it makes the migration measurable instead of guesswork.
>
> **3.6.10.11 (26 August 2026) is not a new protocol.** Its `GameAssembly.dll` and `global-metadata.dat` are byte-identical to 3.6.10.10
---

## 🚀 Quick Start

**Nothing has to be compiled.** The launcher ships as a single ready-to-run executable with every dependency inside it, and the world database ships compressed and extracts itself on first run.

### Step 1 — Install the .NET 10 runtime

Download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). The *Desktop Runtime* is the one you want.

### Step 2 — Point the Dofus client at the emulator

The official client talks to Ankama's servers and checks their SSL certificates. **JondoFix**, a MelonLoader mod, redirects it to your machine instead. It comes already built in this repository.

1. Get **MelonLoader 0.7.x** from [its releases page](https://github.com/LavaGang/MelonLoader/releases). **Read this bit or you will pick the wrong one:** 0.7.x is published as *Open-Beta*, so it shows up as a **pre-release** and the page's "Latest" tag still points at 0.6.x. **0.6.x does not work with this client** — tick *show pre-releases* and take 0.7.x. The setup this repository is tested against runs **0.7.3**.
2. Run the installer and point it at your **`Dofus.exe`**. That is the only thing you have to choose: MelonLoader works out the rest by itself. On this client it reports `Game Type: Il2cpp`, `Game Arch: x64`, `Runtime Type: net6`, Unity `6000.3.16f1` — you do not set any of that.
3. Copy **`JondoFix/JondoFix.dll`** from this repository into the **`Mods/`** folder of your Dofus installation, next to `Dofus.exe`. MelonLoader creates that folder the first time the game starts; if it is not there yet, just create it yourself.

> The mod ships **already compiled** and is the exact binary in use — you never need to build it. `JondoFix/` also carries its source, in case you want to read or change it.

Two things worth knowing afterwards:
* The installer drops a **`version.dll`** next to `Dofus.exe`; that is what loads MelonLoader. Renaming it to `version.dll.disabled` turns the whole thing off so you can play the official game, and renaming it back turns it on again — no need to uninstall anything.
* MelonLoader writes a log per run under **`MelonLoader/Logs/`**. If the client starts but never reaches the emulator, that file is the first place to look.

What JondoFix does: intercepts sockets, Named Pipes and DNS queries and sends them to `localhost` (ports `8888`, `5555`, `15881`, `6337`); stops HTTPS requests from failing against the local self-signed certificate; and injects the environment variables the client expects (`ZAAP_PORT`, `ZAAP_HASH`, and so on).

### Step 3 — Run it

Double-click **`Jondo Emulator Launcher.exe`**. That is the only thing you start by hand: it launches **`Jondo Server.exe`** itself, in its own window with the log and the counters.

On the first run it unpacks `datos/world.zip` into `bases/world.db` (about 240 MB, it takes a moment) and creates `bases/auth.db` with a test account. Sign in to add an account to the launcher's team, select one or several saved profiles, then press **Launch selected**. Up to eight independent Dofus clients can be active at once.

```
Account: keka
Password: test
```

By default the emulator looks for the client next to itself, in a `Cliente 3.6.10.11` folder beside the emulator folder — or `Cliente 3.6.10.10`, whichever it finds first. If yours lives somewhere else, click the path row under the play button and point it at your `Dofus.exe`. The choice is remembered, and if the client later moves the launcher says so instead of failing silently.

The **ES / EN / FR** buttons in the top bar set the language of the launcher *and* of the game: the client is started with that `--langCode`.

**`Jondo Studio.exe`** is the third executable and needs nothing else running: double-click it whenever you want to look at the world or build content. See [Jondo Studio](#-jondo-studio) below.

---

## 📂 What you get

```
Jondo Emulator Launcher.exe   ← this is what you run
Jondo Server.exe              the server; the launcher starts it
Jondo Studio.exe              the world editor; open it when you want to look or build
content/                      the only files a person edits by hand, versioned in git
datos/                        json and bin the emulator reads (maps, items, appearances, zaaps…)
bases/                        world.db and auth.db, the only things the emulator writes
docs/                         technical documentation
launcher_assets/              launcher artwork and music
JondoFix/                     the MelonLoader mod, source and compiled dll
Jondo.Unity.*/                source code
```

`content/` **is** in the repository, deliberately: it is the only folder a person edits by hand, it is small, and a change in it is a reviewable diff.

Important player and administrator actions are also written as one JSON object per line in
`logs/activity.jsonl`. Commands, equipment moves, lottery prizes, granted items, fights, live
administration and new unhandled packet shapes can therefore be filtered without scraping the
human-readable console log. Credentials, launcher tokens and game tickets are never included.

Not in the repository because they are not needed to play: `bases/` (built on first run), `logs/`, `tools/` (the Python that regenerates `datos/`) and `dofus3_data/` (436 MB of raw client dump, only used by those tools).

---

## ✅ Emulation status

✅ done · 🟡 partial · ❌ missing

### 🖥️ Launcher
- ✅ Native WinForms interface drawn from code, with its own theme, artwork and music
- ✅ Account creation and login, written straight to `auth.db`
- ✅ Persistent team of up to 8 accounts, one independent Dofus process each
- ✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use ticket, socket-owned session
- ✅ Independent lifecycle indicators for profiles, processes and sockets
- ✅ Embedded server log; single-file deployment; ES/EN/FR
- ✅ Launcher and server are separate programs — the launcher carries no database, maps, handlers or effect catalogue

### 🌍 Connection and world
- ✅ Zaap, HAAPI and connection server emulation, VIP check bypassed
- ✅ Server and character selection, showing the mount being ridden
- ✅ Character creation with a starter kit — Astrub zaap, adventurer set, 1,000,000 kamas, 101 scrolled points per characteristic
- ✅ World loading, spawn, name hover, last cell and map persisted
- ✅ **15,360 maps**, **17,211** with walkable-cell data, **17,222** with combat cells
- ✅ Movement, map change and adjacent maps; auto-pilot from the minimap and *travel to*
- ✅ Seeing others arrive and leave, in all four directions
- ✅ Up to 8 clients at once, each on its own socket-owned session
- ✅ **A server on another machine.** Every listener honours `JONDO_PUBLIC_BIND`, and the launcher runs a loopback relay so the client reaches it. The relay is not a convenience: HAAPI and the chat server both hand the client `127.0.0.1`, so repointing the client at a remote host cannot work on its own

### 🌀 Travel
- ✅ **62 waypoints** with map, cell and sub-area, plus 3 departure-only zaaps the waypoint table omits
- ✅ Travel between zaaps with the real cost and destination list
- ✅ Discovered zaaps announced on world entry (`hjk`) — without it the travel window reads "No destination"
- ✅ Zaapis of Bonta (24) and Brakmar (21) at a flat 20 kamas, read off captures because client data cannot derive them
- ✅ The right window per list: `hjj` root field 0 zaap, 1 zaapi, 3 boat
- ✅ **16 temporal anomalies** with their 120-minute countdown, surfacing at vestiges (type 359), not at switched-off zaaps
- ✅ **3,815 interactive teleports** in the database
- 🟡 **Every passage is declared with the wrong skill.** All 3,815 say 114, which is *Utilizar* on a zaap. Measured three ways that agree: Ankama's own world graph uses **184** on 5,629 of 5,719 interactive transitions and 114 on none; over 401 captures 184 appears on 420 elements and 114 on 23, every one a zaap; and in our own traffic skill 184 is followed by a map change 178 times while 114 opens the zaap window. 339 and 361 are real but are *not* alternatives — they are signpost skills that ride alongside 184. New passages written in Jondo Studio declare 184; the 3,815 extracted rows have not been rewritten
- ✅ **New passages can be created**, both ways, from Jondo Studio — which is what makes a house with its own interior possible

### 🏘️ Houses, bins and haven bags
- ✅ **1,437 doors on 553 maps**, all enterable and ownerless; **261 house models** with name, price and room count
- ✅ Entering and leaving, which are different messages (`jqw` in, `jru` out), coming out through the door you went in by
- ❌ The house plaque, chest, access code, buying and selling
- ✅ **67 public bins on 63 maps** — they open, show empty and close
- ❌ Putting items into a bin and taking them out
- ✅ Haven bags: entering and leaving, their own zaap, **48 themes**, **4,083 furniture pieces** placed and persisted, chest with the full item flow, lottery machine, and no monsters inside

> Which house sits behind which door is **not in the client**. The 1,437 doors share **114 genuine interiors**, assigned deterministically and kept inside their own neighbourhood; the mapping lives in `datos/casas_mundo_3.6.10.10.json` and can be corrected by hand.

### 💬 Social
- ✅ Information messages as `lqn { type, message, parameters }` against the client's 2,555-entry table, not as chat text
- ✅ Level-up window with music and animation, on a real gain and on `.level` in either direction
- ✅ Private messages via `kth`, which the client routes by opcode and not by channel
- ✅ Last connection time and IP, stored per character
- ✅ Parties — invite, accept, refuse, leave, hand over the lead, kick, and a full member sheet
- ✅ Lead passes on when the leader leaves; a disconnect removes the member and tells the rest
- ✅ Friends list
- ✅ **Every command answers in the session's own language**, from a 48-key catalogue in Spanish, English and French. The language comes from the `--langCode` the launcher started the client with, not from the wire: measured over the nine authentication captures, the client does send its two-letter code, but in `kqz` field 3
- ❌ The invitation popup's *Details* button (`imd` → `ilb`), the dedicated member-gone message (`inc`), party search, party fights and following the leader

### 🎒 Character and inventory
- ✅ **21,748 item templates** and **66,294 item effects** — spawning, equipping, bags, destruction, persistence
- ✅ **929 item sets** with their bonuses
- ✅ **520 mounts** with their look, swapped and unequipped correctly
- ✅ Characteristic assignment, dynamic capital, points in sync across every client panel
- ✅ **17,113 spells** across **34,823 spell levels**; **638 character heads**
- ✅ **539 titles** and **167 ornaments**, applied, persisted and carried in the map actor block
- ✅ Commands — `.teleport`, `.kamas`, `.shop`, `.size`, `.level`, `.item`, `.itemset`
- ✅ **Live administration over HTTP** — `POST /api/personaje` sets base characteristics and kamas on a connected character, persists them and refreshes the sheet without a reconnect. Administrator role only, loopback only, and it takes the session's turn so it cannot cut across a fight
- 🟡 `.level` does not refresh the in-fight spell bar

### 👕 Appearances
Dofus does not ship the item-to-look table: the server sends it. **2,371 of the 2,420 cosmetics** in the catalogue were measured off captures, one garment at a time.

| Type | Working / catalogue | | Type | Working / catalogue |
|---|---:|---|---|---:|
| Shields | 524 / 524 | | Petmounts | 151 / 151 |
| Hats | 464 / 464 | | Mounts | 121 / 121 |
| Capes | 357 / 357 | | Shoulders | 121 / 121 |
| Pets | 242 / 242 | | Costumes | 92 / 92 |
| Weapons | 194 / 194 | | Living objects | 61 / 61 |
| Wings | 44 / 44 | | Miscellaneous | 0 / 49 |

- ✅ Appearance weapons carry no look by design — the client draws them; the server only remembers which of the 10 weapon slots each occupies
- ✅ Living objects imitate a different garment per variant, stored as **543 object/variant pairs** across 10 slots
- ✅ Mount and pet appearances are mutually exclusive, matching the real server
- ✅ **The real equipment renders too, and a cosmetic replaces it rather than stacking on top.** **741 real items** carry their own skin into the look; the slots a visible cosmetic covers are precomputed and skipped
- 🟡 82 of those skins were inferred by image matching and flagged for review by their author, so they are held back at load until somebody measures them
- 🟡 A second, older look path survives in `InventoryHandler` for four items and disagrees with the new table on both the field and the value. Left alone until a capture says which is right

### ⛏️ Professions
- ✅ **25,090 resources on 4,507 maps** across the six gathering jobs, with graphic → (type, skill) crossed from 305 captures
- ✅ The three states — full, depleted, busy — including the skill field moving between `f4` and `f3`
- ✅ Job levels and experience persisted, with the real curve `10 × level × (level − 1)`
- ✅ What you gather lands in the inventory, and the amount grows with job level
- ✅ Too low a job level blocks gathering the way the game does it
- ❌ Crafting professions: workshops, the craft window, and the **4,858 recipes** already in the database

### 👹 NPCs, monsters and dungeons
- ✅ **6,468 NPC templates** with 3D looks and dialogue trees
- ✅ **422 NPCs** standing where Ankama puts them across **202 maps**, cell and orientation taken from captures, dialogue attached where it was captured
- ✅ **5,134 monsters** with native Protobuf bone models, custom scales and textures, quest monsters and archmonsters included
- ✅ **38,744 mapped mob groups**, respawned and kept populated, 1 to 8 monsters each
- ✅ Sub-area aware spawning across **562 sub-areas**, with radius-2 cell validation so nothing spawns on decorations or zaap pillars
- ✅ **187 dungeons** with their **763 rooms**, entrance and exit
- ✅ **No monsters indoors, and none standing on a zaap.** They used to spawn inside houses, banks and shops. The rule is two lists and one exception, and the exception is the one that matters: 753 of the 763 dungeon rooms are themselves marked indoors, so a blanket ban would have emptied every dungeon. 7,214 groups removed of 38,744, and the 763 rooms untouched
- ✅ **NPC colours.** The colour section of a look is `index=value` pairs, sometimes hexadecimal, and it was being read as a plain list — so nothing parsed and every one of the **2,045 NPCs that carry colours** rendered grey
- ✅ A dialogue always offers at least one real reply, so it can always be closed. With an empty list the client draws its own *Leave* which never answers back
- 🟡 **401 monsters have no spells at all** in the database
- ✅ **Dialogue trees.** The client holds every line an NPC can say and every reply it can be given, and never which goes with which — measured across all 6,467 NPCs, there is no field for it. That mapping has always been the server's own, so it has to be authored, and now it can be: a tree written in Jondo Studio makes a reply lead to another line instead of closing the window. Nothing written means the old behaviour, which is what Snori Nairb still does — all 39 replies at once
- ✅ **Monster groups placed by hand**, and Ankama's own removable, without touching the 240 MB database that gets regenerated

### 🪙 Jondo Coin
A currency of this server's own — a real item with its own template, not a reskin of kamas.

- ✅ Drops from every monster at 100%, one coin per 25 monster levels: 1 for 1-25, 2 for 26-50, up to 9 at 201+
- ✅ Its own description in the five client languages, picked at runtime from the language the client is running in
- ✅ Vendors that charge in coins instead of kamas, one per category, appearance shops among them, priced by item type and rarity

See `docs/jondo-coin.md`.

### ⚔️ PvM combat
- ✅ Tactical arenas resolved from each roleplay map by zone offset, with clean context transitions
- ✅ Placement phase with red and blue tiles and cell swapping before *Ready*
- ✅ Isometric geometry (`MapGeometry`) over a pre-computed O(1) BFS distance matrix, with no diagonal steps
- ✅ Line of sight traced between cell centres against the arena's own blocker set
- ✅ Turn protocol, 30-second timers with automatic pass, AP/MP replenishment
- ✅ Movement with per-tile MP cost and collision against occupied cells
- ✅ Loot, victory and defeat screens, experience over **1,889 levels**, level-ups and group respawn
- ✅ Monster AI: a target chosen **per spell**, range measured against that target, walking to the spell's own range band, `MaxCastPerTurn` honoured, breadth-first pathing around obstacles, and line of sight
- 🟡 Weapon strikes apply damage and AP cost; the slash animation does not
- 🟡 `MaxCastPerTarget`, minimum cast interval and cast-in-line are enforced for the player, not for monsters
- ✅ **Push and collision damage** — being shoved into a wall, a hole or another fighter costs health, and the fighter acting as the wall takes half
- ❌ AP/MP dodge rolls, shields, lock and tackle in melee

> **Push damage, measured over 127 collisions in the captures.** `damage = blockedCells × (level/2 + pusher's 84 − target's 85 + 32) / 4`, floored, travelling as a `jwe` with `f14 = 80` and `f4 = −1` right behind the displacement — and alone, with no displacement, when the target could not move a single tile. Three anchors pin the three terms: a level-200 pusher with no bonus deals **33 per tile** and only ever 33, 66, 99 or 132; the level-**165** Zurkarak deals **57** over two tiles, which is `floor(2 × 114.5 / 4)` and which no fixed constant can produce; and a Zobal carrying 100 of push damage plus masks of 0/40/80/120 deals 58/68/78/88. The resistance is subtracted **inside** the quarter — 561 against 30 gives 331 over two tiles, where subtracting outside would give 316. A fighter blocking the push takes `floor(half)`, measured on 9 pairs out of 9. The **Unmovable** state (97) cancels the whole thing, because without displacement there is no collision. All twelve samples are locked into a startup regression guard.

> **Monster AI, measured over the 5,134 monsters.** Range 0-0 spells were never cast, because range was checked against the nearest enemy and that distance is never zero: **1,555 spells, 20.4% of the arsenal, 443 of them carrying damage**. Gluing to melee also locked out the **857 spells with a minimum range above one**. Fixing both drops the monsters that cannot touch the player from **24.9% to 15.1%**, and raises action points actually spent from **58.7% to 87.2%**.

### ✨ Spell effect engine
One engine for all eighteen classes, driven entirely by client data. Not a single spell is written by hand: everything comes out of `SpellLevels.EffectsJson` and the `Effects` catalogue.

- ✅ Effects, triggers and target masks read from the spell — `I` on cast, `TB` turn start, `TE` turn end, `DBE` when hit, `CCMPARR` per tile walked; `a` allies, `A` enemies, `g` summons, `E<n>`/`e<n>` gated on a state
- ✅ States need no code — effect 950 sets a number, 951 clears it, the masks do the rest
- ✅ Area shapes from `zoneDescr` — point, circle, cross, line, diamond, square, whole map — with each spell's own per-tile falloff
- ✅ Displacement — push, pull, step back, step forward, direction taken from the centre of the area, stopping at walls, holes and fighters
- ✅ Criticals rolled against the spell's probability plus the character's, using the spell's separate critical effect list
- ✅ Point steal, life steal, erosion of maximum HP and damage-taken multipliers
- ✅ Buff panel — icon, value, remaining rounds and dispellable flag; buffs start on their delay and expire on their round
- ✅ Cooldowns and cast limits — per turn, per target, minimum interval, initial cooldown
- ✅ Summons as real fighters — own sheet, place in the carousel next to their owner, behaviour spell, lifetime, and they all fall when their summoner dies
- ✅ Item attitudes — the six Dofus and the trophies grant their spell through effect 1175
- ✅ The characteristic sheet in the shape the client expects: 53 entries in a fixed order, and a single-characteristic refresh **replaces** its entry rather than adding to it, so it repeats every field and puts the buff in its own slot `f8`
- ❌ Healing — effect 108 is not wired; its catalogue row has `Characteristic = 0` and `Category = 2`, so it falls into the panel-only branch
- ❌ Glyphs and traps (effects 400, 401, 1091)
- ❌ Appearance-changing spells — the transform payload is an opaque blob, so the Cra's Sentinel works but does not change its look
- ❌ Area shapes `G` (55 effects) and `*` (10), which fall back to the centre tile alone

> The engine is shared, so every class gets whatever its spells happen to use. Only the **Cra** has been driven against real captures spell by spell; the rest are untested. A spell only works when **all** of its effects resolve, and the gaps concentrate in a handful of effect families, so they close in blocks rather than one spell at a time.

### 🎯 Combat challenges
- ✅ The preparation dance, measured across 305 captures with both directions on one timeline: two candidates with a 15-second timer, the player marks and validates, and the server fixes whatever is left when you declare ready
- ✅ **15 of the 16** watched live, with every rule taken from the challenge's own translated description
- ✅ Results travel the moment they happen — a failure the instant the challenge breaks, a success at the end, a defeat failing them all at once
- ✅ The bonus is folded into experience, kamas and drop rates on a win; it is not itemised anywhere on the wire
- ✅ Dungeon and anomaly challenges are imposed at 0% and carry achievements, written once and never offered again
- ❌ *Hired Killer* (35), which needs the server to designate and re-designate the target
- ❌ Challenges without a measured percentage — the client ships no bonus field, and the same challenge appears at 90 and at 150 always at +60, so there is a per-fight modifier nobody has reconstructed

### ❌ Not implemented at all
- Kolossium and PvP combat
- Crafting professions
- Achievements
- Guilds

---

## 🛠️ Jondo Studio

> ⚠️ **Very early.** The Studio is weeks old, it changes every day, and the parts that write files
> have been exercised by one person on one machine. Read it, use it, tell us what is wrong — but
> keep a copy of `content/` before a long session, and expect screens to move under you. Nothing in
> it can damage `world.db` or a running server, which is the one guarantee it does make.

The world editor. A third executable next to the launcher and the server, and it needs neither of
them running: it opens `content/` and the data files through the same paths the server uses and
works on its own. Built with **Avalonia**, so it runs on Windows, macOS and Linux.

It unpacks `world.db` from `datos/world.zip` the first time it runs, the way the server does, so a
fresh clone can open it and see the world without starting anything else.

It exists because of a problem this project could not solve any other way. The client holds a great
deal — every item, every spell, every monster — but there are things it has never held, because on
the real game they were the server's: which reply in a dialogue leads to which line, where an NPC
stands and what it does there, which interactive teleport comes back to which map. Those cannot be
extracted. They have to be **decided**, and until now the only place to decide them was a Python
script and a JSON file nobody could review.

### Three layers, and every row says where it came from

The data lives in three places that cannot be edited the same way: `dofus3_data/` is a raw dump of
the client, `datos/*.json` is regenerated by the tools in `tools/`, and `world.db` is a 240 MB
binary no pull request can review. A hand edit in any of them disappears the next time somebody
runs a script.

So there are three layers, merged on load, and only the last one is ever edited:

| layer | where from | who edits it |
|---|---|---|
| **base** | generated from the client dump | nobody |
| **measured** | learned from packet captures | nobody |
| **authored** | decided by a person | this is the one, and it always wins |

The authored layer is `content/`, in versioned JSON, so a change is a reviewable diff and two people
can edit different maps without colliding. It stores **deltas, not copies**, and it can *erase* a
row it did not write — an NPC Ankama places and we do not want has to be removable without editing
the generated file it came from, because that file gets rewritten.

**Every row carries its provenance**, and that column is the point: six months from now nobody will
remember whether a cell number was measured off a capture or typed in by hand, and without it on
screen the two become indistinguishable.

### What it does today

Nine sections, **in Spanish, English or French** — and the language switch changes both halves at
once. The editor's own words come from one catalogue; the game's words — every NPC, monster, item
and line of dialogue — are read straight out of the client's `Content/I18n/{lang}.bin`, 339,342
texts per language. The format is not documented anywhere; it was worked out and then checked
against `world.db`, where 500 keys sampled at random came back byte for byte identical, including
one of 42,180 characters. An NPC is not called the same thing in Spanish and in French, and a
dialogue tree built against one set of names has to be readable from the other.

**The creatures are drawn**, out of the client's own bundles and nothing copied into the
repository. Monsters come from a picto atlas, 5,130 of the 5,134 covered. NPCs are assembled the way
the client assembles them: bones, a still frame, and the skins the look names — `AnimStatique_1`
holds exactly one frame in 47 of 53 bones measured, so there is no animation to play. The humanoid
rig is put together from its skins and tinted by the look's own colours.

- ✅ **Overview** — which files it read and what came out of each. First screen on purpose: every
  time something has gone wrong for an hour here, it turned out to be reading a different file than
  anybody thought
- ✅ **Traffic** — the client-server conversation, live as it happens and back through the log,
  every frame opened and read **against the protocol the client itself declares**. That is the
  difference between a hex dump and a reading: a length-delimited field could be a string, a nested
  message or a blob, and with `string fytl = 3` in front of you there is nothing to guess. From here
  a packet can be named on the spot, from the **513 real message names** the client still ships in
  its metadata — so naming one stops being invention and becomes a choice off a closed list
- ✅ **Packets** — every kind of packet seen, with a status ladder — unknown, named, documented,
  handled, ignored — a name and notes. Only *unknown* and *handled* can be worked out by code;
  everything between them is a person's work, and until now it had nowhere to live but a Discord
  message
- ✅ **NPCs** — all 422 placements, with the provenance column and the NPC drawn on the map. Pick
  one, click a cell, turn it round, take one of Ankama's away, or put a moved one back where the
  captures had it. Clicking a placement says what it *is*: what it does, what it says, what it
  sells. What gets saved is the **difference** from what the captures measured, never a copy
- ✅ **Dialogues** — which reply leads to which line, with the text on screen rather than ids. Both
  lists are always full, because what an NPC *can* say is known and hiding it behind a drop-down
  made the screen look broken. A reply can lead to another line, and picking a line that is not in
  the tree yet puts it in. A reply's words cannot be typed — the client draws them from its own
  catalogue by id — but any of the game's own lines can be borrowed, and there are 55,037 of them
- ✅ **Monsters** — open a group that is already there, take a monster out, put another in, move it
  two cells left. Opening is free; the moment anything changes, Ankama's group is written off and an
  authored copy takes its place. The picker says which monsters have **no spells at all**: 371 of
  the 5,134, and one of those joins a fight, takes its turn and does nothing
- ✅ **Spells** — every spell with its effects, and the map showing **how far it reaches and what it
  would hit**, worked out by calling the fight engine's own `Zone.Casillas` rather than a drawing of
  it. And the column that matters: whether the engine can actually apply each effect
- ✅ **Passages** — two maps side by side, a door picked on each, and one button that joins them
  **both ways**. A passage can only hang off an interactive element the map already has — the client
  draws those from its own map data — so the screen offers what is there and refuses to put a door
  where there is nothing to click. 4,038 door-shaped elements are sitting unused across 2,469 maps
- ✅ **Map cells** — the three layers painted one at a time, click to toggle and **drag to paint a
  run**, with a compass that jumps to the four maps next door. What is saved is the changed cells,
  never all 560
- ✅ A section that fails shows its error *inside* the editor instead of taking the window down, and
  `Jondo Studio.exe --selftest` builds all nine in all three languages against the real data and
  fails the publish if any throws

**Everything it writes goes to `content/`**, in versioned text. Nothing opens `world.db` for writing
and nothing talks to a running server. What is observed stays where it was observed and only the
conclusions are written down, because those are the part no tool can reproduce.

### What building it turned up

An editor that shows you what the server believes is also an editor that shows you when the server
is wrong. Four things it found, all measured:

- **The unknown-packet registry had been recording nothing for months.** It opened frames with a
  helper that only looks at root field 3, and client frames sit at root field 2 — so after weeks of
  play the table held two rows, both with no opcode over an empty body. 8,974 of the 72,879 frames
  in the log are the client's, and every one had gone in blank
- **Every passage declares the wrong skill.** All 3,815 say 114, which is *Utilizar* on a zaap.
  Ankama's own world graph uses **184** on 5,629 of 5,719 interactive transitions and 114 on none;
  across 401 captures, 184 shows up on 420 elements and 114 on 23, every one a zaap. Our own log
  catches us emitting the pair `(type 0, skill 114)` 84 times — a pair that occurs zero times
  anywhere real. New passages declare 184
- **647 of the game's 872 effects do nothing at all.** They are drawn on the spell card and the
  engine has no code for them and no characteristic to apply — and **15,841 of the 34,823 spell
  levels carry at least one**. The Studio ranks them by how many levels each one breaks, which turns
  a curiosity into a work list: effect 1160 alone is on 6,709 levels, and healing — effect 108 — is
  on 751
- **A monster's picture is filed under its `gfxId`, not its id.** Keyed by id, 847 of 5,134 found a
  picture and every one of those 847 was *somebody else's* creature. Keyed by gfxId it is 5,130

### What is being worked on

- 🚧 **NPC actions per placement** — and this one turned out not to be what the plan assumed. The
  right-click menu is drawn by the *client* from the template's `actions[]`, so an action written
  per placement can only take options away, never add one. Adding would need the map-load packet to
  carry actions per actor, and that has to be measured against a capture first
- 🚧 **Editing spells.** The simulator is there; changing a spell's numbers is not — and with 647
  effects doing nothing, implementing the top of that list buys far more than editing the values of
  an effect the engine ignores
- 🚧 **Shops, loot tables and dungeons** — asked for by the people using it. All three are screens
  over data the server already reads, so they are reachable
- 🚧 **Quests** — a project of its own, and the one everybody wants. There is no quest engine in the
  server and no per-character progress table, so an editor today would write files nobody reads
- 🚧 **A thin admin channel** so a running server can be told to reload one domain, without a
  restart. Localhost only, token per boot, off by default
- 🚧 **The launcher**, which inherits this shell once the editor stops moving

Two things that will not be done the way the plan assumed. The **decor** — the couple of thousand
drawn elements on a map — stays out: it is a project of its own and the editor paints what the
server believes, not what the map looks like. And **NPC animation** is not needed at all: the still
frame *is* the whole animation.

One correction, since this file carried the wrong number for a while. The claim that **1,010 of
1,124 missing passages were discarded for having no return element** is not reproducible from any
code in this repository — the extractor's own counter says 1,357 of 3,644 — and the rule behind it
was doing something else: the return element was never a requirement, it was used to *guess* where
the passage put you down. That guess is wrong 96.9% of the time.

The full plan, with what was decided and why, is in **`docs/world-editor.md`**.

---

## 🧪 Tests

`Jondo.Unity.Tests` — **256 xUnit tests**, grouped by domain: `Content`, `Combat`, `Economy`,
`Protocol`, `Security`, `Sessions`, `Studio`, `World`. They run in about four seconds.

Five of them run against `logs/gameserver_traffic.log` itself when it is on the machine, and skip
when it is not. That is a real weakness — a test that skips proves nothing — and it is there because
of what it caught: the packet registry it replaces was fed real frames for weeks and wrote down two
useless rows, while every test built out of frames this project constructed passed. Code doing what
it was written to do says nothing about whether it was written to do the right thing.

```bash
dotnet test Jondo.Unity.Tests
```

**Publishing the server runs them first and fails if any is red.** Not on build — the inner loop
stays fast and a half-written change can still be compiled to see whether it type-checks — but
publishing is the one step between writing code and a player running it, and it is already the step
that copies the executable to the root. The escape hatch is `-p:SkipTests=true`, which leaves its
trace on the command line rather than in a config file nobody reads.

### Two kinds of check, two homes

Some checks also run **at startup and throw**, so a bad build refuses to boot. That is worth paying
only for what a green test run cannot know:

* **At startup** stay the questions of the form *"is the data I was shipped sane?"* — the fight
  sheet's 53 characteristics in their captured order, the interactive registry, the monster
  spellbooks, the vendor placements, the profession catalogue. `datos/` and `world.db` are
  regenerated by tooling and redistributed compressed, entirely outside the build, so a bad
  regeneration reaches a player with every test still passing.
* **In the test project** live the questions of the form *"is this code correct?"* — the content
  layers, the collision damage formula, the Jondo Coin bands, frame limits, protobuf parsing,
  password hashing, log censorship and session isolation.

---

## 🔎 Surviving the next patch

Every protobuf message in Dofus 3 is named with three random letters — `kub`, `jru`, `lqu` — and on some patches Ankama reshuffles the lot. Nothing else about the protocol changes shape, but the emulator no longer knows what anything is called. **`protocolbuilder`** is the command line for that; **`Jondo Desofuscador.exe`** is the same engine behind one window and one button.

Eight consecutive real clients (3.6.4.3 → 3.6.10.10) were pulled from Ankama's own CDN and compared patch by patch:

- **Ankama does not reshuffle on every patch.** Three of the seven jumps keep all 2,169 names, one for one — five obfuscation generations across eight versions. The tool checks for the identity mapping first, in a second.
- **Zero wrong pairings over 6,505 real pairs.** The matcher never looks at names, only at field numbers, kinds and neighbourhood. It gets 71.1% and misses none; what it cannot decide, it leaves alone.
- **On a patch that does reshuffle, structure alone gets about 11%** — the ceiling, not a tuning problem: distinctive fingerprints collapse from ~750 to ~70, because a many-field message is both the distinctive one and the one most likely to be touched.
- **Chaining through intermediate versions is worse**: 12 pairs against 245 for the direct jump. A plausible idea the measurement refuted.
- Building the `Op` layer also turned up **49 opcodes that only exist in 3.6.4.3** — dead code nobody knew about.

The **`Op` layer** replaced **495 three-letter literals across 35 files** with one generated file, `Jondo.Unity.Protocol/Op.cs`, so applying a mapping never means editing the emulator by hand.

```bash
protocolbuilder mapear <old client> <new client>       # who is who between two versions
protocolbuilder capa   <client> <anchors> . --aplicar  # regenerate Op.cs and migrate call sites
protocolbuilder bajar  3.6.4.3 3.6.10.10 clientes      # fetch old clients from the CDN, 183 MB each
protocolbuilder cadena clientes                        # measure each patch on its own
```

Full write-up in `docs/desofuscacion.md`.

---

## 🧱 Source layout

The two executables:
* **`Jondo.Unity.Server`** → `Jondo Server.exe` — proxies, network parser, handlers, managers, database and the server's log window. The spell effect engine lives in `Managers/`: `SpellEffects` reads the spell data, `EffectEngine` turns it into things that happen to somebody, and `Summons` builds summoned fighters from monster templates.
* **`Jondo.Unity.Launcher`** → `Jondo Emulator Launcher.exe` — the player's window. References the contract and nothing else.

Shared:
* **`Jondo.Unity.Contract`** — paths, settings and the launcher's drawn-from-code widgets
* **`Jondo.Unity.Core`** — networking infrastructure and TCP servers
* **`Jondo.Unity.Auth`** — authentication and HAAPI handlers
* **`Jondo.Unity.Protocol`** — message definitions and the generated `Op` layer
* **`Jondo.Unity.World`** — world logic, `FightInstance`, buffs and states (`Buff`), area shapes and displacement (`Zone`), isometric geometry (`MapGeometry`)
* **`Jondo.Unity.Parser`** — capture parsing
* **`Jondo.Unity.Studio`** → `Jondo Studio.exe`. The world editor, in Avalonia. References
  `Jondo.Unity.World` and `Jondo.Unity.Core` as projects and uses `MapGeometry`, `Fighter` and
  `SpellEffect` directly — that absence of a serialisation boundary is why it is a desktop app and
  not a local web UI. The content layers themselves live in `Jondo.Unity.World/Content/`
* **`Jondo.Unity.Tests`** — 116 xUnit tests, and the gate on publishing

The protocol toolchain, which the emulator does not depend on:
* **`Jondo.Unity.Reversing`** — reads a client with Cpp2IL, rebuilds the `.proto`, matches two versions, indexes the code, downloads old clients from the CDN (`Cytrus`) and generates the `Op` layer (`Layer`)
* **`Jondo.Unity.ProtocolBuilder`** → `protocolbuilder` · **`Jondo.Unity.Deobfuscator`** → `Jondo Desofuscador.exe`
* **`JondoFix`** — the MelonLoader client mod, source plus the compiled dll

Documentation, all of it measured rather than assumed — index in `docs/README.md`. Start with `docs/protocol.md` (how a message travels), `docs/opcodes.md` (what each opcode means and where it was seen), `docs/fight.md` (a fight on the wire, opcode by opcode) and `docs/desofuscacion.md` (surviving a patch).

---

## 💾 Database and persistence

Three **SQLite** databases in `bases/`, and one folder of text:

* **`world.db`** — 41 tables and 659,397 rows: characters, inventories, positions, map persistence, spells, monsters, appearances, wardrobe and haven bags. Distributed compressed as `datos/world.zip` (24.8 MB) and extracted on first run.
* **`auth.db`** — accounts and authentication sessions, created on first run.
* **`paquetes.db`** — the packets the server does not yet know how to answer, deduplicated by protobuf shape. Kept apart from the other two on purpose: it carries nothing needed to play, it can be deleted to start over, and it can be handed to somebody else to look at without handing over anybody's characters.
* **`content/`** — the authored layer, in versioned JSON. The only one edited by hand, and the only one nothing regenerates. See [Jondo Studio](#-jondo-studio).

Files are looked up in `datos/`, then `bases/`, then the root, so a half-moved installation still starts.

**Some regression guards also run at startup and throw**, so the server refuses to boot when the data it was shipped does not match what the code expects — see [Tests](#-tests) for which checks live where, and why.

---
<img width="2559" height="1499" alt="image" src="https://github.com/user-attachments/assets/3b4f1f39-45d3-4efe-b73b-65d1d5e8a595" />
<img width="2559" height="1509" alt="image" src="https://github.com/user-attachments/assets/dde87296-dd2a-498a-b058-1491160b7d04" />
<img width="2559" height="1506" alt="image" src="https://github.com/user-attachments/assets/521bef24-6b19-4061-bc5b-37a178e91163" />
<img width="2559" height="1500" alt="image" src="https://github.com/user-attachments/assets/0f06761a-7dcf-481e-b045-02efce31c58e" />
<img width="2559" height="1500" alt="image" src="https://github.com/user-attachments/assets/60b113e4-3415-435f-8bc4-738e8efbfc2a" />
<img width="2559" height="1499" alt="image" src="https://github.com/user-attachments/assets/6faa6737-b04b-4cba-986f-3046ff2b4f2a" />
<img width="2559" height="1488" alt="image" src="https://github.com/user-attachments/assets/aa2249c3-699d-4137-aeef-96fc2278fcf2" />
<img width="2559" height="1497" alt="image" src="https://github.com/user-attachments/assets/33829fde-d8f1-4b5e-a3f1-11e34fd8c4ca" />
<img width="2559" height="1493" alt="image" src="https://github.com/user-attachments/assets/86a0b6e6-ea31-45a3-b381-4ba4fcc6b043" />
<img width="2559" height="1503" alt="image" src="https://github.com/user-attachments/assets/7c2aec0c-85a5-497b-9e1f-db4b77697605" />
<img width="2559" height="1508" alt="image" src="https://github.com/user-attachments/assets/cb587972-a7c5-42cd-a1e2-c1567cecccc8" />
<img width="910" height="929" alt="image" src="https://github.com/user-attachments/assets/00b35bbe-7356-41d0-ba9a-d079fbc7165f" />
<img width="2559" height="1493" alt="image" src="https://github.com/user-attachments/assets/cb75bca8-358d-4153-a2e6-955c10be92f9" />
<img width="2559" height="1511" alt="image" src="https://github.com/user-attachments/assets/38c437da-d881-4d64-b2b4-0348c789a9a3" />
<img width="2559" height="1503" alt="image" src="https://github.com/user-attachments/assets/95591e2a-f99d-4f66-b8f5-1f0c24ccf548" />
<img width="2559" height="1480" alt="image" src="https://github.com/user-attachments/assets/4d17a777-6839-4ed0-9aac-38768159e4ac" />
<img width="1403" height="1153" alt="image" src="https://github.com/user-attachments/assets/a22d551f-6dec-4147-b821-f6a8c5c7e721" />
<img width="1003" height="824" alt="image" src="https://github.com/user-attachments/assets/82b10866-3f7f-4e79-83fb-f96331066fd7" />
<img width="805" height="1021" alt="image" src="https://github.com/user-attachments/assets/bcbf1292-0474-4279-ab0d-9da0bf2b7ea4" />
<img width="2559" height="1503" alt="image" src="https://github.com/user-attachments/assets/c86bc15b-5bcd-4487-aa3d-391df8be93c0" />
<img width="2559" height="1515" alt="image" src="https://github.com/user-attachments/assets/dd60b531-4b3e-4347-a866-26ecb36046d4" />
<img width="2559" height="1500" alt="image" src="https://github.com/user-attachments/assets/7f0406c5-34c0-46b9-8cf7-fe14913f70e0" />
<img width="2559" height="1504" alt="image" src="https://github.com/user-attachments/assets/6b934f0d-40b7-4a3e-9926-5df97bf9c484" />




