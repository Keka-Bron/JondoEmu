High-performance server emulator for **Dofus 3 Unity (Client 3.6.10.11)** written in C# (**.NET 10**), with decoupled modular projects, a SQLite data layer, a combat engine driven entirely by client data — PvM, duels and Koliseo — a cross-platform launcher and a world editor.

> ⚠️ **Runs against Dofus 3 clients 3.6.10.11 and 3.6.10.10.** Ankama renames every protobuf
> message to three random letters on some patches; there is a toolchain here for surviving that —
> see [Surviving the next patch](#-surviving-the-next-patch).

---

## 📑 Contents

| 🖥️ [Launcher](#-launcher) | 🧩 [Server](#-server) | 🛠️ [Jondo Studio](#-jondo-studio) |
|:---|:---|:---|
| The player's window, in Avalonia. A team of up to eight accounts, each with its character drawn from the client's own bones. | The emulator itself. Four listeners in one process, one session per socket, and guards that refuse to boot on bad data. | The world editor. Nine sections over the client's data, writing a reviewable diff instead of a 240 MB binary. |

&nbsp;

> **New here?** [Quick Start](#-quick-start) puts you in the game in three steps ·
> [What you get](#-what-you-get) is what lands on disk

&nbsp;

- 🌍 &nbsp;**World** &nbsp;— &nbsp;[Connection and authentication](#-connection-and-authentication) · [World and movement](#-world-and-movement) · [Travel](#-travel) · [Houses, bins and haven bags](#-houses-bins-and-haven-bags) · [Social](#-social)

- 🎒 &nbsp;**Character** &nbsp;— &nbsp;[Character and inventory](#-character-and-inventory) · [Appearances](#-appearances) · [Professions](#-professions)

- 📚 &nbsp;**Content** &nbsp;— &nbsp;[NPCs and monsters](#-npcs-and-monsters) · [Quests](#-quests) · [Dungeons](#-dungeons) · [Jondo Coin](#-jondo-coin)

- ⚔️ &nbsp;**Combat** &nbsp;— &nbsp;[One engine, three rulebooks](#-one-engine-three-rulebooks) · [PvM](#-pvm-combat) · [Duels](#-duels) · [Koliseo](#-koliseo) · [Spell effect engine](#-spell-effect-engine) · [Combat challenges](#-combat-challenges) · [Not implemented](#-not-implemented-at-all)

- 🔎 &nbsp;**Tools** &nbsp;— &nbsp;[Jondo Studio](#-jondo-studio) · [Surviving the next patch](#-surviving-the-next-patch)

- 🧱 &nbsp;**Under the hood** &nbsp;— &nbsp;[Tests](#-tests) · [Source layout](#-source-layout) · [Database and persistence](#-database-and-persistence)

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

On the first run it unpacks `datos/world.zip` into `bases/world.db` (about 240 MB, it takes a moment) and creates `bases/auth.db` with a test account. Sign in to add an account to the launcher's team, tick one or several saved profiles, then press **Launch selected**. Up to eight independent Dofus clients can be active at once.

```
Account: keka
Password: test
```

By default the emulator looks for the client next to itself, in a `Cliente 3.6.10.11` folder beside the emulator folder — or `Cliente 3.6.10.10`, whichever it finds first. If yours lives somewhere else, set it in **Settings** and point it at your `Dofus.exe`. The choice is remembered, and if the client later moves the launcher says so instead of failing silently.

The **ES / EN / FR** switch sets the language of the launcher *and* of the game: the client is started with that `--langCode`.

**`Jondo Studio.exe`** is the third executable and needs nothing else running: double-click it whenever you want to look at the world or build content. See [Jondo Studio](#-jondo-studio) below.

---

## 📂 What you get

```
Jondo Emulator Launcher.exe   ← this is what you run
Jondo Server.exe              the server; the launcher starts it
Jondo Studio.exe              the world editor; open it when you want to look or build
content/                      the only files a person edits by hand, versioned in git
datos/                        json and bin the emulator reads (maps, items, appearances, zaaps…)
bases/                        writable databases and five verified pre-migration backup sets
docs/                         technical documentation
launcher_assets/              launcher artwork and music
JondoFix/                     the MelonLoader mod, source and compiled dll
Jondo.Unity.*/                source code
```

`content/` **is** in the repository, deliberately: it is the only folder a person edits by hand, it is small, and a change in it is a reviewable diff.

Important player and administrator actions are also written as one JSON object per line in `logs/activity.jsonl`. Commands, equipment moves, lottery prizes, granted items, fights, live administration and new unhandled packet shapes can therefore be filtered without scraping the human-readable console log. Credentials, launcher tokens and game tickets are never included.

Not in the repository because they are not needed to play: `bases/` (built on first run), `logs/`, `tools/` (the Python that regenerates `datos/`) and `dofus3_data/` (436 MB of raw client dump, only used by those tools).

---

## ✅ Emulation status

✅ done · 🟡 partial · 🚧 in progress · ❌ missing

### 🖥️ Launcher

<img width="2560" height="1512" alt="image" src="https://github.com/user-attachments/assets/68e0e721-b36c-4524-b5d6-660fd5beb3c0" />
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/86f835e0-f161-4f51-af42-a810a192f150" />

Rewritten in **Avalonia**, the same toolkit as the Studio. It used to be Windows Forms, drawn from code; nothing but the music is tied to the Windows desktop any more.

- ✅ **Three screens instead of one wall of buttons** — *Play*, *Accounts*, *Settings*, with the server-status pill in the header
- ✅ **Account cards with the character drawn in them** — portrait, name, level and a big tick. The portrait is assembled from the **client's own bones**, exactly the way Jondo Studio draws NPCs: not one image ships inside the executable
- ✅ The portrait shows the character **as they look in the world** — the chosen head, the real equipment and the cosmetics over it, in the same skin list the game client is sent
- ✅ Persistent team of up to 8 accounts, one independent Dofus process each; the highest-level character of each account is the one shown
- ✅ Account creation and login, written straight to `auth.db`; credentials sealed with DPAPI
- ✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use ticket, socket-owned session
- ✅ Independent lifecycle indicators for profiles, processes and sockets
- ✅ Embedded server log; single-file deployment; ES/EN/FR
- ✅ A neon sign that **starts like a real tube** — a hand-written stutter sequence, then a steady glow with the occasional flicker — and falling stars behind it. The choreography is written down rather than random on purpose: random timings read as a broken light, not a starting one
- ✅ Launcher and server are separate programs — the launcher carries no database, maps, handlers or effect catalogue
- 🚧 **OAuth is wired up and waiting for the website** — loopback redirect and PKCE on the launcher side; the server half is deliberately unwritten until there is a site to talk to

### 🧩 Server

<img width="2558" height="1508" alt="image" src="https://github.com/user-attachments/assets/df3cce87-166d-4f5a-8aff-a4fcd2575c87" />

`Jondo Server.exe`. The launcher starts it, but it is a program in its own right and can be run on
its own — or on another machine.

- ✅ Four listeners in one process — Zaap (`8888`), game (`5555`), chat (`6337`) and HAAPI
  (`15881`), plus a self-signed certificate so the client's HTTPS does not fail
- ✅ **One session per socket**, not one per account: every handler reads the session it is
  serving, so eight clients on one machine never see each other's state
- ✅ Its own window with the live log, the counters and the connected clients
- ✅ **Regression guards that run at boot and refuse to start** when the shipped data does not match
  what the code expects — see [Tests](#-tests)
- ✅ A loopback **control API** the launcher talks to: log tail, account login, and the characters
  of an account with the look already composed for drawing
- ✅ **Runs on another machine.** Every listener honours `JONDO_PUBLIC_BIND`, and the launcher runs a
  loopback relay so the client reaches it. The relay is not a convenience: HAAPI and the chat server
  both hand the client `127.0.0.1`, so repointing the client at a remote host cannot work on its own
- ✅ Unanswerable packets are recorded in their own database, deduplicated by protobuf shape

### 🔐 Connection and authentication

- ✅ Zaap, HAAPI and connection server emulation, VIP check bypassed
- ✅ Account creation and login against `auth.db`, with the password hashed and the attempt rate
  limited **by the socket's own IP** — taking it from the request body meant one JSON field made the
  limiter useless
- ✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use
  ticket, socket-owned session
- ✅ Server and character selection, showing the mount being ridden and each character's equipment
  
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/c4c194ad-dcd1-407f-a3f1-b44c8f4baed2" />
<img width="2558" height="1504" alt="image" src="https://github.com/user-attachments/assets/70d02ad2-8fc0-4ec8-b836-1dda959ed271" />

- ✅ Character creation with a starter kit — Astrub zaap, adventurer set, 1,000,000 kamas, 101
  scrolled points per characteristic
  
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/881c9530-6631-46ed-b85e-c7fd92602455" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/09a94e1f-165a-407d-91f1-1dd7b18063de" />
<img width="2558" height="1510" alt="image" src="https://github.com/user-attachments/assets/a65407d0-7e65-4481-bdfe-ea65554bb29e" />

- ✅ Account roles, and an administrator-only channel over loopback

### 🗺️ World and movement
- ✅ World loading, spawn, name hover, last cell and map persisted. Multiclient.
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/8882de29-36b0-4af9-be22-2d5f3bd4c6d4" />

- ✅ **15,360 maps**, **17,211** with walkable-cell data, **17,222** with combat cells
- ✅ Movement, map change and adjacent maps; auto-pilot from the minimap and *travel to*
<img width="538" height="452" alt="image" src="https://github.com/user-attachments/assets/a6438938-00c2-4a76-b4e1-48abf3d56934" />

- ✅ Seeing others arrive and leave, in all four directions
- ✅ Up to 8 clients at once, each on its own socket-owned session
- ✅ **Everybody is drawn wearing their gear** — the other players on the map, the opponent in a fight and every character on the selection screen. Equipment is read per character from `CharacterItems`, so it never depends on who happens to be connected

### 🌀 Travel

- ✅ **62 waypoints** with map, cell and sub-area, plus 3 departure-only zaaps the waypoint table omits
- ✅ Travel between zaaps with the real cost and destination list
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/1524d485-a845-4b62-a71c-b88de3bb7b54" />

- ✅ Discovered zaaps announced on world entry (`hjk`) — without it the travel window reads "No destination"
- ✅ Zaapis of Bonta (24) and Brakmar (21) at a flat 20 kamas, read off captures because client data cannot derive them
<img width="2560" height="1484" alt="image" src="https://github.com/user-attachments/assets/472e09f2-a49d-431e-8935-f60355457cdd" />

- ✅ The right window per list: `hjj` root field 0 zaap, 1 zaapi, 3 boat
- ✅ **16 temporal anomalies** with their 120-minute countdown, surfacing at vestiges (type 359), not at switched-off zaaps
<img width="2560" height="1514" alt="image" src="https://github.com/user-attachments/assets/942d4d71-9711-45f1-9156-5381f7ad14b8" />

- ✅ **3,815 interactive teleports** imported, 3,719 active across 2,655 maps
- ✅ **Passages that fire when you step on the cell**, hooked to the end of a walk rather than to the map edge — which is what the ground-level exits need
- ✅ Each route carries **its own measured interactive type** instead of a forced zero. The type is part of the element's identity on the client side: with a zero the numbers still travel but the client stops attaching the declaration to the drawing, and the exit sun disappears
- 🟡 **Every extracted passage still declares skill 114**, which is *Utilizar* on a zaap. Measured three ways that agree: Ankama's own world graph uses **184** on 5,629 of 5,719 interactive transitions and 114 on none; over 401 captures 184 appears on 420 elements and 114 on 23, every one a zaap; and in our own traffic skill 184 is followed by a map change 178 times while 114 opens the zaap window. New passages written in Jondo Studio declare 184; the extracted rows have not been rewritten
- ✅ **New passages can be created**, both ways, from Jondo Studio — which is what makes a house with its own interior possible

### 🏘️ Houses, bins and haven bags

- ✅ **1,437 doors on 553 maps**, all enterable and ownerless; **261 house models** with name, price and room count
<img width="1112" height="920" alt="image" src="https://github.com/user-attachments/assets/1506283c-f6cd-45b5-b9c4-f345273f67bb" />

- ✅ Entering and leaving, which are different messages (`jqw` in, `jru` out), coming out through the door you went in by
- ❌ The house plaque, chest, access code, buying and selling
- ✅ **67 public bins on 63 maps** — they open, show empty and close
- ❌ Putting items into a bin and taking them out
- ✅ Haven bags: entering and leaving, their own zaap, **48 themes**, **4,083 furniture pieces** placed and persisted, chest with the full item flow, lottery machine, and no monsters inside
<img width="2560" height="1492" alt="image" src="https://github.com/user-attachments/assets/a81a3b24-8559-4ad5-8a27-e6913eef95a8" />

> Which house sits behind which door is **not in the client**. The 1,437 doors share **114 genuine interiors**, assigned deterministically and kept inside their own neighbourhood; the mapping lives in `datos/casas_mundo_3.6.10.10.json` and can be corrected by hand.

### 💬 Social

- ✅ Information messages as `lqn { type, message, parameters }` against the client's 2,555-entry table, not as chat text
- ✅ Level-up window with music and animation, on a real gain and on `.level` in either direction
<img width="2560" height="1514" alt="image" src="https://github.com/user-attachments/assets/490997fc-1300-4a29-9963-32077efdf0dd" />

- ✅ Private messages via `kth`, which the client routes by opcode and not by channel
- ✅ Last connection time and IP, stored per character
- ✅ Parties — invite, accept, refuse, leave, hand over the lead, kick, and a full member sheet
- ✅ Lead passes on when the leader leaves; a disconnect removes the member and tells the rest
- ✅ Friends list
- ✅ **Every command answers in the session's own language**, from a 48-key catalogue in Spanish, English and French. The language comes from the `--langCode` the launcher started the client with, not from the wire: measured over the nine authentication captures, the client does send its two-letter code, but in `kqz` field 3
- ❌ The invitation popup's *Details* button (`imd` → `ilb`), the dedicated member-gone message (`inc`), party search and following the leader

### 🎒 Character and inventory

- ✅ **21,748 item templates** and **66,294 item effects** — spawning, equipping, bags, destruction, persistence
- ✅ **929 item sets** with their bonuses
- ✅ **520 mounts** with their look, swapped and unequipped correctly
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/375da573-ab61-4bf0-83fd-6f2a8f872cde" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/581b105c-9569-4f54-ab77-01e122b8ce06" />

- ✅ Characteristic assignment, dynamic capital, points in sync across every client panel
<img width="708" height="1048" alt="image" src="https://github.com/user-attachments/assets/b07f0ac2-f701-4f3a-82e2-c04f884d696d" />

- ✅ **17,113 spells** across **34,823 spell levels**; **638 character heads**
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/02b5e575-20e3-47ed-b095-55443fb792ab" />

- ✅ **539 titles** and **167 ornaments**, applied, persisted and carried in the map actor block
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/0e579800-2776-4aba-8629-58bb2e6c7acf" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/25225e6b-2b6e-4aa6-8f56-12937b0754a0" />

- ✅ Commands — `.teleport`, `.kamas`, `.shop`, `.size`, `.level`, `.item`, `.itemset`
- ✅ **Live administration over HTTP** — `POST /api/personaje` sets characteristics, kamas and level, grants items or a mount, and teleports a connected character without a reconnect. `POST /api/rol` changes account roles. Administrator only, loopback only, and serialized with the target session
- 🟡 `.level` repaints the in-fight spell bar, but the fighter's own level is not updated, so the engine still resolves spells at the level the fight started with

### 👕 Appearances
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/30ee645b-191b-4146-9966-d2c3fb72a9cf" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/2655dfd1-d565-484c-9735-54dd00f4f8b0" />

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
- ✅ The same skin list now feeds the launcher's portraits, so one change fixes both
- 🟡 82 of those skins were inferred by image matching and flagged for review by their author, so they are held back at load until somebody measures them
- 🟡 A second, older look path survives in `InventoryHandler` for four items and disagrees with the new table on both the field and the value. Left alone until a capture says which is right
- ❌ **Per-character colours.** Every look is composed from the breed's default palette: there is no colour column anywhere and `customColors` is null at all eleven call sites. Two characters of the same breed and sex are tinted identically

### ⛏️ Professions

- ✅ **25,090 resources on 4,507 maps** across the six gathering jobs, with graphic → (type, skill) crossed from 305 captures
- ✅ The three states — full, depleted, busy — including the skill field moving between `f4` and `f3`
- ✅ Job levels and experience persisted, with the real curve `10 × level × (level − 1)`

- ✅ What you gather lands in the inventory, and the amount grows with job level
- ✅ Too low a job level blocks gathering the way the game does it
- ❌ Crafting professions: workshops, the craft window, and the **4,858 recipes** already in the database

### 👹 NPCs and monsters
<img width="954" height="836" alt="image" src="https://github.com/user-attachments/assets/78779a18-0cd2-4f5c-b403-0c39cd291bcb" />
<img width="2560" height="1496" alt="image" src="https://github.com/user-attachments/assets/bf4e3381-61a9-44e0-bea4-eed3193a7a93" />
<img width="2558" height="1510" alt="image" src="https://github.com/user-attachments/assets/0b4adf75-9b36-4298-b428-d0444297adb3" />

- ✅ **6,468 NPC templates** with 3D looks and dialogue trees
- ✅ **422 NPCs** standing where Ankama puts them across **202 maps**, cell and orientation taken from captures, dialogue attached where it was captured
- ✅ **5,134 monsters** with native Protobuf bone models, custom scales and textures, quest monsters and archmonsters included
<img width="1700" height="930" alt="image" src="https://github.com/user-attachments/assets/02254e58-ec87-4839-82ac-f142ec5ef9cd" />

- ✅ **38,744 mapped mob groups**, respawned and kept populated, 1 to 8 monsters each
- ✅ Sub-area aware spawning across **562 sub-areas**, with radius-2 cell validation so nothing spawns on decorations or zaap pillars
- ✅ **No monsters indoors, and none standing on a zaap** — not in houses, banks or shops. The rule is two lists and one exception, and the exception is the one that matters: 753 of the 763 dungeon rooms are themselves marked indoors, so a blanket ban would empty every dungeon. 7,214 groups of 38,744 kept out, and the 763 rooms untouched
- ✅ **NPC colours**, read as what they are: `index=value` pairs, sometimes hexadecimal. The **2,045 NPCs that carry colours** render with theirs
- ✅ A dialogue always offers at least one real reply, so it can always be closed. With an empty list the client draws its own *Leave* which never answers back
- 🟡 **401 monsters have no spells at all** in the database
- ✅ **Dialogue trees.** The client holds every line an NPC can say and every reply it can be given, and never which goes with which — measured across all 6,467 NPCs, there is no field for it. That mapping has always been the server's own, so it has to be authored, and now it can be
<img width="1138" height="694" alt="image" src="https://github.com/user-attachments/assets/fc1182c3-a261-4bcd-9532-84a2ceda8dc8" />
<img width="1082" height="692" alt="image" src="https://github.com/user-attachments/assets/39cdf857-b506-4968-b2f9-c0c5f80b64c3" />

- ✅ **Monster groups placed by hand**, and Ankama's own removable, without touching the 240 MB database that gets regenerated

### 📜 Quests

<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/6dbe2000-4f3c-4b41-9409-5be932f84d6e" />
<img width="1452" height="1226" alt="image" src="https://github.com/user-attachments/assets/77256193-a9dd-48df-9d0a-5408613fef34" />

**1,976 quests**, with their 2,225 steps and 15,547 objectives, read out of six Unity dumps the
repository does not even carry.

- ✅ A quest is handed over by an NPC saying a particular line — 1,260 steps declare one and every
  one of them resolves to real text, which is what ties the quest catalogue to the dialogue trees
- ✅ Objectives complete two ways: the client says so for the **5,670** that ask you to click
  something the server never sees, and the server counts for itself the ones that ask you to beat a
  monster
- ✅ Progress is written the moment it changes — there is no autosave here, and losing an evening's
  quest is worse than losing a few kamas
- 🟡 The start condition is a language of its own: **29 operators**, brackets three deep, and a `!`
  that means "not" without an `=` after it. Six operators are understood, covering every term of
  **935 of the 1,976** conditions; the rest are let through **and named**, because refusing what
  this emulator cannot model would put 53% of the game's quests out of everybody's reach

Full workings in **`docs/quests.md`**.

### 🏰 Dungeons
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/3c73a696-1de3-46ae-bea6-149bc06009fb" />
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/7b481a47-2fea-43da-a8a0-5b2692030473" />

**187 dungeons**, with their **763 rooms**, their key and their boss.

- ✅ Talk to the guardian, hand over the key, and you are in the first room; win a fight and you
  move on; beat the boss in the last one and you come out
- ✅ The boss is placed at startup in **126** dungeons, in the room the data says, at the highest
  grade it has
- ✅ The keyring and the required item come straight from the client's own data, which is what
  makes a locked door possible
- ✅ Dungeon challenges are imposed at 0% and carry achievements

> It is not Ankama's dungeon, and the difference is worth stating: theirs is a chain of rooms and
> corridors walked through ordinary doors, and **not one of the 187 has a single one of its internal
> passages** — not in the extracted table, not in Ankama's own world graph. A player put in room 0
> would have no way out, so winning moves you instead.

Full workings in **`docs/dungeons.md`**.

### 🪙 Jondo Coin

A currency of this server's own — a real item with its own template.
<img width="1676" height="1102" alt="image" src="https://github.com/user-attachments/assets/aee2eb3f-b2a3-4c35-a35c-fafe69669355" />

- ✅ Drops from every monster at 100%, one coin per 25 monster levels: 1 for 1-25, 2 for 26-50, up to 9 at 201+
- ✅ Its own description in the five client languages, picked at runtime from the language the client is running in
- ✅ Vendors that charge in coins instead of kamas, one per category, appearance shops among them, priced by item type and rarity

See `docs/jondo-coin.md`.

---

## ⚔️ One engine, three rulebooks

There is one fight engine, and it answers three different games. It does not ask *what kind of
fight am I*; it asks **what do I do**, and the answer comes from a rules object — so adding something
to the Koliseo touches one class instead of five methods:

| | Against monsters | Duel | Koliseo |
|---|:---:|:---:|:---:|
| Challenges offered | yes | no | no |
| Placement clock | 45.0 s | — | 59.2 s |
| `kam` type | 4 | 0 | 7 |
| `kaa` countdown | yes | no | yes |
| Monster loot and experience | yes | no | no |
| Koliseo payout | no | no | yes |
| Clears the group on a win | yes | no | no |
| Moves to the next room | yes | no | no |

None of those numbers is chosen: the 4, the 0 and the 7 are the `kam`'s field 2 in the captures,
and the 592 is the `kaa`'s field 5 in the Koliseo one.

Two rules hold the rest of it together:

* **The teams are `Azul` and `Rojo`, not `Team0` and `Team1`.** Nothing assumes one side is the
  players and the other the monsters, because in a duel both sides are people.
* **Everything sent to a client is composed inside that client's own session.** Each fighter's look,
  level, characteristics and equipment come from their own record, so what the second player is sent
  describes the second player.

**Three architecture tests enforce it**, each verified by injecting a real violation and watching it
go red: no lookups that assume one team is the players, no rules decided by fight type outside the
rules object, and nothing writing to a single socket unless it is painting one person's own view.

### 🐉 PvM combat

<!-- 📷 aquí: un combate contra monstruos, colocación y turnos -->

- ✅ Tactical arenas resolved from each roleplay map by zone offset, with clean context transitions
- ✅ Placement phase with red and blue tiles and cell swapping before *Ready*
- ✅ Isometric geometry (`MapGeometry`) over a pre-computed O(1) BFS distance matrix, with no diagonal steps
- ✅ Line of sight traced between cell centres against the arena's own blocker set
- ✅ Turn protocol, 30-second timers with automatic pass, AP/MP replenishment
- ✅ Movement with per-tile MP cost and collision against occupied cells
- ✅ Loot, victory and defeat screens, experience over **1,889 levels**, level-ups and group respawn
- ✅ Monster AI: a target chosen **per spell**, range measured against that target rather than against the nearest enemy, walking to the spell's own range band, `MaxCastPerTurn` honoured, breadth-first pathing around obstacles and line of sight. Measured over the 5,134 monsters: **15.1%** cannot reach the player, against 24.9% without it, and **87.2%** of action points get spent, against 58.7%
- 🟡 Weapon strikes apply damage and AP cost; the slash animation does not
- 🟡 `MaxCastPerTarget`, minimum cast interval and cast-in-line are enforced for the player, not for monsters
- ✅ **Push and collision damage**, `blockedCells × (level/2 + push − resistance + 32) / 4`, floored — measured over 127 collisions, with the resistance subtracted *inside* the quarter. The fighter acting as the wall takes half, and the **Unmovable** state cancels it. Twelve samples are locked into a startup guard
- ❌ AP/MP dodge rolls, shields, lock and tackle in melee

### 🤺 Duels

<!-- 📷 aquí: retar a alguien, el combate, y la pantalla de victoria/derrota -->

Player against player, on the map, by challenging somebody standing there.

- ✅ Offer, accept and refuse, with the challenge id echoed through every frame of the fight
- ✅ Both fighters composed from their **own** character record — look, level, characteristics, equipment
- ✅ Placement with no clock, and no challenges offered: there are no monsters to set them against
- ✅ Victory **and defeat** screens, each player's own, and both sides returned to the map
- ✅ Nothing is won and nothing is lost — no experience, no kamas, no loot
- ✅ The end-of-fight card shows the other player's portrait instead of a question mark: an entry
  with no level is a *monster* to the client, so a person always carries theirs

### 🏟️ Koliseo

<!-- 📷 aquí: la ventana del koliseo, el buscando, y el reparto de kolichas al ganar -->

Ranked PvP through a queue. Open the window, pick a format, get matched, fight, get paid.

- ✅ **The format table** (`lux` → `ltd`) — 1v1, 2v2, 3v3 open and a fourth closed, byte for byte as the capture
- ✅ **Enrolling** (`lsm`), with the format carried as the client's own enum
- ✅ **The queue state** (`lsx`) pushed back, which is what paints *searching* in the window
- ✅ **Matchmaking on enrolment**, one queue per format, drawn under a lock so two simultaneous requests cannot take the same person into two fights
- ✅ Everybody re-checked as still connected **before** anyone loses their place in the queue; if somebody dropped, the rest go back to the queue rather than pay for it
- ✅ The fight itself, with the Koliseo rulebook, and both sides returned to roleplay at the end
- ✅ **The winner is paid** — kamas, Kolichas (item 12736), Vitorichas (34478) and experience. The loser gets nothing, and its experience block carries the gained field *absent* rather than zero, which is how the capture has it
- 🟡 **The amounts are constants, not a formula.** Two winners in one capture is not enough to derive one — they go the wrong way round, the higher level earning fewer kamas — so kamas, Kolichas and Vitorichas sit in three named fields. Experience does better: over the band of the winner's own level the two samples land at 7.22% and 6.12%, so 6.67% is used
- 🚧 The *match found* popup with accept and refuse
- 🚧 Fights are held on an ordinary arena; the real game picks one of the many Koliseo maps at random
- ❌ Rankings (`iqt`, `irc`), two undeciphered lists of over three thousand bytes each
- ❌ The `lst` redirect to a separate Koliseo server. Jondo is one server and holds the fight in place

### ✨ Spell effect engine

One engine for all eighteen classes, driven entirely by client data. Not a single spell is written by hand: everything comes out of `SpellLevels.EffectsJson` and the `Effects` catalogue.

- ✅ Effects, triggers and target masks read from the spell — `I` on cast, `TB` turn start, `TE` turn end, `DBE` when hit, `CCMPARR` per tile walked; `a` allies, `A` enemies, `g` summons, `E<n>`/`e<n>` gated on a state
- ✅ States need no code — effect 950 sets a number, 951 clears it, the masks do the rest
- ✅ Area shapes from `zoneDescr` — point, circle, cross, line, diamond, square, whole map — with each spell's own per-tile falloff
- ✅ Displacement — push, pull, step back, step forward, direction taken from the centre of the area, stopping at walls, holes and fighters
- ✅ Criticals rolled against the spell's probability plus the character's, using the spell's separate critical effect list
- ✅ Point steal, life steal, erosion of maximum HP and damage-taken multipliers
- ✅ Buff panel — icon, value, remaining rounds and dispellable flag; buffs start on their delay and expire on their round
- ✅ **Stack limits** — a spell level's `MaxStack` is honoured, so a bonus that builds up stops where the game stops it
- ✅ Cooldowns and cast limits — per turn, per target, minimum interval, initial cooldown
- ✅ **Rebounds that pick the nearest eligible target** (effect 2160), bounded by a budget so a chain cannot loop, with the damage still attributed to the caster while the animation travels from the previous victim
- ✅ Summons as real fighters — own sheet, place in the carousel next to their owner, behaviour spell, lifetime, and they all fall when their summoner dies
- ✅ Item attitudes — the six Dofus and the trophies grant their spell through effect 1175
- ✅ The characteristic sheet in the shape the client expects: 53 entries in a fixed order, and a single-characteristic refresh **replaces** its entry rather than adding to it
- 🚧 Healing — the FIRE fixed heal (effect 108, 751 spell levels) works. Its five siblings are the same heal in the other elements and none is done: water 2998 (92 levels), air 2999 (66), earth 3000 (62), neutral 3001 (11) and best-element 3002 (30)
- ❌ Glyphs and traps (effects 400, 401, 1091)
- ❌ Appearance-changing spells — the transform payload is an opaque blob
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

- Crafting professions
- Achievements
- Guilds
- Party fights

---

## 🛠️ Jondo Studio

<!-- 📷 aquí: dos o tres pantallas del editor -->

> ⚠️ **Very early.** The Studio changes every day, and the parts that write files have been exercised
> by one person on one machine. Read it, use it, tell us what is wrong — but keep a copy of
> `content/` before a long session, and expect screens to move under you. Nothing in it can damage
> `world.db` or a running server, which is the one guarantee it does make.

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
row it did not write.

**Every row carries its provenance**, and that column is the point: six months from now nobody will
remember whether a cell number was measured off a capture or typed in by hand, and without it on
screen the two become indistinguishable.

### What it does today

Nine sections, **in Spanish, English or French** — and the language switch changes both halves at
once. The editor's own words come from one catalogue; the game's words are read straight out of the
client's `Content/I18n/{lang}.bin`, 339,342 texts per language. The format is not documented
anywhere; it was worked out and then checked against `world.db`, where 500 keys sampled at random
came back byte for byte identical, including one of 42,180 characters.

**The creatures are drawn**, out of the client's own bundles and nothing copied into the repository.
Monsters come from a picto atlas, 5,130 of the 5,134 covered. NPCs are assembled the way the client
assembles them: bones, a still frame, and the skins the look names. That renderer now lives in its
own project, `Jondo.Unity.Sprites`, and the launcher draws its account portraits with it.

- ✅ **Overview** — which files it read and what came out of each. First screen on purpose
- ✅ **Traffic** — the client-server conversation, live and back through the log, every frame read **against the protocol the client itself declares**. From here a packet can be named on the spot, from the **513 real message names** the client still ships in its metadata
- ✅ **Packets** — every kind of packet seen, with a status ladder: unknown, named, documented, handled, ignored
- ✅ **NPCs** — all 422 placements, with the provenance column and the NPC drawn on the map
- ✅ **Dialogues** — which reply leads to which line, with the text on screen rather than ids
- ✅ **Monsters** — open a group, take a monster out, put another in, move it two cells left
- ✅ **Spells** — every spell with its effects, and the map showing **how far it reaches and what it would hit**, worked out by calling the fight engine's own `Zone.Casillas` rather than a drawing of it
- ✅ **Passages** — two maps side by side, a door picked on each, and one button that joins them **both ways**
- ✅ **Map cells** — the three layers painted one at a time, click to toggle and **drag to paint a run**
- ✅ A section that fails shows its error *inside* the editor, and `Jondo Studio.exe --selftest` builds all nine in all three languages against the real data and fails the publish if any throws

**Everything it writes goes to `content/`**, in versioned text. Nothing opens `world.db` for writing
and nothing talks to a running server.

### What is being worked on

- 🚧 **NPC actions per placement** — the right-click menu is drawn by the *client* from the
  template's `actions[]`, so an action written per placement can only take options away, never add
  one
- 🚧 **Editing spells.** The simulator is there; changing a spell's numbers is not
- 🚧 **Shops, loot tables and dungeons** — all three are screens over data the server already reads
- 🚧 **Editing quests.** The engine plays them and the Studio shows them, but nothing writes one yet
- 🚧 **A thin admin channel** so a running server can be told to reload one domain, without a restart

The full plan is in **`docs/world-editor.md`**.

---

## 🧪 Tests

`Jondo.Unity.Tests` — **848 xUnit tests** across 99 files, grouped by domain: `Auth`, `Combat`,
`Content`, `Diagnostics`, `Economy`, `Launcher`, `Movement`, `Network`, `Protocol`, `Quests`,
`Security`, `Sessions`, `Sprites`, `Studio`, `World`. They run in about half a minute.

```bash
dotnet test Jondo.Unity.Tests
```

Five of them run against `logs/gameserver_traffic.log` itself when it is on the machine, and skip
when it is not. A test that skips proves nothing, and that is the trade being made on purpose:
frames this project builds itself only ever prove that the builder and the reader agree, so a
handful of checks are pointed at traffic the real client produced.

**Publishing the server runs them first and fails if any is red.** Not on build — the inner loop
stays fast — but publishing is the one step between writing code and a player running it. The escape
hatch is `-p:SkipTests=true`, which leaves its trace on the command line rather than in a config
file nobody reads.

### Three kinds of check, three homes

* **At startup, and it throws** stay the questions of the form *"is the data I was shipped sane?"* —
  the fight sheet's 53 characteristics in their captured order, the interactive registry, the monster
  spellbooks, the vendor placements, the profession catalogue. `datos/` and `world.db` are
  regenerated by tooling outside the build, so a bad regeneration reaches a player with every test
  still passing.
* **In the test project** live the questions of the form *"is this code correct?"* — the content
  layers, the collision damage formula, the Jondo Coin bands, frame limits, protobuf parsing,
  password hashing, log censorship and session isolation.
* **Architecture tests** ask *"is this code shaped right?"* — they read the fight engine's own
  source and fail on the shapes a multi-client engine cannot afford. They are the only kind that
  catches a mistake **before** it has a symptom, and they hold an exception list where every entry
  carries a written reason.

Some things cannot be asserted by asking whether an operation succeeded, because it always does: a
portrait that draws a character facing away, or with no head, is still a valid PNG. Those are
guarded by counting — the animation name has to end in the direction that faces the camera, and the
head slot has to contribute more than zero triangles.

---

## 🔎 Surviving the next patch

Every protobuf message in Dofus 3 is named with three random letters — `kub`, `jru`, `lqu` — and on some patches Ankama reshuffles the lot. Nothing else about the protocol changes shape, but the emulator no longer knows what anything is called. **`protocolbuilder`** is the command line for that; **`Jondo Desofuscador.exe`** is the same engine behind one window and one button.

Eight consecutive real clients (3.6.4.3 → 3.6.10.10) were pulled from Ankama's own CDN and compared patch by patch:

- **Ankama does not reshuffle on every patch.** Three of the seven jumps keep all 2,169 names, one for one — five obfuscation generations across eight versions. The tool checks for the identity mapping first, in a second.
- **Zero wrong pairings over 6,505 real pairs.** The matcher never looks at names, only at field numbers, kinds and neighbourhood. It gets 71.1% and misses none; what it cannot decide, it leaves alone.
- **On a patch that does reshuffle, structure alone gets about 11%** — the ceiling, not a tuning problem.
- **Chaining through intermediate versions is worse**: 12 pairs against 245 for the direct jump. A plausible idea the measurement refuted.
- Building the `Op` layer also turned up **49 opcodes that only exist in 3.6.4.3**.

The **`Op` layer** replaced **495 three-letter literals across 35 files** with one generated file, `Jondo.Unity.Protocol/Op.cs`, so applying a mapping never means editing the emulator by hand.

```bash
protocolbuilder proto    <client dll> [out.proto]      the client's own message shapes
protocolbuilder mapear   <old client> <new client>     who is who between two versions
protocolbuilder capa     <client> <anchors> . --aplicar  regenerate Op.cs and migrate call sites
protocolbuilder bajar    3.6.4.3 3.6.10.10 clientes    fetch old clients from the CDN, 183 MB each
protocolbuilder cadena   clientes                      measure each patch on its own
```

> `proto` earns its keep beyond migrations. What a message carries is settled by the client's
> own schema rather than by one reading of one capture: `lth { bool, bool }` is two booleans,
> and no amount of staring at two bytes on the wire says that as plainly.

Full write-up in `docs/desofuscacion.md`.

---

## 🧱 Source layout

The three executables:
* **`Jondo.Unity.Server`** → `Jondo Server.exe` — proxies, network parser, handlers, managers, database and the server's log window. The spell effect engine lives in `Managers/`: `SpellEffects` reads the spell data, `EffectEngine` turns it into things that happen to somebody, and `Summons` builds summoned fighters from monster templates
* **`Jondo.Unity.Launcher`** → `Jondo Emulator Launcher.exe` — the player's window, in Avalonia. References the contract and the sprite renderer, and nothing else
* **`Jondo.Unity.Studio`** → `Jondo Studio.exe` — the world editor, in Avalonia

Shared:
* **`Jondo.Unity.Contract`** — paths, settings and the shared palette
* **`Jondo.Unity.Contract.WinForms`** — what is left of the old Windows Forms shell, kept apart so nothing else drags it in
* **`Jondo.Unity.Core`** — networking infrastructure and TCP servers
* **`Jondo.Unity.Auth`** — authentication and HAAPI handlers
* **`Jondo.Unity.Protocol`** — message definitions and the generated `Op` layer
* **`Jondo.Unity.World`** — world logic, `FightInstance`, the fight rulebooks (`FightRules`), buffs and states (`Buff`), area shapes and displacement (`Zone`), isometric geometry (`MapGeometry`)
* **`Jondo.Unity.Sprites`** — draws a character or an NPC out of the client's own bones, skins and atlases. Shared by the Studio and the launcher so a fix to either reaches both
* **`Jondo.Unity.Parser`** — capture parsing
* **`Jondo.Unity.Tests`** — 848 xUnit tests, and the gate on publishing

The protocol toolchain, which the emulator does not depend on:
* **`Jondo.Unity.Reversing`** — reads a client with Cpp2IL, rebuilds the `.proto`, matches two versions, indexes the code, downloads old clients from the CDN (`Cytrus`) and generates the `Op` layer
* **`Jondo.Unity.ProtocolBuilder`** → `protocolbuilder` · **`Jondo.Unity.Deobfuscator`** → `Jondo Desofuscador.exe`
* **`JondoFix`** — the MelonLoader client mod, source plus the compiled dll

Documentation, all of it measured rather than assumed — index in `docs/README.md`. Start with `docs/protocol.md` (how a message travels), `docs/opcodes.md` (what each opcode means and where it was seen), `docs/fight.md` (a fight on the wire, opcode by opcode) and `docs/desofuscacion.md` (surviving a patch).

---

## 💾 Database and persistence

Three **SQLite** databases in `bases/`, and one folder of text:

* **`world.db`** — 41 tables and 659,397 rows: characters, inventories, positions, map persistence, spells, monsters, appearances, wardrobe and haven bags. Distributed compressed as `datos/world.zip` (24.8 MB) and extracted on first run.
* **`auth.db`** — accounts and authentication sessions, created on first run.
* **`paquetes.db`** — the packets the server does not yet know how to answer, deduplicated by protobuf shape. Kept apart on purpose: it carries nothing needed to play, it can be deleted to start over, and it can be handed to somebody else to look at without handing over anybody's characters.
* **`content/`** — the authored layer, in versioned JSON. The only one edited by hand, and the only one nothing regenerates. See [Jondo Studio](#-jondo-studio).

Files are looked up in `datos/`, then `bases/`, then the root, so a half-moved installation still starts.

**Some regression guards also run at startup and throw**, so the server refuses to boot when the data it was shipped does not match what the code expects — see [Tests](#-tests) for which checks live where, and why.

---

<details>
<summary>📷 Old screenshot gallery — being redistributed into the sections above</summary>

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

</details>
