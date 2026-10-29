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

- 🌍 &nbsp;**World** &nbsp;— &nbsp;[Connection and authentication](#-connection-and-authentication) · [World and movement](#%EF%B8%8F-world-and-movement) · [Travel](#-travel) · [Houses, bins and haven bags](#%EF%B8%8F-houses-bins-and-haven-bags) · [Banks and marketplaces](#-banks-and-marketplaces) · [Social](#-social) · [Guilds and raids](#%EF%B8%8F-guilds-and-raids) · [Alliances](#-alliances)

- 🎒 &nbsp;**Character** &nbsp;— &nbsp;[Character and inventory](#-character-and-inventory) · [Appearances](#-appearances) · [Professions](#%EF%B8%8F-professions)

- 📚 &nbsp;**Content** &nbsp;— &nbsp;[NPCs and monsters](#-npcs-and-monsters) · [Quests](#-quests) · [Achievements](#-achievements) · [Almanax](#-almanax) · [Dungeons](#-dungeons) · [Infinite Dreams](#-infinite-dreams) · [Jondo Coin](#-jondo-coin)

- ⚔️ &nbsp;**Combat** &nbsp;— &nbsp;[One engine, four rulebooks](#%EF%B8%8F-one-engine-four-rulebooks) · [PvM](#-pvm-combat) · [Duels](#-duels) · [Koliseo](#%EF%B8%8F-koliseo) · [Spell effect engine](#-spell-effect-engine) · [Spell check-list](#-spell-check-list) · [Combat challenges](#-combat-challenges)

- 🔎 &nbsp;**Tools** &nbsp;— &nbsp;[Admin window (F10)](#-admin-window-f10) · [Jondo Studio](#%EF%B8%8F-jondo-studio) · [Surviving the next patch](#-surviving-the-next-patch)

- 🤝 &nbsp;**Community** &nbsp;— &nbsp;[Community projects](#-community-projects)

- 🧱 &nbsp;**Under the hood** &nbsp;— &nbsp;[Tests](#-tests) · [Source layout](#-source-layout) · [Database and persistence](#-database-and-persistence)

---

## 🚀 Quick Start

**Nothing has to be compiled.** The launcher ships as a single ready-to-run executable with every dependency inside it, and the world database ships compressed and extracts itself on first run.

**⬇️ Download a zip from the [Releases page](../../releases)** and unpack it anywhere. No need to clone this repository to play.

* **`Jondo-Full-vX.Y.Z-win-x64.zip`**: the whole emulator, with the launcher, the server, Jondo Studio and the data they read. Take this one to run a server.
* **`Jondo-Launcher-vX.Y.Z-win-x64.zip`**: only the launcher, for a player whose server runs on another machine. Point the launcher at it with a `servidor=<address>` line in `%APPDATA%\Jondo\lanzador.cfg`.

Either one unpacks as a **`JondoEmu`** folder. Keep that name and put it beside the client's folder from Step 2: that is where JondoFix looks for it.

### Step 1 — Install the .NET 10 runtime

Download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). The *Desktop Runtime* is the one you want.

### Step 2 — Get the 3.6.10.11 client

The live game is on 3.6.11.12 and Ankama's launcher only provides the current version, which this
emulator does not support.

**⬇️ [Dofus 3.6.10.11 — download](https://www.swisstransfer.com/dl/01a13ca0-ff03-7060-b3a6-b847936d1198)**

It is the stock Ankama client. Unpack it as a **`Cliente 3.6.10.11`** folder beside the emulator
folder, which is where the launcher looks first; anywhere else works if you point **Settings** at
your `Dofus.exe`.

A 3.6.10.11 or 3.6.10.10 install from before the patch also works — keep the official launcher
from updating it.

### Step 3 — Point the Dofus client at the emulator

The official client talks to Ankama's servers and checks their SSL certificates. **JondoFix**, a MelonLoader mod, redirects it to your machine instead. It comes already built in this repository.

1. Get **MelonLoader 0.7.x** from [its releases page](https://github.com/LavaGang/MelonLoader/releases). 0.7.x is published as *Open-Beta*, so it shows up as a **pre-release** and the page's "Latest" tag points at 0.6.x. **0.6.x does not work with this client** — tick *show pre-releases* and take 0.7.x. This repository is tested against **0.7.3**.
2. Run the installer and point it at your **`Dofus.exe`**. MelonLoader detects the rest (`Game Type: Il2cpp`, `Game Arch: x64`, `Runtime Type: net6`, Unity `6000.3.16f1`).
3. Nothing to copy: every time the **launcher** starts the client it puts this repository's **`JondoFix/JondoFix.dll`** into the client's **`Mods/`** folder if the one there differs. Only if you start the client some other way, copy that file into `Mods/` yourself (next to `Dofus.exe`; create the folder if it is not there).

Afterwards:
* The installer drops a **`version.dll`** next to `Dofus.exe`, which loads MelonLoader. Renaming it to `version.dll.disabled` turns the whole thing off so you can play the official game; renaming it back turns it on again.
* MelonLoader writes a log per run under **`MelonLoader/Logs/`**. If the client starts but never reaches the emulator, look there first.

What JondoFix does: sends the client's sockets, Named Pipes and DNS queries to `localhost` (ports `8888`, `5555`, `15881`, `6337`), accepts the local self-signed certificate, and injects the environment variables the client expects. It also adds the JondoBots card to the Koliseo window and the [admin window (F10)](#-admin-window-f10).

### Step 4 — Run it

Double-click **`Jondo Emulator Launcher.exe`**. It launches **`Jondo Server.exe`** itself, in its own window with the log and the counters.

On the first run it unpacks `datos/world.zip` into `bases/world.db` (about 240 MB) and creates `bases/auth.db` with a test account. Sign in to add an account to the launcher's team, tick one or several saved profiles, then press **Launch selected**. Up to eight independent Dofus clients can be active at once.

```
Account: keka
Password: test
```

The test account is an **administrator**, so every command and the [admin window (F10)](#-admin-window-f10) can be tried with it, and it comes with characters already made. For a player account of your own, use **CREATE ACCOUNT** in the launcher.

> ⚠️ The test account's password is published right here. Before opening a server to other machines (`JONDO_PUBLIC_BIND=1`), change it or delete the account: anybody who knows it is an administrator.

`world.zip` is only unpacked when there is no `bases/world.db` yet. To get the characters of a newer download over an older installation, delete `bases/world.db` (and with it the characters made on it) before starting.

The **ES / EN / FR** switch sets the language of the launcher *and* of the game.

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

Player and administrator actions are logged in `logs/activity.jsonl`. Credentials, launcher tokens and game tickets are never included.

---

## ✅ Emulation status

✅ done · 🟡 partial · 🚧 in progress · ❌ missing

### 🖥️ Launcher
<img width="2560" height="1512" alt="image" src="https://github.com/user-attachments/assets/68e0e721-b36c-4524-b5d6-660fd5beb3c0" />
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/86f835e0-f161-4f51-af42-a810a192f150" />

Built with **Avalonia**, the same toolkit as the Studio.

- ✅ Three screens — *Play*, *Accounts*, *Settings* — with the server status in the header
- ✅ Account cards with the character drawn in them, as they look in the world: head, equipment and cosmetics
- ✅ A team of up to 8 accounts, one independent Dofus process each
- ✅ Account creation and login; saved credentials are encrypted
- ✅ Embedded server log; single-file deployment; ES/EN/FR
- ✅ HD and 4K scenery packs from Ankama's own CDN for the client's version: resumable and verified downloads
- ✅ Animated neon sign and falling stars
- 🚧 OAuth sign-in

### 🧩 Server
<img width="2558" height="1508" alt="image" src="https://github.com/user-attachments/assets/df3cce87-166d-4f5a-8aff-a4fcd2575c87" />

`Jondo Server.exe`. The launcher starts it, but it can be run on its own — or on another machine.

- ✅ Four listeners in one process — Zaap (`8888`), game (`5555`), chat (`6337`) and HAAPI (`15881`)
- ✅ One session per socket: eight clients on one machine never see each other's state
- ✅ Its own window with the live log, the counters and the connected clients
- ✅ Startup checks that refuse to boot when the shipped data is inconsistent — see [Tests](#-tests)
- ✅ Runs on another machine with `JONDO_PUBLIC_BIND=1`; the launcher relays the client to it
- ✅ A **Settings** window (the *SETTINGS* button) for what the game itself does not decide, stored in
  `config\server_settings.json`. *SAVE AND RESTART* warns every player, counts down a minute and
  restarts the server with the new values. Every setting at its default leaves the game as it is.

  | Setting | What it does |
  |:---|:---|
  | Fewest players to start a raid | Below the client's eight, for small guilds |
  | Most AP / most MP | A cap on what a character can stack |
  | Experience, drop and kamas bonus | A percent on top, for everybody |
  | Hardcore mode | Monsters at twice their size, with three times their life and characteristics |
  | Dying costs no energy | A lost fight keeps the energy |
  | Random loot | See below |
  | Welcome message | A line in the chat on coming into the world |

  **Random loot.** Besides its own table, each monster beaten has a chance (10 % by default, raised
  by challenges and the drop bonus) of dropping two to five items around its level: ordinary
  equipment 25 %, cosmetics 15 %, mounts and pets 10 %, dofus 5 %, legendary equipment 0.5 %, a
  perfect piece with an exo 0.05 %, and consumables the rest. Every piece of equipment that drops
  comes with its characteristics rolled in its range.

### 🔐 Connection and authentication

- ✅ Zaap, HAAPI and connection server emulation
- ✅ Account creation and login, with hashed passwords and rate-limited attempts
- ✅ Server and character selection, showing the mount being ridden and each character's equipment
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/c4c194ad-dcd1-407f-a3f1-b44c8f4baed2" />
<img width="2558" height="1504" alt="image" src="https://github.com/user-attachments/assets/70d02ad2-8fc0-4ec8-b836-1dda959ed271" />

- ✅ Character creation with a starter kit — Astrub zaap, adventurer set, 1,000,000 kamas, 100
  scrolled points per characteristic
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/881c9530-6631-46ed-b85e-c7fd92602455" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/09a94e1f-165a-407d-91f1-1dd7b18063de" />
<img width="2558" height="1510" alt="image" src="https://github.com/user-attachments/assets/a65407d0-7e65-4481-bdfe-ea65554bb29e" />

- ✅ Account roles
- ✅ Reconnecting into a fight: close the client mid-fight and the fight goes on; log back in and
  you are put back on the board as it stands

### 🗺️ World and movement
- ✅ World loading, spawn, last cell and map saved. Multiclient
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/8882de29-36b0-4af9-be22-2d5f3bd4c6d4" />

- ✅ **15,360 maps** with walkable and combat cells
- ✅ Movement, map changes, auto-pilot from the minimap and *travel to*
<img width="538" height="452" alt="image" src="https://github.com/user-attachments/assets/a6438938-00c2-4a76-b4e1-48abf3d56934" />

- ✅ Other players arriving and leaving
- ✅ Everybody is drawn wearing their gear — on the map, in fights and on the selection screen

### 🌀 Travel

- ✅ **62 zaaps** with the real cost and destination list
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/1524d485-a845-4b62-a71c-b88de3bb7b54" />

- ✅ Discovered zaaps remembered
- ✅ Saving your position at a zaap: a lost fight and the recall potion bring you back there
- ✅ Zaapis of Bonta (24) and Brakmar (21)
<img width="2560" height="1484" alt="image" src="https://github.com/user-attachments/assets/472e09f2-a49d-431e-8935-f60355457cdd" />

- ✅ Boats
- ✅ The karts of route 666 in the Landas de Sidimote: seven stops, 100 kamas a trip
- ✅ The Travelers' Tower: its seven rooms joined by their teleporters, and the dimensional portal —
  the mechanism opens it to Anutropía, Sramvil, Xelorium, Zurcalia or Osavora for those with that
  dimension's *Portal* alteration, everybody in the room sees it change, and each dimension's portal
  leads back to the tower
- ✅ Finding a fissure: each model in the localization room launches its dimension's legendary hunt
  (level 50) — five stages of five steps, flags, digs and four tries — that ends in a fight against
  the fissure and four of its dimension's monsters; whoever wins it gets the portal for good, and
  the hunter its divine chest; lost, the hunt is lost
- ✅ The hunts' steps name the landmark to look for — 166 of them on 3,947 maps, as the players have mapped them
- ✅ A nod to the old days: the portals to Anutropía, Sramvil, Xelorium and Zurcalia, gone from the game
  since version 3.2, stand for good in the Amakna village square by the vendors — anybody can talk to
  them or cross them, as often as they like
- ✅ Zurcarák's herald at Zurcalia's entry
- ✅ Osavora's seasons — Naissance, Chasse and Repos, one a week from Monday 8:00 — change the look of
  the dimension's maps
- ✅ Osavora's weekly quests come with their season, once a week each: the season is the game's world
  event and the quest's marker says when you last did it
- ✅ The seasons change Osavora's monsters: +1 MP in the Naissance, more damage in the Chasse, more life in
  the Repos, and every fifth blow they take, the season's own reaction
- ✅ The titan Gargandias, every weekend from Friday 19:00 to Monday 8:00: his temple in Osabana, the altar
  that takes the season's offering (1, 2, then 3 of it, five tries a week), the grate it opens and the
  fight in his room, four players at most
- ✅ **16 temporal anomalies** with their 120-minute countdown
- ✅ Their guardians: each anomaly opens on one of eleven places — Noximilian's workshop, Kelba's
  market, Bonta's palace, Brâkmar… — where its guardian waits with his brutoforme, cublaforme and
  kaxaforme; the zone shows the anomaly, its zaap the vestige, and the clocks tell the hour
<img width="2560" height="1514" alt="image" src="https://github.com/user-attachments/assets/942d4d71-9711-45f1-9156-5381f7ad14b8" />

- ✅ **5,347 interactive teleports** and passages that fire when you step on a cell — the suns and
  stars of houses and caves among them
- ✅ 510 more cells that move you on — the stairs and holes drawn into dungeons and caves, the Dragocerdo's
  labyrinth, the Gélifux caverns, Otomai's ark… —, the ones a quest gates only for those on it
- ✅ The GM prison's three maps joined in a round: trapdoor, hanging cage and treasure chest
- ✅ New passages can be created, both ways, from Jondo Studio
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/e3908060-2ad1-415c-a11a-cb6f323b9378" />

### 🏘️ Houses, bins and haven bags

- ✅ **765 houses** (1,437 door spots on 553 maps), all enterable, with **261 house models**
<img width="1112" height="920" alt="image" src="https://github.com/user-attachments/assets/1506283c-f6cd-45b5-b9c4-f345273f67bb" />

- ✅ Every house can be bought, each with a model of its own neighbourhood
- ✅ Owning a house: plaque, selling and taking off sale, the access code and its keypad, the house
  chests with their lock code
- ✅ Houses with their own rooms and chests: 744 of the 765 doors lead into a house of their own
  neighbourhood with a chest, its stairs and trapdoors taking you through its rooms
- ✅ A house is private: only its owner, whoever knows its code, or its guild when he shares it
- ✅ Sharing a house with the guild, with its rights over the door and the chests
- ✅ Throwing someone out of your house
- ✅ Each house is its own copy of its interior: owners of different houses never meet inside
- ✅ The estate agency of Bonta and Brakmar: every house on sale, searched by price, area, rooms and
  chests
- ✅ Several owners per building, as in the real game: the Casa grande de Bonta has 24 copies, and
  the door lists each owner's and the next free one
- ✅ The Palacio de los Lagos encantados: its door leads into the palace, and its stars, doors and
  stairs take you through its fifteen rooms; the vault door opens on the treasure room's six chests
- ✅ **67 public bins**, shared by everybody
- ✅ Haven bags: entering and leaving, their own zaap, **four rooms** and **every theme** for every
  character, **4,083 furniture pieces** placed and saved, the chest, the lottery machine
- ✅ Each haven bag is its owner's alone, from level 10
- ✅ Sharing it with friends or the guild, who visit it from the friends and guild lists
- ✅ Inviting a player into it, and putting a visitor out
<img width="2560" height="1492" alt="image" src="https://github.com/user-attachments/assets/a81a3b24-8559-4ad5-8a27-e6913eef95a8" />

> The client does not say which interior sits behind each door: each door leads to one of the
> client's own houses of its neighbourhood (Bonta's and Brakmar's to their residences), with its
> rooms and its chests. The mapping lives in `datos/casas_mundo_3.6.10.10.json` and can be
> corrected by hand.

### 🏦 Banks and marketplaces

- ✅ The bank: the account's items and kamas, shared by all its characters, in all ten banks of the world
- ✅ The bank's transfer buttons: everything in or out, what is already on the other side, a selection
- ✅ The guild chest, in the 24 banks that have one, with the ranks' rights per tab and a new tab at
  guild levels 3, 7 and 11
- ✅ The guild chest's history: every deposit and withdrawal in the logbook's chest tab
- ✅ Items sent from one tab of the guild chest to another, joining their like there
- ✅ The bank from level 10: under it the banker says so and offers no chest
- ✅ The seven marketplaces — resources, equipment, consumables, runes, creatures, souls and
  cosmetics — across **40 counters**, the breeders' village's four among them: browse, buy lots of 1, 10, 100 or 1000, sell for the 2 % tax,
  take a lot back, change its price, the sales history
- ✅ Item tooltips show the average price items sell for in the marketplaces
- ✅ The shop's gifts window: take a gift, or all of them, with any of the account's characters
- ✅ The veteran rewards: the 76 the client lists, from 5 «Silbar» scrolls at a week to the as'hesin set,
  arrive as gifts as the account's days of subscription earn them
- ❌ Merchant mode: the 3.6 client has no screen for it

### 💬 Social

- ✅ Information messages in the client's own language
- ✅ Level-up window, with the new spells added to the bar
<img width="2560" height="1514" alt="image" src="https://github.com/user-attachments/assets/490997fc-1300-4a29-9963-32077efdf0dd" />

- ✅ Private messages
- ✅ Player cards («Mostrar perfil»): class, level, looks, whether they are playing and the background you pick for yours
- ✅ «Informaciones» on a player: the game date and who they are in the chat — account, area, server, guild and alliance
- ✅ Pings on players and creatures on the map, seen by your party
- ✅ Friends window: contacts, mutual friends seen playing (character, level, class, in a fight), blocked and ignored players
- ✅ Ankama friends: invite by nickname and tag, accept or refuse, see them connected
- ✅ Blocked and ignored players cannot whisper to you, and you do not hear them on the map
- ✅ Chat status: available, away with a message, idle, private (only friends whisper to you) and solo (no party invitations), seen by your friends, guild and party
- ✅ Parties — invite, accept, refuse, leave, hand over the lead, kick, follow the leader, and the leader's
  switch that has the whole party follow him
- ✅ Following any member of the party from his card, not only the leader
- ✅ Trading with another player: items and kamas
- ✅ Commands answer in Spanish, English or French
- ✅ Emotes, sitting included, and smileys; emotes learned from scrolls and potions
- ✅ Administrators' characters have every attitude of the game
- ✅ Party search: post an announcement, browse the board, apply or join at once, accept or reject
  applicants, edit it, leave it, take it down
- ✅ Automatic dungeon search: wait for the dungeons you choose, or any, alone or as a party; the ready
  check; then one party, the key spent and everybody in the first room (fewer than four players in the
  server settings for small servers)
- ✅ Inside a dungeon found by the search: unanimous votes to give up together (everybody back where
  they were, no penalty) or to put someone out; an hour without searching for whoever leaves a full
  group or is put out of one
- ✅ A group of the search short of players can ask to be completed: the newcomers join it in its room
- ✅ The party invitation's *Details* button
- ✅ Withdrawing an invitation still open, and the party window's lock
- ✅ The ladders window: experience, achievements, Koliseo (by mode, class and level band) and Infinite
  Dreams, the top 100 with your own place, and a search by name

### ⚜️ Guilds and raids

**Guilds**

- ✅ Founding at the Guild Temple's altar with the client's own editor, or with `.gremio crear`
- ✅ Gremia, outside the temple, sells the guildalogem, the guild book and the guild shield
- ✅ The guild's name and emblem changed by its leader with the renaming and emblem potions, which Gremia
  sells too
- ✅ The guild shows under every member's name
- ✅ The guild window: header, levels 1 to 20, ranks and rights, members, notes, log, directory profile
  and directory search
- ✅ Applications and invitations, accepting and refusing
- ✅ Leaving and expelling; assigning ranks; handing the guild over
- ✅ The weekly activity tier, contributions (with their *gremichas* in the bag) and the oracle shop
- ✅ The guild chest — see [Banks and marketplaces](#-banks-and-marketplaces)
- ✅ The *Encargos* tab's twelve weekly missions, drawn in the ranks and categories the guild chose
- ✅ Dungeon, expedition, monster-family and Infinite Dreams missions count as you play, once per member,
  with the «¡Encargo terminado!» notice, *gremichas* and activity points
- ✅ Rerolling a mission with guild kamas, and marking missions as interesting for the rest of the guild
- ✅ Your weekly rewards window, with the guild acknowledgments the milestones leave and its *Recuperar*
- ✅ Saving next week's tier and the missions' ranks and categories from their own windows
- ✅ The *Casas* tab: the houses the members share with the guild, and *Ir* to go into them
- ✅ The guild halls of the seven nations: walking through their rooms, choosing one for the guild and
  giving it one of the looks its level opens; the guild hall and guild home potions
- ✅ A guild's card from its name in the chat or the friends list
- ✅ Joining a guild of open recruitment at once
- ✅ A warning when a guild mate comes on line
- ✅ The message of the day and the logbook's bulletin, with who wrote them and when
- ✅ Ranks deleted (their members moved to the rank chosen) and dragged into another order
- ✅ The logbook: members joining, leaving or turned down, ranks given, created and changed, levels
  and chest tabs reached, weekly milestones, the tier and missions changed, missions done and
  rerolled, raids bought, captained, described, left and finished
- ✅ Dream-trial missions, done by finishing the trial
- ✅ Anomaly missions: monsters and dungeon guardians beaten in an anomaly with an uchronic elixir on,
  and the anomaly fragments and loops it drops there

**Raids** — the Eternal Gardens Sanctuary and the Gigalodón Abyss — run on the client's own screens.

- ✅ Buying a raid in the guild shop; joining, leaving, groups, captaincy, invitations
- ✅ Starting with the raid's minimum of players (lowered in the server settings for small guilds)
- ✅ The tracking panel: participants, countdown, score, goals and the Abyss's salt
- ✅ Goals and floors that open with them; the Abyss's lifts, passages, outpost and arenas
- ✅ The Luminarium puzzle, the key fragments, the light bands and their monster boost, the salt
  deposits, the luminomachines, the Exécrabe's forms and statues, the treasure chest and the Gigalodón
- ✅ The Sanctuary: its hub, its 20 health, its four guardians and their unlocking scripts
- ✅ The Sanctuary's castle: the corridor's doors and the tower's stairs, opened by the guardians and
  the corridor; sixty monsters fought alone with their protectors' bonuses; the Reina and the Princesa
- ✅ The Sanctuary's arenas, the manuscript's five pages and the client's own names for its ways
- ✅ The final score screen, the weekly frieze rewards and the raids ladder
- ✅ Monster aggression inside raids
- ✅ The Sanctuary's Obra monocromática: the pedestals' objects, the paper flowers in their order, the red
  flash of a lost life and the zone taking its colour
- ✅ The Sanctuary's Enclave de los protectores: the gardens' statues copied on the Patio's miniature
  boards, the statue rising in the Obra's colour and its sixteen protectors, the wrong one lost at once
- ✅ The Abyss's light drawn on its maps and fight arenas: bands fading every two minutes, the lamps
  blinking before they do, the luminomachines lighting them up again
- ✅ The Sanctuary's Reserva de Belladona: six paper boats found in the Obra and sunk in turns on the
  two battleship boards, a miss floating them all again
- ✅ The Sanctuary's chess in the Patio de Efedra: Belladona's rules, her pieces found green or red on
  the Reserva's four boards, and her game where four players, turned into rooks and bishops, take their
  places before her fourth turn
- ✅ The Sanctuary's guardians wake by their statues once the four enigmas are solved, their logos lit
  in the hub; each one beaten ends its zone, and the fourth dissolves the corridor's door
- ✅ The guardians remember the enigmas: the Vigilante is freed only by the Enclave statue's kind
  falling first, the Guardián's glyphs lie beside their objects and only the flowers' four are safe;
  a wrong one ends the fight
- ✅ The Defensor plays twice a round once its companions are gone, its second turn shown in the
  timeline
- ✅ The Reina Escarlata's fight starts in her four jails: two monsters in each sharing their damage,
  a Floráculo before each, everyone out and healed when the last of them falls
- ✅ The Reina's Exilio Imperial locks a player in a free jail, passing his turns; breaking its Floráculo
  lets him out, Évadé; everybody jailed loses the fight
- ✅ The Reina's second phase: healed and invulnerable until her Pommeau and Lame fall, playing twice a
  round, with the Volonté de la Princesse to beat as well
- ✅ The Princesa Maldita's Aura turns to stone whoever she sees from her second turn, summons hiding
  players from it; her Flor Maldita stands in the middle on four cells
- ✅ The Flor Maldita at its threshold goes over to the players for two rounds, healing and boosting
  whichever camp it is in
- ✅ The Princesa's second phase: healed and invulnerable, playing twice a round with the Volonté de la
  Reine; the Flor's purple glyph gives a one-use Encantamiento Floral, and two of them break her
  invulnerability
- ✅ Guild experience from missions and the raids ladder raises the guild's level

### 🔱 Alliances

An alliance is made of characters, not guilds.

- ✅ Founding at the Temple of Alliances with an alliance gem (sold by Gremia)
- ✅ The tag under every member's name
- ✅ The directory and its search; joining an alliance with open recruitment
- ✅ The alliance window: card, members, ranks and their eighteen rights
- ✅ Applications and invitations
- ✅ The recruitment card, the alliance message and the bulletin
- ✅ Changing the name, tag or emblem with their potions
- ✅ The alliance chat channel
- ✅ Tax collectors: placed with a tax collector potion, outdoors or in a dungeon, one per alliance
  for a territory and its dungeon, gathering from fights there, talked to, collected or attacked
  (five attackers and five defenders at most), with their own window — seven equipment slots and
  six spells — and the alliance's default spells
- ✅ Collecting a tax collector: what is not taken or recycled before closing the window is gone
  with it
- ✅ Defending tax collectors and prisms from the alliance window's fights tab; giving up a defence
- ✅ Prisms: one per subarea, in their alliance's emblem, listed on the world map, talked to, with
  their teleport; attacked and defended
- ✅ 25 % more experience from fights where one's own alliance holds the prism
- ✅ The alliance fight switch
- ✅ The *Pócima del templo de las alianzas*; Hal Ylanza at the temple sells what Gremia sells
- ✅ Nuggets: recycling resources at the neutral prism beside the temple; collecting a tax collector
  recycles its bag half for you, half for the alliance's pot, more with neighbouring territories held
- ✅ The alliance's nugget pot, handed out to the members' banks by those with the right
- ✅ The alliance's share of the nuggets kept in its prisms, more in higher-level zones; at a
  conquest's end the prism's nuggets go to the winner and the alliance that dominated most
- ✅ A beaten prism turns weakened, then vulnerable a day later, with the territory's warnings
- ✅ Conquests (King of the Hill): the conquest window's score each minute, points for the maps held
  by numbers and for aggressions won, twenty minutes of domination to win
- ✅ Attacking enemy alliance fighters in a conquest's zone
- ✅ The winner's thirty minutes alone to place its prism, born pacified
- ✅ Weakened hours, conquest length, invulnerability and a new prism's protection in the server settings
- ✅ Pacifying crystals: Pepito at the temple trades them for nuggets, and one protects a prism for
  twelve hours (four per prism), with no teleport to it meanwhile
- ✅ Conquest roles chosen with Hal Ylanza, each alliance's limit per role, and the role icons over
  the fighters (a seer sees the enemies')
- ✅ The champion's fights count double, the scout's points are handed in with his potion on the
  prism's map, and the sentinel's potion makes him attack enemies passing near
- ✅ No zaap, travel item, haven bag or Koliseo search from a conquest's zone; a new prism cannot be
  attacked in its first hour
- ✅ Taking a defender's place in an alliance fight
- ✅ Losing an aggression in a conquest leaves a tomb: an allied resurrector raises it, an enemy
  gravedigger frees its soul, or the fallen frees it himself; a champion's soul goes at once
- ✅ The top tax collectors on the board in front of the Temple of Alliances: the ten richest in
  the world and the ten richest in dungeons

### 🎒 Character and inventory

- ✅ Alignment: Chantal Inea, in Incarnam's militia, has you swear loyalty to Bonta or Brakmar, Morry
  Gann, in Amakna's, makes you neutral again for his fee, two weeks apart; your side shows on your
  sheet and to the others, and opens the cities' quests
- ✅ Alignment level and order: each of your city's hundred alignment quests raises your level, and the
  orders' apprenticeships give their ranks, from Néophyte to Héros Légendaire; leaving the city loses them
- ✅ PvP switch in the alignment tab: on, your wings show to everybody, and you may attack the other
  city's winged players up to fifty levels above you
- ✅ **21,748 item templates** — spawning, equipping, bags, destruction, persistence
- ✅ **929 item sets** with their bonuses
- ✅ Boxes that open with a double click, by their own drop tables
- ✅ Travel items: recall potion, city, class and quest potions, home potion, anklas
- ✅ Food and potions that restore life and energy; the lower the energy, the less life they give back
- ✅ Characteristic scrolls and breads
- ✅ Books and documents
- ✅ Experience and job scrolls
- ✅ Alterations, with their own window, and the items that grant them
- ✅ Candies and blessings in their bonus slots
- ✅ Perlequines: their flavour lasts its hour on the clock
- ✅ Spell scrolls: the special spells are learnt for good
- ✅ The guild hall and guild house potions
- ✅ Name and sex change potions: the name is asked for, and the sex changed, on the next connection
- ✅ Pets grow from level 0 to 100 fed with resources from their «Alimentar» window, each resource worth
  what the client says, their characteristics growing with the level; the Almanax's day 341 feeds them more
- ✅ Legendary pets: the bebemot's reflection makes a pet legendary, an impregnated croquette gives it a
  power, and the power is a spell in your fights
- ✅ Mutations: the Orbalantir de Meriana takes you to its nine epic fights, most of them turned into a
  monster with its look, spells and characteristics, until the fight is over or you go back
- ✅ Disguises, curses and followers: the camouflage and disguise potions turn you into what they say
  until a fight or your first step, the curses weigh on your characteristics, and the balloons and
  creatures some items call walk behind you
- ✅ **520 mounts** with their look, swapped and unequipped correctly
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/375da573-ab61-4bf0-83fd-6f2a8f872cde" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/581b105c-9569-4f54-ab77-01e122b8ce06" />

- ✅ Breeding: the gates of the six paddocks open the breeding window, each from its breeder level
- ✅ The stable and the paddocks: rides moved between them and the bag, and renamed
- ✅ The paddock gauges: fuel, two on at a time, serenity, love, maturity, stamina and experience
- ✅ Rides that level up to 200, their characteristics growing with their level
- ✅ Mating with its preview, mákinas and genetichas; cloning and extraction
- ✅ Catching wild rides with nets: the net in the consumables slot gives its spell, once a fight
- ✅ Characteristic points, with scrolled characteristics kept apart
- ✅ Dodge, withdrawal, escape, lock, initiative, prospecting and pods count what your equipment gives
- ✅ Builds («Conjuntos»): made empty, from what you wear or as a copy, with their own items, points,
  spells, name and icon; saved or thrown away, updated, deleted, and put on in one click
- ✅ The item bar keeps what you drag onto it — items, emotes and builds — between sessions, and either
  bar can be emptied in one go and stays empty
<img width="708" height="1048" alt="image" src="https://github.com/user-attachments/assets/b07f0ac2-f701-4f3a-82e2-c04f884d696d" />

- ✅ **17,113 spells**; **638 character heads**
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/02b5e575-20e3-47ed-b095-55443fb792ab" />

- ✅ **539 titles** and **167 ornaments**
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/0e579800-2776-4aba-8629-58bb2e6c7acf" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/25225e6b-2b6e-4aa6-8f56-12937b0754a0" />

- ✅ Life regeneration and energy
- ✅ Commands — `.teleport`, `.kamas`, `.shop`, `.size`, `.level`, `.item`, `.itemset`, `.receta`, `.sueno`, `.gremio`
- ✅ Monsters drawn at their size and colours
- ✅ Live administration over HTTP for administrators — see [`docs/live-character-admin.md`](docs/live-character-admin.md)

### 👕 Appearances
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/30ee645b-191b-4146-9966-d2c3fb72a9cf" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/2655dfd1-d565-484c-9735-54dd00f4f8b0" />

**2,371 of the 2,420 cosmetics** of the catalogue work.

| Type | Working / catalogue | | Type | Working / catalogue |
|---|---:|---|---|---:|
| Shields | 524 / 524 | | Petmounts | 151 / 151 |
| Hats | 464 / 464 | | Mounts | 121 / 121 |
| Capes | 357 / 357 | | Shoulders | 121 / 121 |
| Pets | 242 / 242 | | Costumes | 92 / 92 |
| Weapons | 194 / 194 | | Living objects | 61 / 61 |
| Wings | 44 / 44 | | Miscellaneous | 0 / 49 |

- ✅ Appearance weapons and living objects
- ✅ Mount and pet appearances are mutually exclusive
- ✅ Real equipment renders too, and a cosmetic replaces it
- ✅ Per-character colours: the ones chosen at creation are kept in the look
- ✅ The cosmetics collection: each account keeps its own, filled from the bag and emptied back into it
- ✅ The appearance window's draft switch
- ✅ Mimisymbics: a real item spent into an appearance, kept in the collection for good
- ✅ Hiding an empty slot hides the real piece under it
- ✅ The faces tab: any creation face of your class and sex
- ✅ The bodies tab: your class's «base» or «retro» body
- ✅ The colours tab: recolour your character, slot by slot
- ✅ Face, body and pose items unlock their payable look for every character of the account
- 🟡 The idles tab: your class's idle is kept, but the others do not see it yet
- ✅ Saved outfits: keep a look under a name and an icon, star it, copy it, put it on with one click or throw it away
- ✅ A build can be given an outfit, and putting the build on puts the outfit on too
- ✅ Auras: the one you pick is kept between sessions, and other players see it

### ⛏️ Professions

- ✅ **25,090 resources on 4,507 maps** across the six gathering jobs, with their states
- ✅ Job levels and experience
- ✅ Resource protectors: from job level 20 a harvest may bring out its scarecrow, crackrock or kind, which attacks there and then
- ✅ **577 workshop stations** and the craft window with **4,858 recipes**
- ✅ Crafted equipment rolls each characteristic in its range
<img width="2488" height="1396" alt="image" src="https://github.com/user-attachments/assets/9651bea6-a649-4154-9389-f30c1da7ea58" />

- ✅ Smithmagic on the magus tables: success, partial success, failure, over and exo
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/b4b5f143-fe17-4e42-b656-407480a0414c" />
- ✅ Signature runes
- 🟡 The rune odds follow the community's model; Ankama never published theirs
- ✅ Maging for someone else
- ✅ Breaking items into runes, with the focus
<img width="1378" height="1218" alt="image" src="https://github.com/user-attachments/assets/acb6b847-1ab0-4966-83db-657fd0fa38c3" />
- ✅ The artisans' directory
- ✅ Transcendence runes
- ✅ `.oficios`, `.oficio` and `.receta`
- ✅ Forgegod mode for administrators (`.forjadios on|off`)
<img width="1370" height="1182" alt="image" src="https://github.com/user-attachments/assets/751cb412-d2de-4b21-b91e-498342754dce" />

### 👹 NPCs and monsters
<img width="954" height="836" alt="image" src="https://github.com/user-attachments/assets/78779a18-0cd2-4f5c-b403-0c39cd291bcb" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/43ebcfc4-fdbb-4924-b876-0c06743f8294" />
<img width="2558" height="1510" alt="image" src="https://github.com/user-attachments/assets/0b4adf75-9b36-4298-b428-d0444297adb3" />

- ✅ **6,468 NPC templates** with their looks and dialogue trees
- ✅ **422 NPCs** placed across **202 maps**
- ✅ **5,134 monsters**, quest monsters and archmonsters included — one archmonster of each per zone, back
  two hours after it is beaten
- ✅ Soul capture: wear an empty soul stone, cast Captura de Almas, win with its state still on —
  the group's souls go into full stones, each archmonster and guardian in its own, up to the stone's level
- ✅ Wanted criminals: 81 of them, and the two bounties no quest wants, each in one group of his zone and
  gone three hours once caught;
  beat him with his quest to take him — he walks behind you — and hand him to his jailer, in his prison, who
  also sells news of him for 500 kamas
- ✅ Every wanted poster can be read on its board — the militias of Astrub, Amakna castle, Frigost, Bonta and
  Brâkmar, the Justiciers' base, Sufokia, Saharach and the divine dimensions' outposts — and starts its quest
<img width="1700" height="930" alt="image" src="https://github.com/user-attachments/assets/02254e58-ec87-4839-82ac-f142ec5ef9cd" />

- ✅ **38,744 monster groups**, respawned and kept populated, across **562 sub-areas**
- ✅ No monsters indoors or on zaaps
- 🟡 371 monsters have no spells in the client's own data: summoned objects, and six that roam (eggs, spirits)
- ✅ Dialogue trees authored in `content/npcs/dialogues.json`
<img width="1138" height="694" alt="image" src="https://github.com/user-attachments/assets/fc1182c3-a261-4bcd-9532-84a2ceda8dc8" />
<img width="1082" height="692" alt="image" src="https://github.com/user-attachments/assets/39cdf857-b506-4968-b2f9-c0c5f80b64c3" />

- ✅ Monster groups placed by hand in `content/monsters/groups.json`
- ✅ The kanojedo of the Amakna village and the training room of the Astrub Militia Tower:
  training fights against the Puch Ingball and its themed versions

### 📜 Quests
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/6dbe2000-4f3c-4b41-9409-5be932f84d6e" />
<img width="1452" height="1226" alt="image" src="https://github.com/user-attachments/assets/77256193-a9dd-48df-9d0a-5408613fef34" />

**1,976 quests**, with their 2,225 steps and 15,547 objectives.

- ✅ Quests handed over by NPCs
- ✅ Objectives completed by clicking, by beating monsters and by bringing the souls they ask for in full soul stones
- ✅ Quest items dropped by monsters to whoever is on the quest, and hunters' meat
- ✅ Step rewards: experience, kamas and emotes
- ✅ The followed quests' box keeps the quests you follow, in the order you put them
- ✅ The yearly events' quests only while their event runs: Halouine, Sain Ballotin, the island of Pwâk,
  Vulkania and the Ripate's feast
- 🟡 Start conditions: 1,964 of the 1,976 are fully understood — the class quests by class, the job
  quests by job level, the Almanax's by the day, Osavora's weekly ones by the season and their marker's week
- ✅ Items that validate a quest objective when used (316 of them)
- ✅ Treasure hunts at the Malle aux trésors: Cano Dhain hands out the daily, archaeological and
  saints' hunts, the Anutrof box draws the map — three or four stages, flags, digs, four tries —
  and the Treasure is fought at the end, hitting the chest on legs among its four Vigicofres; its
  chest closes the quest, one hunt a day from 7:00
- ✅ Legendary hunts: put a criminal's map together from its fragments at the Pupitre, start it at
  the planning table and fight the criminal at its end; a defeat gives the map back
- ✅ The guidebook's suggestions: cards drawn from the client's 1,410 activities by level, category and
  area, and the ones you lock stay

Full workings in **`docs/quests.md`**.

### 🏆 Achievements

**2,780 achievements** in 134 categories.

- ✅ The achievement window with each objective's progress
- ✅ Earned by quests, exploration, levels, crafting, monsters, dungeon challenges and items —
  **2,410 of 2,780** fully judged
- ✅ Rewards claimed from the window: kamas, experience, items and emotes
- ✅ Breeding, ride hunting, sweets, breaking items, Koliseo leagues, guild missions and tokens, challenge
  tallies and the guild hall's achievements
- ✅ Players nearby read in their chat the achievements one unlocks
- ✅ Incarnam's maps, the Infinite Dreams' fragments, monsters and guardians, and the dream trials
- 🟡 Alignment achievements: the level and the order are earned, but no grade nor honour

Full workings in **`docs/achievements.md`**.

### 📅 Almanax

- ✅ **376 days**, each with its saint, offering quest and bonuses
- ✅ Ontoral Zo hands over the day's offering once a day
- ✅ The temple follows the seasons: the sanctuary's door opens on the hall of today's season, its door on
  the room of today's month, and the other seasons' rooms lie asleep
- ✅ Every day its saint, alone in the room of its month
- ✅ The day's bonuses, for everybody, to fights (experience, drops, challenges and an extra one, a second loot in dungeons — by zone,
  dungeon, monster family or item worn), the Koliseo, quests, jobs, harvests, crafting (ingredients
  saved, a second item, runes kept), tax collectors, breeding and paddock items, resources' respawn, life
  regeneration and healing consumables, resource protectors, archmonsters, the quality of what a city crafts,
  pets, wanted criminals, the hunters' meat and the loot in the Dreams
- ✅ «El año del dragón»: every offering gives a page of the calendar, and Ontoral Zo trades 365 of them
  for the Dolmanax

### 🏰 Dungeons
<img width="2550" height="1498" alt="image" src="https://github.com/user-attachments/assets/f79f7881-c68e-45b5-ae29-b4aaba928a1d" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/3c73a696-1de3-46ae-bea6-149bc06009fb" />
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/7b481a47-2fea-43da-a8a0-5b2692030473" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/a6347069-3d31-45bc-b40f-f4d942674e48" />

**187 dungeons**, with their **763 rooms**, their key and their boss.

- ✅ Hand over the key to the guardian, fight room after room, beat the boss and come out
- ✅ Leave a dungeon, or the haven bag, from the map panel
- ✅ The monster side grows with each player in the fight, up to eight
- ✅ Dungeon challenges and their achievements

Full workings in **`docs/dungeons.md`**.

### 🌙 Infinite Dreams

Entered from the Plano Astral's well: a dream of 26 rooms in depth, walked band by band.
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/f33f6c10-6889-41f6-8a25-cc17068b7191" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/3bd5a03b-10e0-4dad-8227-627a5f2abf7d" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/65ae17bf-b8cc-4b3c-a811-3258e7c9bfd4" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/27bdff8a-abac-49da-8e6b-1dc2c72b6095" />
<img width="2560" height="1496" alt="image" src="https://github.com/user-attachments/assets/002bdcb9-da34-44ff-95c3-b2f84e578c6b" />
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/7b2732db-be45-44a2-b405-282091d35f4a" />

- ✅ Ten difficulties in three families (Sueño, Paradoja, Pesadilla)
- ✅ Five bands with fountains, dream favours, fights and the **Fin du rêve** in waves
- ✅ Rewards, dream points, bonuses and dreamer levels shown in the HUD
- ✅ Dream loot and dream fragments
- ✅ The fountains' shop and the dream favours with the guide's whole catalogue
- ✅ Dream spells cast from their bar, with charges and a shared cooldown
- ✅ Every bonus works in fights, for summons too; passives like Vent Arrière and Suelta
- ✅ The dreamer's level and the monsters' rise band by band
- ✅ Goblins: Bog, Gobledore, Gobserico and the Rey Gob
- ✅ Dream codes: replay a dream, training mode, the result window at the end
- ✅ Astral storms, Draconiros arenas, three-minute placement and 90-second turns
- ✅ The Dream Market: Neru Stalar and Gobribe
- ✅ A dream is saved and resumed after a disconnection or a restart
- ✅ `.sueno [row]` for administrators
- ✅ The trial FONSOCAC's rules: weapons cheaper, faster and harder-hitting, monsters with double life
- ✅ Dreams in a group: the owner invites his party, who follow him room to room and fight beside him
- ✅ Clicking a room on the dream's map takes you there, once the room you stand in is cleared
- ✅ The trials REVERSED (fight as the dungeon guardian you choose against adventurers' scraps), NILEZAFF (a hit from afar swaps places and sends the damage back around), SINJSONJ (fight as Simueño beside its Monórico) and DOFOUSSE (fight as Yoy, RamboPL, Snyler or Crvor)
- ✅ Each trial at its own difficulty, with its achievement for winning its final fight

### 🪙 Jondo Coin

A currency of this server's own.
<img width="1676" height="1102" alt="image" src="https://github.com/user-attachments/assets/aee2eb3f-b2a3-4c35-a35c-fafe69669355" />

- ✅ Drops from every monster: one coin per 25 monster levels
- ✅ Its own description in the five client languages
- ✅ Vendors that charge in coins, appearance shops among them

See `docs/jondo-coin.md`.

---

## ⚔️ One engine, four rulebooks

One fight engine serves four kinds of fight:

| | Against monsters | Duel | Koliseo | Training |
|---|:---:|:---:|:---:|:---:|
| Challenges offered | yes | no | no | no |
| Placement clock | 45.0 s | — | 59.2 s | 45.0 s |
| Monster loot and experience | yes | no | no | no |
| Koliseo payout | no | no | yes | no |
| Clears the group on a win | yes | no | no | no |
| A defeat costs energy, half the life and the way home | yes | no | no | no |

### 🐉 PvM combat
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/d5fdf2d1-0244-4529-b2b9-06cf3dcdc1e5" />

- ✅ Tactical arenas, placement phase and cell swapping
- ✅ Swapping places with an ally during the placement: he is asked and accepts or refuses
- ✅ Line of sight, turns with their timer, AP and MP; unused turn time partly kept
- ✅ Movement, tackle and escape
- ✅ Loot, victory and defeat screens, experience over **1,889 levels**, group respawn
- ✅ End-of-fight statistics
- ✅ Monsters and bosses run their own spells' mechanics — see **`docs/bosses.md`**
- ✅ Monster AI that plans its turn
- ✅ Weapon strikes, with their slash
- ✅ Push and collision damage
- ✅ Joining someone else's fight; party fights and their options
- ✅ Cells marked for your side
- ✅ The Forgelance's spells drawn as in the real game, and its Immortal Lance's states
- ✅ Watching a fight: the map's list of fights, or a friend's, guild mate's or alliance mate's from
  their lists; the fight's sides can close it to spectators
- ✅ A won fight is shared among the players
- ✅ Experience as the monster group's tooltip counts it: group bonus, levels, wisdom and the 5 % bonus
- ✅ Limit on earned experience, from the characteristics panel
- ✅ As in Dofus 3, mounts gain experience by breeding and guilds by their weekly activity
- ✅ Losing costs energy and half the life, and sends you back to the zaap; no ghosts or phoenixes,
  as in Dofus 3 (tombs only in conquests)

### 🤺 Duels
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/286367e0-6342-4aef-b07b-52d3bdbdf9d4" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/c1f3f058-81ea-4189-8f4b-312e3209f63a" />

Player against player, on the map, by challenging somebody standing there.

- ✅ Offer, accept and refuse
- ✅ Both fighters with their own look, level, characteristics and equipment
- ✅ Victory and defeat screens, and both sides back on the map
- ✅ Nothing is won and nothing is lost

### 🏟️ Koliseo
<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/2b19a035-4124-41d6-9c43-0c6881f69e40" />
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/417c9bf9-c895-4a7a-8de3-ef394ce5392c" />
<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/0a3bc3d6-ce61-4729-aa23-958a024c07fb" />
<img width="2560" height="1496" alt="image" src="https://github.com/user-attachments/assets/dfb0aeb1-b507-48b0-8e05-286c3fa1945e" />

Ranked PvP through a queue. Open the window, pick a format, get matched, fight, get paid.

- ✅ 1v1, 2v2, 3v3 and a fourth card, **1v1 against a JondoBot**
- ✅ **JondoBots**: a random class at level 200, dressed in an epic set and riding a mount, played by
  the server's tactics
<img width="2560" height="1506" alt="image" src="https://github.com/user-attachments/assets/7506287d-71b4-40a6-ad76-d4e67625aeac" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/53350da7-e8e9-42b3-85a2-cc8c473a4310" />
<img width="2560" height="1512" alt="image" src="https://github.com/user-attachments/assets/f468a367-7a7e-485b-a552-2ee143b0f05a" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/71b362a6-6893-4652-ad38-e4073343ddb1" />

- ✅ A JondoBot plans its whole turn, trying sequences of casts on a copy of the fight, and plays its
  class: the Xelor's telefrags, the Pandawa's drink and throws, the Zobal's masks, the Sadida's dolls,
  the Tymador's bombs and walls, the Steamer's turrets, the Selatrop's portals, and the rest of the
  classes' states
- ✅ Giving up a fight against JondoBots
- ✅ Giving up a fight of people: leave alone, or vote with your team to surrender
- ✅ Matchmaking by rating, with levels kept apart and parties kept together
- ✅ Leaving the queue
- ✅ The winner is paid — kamas, Kolichas, Vitorichas and experience
- ✅ Fights on the Koliseo's own arenas
- ✅ The ladder: hidden rating, the client's **26 leagues**, placement fights, seasons
- 🟡 The payout is fixed amounts, not the game's formula
- 🟡 The rating curve and the season length are this server's own
- ✅ The *match found* popup with accept and refuse, and the sanction for letting it run out
- ✅ The ranking lists, per mode, class and level band, in the ladders window

### ✨ Spell effect engine

One engine for all nineteen classes, driven entirely by the client's spell data. Of the **179 effects
the 836 class spells use, 116 have a branch in the engine and 63 are plain characteristics** — none
is missing. The table is [`docs/effect-coverage.txt`](docs/effect-coverage.txt).

- ✅ Effects, triggers and target masks read from each spell
- ✅ Area shapes, pushes and pulls, teleports, swaps, carry and throw, illusions
- ✅ Criticals, steals, erosion, damage and resistance percentages, healing, shields
- ✅ Buffs and states, cooldowns and cast limits, spell modifiers
- ✅ Sub-casts, glyphs, traps and runes, summons, bombs and their walls, portals, poisons
- ✅ Class passives and item attitudes
- ✅ AP and MP removal against dodge

### 🔬 Spell check-list

Every class spell resolves in the engine. *Seen working* counts the spells checked against the real
game; the list is generated by `tools/spell_checklist.py`.

| Class | Seen working | Resolve on paper | Spells |
|---|:---:|:---:|:---:|
| Feca | 0 | 44 | 44 |
| Osamodas | 0 | 44 | 44 |
| Anutrof | 0 | 44 | 44 |
| Sram | 0 | 44 | 44 |
| Xelor | 33 | 44 | 44 |
| Zurcarák | 0 | 44 | 44 |
| Aniripsa | 0 | 44 | 44 |
| Yopuka | 44 | 44 | 44 |
| Ocra | 7 | 44 | 44 |
| Sadida | 0 | 44 | 44 |
| Sacrógrito | 0 | 44 | 44 |
| Pandawa | 0 | 44 | 44 |
| Tymador | 13 | 44 | 44 |
| Zobal | 0 | 44 | 44 |
| Steamer | 0 | 44 | 44 |
| Selatrop | 8 | 44 | 44 |
| Hipermago | 0 | 44 | 44 |
| Uginak | 0 | 44 | 44 |
| Forjalanza | 0 | 44 | 44 |
| **All** | **105** | **836** | **836** |

### 🎯 Combat challenges

- ✅ The preparation phase: two candidates, marked and validated
- ✅ **39 of the 43** general challenges watched live, *Hired Killer* among them (the other four are a breach's)
- ✅ The bonus folded into experience, kamas and drops
- ✅ Dungeon and anomaly challenges

---

## 🔑 Admin window (F10)

An administration panel inside the game. Press **F10** with an administrator's character in the
world and a window opens over the map, drawn with the client's own pieces. Press F10 again, or its
cross, to close it.

It started as [JimmyMtl's item window](https://github.com/Keka-Bron/JondoEmu/pull/49) and grew into
five tabs.

An administrator's characters also carry the spells Ankama keeps for its team — *Doom de Masas* and
the «[!] Sorts admins» set — and every attitude of the game.

<img width="2050" height="1426" alt="image" src="https://github.com/user-attachments/assets/e126bd4c-00ef-4266-ab2a-10c57cd872fa" />

### What it needs

- **An administrator account** (see `docs/role.md`). The test account is one.
- **The game started from the Jondo launcher.**
- The server on the same machine, or on another one opened with `JONDO_PUBLIC_BIND=1`.

### The window

- Resizable, see-through while the pointer is elsewhere.
- Wherever a character is chosen, the connected characters are in a drop-down.
- The line at the bottom of each tab says what the server answered.

### Items

- **Search** the client's catalogue by name, type, id or level range; filter by category and type.
- **Give · MAX stats** or **Give · RANDOM stats**, to yourself or any connected character, up to 100
  at a time. `.item <id> [quantity] random` does the same from the chat.
- **Gift**: the item waits on that player's account, in the shop's gifts window, for any of its
  characters to take.

### Character

- **Level**, the six base characteristics and **kamas**, for yourself or any connected character.
- **Give mount**.

### Teleport

- **Find a place** by name, coordinates or map id, then **Teleport**.
- **Go to them** and **Bring here**.

### Spawn

- **NPCs** and **groups of monsters** (up to eight, any grades) on your own cell.
- **On this map** lists every NPC and monster group on your map, each with **Remove**.

### Jail

Ten minutes in the game's own GM prison.

- **Jail** a player: they cannot leave, use commands or speak but in private and on the general
  channel.
- **In jail** lists the prisoners with their time left; **Release** lets one out early. The time keeps
  running offline and across restarts.
- **Go to the jail** takes you to the corridor.

<img width="2560" height="1498" alt="image" src="https://github.com/user-attachments/assets/1b35bc2b-56b6-41ce-af52-6f43f79cc6d5" />

### How it works

JondoFix builds the window at runtime out of the client's own interface components. Every button is
a request to the server's control API, signed with the account's launcher token and checked against
the administrator role. The routes are in **[`docs/live-character-admin.md`](docs/live-character-admin.md)**.

---

## 🛠️ Jondo Studio
<img width="2560" height="1508" alt="image" src="https://github.com/user-attachments/assets/14ee4541-d473-4bd1-81dd-617d03c8ba82" />
<img width="2558" height="1502" alt="image" src="https://github.com/user-attachments/assets/21917c8d-4e7a-43a1-a7bc-73dddee31137" />
<img width="2558" height="1496" alt="image" src="https://github.com/user-attachments/assets/39afe77a-c451-43a2-a67b-8ab5e6abe4c4" />
<img width="2558" height="1508" alt="image" src="https://github.com/user-attachments/assets/c139b38c-232d-4a58-9f45-572e643ccd93" />

> ⚠️ **Very early.** The Studio changes often. Keep a copy of `content/` before a long session. Nothing
> in it can damage `world.db` or a running server.

The world editor: a third executable that needs neither the launcher nor the server running. Built
with **Avalonia**, so it runs on Windows, macOS and Linux.

The client holds every item, spell and monster, but not what the real server decided: which reply in
a dialogue leads to which line, where an NPC stands, which passage leads where. Those are authored in
`content/`, as versioned JSON, so every change is a reviewable diff.

- ✅ **Overview**, **Traffic** (the live client-server conversation) and **Packets**
- ✅ **NPCs**, **Dialogues** and **Monsters**
- ✅ **Spells** — every spell with its effects and its reach drawn on the map
- ✅ **Passages** — join two maps both ways
- ✅ **Map cells** — paint the walkable, line-of-sight and combat layers
- ✅ Spanish, English and French
- 🚧 NPC actions, editing spells, shops, loot tables, dungeons and quests

The full plan is in **`docs/world-editor.md`**.

---

## 🧪 Tests

`Jondo.Unity.Tests` — **2,370 xUnit tests**, grouped by domain. They run in about a minute.

```bash
dotnet test Jondo.Unity.Tests
```

**Publishing the server runs them first and fails if any is red.** The escape hatch is
`-p:SkipTests=true`.

The server also checks its shipped data at startup and refuses to boot when it is inconsistent.

---

## 🔎 Surviving the next patch

Every protobuf message in Dofus 3 is named with three random letters, and on some patches Ankama
reshuffles them. **`protocolbuilder`** is the command line for that; **`Jondo Desofuscador.exe`** is
the same engine behind one window. The `Op` layer is one generated file, `Jondo.Unity.Protocol/Op.cs`,
so applying a mapping never means editing the emulator by hand.

```bash
protocolbuilder proto    <client dll> [out.proto]      the client's own message shapes
protocolbuilder mapear   <old client> <new client>     who is who between two versions
protocolbuilder capa     <client> <anchors> . --aplicar  regenerate Op.cs and migrate call sites
protocolbuilder bajar    3.6.4.3 3.6.10.10 clientes    fetch old clients from the CDN
protocolbuilder cadena   clientes                      measure each patch on its own
```

Full write-up in `docs/deobfuscation.md`.

---

## 🧱 Source layout

The three executables:
* **`Jondo.Unity.Server`** → `Jondo Server.exe` — network, handlers, managers, database and the server window
* **`Jondo.Unity.Launcher`** → `Jondo Emulator Launcher.exe` — the player's window, in Avalonia
* **`Jondo.Unity.Studio`** → `Jondo Studio.exe` — the world editor, in Avalonia

Shared:
* **`Jondo.Unity.Contract`** — paths, settings and the shared palette
* **`Jondo.Unity.Contract.WinForms`** — the Windows Forms shell of the server window
* **`Jondo.Unity.Core`** — the file lookup: `datos/`, then `bases/`, then the root
* **`Jondo.Unity.Protocol`** — message definitions and the generated `Op` layer
* **`Jondo.Unity.World`** — world and fight logic, buffs, areas, geometry and criteria
* **`Jondo.Unity.Sprites`** — draws characters and NPCs out of the client's own bones and skins
* **`Jondo.Unity.Cytrus`** — reads Ankama's Cytrus manifests for the texture packs
* **`Jondo.Unity.Tests`** — the xUnit tests, and the gate on publishing

The protocol toolchain, which the emulator does not depend on:
* **`Jondo.Unity.Reversing`**, **`Jondo.Unity.ProtocolBuilder`** → `protocolbuilder`, **`Jondo.Unity.Deobfuscator`** → `Jondo Desofuscador.exe`
* **`JondoFix`** — the MelonLoader client mod, source plus the compiled dll

Documentation index in `docs/README.md`.

---

## 💾 Database and persistence

Three **SQLite** databases in `bases/`, and one folder of text:

* **`world.db`** — characters, inventories, positions, guilds, alliances, houses and every other saved
  state. Distributed compressed as `datos/world.zip` and extracted on first run.
* **`auth.db`** — accounts and sessions, created on first run.
* **`paquetes.db`** — packets the server does not yet answer. It can be deleted.
* **`content/`** — the authored layer, in versioned JSON. See [Jondo Studio](#%EF%B8%8F-jondo-studio).

---

## 🤝 Community projects

Projects other people build on top of Jondo. They live in their own repositories, with their own authors and licences.

| Project | Author | What it is |
|:---|:---|:---|
| [Jondo.Unity.WebClient](https://github.com/leonardo-spy/Jondo.Unity.WebClient) | [leonardo-spy](https://github.com/leonardo-spy) | The Dofus 3 client running in the browser (Unity, WebGL), connecting to Jondo over WebSocket. |

Building something on Jondo? Open an issue or a pull request adding it to this table.
