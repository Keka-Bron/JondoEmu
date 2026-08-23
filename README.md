High-performance server emulator for **Dofus 3 Unity (Client 3.6.10.10)** written in C# (**NET 10**), architected with decoupled modular projects, a SQLite database layer, and a playable PvM combat engine with a data-driven spell effect system.

> ⚠️ **Compatibility Notice**: This emulator strictly requires **Dofus 3 Client Version 3.6.10.10 (mid August 2026)**. It is **NOT compatible** with newer or latest versions of the official Dofus client due to underlying protocol changes.
>
> Ankama renames every protobuf message to three random letters on some patches, which is what breaks compatibility. There is now a toolchain in this repository for surviving that — see [Surviving the next patch](#-surviving-the-next-patch). It does not make the emulator version-agnostic; it makes the migration measurable instead of guesswork.

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

What JondoFix does:
* **Network redirection** — intercepts sockets, Named Pipes and DNS queries and sends them to `localhost` (ports `8888`, `5555`, `15881`, `6337`).
* **SSL bypass** — stops HTTPS requests from failing against the local self-signed certificate.
* **Environment configuration** — injects the variables the client expects (`ZAAP_PORT`, `ZAAP_HASH`, and so on).

### Step 3 — Run it

Double-click **`Jondo Emulator Launcher.exe`**. That is still the only thing you start by hand, but there are now **two executables**: the launcher is the window you use, and it starts **`Jondo Server.exe`** by itself. They were one program until the split; keeping them apart means the launcher you hand to a player carries no database, no maps, no protocol handlers and no effect catalogue — it references the shared contract and nothing else. The server has its own window with the log and the counters.

On the first run it unpacks `datos/world.zip` into `bases/world.db` (about 240 MB, it takes a moment) and creates `bases/auth.db` with a test account. Sign in to add an account to the launcher's team, select one or several saved profiles, then press **Launch selected**. Up to eight independent Dofus clients can be active at once. Create your account in the launcher or use the test account as follows:

Account: keka
Password: test

By default the emulator looks for the client next to itself, in a `Cliente 3.6.10.10` folder beside
the emulator folder. If yours lives somewhere else — another drive, another name — click the path
row under the play button and point it at your `Dofus.exe`. The choice is remembered, and if the
client later moves the launcher says so instead of failing silently.

The **ES / EN / FR** buttons in the top bar set the language of the launcher *and* of the game: the
client is started with that `--langCode`. The path row shows which one is in effect.

---

## 📂 What you get

The root deliberately holds almost nothing — the launcher and little else. Everything is inside folders:

```
Jondo Emulator Launcher.exe   ← this is what you run
Jondo Server.exe              the server; the launcher starts it, you never launch it yourself
datos/                        json and bin the emulator reads (maps, items, appearances, zaaps…)
bases/                        world.db and auth.db, the only things the emulator writes
docs/                         technical documentation
launcher_assets/              launcher artwork and music
JondoFix/                     the MelonLoader mod, source and compiled dll
Jondo.Unity.*/                source code
```

Some folders are **not** in the repository because they are not needed to play, and appear on your machine as you use it: `bases/` (the databases, built on first run), `logs/`, `tools/` (the Python that regenerates the files in `datos/`) and `dofus3_data/` (436 MB of raw client dump, only used by those tools).

---

## 🚀 Emulation Status

### 🖥️ Custom Launcher
- [x] **Native WinForms interface**, drawn from code, with its own theme, artwork and background music.
- [x] **Account creation and login** from the launcher itself, written straight to `auth.db`.
- [x] **Persistent team of up to eight accounts** — add profiles once, select any subset or all of them, and launch one independent Dofus process per selected account.
- [x] **Per-client identity chain** — unique instance id, launch hash, Zaap game session, game token, single-use connection ticket and socket-owned game session.
- [x] **Independent lifecycle indicators** — selected profiles, active client processes and connected game sockets are tracked separately; closing one client does not alter the others.
- [x] **Embedded server log** so you can watch traffic and errors without a console window.
- [x] **Single-file deployment** — the twelve dependency DLLs travel inside the executable; the folder stays clean.
- [x] **Multilanguage**.
- [x] **Launcher and server are separate programs** — `Jondo Emulator Launcher.exe` and `Jondo Server.exe`. The launcher starts the server itself, and carries none of it: no database, no maps, no protocol handlers, no effect catalogue. The server keeps its own window with the log and the counters.

### 🌍 World & Connection
- [x] **Client / Server / Authentication emulation** (Zaap, HAAPI, Connection Server, with the VIP subscription check bypassed).
- [x] **Server selection and character selection**, showing the mount the character is riding.
- [x] **Character creation** with a starter kit: Astrub zaap as the spawn point, adventurer set, 1,000,000 kamas, level 1 and 101 scrolled points per characteristic.
- [x] **World loading**, character spawn and name hover.
- [x] **Movement, map change, map loading and adjacent maps** across **15,360 maps**, with **17,211** of them carrying walkable-cell data.
- [x] **Last cell and map persistence** in the database.
- [x] **Auto-pilot** — double-click on the minimap or the *travel to* option.

### 🌀 Zaaps, Zaapis and Anomalies
- [x] **62 waypoints** mapped from the client data, with their map, cell and sub-area, plus the three departure-only zaaps the waypoint table does not list (the guild reception room among them).
- [x] **Travel between zaaps** with the real cost and destination list.
- [x] **Discovered zaaps** announced on world entry. The client shows *nothing at all* without that list — it is `hjk`, 45 map ids packed into one message, and its absence is why the travel window used to read "No destination".
- [x] **Zaapis** of Bonta (24 destinations) and Brakmar (21), flat 20 kamas. Their destination tables cannot be derived from client data — four of every six destinations have no zaapi of their own — so they were read off the captures.
- [x] **The right window.** The `hjj` carries a root field that decides which window the client opens: 0 zaap, 1 zaapi, 3 boat. Measured across all twelve captured lists without exception.
- [x] **Temporal anomalies**, the tab beside the zaap list: 16 of them, each with the real 120-minute countdown. The 15 "unactivated" waypoints turned out not to be switched-off zaaps but **vestiges**, type 359, where an anomaly surfaces — and from a vestige only anomalies are offered.
- [x] The alliance-temple waypoint, a special case: the temple map has four interactive elements and only one is the waypoint, so it carries an explicit override (`datos/zaap_overrides.json`) instead of being guessed.

### 🏘️ Houses
- [x] **1,437 doors on 553 maps**, all enterable, all ownerless.
- [x] **261 house models** extracted from the client — name, price, room count — from 1M kamas up to the 60M, 15-room *Palacio de los lagos*.
- [x] **Entering and leaving**, which are different messages: you go in with `jqw` and come out with `jru`, and the map id sits in a different field in each. You come out through the door you went in by.
- [ ] The house **plaque** — owner, price, for-sale — is not sent yet. Across 34 capture folders there are 1,276 plaques and every one has an owner, so what an ownerless house looks like on the wire is still unmeasured, and `jss` is not the message to guess in.
- [ ] House chest, access code, buying and selling.

> Which house sits behind which door is **not in the client** — checked three ways. The 1,437 doors share **114 genuine interiors**, and Jondo assigns each door one of them deterministically, keeping it inside its own neighbourhood where it can. Sharing an interior is not a bug: after a server merge there were fewer houses than players, so in the real game one interior serves many owners, each with their own separate instance. The mapping lives in `datos/casas_mundo_3.6.10.10.json` and can be corrected by hand.
>
> An earlier version picked interiors with a blocklist over the 2,377 maps at (0,0) and sent players into public workshops — a door in Astrub opened onto a Pandala forge, and the forge acted as the way out. Interiors are now taken from an **inclusion list**: the residence sub-areas, and nothing else.

### 🗑️ Bins
- [x] **67 public bins in 63 maps**, recognised by their four graphics. They open, show empty and close.
- [ ] Putting items in and taking them out: that needs a store per bin, and the haven-bag chest's switch would send them to your own house instead.

### 🏠 Haven Bags (Merkasako)
- [x] **Entering and leaving** from the outside, and the haven bag's own zaap.
- [x] **48 themes**, switchable and persisted.
- [x] **4,083 pieces of furniture** available in the decoration editor, with placement saved to the database.
- [x] **Chest** with the full item flow — putting in, taking out, and persistence across sessions.
- [x] **Lottery machine**, unlimited, handing out items with overpowered rolled stats.
- [x] **No monsters spawn inside**, unlike the rest of the world.

### ⛏️ Gathering professions
- [x] **25,090 resources on 4,507 maps** across the six gathering jobs. The client knows where every element is and which graphic it uses, but **not what it is** — the type and the skill come from the server. Crossing 305 captures with the client dump yields graphic → (type, skill), and from the skill the client catalogue gives the job and the item you get.
- [x] **The three states** — full, depleted, busy — declared the way the client expects them. The skill moves field: it travels in `f4` when the resource can be used and in `f3` when it cannot. Verified on 25 ash trees of one map without a single exception.
- [x] **Job levels and experience**, persisted per character, with the real curve (`10 × level × (level − 1)`).
- [x] **What you gather lands in your inventory**, and the amount grows with your job level over the resource's own level, so levelling up really does pay.
- [x] **Too low a job level blocks gathering** the way the game does it: the tool icon turns red on hover, exactly like a depleted resource. No chat line — see below.
- [ ] Crafting professions: workshops, recipes and the craft window.

### 💬 Information messages, level-up and private chat
- [x] **Information messages the right way.** "Last connection…", "You gained 320 kamas", "You do not have the required job level" are **not text from the server**: the server sends a number and the client prints its own translated string. That is `lqn { type, message, parameters }`, resolved against the client's 2,555-entry table. Sending those as chat lines instead publishes them on the **general channel for everyone on the map** to read — which is what used to happen.
- [x] **The level-up window** — the music, the animation and the summary, on a real level gain and on `.level` in either direction, showing the destination level's numbers.
- [x] **Private messages** between characters. A whisper is not a chat channel: it has its own message, `kth`, and the client routes it by opcode, not by channel number. Sending it as a channel-9 chat line arrives, is accepted, and paints absolutely nothing.
- [x] **Last connection time and IP**, stored per character and shown on every world entry.

### 👥 Parties
- [x] **Invite, accept, refuse, leave, hand over the lead and kick**, measured against six captures taken from **both sides** — inviting and being invited are not the same messages.
- [x] **Kicking** is the one that is not in any capture: across all 34 folders nobody ever throws anybody out. The request was read off the running client — `ili { party, who }` — and the answer is built only out of messages that *are* measured: the kicked player gets the same `ils` they would get by leaving, and the rest get the party again. Kicking someone who has not answered the invitation yet withdraws it instead.
- [x] Two things that mislead: you **invite by name and accept by party id**, and the party is **created when you invite**, before the other player answers, which is why a one-member party arrives at once and dissolves by itself on a refusal.
- [x] **A full member sheet** — name, level, sex, look, breed, map position, life, prospecting and initiative. The client sorts the panel by initiative. Position is checked against the database: the four maps in the captures give exactly the coordinates and sub-areas the messages carry.
- [x] **If the leader leaves**, the lead passes to the next member; **if someone disconnects**, they leave the party and the rest are told. Neither is in any capture, and without them a party with real people breaks in a minute.
- [ ] The **Details** button of the invitation popup does not answer yet (`imd` → `ilb`).
- [ ] The dedicated *a member is gone* message. The client has a handler for `inc`, so it exists, but no capture contains one and the generated `.proto` gets field numbers wrong often enough not to trust it. Until it is measured, the remaining members are simply sent the party again.
- [ ] Party search, party fights and following the leader across maps.

### 👕 Appearances (Cosmetics)
Dofus does not ship the item-to-look table in its client data: the server sends it. Every pair below was **measured off real packet captures**, one garment at a time — **2,371 of the 2,420 cosmetics in the catalogue**.

| Type | Working | In catalogue |
|---|---:|---:|
| Appearance shields | 524 | 524 |
| Appearance hats | 464 | 464 |
| Appearance capes | 357 | 357 |
| Appearance pets | 242 | 242 |
| Appearance weapons | 194 | 194 |
| Appearance petmounts | 151 | 151 |
| Appearance mounts | 121 | 121 |
| Shoulders | 121 | 121 |
| Costumes | 92 | 92 |
| Living objects | 61 | 61 |
| Wings | 44 | 44 |
| Miscellaneous appearance objects | 0 | 49 |

Details worth knowing:
- **Hats, capes, shields, shoulders, costumes and wings** each push one skin into the look. Three of them push two: cape 18579, shield 13240 and costume 18525.
- **Appearance pets** hang off the look as a sub-entity with their own bones, scale and colour. Forty-four of them take the wearer's own tint rather than carrying a palette.
- **Petmounts and appearance mounts** take over the root of the look, replacing the mount you are riding — bones, scale, colour, and in the case of appearance mounts a skin of their own.
- **Appearance weapons** carry no look at all, and that is not a gap: the client draws them by itself and they only show while animating. What the server has to remember is which of the **ten weapon slots** each one occupies — one per real weapon type: bow, wand, staff, dagger, scythe, axe, spear, hammer, shovel and sword.
- **Living objects** imitate a different garment depending on the variant you pick, so they are stored as **543 object/variant pairs** across ten different slots. Those that land on the amulet, ring, belt and boot slots carry no skin, because Dofus never draws those on the character.
- **Mount and pet appearances are mutually exclusive**, matching the real server: while mounted the pet shows in the appearance window preview but never reaches the map.

### 🎖️ Titles and Ornaments
- [x] **539 titles** and **167 ornaments** offered, the complete catalogue the client knows about.
- [x] Applied and persisted per character, and carried inside the map actor block so they show on hover.
- [x] Verified against real captures: of the 476 titles and 166 ornaments the official server was seen accepting, every single one is in the list.

### 🎒 Inventory and Character
- [x] **Inventory system** — item spawning, equipping and unequipping, item bags, destruction, and persistent storage over **21,748 item templates** and **66,294 item effects**.
- [x] **929 item sets** with their bonuses.
- [x] **Mounts** — **520 of them** with their look, correctly swapped and unequipped.
- [x] **Character stats** — characteristic assignment is fully functional; all stats map correctly, capital is dynamic, and remaining points stay in sync across every client panel including the left sidebar HUD.
- [x] **Spells and spell variants** — **17,113 spells** across **34,823 spell levels**.
- [x] **638 character heads**.

### 👹 NPCs, Monsters and Dungeons
- [x] **NPCs** — **6,468 templates** with spawns, 3D looks and dialogue trees.
- [x] **Monsters & mobs system**, complete:
  - **Dynamic map spawning and respawner** — 2 to 4 mob groups per map, kept populated, over **38,744 mapped groups**.
  - **Sub-area aware spawning** — a map is populated with monsters that actually belong to its sub-area, across **562 sub-areas**.
  - **Level and grade management** with correct experience calculation.
  - **3D looks and skeleton system** — native Protobuf bone models, custom scales and textures for **5,134 monsters**, quest monsters and archmonsters included.
  - **Multi-monster groups** of 1 to 8.
  - **Radius-2 cell validation** (`GetInnerWalkableCells`) so mobs never spawn on decorations, walls, house windows or zaap pillars.
- [x] **187 dungeons** with their **763 rooms**, entrance and exit.

### ⚔️ PvM Combat
Playable. The migration from 3.6.4.3 is done and fights are no longer gated.

- [x] **Tactical arenas** — each roleplay map resolves to its combat arena by zone offset.
- [x] **Context transitions** — clean switching between roleplay and tactical combat, restoring world state when the fight ends.
- [x] **Placement phase** — red and blue placement tiles with cell swapping before *Ready*.
- [x] **Isometric grid geometry** (`MapGeometry`) using a pre-computed $O(1)$ BFS distance matrix.
- [x] **Line of sight** — obstacle validation extracted for **17,222 maps**, tracing segments between cell centres.
- [x] **Turn protocol and timers** — handshake, 30-second turns with automatic pass, AP/MP replenishment.
- [x] **Movement** — cell-by-cell path expansion, MP charged per tile, and collision against occupied cells.
- [x] **Active monster AI** — target selection by lowest HP, HP percentage, isolation and distance; ranged attacks; minimal-MP BFS pathing; and fleeing below 30% HP.
- [x] **Fight resolution and progression** — victory and defeat screens, experience over **1,889 levels**, official loot drops, level ups and group respawns.

### ✨ Spell effect engine
One engine for all eighteen classes, driven entirely by client data. There is not a single spell
written by hand: everything comes out of `SpellLevels.EffectsJson` and the `Effects` catalogue.

- [x] **Effects, triggers and target masks** read from the spell itself — `I` on cast, `TB` at turn
      start, `TE` at turn end, `DBE` when hit, `CCMPARR` per tile walked. `a` are allies, `A` are
      enemies, `g` are summons, and `E<n>` / `e<n>` gate on a state.
- [x] **States** need no code: effect 950 sets a number, 951 clears it, and the masks do the rest.
- [x] **Area shapes** from `zoneDescr` — point, circle, cross, line, diamond, square and whole-map,
      with the per-tile damage falloff each spell declares (10% per tile, capped at four steps, on
      the spells that carry it).
- [x] **Displacement** — push, pull, step back and step forward, with the direction taken from the
      centre of the area, stopping at walls, holes and other fighters.
- [x] **Criticals** — rolled against the spell's own probability plus the character's, using the
      spell's separate critical effect list (Frozen Arrow goes from 21-24 to 25-29).
- [x] **Point steal**, life steal, erosion of maximum HP, and damage-taken multipliers.
- [x] **Buff panel** — every effect reaches the client's *Effects* window with its icon, value,
      remaining rounds and dispellable flag; buffs expire on their round and are retracted.
- [x] **Cooldowns and cast limits** — per turn, per target, minimum interval and initial cooldown.
- [x] **Summons** — real fighters, not buffs: their own sheet, a place in the turn carousel next to
      their owner, their own behaviour spell, a lifetime, and they all fall when their summoner
      dies. Weighted random picks the variant (80% Arakna / 20% Greater Arakna), and the summon cap
      comes from the character's own characteristic.
- [x] **Item attitudes** — the six Dofus and the trophies grant their spell through effect 1175 and
      hook into the same triggers.

Everything above was measured against real packet captures and is covered by an offline harness
that compares the emulator's bytes against the capture's, packet by packet.

### 🏹 Class status
The engine is shared, so every class gets whatever its spells happen to use. Only the **Cra** has
been driven against real captures spell by spell; the rest are untested and listed for honesty.

| Class | Spells with every effect applied |
|---|---:|
| **Cra** (tested against 37 captures) | **17 / 44** |
| Enutrof | 26 / 44 |
| Osamodas | 21 / 44 |
| Feca, Iop | 20 / 44 |
| Pandawa, Eliotrope | 19 / 44 |
| Sacrier | 18 / 44 |
| Sram, Zobal | 15 / 44 |
| Ecaflip, Rogue | 11 / 44 |
| Steamer | 5 / 44 |
| Sadida, Ouginak | 4 / 44 |
| Eniripsa | 2 / 44 |
| Huppermage | 1 / 44 |
| Xelor | 0 / 44 |

A spell counts only when **all** of its effects resolve. The gaps are concentrated in a handful of
effect families — glyphs and traps, appearance changes, spell-effect removal — so they close in
blocks rather than one spell at a time.

### 🚧 Work in progress
- [ ] **Combat stat panel** — buffs feed the damage formula correctly, but the character sheet and
      the damage preview still show the pre-buff numbers: the per-characteristic sheet packet is
      measured and not yet emitted.
- [ ] **Glyphs and traps** (effects 400, 401, 1091) — the board entity exists for summons; these
      reuse it but are not wired.
- [ ] **Appearance-changing spells** — the transform payload is an opaque blob that has not been
      decoded, so the Cra's Sentinel works but does not change its look.
- [ ] **Commands** — `.teleport`, `.kamas`, `.shop`, `.size` and `.level` work; `.level` does not
      refresh the in-fight spell bar.
- [ ] **Weapon strikes** — damage and AP cost apply; the slash animation does not.

### ❌ Not implemented
- [ ] Kolossium and PvP combat
- [ ] AP/MP dodge rolls, shields, lock and tackle in melee
- [ ] Crafting professions (gathering ones do work — see above)
- [ ] Achievements
- [ ] Guilds
- [ ] Combat challenges — the fifteen opcodes are measured and the family is confirmed in the client's own dispatcher; nothing is wired yet.

---

## 🔎 Surviving the next patch

Every protobuf message in Dofus 3 is named with three random letters — `kub`, `jru`, `lqu` — and on
some patches Ankama reshuffles the lot. Nothing else about the protocol changes shape, but the
emulator no longer knows what anything is called. This is the single reason an emulator dies on
patch day, and there is now a toolchain for it.

**`protocolbuilder`** is the command line; **`Jondo Desofuscador.exe`** is the same engine behind one
window and one button. You give it the client you already knew and the one that just shipped, and it
answers with the mapping.

### What was measured

Eight consecutive real clients (3.6.4.3 → 3.6.10.10) were downloaded from Ankama's own CDN and
compared patch by patch. Some of it was surprising:

* **Ankama does not reshuffle on every patch.** Three of the seven jumps keep all 2,169 names, one
  for one. There are five obfuscation generations across the eight versions. When nothing moved, the
  mapping is the identity and there is no work to do — the tool checks this first, in a second.
* **Zero wrong pairings over 6,505 real pairs.** The matcher never looks at names, only at field
  numbers, field kinds and neighbourhood, so a message that keeps its name is an answer it could not
  have copied. It gets 71.1% of them and misses none. What it cannot decide, it leaves alone.
* **On a patch that does reshuffle, structure alone gets about 11%** — and that is the ceiling, not
  a tuning problem. The distinctive fingerprints collapse from ~750 to ~70, because a message is
  distinctive when it has many fields and a many-field message is the one most likely to be touched.
* **Chaining through intermediate versions is worse, not better**: 12 pairs against 245 for the
  direct jump. It was a plausible idea and the measurement refuted it.

The rest is what the anchors, the code index and a language model are for: the tool hands over the
ambiguous ones with their candidate list and everything already known about each, and never invents
an answer. Full write-up in **`docs/desofuscacion.md`**.

### The `Op` layer

A mapping is worthless if applying it means editing the emulator by hand. It used to mean exactly
that: **495 three-letter literals across 35 files**. They are now behind one generated file,
`Jondo.Unity.Protocol/Op.cs`, with a name per opcode and its meaning as documentation:

```csharp
ConnectionProtocol.Push(Op.HelloGameMessage, …)
case Op.BasicTimeMessage: …
```

Regenerating it after a patch is one command and the emulator is not touched. Building it also
turned up **49 opcodes that only exist in 3.6.4.3** — code that has not been able to match anything
for a long time and nobody knew.

```bash
protocolbuilder mapear <old client> <new client>    # who is who between two versions
protocolbuilder capa   <client> <anchors> . --aplicar  # regenerate Op.cs and migrate call sites
protocolbuilder bajar  3.6.4.3 3.6.10.10 clientes   # fetch old clients from the CDN, 183 MB each
protocolbuilder cadena clientes                     # measure each patch on its own
```

---

## 🧱 Source layout

* **`Jondo.Unity.sln`** — solution grouping every subproject.

  The two executables:
  * **`Jondo.Unity.Server`** → `Jondo Server.exe`. Proxies, network parser, handlers, managers,
    database management and the server's own log window.
  * **`Jondo.Unity.Launcher`** → `Jondo Emulator Launcher.exe`. The window players use. References
    the contract and nothing else — no database, no maps, no protocol.

  What they share and what they build on:
  * **`Jondo.Unity.Contract`** — what both executables agree on: paths, settings and the launcher's
    drawn-from-code UI widgets.
  * **`Jondo.Unity.Core`** — core networking infrastructure and TCP servers.
  * **`Jondo.Unity.Auth`** — authentication and HAAPI service handlers.
  * **`Jondo.Unity.Protocol`** — protocol buffers, message definitions, and the generated `Op`
    opcode layer.
  * **`Jondo.Unity.World`** — game node / world logic, combat engine (`FightInstance`), buffs and
    states (`Embrujo`), area shapes and displacement (`Zona`), isometric geometry (`MapGeometry`)
    and monster AI.
  * **`Jondo.Unity.Parser`** — capture parsing.

  The protocol toolchain, which the emulator does not depend on:
  * **`Jondo.Unity.Reversing`** — the whole of it with no face on it: reads a client with Cpp2IL,
    rebuilds the `.proto`, matches two versions, indexes the code, downloads old clients from the
    CDN (`Cytrus`) and generates the `Op` layer (`Layer`).
  * **`Jondo.Unity.ProtocolBuilder`** → `protocolbuilder`, the command line.
  * **`Jondo.Unity.Deobfuscator`** → `Jondo Desofuscador.exe`, the same engine behind one window.
* **`JondoFix`** — the MelonLoader client mod, source plus the compiled `JondoFix.dll`.

Documentation, all of it measured rather than assumed — see `docs/README.md` for the full index:

* **`docs/protocol.md`** — how a message travels: framing, the `Any` envelope and the opcode.
* **`docs/opcodes.md`** — what each three-letter opcode means, and where that was seen.
* **`docs/desofuscacion.md`** — surviving a patch: what rotates, what does not, and every number
  above with the method that produced it.
* **`docs/fight.md`** — how a fight is put together on the wire, opcode by opcode.
* **`docs/launcher.md`** — native team UI and the identity flow for up to eight client processes.
* **`docs/sessions.md`** — socket-owned game sessions, per-player state and map broadcasts.
* **`docs/appearances.md`**, **`docs/world.md`**, **`docs/data.md`** — cosmetics, world data and
  where every file in `datos/` comes from.
* **`docs/multijugador.md`** — historical migration plan and remaining multiplayer work.

The spell effect engine lives in `Jondo.Unity.Launcher/Managers`: `EfectosDeHechizo` reads the
spell data, `MotorDeEfectos` turns it into things that happen to somebody, and `Invocaciones`
builds summoned fighters from the monster templates.

---

## 💾 Database and persistence

Two **SQLite** databases, both in `bases/`:

* **`world.db`** — characters, inventories, positions, map persistence, spells, monsters, appearances, wardrobe and haven bags. Distributed compressed as `datos/world.zip`; the emulator extracts it by itself the first time it starts.
* **`auth.db`** — accounts and authentication sessions. Created on first run.

Files are looked up in `datos/`, then `bases/`, then the root, so a half-moved installation still starts.

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




