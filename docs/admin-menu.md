# The admin menu of the client

Dofus 3 ships an administration menu of its own (`Core.UILogic.Admin.AdminMenu`). For a staff account
it is the first entry of the context menu of a character -- right click on one's own character or on
another player's -- and its entries run the **staff console's** commands (F11): the line travels in an
`mfb`, the server runs it as the chat's command without its dot (`StaffConsoleHandler` ->
`CommandHandler`) and answers with `mez` and `mew`. The menu is data, not code: a XML file, which
JondoFix writes for the client.

```
right click on Name -> Jondo - Admin -> Moderation -> Mute -> 30 minutes
                                          mfb { "mute Name 30m" }  ->  .mute Name 30m
```

## How it is switched on

| Piece | Where |
|---|---|
| The menu exists only if the connection server's identification (`IdentificationSuccess`) carries `AccountRights.ShowConsole` | `ConnectionBackend.Identify`: set for moderator and above |
| The client reads `<persistentDataPath>\Admin\menuadmin.xml` (`%USERPROFILE%\AppData\LocalLow\Ankama\Dofus\Admin\menuadmin.xml`) | `JondoFix/Admin/menuadmin.{fr,en,es}.xml`, embedded resources: the one of the language the client was started in (`--langCode`; English for any other) is written there at the client's start (`StaffConsole.InstallAdminMenuFile`) |
| The file is read once at the start of the game: "Recharger ce menu" (`loadxml`) reads it again | edit the file in `persistentDataPath` and click it to try a change without restarting |
| The client has no way to translate a menu: its overwrite files (`Admin/{rank}_{n}.xml`, read only when `ranks.xml` gives a rank) go by rank, not by language. Hence one file per language | `AdminMenuTests`: the translations have the French menu's items, types and commands |
| One client change: `#item` also opened the player's inventory, centered over the catalogue window | `StaffConsole.NoInventoryBehindTheSelector` |

How it was found out is in `bugs/menu-admin/RAPPORT.md` and `bugs/menu-admin/complet/RAPPORT.md`.

## What the client's XML supports (3.6.10.11)

Root element: anything. Children: `<item .../>`. Read from the client's own code (Cpp2IL dump of
`AdminMenu`, its items and `AdminSelectItemUI`) and tried in the game.

| `type` | What it does | Notes |
|---|---|---|
| `menu` | A submenu; its `<item>` children are the entries | Nested to any depth (three levels tried) |
| `static` | A title row, shown in capitals, no action | Takes the colors; `%p` is replaced |
| `separator` | A gap | |
| `sendcommand` | `command` goes out as the staff console's line | `delay` (ms) is read |
| `preparecommand` | Writes `command` in the console's input and leaves it there | The console must be open (F11) |
| `sendchat` | `command` goes out as a chat line on the general channel | A command needs its dot: `.version` |
| `batch` | Its `<item>` children run one after the other | `delay` (ms) on a child, `repeat` on the batch |
| `startup` | Like `batch`, run when the player enters the game | |
| `loadxml` | Reads the menu file again | |

Attributes: `label`, `help` (a tooltip), `command`, `delay`, `repeat`, and `rank` -- which is **not**
usable: an item whose `rank` is above the player's is hidden, and the player's rank is always 0 here
(`ranks.xml` is not read). Hiding by role is the server's job; the menu only labels what an
administrator alone can do ("(administrateur)").

**Not supported by the client** (Dofus 2 had them, some servers' menus still carry them, and they do
nothing or break): `clearConsole`, `openConsole`, `closeConsole`, `menuDebug`, `prepareChat`, `emote`,
`smiley`, `direction`. `AdminMenuTests` refuses any type outside the table above.

### Colors and text

`label` is drawn as Unity rich text: `<color=#ff7070>Danger</color>` works, and so do `<b>` and `<i>`
(in XML: `&lt;color=#ff7070&gt;...&lt;/color&gt;`). The `<font color='...'>` of Dofus 2 is printed
literally. The root entry's own label is drawn plain.

### Parameters

`%p` -- the name of the character that was clicked (one's own when one clicks oneself) -- in a
`command` and in a `label`. `%g` and `%a` are empty here, and `%c %n %m %d %h %t %s %v` of Dofus 2
are not replaced by this client.

### Selectors

`#item`, `#monster`, `#look`, `#fireworks`, `#spell` and `#npc` open `AdminSelectItemUI` -- the
client's catalogue window of that kind, with a category list and a search box -- and the token is
replaced by the id picked: `item #item %p` becomes `item 44 Name`. In the window **hover a row and
double click it** (a click only selects), the window stays open to pick again, and the answer comes
a moment later. What works:

| Selector | State |
|---|---|
| `#item` | Works: "OBJETS", the client's whole item catalogue with icons. The id only, no quantity. The client also opened the player's inventory on top of it -- which is why it once looked like "`#item` only opens the inventory" --; JondoFix shuts that inventory (a Harmony prefix on `OpenAdminItemSelection`) |
| `#npc` | Works: "PNJ", `name - id` |
| `#spell` | Opens and loads; no server command uses it yet |
| `#monster` | The window opens **empty**: the client's `ParseItems` throws "An item with the same key has already been added. Key: Minos" (two monster races of its data have that name). A bug of the client's own data: there is no fix on our side. The menu uses a monster id typed by hand |
| `#look`, `#fireworks` | Not tried; no server command takes a look or a firework |

## The commands

Written without the dot in the console and the menu, with it in the chat. The role is checked by the
server on every command (`CommandHandler`'s `HaceFalta` table; a command missing from it is an
administrator's), and the result is written to `ActivityJournal`. Replies are localised es/en/fr
(`CommandTexts`, see `command-localization.md`).

**A player's name goes first** in the commands about a player (`jail Name`, `mute Name 30m`) and
**last** in the ones that change a character (`kamas 1000000 Name`): the last word is the target
when it is a connected character and not a number or an option word (`max`, `random`, `all`...); an
`@` in front (`@Name`) forces it. Without a name the command is for the staff's own character. A
character in a fight is refused. The staff is told `Done on Name: ...`.

| Command | Role | What it does |
|---|---|---|
| `who` | moderator | The connected characters: level, coordinates, map |
| `whoami` | moderator | Own sheet: id, level, account, role, map, cell, kamas, language |
| `whois <Name>` | moderator | The same for a connected character, with jail / mute / fight flags |
| `where [Name]` | moderator | Map, coordinates, subarea and cell |
| `mapinfo` | moderator | The map's NPCs and monster groups with the ids `remove` takes, and the players on it |
| `version` | moderator | Version and players online |
| `teleport [x,y]` / `teleport <map id>` | moderator | As ever |
| `goto <Name>` | moderator | To that character's map and cell |
| `summon <Name>` | moderator | Brings him to one's cell |
| `jail <Name>` / `unjail <Name>` / `jailed` | moderator | Ten minutes in the GM prison (`Managers.Jail`); `unjail` also works offline; the list with minutes left |
| `kick <Name>` | moderator | Closes his connection (he saves and may come back) |
| `mute <Name> [time]` / `unmute <Name>` | moderator | No line in any chat channel; `30`, `30m`, `12h`, `7d`; ten minutes by default, three weeks at most; in memory (a restart lifts it) |
| `warn <Name> <text>` | moderator | A notice only he sees |
| `ban <Name> [time]` | **administrator** | Bans his ACCOUNT -- the game entry refuses its ticket -- and throws it out; no time is for good. Refuses one's own account and another administrator's. Kept in `Bans` of world.db |
| `unban <Name>` / `bans` | **administrator** | Lifts it by the name it was banned under; the list |
| `kamas`, `level`, `size`, `oficio`, `oficios` | game master | As ever, plus a target |
| `quest start\|finish\|forget <id> [Name]` | game master | Starts, closes whole or wipes out a quest (`QuestAdmin`, shared with the test harness's quest route) |
| `quests` | game master | The quests under way and how many are finished |
| `energy [value] [Name]` | game master | Energy, the maximum by default |
| `heal [Name]` | game master | All the life back |
| `stats vit\|sag\|for\|int\|cha\|agi <value> [Name]` | game master | A base characteristic (0 to 10,000,000) |
| `spawn npc <id>` / `spawn monster <id>[:grade] ...` | game master | On one's own cell, up to eight monsters (`MapAdmin`, as the F10 window) |
| `remove npc\|group <id>` | game master | Takes one off the map; ids from `mapinfo` (they are negative) |
| `gift <item> [qty] [Name]` | game master | A gift in the shop's gifts window of the account |
| `item`, `itemset`, `receta`, `relative`, `allycontrol` | administrator | As ever, `item`, `itemset` and `receta` with a target |
| `announce <text>` | **administrator** | A notice for everybody connected, in each one's language |
| `save` | administrator | Every connected character written to the database |
| `reload` | administrator | The content read again (`ContentReload`, shared with the harness's reload route) |
| `shutdown [minutes]` / `shutdown cancel` | **administrator** | Stops the server now, or after a countdown announced at the start and at the last minute |
| `help` | moderator | The console's own: every command |

The dangerous ones -- `ban`, `shutdown` -- are administrator's, and the menu puts them behind a
"Confirmer" submenu (`AdminMenuTests` checks it).

## The menu

The tree is `JondoFix/Admin/menuadmin.fr.xml` itself (the English and Spanish ones are its translations); its top level is `Jondo - Admin`:
Modération (movement, information, prison, mute, warn, kick, ban), Personnage (kamas, level,
characteristics, life and energy, size, jobs, items, quests, allied fighter control), Monde (teleport, monsters and
NPCs, quests for testing), Informations, Serveur (announcements, save, reload, stop) and "Recharger ce
menu". Commands that take a free value are `preparecommand`s: the console opens with the beginning
written, and the value (and a name, to act on another character) is typed after it.

## Tests

`Jondo.Unity.Tests/Commands/AdminCommandTests.cs` (target parsing, roles, durations, quest and monster
arguments, mutes) and `AdminMenuTests.cs`, which reads `menuadmin.xml` and checks that every
`command=` is a command of `CommandHandler`, every type is one the client parses, parameters and
selectors are the ones the client replaces, nothing uses `rank`, the dangerous commands sit behind a
"Confirmer" menu and the administrator's commands under an "(administrateur)" label. Run only these:

```sh
dotnet test Jondo.Unity.Tests --filter "FullyQualifiedName~AdminCommandTests|FullyQualifiedName~AdminMenuTests"
```

They touch no database.

## Adding an entry

1. The command must exist: add it to `Uso` (with its usage text in the three catalogues) and to
   `HaceFalta` in `CommandHandler.cs`, and implement it in `CommandHandler.Admin.cs`.
2. Add the `<item>` to `menuadmin.xml`; build JondoFix (`dotnet build JondoFix`); the client rewrites
   the file at its next start. `AdminMenuTests` fails if the command is unknown.
