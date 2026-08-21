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

Choose `Dofus.exe` in the launcher and click **Install client support**, or simply launch an
account. The launcher silently installs the pinned official **MelonLoader 0.7.3 x64** package and
its bundled **JondoFix**. The download is accepted only after its SHA-256 matches the official
release asset pinned by this build; extraction is staged, paths are checked, existing files are
backed up, and a failed install rolls back. No external installer window is opened.

Manual installation remains possible: extract the official `MelonLoader.x64.zip` beside
`Dofus.exe`, then put `JondoFix/JondoFix.dll` in the client's `Mods/` directory.

> The mod ships **already compiled** and is the exact binary in use — you never need to build it. `JondoFix/` also carries its source, in case you want to read or change it.

Two things worth knowing afterwards:
* The installer drops a **`version.dll`** next to `Dofus.exe`; that is what loads MelonLoader. Renaming it to `version.dll.disabled` turns the whole thing off so you can play the official game, and renaming it back turns it on again — no need to uninstall anything.
* MelonLoader writes a log per run under **`MelonLoader/Logs/`**. If the client starts but never reaches the emulator, that file is the first place to look.

What JondoFix does:
* **Network redirection** — intercepts the Dofus service connections and sends them to the server domain/IP selected in the launcher (ports `8888`, `5555`, `5556`, `15881`, `6337`).
* **SSL bypass** — stops HTTPS requests from failing against the local self-signed certificate.
* **Environment configuration** — injects the variables the client expects (`ZAAP_PORT`, `ZAAP_HASH`, and so on).

### Step 3 — Run it

The server operator starts **`Jondo Server.exe`** on the server machine. Players start only
**`Jondo Emulator Launcher.exe`** on their PCs. The launcher never looks for, starts, or stops a
local server process. Use the server row in the launcher to enter the cloud machine's domain/IP or
its HTTPS reverse-proxy URL; the URL is used for account/control calls and its host is passed
privately to JondoFix for all redirected game services.

The server binds to loopback by default. A cloud host opts into network listeners with
`JONDO_PUBLIC_BIND=1`. Dofus must reach raw port 8888 for HAAPI, but in public mode the server blocks
remote launcher `/api/*` calls on that plaintext connection. Put the launcher control path behind a
loopback HTTPS reverse proxy and enter its public HTTPS URL in the launcher. A trusted-LAN-only
deployment can opt out with `JONDO_ALLOW_INSECURE_CONTROL=1`. Open only the native game ports the
deployment actually needs.

On Windows, the account running a public server needs a one-time URL reservation for the HAAPI
listener (run in an elevated terminal, replacing the account name):

```powershell
netsh http add urlacl url=http://+:8888/ user=DOMAIN\JondoService
```

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
Jondo.Electron.Launcher/      Electron + React launcher source and catalogue explorer
```

Some folders are **not** in the repository because they are not needed to play, and appear on your machine as you use it: `bases/` (the databases, built on first run), `logs/`, `tools/` (the Python that regenerates the files in `datos/`) and `dofus3_data/` (436 MB of raw client dump, only used by those tools).

---

## 🚀 Emulation Status

### 🖥️ Custom Launcher
- [x] **Electron + React interface**, using the Jondo artwork with a responsive player flow and a separate read-only client-data catalogue explorer.
- [x] **Account creation and login** from the launcher itself, written straight to `auth.db`.
- [x] **Persistent team of up to eight accounts** — add profiles once, select any subset or all of them, and launch one independent Dofus process per selected account.
- [x] **Per-client identity chain** — unique instance id, launch hash, Zaap game session, game token, single-use connection ticket and socket-owned game session.
- [x] **Independent lifecycle indicators** — selected profiles, active client processes and connected game sockets are tracked separately; closing one client does not alter the others.
- [x] **Embedded server log** so you can watch traffic and errors without a console window.
- [x] **Single-file deployment** — the twelve dependency DLLs travel inside the executable; the folder stays clean.
- [x] **Multilanguage**.
- [x] **Launcher and server are separate programs** — `Jondo Emulator Launcher.exe` is the player-side client and does not start or require a local server process. It connects to the configured domain/IP, while `Jondo Server.exe`, its databases and game data stay on the server machine.
- [x] **Client-data catalogue browser** — choose a pinned `client_data/3.6.10.10` snapshot to search its 204 extracted DataRoot catalogues and inspect exact JSON rows without changing a server database.

### 🌍 World & Connection
- [x] **Client / Server / Authentication emulation** (Zaap, HAAPI, Connection Server, with the VIP subscription check bypassed).
- [x] **Server selection and character selection**, showing the mount the character is riding.
- [x] **Character creation** with a starter kit: Astrub zaap as the spawn point, adventurer set, 1,000,000 kamas, level 1 and 101 scrolled points per characteristic.
- [x] **World loading**, character spawn and name hover.
- [x] **Movement, map change, map loading and adjacent maps** across **15,360 maps**, with **17,211** of them carrying walkable-cell data.
- [x] **Last cell and map persistence** in the database.
- [x] **Auto-pilot** — double-click on the minimap or the *travel to* option.

### 🚪 Interactives and Houses
- [x] **3,091 world-graph interactives** — criterion-free, single-target doors, stairs, ladders and passages are joined to their exact live map element and execute the destination encoded by the pinned 3.6.10.10 client graph.
- [x] **Persistent houses** — 674 supported exterior doors across 106 interiors, with saved owner, price and access policy, first-hand purchase, and the measured house enter/exit packet flows.
- [x] **Evidence precedence** — stale elements and exact world-graph keys are never reclassified from a reused zaap/house graphic. Conditional and multi-target graph routes remain disabled until their criteria can be evaluated correctly.

House placement and interior assignment are server-owned emulator data. The client provides 261
static house models but does not ship the official server's placements, owners or destinations;
those are not presented as native Ankama world state.

### 🌀 Zaaps and Zaapis
- [x] **62 waypoints** mapped from the client data, with their map, cell and sub-area.
- [x] **Travel between zaaps** with the real cost and destination list.
- [x] **Zaapis** and the alliance-temple waypoint, which is a special case: the temple map has four interactive elements and only one of them is the waypoint, so it carries an explicit override (`datos/zaap_overrides.json`) instead of being guessed.

### 🏠 Haven Bags (Merkasako)
- [x] **Entering and leaving** from the outside, and the haven bag's own zaap.
- [x] **48 themes**, switchable and persisted.
- [x] **4,083 pieces of furniture** available in the decoration editor, with placement saved to the database.
- [x] **Chest** with the full item flow — putting in, taking out, and persistence across sessions.
- [x] **Lottery machine**, unlimited, handing out items with overpowered rolled stats.
- [x] **No monsters spawn inside**, unlike the rest of the world.

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
- [ ] Professions
- [ ] Achievements
- [ ] Guilds

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
