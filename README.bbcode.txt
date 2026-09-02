High-performance server emulator for [b]Dofus 3 Unity (Client 3.6.10.11)[/b] written in C# ([b].NET 10[/b]), with decoupled modular projects, a SQLite data layer, a combat engine driven entirely by client data — PvM, duels and Koliseo — a cross-platform launcher and a world editor.

[quote]⚠️ [b]Runs against Dofus 3 clients 3.6.10.11 and 3.6.10.10.[/b] Ankama renames every protobuf message to three random letters on some patches; there is a toolchain here for surviving that — see Surviving the next patch.[/quote]

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]📑 Contents[/b][/size]

[code]
🖥️ Launcher                                                                                                                   🧩 Server                                                                                                                 🛠️ Jondo Studio
----------------------------------------------------------------------------------------------------------------------------  -----------------------------------------------------------------------------------------------------------------------  -------------------------------------------------------------------------------------------------------------
The player's window, in Avalonia. A team of up to eight accounts, each with its character drawn from the client's own bones.  The emulator itself. Four listeners in one process, one session per socket, and guards that refuse to boot on bad data.  The world editor. Nine sections over the client's data, writing a reviewable diff instead of a 240 MB binary.
[/code]

[quote][b]New here?[/b] Quick Start puts you in the game in three steps · What you get is what lands on disk[/quote]

[list]
[*]🌍  [b]World[/b]  —  Connection and authentication · World and movement · Travel · Houses, bins and haven bags · Social
[/list]

[list]
[*]🎒  [b]Character[/b]  —  Character and inventory · Appearances · Professions
[/list]

[list]
[*]📚  [b]Content[/b]  —  NPCs and monsters · Quests · Dungeons · Jondo Coin
[/list]

[list]
[*]⚔️  [b]Combat[/b]  —  One engine, three rulebooks · PvM · Duels · Koliseo · Spell effect engine · Combat challenges · Not implemented
[/list]

[list]
[*]🔎  [b]Tools[/b]  —  Jondo Studio · Surviving the next patch
[/list]

[list]
[*]🧱  [b]Under the hood[/b]  —  Tests · Source layout · Database and persistence
[/list]

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]🚀 Quick Start[/b][/size]

[b]Nothing has to be compiled.[/b] The launcher ships as a single ready-to-run executable with every dependency inside it, and the world database ships compressed and extracts itself on first run.

[size=4][b]Step 1 — Install the .NET 10 runtime[/b][/size]

Download it from [url=https://dotnet.microsoft.com/download/dotnet/10.0]dotnet.microsoft.com[/url]. The [i]Desktop Runtime[/i] is the one you want.

[size=4][b]Step 2 — Point the Dofus client at the emulator[/b][/size]

The official client talks to Ankama's servers and checks their SSL certificates. [b]JondoFix[/b], a MelonLoader mod, redirects it to your machine instead. It comes already built in this repository.

[list=1]
[*]Get [b]MelonLoader 0.7.x[/b] from [url=https://github.com/LavaGang/MelonLoader/releases]its releases page[/url]. [b]Read this bit or you will pick the wrong one:[/b] 0.7.x is published as [i]Open-Beta[/i], so it shows up as a [b]pre-release[/b] and the page's "Latest" tag still points at 0.6.x. [b]0.6.x does not work with this client[/b] — tick [i]show pre-releases[/i] and take 0.7.x. The setup this repository is tested against runs [b]0.7.3[/b].
[*]Run the installer and point it at your [b][font=monospace]Dofus.exe[/font][/b]. That is the only thing you have to choose: MelonLoader works out the rest by itself. On this client it reports [font=monospace]Game Type: Il2cpp[/font], [font=monospace]Game Arch: x64[/font], [font=monospace]Runtime Type: net6[/font], Unity [font=monospace]6000.3.16f1[/font] — you do not set any of that.
[*]Copy [b][font=monospace]JondoFix/JondoFix.dll[/font][/b] from this repository into the [b][font=monospace]Mods/[/font][/b] folder of your Dofus installation, next to [font=monospace]Dofus.exe[/font]. MelonLoader creates that folder the first time the game starts; if it is not there yet, just create it yourself.
[/list]

[quote]The mod ships [b]already compiled[/b] and is the exact binary in use — you never need to build it. [font=monospace]JondoFix/[/font] also carries its source, in case you want to read or change it.[/quote]

Two things worth knowing afterwards:
[list]
[*]The installer drops a [b][font=monospace]version.dll[/font][/b] next to [font=monospace]Dofus.exe[/font]; that is what loads MelonLoader. Renaming it to [font=monospace]version.dll.disabled[/font] turns the whole thing off so you can play the official game, and renaming it back turns it on again — no need to uninstall anything.
[*]MelonLoader writes a log per run under [b][font=monospace]MelonLoader/Logs/[/font][/b]. If the client starts but never reaches the emulator, that file is the first place to look.
[/list]

What JondoFix does: intercepts sockets, Named Pipes and DNS queries and sends them to [font=monospace]localhost[/font] (ports [font=monospace]8888[/font], [font=monospace]5555[/font], [font=monospace]15881[/font], [font=monospace]6337[/font]); stops HTTPS requests from failing against the local self-signed certificate; and injects the environment variables the client expects ([font=monospace]ZAAP_PORT[/font], [font=monospace]ZAAP_HASH[/font], and so on).

[size=4][b]Step 3 — Run it[/b][/size]

Double-click [b][font=monospace]Jondo Emulator Launcher.exe[/font][/b]. That is the only thing you start by hand: it launches [b][font=monospace]Jondo Server.exe[/font][/b] itself, in its own window with the log and the counters.

On the first run it unpacks [font=monospace]datos/world.zip[/font] into [font=monospace]bases/world.db[/font] (about 240 MB, it takes a moment) and creates [font=monospace]bases/auth.db[/font] with a test account. Sign in to add an account to the launcher's team, tick one or several saved profiles, then press [b]Launch selected[/b]. Up to eight independent Dofus clients can be active at once.

[code]
Account: keka
Password: test
[/code]

By default the emulator looks for the client next to itself, in a [font=monospace]Cliente 3.6.10.11[/font] folder beside the emulator folder — or [font=monospace]Cliente 3.6.10.10[/font], whichever it finds first. If yours lives somewhere else, set it in [b]Settings[/b] and point it at your [font=monospace]Dofus.exe[/font]. The choice is remembered, and if the client later moves the launcher says so instead of failing silently.

The [b]ES / EN / FR[/b] switch sets the language of the launcher [i]and[/i] of the game: the client is started with that [font=monospace]--langCode[/font].

[b][font=monospace]Jondo Studio.exe[/font][/b] is the third executable and needs nothing else running: double-click it whenever you want to look at the world or build content. See Jondo Studio below.

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]📂 What you get[/b][/size]

[code]
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
[/code]

[font=monospace]content/[/font] [b]is[/b] in the repository, deliberately: it is the only folder a person edits by hand, it is small, and a change in it is a reviewable diff.

Important player and administrator actions are also written as one JSON object per line in [font=monospace]logs/activity.jsonl[/font]. Commands, equipment moves, lottery prizes, granted items, fights, live administration and new unhandled packet shapes can therefore be filtered without scraping the human-readable console log. Credentials, launcher tokens and game tickets are never included.

Not in the repository because they are not needed to play: [font=monospace]bases/[/font] (built on first run), [font=monospace]logs/[/font], [font=monospace]tools/[/font] (the Python that regenerates [font=monospace]datos/[/font]) and [font=monospace]dofus3_data/[/font] (436 MB of raw client dump, only used by those tools).

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]✅ Emulation status[/b][/size]

✅ done · 🟡 partial · 🚧 in progress · ❌ missing

[size=4][b]🖥️ Launcher[/b][/size]

Rewritten in [b]Avalonia[/b], the same toolkit as the Studio. It used to be Windows Forms, drawn from code; nothing but the music is tied to the Windows desktop any more.

[list]
[*]✅ [b]Three screens instead of one wall of buttons[/b] — [i]Play[/i], [i]Accounts[/i], [i]Settings[/i], with the server-status pill in the header
[*]✅ [b]Account cards with the character drawn in them[/b] — portrait, name, level and a big tick. The portrait is assembled from the [b]client's own bones[/b], exactly the way Jondo Studio draws NPCs: not one image ships inside the executable
[*]✅ The portrait shows the character [b]as they look in the world[/b] — the chosen head, the real equipment and the cosmetics over it, in the same skin list the game client is sent
[*]✅ Persistent team of up to 8 accounts, one independent Dofus process each; the highest-level character of each account is the one shown
[*]✅ Account creation and login, written straight to [font=monospace]auth.db[/font]; credentials sealed with DPAPI
[*]✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use ticket, socket-owned session
[*]✅ Independent lifecycle indicators for profiles, processes and sockets
[*]✅ Embedded server log; single-file deployment; ES/EN/FR
[*]✅ A neon sign that [b]starts like a real tube[/b] — a hand-written stutter sequence, then a steady glow with the occasional flicker — and falling stars behind it. The choreography is written down rather than random on purpose: random timings read as a broken light, not a starting one
[*]✅ Launcher and server are separate programs — the launcher carries no database, maps, handlers or effect catalogue
[*]🚧 [b]OAuth is wired up and waiting for the website[/b] — loopback redirect and PKCE on the launcher side; the server half is deliberately unwritten until there is a site to talk to
[/list]

[size=4][b]🧩 Server[/b][/size]

[font=monospace]Jondo Server.exe[/font]. The launcher starts it, but it is a program in its own right and can be run on its own — or on another machine.

[list]
[*]✅ Four listeners in one process — Zaap ([font=monospace]8888[/font]), game ([font=monospace]5555[/font]), chat ([font=monospace]6337[/font]) and HAAPI (`15881`), plus a self-signed certificate so the client's HTTPS does not fail
[*]✅ [b]One session per socket[/b], not one per account: every handler reads the session it is serving, so eight clients on one machine never see each other's state
[*]✅ Its own window with the live log, the counters and the connected clients
[*]✅ [b]Regression guards that run at boot and refuse to start[/b] when the shipped data does not match what the code expects — see [Tests](#-tests)
[*]✅ A loopback [b]control API[/b] the launcher talks to: log tail, account login, and the characters of an account with the look already composed for drawing
[*]✅ [b]Runs on another machine.[/b] Every listener honours [font=monospace]JONDO_PUBLIC_BIND[/font], and the launcher runs a loopback relay so the client reaches it. The relay is not a convenience: HAAPI and the chat server both hand the client `127.0.0.1`, so repointing the client at a remote host cannot work on its own
[*]✅ Unanswerable packets are recorded in their own database, deduplicated by protobuf shape
[/list]

[size=4][b]🔐 Connection and authentication[/b][/size]

[list]
[*]✅ Zaap, HAAPI and connection server emulation, VIP check bypassed
[*]✅ Account creation and login against [font=monospace]auth.db[/font], with the password hashed and the attempt rate limited **by the socket's own IP** — taking it from the request body meant one JSON field made the limiter useless
[*]✅ Per-client identity chain — instance id, launch hash, Zaap session, game token, single-use ticket, socket-owned session
[*]✅ Server and character selection, showing the mount being ridden and each character's equipment
[*]✅ Character creation with a starter kit — Astrub zaap, adventurer set, 1,000,000 kamas, 101 scrolled points per characteristic
[*]✅ Account roles, and an administrator-only channel over loopback
[/list]

[size=4][b]🗺️ World and movement[/b][/size]

[list]
[*]✅ World loading, spawn, name hover, last cell and map persisted
[*]✅ [b]15,360 maps[/b], [b]17,211[/b] with walkable-cell data, [b]17,222[/b] with combat cells
[*]✅ Movement, map change and adjacent maps; auto-pilot from the minimap and [i]travel to[/i]
[*]✅ Seeing others arrive and leave, in all four directions
[*]✅ Up to 8 clients at once, each on its own socket-owned session
[*]✅ [b]Everybody is drawn wearing their gear[/b] — the other players on the map, the opponent in a fight and every character on the selection screen. Equipment is read per character from [font=monospace]CharacterItems[/font], so it never depends on who happens to be connected
[/list]

[size=4][b]🌀 Travel[/b][/size]

[list]
[*]✅ [b]62 waypoints[/b] with map, cell and sub-area, plus 3 departure-only zaaps the waypoint table omits
[*]✅ Travel between zaaps with the real cost and destination list
[*]✅ Discovered zaaps announced on world entry ([font=monospace]hjk[/font]) — without it the travel window reads "No destination"
[*]✅ Zaapis of Bonta (24) and Brakmar (21) at a flat 20 kamas, read off captures because client data cannot derive them
[*]✅ The right window per list: [font=monospace]hjj[/font] root field 0 zaap, 1 zaapi, 3 boat
[*]✅ [b]16 temporal anomalies[/b] with their 120-minute countdown, surfacing at vestiges (type 359), not at switched-off zaaps
[*]✅ [b]3,815 interactive teleports[/b] imported, 3,719 active across 2,655 maps
[*]✅ [b]Passages that fire when you step on the cell[/b], hooked to the end of a walk rather than to the map edge — which is what the ground-level exits need
[*]✅ Each route carries [b]its own measured interactive type[/b] instead of a forced zero. The type is part of the element's identity on the client side: with a zero the numbers still travel but the client stops attaching the declaration to the drawing, and the exit sun disappears
[*]🟡 [b]Every extracted passage still declares skill 114[/b], which is [i]Utilizar[/i] on a zaap. Measured three ways that agree: Ankama's own world graph uses [b]184[/b] on 5,629 of 5,719 interactive transitions and 114 on none; over 401 captures 184 appears on 420 elements and 114 on 23, every one a zaap; and in our own traffic skill 184 is followed by a map change 178 times while 114 opens the zaap window. New passages written in Jondo Studio declare 184; the extracted rows have not been rewritten
[*]✅ [b]New passages can be created[/b], both ways, from Jondo Studio — which is what makes a house with its own interior possible
[/list]

[size=4][b]🏘️ Houses, bins and haven bags[/b][/size]

[list]
[*]✅ [b]1,437 doors on 553 maps[/b], all enterable and ownerless; [b]261 house models[/b] with name, price and room count
[*]✅ Entering and leaving, which are different messages ([font=monospace]jqw[/font] in, [font=monospace]jru[/font] out), coming out through the door you went in by
[*]❌ The house plaque, chest, access code, buying and selling
[*]✅ [b]67 public bins on 63 maps[/b] — they open, show empty and close
[*]❌ Putting items into a bin and taking them out
[*]✅ Haven bags: entering and leaving, their own zaap, [b]48 themes[/b], [b]4,083 furniture pieces[/b] placed and persisted, chest with the full item flow, lottery machine, and no monsters inside
[/list]

[quote]Which house sits behind which door is [b]not in the client[/b]. The 1,437 doors share [b]114 genuine interiors[/b], assigned deterministically and kept inside their own neighbourhood; the mapping lives in [font=monospace]datos/casas_mundo_3.6.10.10.json[/font] and can be corrected by hand.[/quote]

[size=4][b]💬 Social[/b][/size]

[list]
[*]✅ Information messages as [font=monospace]lqn { type, message, parameters }[/font] against the client's 2,555-entry table, not as chat text
[*]✅ Level-up window with music and animation, on a real gain and on [font=monospace].level[/font] in either direction
[*]✅ Private messages via [font=monospace]kth[/font], which the client routes by opcode and not by channel
[*]✅ Last connection time and IP, stored per character
[*]✅ Parties — invite, accept, refuse, leave, hand over the lead, kick, and a full member sheet
[*]✅ Lead passes on when the leader leaves; a disconnect removes the member and tells the rest
[*]✅ Friends list
[*]✅ [b]Every command answers in the session's own language[/b], from a 48-key catalogue in Spanish, English and French. The language comes from the [font=monospace]--langCode[/font] the launcher started the client with, not from the wire: measured over the nine authentication captures, the client does send its two-letter code, but in [font=monospace]kqz[/font] field 3
[*]❌ The invitation popup's [i]Details[/i] button ([font=monospace]imd[/font] → [font=monospace]ilb[/font]), the dedicated member-gone message ([font=monospace]inc[/font]), party search and following the leader
[/list]

[size=4][b]🎒 Character and inventory[/b][/size]

[list]
[*]✅ [b]21,748 item templates[/b] and [b]66,294 item effects[/b] — spawning, equipping, bags, destruction, persistence
[*]✅ [b]929 item sets[/b] with their bonuses
[*]✅ [b]520 mounts[/b] with their look, swapped and unequipped correctly
[*]✅ Characteristic assignment, dynamic capital, points in sync across every client panel
[*]✅ [b]17,113 spells[/b] across [b]34,823 spell levels[/b]; [b]638 character heads[/b]
[*]✅ [b]539 titles[/b] and [b]167 ornaments[/b], applied, persisted and carried in the map actor block
[*]✅ Commands — [font=monospace].teleport[/font], [font=monospace].kamas[/font], [font=monospace].shop[/font], [font=monospace].size[/font], [font=monospace].level[/font], [font=monospace].item[/font], [font=monospace].itemset[/font]
[*]✅ [b]Live administration over HTTP[/b] — [font=monospace]POST /api/personaje[/font] sets characteristics, kamas and level, grants items or a mount, and teleports a connected character without a reconnect. [font=monospace]POST /api/rol[/font] changes account roles. Administrator only, loopback only, and serialized with the target session
[*]🟡 [font=monospace].level[/font] repaints the in-fight spell bar, but the fighter's own level is not updated, so the engine still resolves spells at the level the fight started with
[/list]

[size=4][b]👕 Appearances[/b][/size]

Dofus does not ship the item-to-look table: the server sends it. [b]2,371 of the 2,420 cosmetics[/b] in the catalogue were measured off captures, one garment at a time.

[code]
Type     Working / catalogue    Type            Working / catalogue
-------  -------------------    --------------  -------------------
Shields  524 / 524              Petmounts       151 / 151
Hats     464 / 464              Mounts          121 / 121
Capes    357 / 357              Shoulders       121 / 121
Pets     242 / 242              Costumes        92 / 92
Weapons  194 / 194              Living objects  61 / 61
Wings    44 / 44                Miscellaneous   0 / 49
[/code]

[list]
[*]✅ Appearance weapons carry no look by design — the client draws them; the server only remembers which of the 10 weapon slots each occupies
[*]✅ Living objects imitate a different garment per variant, stored as [b]543 object/variant pairs[/b] across 10 slots
[*]✅ Mount and pet appearances are mutually exclusive, matching the real server
[*]✅ [b]The real equipment renders too, and a cosmetic replaces it rather than stacking on top.[/b] [b]741 real items[/b] carry their own skin into the look; the slots a visible cosmetic covers are precomputed and skipped
[*]✅ The same skin list now feeds the launcher's portraits, so one change fixes both
[*]🟡 82 of those skins were inferred by image matching and flagged for review by their author, so they are held back at load until somebody measures them
[*]🟡 A second, older look path survives in [font=monospace]InventoryHandler[/font] for four items and disagrees with the new table on both the field and the value. Left alone until a capture says which is right
[*]❌ [b]Per-character colours.[/b] Every look is composed from the breed's default palette: there is no colour column anywhere and [font=monospace]customColors[/font] is null at all eleven call sites. Two characters of the same breed and sex are tinted identically
[/list]

[size=4][b]⛏️ Professions[/b][/size]

[list]
[*]✅ [b]25,090 resources on 4,507 maps[/b] across the six gathering jobs, with graphic → (type, skill) crossed from 305 captures
[*]✅ The three states — full, depleted, busy — including the skill field moving between [font=monospace]f4[/font] and [font=monospace]f3[/font]
[*]✅ Job levels and experience persisted, with the real curve [font=monospace]10 × level × (level − 1)[/font]
[*]✅ What you gather lands in the inventory, and the amount grows with job level
[*]✅ Too low a job level blocks gathering the way the game does it
[*]❌ Crafting professions: workshops, the craft window, and the [b]4,858 recipes[/b] already in the database
[/list]

[size=4][b]👹 NPCs and monsters[/b][/size]

[list]
[*]✅ [b]6,468 NPC templates[/b] with 3D looks and dialogue trees
[*]✅ [b]422 NPCs[/b] standing where Ankama puts them across [b]202 maps[/b], cell and orientation taken from captures, dialogue attached where it was captured
[*]✅ [b]5,134 monsters[/b] with native Protobuf bone models, custom scales and textures, quest monsters and archmonsters included
[*]✅ [b]38,744 mapped mob groups[/b], respawned and kept populated, 1 to 8 monsters each
[*]✅ Sub-area aware spawning across [b]562 sub-areas[/b], with radius-2 cell validation so nothing spawns on decorations or zaap pillars
[*]✅ [b]No monsters indoors, and none standing on a zaap[/b] — not in houses, banks or shops. The rule is two lists and one exception, and the exception is the one that matters: 753 of the 763 dungeon rooms are themselves marked indoors, so a blanket ban would empty every dungeon. 7,214 groups of 38,744 kept out, and the 763 rooms untouched
[*]✅ [b]NPC colours[/b], read as what they are: [font=monospace]index=value[/font] pairs, sometimes hexadecimal. The [b]2,045 NPCs that carry colours[/b] render with theirs
[*]✅ A dialogue always offers at least one real reply, so it can always be closed. With an empty list the client draws its own [i]Leave[/i] which never answers back
[*]🟡 [b]401 monsters have no spells at all[/b] in the database
[*]✅ [b]Dialogue trees.[/b] The client holds every line an NPC can say and every reply it can be given, and never which goes with which — measured across all 6,467 NPCs, there is no field for it. That mapping has always been the server's own, so it has to be authored, and now it can be
[*]✅ [b]Monster groups placed by hand[/b], and Ankama's own removable, without touching the 240 MB database that gets regenerated
[/list]

[size=4][b]📜 Quests[/b][/size]

[b]1,976 quests[/b], with their 2,225 steps and 15,547 objectives, read out of six Unity dumps the repository does not even carry.

[list]
[*]✅ A quest is handed over by an NPC saying a particular line — 1,260 steps declare one and every one of them resolves to real text, which is what ties the quest catalogue to the dialogue trees
[*]✅ Objectives complete two ways: the client says so for the [b]5,670[/b] that ask you to click something the server never sees, and the server counts for itself the ones that ask you to beat a monster
[*]✅ Progress is written the moment it changes — there is no autosave here, and losing an evening's quest is worse than losing a few kamas
[*]🟡 The start condition is a language of its own: [b]29 operators[/b], brackets three deep, and a [font=monospace]![/font] that means "not" without an `=` after it. Six operators are understood, covering every term of **935 of the 1,976** conditions; the rest are let through **and named**, because refusing what this emulator cannot model would put 53% of the game's quests out of everybody's reach
[/list]

Full workings in [b][font=monospace]docs/quests.md[/font][/b].

[size=4][b]🏰 Dungeons[/b][/size]

[b]187 dungeons[/b], with their [b]763 rooms[/b], their key and their boss.

[list]
[*]✅ Talk to the guardian, hand over the key, and you are in the first room; win a fight and you move on; beat the boss in the last one and you come out
[*]✅ The boss is placed at startup in [b]126[/b] dungeons, in the room the data says, at the highest grade it has
[*]✅ The keyring and the required item come straight from the client's own data, which is what makes a locked door possible
[*]✅ Dungeon challenges are imposed at 0% and carry achievements
[/list]

[quote]It is not Ankama's dungeon, and the difference is worth stating: theirs is a chain of rooms and corridors walked through ordinary doors, and [b]not one of the 187 has a single one of its internal passages[/b] — not in the extracted table, not in Ankama's own world graph. A player put in room 0 would have no way out, so winning moves you instead.[/quote]

Full workings in [b][font=monospace]docs/dungeons.md[/font][/b].

[size=4][b]🪙 Jondo Coin[/b][/size]

A currency of this server's own — a real item with its own template, not a reskin of kamas.

[list]
[*]✅ Drops from every monster at 100%, one coin per 25 monster levels: 1 for 1-25, 2 for 26-50, up to 9 at 201+
[*]✅ Its own description in the five client languages, picked at runtime from the language the client is running in
[*]✅ Vendors that charge in coins instead of kamas, one per category, appearance shops among them, priced by item type and rarity
[/list]

See [font=monospace]docs/jondo-coin.md[/font].

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]⚔️ One engine, three rulebooks[/b][/size]

There is one fight engine, and it answers three different games. It does not ask [i]what kind of fight am I[/i]; it asks [b]what do I do[/b], and the answer comes from a rules object — so adding something to the Koliseo touches one class instead of five methods:

[code]
                                      Against monsters  Duel  Koliseo
------------------------------------  ----------------  ----  -------
Challenges offered                    yes               no    no
Placement clock                       45.0 s            —     59.2 s
[font=monospace]kam[/font] type       4                 0     7
[font=monospace]kaa[/font] countdown  yes               no    yes
Monster loot and experience           yes               no    no
Koliseo payout                        no                no    yes
Clears the group on a win             yes               no    no
Moves to the next room                yes               no    no
[/code]

None of those numbers is chosen: the 4, the 0 and the 7 are the [font=monospace]kam[/font]'s field 2 in the captures, and the 592 is the [font=monospace]kaa[/font]'s field 5 in the Koliseo one.

Two rules hold the rest of it together:

[list]
[*][b]The teams are [font=monospace]Azul[/font] and [font=monospace]Rojo[/font], not [font=monospace]Team0[/font] and [font=monospace]Team1[/font].[/b] Nothing assumes one side is the players and the other the monsters, because in a duel both sides are people.
[*][b]Everything sent to a client is composed inside that client's own session.[/b] Each fighter's look, level, characteristics and equipment come from their own record, so what the second player is sent describes the second player.
[/list]

[b]Three architecture tests enforce it[/b], each verified by injecting a real violation and watching it go red: no lookups that assume one team is the players, no rules decided by fight type outside the rules object, and nothing writing to a single socket unless it is painting one person's own view.

[size=4][b]🐉 PvM combat[/b][/size]

[list]
[*]✅ Tactical arenas resolved from each roleplay map by zone offset, with clean context transitions
[*]✅ Placement phase with red and blue tiles and cell swapping before [i]Ready[/i]
[*]✅ Isometric geometry ([font=monospace]MapGeometry[/font]) over a pre-computed O(1) BFS distance matrix, with no diagonal steps
[*]✅ Line of sight traced between cell centres against the arena's own blocker set
[*]✅ Turn protocol, 30-second timers with automatic pass, AP/MP replenishment
[*]✅ Movement with per-tile MP cost and collision against occupied cells
[*]✅ Loot, victory and defeat screens, experience over [b]1,889 levels[/b], level-ups and group respawn
[*]✅ Monster AI: a target chosen [b]per spell[/b], range measured against that target rather than against the nearest enemy, walking to the spell's own range band, [font=monospace]MaxCastPerTurn[/font] honoured, breadth-first pathing around obstacles and line of sight. Measured over the 5,134 monsters: [b]15.1%[/b] cannot reach the player, against 24.9% without it, and [b]87.2%[/b] of action points get spent, against 58.7%
[*]🟡 Weapon strikes apply damage and AP cost; the slash animation does not
[*]🟡 [font=monospace]MaxCastPerTarget[/font], minimum cast interval and cast-in-line are enforced for the player, not for monsters
[*]✅ [b]Push and collision damage[/b], [font=monospace]blockedCells × (level/2 + push − resistance + 32) / 4[/font], floored — measured over 127 collisions, with the resistance subtracted [i]inside[/i] the quarter. The fighter acting as the wall takes half, and the [b]Unmovable[/b] state cancels it. Twelve samples are locked into a startup guard
[*]❌ AP/MP dodge rolls, shields, lock and tackle in melee
[/list]

[size=4][b]🤺 Duels[/b][/size]

Player against player, on the map, by challenging somebody standing there.

[list]
[*]✅ Offer, accept and refuse, with the challenge id echoed through every frame of the fight
[*]✅ Both fighters composed from their [b]own[/b] character record — look, level, characteristics, equipment
[*]✅ Placement with no clock, and no challenges offered: there are no monsters to set them against
[*]✅ Victory [b]and defeat[/b] screens, each player's own, and both sides returned to the map
[*]✅ Nothing is won and nothing is lost — no experience, no kamas, no loot
[*]✅ The end-of-fight card shows the other player's portrait instead of a question mark: an entry with no level is a *monster* to the client, so a person always carries theirs
[/list]

[size=4][b]🏟️ Koliseo[/b][/size]

Ranked PvP through a queue. Open the window, pick a format, get matched, fight, get paid.

[list]
[*]✅ [b]The format table[/b] ([font=monospace]lux[/font] → [font=monospace]ltd[/font]) — 1v1, 2v2, 3v3 open and a fourth closed, byte for byte as the capture
[*]✅ [b]Enrolling[/b] ([font=monospace]lsm[/font]), with the format carried as the client's own enum
[*]✅ [b]The queue state[/b] ([font=monospace]lsx[/font]) pushed back, which is what paints [i]searching[/i] in the window
[*]✅ [b]Matchmaking on enrolment[/b], one queue per format, drawn under a lock so two simultaneous requests cannot take the same person into two fights
[*]✅ Everybody re-checked as still connected [b]before[/b] anyone loses their place in the queue; if somebody dropped, the rest go back to the queue rather than pay for it
[*]✅ The fight itself, with the Koliseo rulebook, and both sides returned to roleplay at the end
[*]✅ [b]The winner is paid[/b] — kamas, Kolichas (item 12736), Vitorichas (34478) and experience. The loser gets nothing, and its experience block carries the gained field [i]absent[/i] rather than zero, which is how the capture has it
[*]🟡 [b]The amounts are constants, not a formula.[/b] Two winners in one capture is not enough to derive one — they go the wrong way round, the higher level earning fewer kamas — so kamas, Kolichas and Vitorichas sit in three named fields. Experience does better: over the band of the winner's own level the two samples land at 7.22% and 6.12%, so 6.67% is used
[*]🚧 The [i]match found[/i] popup with accept and refuse
[*]🚧 Fights are held on an ordinary arena; the real game picks one of the many Koliseo maps at random
[*]❌ Rankings ([font=monospace]iqt[/font], [font=monospace]irc[/font]), two undeciphered lists of over three thousand bytes each
[*]❌ The [font=monospace]lst[/font] redirect to a separate Koliseo server. Jondo is one server and holds the fight in place
[/list]

[size=4][b]✨ Spell effect engine[/b][/size]

One engine for all eighteen classes, driven entirely by client data. Not a single spell is written by hand: everything comes out of [font=monospace]SpellLevels.EffectsJson[/font] and the [font=monospace]Effects[/font] catalogue.

[list]
[*]✅ Effects, triggers and target masks read from the spell — [font=monospace]I[/font] on cast, [font=monospace]TB[/font] turn start, [font=monospace]TE[/font] turn end, [font=monospace]DBE[/font] when hit, [font=monospace]CCMPARR[/font] per tile walked; [font=monospace]a[/font] allies, [font=monospace]A[/font] enemies, [font=monospace]g[/font] summons, [font=monospace]E<n>[/font]/[font=monospace]e<n>[/font] gated on a state
[*]✅ States need no code — effect 950 sets a number, 951 clears it, the masks do the rest
[*]✅ Area shapes from [font=monospace]zoneDescr[/font] — point, circle, cross, line, diamond, square, whole map — with each spell's own per-tile falloff
[*]✅ Displacement — push, pull, step back, step forward, direction taken from the centre of the area, stopping at walls, holes and fighters
[*]✅ Criticals rolled against the spell's probability plus the character's, using the spell's separate critical effect list
[*]✅ Point steal, life steal, erosion of maximum HP and damage-taken multipliers
[*]✅ Buff panel — icon, value, remaining rounds and dispellable flag; buffs start on their delay and expire on their round
[*]✅ [b]Stack limits[/b] — a spell level's [font=monospace]MaxStack[/font] is honoured, so a bonus that builds up stops where the game stops it
[*]✅ Cooldowns and cast limits — per turn, per target, minimum interval, initial cooldown
[*]✅ [b]Rebounds that pick the nearest eligible target[/b] (effect 2160), bounded by a budget so a chain cannot loop, with the damage still attributed to the caster while the animation travels from the previous victim
[*]✅ Summons as real fighters — own sheet, place in the carousel next to their owner, behaviour spell, lifetime, and they all fall when their summoner dies
[*]✅ Item attitudes — the six Dofus and the trophies grant their spell through effect 1175
[*]✅ The characteristic sheet in the shape the client expects: 53 entries in a fixed order, and a single-characteristic refresh [b]replaces[/b] its entry rather than adding to it
[*]🚧 Healing — the FIRE fixed heal (effect 108, 751 spell levels) works. Its five siblings are the same heal in the other elements and none is done: water 2998 (92 levels), air 2999 (66), earth 3000 (62), neutral 3001 (11) and best-element 3002 (30)
[*]❌ Glyphs and traps (effects 400, 401, 1091)
[*]❌ Appearance-changing spells — the transform payload is an opaque blob
[*]❌ Area shapes [font=monospace]G[/font] (55 effects) and [font=monospace]*[/font] (10), which fall back to the centre tile alone
[/list]

[quote]The engine is shared, so every class gets whatever its spells happen to use. Only the [b]Cra[/b] has been driven against real captures spell by spell; the rest are untested. A spell only works when [b]all[/b] of its effects resolve, and the gaps concentrate in a handful of effect families, so they close in blocks rather than one spell at a time.[/quote]

[size=4][b]🎯 Combat challenges[/b][/size]

[list]
[*]✅ The preparation dance, measured across 305 captures with both directions on one timeline: two candidates with a 15-second timer, the player marks and validates, and the server fixes whatever is left when you declare ready
[*]✅ [b]15 of the 16[/b] watched live, with every rule taken from the challenge's own translated description
[*]✅ Results travel the moment they happen — a failure the instant the challenge breaks, a success at the end, a defeat failing them all at once
[*]✅ The bonus is folded into experience, kamas and drop rates on a win; it is not itemised anywhere on the wire
[*]✅ Dungeon and anomaly challenges are imposed at 0% and carry achievements, written once and never offered again
[*]❌ [i]Hired Killer[/i] (35), which needs the server to designate and re-designate the target
[*]❌ Challenges without a measured percentage — the client ships no bonus field, and the same challenge appears at 90 and at 150 always at +60, so there is a per-fight modifier nobody has reconstructed
[/list]

[size=4][b]❌ Not implemented at all[/b][/size]

[list]
[*]Crafting professions
[*]Achievements
[*]Guilds
[*]Party fights
[/list]

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]🛠️ Jondo Studio[/b][/size]

[quote]⚠️ [b]Very early.[/b] The Studio changes every day, and the parts that write files have been exercised by one person on one machine. Read it, use it, tell us what is wrong — but keep a copy of [font=monospace]content/[/font] before a long session, and expect screens to move under you. Nothing in it can damage [font=monospace]world.db[/font] or a running server, which is the one guarantee it does make.[/quote]

The world editor. A third executable next to the launcher and the server, and it needs neither of them running: it opens [font=monospace]content/[/font] and the data files through the same paths the server uses and works on its own. Built with [b]Avalonia[/b], so it runs on Windows, macOS and Linux.

It unpacks [font=monospace]world.db[/font] from [font=monospace]datos/world.zip[/font] the first time it runs, the way the server does, so a fresh clone can open it and see the world without starting anything else.

It exists because of a problem this project could not solve any other way. The client holds a great deal — every item, every spell, every monster — but there are things it has never held, because on the real game they were the server's: which reply in a dialogue leads to which line, where an NPC stands and what it does there, which interactive teleport comes back to which map. Those cannot be extracted. They have to be [b]decided[/b], and until now the only place to decide them was a Python script and a JSON file nobody could review.

[size=4][b]Three layers, and every row says where it came from[/b][/size]

The data lives in three places that cannot be edited the same way: [font=monospace]dofus3_data/[/font] is a raw dump of the client, [font=monospace]datos/*.json[/font] is regenerated by the tools in [font=monospace]tools/[/font], and [font=monospace]world.db[/font] is a 240 MB binary no pull request can review. A hand edit in any of them disappears the next time somebody runs a script.

So there are three layers, merged on load, and only the last one is ever edited:

[code]
layer            where from                      who edits it
---------------  ------------------------------  -----------------------------------
[b]base[/b]      generated from the client dump  nobody
[b]measured[/b]  learned from packet captures    nobody
[b]authored[/b]  decided by a person             this is the one, and it always wins
[/code]

The authored layer is [font=monospace]content/[/font], in versioned JSON, so a change is a reviewable diff and two people can edit different maps without colliding. It stores [b]deltas, not copies[/b], and it can [i]erase[/i] a row it did not write.

[b]Every row carries its provenance[/b], and that column is the point: six months from now nobody will remember whether a cell number was measured off a capture or typed in by hand, and without it on screen the two become indistinguishable.

[size=4][b]What it does today[/b][/size]

Nine sections, [b]in Spanish, English or French[/b] — and the language switch changes both halves at once. The editor's own words come from one catalogue; the game's words are read straight out of the client's [font=monospace]Content/I18n/{lang}.bin[/font], 339,342 texts per language. The format is not documented anywhere; it was worked out and then checked against [font=monospace]world.db[/font], where 500 keys sampled at random came back byte for byte identical, including one of 42,180 characters.

[b]The creatures are drawn[/b], out of the client's own bundles and nothing copied into the repository. Monsters come from a picto atlas, 5,130 of the 5,134 covered. NPCs are assembled the way the client assembles them: bones, a still frame, and the skins the look names. That renderer now lives in its own project, [font=monospace]Jondo.Unity.Sprites[/font], and the launcher draws its account portraits with it.

[list]
[*]✅ [b]Overview[/b] — which files it read and what came out of each. First screen on purpose
[*]✅ [b]Traffic[/b] — the client-server conversation, live and back through the log, every frame read [b]against the protocol the client itself declares[/b]. From here a packet can be named on the spot, from the [b]513 real message names[/b] the client still ships in its metadata
[*]✅ [b]Packets[/b] — every kind of packet seen, with a status ladder: unknown, named, documented, handled, ignored
[*]✅ [b]NPCs[/b] — all 422 placements, with the provenance column and the NPC drawn on the map
[*]✅ [b]Dialogues[/b] — which reply leads to which line, with the text on screen rather than ids
[*]✅ [b]Monsters[/b] — open a group, take a monster out, put another in, move it two cells left
[*]✅ [b]Spells[/b] — every spell with its effects, and the map showing [b]how far it reaches and what it would hit[/b], worked out by calling the fight engine's own [font=monospace]Zone.Casillas[/font] rather than a drawing of it
[*]✅ [b]Passages[/b] — two maps side by side, a door picked on each, and one button that joins them [b]both ways[/b]
[*]✅ [b]Map cells[/b] — the three layers painted one at a time, click to toggle and [b]drag to paint a run[/b]
[*]✅ A section that fails shows its error [i]inside[/i] the editor, and [font=monospace]Jondo Studio.exe --selftest[/font] builds all nine in all three languages against the real data and fails the publish if any throws
[/list]

[b]Everything it writes goes to [font=monospace]content/[/font][/b], in versioned text. Nothing opens [font=monospace]world.db[/font] for writing and nothing talks to a running server.

[size=4][b]What is being worked on[/b][/size]

[list]
[*]🚧 [b]NPC actions per placement[/b] — the right-click menu is drawn by the [i]client[/i] from the template's `actions[]`, so an action written per placement can only take options away, never add one
[*]🚧 [b]Editing spells.[/b] The simulator is there; changing a spell's numbers is not
[*]🚧 [b]Shops, loot tables and dungeons[/b] — all three are screens over data the server already reads
[*]🚧 [b]Editing quests.[/b] The engine plays them and the Studio shows them, but nothing writes one yet
[*]🚧 [b]A thin admin channel[/b] so a running server can be told to reload one domain, without a restart
[/list]

The full plan is in [b][font=monospace]docs/world-editor.md[/font][/b].

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]🧪 Tests[/b][/size]

[font=monospace]Jondo.Unity.Tests[/font] — [b]848 xUnit tests[/b] across 99 files, grouped by domain: [font=monospace]Auth[/font], [font=monospace]Combat[/font], [font=monospace]Content[/font], [font=monospace]Diagnostics[/font], [font=monospace]Economy[/font], [font=monospace]Launcher[/font], [font=monospace]Movement[/font], [font=monospace]Network[/font], [font=monospace]Protocol[/font], [font=monospace]Quests[/font], [font=monospace]Security[/font], [font=monospace]Sessions[/font], [font=monospace]Sprites[/font], [font=monospace]Studio[/font], [font=monospace]World[/font]. They run in about half a minute.

[code]
dotnet test Jondo.Unity.Tests
[/code]

Five of them run against [font=monospace]logs/gameserver_traffic.log[/font] itself when it is on the machine, and skip when it is not. A test that skips proves nothing, and that is the trade being made on purpose: frames this project builds itself only ever prove that the builder and the reader agree, so a handful of checks are pointed at traffic the real client produced.

[b]Publishing the server runs them first and fails if any is red.[/b] Not on build — the inner loop stays fast — but publishing is the one step between writing code and a player running it. The escape hatch is [font=monospace]-p:SkipTests=true[/font], which leaves its trace on the command line rather than in a config file nobody reads.

[size=4][b]Three kinds of check, three homes[/b][/size]

[list]
[*][b]At startup, and it throws[/b] stay the questions of the form [i]"is the data I was shipped sane?"[/i] — the fight sheet's 53 characteristics in their captured order, the interactive registry, the monster spellbooks, the vendor placements, the profession catalogue. `datos/` and `world.db` are regenerated by tooling outside the build, so a bad regeneration reaches a player with every test still passing.
[*][b]In the test project[/b] live the questions of the form [i]"is this code correct?"[/i] — the content layers, the collision damage formula, the Jondo Coin bands, frame limits, protobuf parsing, password hashing, log censorship and session isolation.
[*][b]Architecture tests[/b] ask [i]"is this code shaped right?"[/i] — they read the fight engine's own source and fail on the shapes a multi-client engine cannot afford. They are the only kind that catches a mistake **before** it has a symptom, and they hold an exception list where every entry carries a written reason.
[/list]

Some things cannot be asserted by asking whether an operation succeeded, because it always does: a portrait that draws a character facing away, or with no head, is still a valid PNG. Those are guarded by counting — the animation name has to end in the direction that faces the camera, and the head slot has to contribute more than zero triangles.

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]🔎 Surviving the next patch[/b][/size]

Every protobuf message in Dofus 3 is named with three random letters — [font=monospace]kub[/font], [font=monospace]jru[/font], [font=monospace]lqu[/font] — and on some patches Ankama reshuffles the lot. Nothing else about the protocol changes shape, but the emulator no longer knows what anything is called. [b][font=monospace]protocolbuilder[/font][/b] is the command line for that; [b][font=monospace]Jondo Desofuscador.exe[/font][/b] is the same engine behind one window and one button.

Eight consecutive real clients (3.6.4.3 → 3.6.10.10) were pulled from Ankama's own CDN and compared patch by patch:

[list]
[*][b]Ankama does not reshuffle on every patch.[/b] Three of the seven jumps keep all 2,169 names, one for one — five obfuscation generations across eight versions. The tool checks for the identity mapping first, in a second.
[*][b]Zero wrong pairings over 6,505 real pairs.[/b] The matcher never looks at names, only at field numbers, kinds and neighbourhood. It gets 71.1% and misses none; what it cannot decide, it leaves alone.
[*][b]On a patch that does reshuffle, structure alone gets about 11%[/b] — the ceiling, not a tuning problem.
[*][b]Chaining through intermediate versions is worse[/b]: 12 pairs against 245 for the direct jump. A plausible idea the measurement refuted.
[*]Building the [font=monospace]Op[/font] layer also turned up [b]49 opcodes that only exist in 3.6.4.3[/b].
[/list]

The [b][font=monospace]Op[/font] layer[/b] replaced [b]495 three-letter literals across 35 files[/b] with one generated file, [font=monospace]Jondo.Unity.Protocol/Op.cs[/font], so applying a mapping never means editing the emulator by hand.

[code]
protocolbuilder proto    <client dll> [out.proto]      the client's own message shapes
protocolbuilder mapear   <old client> <new client>     who is who between two versions
protocolbuilder capa     <client> <anchors> . --aplicar  regenerate Op.cs and migrate call sites
protocolbuilder bajar    3.6.4.3 3.6.10.10 clientes    fetch old clients from the CDN, 183 MB each
protocolbuilder cadena   clientes                      measure each patch on its own
[/code]

[quote][font=monospace]proto[/font] earns its keep beyond migrations. What a message carries is settled by the client's own schema rather than by one reading of one capture: [font=monospace]lth { bool, bool }[/font] is two booleans, and no amount of staring at two bytes on the wire says that as plainly.[/quote]

Full write-up in [font=monospace]docs/desofuscacion.md[/font].

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]🧱 Source layout[/b][/size]

The three executables:
[list]
[*][b][font=monospace]Jondo.Unity.Server[/font][/b] → [font=monospace]Jondo Server.exe[/font] — proxies, network parser, handlers, managers, database and the server's log window. The spell effect engine lives in [font=monospace]Managers/[/font]: [font=monospace]SpellEffects[/font] reads the spell data, [font=monospace]EffectEngine[/font] turns it into things that happen to somebody, and [font=monospace]Summons[/font] builds summoned fighters from monster templates
[*][b][font=monospace]Jondo.Unity.Launcher[/font][/b] → [font=monospace]Jondo Emulator Launcher.exe[/font] — the player's window, in Avalonia. References the contract and the sprite renderer, and nothing else
[*][b][font=monospace]Jondo.Unity.Studio[/font][/b] → [font=monospace]Jondo Studio.exe[/font] — the world editor, in Avalonia
[/list]

Shared:
[list]
[*][b][font=monospace]Jondo.Unity.Contract[/font][/b] — paths, settings and the shared palette
[*][b][font=monospace]Jondo.Unity.Contract.WinForms[/font][/b] — what is left of the old Windows Forms shell, kept apart so nothing else drags it in
[*][b][font=monospace]Jondo.Unity.Core[/font][/b] — networking infrastructure and TCP servers
[*][b][font=monospace]Jondo.Unity.Auth[/font][/b] — authentication and HAAPI handlers
[*][b][font=monospace]Jondo.Unity.Protocol[/font][/b] — message definitions and the generated [font=monospace]Op[/font] layer
[*][b][font=monospace]Jondo.Unity.World[/font][/b] — world logic, [font=monospace]FightInstance[/font], the fight rulebooks ([font=monospace]FightRules[/font]), buffs and states ([font=monospace]Buff[/font]), area shapes and displacement ([font=monospace]Zone[/font]), isometric geometry ([font=monospace]MapGeometry[/font])
[*][b][font=monospace]Jondo.Unity.Sprites[/font][/b] — draws a character or an NPC out of the client's own bones, skins and atlases. Shared by the Studio and the launcher so a fix to either reaches both
[*][b][font=monospace]Jondo.Unity.Parser[/font][/b] — capture parsing
[*][b][font=monospace]Jondo.Unity.Tests[/font][/b] — 848 xUnit tests, and the gate on publishing
[/list]

The protocol toolchain, which the emulator does not depend on:
[list]
[*][b][font=monospace]Jondo.Unity.Reversing[/font][/b] — reads a client with Cpp2IL, rebuilds the [font=monospace].proto[/font], matches two versions, indexes the code, downloads old clients from the CDN ([font=monospace]Cytrus[/font]) and generates the [font=monospace]Op[/font] layer
[*][b][font=monospace]Jondo.Unity.ProtocolBuilder[/font][/b] → [font=monospace]protocolbuilder[/font] · [b][font=monospace]Jondo.Unity.Deobfuscator[/font][/b] → [font=monospace]Jondo Desofuscador.exe[/font]
[*][b][font=monospace]JondoFix[/font][/b] — the MelonLoader client mod, source plus the compiled dll
[/list]

Documentation, all of it measured rather than assumed — index in [font=monospace]docs/README.md[/font]. Start with [font=monospace]docs/protocol.md[/font] (how a message travels), [font=monospace]docs/opcodes.md[/font] (what each opcode means and where it was seen), [font=monospace]docs/fight.md[/font] (a fight on the wire, opcode by opcode) and [font=monospace]docs/desofuscacion.md[/font] (surviving a patch).

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[size=5][b]💾 Database and persistence[/b][/size]

Three [b]SQLite[/b] databases in [font=monospace]bases/[/font], and one folder of text:

[list]
[*][b][font=monospace]world.db[/font][/b] — 41 tables and 659,397 rows: characters, inventories, positions, map persistence, spells, monsters, appearances, wardrobe and haven bags. Distributed compressed as [font=monospace]datos/world.zip[/font] (24.8 MB) and extracted on first run.
[*][b][font=monospace]auth.db[/font][/b] — accounts and authentication sessions, created on first run.
[*][b][font=monospace]paquetes.db[/font][/b] — the packets the server does not yet know how to answer, deduplicated by protobuf shape. Kept apart on purpose: it carries nothing needed to play, it can be deleted to start over, and it can be handed to somebody else to look at without handing over anybody's characters.
[*][b][font=monospace]content/[/font][/b] — the authored layer, in versioned JSON. The only one edited by hand, and the only one nothing regenerates. See Jondo Studio.
[/list]

Files are looked up in [font=monospace]datos/[/font], then [font=monospace]bases/[/font], then the root, so a half-moved installation still starts.

[b]Some regression guards also run at startup and throw[/b], so the server refuses to boot when the data it was shipped does not match what the code expects — see Tests for which checks live where, and why.

――――――――――――――――――――――――――――――――――――――――――――――――――――――――――――

[b]📷 Old screenshot gallery — being redistributed into the sections above[/b]

[img]https://github.com/user-attachments/assets/3b4f1f39-45d3-4efe-b73b-65d1d5e8a595[/img]
[img]https://github.com/user-attachments/assets/dde87296-dd2a-498a-b058-1491160b7d04[/img]
[img]https://github.com/user-attachments/assets/521bef24-6b19-4061-bc5b-37a178e91163[/img]
[img]https://github.com/user-attachments/assets/0f06761a-7dcf-481e-b045-02efce31c58e[/img]
[img]https://github.com/user-attachments/assets/60b113e4-3415-435f-8bc4-738e8efbfc2a[/img]
[img]https://github.com/user-attachments/assets/6faa6737-b04b-4cba-986f-3046ff2b4f2a[/img]
[img]https://github.com/user-attachments/assets/aa2249c3-699d-4137-aeef-96fc2278fcf2[/img]
[img]https://github.com/user-attachments/assets/33829fde-d8f1-4b5e-a3f1-11e34fd8c4ca[/img]
[img]https://github.com/user-attachments/assets/86a0b6e6-ea31-45a3-b381-4ba4fcc6b043[/img]
[img]https://github.com/user-attachments/assets/7c2aec0c-85a5-497b-9e1f-db4b77697605[/img]
[img]https://github.com/user-attachments/assets/cb587972-a7c5-42cd-a1e2-c1567cecccc8[/img]
[img]https://github.com/user-attachments/assets/00b35bbe-7356-41d0-ba9a-d079fbc7165f[/img]
[img]https://github.com/user-attachments/assets/cb75bca8-358d-4153-a2e6-955c10be92f9[/img]
[img]https://github.com/user-attachments/assets/38c437da-d881-4d64-b2b4-0348c789a9a3[/img]
[img]https://github.com/user-attachments/assets/95591e2a-f99d-4f66-b8f5-1f0c24ccf548[/img]
[img]https://github.com/user-attachments/assets/4d17a777-6839-4ed0-9aac-38768159e4ac[/img]
[img]https://github.com/user-attachments/assets/a22d551f-6dec-4147-b821-f6a8c5c7e721[/img]
[img]https://github.com/user-attachments/assets/82b10866-3f7f-4e79-83fb-f96331066fd7[/img]
[img]https://github.com/user-attachments/assets/bcbf1292-0474-4279-ab0d-9da0bf2b7ea4[/img]
[img]https://github.com/user-attachments/assets/c86bc15b-5bcd-4487-aa3d-391df8be93c0[/img]
[img]https://github.com/user-attachments/assets/dd60b531-4b3e-4347-a866-26ecb36046d4[/img]
[img]https://github.com/user-attachments/assets/7f0406c5-34c0-46b9-8cf7-fe14913f70e0[/img]
[img]https://github.com/user-attachments/assets/6b934f0d-40b7-4a3e-9926-5df97bf9c484[/img]
