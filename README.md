High-performance server emulator for **Dofus 3 Unity (Client 3.6.10.10)** written in C# (**NET 10**), architected with decoupled modular projects, a SQLite database layer, and a functional PvM combat engine.

> ⚠️ **Compatibility Notice**: This emulator strictly requires **Dofus 3 Client Version 3.6.10.10 (mid August 2026)**. It is **NOT compatible** with newer or latest versions of the official Dofus client due to underlying protocol changes.

---

## 🚀 Quick Start

**Nothing has to be compiled.** The launcher ships as a single ready-to-run executable with every dependency inside it, and the world database ships compressed and extracts itself on first run.

### Step 1 — Install the .NET 10 runtime

Download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). The *Desktop Runtime* is the one you want.

### Step 2 — Point the Dofus client at the emulator

The official client talks to Ankama's servers and checks their SSL certificates. **JondoFix**, a MelonLoader mod, redirects it to your machine instead. It comes already built in this repository.

1. Download the **MelonLoader** installer (`0.6.x` or newer, .NET 6 compatible) from [its releases page](https://github.com/LavaGang/MelonLoader/releases).
2. Run it, select your `Dofus.exe`, leave runtime detection on **IL2CPP / .NET 6**, and install.
3. Copy **`JondoFix/JondoFix.dll`** from this repository into the **`Mods/`** folder of your Dofus installation.

What JondoFix does:
* **Network redirection** — intercepts sockets, Named Pipes and DNS queries and sends them to `localhost` (ports `8888`, `5555`, `15881`, `6337`).
* **SSL bypass** — stops HTTPS requests from failing against the local self-signed certificate.
* **Environment configuration** — injects the variables the client expects (`ZAAP_PORT`, `ZAAP_HASH`, and so on).

### Step 3 — Run it

Double-click **`Jondo Emulator Launcher.exe`**. On the first run it unpacks `datos/world.zip` into `bases/world.db` (about 240 MB, it takes a moment) and creates `bases/auth.db` with a test account. Create your account in the launcher, press play, and start the Dofus client, or use the test account as follows:

Account: keka
Password: test

The emulator expects the client to sit next to it, in a `Cliente 3.6.10.10` folder beside the emulator folder.

---

## 📂 What you get

The root deliberately holds almost nothing — the launcher and little else. Everything is inside folders:

```
Jondo Emulator Launcher.exe   ← this is what you run, and there is nothing else to run
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
- [x] **Embedded server log** so you can watch traffic and errors without a console window.
- [x] **Single-file deployment** — the twelve dependency DLLs travel inside the executable; the folder stays clean.
- [x] **Multilanguage**.

### 🌍 World & Connection
- [x] **Client / Server / Authentication emulation** (Zaap, HAAPI, Connection Server, with the VIP subscription check bypassed).
- [x] **Server selection and character selection**, showing the mount the character is riding.
- [x] **Character creation** with a starter kit: Astrub zaap as the spawn point, adventurer set, 1,000,000 kamas, level 1 and 101 scrolled points per characteristic.
- [x] **World loading**, character spawn and name hover.
- [x] **Movement, map change, map loading and adjacent maps** across **15,360 maps**, with **17,211** of them carrying walkable-cell data.
- [x] **Last cell and map persistence** in the database.
- [x] **Auto-pilot** — double-click on the minimap or the *travel to* option.

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

### ⚔️ PvM Combat (functional core engine)
- [x] **Tactical arenas** — each roleplay map resolves to its combat arena by zone offset.
- [x] **Context transitions** — clean switching between roleplay and tactical combat, restoring world state when the fight ends.
- [x] **Placement phase** — red and blue placement tiles with cell swapping before *Ready*.
- [x] **Isometric grid geometry** (`MapGeometry`) using a pre-computed $O(1)$ BFS distance matrix.
- [x] **Line of sight** — obstacle validation extracted for **17,222 maps**, tracing segments between cell centres.
- [x] **Turn protocol and timers** — handshake, 30-second turns with automatic pass, AP/MP replenishment.
- [x] **Movement** — cell-by-cell path expansion with MP deduction.
- [x] **Spells and elemental damage** — level-based querying (AP cost, range, LoS, per-turn and per-target limits), damage from stats, equipment power and target resistances, and critical rolls.
- [x] **Active monster AI** — target selection by lowest HP, HP percentage, isolation and distance; ranged attacks; minimal-MP BFS pathing; and fleeing below 30% HP.
- [x] **Fight resolution and progression** — victory and defeat screens, experience over **1,889 levels**, official loot drops, level ups and group respawns.

### 🚧 Work in progress
- [ ] **Commands** — `.level` and `.kamas` exist but are rough.
- [ ] **Partial combat mechanics**:
  - Pushback trajectory (destination cell is calculated; collision damage and the pushback animation are not).
  - Weapon strikes (damage and AP cost apply; the slash animation does not).
  - Stat reductions (stats drop; the fighter debuff widget does not show).
  - Inventory kamas (persist and show on the victory screen; the UI tab needs a refresh).

### ❌ Not implemented
- [ ] Kolossium and PvP combat
- [ ] Advanced combat features (AP/MP dodge rolls, area-of-effect spell shapes, summons, shields, lock and dodge in melee)
- [ ] Professions
- [ ] Achievements
- [ ] Guilds

---

## 🧱 Source layout

* **`Jondo.Unity.sln`** — solution grouping every subproject:
  * **`Jondo.Unity.Launcher`** — entry point, proxies, network parser, handlers, managers, launcher UI and database management.
  * **`Jondo.Unity.Core`** — core networking infrastructure and TCP servers.
  * **`Jondo.Unity.Auth`** — authentication and HAAPI service handlers.
  * **`Jondo.Unity.Protocol`** — protocol buffers, constants and message definitions.
  * **`Jondo.Unity.World`** — game node / world logic, combat engine (`FightInstance`), isometric geometry (`MapGeometry`) and monster AI.
* **`JondoFix`** — the MelonLoader client mod, source plus the compiled `JondoFix.dll`.
* **`docs/EspecificacionTecnica.md`** — protocol specification, network architecture, ports and iteration logs.

---

## 💾 Database and persistence

Two **SQLite** databases, both in `bases/`:

* **`world.db`** — characters, inventories, positions, map persistence, spells, monsters, appearances, wardrobe and haven bags. Distributed compressed as `datos/world.zip`; the emulator extracts it by itself the first time it starts.
* **`auth.db`** — accounts and authentication sessions. Created on first run.

Files are looked up in `datos/`, then `bases/`, then the root, so a half-moved installation still starts.

---
<img width="2550" height="1501" alt="image" src="https://github.com/user-attachments/assets/74b1d19c-3bfe-40f8-9c74-e82f4647173a" />
<img width="2559" height="1506" alt="image" src="https://github.com/user-attachments/assets/521bef24-6b19-4061-bc5b-37a178e91163" />
<img width="2559" height="1500" alt="image" src="https://github.com/user-attachments/assets/0f06761a-7dcf-481e-b045-02efce31c58e" />
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
