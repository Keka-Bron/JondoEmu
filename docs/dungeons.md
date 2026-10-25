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

**Moving on.** Winning a fight in a room moves to the next one. In the last one, to the exit,
except for Kardorim's post-boss room described below.

**Rooms after the boss (`content/world/dungeon_exits.json`).** A dungeon can leave its
winners in a room of its own before the way out: one row per dungeon names the boss room,
the post-boss room and its cell, the NPC there and his replies that leave. Kardorim's is
the only one so far.

**Kardorim (23 October 2026).** Victory in his boss room `152834048` loads the post-boss
room `152835072` on cell **382**, before the fight's return-map message is sent. This
room is not added to `DungeonRooms`: it has no fight group and is not another boss room.
The catalogue's exterior exit stays **`153881600`**. Talking to Kardorim (NPC **2936**)
there and choosing **"Quitter la crypte de Kardorim"** closes the conversation, then
loads that exterior map. All four exit reply ids declared by his template are handled:
`24994`, `24997`, `25000`, `25001` (client text ids `555709`–`555712`). His other quest
and Milimilou replies keep their existing behaviour. The map panel's leave option also
recognises this post-boss room. Other dungeons retain their existing victory routes
and default arrival cell. `KardorimExitTests` checks the victory's map/cell and load
message, the NPC's exit replies, the panel exit and unchanged routes.

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

Not missing any more:
- the dungeon challenges. The 684 flagged `solo_mazmorra` all name a monster, and the boss room imposes
  those of the monsters in front (`ChallengeHandler.ImposeAsync`), bonus at zero, as the anomaly's
  capture shows;
- the guardian's replies: only the key's and the keyring's let one in (`DungeonHandler.DoorReplies`,
  `DungeonDoor`), any other answer leaves the player outside;
- the group search: the announcements and the automatic matchmaking of the dungeons' lobby.

- **The internal passages**, in part. The 2.73 world graph's cell passages no route covered -- 510 stairs,
  holes and doorways drawn into dungeons' and caves' maps -- are floor passages since 22/10
  (`datos/floor_passages_worldgraph_2.73.json`, tools/extraer_pasos_de_casilla.py), with an approximate
  arrival cell and their criterion. Doors the graph does not have are still missing.
- **The waves** of some new dungeons (Despedazadora, Venerable, Bzupervibzor) and the hunting altar: the
  client has their arrival (`jyb`, done for the dreams) but not which monsters come in which wave.
