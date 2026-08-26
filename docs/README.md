# Jondo documentation

Server emulator for Dofus **3.6.10.10**, C# on .NET 10. This folder explains how the client talks
to the server, what each thing that travels on the wire means, and where every piece of data comes
from.

Nothing here is guesswork. The protocol was measured against **242 `.pcapng` captures** of real
sessions (246 MB, sorted into 31 topic folders), and the numbers come from `datos/`, from
`bases/world.db` and from the client dump. Where something could not be checked, it says so.

Every count in these documents is tied to that 242-capture set, as it stood on **15 August 2026**.
The collection keeps growing, so a fresh sweep will not match to the last digit — a new capture can
add an opcode nobody had seen. Treat the counts as measurements of a snapshot, not as constants.
What does not drift is the shape of things: which field carries what, and why.

The captures are **not** in the repository. They carry real account names, so `.gitignore` keeps
`*.pcapng` out. The documents quote what was measured from them, never their contents.

---

## The documents

**`protocol.md`** — How a message travels.
The TCP framing on port 5555: the varint length prefix, the protobuf `Any` envelope carrying
`type.ankama.com/` plus a three-letter opcode, and the three root fields (server push, client
request, server answer). It also covers the two details that break implementations: the client
always sends request id `-1`, so answers pair up by order and not by id, and it drops any message
over 131,072 bytes — that limit is the client's own log line, not a guess.
*Go there* when you have to read or write a frame by hand, or when the client closes the connection
without saying why.

**`opcodes.md`** — What those three letters mean.
The opcode table, built by crossing two sources: the **234** opcodes the code names and the **669**
seen in the captures. **128** are in both lists — what is actually implemented. **541** appear only
in captures: what the game uses and the emulator does not answer yet. **106** appear only in the
code: every one of them sits inside a full `type.ankama.com/` URI, so they are not noise from the
scan — they are messages the emulator builds that none of the 242 captures triggered. Some may be
left over from version 3.6.4.3; that is not confirmed.
*Go there* when an unknown opcode shows up in a dump, or before implementing something, to see
whether the message is already identified.

**`appearances.md`** — What the character looks like.
Dofus does not ship the item-to-look table in its client data: the server sends it. So the looks
were measured one garment at a time in the appearance window — **2,371** of the **2,420** in the
catalogue (`datos/cosmetics.json`, 12 types). The document explains where each type plugs in — a
skin on the character for hats, capes and shields, a sub-entity for pets, the root of the look for
mounts and petmounts — and why appearance weapons carry no look but do occupy a slot.
*Go there* when a garment does not show, shows in the wrong colour, or pushes another one out.

**`world.md`** — The map and what lives on it.
The **15,360** maps in `world.db`, the walkable-cell data covering **17,211** map ids, the
neighbours, the **62** zaaps, the **187** dungeons with their **763** rooms, the **5,134** monsters
and how they are spread over **562** sub-areas.
*Go there* when something in the world is not where it should be: a map that will not load, a
monster inside a wall, a zaap that leads nowhere.

**`interactives.md`** — Clickable map elements and their actions.
How the generic `(map, element, skill instance)` registry declares elements in `jss`, resolves
client `iwo` requests and dispatches zaaps, haven-bag chests and the lottery without sharing player
state between sockets. It also gives the extension path for doors, buildings, workshops, crafting
stations and resources.
*Go there* when an element has no cursor or action, when an `iwo` is rejected, or before adding a
new interactive category.

**`sessions.md`** — Multiple connected accounts and map broadcasts.
Why the former static player state could only support one client, how `GameSession`, `SessionState`,
`SessionContext` and the concurrent `SessionRegistry` divide ownership now, how a socket is bound to
an account and character, and how targeted sends and map broadcasts stay frame-safe.
*Go there* when adding a handler that reads player state, when sending an event to nearby players,
or when debugging cross-account state leaks.

**`launcher.md`** — Native launcher and teams of up to eight accounts.
How profiles and selections are persisted, how one process is launched per selected account, how
`InstanceId`, launch hash, Zaap `gameSession`, game token and single-use ticket preserve identity,
and where the independent process and socket limits are enforced.
*Go there* when changing the team UI, the launch arguments, account-token resolution or the path
from a launcher row to its game socket.

**`remote-server.md`** — Running the server on another machine.
How `JONDO_PUBLIC_BIND` opens all server services consistently, how the launcher's managed loopback
relay bridges JondoFix to the configured host, which ports are involved and what the relay does not
provide in terms of transport security.
*Go there* when moving the server to a LAN host or VPS.

**`data.md`** — Where each number comes from.
The **27** files in `datos/` (22 json, 4 bin and `world.zip`), the **31** tables in
`bases/world.db`, and which of the **22** Python scripts in `tools/` builds each one. Most come out
of `dofus3_data/`; a few — the item-to-look table among them — were measured off the captures
instead. Three generators that `Paths.cs` still names by hand (`extract_fight_cells.py`,
`extract_character_xp.py`, `extraer_world.py`) are no longer in `tools/`.
*Go there* when you need to regenerate a data file, or when you want to know which source a value
came from before trusting it.

**`item-commands.md`** — Administrator item creation commands.
The exact syntax and behavior of `.item` and `.itemset`, including role checks, template and set
data sources, maximum factory effects, persistence, inventory updates and partial-set failures.
*Go there* when giving an item by template id, creating a complete set or diagnosing a rejected id.

**`live-character-admin.md`** — Live character administration over the local control API.
The authenticated `POST /api/personaje` route, its administrator-role check, accepted base-stat and
kamas fields, session serialization, persistence, immediate client refresh and error responses.
*Go there* when building an administration tool that must update an online character without a
restart or reconnect.
**`command-localization.md`** — Spanish, English and French command replies.
How the launch language crosses the single-use session ticket, why it belongs to `SessionState`,
which replies are translated and how catalogue completeness is guarded at startup.
*Go there* when adding a command response or another server-side player message.

**`unknown-packets.md`** — What the client sends that no handler claims.
Why it is grouped by message shape rather than by opcode, the measured field-number ceiling that
keeps a byte blob from posing as a structure, and the `.packets` command.
*Go there* before hunting for a missing feature: the list says where to look.

**`jondo-coin.md`** — The server's own currency.
Which Ankama item it reuses and why, how much each monster drops, how the client is made to call it
"Jondo Coin", and how to point an NPC shop at it so it charges coins instead of kamas.
*Go there* when changing the drop rate, adding a shop that charges in coins, or renaming an item.

**`role.md`** — The account-role scale and its migration.
The corrected Giny-compatible roles 1 through 5, why the former 1-to-4 definition was ambiguous,
how old administrators are migrated exactly once, and the current command permission thresholds.
*Go there* when adding a protected command, changing an account role or investigating access.

**`NOTAS_MIGRACION_AUTH.md`** — The jump from 3.6.4.3 to 3.6.10.10. **Written in Spanish** (1,030
lines); the rest of this folder is in English.
The full sequence from client start to walking on a map, message by message, with what changed in
this version: server selection, character list, character creation, world entry, map actors,
characteristics, chat, spells and equipment. It also tracks what is done and what is missing.
*Go there* when something fails before you reach the world, or when you want to know which exact
message changed since the previous version.

---

## Contributing

### Where things live

| What | Where |
|---|---|
| The whole server: proxies, handlers, managers, database and launcher UI | `Jondo.Unity.Launcher/` (78 C# files, ~28,200 lines) |
| Message handlers, one per subject | `Jondo.Unity.Launcher/Handlers/` (21 files) |
| In-memory tables: cosmetics, mounts, spells, dungeons, mobs… | `Jondo.Unity.Launcher/Managers/` (21 files) |
| Envelope, framing and the per-port servers | `Jondo.Unity.Launcher/Network/` and `Zaap/ZaapService.cs`; `Program.cs` starts five listeners: 5555 game, 5556 game node, 6337 chat, 8888 HAAPI, 15881 Zaap |
| The generated protobuf message classes | `Jondo.Unity.Protocol/Messages/Protocol.proto` |
| The opcodes themselves, as three-letter literals | `Network/ConnectionProtocol.cs`, `Network/WorldEntry.cs`, `Network/GameNodeProxy.cs` and the handlers; the console labels are in `Protocol/NetworkMessage.cs` |
| Fight state, monster AI, damage and isometric geometry | `Jondo.Unity.World/Fights/`, `Jondo.Unity.World/Maps/MapGeometry.cs`; the packet side of a fight is `Handlers/FightHandler.cs` |
| File lookup order: `datos/`, then `bases/`, then the root | `Jondo.Unity.Launcher/Paths.cs` |
| The MelonLoader mod that redirects the client | `JondoFix/` |

`Jondo.Unity.Auth` (1 file), `Jondo.Unity.Core` (3 files, 132 lines) and `Jondo.Unity.Parser`
(1 file, 82 lines) are near-empty shells. Authentication and HAAPI live in
`Jondo.Unity.Launcher/Network/`, not in the project that carries their name. Do not waste time
looking there.

Two more files are dead and misleading: nothing references `Jondo.Unity.Protocol/OpcodeRegistry.cs`
or `TypeRegistry.cs`, and `OpcodeRegistry` maps `kub` to a fight placement message while the code
that runs sends `kub` for characteristics. There is no central opcode table in the emulator.

### Checking things yourself

Python is invoked as **`py`**, never as `python`.

- `py tools/pcap.py <capture.pcapng>` reassembles the TCP stream and prints the opcode timeline;
  add `--raw <opcode>` for a hex dump of those messages.
- `py tools/cliente_falso.py` talks to the emulator without opening the game.

`tools/`, `bases/` and `dofus3_data/` are **not** in the repository. None of them is needed to run
the emulator: `datos/` already holds everything it reads, `bases/world.db` is unpacked from
`datos/world.zip` on first run (`bases/auth.db` the emulator creates itself), and `tools/` only
regenerates `datos/` and reads the captures. The emulator never calls any of it.

### When writing documentation

It has to be checkable. Every figure must come from the code, from `datos/`, from `world.db` or
from a capture. If you cannot verify it, either leave it out or mark it clearly as unconfirmed.
A short, correct table beats a long, guessed one — and never invent the meaning of an opcode.

Write in English, and explain *why* a decision was made, not only what it does.

### Real account data: never

The captures come from real accounts. **Do not write any character name, nickname, account label or
chat text into the documentation**, yours or anyone else's. Numbers — item ids, map ids, opcodes —
are fine; they identify nobody.

The same rule applies to the repository. `.gitignore` already excludes `logs/`, `*.log`, `*.csv`,
`*.pcapng` and `*.hex` under a comment that is exactly this blunt:

> `# Nunca. Llevan nombres de cuentas reales.` — *Never. They carry real account names.*

`bases/` and `dofus3_data/` are excluded too. If you add a new tool or a new dump, check first that
it does not drag anything from an account with it.
