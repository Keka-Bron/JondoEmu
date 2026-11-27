# The hero mode

The account's other characters connected beside the one in the world -- the master -- fighting at
his side and played by hand from his client in their turns. Ankama's Dofus 3 has none yet (« rien en
2026 »); Wakfu's heroes and Dofus Touch's « Duo de choc » (1.72) are the model.

Install the [Heroes server package](server-mod-migration.md#heroes), add `heroes` to
`ServerMods` and restart. In the package's settings, `Enabled` defaults to true,
`MaxHeroes` defaults to 7, and `ReloadMapOnSwitch` defaults to false. The window has
eight places including the master. The server advertises the package's client DLL
while the package is active.

## How a hero exists

`Heroes.Server/HeroGroups.cs` owns the active group. The host loads each follower's sheet, bag
and gear into a socketless session through the companion session service. The package
holds that session and the host uses it for the follower's character state. Only client
connections are in the host's session registry.

- **Connect**: a character of the master's account, not the master, not played from a client of its
  own, not somebody's hero already, under the limit, and the master not in a fight.
- **Disconnect**: saved as he stands; not while he is in a fight.
- The master leaving the world takes his heroes with him, each saved (`SessionTeardown`).
- A hero chosen from a client of his own stops being a hero (`CharacterSelectionHandler`).

## On the map, and in the world for the others

`HeroGroups.World.cs` manages following and uses the host's companion presence service to
update map actors. A connected hero stands on a free walkable cell beside his master, where
everyone nearby can see him. every map's actor list carries the heroes of the
people on it (`ConnectionProtocol.BuildMapActors`), a hero connecting is drawn (jsn) and one let go
taken off (kmu). He walks after his master -- the master's own walk, the first hero stopping one step
short of its end, the next two -- and goes with him from one map to the next
(`SessionRegistry.AnunciarMudanzaAsync`). In a fight he is off the map, and back beside his master
when it is over. A map he reaches beside his master is his too: the zone explored for his
achievements, and its zaap discovered (`ZaapDiscoveries.Discover`), with no need to play him there.

For his guild and his alliance he is in the world (`SessionRegistry.IsOnline`): their member lists
show him connected, and they are told as he connects or is let go. His friends see his account
playing, as they see any character of it.

## In the game's party

The package supplies party behavior through the host's party hooks. A connected hero is a member
of his master's party, drawn in the game's own party window: the party the master is in, or a new one he leads (ing to
him, ink to the others). A hero never leads -- the lead passes to somebody played, never to a hero,
and a party of heroes alone breaks up --, and nobody sends a hero anything: he has no client.

The heroes go wherever their master's party goes. Invited while in a party of his heroes alone, the
master is free to answer: that party is let go, and his heroes come back into whichever he ends in
(accepting, turning down, the invitation withdrawn). Leaving a party, or put out of it, he takes his
heroes into one of their own around him. The leader putting out a hero of his own lets that hero go.
Taking a hero in hand leaves the party as it is: the same characters stay in it --
let go and taken back without it being told (held by the Heroes package) --, the one played takes the lead
the other had, and his client is given the party again (`PartyHandler.SwitchedAsync`).

The package moves socketless members through the host’s companion presence service.

## Making a hero

The window's empty places, as many as the account has character slots left, each offer to make a
hero. `heroes/create` makes him by the creation screen's own rules (`CharacterCreationHandler.Make`:
the name's rule, a name nobody has, a slot left) with the name, the breed, the sex, the head and the
colours chosen -- the breed's for any not sent --, and connects him as a hero at once when there is
room; `heroes/name` draws a name nobody has.

The screen is the game's own (`HeroesMod`'s «The game's own CharacterCreation, whole»): its template
(`Assets/UI/Auth/characterCreation.uxml`) cloned into a window over the world and the game's
`CharacterCreation` class made on it, so the breeds, the roles, the heads, the colours, the attitudes
and the spells are all the client's. Its twelve services are found breadth first through the fields
the services keep one another in, from the displaying one taken off a displayer on screen. Three
things of it are turned: `CreateCharacter` asks `heroes/create` with the name, the breed, the sex,
the head and the six colours the screen holds; `RequestNameGeneration` asks `heroes/name`; and the
popup it opens with -- «this modifies your character for good», the world's way into it -- is not
shown. When the class cannot be made the mod binds the template by hand (breeds, sex, heads, name),
and when the template cannot be had it shows a form of its own.

The window's portraits and the group panel's are the client's own `EntityDisplayer`s, alive: seated
(`AnimEmoteSit_Statique`) for a character not in the world. `UserData\HeroesTuning.cfg` («name =
value» a line) is read again whenever it changes, for sizes to be set with the game running.

The slots per account are a setting (`ServerSettings.CharacterSlots`, 8 by default: a whole group
of heroes with its master; the real server's is five); the server list announces them. It is set in
`config\server_settings.json`: the settings window is full at its smallest size.

## The mod's keys

Ctrl+Tab opens and closes the window. Tab alone takes the next of the group in hand, in the order
the account's characters come, passing over whoever fights; not while a text field has the keyboard.

## In a fight

`Heroes.Server/HeroGroups.Fights.cs`. When the master opens a fight against monsters, each of his heroes is
built as a player is -- inside his own session, so `BuildPlayerFighter` reads him -- and put on his
side, ready. He is a *played ally* (`Fighter.IsPlayedAlly`, `DrivenBy` = the master), as Barnabé is:

- his bar goes to the master's client at the start: his own layout, his class's spells and the
  weapon, with the master in the `jyy`'s f4, where a summon's bar carries its owner;
- his turn's `jyj` goes to the master's client, and the master's `jrw`, `jwh` and `jxy` then move,
  cast and pass for him -- the `jwh` names no caster, the fight's turn does.

His bar is his own layout packed: the weapon on slot 0, his spells after it with no gaps, then
what he knows and did not place -- the master's client does not turn the pages of a bar that is not
its character's. A hero's summons are the master's to play as well: their bar, their turn and their
clock go to the master's client (`FightHandler.PlayerOf`); with nobody found to play one, its turn
had no clock and never ended.

The 3.6.10.11 client does all of it with no patch: its `CurrentPlayedFighterService` and the slave
fight panels are what play a controlled summon. Measured on 2026-11-15 with the `.heros` probe: two
heroes moved, cast their class spells and passed their turns from the master's client.

His share of a won fight is planned with his master's (`PlanRewards`, paid in his own session) and
shown in the master's end screen. Experience, levels, kamas and items go to the participating
character, who is saved where the master stands.

## The Heroes mod

`Heroes/` contains the MelonLoader mod that the launcher loads from the active package's client
manifest. **Ctrl+Tab**, or the mod's button at the end of the MENUS bar (`gd_btnUis`), opens a window of eight cards: the master (gold, a crown), his heroes (a light on), the
account's other characters to connect, empty places. Portraits are drawn by the server from the
client's own bones, as the launcher draws its team (`Jondo.Unity.Sprites`), in the background as soon
as the list is asked for, and the mod asks for it all along, so they are there at the first opening.

While a hero is connected, a **group panel** stands at the right of the screen: each member's
portrait and life (his fighter's in a fight, his resting life outside one), a crown for the master,
and switches for passing a hero's turns automatically, sharing quest progress, and choosing who
receives the group's loot. Everyone keeps their own experience and kamas.
It folds with its «—» button; the Heroes icon beside it opens the window. Every switch says what it
does under the pointer, in the game's language, and a portrait shows what a group's tooltip shows of
a member: class, level, life, AP, MP, initiative, prospecting and wisdom.

**Share quest progress** is the checklist button on each connected hero. It starts eligible
quests and advances matching objectives when the active character talks to an NPC, uses a
quest object, reads a quest book the hero carries too or the notices of the map, hands his key
to a dungeon's guardian, discovers a location, or wins a fight the hero joined. At such a door a
hero whose quest wants the key goes in on a key -- his, else the group's -- rather than by his
keyring. Each hero uses their own quest log, prerequisites and inventory, and receives
their own quest rewards. Actions such as crafting and paid replies still require playing the
character; a reply that only moves the master moves his heroes with him, and is shared.
The switch starts **on** (Keka-Bron/JondoEmu#206: a forgotten switch left heroes behind unseen);
turning it off for a hero stays with the character across hero swaps until the server restarts.

When the master moves a quest and a hero who shares quests does not follow, the master is told at
once, in a warning in his language: «Greensback n'a pas suivi ta quête : il lui manque 2 des objets
demandés». Once a quest, not again at each step after. The reason is the first that applies:

| Reason | When |
| --- | --- |
| Not on your map / in a fight | The hero was elsewhere, or a fight was on |
| Not among the winners of that fight | A quest monster killed in a fight the hero did not win with him |
| Items missing | The NPC or element takes items the hero lacks: which, and how many |
| Step not shared | The reply takes items or kamas, opens a dungeon's door, or changes the master's body on the way (a plain passage or teleport is shared: the heroes go with him) |
| Conditions not met | The hero cannot start the quest: his level, a quest before it |
| Quest not started / not on the same step | He was behind already |

The hero's quests button then stays **red**; under the pointer it names each quest and why, with the
client's own quest and item names. It turns back by itself once the hero has caught up -- by doing the
step himself -- and is cleared when his sharing is turned off. `heroes/list` carries it per hero as
`questLag: [{ quest, step, reason, item, count }]` (`HeroGroups.LagsOf`).

Each connected hero's card in the window has three more glyphs -- a chest, three bars, a star --
that open his bag, his characteristics and his spells, to look at (`POST /api/heroes/sheet { id }`):
ids from the server, named and drawn by the client's own data (`DataCenterModule`, its icon tiles).

**Taking a hero in hand** (the panel's **PLAY**, or Tab) happens at once, in the same connection:
`POST /api/heroes/switch { id }` (Heroes package). The master is saved and taken off his map, the hero
selected into the same session where the master stood, and the client sent, mid-session, the burst
a selection sends (the «character selected» with the hero's id, his sheet, bag, spells and
shortcuts); it takes a second selection while in the world. The map is not loaded again: the actor
played before is taken off for his own client too and the one played now drawn where he stood
(the package setting `ReloadMapOnSwitch` brings the whole map back). Only the two of them change
hands: the other heroes stay as they stand, on the map and with their switches, the new one's heroes
from then on (`HeroGroups.HandOver`), and the one played before comes back as a hero at his place in
the group's round, so that Tab goes round them all.

`{ id, later: 1 }` asks for it at the client's next character list instead (`HeroGroups.AskSwitch`):
the server picks the hero there itself, the way it picks a character still in a fight (the list with
its kvd, the client's kwb, `GameEntryService.ResumeAsync`). A master leaving the
world leaves his group aside for his account for three minutes (`HeroGroups.DisconnectAll(session)`);
whoever of it comes back into the world takes it back (`HeroGroups.Regroup`, on entry): the one who led
it is a hero of his, the others are his heroes again with their switches. Quitting and coming back
within the three minutes brings the group back the same way.

| Route | Answers |
|:---|:---|
| `POST /api/heroes/list` | the account's characters: master, hero, busy, in a fight; the limit and the count |
| `POST /api/heroes/connect` `{ id }` | `ok`, or why not: `off`, `self`, `account`, `online`, `already`, `full`, `fight`, `missing` |
| `POST /api/heroes/disconnect` `{ id }` | `none` when he is not one or is fighting |
| `POST /api/heroes/portrait` `{ id }` | `{ png }` in base64, or 404 when it cannot be drawn |
| `POST /api/heroes/autopass` `{ id, on }` | a hero's turns passed on their own, or not |
| `POST /api/heroes/loot` `{ id }` | the group's items to him; the same one again, back to each his own |

Every route requires the session's own token (`401 sesion`). Name suggestions, previews and owned
portraits can be requested without a character in the world. The other routes require an active
character (`409 world`). Disabling the package removes the routes (`404 ruta`) after restart.

## Not yet

- A switch to stop the heroes following, or to hide them from the map.
- Joining a fight somebody else opened, a duel, the Koliseo, an aggression: only the master's own
  fights against monsters bring his heroes in.
- A defeat's consequences on a hero beyond the fight; the master walking out of a fight leaves his
  heroes in it with nobody to play them.
- The group panel's own «follow» and «show on the map» switches.
- The spell book (the client's own) shows the master's spells, not the hero's.

The core `.allycontrol` command opens a developer test fight in which an administrator controls allied JondoBots.
