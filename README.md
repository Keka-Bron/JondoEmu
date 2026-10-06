High-performance server emulator for **Dofus 3 Unity (Client 3.6.10.11)** written in C# (**.NET 10**), with decoupled modular projects, a SQLite data layer, a combat engine driven entirely by client data — PvM, duels and Koliseo — a cross-platform launcher and a world editor.

> ⚠️ **Runs against Dofus 3 clients 3.6.10.11 and 3.6.10.10.** The live game is on 3.6.11.12, so the
> official launcher does not provide a client that works here — [Step 2](#step-2--get-the-361011-client)
> has a download. Ankama renames every protobuf message on some patches; the toolchain for that is in
> [Surviving the next patch](#-surviving-the-next-patch).

---

## 📑 Contents

| 🖥️ [Launcher](#%EF%B8%8F-launcher) | 🧩 [Server](#-server) | 🛠️ [Jondo Studio](#%EF%B8%8F-jondo-studio) |
|:---|:---|:---|
| The player's window, in Avalonia. A team of up to eight accounts, each with its character drawn from the client's own bones. | The emulator itself. Four listeners in one process, one session per socket, and guards that refuse to boot on bad data. | The world editor. Nine sections over the client's data, writing a reviewable diff instead of a 240 MB binary. |

&nbsp;

> **New here?** [Quick Start](#-quick-start) puts you in the game in four steps ·
> [What you get](#-what-you-get) is what lands on disk

&nbsp;

- 🌍 &nbsp;**World** &nbsp;— &nbsp;[Connection and authentication](#-connection-and-authentication) · [World and movement](#%EF%B8%8F-world-and-movement) · [Travel](#-travel) · [Houses, bins and haven bags](#%EF%B8%8F-houses-bins-and-haven-bags) · [Banks and marketplaces](#-banks-and-marketplaces) · [Social](#-social) · [Guilds and raids](#%EF%B8%8F-guilds-and-raids)

- 🎒 &nbsp;**Character** &nbsp;— &nbsp;[Character and inventory](#-character-and-inventory) · [Appearances](#-appearances) · [Professions](#%EF%B8%8F-professions)

- 📚 &nbsp;**Content** &nbsp;— &nbsp;[NPCs and monsters](#-npcs-and-monsters) · [Quests](#-quests) · [Achievements](#-achievements) · [Almanax](#-almanax) · [Dungeons](#-dungeons) · [Infinite Dreams](#-infinite-dreams) · [Jondo Coin](#-jondo-coin)

- ⚔️ &nbsp;**Combat** &nbsp;— &nbsp;[One engine, four rulebooks](#%EF%B8%8F-one-engine-four-rulebooks) · [PvM](#-pvm-combat) · [Duels](#-duels) · [Koliseo](#%EF%B8%8F-koliseo) · [Spell effect engine](#-spell-effect-engine) · [Spell check-list](#-spell-check-list) · [Combat challenges](#-combat-challenges)

- 🔎 &nbsp;**Tools** &nbsp;— &nbsp;[Admin window (F10)](#-admin-window-f10) · [Jondo Studio](#%EF%B8%8F-jondo-studio) · [Surviving the next patch](#-surviving-the-next-patch)

- 🤝 &nbsp;**Community** &nbsp;— &nbsp;[Community projects](#-community-projects)

- 🧱 &nbsp;**Under the hood** &nbsp;— &nbsp;[Tests](#-tests) · [Source layout](#-source-layout) · [Database and persistence](#-database-and-persistence)

---

## 🚀 Quick Start

**Nothing has to be compiled.** The launcher ships as a single ready-to-run executable with every dependency inside it, and the world database ships compressed and extracts itself on first run.

### Step 1 — Install the .NET 10 runtime

Download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). The *Desktop Runtime* is the one you want.

### Step 2 — Get the 3.6.10.11 client

The live game is on 3.6.11.12 and Ankama's launcher only provides the current version, which this
emulator does not support.

**⬇️ [Dofus 3.6.10.11 — download](https://www.swisstransfer.com/dl/01a082ed-3e6a-70b9-987b-7f2551484389)**

It is the stock Ankama client. Unpack it as a **`Cliente 3.6.10.11`** folder beside the emulator
folder, which is where the launcher looks first; anywhere else works if you point **Settings** at
your `Dofus.exe`.

A 3.6.10.11 or 3.6.10.10 install from before the patch also works — keep the official launcher
from updating it.

### Step 3 — Point the Dofus client at the emulator

The official client talks to Ankama's servers and checks their SSL certificates. **JondoFix**, a MelonLoader mod, redirects it to your machine instead. It comes already built in this repository.

1. Get **MelonLoader 0.7.x** from [its releases page](https://github.com/LavaGang/MelonLoader/releases). 0.7.x is published as *Open-Beta*, so it shows up as a **pre-release** and the page's "Latest" tag points at 0.6.x. **0.6.x does not work with this client** — tick *show pre-releases* and take 0.7.x. This repository is tested against **0.7.3**.
2. Run the installer and point it at your **`Dofus.exe`**. MelonLoader detects the rest (`Game Type: Il2cpp`, `Game Arch: x64`, `Runtime Type: net6`, Unity `6000.3.16f1`).
3. Nothing to copy: every time the **launcher** starts the client it puts this repository's **`JondoFix/JondoFix.dll`** into the client's **`Mods/`** folder if the one there differs, so a client with MelonLoader gets the mod's changes with the emulator, never with a new client. Only if you start the client some other way, copy that file into `Mods/` yourself (next to `Dofus.exe`; create the folder if it is not there).

> The mod ships **already compiled**; `JondoFix/` also carries its source.

Afterwards:
* The installer drops a **`version.dll`** next to `Dofus.exe`, which loads MelonLoader. Renaming it to `version.dll.disabled` turns the whole thing off so you can play the official game; renaming it back turns it on again.
* MelonLoader writes a log per run under **`MelonLoader/Logs/`**. If the client starts but never reaches the emulator, look there first.

What JondoFix does: intercepts sockets, Named Pipes and DNS queries and sends them to `localhost` (ports `8888`, `5555`, `15881`, `6337`); stops HTTPS requests from failing against the local self-signed certificate; and injects the environment variables the client expects (`ZAAP_PORT`, `ZAAP_HASH`, and so on). In the Koliseo window it makes room for the fourth card, names it after the JondoBots in the client's language and opens their rules in a window of the client's own (see [Koliseo](#%EF%B8%8F-koliseo)).

### Step 4 — Run it

Double-click **`Jondo Emulator Launcher.exe`**. It launches **`Jondo Server.exe`** itself, in its own window with the log and the counters.

On the first run it unpacks `datos/world.zip` into `bases/world.db` (about 240 MB) and creates `bases/auth.db` with a test account. Sign in to add an account to the launcher's team, tick one or several saved profiles, then press **Launch selected**. Up to eight independent Dofus clients can be active at once.

```
Account: keka
Password: test
```

`keka` is an **administrator**, so every command and the [admin window (F10)](#-admin-window-f10) can be tried with it, and it comes with its characters already made — `[#KEKA-BRON#]` at level 204, `Test`, `Tymaviejas` and `Bron` — with their equipment. For a player account of your own, use **CREATE ACCOUNT** in the launcher.

> ⚠️ The password of `keka` is published right here. Before opening a server to other machines (`JONDO_PUBLIC_BIND=1`), change it or delete the account: anybody who knows it is an administrator.

`world.zip` is only unpacked when there is no `bases/world.db` yet. To get the characters of a newer download over an older installation, delete `bases/world.db` (and with it the characters made on it) before starting.

By default the emulator looks for the client next to itself, in a `Cliente 3.6.10.11` folder beside the emulator folder — or `Cliente 3.6.10.10`, whichever it finds first. If yours lives somewhere else, set it in **Settings**. The choice is remembered.

The **ES / EN / FR** switch sets the language of the launcher *and* of the game: the client is started with that `--langCode`.

**`Jondo Studio.exe`** is the third executable and needs nothing else running. See [Jondo Studio](#%EF%B8%8F-jondo-studio).

---

## 📂 What you get

```
Jondo Emulator Launcher.exe   ← this is what you run
Jondo Server.exe              the server; the launcher starts it
Jondo Studio.exe              the world editor
content/                      the only files edited by hand, versioned in git
datos/                        json and bin the emulator reads (maps, items, appearances, zaaps…)
bases/                        writable databases and backups
docs/                         technical documentation
launcher_assets/              launcher artwork and music
JondoFix/                     the MelonLoader mod, source and compiled dll
Jondo.Unity.*/                source code
```

Player and administrator actions are written as one JSON object per line in `logs/activity.jsonl`: commands, equipment moves, lottery prizes, granted items, fights, live administration and new unhandled packet shapes. Credentials, launcher tokens and game tickets are never included.

---

## ✅ Emulation status

✅ done · 🟡 partial · 🚧 in progress · ❌ missing

### 🖥️ Launcher
<img width="2560" height="1512" alt="image" src="https://github.com/user-attachments/assets/68e0e721-b36c-4524-b5d6-660fd5beb3c0" />
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/86f835e0-f161-4f51-af42-a810a192f150" />

Built with **Avalonia**, the same toolkit as the Studio.

- ✅ Three screens — *Play*, *Accounts*, *Settings* — with the server-status pill in the header
- ✅ Account cards with the character drawn in them — portrait, name and level. The portrait is assembled from the client's own bones, the same way Jondo Studio draws NPCs; no character image ships inside the executable
- ✅ The portrait shows the character as they look in the world: chosen head, real equipment and the cosmetics over it
- ✅ Persistent team of up to 8 accounts, one independent Dofus process each; the highest-level character of each account is the one shown
- ✅ Account creation and login, written to `auth.db`; credentials sealed with DPAPI
- ✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use ticket, socket-owned session
- ✅ Independent lifecycle indicators for profiles, processes and sockets
- ✅ Embedded server log; single-file deployment; ES/EN/FR
- ✅ HD and 4K scenery packs from Ankama's own CDN, for the client's own version only: resumable download, every chunk and file checked against its SHA-1, verify and remove, and `--hdReady` / `--4kReady` passed only for a verified pack (`docs/client-graphics.md`)
- ✅ Animated neon sign and falling stars
- ✅ Launcher and server are separate programs — the launcher carries no database, maps, handlers or effect catalogue
- 🚧 OAuth — loopback redirect and PKCE on the launcher side; the server half waits for a website

### 🧩 Server
<img width="2558" height="1508" alt="image" src="https://github.com/user-attachments/assets/df3cce87-166d-4f5a-8aff-a4fcd2575c87" />

`Jondo Server.exe`. The launcher starts it, but it can be run on its own — or on another machine.

- ✅ Four listeners in one process — Zaap (`8888`), game (`5555`), chat (`6337`) and HAAPI
  (`15881`), plus a self-signed certificate for the client's HTTPS
- ✅ One session per socket: every handler reads the session it is serving, so eight clients on one
  machine never see each other's state
- ✅ Its own window with the live log, the counters and the connected clients
- ✅ Regression guards that run at boot and refuse to start when the shipped data does not match
  what the code expects — see [Tests](#-tests)
- ✅ A loopback control API the launcher talks to: log tail, account login, and the characters of an
  account with the look already composed for drawing
- ✅ Runs on another machine: every listener honours `JONDO_PUBLIC_BIND`, and the launcher runs a
  loopback relay so the client reaches it (HAAPI and the chat server hand the client `127.0.0.1`)
- ✅ Unanswerable packets are recorded in their own database, deduplicated by protobuf shape

### 🔐 Connection and authentication

- ✅ Zaap, HAAPI and connection server emulation, VIP check bypassed
- ✅ Account creation and login against `auth.db`, with the password hashed and the attempt rate
  limited by the socket's IP
- ✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use
  ticket, socket-owned session
- ✅ Server and character selection, showing the mount being ridden and each character's equipment
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/c4c194ad-dcd1-407f-a3f1-b44c8f4baed2" />
<img width="2558" height="1504" alt="image" src="https://github.com/user-attachments/assets/70d02ad2-8fc0-4ec8-b836-1dda959ed271" />

- ✅ Character creation with a starter kit — Astrub zaap, adventurer set, 1,000,000 kamas, 100
  scrolled points per characteristic
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/881c9530-6631-46ed-b85e-c7fd92602455" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/09a94e1f-165a-407d-91f1-1dd7b18063de" />
<img width="2558" height="1510" alt="image" src="https://github.com/user-attachments/assets/a65407d0-7e65-4481-bdfe-ea65554bb29e" />

- ✅ Account roles, and an administrator-only channel over loopback
- ✅ Reconnecting into a fight. Close the client mid-fight and the fight goes on without you; log
  back in and the character still fighting is picked without a selection screen and put back on the
  board as it stands — the in-progress `kaa`, every fighter, the live buffs, and the current turn
  with the time it has left. Picking a character the ordinary way while it has a fight pending
  counts as a surrender, and the character enters the world on the roleplay map it left

### 🗺️ World and movement
- ✅ World loading, spawn, name hover, last cell and map persisted. Multiclient.
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/8882de29-36b0-4af9-be22-2d5f3bd4c6d4" />

- ✅ **15,360 maps**, **17,211** with walkable-cell data, **17,222** with combat cells
- ✅ Movement, map change and adjacent maps; auto-pilot from the minimap and *travel to*
<img width="538" height="452" alt="image" src="https://github.com/user-attachments/assets/a6438938-00c2-4a76-b4e1-48abf3d56934" />

- ✅ Seeing others arrive and leave, in all four directions: whoever walks off the map disappears from the others' screens (`kmu`), with the `jsd` before it for their party, as the captures send it
- ✅ Up to 8 clients at once, each on its own socket-owned session
- ✅ Everybody is drawn wearing their gear — the other players on the map, the opponent in a fight and every character on the selection screen. Equipment is read per character from `CharacterItems`

### 🌀 Travel

- ✅ **62 waypoints** with map, cell and sub-area, plus 3 departure-only zaaps the waypoint table omits
- ✅ Travel between zaaps with the real cost and destination list
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/1524d485-a845-4b62-a71c-b88de3bb7b54" />

- ✅ Discovered zaaps announced on world entry (`hjk`)
- ✅ Zaapis of Bonta (24) and Brakmar (21) at a flat 20 kamas
<img width="2560" height="1484" alt="image" src="https://github.com/user-attachments/assets/472e09f2-a49d-431e-8935-f60355457cdd" />

- ✅ The right window per list: `hjj` root field 0 zaap, 1 zaapi, 3 boat
- ✅ **16 temporal anomalies** with their 120-minute countdown, surfacing at vestiges (type 359)
<img width="2560" height="1514" alt="image" src="https://github.com/user-attachments/assets/942d4d71-9711-45f1-9156-5381f7ad14b8" />

- ✅ **3,815 interactive teleports** imported, 3,719 active across 2,655 maps
- ✅ Passages that fire when you step on the cell, hooked to the end of a walk — and floor passages with no element at all (`content/interactives/floor_passages.json`), for the maps that need a way out and have nothing to click
- ✅ The GM prison's three maps, all at [66,6] and joined by nothing on the world grid, made one round: the sky jail's trapdoor goes down to the dungeon, the dungeon's hanging cage lifts you to the island, and the island's treasure chest brings you back to the jail at the foot of the trapdoor. The chest wears the placeholder graphic of the olivioleta trees, so it was being offered as a tree to cut; a passage now makes it a door
- ✅ Each route carries its own interactive type
- 🟡 Every extracted passage still declares skill 114 (*Utilizar* on a zaap) where the game uses
  184; new passages written in Jondo Studio declare 184
- ✅ New passages can be created, both ways, from Jondo Studio
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/e3908060-2ad1-415c-a11a-cb6f323b9378" />

### 🏘️ Houses, bins and haven bags

- ✅ **1,437 doors on 553 maps**, all enterable; **261 house models** with name, price and room count. The **37 doors whose model is known** can be owned — per account, as the captures name the owner — and their owner, price and codes are kept in the base
<img width="1112" height="920" alt="image" src="https://github.com/user-attachments/assets/1506283c-f6cd-45b5-b9c4-f345273f67bb" />

- ✅ Entering and leaving (`jqw` in, `jru` out), coming out through the door you went in by
- ✅ The plaque (`lnx`) of a house with an owner, on its street and inside it, and the account's houses (`jaa`) at login; each viewer gets his own door — the owner *Vender* or *Modificar el precio de venta* and *Modificar el código*, anybody else *Entrar* and, on a house for sale, *Comprar*
- ✅ Selling and taking off sale from the door's own window (`khr` → `izv`/`izr` → `jan`), with the burst of the two captures byte for byte
- ✅ The access code — set, change, remove — and the keypad a stranger gets at a locked door; a wrong code is refused as the capture shows
- ✅ The house chests, one storage per house and chest, with their lock code: the owner opens and locks, anybody else opens and, if locked, types the code; items in and out as in the capture
- 🟡 Buying — *Comprar* on a house for sale, or on one nobody owns at its model's price — pays the seller's bank, hands it what the house's chests held, and clears the codes. No capture buys a house: the buyer's window and the message that confirms it are inferred, and the confirmation is only honoured when it carries the price that window showed
- 🟡 Also inferred: the plaque's `f5` read as "locked", getting in or opening a chest with the right code (only a wrong one was captured), and what the rest of the street is told
- ❌ The other 1,400 doors cannot be owned (no model, no price); houses shared with a guild, kicking someone out, the list of houses on sale
- ✅ **67 public bins on 63 maps**, shared by everybody and kept in the base: what one player throws in, anybody takes out — one unit per `-1`, the bin's stack sent again with what is left, as in the Bonta capture
- ✅ Haven bags: entering and leaving — whoever stays on the street stops seeing the one who went in —, their own zaap, **48 themes**, **4,083 furniture pieces** placed and persisted, chest with the full item flow (its own window kind, `-1` takes one unit, a moved stack gets a new uid), lottery machine, and no monsters inside
<img width="2560" height="1492" alt="image" src="https://github.com/user-attachments/assets/a81a3b24-8559-4ad5-8a27-e6913eef95a8" />

> Which house sits behind which door is not in the client data. The 1,437 doors share **114 genuine interiors**, assigned deterministically within their own neighbourhood; the mapping lives in `datos/casas_mundo_3.6.10.10.json` and can be corrected by hand.

### 🏦 Banks and marketplaces

- ✅ The bank, opened by the banker's "I want to consult my chest", as in the Bonta capture: one kama a stored stack to open it, the account's items and kamas shared by all its characters and kept in the base, items and kamas in and out — a moved stack gets a new uid, `-1` moves a single unit
- ✅ A banker in all ten banks of the client's world map: Bonta's where the capture has him; the other nine — Astrub, Brakmar, Amakna, Pandala, Sufokia, Frigost, Picanesburgo and two villages — inside their bank's first room near where Bonta's stands, each the client's own banker for the place (the Brakmarian, Moneo for the Saharach, Yendong for Pandala, the owl elsewhere). Their cells are inferred, no capture shows those maps
- ✅ The guild chest, in the **24 banks** that have one: the guild's own, kept in the base, opened as in the Bonta capture (`ivl`, `kbk`, `iwb`, `jlo`, `jlq`), with the ranks' rights to look, put in and take out — the client's own rights table, tab by tab
- ❌ The bank's level condition; the guild chest's extra tabs (bought with the guild hall's evolutions), its kamas and its history
- ✅ The marketplaces: the client's seven — resources, equipment, consumables, runes, creatures, souls and cosmetics — each one shared by every counter of its kind, **39 counters** in Bonta, Brakmar, Astrub, Pandala, Frigost, Sufokia, Incarnam and two more towns. Browse by type and by item, buy a lot of 1, 10, 100 or 1000 — into the bag, and its price into the seller's bank whether they are connected or not — put a lot on sale for the 2 % tax, take it back; 672 hours on sale. Measured in the five captures that open one
- 🟡 Changing the price of a lot on sale, one or several at once from the sell window: the tax is the client's own reckoning — the whole 2 % on a dearer price, 1 % on a cheaper one — and the lot keeps its time on sale. Read off the client's code (`kch`, answered `ken` + `kes`), no capture changes a price
- 🟡 The sales history window: every lot of the account sold, or come back unsold, over the last 30 days, with its kamas, date and marketplace — sent at login, after each sale or expiry while connected, and when the window opens (`lar` → `las`); the sale notice in the chat now carries the client's "venta" link that opens it. A seller's open sell window loses a lot as soon as it is sold, taken back or runs out (`ken`). Read off the client's code, no capture shows one
- ✅ The sale tax is the client's own: to the nearest kama (halves to even), never under 1 — the one captured sale, 999 kamas for 20, agrees
- 🟡 Inferred rather than captured: taking a lot back, the end of a lot's time (it returns to the seller's bank), the refusals, and what resources and consumables take. Six of the client's 45 marketplace hints have no counter we can tell apart from a door, and are left out
- ❌ Merchant mode: this 3.6 client has no screen for it — no window, menu entry or asset to set up a stall, manage its stock or buy from one — so it cannot be reached from the game and is not implemented

### 💬 Social

- ✅ Information messages as `lqn { type, message, parameters }` against the client's 2,555-entry table
- ✅ Level-up window with music and animation, on a real gain and on `.level` in either direction
<img width="2560" height="1514" alt="image" src="https://github.com/user-attachments/assets/490997fc-1300-4a29-9963-32077efdf0dd" />

- ✅ Private messages (`kth`)
- ✅ Last connection time and IP, stored per character
- ✅ Parties — invite, accept, refuse, leave, hand over the lead, kick, and a full member sheet
- ✅ Lead passes on when the leader leaves; a disconnect removes the member and tells the rest
- ✅ Friends list
- ✅ Trading with another player on the map: ask, refuse or accept, lay stacks down and take them back, kamas, ready on both sides, and the goods changing hands — a new stack under a new uid, or onto one of the same — measured on both sides in the two trade captures
- ✅ Every command answers in the session's own language, from a catalogue in Spanish, English and French. The language comes from the `--langCode` the launcher started the client with
- ✅ Following the leader: the member's client walks after him map by map on each `ikv` the server sends it, as the follow capture measures; a zaap cuts the follow, as on the real server
- ✅ Emotes from the emote bar, sitting included: played for everybody on the map (`khl` → `khh`,
  byte for byte against the juggling capture), refused in silence on a mount when the emote forbids
  it and too soon after the last one, as the captures refuse them. A new character has the four
  the creation captures show; the ones learned — an achievement's reward — are announced (`khi`)
  and kept
- ✅ Smileys over the head for the whole map (`hov` → `hoc`) and the mood smiley (`hor` → `hns`)
- ❌ Emotes learned from an item: consumables are not used yet
- ❌ The invitation popup's *Details* button (`imd` → `ilb`), the dedicated member-gone message (`inc`) and party search

### ⚜️ Guilds and raids

**Guilds**

- ✅ Founding: the altar of the Guild Temple (element 480310 on map 106169344, [0,−8] north of the
  Amakna village) opens the client's own editor; the `jjg` it sends back spends the guildalogem
  (item 1575), and the founder is redrawn with the guild under his name. `.gremio crear <nombre>`
  founds through the same path with a fixed emblem
- ✅ Gremia, outside the temple, sells the guildalogem for one kama, the book *Acerca de los gremios*
  for 500 and the guild shield for 100,000 (`content/npcs/shops.json`)
- ✅ The guild block travels in every map actor (`f5 { f4 { emblem, id, name, level } }`), so a
  guilded character shows the guild under their name to everyone
- ✅ The guild window: header (`jhh`), ranks (`jco`), member list (`jgu`) with class, level,
  achievement points, gremichas, online state and the leader's note; the guild comes with you into
  the world on login, rebuilt from the database
- ✅ The window answered request by request, as in the captures: opening (`jlk` → the chest's tabs
  and the header), the members only when `jml` asks for them, the perks' `jff` as an answer, and a
  tab change (`jii`) not at all. Login says you belong (`jhe`, with your contribution) and never
  "you have just joined", which only joining says (`jco` before `jgw`, as the client needs)
- ✅ The tabs this server keeps nothing for — perks, raids, the paged list, the collectors' —
  answered empty as a new guild's are (`jfv`, `jeu`, `jga`, `jgr`, `jet`, `jfw`, `hzc`, `hvx`), and
  the week's reset (`jew` → `jez`, Tuesday 05:00 UTC, in all five captures)
- ✅ Leave from the window (`jho`) or with `.gremio salir`; kick with `.gremio expulsar`
- ✅ Ranks — open, rename, set rights, create (`jcs`, `jct`, `jck`, `jcv`), each answered with the
  whole `jco`. Rights are stored as they arrive. `.gremio rango <personaje> <n>` assigns one
- ✅ Member notes (`jjj` → `jgz`), the guild log (`jim` → `jil`: founding and every join), the
  directory profile the leader writes (`jcc` → `jci`: description, level range, tags and title) and
  the directory search (`jjm` → `jme` + `jiv`: every guild with its leader, size and emblem)
- ✅ Applications and invitations both ways — apply, list, read one, accept
- ✅ Contributions — 10,000 kamas buy 10 guild kamas, five a week, the week turning on Tuesday
- ✅ The oracle shop, five oracles, priced by how many accounts the guild has
- ❌ The client's own requests for applying, inviting, kicking, assigning a rank and buying a raid
  are not handled; `.gremio` and `.raid` stand in
- ✅ The guild chest, in the banks — see [Banks and marketplaces](#-banks-and-marketplaces)
- ❌ The *Encargos* and *Casas* tabs

**Raids** — the Gigalodón Abyss and the Eternal Gardens Sanctuary — are bought with guild kamas
(360 and 480), launched by a captain and run against a clock: an hour the first, two the second.

- ✅ The instance carries the raid's named variables, `Raid_Score` and `n1..n5_worldlight`, which
  the content the client ships reads through its own criteria
- ✅ A criterion evaluator over the client's criterion language — `&`, `|`, parentheses — with a
  tri-state answer, so an unknown term is not read as false
- ✅ Monster aggression follows the monsters' own criteria in `world.db`: the Abyss monsters are
  immune while their floor has light
- ✅ The clock returns everyone to the map and cell they came from, and the captain can close the
  raid early
- ✅ Raid loot from the monsters' global loot table: depths salt at 30% (100% from the three floor
  guardians) and the seven gems, each monster with its own rates. A global loot row whose criterion
  cannot be evaluated does not drop
- ✅ The luminomachine, NPC 8007, one on each of the five lit floors: it offers the light bands the
  player can pay for, takes the salt and raises that floor's `nX_worldlight`. One more band costs 1,
  3, 6 and 10 salt; a jump pays the sum
- ✅ The chest at the far end, NPC 7861, with its two screens: drop every treasure in, or take it
  and end the raid for the whole team. Anyone may take it
- ✅ A treasure is any item carrying effect 4063, *Valor de un objeto*: the gems from Quartz at 2 to
  Ónix at 30, the three guardians' trophies at 1000, 5000 and 10000, and the salt at 1
- ✅ The chest fills up as the score rises, through the five looks of its template (5000, 13000,
  27000 and 45000 points). NPC templates with several looks and a criterion each pick the right one
  per player
- ✅ The weekly ladder, per raid, keeping each guild's best run of the week, ties broken by who got
  there first; `.raid clasificacion` prints it with the podium ornament of each place
- ❌ The podium ornaments are named, not granted: the wardrobe offers all 167 to everybody
- ❌ The raid panel — timer, score and light on screen; `.raid` prints them instead
- ❌ The Gigalodón fight when the clock beats you to the chest; the clock closes the raid
- ❌ The entry map and the positions of machines and chests are not taken from captures: the lowest
  map of each floor and the walkable cell nearest the middle are used

### 🎒 Character and inventory

- ✅ **21,748 item templates** and **66,294 item effects** — spawning, equipping, bags, destruction, persistence
- ✅ **929 item sets** with their bonuses
- ✅ **520 mounts** with their look, swapped and unequipped correctly
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/375da573-ab61-4bf0-83fd-6f2a8f872cde" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/581b105c-9569-4f54-ab77-01e122b8ce06" />

- ✅ Characteristic assignment, dynamic capital, points in sync across every client panel
- ✅ Scrolled characteristics kept apart from spent points: every character starts with 100 in each
  of the six, sent in the field the client draws as *Adicional*, and the capital counts only the
  points the player spent
<img width="708" height="1048" alt="image" src="https://github.com/user-attachments/assets/b07f0ac2-f701-4f3a-82e2-c04f884d696d" />

- ✅ **17,113 spells** across **34,823 spell levels**; **638 character heads**
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/02b5e575-20e3-47ed-b095-55443fb792ab" />

- ✅ **539 titles** and **167 ornaments**, applied, persisted and carried in the map actor block
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/0e579800-2776-4aba-8629-58bb2e6c7acf" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/25225e6b-2b6e-4aa6-8f56-12937b0754a0" />

- ✅ Life regeneration, run by the client and switched by the server: started on every return to a
  roleplay map (`ktz`) and stopped on the way into a fight (`kuq`), so a fight starts on the life
  the ticks earned — after a defeat, from half the maximum (characteristic 97 in the `kub`)
- ✅ Energy, out of the gauge of 10,000 (characteristics 29 and 47), kept per character and spent by
  lost fights against monsters; it never drops below 1, so nobody becomes a ghost
- ❌ Energy coming back — with rest or consumables; no capture measures the rate
- ✅ Commands — `.teleport [x,y]` or `.teleport <map id>`, `.kamas`, `.shop`, `.size`, `.level`, `.item`, `.itemset`, `.receta`, `.sueno`, `.gremio`, `.raid`; they answer with an information line only their author sees
- ✅ The administrator's window, on **F10** in the client: items, a character's level, characteristics and kamas, teleports, NPCs and monsters spawned and removed, and the jail — see **[Admin window (F10)](#-admin-window-f10)**
- ✅ Monsters at their size: groups on the map and fighters carry the look's colours, scale and skins as the official server sends them, so Conde Kontatrás is as tall as a person and not a doll
- ✅ Live administration over HTTP — `POST /api/personaje` sets characteristics, kamas and level, grants items (at their maximum or rolled) or a mount, and teleports a connected character without a reconnect. `POST /api/rol` changes account roles. Administrator only, loopback only
- 🟡 `.level` repaints the in-fight spell bar, but the fighter's own level is not updated until the next fight

### 👕 Appearances
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/30ee645b-191b-4146-9966-d2c3fb72a9cf" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/2655dfd1-d565-484c-9735-54dd00f4f8b0" />

Dofus does not ship the item-to-look table: the server sends it. **2,371 of the 2,420 cosmetics** in the catalogue are covered.

| Type | Working / catalogue | | Type | Working / catalogue |
|---|---:|---|---|---:|
| Shields | 524 / 524 | | Petmounts | 151 / 151 |
| Hats | 464 / 464 | | Mounts | 121 / 121 |
| Capes | 357 / 357 | | Shoulders | 121 / 121 |
| Pets | 242 / 242 | | Costumes | 92 / 92 |
| Weapons | 194 / 194 | | Living objects | 61 / 61 |
| Wings | 44 / 44 | | Miscellaneous | 0 / 49 |

- ✅ Appearance weapons carry no look — the client draws them; the server remembers which of the 10 weapon slots each occupies
- ✅ Living objects imitate a different garment per variant, stored as **543 object/variant pairs** across 10 slots
- ✅ Mount and pet appearances are mutually exclusive
- ✅ The real equipment renders too, and a cosmetic replaces it rather than stacking on top: **741 real items** carry their own skin into the look
- ✅ The same skin list feeds the launcher's portraits
- 🟡 82 skins were inferred by image matching and are held back at load until verified
- 🟡 A second look path survives in `InventoryHandler` for four items
- ❌ Per-character colours: every look is composed from the breed's default palette

### ⛏️ Professions

- ✅ **25,090 resources on 4,507 maps** across the six gathering jobs
- ✅ The three states — full, depleted, busy
- ✅ Job levels and experience persisted, with the curve `10 × level × (level − 1)`

- ✅ What you gather lands in the inventory, and the amount grows with job level
- ✅ Too low a job level blocks gathering
- ✅ **577 workshop stations** on the world's maps, recognised by their graphic: 21 declared in the captures' `jss`, 2 seen used, 15 from PR #44's Incarnam captures and 16 found inside the workshops of a one-skill job (tailor, shoemaker, sculptor, smith, jeweller, handyman, hunter, fisherman...)
- ✅ The craft window of every job with any of the **4,858 recipes**: pick a recipe or lay the ingredients by hand, craft one or many, the job's level asked for
- ✅ Crafted equipment rolls each characteristic in its own range; what rolls nothing joins a stack
<img width="2488" height="1396" alt="image" src="https://github.com/user-attachments/assets/9651bea6-a649-4154-9389-f30c1da7ea58" />

- ✅ Craft experience `⌊20 · recipe level / (1 + 0.1 · gap^1.1)⌋` — the tutorial's +20 — and the level-up window (`isz`), now for gathering too
- ✅ Smithmagic on the six magus tables: clean success, partial success (it enters and costs weight elsewhere) and failure, with the client's own rune weights, the pool, over and exo up to a weight of 101, exo AP/MP/range at 1%
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/b4b5f143-fe17-4e42-b656-407480a0414c" />
- ✅ Signature runes: "Fabricado por" on a craft, "Modificado por" on a magus table, stored in the item itself
- 🟡 The odds of a rune are the community's model (66/34/0 on a weak item, 43/50/7 at the perfect jet, a 15% floor, a rune's reach of 30·√weight); Ankama never published theirs
- ✅ Maging for someone else: invite a customer or a magus from a magus table, the customer lays their item, runes and signature, pays when a rune went on their item; the magus' side measured whole, the customer's mirrored
- ✅ Breaking items at the grinder into their base runes, `(3 · value · weight · level / 200 + 1) · coefficient`
- ✅ The breaking focus: the focused characteristic takes half the weight of every other
<img width="1378" height="1218" alt="image" src="https://github.com/user-attachments/assets/acb6b847-1ab0-4966-83db-657fd0fa38c3" />
- ✅ The artisans' directory: each job's settings (free, minimum level) kept per character, the public list, and the book of every workshop opening its jobs
- ✅ Transcendence runes: 100% of success within the density rule of their own data, never over an over or an exo, and the item closed to smithmagic afterwards
- ➖ Corruption runes: not in the 3.6.10 game data (Ankama withdrew them in 2.51); only their help text remains
- ✅ `.oficios [level]` puts every job at a level (200 by default), `.oficio <job> <level>` one of them
- ✅ `.receta <item> [times]` puts the ingredients of an item's recipe in the bag, each onto the stack already there
- ✅ Forgegod mode for administrators (`.forjadios on|off`, `.forgegod`, `.forgedieu`): no rune fails, no weight cap, two AP of exo, transcendence on anything, no job level on recipes
<img width="1370" height="1182" alt="image" src="https://github.com/user-attachments/assets/751cb412-d2de-4b21-b91e-498342754dce" />

### 👹 NPCs and monsters
<img width="954" height="836" alt="image" src="https://github.com/user-attachments/assets/78779a18-0cd2-4f5c-b403-0c39cd291bcb" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/43ebcfc4-fdbb-4924-b876-0c06743f8294" />
<img width="2558" height="1510" alt="image" src="https://github.com/user-attachments/assets/0b4adf75-9b36-4298-b428-d0444297adb3" />

- ✅ **6,468 NPC templates** with 3D looks and dialogue trees
- ✅ **422 NPCs** standing where Ankama puts them across **202 maps**, with dialogue attached where known
- ✅ **5,134 monsters** with native Protobuf bone models, custom scales and textures, quest monsters and archmonsters included
<img width="1700" height="930" alt="image" src="https://github.com/user-attachments/assets/02254e58-ec87-4839-82ac-f142ec5ef9cd" />

- ✅ **38,744 mapped mob groups**, respawned and kept populated, 1 to 8 monsters each
- ✅ Sub-area aware spawning across **562 sub-areas**, with radius-2 cell validation so nothing spawns on decorations or zaap pillars
- ✅ No monsters indoors, and none standing on a zaap — not in houses, banks or shops; the 763 dungeon rooms are exempt. 7,214 groups of 38,744 kept out
- ✅ NPC colours read as `index=value` pairs, decimal or hexadecimal: the **2,045 NPCs that carry colours** render with theirs
- ✅ A dialogue always offers at least one real reply, so it can always be closed
- 🟡 **401 monsters have no spells at all** in the database
- ✅ Dialogue trees — which reply leads to which line — are authored in `content/npcs/dialogues.json`; the client's data does not hold that mapping
<img width="1138" height="694" alt="image" src="https://github.com/user-attachments/assets/fc1182c3-a261-4bcd-9532-84a2ceda8dc8" />
<img width="1082" height="692" alt="image" src="https://github.com/user-attachments/assets/39cdf857-b506-4968-b2f9-c0c5f80b64c3" />

- ✅ Monster groups placed by hand, and Ankama's own removable, in `content/monsters/groups.json`
- ✅ The kanojedo of the Amakna village (map 99090957): its door, six Puch Ingball inside — one per
  grade from level 1 to level 200, never moving and never replaced — and the master, NPC 7416,
  whose two screens set a session up: six levels, then one to four puchs, and the fight opens on the
  spot. Themed puchs — Vil Smis, Sombra, Hiperescampo, Sylargh, Cráneo Rosa — take turns with the
  Ingball at random among those with a grade at that level. Training fights offer no challenges,
  give no rewards and leave the group in place
- ✅ Measured arenas in `content/fights/arenas.json` pair a roleplay map with the arena its fights
  are held on, ahead of the general rule

### 📜 Quests
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/6dbe2000-4f3c-4b41-9409-5be932f84d6e" />
<img width="1452" height="1226" alt="image" src="https://github.com/user-attachments/assets/77256193-a9dd-48df-9d0a-5408613fef34" />

**1,976 quests**, with their 2,225 steps and 15,547 objectives.

- ✅ A quest is handed over by an NPC saying a particular line — 1,260 steps declare one
- ✅ Objectives complete two ways: the client reports the **5,670** that ask you to click something
  the server never sees, and the server counts the ones that ask you to beat a monster
- ✅ Progress is written the moment it changes
- ✅ A finished step pays its experience and kamas: the ratios are ratios of the client's own
  formula, read out of its code and exact against the tutorial capture (quest 1629 pays 141 at
  level 2, with that character's 5 % bonus). Only the reward of the character's level bracket is
  paid, and emotes are taught
- 🟡 The start condition language has **29 operators**; six are understood, covering every term of
  **935 of the 1,976** conditions, plus `Ad`, the Almanax day. The rest are let through and named

Full workings in **`docs/quests.md`**.

### 🏆 Achievements

**2,780 achievements** in 134 categories, with 8,946 objectives and 6,394 rewards, all from the
client's own data — and the 272 objectives the client names but does not describe, tied to a zone,
a level, a job, a quest or a monster by the achievement's own name and description.

- ✅ The achievement window: the achievements closest to being earned when it opens, each category
  with every achievement and every objective's progress — a tally drawn as 91 of 100 — and the
  list of what is earned on entering the world. Byte for byte against the one capture that opens it
- ✅ Earned the way the game earns them: finishing quests, exploring a zone (the 17 exploration
  achievements the captures earn all fire on entering the subarea they are named after), character
  and job levels, crafting, monsters beaten — in their dungeon for the dungeon ones, with a challenge
  won for the family ones —, dungeon challenges validated, items held, achievement points and
  achievements built on others. **2,172 of 2,780** have every objective in terms this engine
  judges; the Temporis ones (`SC=5`) are judged false on a classic server
- ✅ The notification (`mfu`) when one is earned; the reward only when it is claimed (`mga`), once,
  answered as the captures answer it: the kamas, the character sheet and the experience gained, the
  items, the emotes, and `mfs`
- ✅ Experience and kamas by the client's formula, exact against the nine claims in the captures
  once the bonus those characters had (5 %, and 110 % for one) is counted in
- 🟡 That per-character experience bonus is not modelled: a claim here pays the base
- ✅ Kept per character, with the tallies they count, in tables created at startup
- 🟡 Titles and ornaments are logged and not sent: every character is already offered all 539
  and 167
- ❌ BI, Sc, EB, HD, EI, lB, Pr and the other operators the server judges by itself for the rest:
  breeding mounts, eating sweets, leagues, alignment ranks, the tutorial's first part
- ❌ Somebody else's achievement announced in the chat (`mgc`), and guild points as a reward

Full workings in **`docs/achievements.md`**.

### 📅 Almanax

The calendar is the client's own: **376 days**, each with its saint, its offering quest and its
bonuses. No capture visits the sanctuary, so all of this is **inferred** from the data and runs
through the quest engine.

- ✅ Today's entry: every day of the year resolves to one — the month's saint, or the moveable feast
  on its date, and Bryss where he stands in. It answers the offering quests' own `Ad` condition
- ✅ Ontoral Zo hands over today's offering, "Ofrenda para …", and marks it over his head; bringing
  the offering, seeing the saint and going back to him close its objectives; its reward is the one
  of the character's level bracket, experience and kamas included; once a day
- 🟡 The saint of the day stands only where the client's data places him: 80 of the 373 saints. On
  the other days the "see the saint" objective cannot be closed
- 🟡 "Reza ante el altar" is free text, closed by the client's own report, as every free-text
  objective is; whether the client reports it at the altar is not measured
- 🟡 Of the day's bonuses, the ones with no condition that touch quests and jobs are applied —
  quest experience, quest kamas, job experience, each named by the one day whose own text says what
  it does
- ❌ The rest — monsters' experience and drops, harvests, challenges — come with conditions whose
  types the client's data does not explain, and are named, not applied

### 🏰 Dungeons
<img width="2550" height="1498" alt="image" src="https://github.com/user-attachments/assets/f79f7881-c68e-45b5-ae29-b4aaba928a1d" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/3c73a696-1de3-46ae-bea6-149bc06009fb" />
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/7b481a47-2fea-43da-a8a0-5b2692030473" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/a6347069-3d31-45bc-b40f-f4d942674e48" />

**187 dungeons**, with their **763 rooms**, their key and their boss.

- ✅ Talk to the guardian, hand over the key, and you are in the first room; win a fight and you
  move on; beat the boss in the last one and you come out
- ✅ The boss is placed at startup in **126** dungeons, in the room the data says, at its highest grade
- ✅ Each room has one group of eight built from the dungeon's own monsters, and a fight takes the first `clamp(players, 4, 8)` — four for a player alone — at the room's grade, as the jalatós capture shows; the map carries the group's variants by team size byte for byte, and the monster side grows by one with each player who joins the fight, up to eight; a beaten room comes back as itself, boss included
- ✅ The keyring and the required item come from the client's own data
- ✅ Dungeon challenges are imposed at 0% and carry achievements

> Ankama's dungeons are chains of rooms walked through doors, and none of the 187 has its internal
> passages in the extracted data or in Ankama's own world graph, so winning a fight moves you to the
> next room instead.

Full workings in **`docs/dungeons.md`**.

### 🌙 Infinite Dreams

Entered from the Plano Astral's well: a dream of 26 rooms in depth, walked band by band.
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/f33f6c10-6889-41f6-8a25-cc17068b7191" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/3bd5a03b-10e0-4dad-8227-627a5f2abf7d" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/65ae17bf-b8cc-4b3c-a811-3258e7c9bfd4" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/27bdff8a-abac-49da-8e6b-1dc2c72b6095" />
<img width="2560" height="1496" alt="image" src="https://github.com/user-attachments/assets/002bdcb9-da34-44ff-95c3-b2f84e578c6b" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/7b2732db-be45-44a2-b405-282091d35f4a" />

- ✅ Ten difficulties in three families (Sueño, Paradoja, Pesadilla), each with its measured starting bonus, dream points, astral storms and Draconiros arena
- ✅ Five bands, as the invitation capture measures them: fountains at rows 4, 10, 16 and 25, band IV closed by one fight room alone, and the **Fin du rêve** at row 26
- ✅ Every fight room pays its reward and its dream points on entry — 5, 15 for the marked ones, 10 in band V — and the HUD shows the score, the points, the bonuses summed and the dreamer levels. A reward is what the client's own reward rows say: a bonus, 15 or 30 dream points, an astral storm and five points, or 50 dreamer levels
- ✅ **Dream favours** (Faveur Onirique): in bands II to IV, three bands in four, one room of kind 2 with the Dispensador de favores. "Acepto el favor." opens the client's shop window in favour mode on three free choices — two bonuses and the purse of 10 dream points, always last. Its doors stay shut until one is chosen; an astral storm draws the two bonuses again
- ✅ Every door shows what is behind it — fountain, favour, fight, difficult fight or the Fin du rêve — with the portal type the client's own staging sequences read (it was the fight's for every door)
- ✅ **Dream loot**: a won fight pays each winner ten dream reflections per 100 % of the room's loot bonus, rounded up (17 at 168 %, as in the capture), and rolls the dream's own loot table — astral runes by palier from Paradoja I, legends, Sueñoscudos — at the room's bonus, which grows a tenth a palier and a twentieth in a marked room. No kamas and none of the monsters' world loot, as in the capture; the Jondo coin still drops
- ✅ The Fin du rêve pays its intensity's dream fragments (Retazo de sueño) for every wave that fell — 25 a wave in Sueño I up to 1000 in Pesadilla III — once the dream is finished, even to a dreamer who falls after its minimum of waves
- ✅ The bestiary, the loot table and the placement map of the room one stands in
- ✅ The fountains' shop (Rey Gob one fountain in four): bonuses, spells and dream points bought with dream points; the Rey Gob's favour — dream points × 1.5 — once per fountain
- ✅ Astral storms reroll the room's group and map; a dream is saved to the base and resumed after a disconnection or a restart
- ✅ The Fin du rêve in waves of bosses, wanted monsters and high-level monsters: level 250 +5 a wave (1 to win, 5 at most) in a Sueño, 275 +10 (3 to win, 15 at most) in a Paradoja, 300 +15 (3 to win, no end) in a Pesadilla. Winning it, or falling after the waves it takes, ends the dream won
- ✅ A lost fight spends the Draconiros arena and the room can be tried again; with no arena left the dream is lost
- ✅ The dream's interface only on the dream's own maps: leaving by any way — its exit, the Merkasako, a zaap, a teleport — closes it (`ixg`, sent on its own as the real server does), and coming back onto a dream's map from outside puts the player back in the room, its group and its interface as on waking there
- ✅ `.sueno [row]` (`.sueño`, `.dream`, `.reve`), administrators only: carries the dream in progress down its own graph to a room of that row, or to the Fin du rêve with no row — every room on the way entered and won as if fought, its bonus and dream points paid
- 🟡 Monsters are brought to the Fin du rêve's level by scaling their life and characteristics; the game's own scaling is not known, and the other rooms fight at the world groups' own grades
- 🟡 Inferred, not captured: the favour room's map and where its NPC stands, which bonuses a favour draws from, and the loot bonus rule past the three rooms and the guide's example it was read from. Dreamer levels are counted and shown, and applied nowhere; a dream fight's experience is still the world monsters'
- ❌ The effects of the spells the shop sells
- ❌ The dream market and the arenero's exchange for reflections: the market's two maps are in the client (238683394 and 238685442, "Mercado onírico"), but no capture or data puts its merchants (Naru Stalar, Goblastral) on them, and the arenero's only placement — a quest objective — is on map 195559426, which this client does not have

Where each part comes from — the captures, the client's DataRoots, the client's code and the dofuspourlesnoobs guide — is written next to the code, in `Managers/Dreams.cs`, `Managers/DreamData.cs` and `Network/DreamProtocol.cs`; the client's tables and the captured loot table are in `datos/suenos_3.6.10.10.json`.

### 🪙 Jondo Coin

A currency of this server's own — a real item with its own template.
<img width="1676" height="1102" alt="image" src="https://github.com/user-attachments/assets/aee2eb3f-b2a3-4c35-a35c-fafe69669355" />

- ✅ Drops from every monster at 100%, one coin per 25 monster levels: 1 for 1-25, 2 for 26-50, up to 9 at 201+
- ✅ Its own description in the five client languages, picked at runtime from the language the client is running in
- ✅ Vendors that charge in coins instead of kamas, one per category, appearance shops among them, priced by item type and rarity

See `docs/jondo-coin.md`.

---

## ⚔️ One engine, four rulebooks

One fight engine serves four kinds of fight. What changes between them comes from a rules object:

| | Against monsters | Duel | Koliseo | Training |
|---|:---:|:---:|:---:|:---:|
| Challenges offered | yes | no | no | no |
| Placement clock | 45.0 s | — | 59.2 s | 45.0 s |
| `kam` type | 4 | 0 | 7 | 4 |
| `kaa` countdown | yes | no | yes | yes |
| Monster loot and experience | yes | no | no | no |
| Koliseo payout | no | no | yes | no |
| Clears the group on a win | yes | no | no | no |
| Moves to the next room | yes | no | no | no |
| A defeat costs energy, half the life and the way home | yes | no | no | no |

Two rules hold the rest together:

* The teams are `Azul` and `Rojo`, not players and monsters: in a duel both sides are people.
* Everything sent to a client is composed inside that client's own session, from each fighter's own record.

Three architecture tests enforce it: no lookups that assume one team is the players, no rules decided by fight type outside the rules object, and nothing writing to a single socket unless it is painting one person's own view.

### 🐉 PvM combat
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/d5fdf2d1-0244-4529-b2b9-06cf3dcdc1e5" />

- ✅ Tactical arenas resolved from each roleplay map, with clean context transitions
- ✅ Placement phase with red and blue tiles and cell swapping before *Ready*
- ✅ Isometric geometry (`MapGeometry`) over a pre-computed O(1) BFS distance matrix, with no diagonal steps
- ✅ Line of sight traced between cell centres against the arena's own blocker set
- ✅ Turn protocol, 30-second timers with automatic pass, AP/MP replenishment
- ✅ Unused turn time is kept: a character who passes keeps half of what was left (the `jyt`'s f1), carried into his next turn (the `jzc`'s f4) and onto its clock, as the captures do — up to a turn of a minute and a half with it (the captures stop at a minute). Monsters, summons and JondoBots keep none
- ✅ Movement with per-tile MP cost and collision against occupied cells
- ✅ Loot, victory and defeat screens, experience over **1,889 levels**, level-ups and group respawn
- ✅ The end waits for the client, as every fight end of the captures does: the last sequence, a `jxh` naming whose turn it was, and the result screen only once the client's `jwz` says it has played it all — so the blows that end a fight are seen, whoever lands them. A poison that kills at its victim's turn start ends the fight there too
- ✅ End-of-fight statistics — damage dealt by source (own casts, glyphs and walls, summons, turn triggers, pushes), taken, heals given and received, shields, enemies defeated, and the per-turn and per-AP averages, each player getting their own numbers
- ✅ Monsters and bosses run their own spells' mechanics: the behaviour spell cast at the start, triggered rows armed on every fighter they name, 30+ triggers (damage by element, heals, states on and off, pushes and collisions, thresholds, deaths), state disabling (952), telefrags, delayed sub-casts, life thresholds, revives, glyphs shown in their own colours — Conde Kontatrás's clock works end to end. See **`docs/bosses.md`**
- ✅ Monster AI that plans its turn: every spell it can pay for, against every target, from every cell its MP reach — the blow against the target's resistance, kills first and the weakest enemy focused, heals for the badly wounded, AP/MP removal, buffs and summons once a turn; cooldowns, casts per turn and per target honoured; then it places itself (ranged at its reach, melee against the weakest to lock him, fleeing when nearly dead). Every cell it plans to leave next to an enemy is charged the tackle it will pay, so a held monster neither plans a retreat its MP will not cover nor a cast the lost AP will not pay for
- 🟡 Weapon strikes apply damage and AP cost; the slash animation does not
- ✅ Push and collision damage, `blockedCells × (level/2 + push − resistance + 32) / 4`, floored. The fighter acting as the wall takes half, and the **Unmovable** state cancels it
- ✅ Joining someone else's fight in its placement: the swords on the map, a click on them (`kay`), or a party member pulled in behind the leader with *automatic entry*, and *automatic ready*; a dungeon's monster side grows with each player to the first `clamp(players, 4, 8)` of the room's eight
- ✅ A party opens its fights kept to the party, as the real server does, and the side's leader switches the options from the fight window — no spectators, party only, closed, asking for help (`jzx` → `kau`); an outsider knocking on a party-only side is turned down (`jxs` 16)
- ✅ A won fight is shared: the experience with the game's group bonus, each player's part by level up to two and a half times the strongest monster's; the kamas by prospecting; the items rolled for each player; and every end screen lists everybody's gains, as the follow capture shows. A player alone gets what he always got
- 🟡 Wisdom, the experience given to a mount or a guild and account bonuses are not modelled, alone or in a group; refusals other than a party-only side are not answered
- ✅ A dropped client does not stop the fight, and the player can come back into it — see
  [Connection and authentication](#-connection-and-authentication)
- ✅ Tackle and escape, for players, monsters and summons alike: every cell left next to an enemy
  keeps `(escape + 2) / (2 × (tackle + 2))` of the AP and MP, the loss rounded half up — the seven
  tackles of the captures, to the point. Sent as the real server does, inside the walk: `jwe 104`
  naming the tacklers, then each loss behind its sheet (`101` AP, `127` MP), then the path; a path
  that walks into contact pays where it arrives, and one the tackle leaves without MP stops there
- ✅ Who does not tackle or is not tackled: templates without the client's `CanTackle` bit (the
  training dummies), states flagged *cantTackle* / *cantBeTackled* (No Placable, Arraigado…), the
  invisible, the carried and the dead. Monsters tackle and escape with a tenth of their agility plus
  their grade's bonus, as their captured sheets do
- 🟡 Several tacklers at once each keep their own share of what is left, which no capture shows;
  summons tackle with none of their own, since their tackle and escape are not derived from their
  agility; the "when tackled" triggers of items are not fired
- ✅ Losing against monsters: energy down by ten per level up to 200 (2,000 at 354, never below 1)
  with its "Has perdido … puntos de energía", half the maximum life missing, and back beside the
  zaap of the save point — a duel, the Koliseo, the kanojedo and a dream cost nothing
- 🟡 The save point is the Astrub zaap every character starts beside: saving another is in no capture
- ❌ The anomaly's defeat back to its vestige, and ghosts, tombs and phoenixes, which the energy never reaches

### 🤺 Duels
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/286367e0-6342-4aef-b07b-52d3bdbdf9d4" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/c1f3f058-81ea-4189-8f4b-312e3209f63a" />

Player against player, on the map, by challenging somebody standing there.

- ✅ Offer, accept and refuse, with the challenge id echoed through every frame of the fight
- ✅ Both fighters composed from their own character record — look, level, characteristics, equipment
- ✅ Placement with no clock, and no challenges offered
- ✅ Victory and defeat screens, each player's own, and both sides returned to the map
- ✅ Nothing is won and nothing is lost — no experience, no kamas, no loot
- ✅ The end-of-fight card shows the other player's portrait

### 🏟️ Koliseo
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/2b19a035-4124-41d6-9c43-0c6881f69e40" />
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/417c9bf9-c895-4a7a-8de3-ef394ce5392c" />
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/0a3bc3d6-ce61-4729-aa23-958a024c07fb" />
<img width="2560" height="1496" alt="image" src="https://github.com/user-attachments/assets/dfb0aeb1-b507-48b0-8e05-286c3fa1945e" />

Ranked PvP through a queue. Open the window, pick a format, get matched, fight, get paid.

- ✅ The format table (`lux` → `ltd`) — 1v1, 2v2, 3v3 and a fourth card, **1v1 against a JondoBot**
- ✅ **JondoBots**, on the Koliseo window's own fourth card (the client's "event" card, open and made a 1v1): enrol and a JondoBot is drawn at once — a random class, none of the last eight that player faced, level 200, 12 AP, 6 MP, 1500 in every element, 6666 life, +6 range, 30 % critical (rolled on every cast, as a player's), 20 % resistance everywhere, +3 summons, one variant of each spell pair at random at its level-200 grade, and its class's passive. Shown as a character of its class wearing the look of one of the notable NPCs of that class (placed in the world, with dialogue, dressed) when it has any, half again as big as a character (size 150), dressed in one epic set drawn at random — the hat, cape and shield of a single set of level 100 or more, 81 of them with two or three pieces whose skin is known, each piece in place of whatever the NPC wore on that slot — and riding a mount drawn among the 332 looks of mounts.json and the 272 measured appearance mounts, all of it drawn in the fight; played by the server's tactics; its summons play themselves too The 1v1's own flow: *searching*, the match-found popup, accept or the sanction for letting it run out. It pays as a Koliseo and does not touch the ladder; the normal modes never meet a bot
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/7506287d-71b4-40a6-ad76-d4e67625aeac" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/53350da7-e8e9-42b3-85a2-cc8c473a4310" />
<img width="2560" height="1512" alt="image" src="https://github.com/user-attachments/assets/f468a367-7a7e-485b-a552-2ee143b0f05a" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/71b362a6-6893-4652-ad38-e4073343ddb1" />

- ✅ The Koliseo window, through JondoFix: with four cards shown it is widened from 1,328 to 1,760 so each keeps the 416 it is laid out for (read off the client's own UXML), and kept centred; the fourth card is called *JondoBots Mortales* / *JondoBots of Doom* (and in French, Portuguese and German) with its one-line description, in the language the launcher starts the client in; its *view the rules* button, which asked for a guide article this client lacks and showed the Abono's, opens the JondoBots' rules in a WindowFigma — the client's own window, frame, title bar and close button
- ✅ **The JondoBots' tactics read their spells as the fight will apply them**, row by row: which cells each row covers (lines, crosses, circles) from where the bot would stand, whom it touches there by the engine's own reading of its mask — sides, a template such as the Forjalanza's lance, states, class — and what it does to each: the blows summed per enemy (a kill and a weak enemy worth more), heals to a wounded ally, each point of a characteristic by what it is (an AP is a spell, a hundred of an element a few percent of every blow; points taken only up to the ones the enemy has), a summon once, toward the enemy and only within the fight's summon limit, and the sub-spells a row casts, followed. Poisons and hooks count a little less. A buff that raises the coming blow goes first when it is worth half of it; a ranged bot ends its turn out of the enemy's sight and not stuck to him; defence is worth twice as much under half the life. Every fighter in between blocks its sight, as a pillar does. A trap is worth its own spell on whoever steps on it — only the rows whose zone takes his cell in — times the odds he walks there: best on his way to the bot, then next to it, less anywhere else he can reach, less again for each trap of its side already out, and never two on one cell nor a summon on one of its own. Going invisible is worth a twentieth of the life kept out of reach, half as much again under half the life; the Sram's double goes out as a summon does. A poison or a hook bites once a turn for as long as it lasts, each turn a little less sure. An invisible enemy is not aimed at where he stands: the bot knows only where it last saw him — where he went invisible, then each cell he casts from — and throws its blows there on a guess. What it replaced read every positive number of a spell as a buff — every state became five thousand points — and threw the Forjalanza's lance on the cells next to it, behind itself on its first turn
- ✅ Enrolling (`lsm`), with the format carried as the client's own enum
- ✅ The queue state (`lsx`) pushed back, which paints *searching* in the window
- ✅ Leaving the queue (`lsi`, read from the client): the window's button takes out the party that enrolled together, and each window goes back to *search a fight* (`lsx` with reason 3). On the JondoBot card it withdraws the drawn fight without a sanction
- ✅ Matchmaking by rating: the oldest in the queue is served first, the closest ratings are taken, and the two sides are split so their average ratings are as even as can be. The rating window starts at 150 points and widens 10 a second of waiting; the queue is looked at again every 5 seconds
- ✅ Levels kept apart: nobody faces, or fights beside, someone more than 20 levels away — however long the wait — unless both are placed and within 100 rating points, the ladder saying they are even. A party enrolled together is one unit: never split, always one side, exempt from its own gaps
- ✅ Everybody re-checked as still connected before anyone loses their place in the queue
- ✅ The fight itself, with the Koliseo rulebook, and both sides returned to roleplay at the end
- ✅ The winner is paid — kamas, Kolichas (item 12736), Vitorichas (34478) and experience. The loser gets nothing
- 🟡 The amounts are constants, not a formula; experience is 6.67% of the winner's level band
- 🚧 The *match found* popup with accept and refuse
- ✅ Fights are held on one of the Koliseo's own arenas, drawn at random among those with room for both sides: 394 of the 441 maps of its three subareas, with the placement cells of the client's map data. The 46 without a name are left out — three of them are no board at all, every one of their 522 cells walkable in a fight where the drawn board has 253, and a fighter walked out into the void on a map the client titled "Amakna 0,0"
- ✅ The ladder of the current game (the December 2023 rework): one hidden rating per mode, and the league it sets — the client's **26 leagues**, Bronze, Silver, Gold, Platinum and Diamond in five divisions each, and Legend, straight from its `ArenaLeaguesDataRoot`. Each division overlaps the next by 50 points, the official "buffer": a player keeps his division while his rating stays inside it, and goes up or down when it leaves it
- ✅ Five placement fights per mode before the first league, as the captures show; wins and fights of the season and of the day; the season's best league. All of it in the Koliseo window through `lty`, at world entry and after each fight — the world entry's byte for byte for an unplaced character
- 🟡 Inferred, the official figures being qualitative: the rating moves on the Elo curve, 45 points for an even fight (K = 90, twice that in placement), so that 3–4 wins change a division as Ankama says; the starting rating is five a level (1000 at 200); a season lasts 91 days and starts everything again. Levels gained outside the Koliseo and a change of class do not move the rating; the season's ornament and title are not given
- ❌ The Legend ranking lists (`iqt`, `irc`)
- ❌ The `lst` redirect to a separate Koliseo server. Jondo is one server and holds the fight in place

### ✨ Spell effect engine

One engine for all eighteen classes, driven entirely by client data: everything comes out of
`SpellLevels.EffectsJson` and the `Effects` catalogue, and each thing a spell can do — push, shield,
carry, summon, copy — is one primitive that every spell using it shares. Of the **179 effects the
836 class spells use, 116 have a branch in the engine and 63 need no code** — they are
characteristics read from the `Effects` table — **and none is missing**. The table, effect by effect
with how many spells each touches, is [`docs/effect-coverage.txt`](docs/effect-coverage.txt).

- ✅ Effects, triggers and target masks read from the spell — `I` on cast, `TB` turn start, `TE` turn end, `DBE` when hit by an enemy, `DM`/`DR` when hurt in melee or at range, `DS` by a spell, `DT` by a trap, `X` on death, `CCMPARR`/`CMPARR` per tile walked, `CI` on summoning, `CC` on a critical hit, `K` on a kill, `CS` on putting a shield, `DIS` on being dispelled, `MA`/`MS`/`PO`/`CPD` pulled, swapped, moving somebody, dealing push damage, `CAPAS`/`CMPAS` on taking AP or MP, `R` on losing range, `PT` on crossing a portal, `CPT` when somebody crosses one of the bearer's, `PST` on casting through one; `a` allies (the caster included when he stands in his own zone), `A` enemies, `g` the other allies, `c` the caster when he is in the zone, `l`/`L` and `H` the players, `m`/`M` the monsters, `i`/`I` and `j`/`J` the summons — lower case the caster's side, upper case the other —, `O`/`o` whoever dealt the triggering blow, `u` the summon whose coming set it off, `K` the one the caster carried, `P`/`p` the caster's own summons, `h` the caster's summoner, `T`/`W`/`U` the telefragged, the teleport that found no cell and the one just summoned, `B<n>`/`b<n>` a class or not, `PB`/`pb` with a shield or without, `R`/`r` through a portal or not, `E<n>`/`e<n>` gated on a state, `V<n>`/`v<n>` on a life threshold, `*E<n>`/`*e<n>` conditions on the caster. States and positions are judged on one snapshot taken before the cast: a row after a pull still reaches whoever was in the zone. Many of the triggers are read off the sheets that name them and fire in no capture; the code says which
- ✅ Rows for the client only are not read — the client's `ForClientOnly` bit marks the sheet's copy of what a spell does through a sub-cast (Furor's "+20", Vitalidad's "+N%", Manticolmillo's "+15 huida", Virtud's shield); the real server sends none of them
- ✅ One draw per cast — the `random` shares of a spell level add up to 100 and one draw picks a row and everything in its `group`: Bumerán Pérfido steals in one element and boosts that element's characteristic
- ✅ Stack limits — a spell level's `maxStack` says how many equivalent rows live together: `-1` without limit (Fervor, Tumulto), `1` the new row replaces the old (Espada del Juicio, announced gone before the new one), `2` and up a cap (Presión, Espada Destructora)
- ✅ States need no code — effect 950 sets a number, 951 clears it, the masks do the rest; the 104 states the client flags — invulnerable, cannot be moved or pushed, incurable, kept out of the portals — are read from `datos/spell_states.json`: an invulnerable target takes no blow at all, a pinned one no push
- ✅ Area shapes from `zoneDescr`, every letter the way the client's own zone factory builds it: the crosses `P X Q + # *` (`Q` and `#` without their centre, `param2` counted in steps along each ray), the circles `C O I`, the lines `L /`, the bars `T -`, the line from the caster `l` (its first step and its length), `U` bent back towards the caster, the cone `V`, the fork `F`, the squares `G W`, the boomerang `B`, the checkerboard `D`, the rectangle `R`, the outside circle `Z`, the cells named outright `;` and the whole map — with each spell's own per-tile falloff
- ✅ Displacement — push, pull, step back, step forward, push without damage, and push or pull to the aimed cell (783/1043); direction taken from the centre of the area, stopping at walls, holes and fighters
- ✅ Teleports — to a cell, back to the previous position, symmetrical around the caster or the target — and position swaps
- ✅ Carry and throw (50/51) — the Pandawa's Karcham and Chamrak and the Tymobot's Pinzas share the two primitives
- ✅ Illusions (1097) — Tymadura: the caster jumps to the aimed cell and copies with his stats of the moment appear around the cell he left; they hold a cell, do not play, and go at the first damaging hit. His own side sees him translucent among opaque copies; the other side sees the copies dressed as him
- ✅ Criticals rolled against the spell's probability plus the character's, using the spell's critical effect list down the whole chain: a chained spell with a critical list of its own runs it and its rows carry the flag (Virtud's critical shield is 550% of the level, from 29723's own critical entry); one without runs its ordinary list unflagged
- ✅ Point steal, life steal, erosion of maximum HP and damage-taken multipliers
- ✅ Healing in all five elements, AP given back, best-element and worst-element damage (2822, 2832) and life steal
- ✅ Shields, by caster level or by HP: buff row 1040 and characteristic 96, as many rows as the level's `maxStack`; a replaced row takes its points with it
- ✅ Vitality percentages (1033/1078) — of the maximum life, base and gear included; announced as the flat rows the client draws, 153 down and 125 up, with the points
- ✅ Buff panel — icon, value, remaining rounds and dispellable flag; buffs start on their delay and expire on their round
- ✅ Cooldowns and cast limits — per turn, per target, minimum interval, initial cooldown; a spell that needs an empty cell, or a taken one, is refused before the AP go
- ✅ Nine sub-cast families, one table — 792 is cast by the target at its own cell, 1160 by the caster at the candidate's, 1017 back at the parent caster, 2160 at the nearest eligible target under a budget, 2794 at the parent cell
- ✅ Glyphs, traps and runes — 623 spells, one system: the four families share a shape and differ in when they fire, and a glyph that fires goes through the ordinary cast path
- ✅ Summons as real fighters — own sheet, behaviour spell, lifetime, and they all fall when their summoner dies. Whether one plays is bit 6 of its template's `m_flags`; those that do are driven by their owner from his own client. Capacity is the template's `summonCost` added up. Their characteristics come from their grade at the summoner's level, as the sheets of the captures read: the grade's own times one plus a hundredth of the level (300 is 900 at level 200), the bonus ones as they are, and three fifths of the bonus damage as power — an Osamodas' Tofu has 50 of agility and 30 of power. They had none, and hit with their spells' bare dice
- ✅ Bombs — a summon that costs nothing against the limit, stays out of the carousel, detonates through its own explosion (1009) once per chain, climbs a combo through spells 20497 and 20500, and lines up into walls of two or three with one to six cells between them, charged on entry and at turn start. The +1 AP per living bomb (Encendimiento, on the Tymador) goes with the bomb when it dies, as in the capture; it stayed, and by the third turn a second bomb could not be paid for. The chain reaction is not done
- ✅ Class passives — each class casts its own initial spell before the first turn (*La Astucia del Tymador*, *El Alcance de Ocra*, *La Sombra de Sram*, *El Escudo de Feca*…), kept in `content/fights/class_passives.json`. The initial spells of a character's own choices go with it
- ✅ Hooked spells fire on every trigger — turn start, turn end, when hit, on death and per step walked — from their original caster, chained spells included, inside one sequence of the bearer's; a hooked row with a delay waits that many rounds (Furor's decay fires at the end of the turn after the cast, and hooks the grade it falls to); every chained cast is announced once per grade before the first thing it does — on the cell it was aimed at and without the f8 of a cast somebody made, as 18,526 chained casts of the captures go —, and a 406 after the rows it takes, with an f5 when its row carries the bit 4 of its flags (17 of 17)
- ❌ The hooks themselves go out, in the real server, as hidden rows of their own — one per trigger, "jxm 1160 'D'" and "'XD'" on Resonancia's target — that come off with a jya; this server keeps its hooks off the panel. A jxm's state trigger goes out bare, `EON`/`EOFF`/`EK` as in all 581 of the captures
- ✅ Delayed effects — a `delay` in the catalogue is a hidden row with trigger `Y` that fires at the first turn of its round (the survival beacon's lifetime, Paso de Cacería's +1 MP)
- ✅ Points and rows across turns — a turn starts with the maximum plus the live point buffs; an expired row falls at the start of its caster's turn
- ✅ AP and MP removal against dodge — rolled point by point with the game's own odds, `(points left / maximum) × (retira + 2) / (esquiva + 2) × ½`, between 10% and 90%; *retira* and *esquiva* are a tenth of wisdom plus the gear (410–413, 160–163) and the live rows, monsters carry their grade's `paDodge`/`pmDodge`. What is dodged goes out as `jwe 308/309`, what lands as a `-N PA/PM` row (168/169)
- ✅ A summon with nothing to play hands its turn on; every summon that can act is its owner's to play by hand
- ✅ *-N de daños recibidos* (105, 265) and *daños sufridos x#1%* (1163) under a damage kind — rows read by the blow, whose letters say which blows: `DR` ranged, `DM`/`DCAC` melee, `D` any, `DTB`/`DTE` a turn's poison. The elemental and per-source letters are registered and not yet read
- ✅ Item attitudes — the six Dofus and the trophies grant their spell through effect 1175
- ✅ Appearance-changing spells — the transform replaces the root bones and keeps colours, skins, scale and pets, through combat action 149
- ✅ Script markers 3792 and 3793 do nothing: their value is a script id, not an effect
- ✅ The characteristic sheet in the shape the client expects: 53 entries in a fixed order, and a single-characteristic refresh replaces its entry
- ✅ AP given back (120) go out as the real server sends them: the AP sheet in its short sequence, then "jwe 120" with the points — 117 of them in 35 captures
- ✅ Spell modifiers, the catalogue's category 3 — AP cost, cast interval, critical, casts per turn and per target, ranges added, taken away or pinned, the cell needed, line of sight, base healing — held as rows and told as the client computes with them: hnd for the total, hnk to take it away, the numbering read off the captures
- ✅ Steals go out as two rows, the malus on the target and the bonus on the caster, as the 26 of the captures; and a double of the caster (180), invisibility revealed (202), a share of the final damage taken (1223), heals out of the blow (786, 2973, 2020), best-element healing (3002), rolls maximised and minimised (782, 781), damage by the MP left (1012–1016), one grade of a spell taken off (1406)
- ✅ Portals (1181, 1182, 1183), checked frame for frame against the six Selatrop captures — four a Selatrop, the fifth pushing the oldest out (jwe 310, then its jwe 401); a portal on when it can be used and another of its network can too, off when crossed this turn, switched off by a 1183 until its caster's next turn, or stood on; each change a "jwe 1181", and a turn start bringing back what it can in a sequence of its own. Walked or pushed onto one that is on, a fighter goes through the network — from each portal the nearest usable one not yet taken, the newest between two as near, the last one the way out —, and a walk ends there; a Teleportal (1182) takes whoever stands on one. A spell aimed at a portal comes out of the last one, landing where the caster's aim leads from there, the cast naming the portals it crossed; a spell about portals is not projected. The enemies' first-round state, "Teleportal Imposible", keeps them out
- ✅ Poisons hit when their trigger comes, class spells too: a damage row under TB or TE (37 of them — Arsénico, Toxinas, Epidemia, Inyección Tóxica...) is hooked at the cast and dealt at the start or end of the bearer's turn, in its row order; in sram-arsenico.pcapng the cast hooks "98 under TB" and the 27 air damage go out at each target's turn start
- ✅ A spell that runs at two grades in one cast is hooked grade by grade: Doble's "cast Intercambio de Doble at the end of the turn" (12966 grade 1) goes on the double and not on the Sram, who only carries grade 2's state — hooked on him too, he cast it at himself and died of it; in sram-doble.pcapng the double casts it in its own second turn, swaps with the Sram and dies
- ✅ A trap goes off at its centre, wherever in it it is stepped on — the 306 of the Sram captures names the cell it was aimed at, and its push takes its direction from there — and is spent before its effects, so its own push cannot set it off twice
- 🟡 What a spell through portals gains, "+#3% daños, +#1% por casilla que separe entre 2 portales" — the entry's value and 2% a cell between portals, on its damage and its healing — is the effect's own text: no capture holds a blow of known stats both ways
- 🟡 Interception (765) and damage sharing (1061): the rows go out as captured — hidden, under "D" or "DM", in the name of whoever cast them —, but what a blow does with them is the sheets' words, since no capture holds an intercepted blow or a linked fighter hit: an interceptor takes the blow in the place of the one hit, against his own resistances and shield; linked fighters take equal shares of it, what does not divide staying on the one hit
- ✅ Lazo Espiritual's bond (2184): the bound walk up to the Osamodas as far as its die, into contact, in walks of their own with his facing — its capture, frame for frame
- 🟡 2017, Recursividad's: 1017 with its value capping the candidates, read off its sheet — a turret in contact throws the enemy over it and pushes him on

> The **Ocra**, the **Tymador** and the **Yopuka** have been checked against the real client spell by spell, and three of the **Selatrop**'s against their own captures; the check-list below says which spells. Every class spell now resolves on paper; what the paper cannot say is in the notes after the dashes.

### 🔬 Spell check-list

Every class spell, one line each, in the order of the spell book. ✅ means it has been checked
against the real client, or against its own capture, and does what the game does. ❌ means it has
not — either the engine cannot resolve part of it on paper, and the reason follows the dash (an
effect with no implementation, a target mask or an area shape the engine does not read; sub-casts
are followed, so a gap in a chained spell shows on the spell that starts the chain), or it resolves
on paper and has not been checked yet. A ✅ with "unread on paper" after the dash works in the
game while a letter of its sheet is still not read.

The list is generated from `world.db` and the engine's own source by `tools/spell_checklist.py`;
the ✅ and the notes are set by hand in it. Rows for the client only are left out of the count.
**Every one of the 836 resolves on paper.** What a ❌ says after its dash is what the paper cannot
settle: a mechanic read off the spell's own sheet with no capture to hold it to — interception,
shared damage, the tarot's K and CS, Recursividad's turret, the triggers of the portals — or the one
difference left against its capture.

| Class | Seen working | Resolve on paper | Spells |
|---|:---:|:---:|:---:|
| Feca | 0 | 44 | 44 |
| Osamodas | 0 | 44 | 44 |
| Anutrof | 0 | 44 | 44 |
| Sram | 0 | 44 | 44 |
| Xelor | 0 | 44 | 44 |
| Zurcarák | 0 | 44 | 44 |
| Aniripsa | 0 | 44 | 44 |
| Yopuka | 22 | 44 | 44 |
| Ocra | 7 | 44 | 44 |
| Sadida | 0 | 44 | 44 |
| Sacrógrito | 0 | 44 | 44 |
| Pandawa | 0 | 44 | 44 |
| Tymador | 13 | 44 | 44 |
| Zobal | 0 | 44 | 44 |
| Steamer | 0 | 44 | 44 |
| Selatrop | 3 | 44 | 44 |
| Hipermago | 0 | 44 | 44 |
| Uginak | 0 | 44 | 44 |
| Forjalanza | 0 | 44 | 44 |
| **All** | **45** | **836** | **836** |

<details><summary><b>Feca</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Somnolencia
- ❌ Maniobra
- ❌ Languidez
- ❌ Atonía
- ❌ Murallón
- ❌ Fortificación
- ❌ Tifón
- ❌ Borrasca
- ❌ Escalofrío
- ❌ Chaparrón
- ❌ Barricada
- ❌ Pavés
- ❌ Recelo
- ❌ Parapeto
- ❌ Letargo
- ❌ Reagrupamiento
- ❌ Nimbo
- ❌ Estrato
- ❌ Bastión
- ❌ Tregua
- ❌ Puñalada
- ❌ Tetania
- ❌ Pompa
- ❌ Cencerro
- ❌ Silbo
- ❌ Cayado
- ❌ Sopor
- ❌ Escapadita
- ❌ Aprisco
- ❌ Excursión
- ❌ Escudo feca
- ❌ Posición Defensiva
- ❌ Refuerzo
- ❌ Ataraxia
- ❌ Prado
- ❌ Pasto
- ❌ Valle
- ❌ Escarcha
- ❌ Tierra Batida
- ❌ Refugio
- ❌ Trashumancia
- ❌ Égida — no capture holds an intercepted blow: who takes it is the sheet's word (765)
- ❌ Tierra Quemada
- ❌ Vigía

</details>

<details><summary><b>Osamodas</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Grito de Cuerbok
- ❌ Colmillos de Milubo
- ❌ Pinchos de Prespic
- ❌ Desplume
- ❌ Dientes de Piranya
- ❌ Corazón Salvaje
- ❌ Grito del Oso
- ❌ Baba de Sapo
- ❌ Látigo
- ❌ Fusta
- ❌ Tofu
- ❌ Garras de Chtigre
- ❌ Jalató
- ❌ Garras de Buitre
- ❌ Saponito
- ❌ Vellocino de Oro
- ❌ Dragún
- ❌ Mordedura de Serpiente
- ❌ Séquito Salvaje
- ❌ Pacto Bestial
- ❌ Chute Motivador
- ❌ Comunión Animal — no capture holds a linked fighter hit: the equal shares are the sheet's word (1061)
- ❌ Salta la Ranadina
- ❌ Tornado de Plumas
- ❌ Soplido Dracónico
- ❌ Golpe del Crujidor
- ❌ Carga Bestial
- ❌ Canto de Fénix
- ❌ Golpazo Aéreo
- ❌ Torbellino
- ❌ Disciplina
- ❌ Fuete
- ❌ Gorditofu
- ❌ Crujintesco
- ❌ Jalatorpe
- ❌ Cocolérico
- ❌ Saponcio
- ❌ Azufrénix
- ❌ Dragonito
- ❌ Escararrayo
- ❌ Lazo Espiritual — its follow and its hooks' state triggers are its capture's; the hooks go out there as hidden rows, not here
- ❌ Relevo Espiritual
- ❌ Espíritu Glotón
- ❌ Espíritu Burlón

</details>

<details><summary><b>Anutrof</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Lanzamiento de Monedas
- ❌ Moneda sonante
- ❌ Pala Fantomática
- ❌ Último Recurso
- ❌ Mochila Animada
- ❌ Morral Animado
- ❌ Jarabe de Pala
- ❌ Desprendimiento
- ❌ Bancarrota
- ❌ Lanzamiento de Pala
- ❌ Fiebre del Oro
- ❌ Andador
- ❌ Caja de Pandora
- ❌ Caja de Herramientas
- ❌ Fuerza de la Edad
- ❌ Búsqueda de Oro
- ❌ Llave del Tesoro
- ❌ Llave de Brazo
- ❌ Subterráneo
- ❌ Laya de los Ancianos
- ❌ Palas animadas
- ❌ Laya Animada
- ❌ Avaricia
- ❌ Decadencia
- ❌ Pala Aurífera
- ❌ Turbera
- ❌ Torpeza
- ❌ Edad de Oro
- ❌ Terraplenado
- ❌ Fuego de Mina
- ❌ Oportunidad
- ❌ Explosión de Grisú
- ❌ Debilitación
- ❌ Obsolescencia
- ❌ Jubilación Anticipada
- ❌ Pala de la Fortuna
- ❌ Corrupción
- ❌ Túnel de Fortuna
- ❌ Caducidad
- ❌ Tamizado
- ❌ Pala de los Ancianos
- ❌ Filón
- ❌ Cofre Animado
- ❌ Arcón Animado

</details>

<details><summary><b>Sram</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Invisibilidad
- ❌ Bruma
- ❌ Trampas solapadas
- ❌ Zalagarda
- ❌ Truhanería
- ❌ Abrojo
- ❌ Arsénico
- ❌ Toxinas
- ❌ Trampa Repulsiva
- ❌ Trampa Espeluznante
- ❌ Engaño
- ❌ Rebanacuellos
- ❌ Doble
- ❌ Conspirador
- ❌ Trampa Fangosa
- ❌ Epidemia
- ❌ Extorsión
- ❌ Registro
- ❌ Crueldad
- ❌ Mala Sombra
- ❌ Trampa Funesta
- ❌ Efracción
- ❌ Trampa de Inmovilización
- ❌ Fosa Común
- ❌ Trampa Miserable
- ❌ Trampa de Fragmentación
- ❌ Estafa
- ❌ Hurto
- ❌ Pillaje
- ❌ Ataque Mortal
- ❌ Miedo
- ❌ Equivocación
- ❌ Karadura
- ❌ Perfidia
- ❌ Concentración de Chakra
- ❌ Artimaña
- ❌ Trampas mortales
- ❌ Calamidad
- ❌ Escapatoria
- ❌ Marca Mortuoria
- ❌ Trampa de Deriva
- ❌ Trampa Insidiosa
- ❌ Estratagema
- ❌ Inyección Tóxica

</details>

<details><summary><b>Xelor</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Teletransportación
- ❌ Astrolabio
- ❌ Perturbación
- ❌ Rueda Dentada
- ❌ Recuerdo
- ❌ Permutación
- ❌ Marchitación
- ❌ Aguja
- ❌ Rebobinamiento
- ❌ Remanencia
- ❌ Refracción
- ❌ Regulador
- ❌ Cómplice
- ❌ Esfera de Xelor
- ❌ Congelación
- ❌ Polvo
- ❌ Ralentización
- ❌ Reloj de Arena de Xelor
- ❌ Engranaje
- ❌ Cuentagotas
- ❌ Borroso Temporal
- ❌ Conservación
- ❌ Distorsión
- ❌ Arenas del Tiempo
- ❌ El Tiempo Vuela
- ❌ Premonición
- ❌ Rayo Oscuro
- ❌ Desecamiento
- ❌ Paradoja
- ❌ Falla
- ❌ Syncro
- ❌ Tañido
- ❌ Petrificación
- ❌ Reloj de Bolsillo
- ❌ Reloj
- ❌ Reloj de Agua
- ❌ Golpe de Xelor
- ❌ Péndulo
- ❌ Momificación
- ❌ 25ª Hora
- ❌ Rolbac
- ❌ Inestabilidad
- ❌ Desincronización
- ❌ Espaciotiempo

</details>

<details><summary><b>Zurcarák</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Espíritu Felino
- ❌ Kraps
- ❌ Garra Invocadora
- ❌ Caricia Invocadora
- ❌ Golpe de Fortuna
- ❌ Redistribución
- ❌ Olfato
- ❌ Rueda de la Fortuna
- ❌ Reflejos
- ❌ Lametazo
- ❌ Truco
- ❌ Todo o Nada
- ❌ Salto del Felino
- ❌ Trenzado
- ❌ Topkaj
- ❌ Garra Juguetona
- ❌ Jass
- ❌ Desdicha
- ❌ Cara o Cruz
- ❌ Fantasmada
- ❌ Segunda Oportunidad
- ❌ Nueve Vidas
- ❌ Farol
- ❌ Rekop
- ❌ Almohadillas
- ❌ Bufido
- ❌ Yams
- ❌ Lengua Raspadora
- ❌ Belote
- ❌ Peligro
- ❌ Baraka
- ❌ Osadía
- ❌ Ruleta
- ❌ Tarot de Zurcarák — the cards' K and CS are their own words; no capture fires either
- ❌ Castillo de Naipes
- ❌ Buena Estrella
- ❌ Blakjak
- ❌ Destino de Zurcarák
- ❌ Percepción
- ❌ Predación
- ❌ Ovillo
- ❌ Garra de Ceangal
- ❌ Feliación
- ❌ Desventura

</details>

<details><summary><b>Aniripsa</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Palabra de Amistad
- ❌ Palabra Alquímica
- ❌ Palabra Escandalosa
- ❌ Grito Ensordecedor
- ❌ Palabra Juguetona
- ❌ Palabra Maliciosa
- ❌ Palabra Vampírica
- ❌ Sollozos
- ❌ Palabra Estimulante
- ❌ Palabra de Declive
- ❌ Blasfemia
- ❌ Ungüento Ancestral
- ❌ Pintura de Guerra
- ❌ Palabra Secreta
- ❌ Lamentos
- ❌ Demencia
- ❌ Palabra Turbulenta
- ❌ Palabra Furiosa
- ❌ Palabra Revitalizante
- ❌ Palabra Galvanizadora
- ❌ Palabra Bromista
- ❌ Palabra Censurada
- ❌ Palabra Florida
- ❌ Bosquecillo Encantado
- ❌ Palabra de Juventud
- ❌ Palabra Deprimente
- ❌ Grito de Guerra
- ❌ Palabra Ritual
- ❌ Palabra Prohibida
- ❌ Palabra Exangüe
- ❌ Palabra Abrumadora
- ❌ Palabra Desanimadora
- ❌ Ladronceo
- ❌ Palabra Entretenida
- ❌ Palabra de Vuelo
- ❌ Fuente de Juventud
- ❌ Pincel Tribal
- ❌ Coro Estridente
- ❌ Crioterapia
- ❌ Murmullo
- ❌ Palabra de Pavor
- ❌ Escalpelo
- ❌ Palabra de Reconstitución
- ❌ Palabra de Solidaridad

</details>

<details><summary><b>Yopuka</b> — 22 of 44 seen working, 44 resolve on paper</summary>

- ✅ Machete
- ❌ Acumulación
- ✅ Intimidación
- ❌ Conquista
- ✅ Salto — the x115% row on the enemies around the arrival, under D
- ❌ Agitación
- ✅ Fervor
- ❌ Amenaza
- ✅ Espada Divina
- ❌ Espada del Juicio
- ✅ Espada Destructora — the T is the bar across the cast, and two casts erode 26%
- ❌ Fustigación
- ✅ Aguante
- ❌ Pugilato
- ✅ Soplido
- ❌ Congregación
- ✅ Concentración — the L,M,l,m,c and J,j rows: monsters and players, and the summons
- ❌ Sentencia
- ✅ Furor — 28604's rows alone: Furor I and II, and the decay at the end of the turn after
- ❌ Ira de Yopuka
- ✅ Fricción — the state lands on the enemy it just pulled, and the DBE hook pulls him again
- ❌ Golpe por Golpe
- ✅ Influencia — the Invulnerable state takes the whole blow, and the -100 PM row
- ❌ Duelo Yopukil
- ✅ Potencia
- ❌ Vindicta
- ✅ Virtud — 29723's rows alone: the shield around, 550% on a critical, and one -50 per ally in contact
- ❌ Masacre
- ✅ Tempestad de Potencia
- ❌ Casca
- ✅ Espada Celeste
- ❌ Cénit
- ✅ Vitalidad — 25215's row alone: +20% of the maximum life on oneself, +10% on the others
- ❌ Violencia
- ✅ Espada de Yopuka
- ❌ Cuchillo de Carnicero
- ✅ Espada del Destino
- ❌ Tumulto
- ✅ Presión — the two casts add up to 20% (its maxStack is 2)
- ❌ Fractura
- ✅ Oleada
- ❌ Anillo Destructor
- ✅ Precipitación
- ❌ Determinación

</details>

<details><summary><b>Ocra</b> — 7 of 44 seen working, 44 resolve on paper</summary>

- ✅ Flecha Helada — the critical roll, measured
- ❌ Flecha Acosante
- ❌ Flecha de Pelea
- ❌ Diamantes Destructores
- ❌ Flecha Azotadora
- ❌ Flecha Asaltante
- ❌ Flecha Vagabunda
- ❌ Flecha Evasiva
- ✅ Paso de Cacería — the jump, and the +1 MP the turn after
- ❌ Baliza Táctica
- ❌ Disparos Lejanos
- ❌ Tiro Penetrante
- ❌ Flecha Detonadora
- ❌ Flecha Ralentizante
- ❌ Flecha de Abolición
- ❌ Flecha Perseguidora
- ❌ Flecha de Retroceso
- ❌ Flecha Impactante
- ❌ Flecha Inmovilizadora
- ❌ Flecha Tiránica
- ❌ Tiros Potentes
- ❌ Flechas Amorosas — no capture holds a linked fighter hit: the equal shares are the sheet's word (1061)
- ❌ Flecha de Dispersión
- ❌ Flechas Flamígeras
- ❌ Flecha Explosiva
- ❌ Flecha Masacrante
- ❌ Ojo de Topo
- ❌ Lluvia de Flechas
- ❌ Ojo por Ojo
- ❌ Flecha Paralizadora
- ✅ Baliza de Supervivencia — she plays her turn on her own and dies two rounds later through her own 141
- ✅ Represalias
- ✅ Tiro de Repliegue
- ❌ Vendetta
- ✅ Flecha Castigadora — its start, measured
- ❌ Flecha del Juicio
- ❌ Flecha de Expiación
- ❌ Flecha de Redención
- ❌ Flecha Percutiente
- ❌ Flecha Búmeran
- ❌ Flecha Voraz
- ✅ Flecha Fulminante — the rebound, measured from both sides
- ❌ Agudeza Absoluta
- ❌ Centinela

</details>

<details><summary><b>Sadida</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ La Loca
- ❌ La Loca Transmutada
- ❌ Árbol
- ❌ Árbol Frondoso
- ❌ Zarza
- ❌ Zarza Insolente
- ❌ Plaga
- ❌ Bosque Encantado
- ❌ La Bloqueadora
- ❌ La Bloqueadora Transmutada
- ❌ Lágrima de Sadida
- ❌ Subida de Savia
- ❌ Savia Paralizante
- ❌ Miasmas
- ❌ Zarza Tranquilizadora
- ❌ Trasplante
- ❌ Potencia Silvestre
- ❌ Influencia Vegetal
- ❌ La Sacrificada
- ❌ La Sacrificada Transmutada
- ❌ Temblor
- ❌ Mandrágora
- ❌ Don Natural — no capture holds a linked fighter hit: the equal shares are the sheet's word (1061)
- ❌ Armonía — no capture holds a linked fighter hit: the equal shares are the sheet's word (1061)
- ❌ Sacrificio Vudú
- ❌ Cardos Ardientes
- ❌ Contagio
- ❌ Manglar
- ❌ Inoculación
- ❌ Fuerza de la Naturaleza
- ❌ La Hinchable
- ❌ La Hinchable Transmutada
- ❌ Zarzas Agresivas
- ❌ Fetiches Calcinados
- ❌ Árbol de Vida
- ❌ Altruismo Vegetal
- ❌ Matorral Ardiente
- ❌ Fuego Montés
- ❌ Cicuta
- ❌ Viento Envenenado
- ❌ Hierbas Locas
- ❌ Maldición Vudú
- ❌ La Superpoderosa
- ❌ La Superpoderosa Transmutada

</details>

<details><summary><b>Sacrógrito</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Mutilación
- ❌ Pacto de Sangre
- ❌ Espada Voraz
- ❌ Espada Bailarina
- ❌ Rapapolvo
- ❌ Fulgor
- ❌ Asalto
- ❌ Aversión
- ❌ Transposición
- ❌ Fluctuación
- ❌ Condensación
- ❌ Aflujo
- ❌ Hostilidad
- ❌ Proyección
- ❌ Corona de Espinas
- ❌ Picota
- ❌ Transfusión
- ❌ Lazos de Sangre
- ❌ Hecatombe
- ❌ Corte
- ❌ Baño de Sangre
- ❌ Inmolación
- ❌ Sacrificio — no capture holds an intercepted blow: who takes it is the sheet's word (765)
- ❌ Penitencia
- ❌ Desolación
- ❌ Desencadenamiento
- ❌ Disolución
- ❌ Carnicería
- ❌ Libación
- ❌ Castigo
- ❌ Berserker
- ❌ Ritual de Jashin
- ❌ Absorción
- ❌ Furia
- ❌ Suplicio
- ❌ Nerviosismo
- ❌ Estasis
- ❌ Escozor
- ❌ Atracción
- ❌ Perfusión
- ❌ Punición
- ❌ Locura Sanguinaria
- ❌ Hemorragia
- ❌ Aniquilamiento

</details>

<details><summary><b>Pandawa</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Palma Explosiva
- ❌ Destilación
- ❌ Resaca
- ❌ Soplido Flamígero
- ❌ Comilona
- ❌ Tranka
- ❌ Terror
- ❌ Consuelo
- ❌ Ventolera
- ❌ Jarana
- ❌ Karcham
- ❌ Chamrak
- ❌ Ola Marejadora
- ❌ Pandjiu
- ❌ Desalojo
- ❌ Soplido Alcoholizado
- ❌ Ebriedad
- ❌ Embriaguez
- ❌ Estabilización
- ❌ Escalada
- ❌ Enlace Espirituoso
- ❌ Bambú
- ❌ Etilo
- ❌ Entumecimiento
- ❌ Aguardiente
- ❌ Aguachirle
- ❌ Deshonra
- ❌ Maceración
- ❌ Fermentación
- ❌ Bambusería
- ❌ Propulsión
- ❌ Absenta
- ❌ Camilla
- ❌ Alcoshu
- ❌ Pandikulación
- ❌ Licor
- ❌ Náuseas
- ❌ Cascada
- ❌ Leche de Bambú
- ❌ Interdicción
- ❌ Frasco Explosivo
- ❌ Pandatak
- ❌ Pandenkulo
- ❌ Mano de Pandawa

</details>

<details><summary><b>Tymador</b> — 13 of 44 seen working, 44 resolve on paper</summary>

- ✅ Detonador
- ❌ Estopín
- ✅ Explobomba
- ❌ Explobomba Resiliente
- ✅ Tornabombas
- ❌ Tornabomba Resiliente
- ❌ Patada
- ❌ Ardid
- ❌ Extracción
- ❌ Cadencia
- ❌ Imantación — does nothing on an empty cell, as it should
- ❌ Cruce
- ✅ Fusil
- ❌ Obliteración
- ❌ Jugarreta
- ❌ Bomba Ambulante
- ✅ Bombas de agua
- ❌ Bomba de Agua Resiliente
- ✅ Tymobot — and its own Empujoncito, Aspirador and Pinzas, driven from the owner's client; it dies at the end of its turn
- ❌ Megabomba
- ❌ Bombardeo
- ❌ Metralla
- ❌ Receptación
- ❌ Emplomado
- ✅ Tymadura — byte for byte against its capture
- ❌ Argucia
- ❌ Púlsar
- ❌ Perdigonazo
- ✅ Remisión
- ❌ Búnker
- ❌ Dagas Bumerán
- ❌ Tromba
- ❌ Polvo
- ❌ Bomba Pegajosa
- ✅ Kabúm
- ❌ Impostura
- ✅ Último Aliento
- ❌ Trampa Magnética
- ✅ Mosquete
- ❌ Granalla
- ✅ Colado
- ❌ Arcabuz
- ✅ Sismobomba
- ❌ Sismobomba Resiliente

</details>

<details><summary><b>Zobal</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Boliche
- ❌ Ronda
- ❌ Catalepsia
- ❌ Apostasía
- ❌ Máscara Eskérdikat
- ❌ Máscara de Cobarde
- ❌ Brincadeira
- ❌ Picado
- ❌ Apoyo
- ❌ Pivote
- ❌ Máscara Sáikopat
- ❌ Máscara de Histérico
- ❌ Furial
- ❌ Bocciara
- ❌ Cabriola
- ❌ Purgatorio
- ❌ Tortoruga
- ❌ Armaduro
- ❌ Esprín
- ❌ Scudo
- ❌ Apatía
- ❌ Retención
- ❌ Coraza
- ❌ Ginga
- ❌ Fogosidad
- ❌ Mascarada
- ❌ Desbandada
- ❌ Comedia
- ❌ Parafuso
- ❌ Martelo
- ❌ Ponteira
- ❌ Agular
- ❌ Trance
- ❌ Neurosis
- ❌ Cabalgata
- ❌ Reclamo
- ❌ Infernus
- ❌ Distancia
- ❌ Carnavalo
- ❌ Transfiguración
- ❌ Máscara de Intrépido
- ❌ Máscara de Incansable
- ❌ Mueca
- ❌ Difracción

</details>

<details><summary><b>Steamer</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Torpedo
- ❌ Timón
- ❌ Catalejo
- ❌ Corrosión
- ❌ Amarre
- ❌ Ventalla
- ❌ Evolución
- ❌ Sobretensión
- ❌ Albarrama — no capture holds an intercepted blow: who takes it is the sheet's word (765)
- ❌ Recursividad — 2017 is read off its sheet: its capture only casts it with no turret in contact
- ❌ Escafandra
- ❌ Blindaje
- ❌ Anclaje
- ❌ Cortocircuito
- ❌ Guardianas
- ❌ Perforadora
- ❌ Corriente
- ❌ Harmatán
- ❌ Sabotaje
- ❌ Periscopio
- ❌ Asistencia
- ❌ Derivación
- ❌ Aspiración
- ❌ Pistón
- ❌ Tactiquillas
- ❌ Batiscafo
- ❌ Arponeras
- ❌ Arrastrero
- ❌ Socorrismo
- ❌ Salvamento
- ❌ Tridente
- ❌ Cabestrante
- ❌ Sónar
- ❌ Emboscada
- ❌ Compás
- ❌ Brújula
- ❌ Turbina
- ❌ Piratería
- ❌ Buceo
- ❌ Zambullida
- ❌ Resacón
- ❌ Espuma de Mar
- ❌ Marea
- ❌ Vapor

</details>

<details><summary><b>Selatrop</b> — 3 of 44 seen working, 44 resolve on paper</summary>

- ✅ Portal — laid, turned on, the fifth pushing the first out, walked through and back at the next turn
- ❌ Errancia — Portal's rows at another grade, with no capture of its own
- ❌ Insulto
- ❌ Desprecio
- ❌ Audacia
- ❌ Tribulación
- ❌ Shock
- ❌ Convulsión
- ❌ Rayo de Wakfu
- ❌ Resplandor
- ✅ Neutral — the portal off, its AP back, and the portal on again at the Selatrop's next turn
- ❌ Interrupción — Neutral's 1183 on the whole map, with no capture of its own
- ❌ Afrenta
- ❌ Aplomo
- ❌ Trascendencia
- ❌ Exilio — a portal under the target and a Teleportal, as Resonancia's; no capture of its own
- ❌ Terapia
- ❌ Puño Relámpago
- ❌ Distribución
- ❌ Soberbia
- ✅ Estela — the portal under him, off while he stands on it, his jump, and the portal on again
- ❌ Estupor — two portals and a swap; no capture of its own
- ❌ Acoso
- ❌ Cataclismo
- ❌ Sanación
- ❌ Conjuro
- ❌ Insolencia
- ❌ Desdén
- ❌ Odisea
- ❌ Éxodo
- ❌ Cábala
- ❌ Resiliencia
- ❌ Aflicción
- ❌ Ofensiva
- ❌ Resonancia — its 406, portal and Teleportal are its capture's, frame for frame; its hook goes out there as hidden rows, not here
- ❌ Vestigio
- ❌ Mofa
- ❌ Sinecura
- ❌ Extinción
- ❌ Sermón
- ❌ Ridículo
- ❌ Sarcasmo
- ❌ Ayuda Mutua — CPT fires on the owner of the portal crossed, read off its sheet; no capture of it
- ❌ Coalición — PT fires on whoever crosses, read off its sheet; no capture of it

</details>

<details><summary><b>Hipermago</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Onda Sísmica
- ❌ Tizón
- ❌ Éter
- ❌ Catarata
- ❌ Runificación
- ❌ Manifestación
- ❌ Lanzallamas
- ❌ Lanzas Telúricas
- ❌ Estalagmita
- ❌ Onda Celeste
- ❌ Tormenta
- ❌ Huracán
- ❌ Lanza Solar
- ❌ Cometa
- ❌ Polaridad
- ❌ Convección
- ❌ Trazo Flamígero
- ❌ Estalactita
- ❌ Glaciar
- ❌ Volcán
- ❌ Propagación
- ❌ Prisma Rúnico
- ❌ Escudo Elemental
- ❌ Guardián Elemental
- ❌ Hoja Astral
- ❌ Deflagración
- ❌ Contribución
- ❌ Impronta
- ❌ Diluvio
- ❌ Asteroide
- ❌ Sobrecarga Rúnica
- ❌ Sublimación
- ❌ Ráfaga
- ❌ Brecha
- ❌ Meteoro
- ❌ Avalancha
- ❌ Ciclo Elemental
- ❌ Corriente Cuadramental
- ❌ Travesía
- ❌ Repulsión Rúnica
- ❌ Drenaje Elemental
- ❌ Tributo
- ❌ Supernova
- ❌ Torrente Arcano

</details>

<details><summary><b>Uginak</b> — 0 of 44 seen working, 44 resolve on paper</summary>

> Rage is handled: the spells that give Rage (Moloso and Carroña among them) give it
> even when their damage kills the target. On the third step the Ouginak takes its bestial
> form until the end of its following turn.

- ❌ Convergencia
- ❌ Busca
- ❌ Presa
- ❌ Animal de Caza
- ❌ Moloso
- ❌ Mandíbula
- ❌ Cúbito
- ❌ Calcáneo
- ❌ Carcasa
- ❌ Batida
- ❌ Ojeo
- ❌ Ladrar
- ❌ Amaine
- ❌ Afección
- ❌ Lanzagozquetes
- ❌ Gangrena
- ❌ Dogo
- ❌ Restos
- ❌ Tibia
- ❌ Húmero
- ❌ Rastreo
- ❌ Despiece
- ❌ Sabueso
- ❌ Tetanización
- ❌ Arcanino
- ❌ Caninos
- ❌ Pelaje Protector
- ❌ Ferocidad
- ❌ Carroña
- ❌ Radio
- ❌ Hueso con Tuétano
- ❌ Bozal
- ❌ Pánico
- ❌ Caza
- ❌ Amarok
- ❌ Cerbero
- ❌ Ladrido
- ❌ Enojo
- ❌ Cacería
- ❌ Vértebra
- ❌ Olfacción
- ❌ Ensañamiento
- ❌ Clamor de la Manada
- ❌ Luna Nueva

</details>

<details><summary><b>Forjalanza</b> — 0 of 44 seen working, 44 resolve on paper</summary>

- ❌ Lanza del Lago
- ❌ Chuzo Sísmico
- ❌ Lanzapiedras
- ❌ Jabalina Rayo
- ❌ Epílogo
- ❌ Anticipación
- ❌ Lanza de Incendios
- ❌ Lluvia Dorena
- ❌ Carga Heroica
- ❌ Galantería
- ❌ Colapso
- ❌ Lanza Ciclón
- ❌ Al Tridente
- ❌ Maelstrom
- ❌ Falange
- ❌ Oriflama
- ❌ Estocada Ardiente
- ❌ Octava
- ❌ Golpiza de Bronce
- ❌ Sublevación
- ❌ Balestra
- ❌ Molino de Viento
- ❌ Talón de Barro
- ❌ Posición de Fondo
- ❌ Kyrja
- ❌ Vajra
- ❌ Muspel
- ❌ Ydra
- ❌ Punzón
- ❌ Abrazo de Valquíride
- ❌ Tierra Media
- ❌ Despeje
- ❌ Caballería
- ❌ Renombre
- ❌ Jormun
- ❌ Cadena Candente
- ❌ Preludio al Hierro
- ❌ Crepúsculo
- ❌ Noa
- ❌ Elding
- ❌ Eclipse
- ❌ Holmgang
- ❌ Jabalina Keatina
- ❌ Molino Rojo

</details>

### 🎯 Combat challenges

- ✅ The preparation phase: two candidates with a 15-second timer, the player marks and validates, and the server fixes whatever is left when you declare ready
- ✅ **15 of the 16** watched live, with every rule taken from the challenge's own translated description
- ✅ Results travel the moment they happen — a failure the instant the challenge breaks, a success at the end, a defeat failing them all at once
- ✅ The bonus is folded into experience, kamas and drop rates on a win — the same verdicts and the same bonus for every player of the fight
- ✅ Dungeon and anomaly challenges are imposed at 0% and carry achievements, written once and never offered again
- ❌ *Hired Killer* (35), which needs the server to designate and re-designate the target
- ❌ Challenges without a known percentage: the client ships no bonus field for them

---

## 🔑 Admin window (F10)

An administration panel inside the game. Press **F10** with an administrator's character in the
world and a window opens over the map, drawn with the client's own pieces — the same window frame,
tabs, buttons and text fields as Dofus's menus — so it looks like one of them. Press F10 again, or
its cross, to close it.

It started as [JimmyMtl's item window](https://github.com/Keka-Bron/JondoEmu/pull/49) and grew
into five tabs.

<img width="2050" height="1426" alt="image" src="https://github.com/user-attachments/assets/e126bd4c-00ef-4266-ab2a-10c57cd872fa" />

### What it needs

- **An administrator account** (role 5, see `docs/role.md`). The test account `keka` / `test` is one;
  another account is promoted with `POST /api/rol`.
- **The game started from the Jondo launcher.** The launcher installs the JondoFix mod into the
  client's `Mods` folder and hands the client the account's token, to administrators only.
- The server on the same machine, or on another one opened with `JONDO_PUBLIC_BIND=1`: the launcher
  relays the window's port (8888) along with the game's.

### The window

- Resized from its bottom right corner, down to 960 × 620; it keeps its size between openings.
- See-through while the pointer is elsewhere, so what happens on the map can be watched; nearly
  solid while the pointer is on it.
- Clicks on it never reach the map behind.
- Wherever a character is chosen, the connected characters are in a drop-down, with their level and
  whether they are in jail; **Refresh** reads them again.
- The line at the bottom of each tab says what the server answered: done, or why not — not
  connected, in a fight, already in jail…

### Items

The client's own item catalogue, with its icons.

- **Search** by name, type, id, or a level range such as `190-200`; filter by category and then by
  type; pages of 40.
- Pick an item to see its lines, choose **to whom** (yourself or any connected character) and the
  **quantity**.
- **Give · MAX stats** gives it with every characteristic at the top of its range; **Give · RANDOM
  stats** rolls each one, as a drop would (up to 100 items at a time). `.item <id> [quantity]
  random` does the same from the chat.

### Character

For yourself or any connected character.

- **Level**, the six base characteristics (vitality, wisdom, strength, intelligence, chance,
  agility) and **kamas**. When a character is picked, the fields fill in with what they have now;
  change what you want and press **Apply** — only what changed is sent, and the fields read the
  result back.
- **Give mount**: a mount by its item id, put on at once.

Everything changes live, without reconnecting; a fight in progress blocks it.

### Teleport

Moves yourself or the character picked in the drop-down.

- **Find a place**: type part of a name — `bonta`, `astrub`, `barrio de los herreros` — and the
  maps of that area or subarea are listed with their coordinates, outdoor ones first, as Jondo
  Studio's map field does. Coordinates (`4,-18`) and map ids work too. Clicking one puts it in the
  map field.
- Or type a **map id** or **coordinates `x,y`**, and optionally a **cell**, and press **Teleport**.
- **Go to them** takes you to the picked player's map and cell; **Bring here** brings them to yours.

### Spawn

NPCs and monsters on the map you stand on, on your own cell, seen at once by everyone on the map.

- Switch between **Monsters** and **NPCs**; search by name, id, or (monsters) a level range such as
  `1-50`; pages of 30, with each monster's levels.
- **An NPC**: pick it and press **Spawn NPC here**.
- **A group of monsters**: pick a monster and click one of its grades (each with its level) to add
  it; up to eight, mixed as you like. Click a member to take it out, then **Spawn group here**.
- **On this map** lists every NPC and monster group standing on your map, each with **Remove**.

What you spawn or remove lasts until the server restarts, like everything else that happens on a
map; a map emptied by hand is not refilled with fresh groups meanwhile.

### Jail

Ten minutes in the game's own GM prison, for a player who needs a break.

- Pick the player and press **Jail**. They go into one of the prison's four cells and you to the
  corridor beside its bars; a second prisoner gets a cell of their own.
- While inside, a prisoner cannot leave by any road — zaap, zaapi, haven bag, house, dungeon,
  Koliseo, teleports, passages — cannot use commands, and cannot speak but on the general channel
  and in private messages. A lost fight sends them back to their cell, not to their save point.
- **In jail** lists the prisoners with their time left, counting down, and **Release** lets one out
  early. The time keeps running while they are offline and survives a server restart. When it is
  up, or when they are released, they go back exactly where they were taken from — at their next
  login if they are offline.
- **Go to the jail** takes you to the corridor alone, to talk to a prisoner.

<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/1b35bc2b-56b6-41ce-af52-6f43f79cc6d5" />

**The GM prison has three maps**, all at [66,6] and joined by nothing on the world map: the jail in
the clouds, a dungeon underneath and a desert island. They are linked in a round. The **trapdoor**
in the middle of the jail's corridor goes down to the dungeon; stepping into the dungeon's
**hanging cage** lifts you to the island; opening the island's **treasure chest** brings you back
to the jail, at the foot of the trapdoor. A prisoner cannot use any of them.

### How it works

The window is not a reworked client screen: JondoFix (a MelonLoader mod) builds it at runtime out
of the client's own interface components — `WindowFigma`, `DofusTabGroup`, `DofusButtonCustom`,
`TextInput` — and adds it to the client's interface layer, so it takes the game's look by itself.

The window decides nothing. Every button is a request to the server's control API, signed with the
account's launcher token, and the server checks the token and the administrator's role on every
one of them. The routes are documented in **[`docs/live-character-admin.md`](docs/live-character-admin.md)**,
and can be called by any other tool the same way.

---

## 🛠️ Jondo Studio
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/14ee4541-d473-4bd1-81dd-617d03c8ba82" />
<img width="2558" height="1502" alt="image" src="https://github.com/user-attachments/assets/21917c8d-4e7a-43a1-a7bc-73dddee31137" />
<img width="2558" height="1496" alt="image" src="https://github.com/user-attachments/assets/39afe77a-c451-43a2-a67b-8ab5e6abe4c4" />
<img width="2558" height="1508" alt="image" src="https://github.com/user-attachments/assets/c139b38c-232d-4a58-9f45-572e643ccd93" />

> ⚠️ **Very early.** The Studio changes often. Keep a copy of `content/` before a long session. Nothing
> in it can damage `world.db` or a running server.

The world editor. A third executable next to the launcher and the server, and it needs neither of
them running: it opens `content/` and the data files through the same paths the server uses. Built
with **Avalonia**, so it runs on Windows, macOS and Linux.

It unpacks `world.db` from `datos/world.zip` the first time it runs, the way the server does.

The client holds every item, spell and monster, but not what the real server decided: which reply in
a dialogue leads to which line, where an NPC stands and what it does there, which interactive
teleport comes back to which map. Those have to be authored, and the Studio is where.

### Three layers, and every row says where it came from

The data lives in three places: `dofus3_data/` is a raw dump of the client, `datos/*.json` is
regenerated by the tools in `tools/`, and `world.db` is a 240 MB binary. Only the last layer is ever
edited:

| layer | where from | who edits it |
|---|---|---|
| **base** | generated from the client dump | nobody |
| **measured** | learned from packet captures | nobody |
| **authored** | decided by a person | this is the one, and it always wins |

The authored layer is `content/`, in versioned JSON, so a change is a reviewable diff. It stores
deltas, not copies, and it can erase a row it did not write. Every row carries its provenance.

### What it does today

Nine sections, **in Spanish, English or French** — the language switch changes both the editor's own
words and the game's, which are read straight out of the client's `Content/I18n/{lang}.bin`, 339,342
texts per language.

The creatures are drawn out of the client's own bundles. Monsters come from a picto atlas, 5,130 of
the 5,134 covered. NPCs are assembled the way the client assembles them: bones, a still frame, and
the skins the look names. That renderer lives in `Jondo.Unity.Sprites`, and the launcher draws its
account portraits with it.

- ✅ **Overview** — which files it read and what came out of each
- ✅ **Traffic** — the client-server conversation, live and back through the log, every frame read against the protocol the client itself declares. A packet can be named on the spot, from the **513 real message names** the client ships in its metadata
- ✅ **Packets** — every kind of packet seen, with a status ladder: unknown, named, documented, handled, ignored
- ✅ **NPCs** — all 422 placements, with the provenance column and the NPC drawn on the map
- ✅ **Dialogues** — which reply leads to which line, with the text on screen
- ✅ **Monsters** — open a group, take a monster out, put another in, move it
- ✅ **Spells** — every spell with its effects, and the map showing how far it reaches and what it would hit, computed by the fight engine's own `Zone.Casillas`
- ✅ **Passages** — two maps side by side, a door picked on each, and one button that joins them both ways
- ✅ **Map cells** — the three layers painted one at a time, click to toggle and drag to paint a run
- ✅ A section that fails shows its error inside the editor, and `Jondo Studio.exe --selftest` builds all nine in all three languages against the real data

Everything it writes goes to `content/`. Nothing opens `world.db` for writing and nothing talks to a running server.

### What is being worked on

- 🚧 NPC actions per placement
- 🚧 Editing spells: the simulator is there; changing a spell's numbers is not
- 🚧 Shops, loot tables and dungeons
- 🚧 Editing quests: the engine plays them and the Studio shows them, but nothing writes one yet
- 🚧 A thin admin channel so a running server can be told to reload one domain, without a restart

The full plan is in **`docs/world-editor.md`**.

---

## 🧪 Tests

`Jondo.Unity.Tests` — **1,903 xUnit tests**, grouped by domain: `Auth`, `Combat`, `Commands`,
`Content`, `Diagnostics`, `Economy`, `Launcher`, `Movement`, `Network`, `Protocol`, `Quests`,
`Security`, `Sessions`, `Sprites`, `Studio`, `World`. They run in about half a minute.

```bash
dotnet test Jondo.Unity.Tests
```

A few of them run against `logs/gameserver_traffic.log` and `bases/world.db` when they are on the
machine, and skip when they are not.

**Publishing the server runs them first and fails if any is red.** The escape hatch is
`-p:SkipTests=true`.

### Three kinds of check, three homes

* **At startup** run the questions of the form *"is the data I was shipped sane?"* — the fight
  sheet's 53 characteristics in their order, the interactive registry, the monster spellbooks, the
  vendor placements, the profession catalogue. The server refuses to boot when one fails.
* **In the test project** live the questions of the form *"is this code correct?"* — the content
  layers, the collision damage formula, the Jondo Coin bands, frame limits, protobuf parsing,
  password hashing, log censorship, session isolation, and frames compared byte for byte against
  captures.
* **Architecture tests** ask *"is this code shaped right?"* — they read the fight engine's own
  source and fail on the shapes a multi-client engine cannot afford, with an exception list where
  every entry carries a written reason.

Portraits are checked by counting: the animation name has to end in the direction that faces the
camera, and the head slot has to contribute more than zero triangles.

---

## 🔎 Surviving the next patch

Every protobuf message in Dofus 3 is named with three random letters — `kub`, `jru`, `lqu` — and on some patches Ankama reshuffles the lot. Nothing else about the protocol changes shape. **`protocolbuilder`** is the command line for that; **`Jondo Desofuscador.exe`** is the same engine behind one window and one button.

Eight consecutive real clients (3.6.4.3 → 3.6.10.10) were compared patch by patch:

- Ankama does not reshuffle on every patch: three of the seven jumps keep all 2,169 names, one for one. The tool checks for the identity mapping first.
- The matcher never looks at names, only at field numbers, kinds and neighbourhood. It resolves 71.1% of pairs with zero wrong pairings over 6,505; what it cannot decide, it leaves alone.
- On a patch that does reshuffle, structure alone resolves about 11%.
- Chaining through intermediate versions is worse than the direct jump.
- 49 opcodes only exist in 3.6.4.3.

The **`Op` layer** is one generated file, `Jondo.Unity.Protocol/Op.cs`, so applying a mapping never means editing the emulator by hand.

```bash
protocolbuilder proto    <client dll> [out.proto]      the client's own message shapes
protocolbuilder mapear   <old client> <new client>     who is who between two versions
protocolbuilder capa     <client> <anchors> . --aplicar  regenerate Op.cs and migrate call sites
protocolbuilder bajar    3.6.4.3 3.6.10.10 clientes    fetch old clients from the CDN, 183 MB each
protocolbuilder cadena   clientes                      measure each patch on its own
```

> `proto` also settles what a message carries from the client's own schema: `lth { bool, bool }` is two booleans.

Full write-up in `docs/deobfuscation.md`.

---

## 🧱 Source layout

The three executables:
* **`Jondo.Unity.Server`** → `Jondo Server.exe` — proxies, network parser, handlers, managers, database and the server's log window. The spell effect engine lives in `Managers/`: `SpellEffects` reads the spell data, `EffectEngine` applies it, and `Summons` builds summoned fighters from monster templates
* **`Jondo.Unity.Launcher`** → `Jondo Emulator Launcher.exe` — the player's window, in Avalonia. References the contract and the sprite renderer
* **`Jondo.Unity.Studio`** → `Jondo Studio.exe` — the world editor, in Avalonia

Shared:
* **`Jondo.Unity.Contract`** — paths, settings and the shared palette
* **`Jondo.Unity.Contract.WinForms`** — the Windows Forms shell of the server window
* **`Jondo.Unity.Core`** — networking infrastructure and TCP servers
* **`Jondo.Unity.Auth`** — authentication and HAAPI handlers
* **`Jondo.Unity.Protocol`** — message definitions and the generated `Op` layer
* **`Jondo.Unity.World`** — world logic, `FightInstance`, the fight rulebooks (`FightRules`), buffs and states (`Buff`), area shapes and displacement (`Zone`), isometric geometry (`MapGeometry`), the criterion evaluator (`Criterion`), raids and the kanojedo content
* **`Jondo.Unity.Sprites`** — draws a character or an NPC out of the client's own bones, skins and atlases. Shared by the Studio and the launcher
* **`Jondo.Unity.Cytrus`** — reads Ankama's Cytrus manifests and plans the bundle requests; the launcher's texture-pack download uses it
* **`Jondo.Unity.Parser`** — capture parsing
* **`Jondo.Unity.Tests`** — the xUnit tests, and the gate on publishing

The protocol toolchain, which the emulator does not depend on:
* **`Jondo.Unity.Reversing`** — reads a client with Cpp2IL, rebuilds the `.proto`, matches two versions, indexes the code, downloads old clients from the CDN (`Cytrus`) and generates the `Op` layer
* **`Jondo.Unity.ProtocolBuilder`** → `protocolbuilder` · **`Jondo.Unity.Deobfuscator`** → `Jondo Desofuscador.exe`
* **`JondoFix`** — the MelonLoader client mod, source plus the compiled dll

Documentation index in `docs/README.md`. Start with `docs/protocol.md` (how a message travels), `docs/opcodes.md` (what each opcode means and where it was seen), `docs/fight.md` (a fight on the wire, opcode by opcode) and `docs/deobfuscation.md` (surviving a patch).

---

## 💾 Database and persistence

Three **SQLite** databases in `bases/`, and one folder of text:

* **`world.db`** — characters, inventories, positions, map persistence, spells, monsters, appearances, wardrobe, haven bags, houses and their chests, bins, guilds, the guild chest and raids, quests, achievements and the tallies they count, learned emotes. Distributed compressed as `datos/world.zip` (24.8 MB) and extracted on first run.
* **`auth.db`** — accounts and authentication sessions, created on first run.
* **`paquetes.db`** — the packets the server does not yet know how to answer, deduplicated by protobuf shape. It carries nothing needed to play and can be deleted to start over.
* **`content/`** — the authored layer, in versioned JSON. The only one edited by hand, and the only one nothing regenerates. See [Jondo Studio](#%EF%B8%8F-jondo-studio).

Files are looked up in `datos/`, then `bases/`, then the root.

Some regression guards run at startup and throw, so the server refuses to boot when the data it was shipped does not match what the code expects — see [Tests](#-tests).

---

## 🤝 Community projects

Projects other people build on top of Jondo. They live in their own repositories, with their own authors and licences: Jondo does not build, test or review them, and links them here so they can be found.

| Project | Author | What it is |
|:---|:---|:---|
| [Jondo.Unity.WebClient](https://github.com/leonardo-spy/Jondo.Unity.WebClient) | [leonardo-spy](https://github.com/leonardo-spy) | The Dofus 3 client running in the browser (Unity, WebGL), connecting to Jondo over WebSocket. |

Building something on Jondo? Open an issue or a pull request adding it to this table.
