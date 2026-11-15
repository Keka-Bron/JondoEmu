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
It folds with its «—» button; the Heroes icon beside it opens the window.

**Taking a hero in hand** (the panel's **PLAY**) is changing character: the mod goes through the
client's own main menu («change character», `MainMenu.OnCharacterSelection` and its confirmation,
kept as the menu comes up), the client leaves and comes back with the hero. A master leaving the
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

- Heroes on the map: they do not follow the master or show beside him.
- Joining a fight somebody else opened, a duel, the Koliseo, an aggression: only the master's own
  fights against monsters bring his heroes in.
- A defeat's consequences on a hero beyond the fight; the master walking out of a fight leaves his
  heroes in it with nobody to play them.
- Creating a character from the window.
- The group panel's «follow» and «show on the map» of Auralis's.
- PLAY needs the client's main menu to have come up once (Esc) since the game started.
- The spell book (the client's own) shows the master's spells, not the hero's.

The `.heros` command (administrators) still opens the probe's fight: JondoBots at one's side,
played as heroes, against as many JondoBots.
