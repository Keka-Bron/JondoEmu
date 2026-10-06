# Dungeons

Going in, moving from room to room, killing the boss and leaving. Measured on
`Mazmorras\mazmorra de los jalatós completa`, which is someone walking the Corte del Jalató Real
from end to end, and on the client dump.

---

## 1. What Ankama does

The capture shows dungeon 1 whole. Eleven maps and five fights, with a pattern that repeats
exactly five times:

```
120063489   entrance, with the guardian (NPC 173, «Rotabla, el pastor»)
121373185   room 0   ┐
121373190   corridor ┘ fight
121374209   room 1   ┐
121374214   corridor ┘ fight
121375233   room 2   ...
121373187   room 3
121374211   room 4   ← last one: the boss
121374216   corridor
121375235   corridor
120063489   back at the entrance, which in this one is also the exit
```

**And the order of `DungeonRooms` is the real order.** That had been in doubt for years —
`DungeonManager` itself says so: the Biblioteca del Maestro Cuerbok lists its rooms at x = -14, -13, -15,
which is not a progression— and now there is a dungeon to check it against. The five rooms of the
capture come out in the order the table gives. One out of 187, so the doubt still stands for the other
186; but it is no longer only an assumption.

### The door

```
C->S  iov  {1:3, 2:120063489, 3:actor}     clicks the guardian, «talk» action
S->C  ioc                                  the dialog opens
S->C  ios  {1: 646, ...}                   the guardian complains about his jalatós
C->S  ioy                                  the player answers
S->C  ios  {1: 17040, ...}                 «¿Seguro que quieres utilizar el manojo de llaves
                                            para entrar?»
C->S  ioy                                  answers yes
S->C  kld                                  the dialog closes
S->C  iun                                  the key is spent
S->C  jru  {2: 121373185}                  inside, room 0
```

Line 17040 is not declared by any NPC: the server puts it there, just like the quest ones.

---

## 2. The missing data

`tools/extract_dungeons.py` **threw away half of what was needed**. The client dump carried
`availableOnKeyring`, `requiredObjects`, `achievements`,
`availableInAutomaticGroupSearch` and `availableInLobby` from the start, and the extractor kept eight fields and
discarded the rest. That is why there was no way to lock a dungeon with a key: the data existed and did not
get through.

With the extractor extended, out of the 187:

| | |
|---|---|
| ask for a key | **126** |
| also accept the keyring | **107** |
| declare a boss | **126** |
| distinct items used as keys | 129 |

Number 1 asks for item **1568, «Llave de la Corte del Jalató Real»**, and the keyring is **10207,
«Manojo de llaves»**.

---

## 3. What this server does

`DungeonManager` existed —226 lines— and **nobody called it**; its own comment said so.
It is now wired in.

**Going in.** Talking to an NPC standing on a dungeon's entrance map and answering it:
it checks the minimum level, looks for the key in the bag —its own key first, the keyring after—, spends it
and teleports to the first room. 53 of the 187 entrances already have a guardian placed, and
the names give it away: «Guawdia wabbit», «Guardián koalak», «Discípulo de Ugah».

**Moving on.** Winning a fight in a room moves to the next one. In the last one, to the exit.

**The groups.** Each room has ONE group of eight, built at startup from the monsters belonging
to the room's subarea (`Subareas.Monsters`, without the dungeon's bosses), and the fight takes the
first `clamp(players, 4, 8)`: four for one to four players, and one more for each player
from the fifth on. That is what the capture `Mazmorras/mazmorra de los jalatós completa` shows: the `jss`
of each room carries the group of eight and its alternatives by number of players (1 → 4, 5 → 5 … 8 → 8),
each one the first N of the eight, and the sacrier who went in alone fought four in all five
rooms. The first four are four different species; the monsters of room k are at grade k
(1 to 5 in that dungeon; in the others, spread linearly across the grades). The map sends the
group with its alternatives, identical byte for byte to those in the capture (`DungeonGroupSizeTests`).

**The boss.** In the last room of the 126 dungeons with a declared boss, the boss leads the group, a
single time and at its highest grade, with seven of the dungeon's monsters behind it.

**On winning**, the room is rebuilt the same way —its boss included— instead of being repopulated at random.

---

## 4. How it differs from Ankama, and why

**There are no corridors and no walking between rooms: winning teleports you.** It is not a design
choice, it is the only thing the topology allows: **none of the 187 dungeons has a single one of
its internal passages**, neither in the extracted table nor in Ankama's own world graph. A
player put in room 0 would have no way out.

**Any fight won in a room moves on.** Each room has a single group, so winning it is
clearing it.

**Any answer to the guardian lets you in**, because the dialog tree of those NPCs is not written.
The confirmation line exists and is theirs; putting it in is the editor's job.

**The group grows during placement**, as in the game. Each time someone joins the fight of
a room, the monsters' side is rebuilt whole with the first `clamp(players, 4, 8)` of its
eight and with new ids, even if the number does not change: that is what `Busqueda grupo/busqueda
automatica de grupo...` shows, four players joining fight 471 one by one, with -1..-4 removed
(jzw) and -5..-8 added (kae) on the second arrival, and so on up to -13..-16. See
`Handlers/FightJoin.cs` and `docs/fight.md`.

---

## 5. A bug there was and no longer is

`MobSpawnManager` started **before** `DungeonManager`, and it reads `DungeonRooms` so as not to empty the
dungeons with the «no monsters indoors» veto —753 of the 763 rooms are flagged that way—.
Since `DungeonManager` is the one that writes that table, what the spawner read was **what the previous
startup left written**. In a stable world it does not show; the day the list of rooms changes, it does.
They now go in the right order.

---

## 6. What is missing

- **The internal passages.** That is what separates this from the real dungeon. They are ~1,800 doors and the
  passage editor already knows how to place them; what there is not is somewhere to take them from automatically.
- **The guardian's dialog tree**, with the confirmation and a «no, thanks» that does not let you in.
- **The waves** of some new dungeons (Despedazadora, Venerable, Bzupervibzor) and the hunting altar.
- **Dungeon challenges**: 684 of the 842 are flagged `solo_mazmorra` and are never offered,
  because nothing tells the fight it is inside one. `DungeonHandler.IsBossRoom` and
  `DungeonManager.OfRoom` now do know it.
- **Automatic matchmaking** and the lobby. There is nothing, and the data already carries the flags.
