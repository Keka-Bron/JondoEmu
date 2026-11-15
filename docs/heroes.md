# The hero mode

The account's other characters connected beside the one in the world -- the master -- fighting at
his side and played by hand from his client in their turns. Ankama's Dofus 3 has none yet (« rien en
2026 »); Wakfu's heroes and Dofus Touch's « Duo de choc » (1.72) are the model.

Server setting: *Game modes → Hero mode*, on by default, and *Heroes at once* (3 by default, 7 at
most: the window has eight places with the master).

## How a hero exists

`Managers/Heroes.cs`. A hero has no client: his character is loaded into a session with no socket
(`GameSession.SinSocket`, `CharacterSelectionHandler.LoadForHero`) -- his sheet, bag and gear, as a
selection loads them. That session is in no registry: nothing is sent to it, no map draws it, and a
fight's people (`Publico`) leave it out. Everything written against a session's state reads and
writes the hero's when run inside it.

- **Connect**: a character of the master's account, not the master, not played from a client of its
  own, not somebody's hero already, under the limit, and the master not in a fight.
- **Disconnect**: saved as he stands; not while he is in a fight.
- The master leaving the world takes his heroes with him, each saved (`SessionTeardown`).
- A hero chosen from a client of his own stops being a hero (`CharacterSelectionHandler`).

## On the map, and in the world for the others

`Network/HeroPresence.cs`. A connected hero stands on his master's map, on a free walkable cell
beside him, and is drawn for everybody there: every map's actor list carries the heroes of the
people on it (`ConnectionProtocol.BuildMapActors`), a hero connecting is drawn (jsn) and one let go
taken off (kmu). He walks after his master -- the master's own walk, the first hero stopping one step
short of its end, the next two -- and goes with him from one map to the next
(`SessionRegistry.AnunciarMudanzaAsync`). In a fight he is off the map, and back beside his master
when it is over.

For his guild and his alliance he is in the world (`SessionRegistry.IsOnline`): their member lists
show him connected, and they are told as he connects or is let go. His friends see his account
playing, as they see any character of it.

## In the game's party

`Handlers/PartyHandler.cs`, its Heroes part. A connected hero is a member of his master's party,
drawn in the game's own party window: the party the master is in, or a new one he leads (ing to
him, ink to the others). A hero never leads -- the lead passes to somebody played, never to a hero,
and a party of heroes alone breaks up --, and nobody sends a hero anything: he has no client.

The heroes go wherever their master's party goes. Invited while in a party of his heroes alone, the
master is free to answer: that party is let go, and his heroes come back into whichever he ends in
(accepting, turning down, the invitation withdrawn). Leaving a party, or put out of it, he takes his
heroes into one of their own around him. The leader putting out a hero of his own lets that hero go.
Taking a hero in hand leaves the party as it is: the same characters stay in it --
let go and taken back without it being told (`Heroes.HoldParty`) --, the one played takes the lead
the other had, and his client is given the party again (`PartyHandler.SwitchedAsync`).

The game's own «follow the leader» cannot move a hero (no client to walk): `HeroPresence` does.

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

`Handlers/FightHeroes.cs`. When the master opens a fight against monsters, each of his heroes is
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
shown in the master's end screen; then it goes into his own sheet and bag -- experience and levels,
kamas, items -- and he is saved where his master stands (`HeroesFightOver`).

## The Heroes mod

`Heroes/`, a MelonLoader mod built like Cazacabezas, which the launcher puts in every client's
`Mods/`. **Ctrl+Tab**, or its button at the end of the MENUS bar (`gd_btnUis`, an icon the mod
draws), opens a window of eight cards: the master (gold, a crown), his heroes (a light on), the
account's other characters to connect, empty places. Portraits are drawn by the server from the
client's own bones, as the launcher draws its team (`Jondo.Unity.Sprites`), in the background as soon
as the list is asked for, and the mod asks for it all along, so they are there at the first opening.

While a hero is connected, a **group panel** stands at the right of the screen: each member's
portrait and life (his fighter's in a fight, his resting life outside one), a crown for the master,
and two switches -- **AUTO**, a hero's turns passed by the server as they come, and **LOOT**, the
group's items all to that one (each keeps his experience and kamas; `FightHandler.GatherHeroLoot`).
It folds with its «—» button; the Heroes icon beside it opens the window. Every switch says what it
does under the pointer, in the game's language, and a portrait shows what a group's tooltip shows of
a member: class, level, life, AP, MP, initiative, prospecting and wisdom.

Each connected hero's card in the window has three more glyphs -- a chest, three bars, a star --
that open his bag, his characteristics and his spells, to look at (`POST /api/heroes/sheet { id }`):
ids from the server, named and drawn by the client's own data (`DataCenterModule`, its icon tiles).

**Taking a hero in hand** (the panel's **PLAY**, or Tab) happens at once, in the same connection:
`POST /api/heroes/switch { id }` (`HeroSwitch`). The master is saved and taken off his map, the hero
selected into the same session where the master stood, and the client sent, mid-session, the burst
a selection sends (the «character selected» with the hero's id, his sheet, bag, spells and
shortcuts); it takes a second selection while in the world. The map is not loaded again: the actor
played before is taken off for his own client too and the one played now drawn where he stood
(`ServerSettings.HeroSwitchReloadsMap` brings the whole map back). Only the two of them change
hands: the other heroes stay as they stand, on the map and with their switches, the new one's heroes
from then on (`Heroes.HandOver`), and the one played before comes back as a hero at his place in
the group's round, so that Tab goes round them all.

`{ id, later: 1 }` asks for it at the client's next character list instead (`Heroes.AskSwitch`):
the server picks the hero there itself, the way it picks a character still in a fight (the list with
its kvd, the client's kwb, `GameEntryService.ResumeAsync`). A master leaving the
world leaves his group aside for his account for three minutes (`Heroes.DisconnectAll(session)`);
whoever of it comes back into the world takes it back (`Heroes.Regroup`, on entry): the one who led
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

All need the master in the world (`409 world`), with the session's own token.

## Not yet

- A switch to stop the heroes following, or to hide them from the map.
- Joining a fight somebody else opened, a duel, the Koliseo, an aggression: only the master's own
  fights against monsters bring his heroes in.
- A defeat's consequences on a hero beyond the fight; the master walking out of a fight leaves his
  heroes in it with nobody to play them.
- The group panel's own «follow» and «show on the map» switches.
- The spell book (the client's own) shows the master's spells, not the hero's.

The `.heros` command (administrators) still opens the probe's fight: JondoBots at one's side,
played as heroes, against as many JondoBots.
