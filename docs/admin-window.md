# Admin window (F10)

The administration panel inside the game, opened with **F10** by an administrator's character.

## What it needs

- **An administrator account** (see `docs/role.md`). The test account is one.
- **The game started from the Jondo launcher.**
- The server on the same machine, or on another one opened with `JONDO_PUBLIC_BIND=1`.

## The window

- Resizable, see-through while the pointer is elsewhere.
- Wherever a character is chosen, the connected characters are in a drop-down.
- The line at the bottom of each tab says what the server answered.

## The right-click menu and the staff console

- Right-click a character with a staff account for the client's own admin menu, in French, English or
  Spanish: go to them, bring them, jail, mute, warn, kick, ban, and the game master's tools.
- **F11** opens the client's staff console for moderators and above.
- The server checks every command against the account's role, and staff only act on lower roles.

## Items

- **Search** the client's catalogue by name, type, id or level range; filter by category and type.
- **Give · MAX stats** or **Give · RANDOM stats**, to yourself or any connected character, up to 100
  at a time. `.item <id> [quantity] random` does the same from the chat.
- **Gift**: the item waits on that player's account, in the shop's gifts window, for any of its
  characters to take.

## Character

- **Level**, the six base characteristics and **kamas**, for yourself or any connected character.
- **Give mount**.

## Teleport

- **Find a place** by name, coordinates or map id, then **Teleport**.
- **Go to them** and **Bring here**.

## Spawn

- **NPCs** and **groups of monsters** (up to eight, any grades) on your own cell.
- **On this map** lists every NPC and monster group on your map, each with **Remove**.

## Jail

Ten minutes in the game's own GM prison.

- **Jail** a player: they cannot leave, use commands or speak but in private and on the general
  channel.
- **In jail** lists the prisoners with their time left; **Release** lets one out early. The time keeps
  running offline and across restarts.
- **Go to the jail** takes you to the corridor.

## How it works

JondoFix builds the window at runtime out of the client's own interface components. Every button is
a request to the server's control API, signed with the account's launcher token and checked against
the administrator role. The routes are in **[`docs/live-character-admin.md`](docs/live-character-admin.md)**.
