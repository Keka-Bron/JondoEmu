# Fights (3.6.10.10)

What the wire actually carries during a fight, measured from the 15 captures in
`Wireshark captures from real game/Combate`. Nothing here is guessed from the
older protocol: every shape below was read out of the bytes.

Read this together with [protocol.md](protocol.md) for the framing and the `Any`
envelope, and [opcodes.md](opcodes.md) for the wider opcode census.

## The state of the emulator's fight code

`Handlers/FightHandler.cs` was written against 3.6.4.3. Of the 48 opcodes it
mentions, **7 still exist in 3.6.10.10** (`hoy`, `jwe`, `jxw`, `jya`, `jyg`,
`jyj`, `krp`) and 41 never appear once in any combat capture:

```
bvr igs irm joh joi joo joq jox jpf jtx jub juc jud jut juu jvm jvn jwb jwf
jwk jwl jwm jwo jwu jxe jxx jyf jyi jyk jyn jys jyz jza kkm kkp kkq kkr kkz
krh lor lsy
```

Meanwhile 271 opcodes that do appear in those captures are not mentioned by the
code at all. So the fight is not a matter of patching a few messages: the whole
message layer has to be rebuilt.

The good news is that only the message layer is wrong. The state machine in
`FightHandler` — fight instances, teams, placement, the turn timer, spell
casting, the monster turn, loot and the end of the fight — is a reasonable
skeleton to keep. The work is to replace what it puts on the wire.

Regenerate the census at any time with:

```bash
py tools/censo_combate.py
```

## Reading a capture in order

Both directions are separate TCP streams, and `pcap.streams()` returns one
timestamp per stream, so it cannot tell you what answered what. In a fight the
order *is* the information, so use `tools/hilo.py` instead: it reassembles each
direction while remembering when each piece of the stream arrived, stamps every
frame with the segment where it *ends*, and merges both directions by clock.

```python
import hilo
hilo.resumen(capture)          # the whole thread, in order
hilo.ver(capture, "kba", 2)    # the first two kba, as a field tree
```

## Volume, and what that tells you

Across the 15 captures, by direction:

| Server | count | Client | count |
|---|---|---|---|
| `jtn` | 6,945 | `jti` | 315 |
| `jwe` | 3,644 | `kqo` | 238 |
| `jxm` | 2,483 | `jwz` | 114 |
| `jto` | 2,229 | `jrw` | 95 |
| `jwi` | 2,229 | `jwh` | 89 |
| `jxw` | 1,555 | `ieo` | 35 |
| `jya` | 1,377 | `jxy` | 31 |

`jto` and `jwi` appear exactly the same number of times, in every capture. They
are a matched pair that brackets everything else — a sequence opening and
closing — and `jtn`, `jwe` and `jxm` are what happens inside.

## Preparation

The sequence below is frames 10–72 of *combate contra poutch nivel 50…*, and the
same shape appears in *hablar con poutch ingball…* and the rest.

The client asks for the fight by sending `hqa { f1: contextual id of the monster
group }` — the same negative id the group carries in `jss`. The server answers
`jsq`, empty, and then runs an ordinary map change (`kub`, `jru`, `lva`) onto the
fight map. Then:

```
S→C  jxg   one per fighter: where it stands, its sheet and its look
S→C  kba   the placement cells, blue and red
S→C  jzu   who is on each team
S→C  jwq   empty
S→C  jrk   { f2: 10, f3: empty, f4: map id }
C→S  jzy   { f1: fighter, f2: cell }        the player picks a starting cell
S→C  kmk   { f2 (repeated) { f1: cell, f2: orientation, f3: fighter } }
C→S  kaq   { f1: 1 }                        the ready button
S→C  kah   { f1: fighter, f3: 1 }           ready, acknowledged
S→C  jyy   the spell bar to fight with
```

Between the placement cells and the ready button the server sends **no timer at
all** — only `kqo`/`kqy` heartbeats. The placement countdown is the client's own.
The server's only job is to start the fight when it runs out.

### `kba` — the blue and red cells

```
f1 { f1: packed cells of team 0
     f2: packed cells of team 1 }
```

Measured: 16 cells per team.

```
f1 = [298, 368, 411, 413, 373, 317, 273, 271, 285, 288, 302, 312, 382, 386, 397, 400]
f2 = [284, 381, 425, 428, 387, 303, 260, 257, 270, 274, 289, 297, 396, 401, 410, 414]
```

### `jzu` — the teams

```
f2 (repeated, one per team) { f3 { f2: fighter id } }
```

During preparation the enemy side carries `-1` rather than a real id: the
monster group has not been split into individual fighters yet. The player's own
side already carries the character's contextual id.

### `jxg` — a fighter

The envelope is the same one the map uses for an actor in `jss`, which is why
the client can draw a fighter with the code it already has:

```
f1 { f1: cell, f2: orientation, f4: 0 }
f2 { f2: the sheet, f3: the look }
f3: fighter id
```

The sheet (`f2.f2`) is a long list of `f5 { f1: characteristic, f2: value }`
entries — 1, 23, 27, 28, 33, 34, 35, 36, 37, 54, 55, 56, 57, 58, 85, 87, 101 and
on, most of them empty during preparation and a run of them at 100.

### `kmk` — who stands where

```
f2 (repeated) { f1: cell, f2: orientation, f3: fighter id }
```

A cell being vacated is sent with fighter `-1`. Moving during placement
therefore travels as two entries in one message: the old cell freed and the new
one taken.

### `jzy` and `kaq` — the two things the client asks for

`jzy { f1: fighter, f2: cell }` is the player dragging themselves onto another
blue cell. `kaq { f1: 1 }` is the ready button; the server answers `kah`.

### `jyy` — the spell bar

```
f3: fighter id
f4: fighter id  (the same one; they have never been seen differing)
f6 (repeated) { f1: grade, f3: spell id, f4: 1 }
```

### `jxc` — not the turn order

`jxc { f1 (repeated) { f1: id, f2: 1 }, f4: fighter id }` carries a mix of spell
ids (12736, which also appears in `jyy`) and small numbers (370, 373), so it is
*not* the initiative list, whatever it is. The turn order has not been located
yet.

## Coming into somebody else's fight

Measured in `Combate/meterse en combate de otra persona haciendo click en la espadita...` (a
click on the swords), `Combate/entrar a combate con listo automatico y entrada automatica
siguiendo a lider de grupo...` (a party member pulled in behind his leader),
`Busqueda grupo/busqueda automatica de grupo...` (four players into one dungeon fight) and
`Mazmorras/mazmorra de los jalatós completa` (a refused one). Builders in
`Network/FightJoinProtocol.cs`, logic in `Handlers/FightJoin.cs` and `FightInstance.JoinTeam`.

What the map sees when somebody attacks a group (follow capture 131-141):

```
S→C  kmu { f2: the group }  kmu { f2: the attacker }     both go off the map (no jsd)
S→C  hpy { f1: flags [attacker cell, group cell], f2: 4, team, team, options x2, f6: fight }
S→C  jqz { f2: fights on this map }
S→C  kae { team 0 with its people }  kae { team 1, empty }  kae { team 1 +1 monster } x N
```

A jss of a map with fights carries each one in placement as an f12 (the hpy's body) and is
followed by a jqz. When the placement ends the swords go with `hpr { f1: fight }`; the jqz drops
when the fight is over.

Coming in: `C→S kay { f1: a fighter of the side, f2: fight }`, or nothing at all when the server
pulls a party member in (he has the automatic entry on, stands on the map, and his leader opens
the fight; if he is walking, when his walk ends). Then `kml kmp(1) jru lqu kuq lva` to him, and to
the whole fight `kmk {his enemies, him}`, `kae {his side}`, `kxa kwk`, `jxg {him}`, `jzu`. His
board is the ordinary one, with the kam naming who opened the fight and the kaa what is LEFT of
the placement (442 at 0.7 s). Every board carries one kae: the receiver's own side, empty.

In a dungeon every arrival rebuilds the monster side (`kar`, `kmu`, `jzw` per monster gone; `kmk`
x2, `jxg`, `jzu` per monster come) to the first clamp(people, 4, 8) of the room's eight.

The party window's two switches are server-side, each answered on root 3 with an empty message:
`ilf → ikm` / `int → ilv` automatic entry on / off, `ikr → inn` / `inp → ilr` automatic ready
on / off (which pair is which is read off the clock of the capture). With the automatic ready on,
a member is ready when his leader presses ready: both kah leave together.

Leaving from the placement (`kme`): `jxa`, then `kml kmp ktz jru lqu` as at a fight's end.

Refusals: only `jxs { f1, f2: 16 }` is measured, for a side restricted to its party. The other
reasons (started, full) are not answered at all rather than invented.

## What the builders reproduce

`Network/FightProtocol.cs` builds `kba`, `jzu`, `jrk`, `kmk` and `kah`, and reads
`hqa`, `jzy` and `kaq`. `ConnectionProtocolSelfTest` compares each of those
against the exact bytes the real server sent in *combate contra poutch nivel
50…* — same cells, same fighter, same map — and it runs at startup, so a builder
that drifts fails there instead of in the client.

Still unwired: `FightHandler` has not been changed over to these yet, so the
preparation does not run end to end in game.

## Still to measure

Everything past the start of the fight proper. In particular:

- `jto` / `jwi`, the sequence brackets, and what the codes in their fields mean
  (8, 1 and 3 have been seen).
- `jtn`, by far the most frequent message, and why so many of them are empty.
- `jwe`, `jxm`, `jxw` and `jya`: the effects, the damage and the point
  variations.
- `jti` and `jwh`, which is what the client sends to cast a spell and to move.
- `jxy`, empty, which is almost certainly "pass turn".
- The turn timers, the end of the fight and the reward panel.

## What a row of `EffectsJson` means

Measured on the class captures (Yopuka, Ocra, Tymador) and the client's own
metadata (`Core.DataCenter.Metadata.Effect.EffectInstanceFlags`).

- `m_flags`: 1 visible in the tooltip, 2 in the buff panel, 4 in the fight
  log, 8 on the terrain, **16 for the client only**. A row with the 16 is the
  sheet's copy of what the spell does through a sub-cast, and the real server
  never sends it: Furor's "+20" (the real one is 28604's), Vitalidad's "+N%"
  (25215's), Manticolmillo's "+15 huida" (24012's), Virtud's shield and "-50"
  (29723's), Remisión's push under DM (13430's), Ojo por Ojo's "+6" (no row at
  all in three casts), the water bomb's "-2 PA" (25589's, by combo). The
  engine drops them when it reads the spell (`SpellEffect.ForClientOnly`).
- `random` and `group`: one draw per spell level. The shares of the random
  rows of a level add up to 100 in 1,602 of the 1,603 levels that carry any;
  the draw picks one row and every row of its `group` comes with it; group 0
  is no group. Bumerán Pérfido: eight rows of 12.5 in four groups, a life
  steal and the characteristic of the same element each.
- `maxStack` of the level: how many equivalent rows (same entry, same spell,
  same caster) live together on a target. `-1` no limit (Fervor cast twice in
  a turn keeps both shields, Tumulto one "+20" per enemy), `0`/`1` the new row
  replaces the old — dropped as `jya` + `jwe 514` before the new `jxm`, never
  refreshed under its number —, `N` a cap the oldest gives way to. Presión and
  Espada Destructora are 2.
- Targets are picked before anything moves: Fricción pulls the enemy and its
  state still lands on him, cast at the cell he left.
- A hooked row with a `delay` fires that many rounds after the cast: Furor's
  "1160 under TE" goes out with the round after the cast in its `f12`.
- Rows read by the blow, registered at the cast with their kind as a
  condition: `-N de daños recibidos` (105, 265) and `daños sufridos x#1%`
  (1163) under `D`, `DR`, `DM`/`DCAC`, `DTB`/`DTE`.
- Mask letters, by side (lower case the caster's, upper case the other): `c`
  the caster when he stands in the zone (Acumulación's "en el lanzador",
  Flecha Asaltante's `950 mask c` on the Ocra one cell from the centre),
  `l`/`L` the players (Caja de Herramientas, Ghulificación), `m`/`M` the
  monsters that are nobody's summon, `i`/`I` and `j`/`J` the summons (Látigo's
  "si es una invocación aliada" is a bare `i`; what tells `j` from `i` is not
  written anywhere). `H` the enemy characters, as `L`; `D`/`d` the companions,
  which this build does not field, so they name nobody.
- Rows read by the blow, too: `Intercepta los daños` (765) and `Comparte los
  daños` (1061), "jxm 765 'D'" and "jxm 1061 'D'" with no dice and family 7, one
  per bearer, in the name of the one who cast them (Sacrifice frame 62, Égida
  1500 laid by the shield on its Feca, DonNaturel 334, Harmonie 182-185). What a
  blow does with them is in no capture.
- A trigger in a `jxm` goes out bare: `EON`, `EOFF`, `EK` -- the state or the
  mask behind it stays in the data, in all 581 of them.
- The hooks a class spell lays go out as rows of their own, one per trigger
  (Resonancia's "jxm 1160 'D'" and "'XD'" on its target, frames 195-196; Lazo
  Espiritual's six on the Osamodas), and come off with a `jya` when a `406` takes
  the spell. The emulator does not send them yet.
- Zone letters measured on impacts with known positions: `Q` is a straight
  cross like `X` with `param2` as inner radius (Palabra Turbulenta Q1 pushes
  the four cells around, Llave de Contacto "en una cruz de 1 casilla"); `T` is
  the bar across the cast, the centre and `param1` cells to each side
  perpendicular to the nearest of the eight directions from the caster (seven
  impacts: Cencerro, Magmacha Calcinada, Flecha de Pelea ×2, Impacto
  Aplastante, Espora Dyka ×2, none behind or in front); `O` is the ring.
- Spell states: the client's `SpellStateData` flags 104 of 6,375 states —
  `invulnerable`, `invulnerableMelee`/`Range`, `cantBeMoved`, `cantBePushed`,
  `incurable`, `cantDealDamage`, `preventsSpellCast`... — in
  `datos/spell_states.json`. A blow on an invulnerable target goes out as
  `jwe <effect> f40{f2: victim, f4: element}`, no amount (Influencia's
  capture), and nothing of the blow happens. One more is read off its effects:
  678, "Teleportal Imposible", the only state with effect 33, keeps its bearer
  out of the portals.
- A `406` is announced after the rows it takes: `jwe 406 f33{f2: spell, f4:
  from whom}` behind their `jya`, and `f5=1` when the 406's own row is visible
  in the fight log (`m_flags` 4): 17 of the 17 such removals, none of the 1,397
  others.
- A chained cast's `jwe 300` has no `f8`, the mark of a cast somebody made --
  18,526 chained casts without it, 3,171 of 3,171 cast from a `jwh` with it --
  and names the cell it was aimed at, where its caster stood even if a row of the
  same cast has moved him since (Estela's 31021 on 371, frame 10).
- Ouginak Rage is resolved by chained spells after the parent spell's damage.
  The struck target remains a routing candidate for the Rage manager even when
  that damage killed it; Molosse, Carroña and the other Rage-generating spells
  therefore grant Rage on lethal hits too. Other effects still ignore dead fighters.
  Its third point schedules the beast-form cleanup for the following round: the
  form stays active through the Ouginak's next turn and ends with that turn.
- `Devuelve N PA` (120): the AP sheet in its short sequence, then `jwe 120
  f20{f1: N, f2: who}` in the caster's name, 117 times in 35 captures.
- A cast on an empty cell names nobody: no `f2` in its `jwe 300`.
- A critical cast runs the whole chain on the critical lists. A chained spell
  with a critical list of its own uses it and its rows go out with `f9=1` and
  the critical entry's uid (Virtud's shield: `1040 dice 1100 uid 383796`,
  29723's critical row, next to the ordinary `1000 uid 383778`); one without
  runs its ordinary list and its rows go out unflagged (Tumulto's 13154). A
  waiting row keeps the flag and hands it to the live row it turns into. The
  chained cast's own `jwe 300` carries no `f5`.
- What a turn trigger fires goes inside one action sequence of the bearer's,
  after his `jyt`: `jyt -6, jto{-6,3}, jwe 300 13155, ..., jya 67, jwi`
  (Sentencia). Sent bare, the client applies none of it.
- `+N% vitalidad` (1078) and `-N%` (1033) are of the maximum life, base and
  gear included: the naked level-200 test characters of the captures stand at
  1,150 and get +230 and -575. Announced as the flat rows 125 and 153.

## Portals

Measured on the six Selatrop captures; the rules are `World/Fights/Portals.cs`,
the frames `Handlers/FightPortals.cs`.

- Laid (1181): `jwe 401 f32{f1{f1{f2: 255, f3: cell}, f2: the row's diceSide
  (44338, Teleportal's level), f3: its diceNum (2), f4: number, f5: 3, f6: grade,
  f9: the spell, f10: cell, f11: 1 when on, f12: owner}}` -- `f5` is the kind of
  mark, 2 on a bomb wall, none on a glyph. Four a Selatrop: the fifth takes the
  oldest off first (`jwe 310`). Laid off, it is followed by its own `jwe 1181
  f17{f1: number}`; every other portal it turns on or off by its own.
- On and off (`jwe 1181 f17{f1: number, f2: 1 when on}`): a portal is on when it
  can be used and another of its owner's can too; it cannot when it was crossed
  this turn, when a 1183 switched it off (until that caster's next turn), or when
  somebody stands on it. The one laid under its own owner counts as free as it is
  laid (Estela's goes down on and off right after). The turn start brings back
  what it can, in a `jto 2` of the one beginning, the 1181s in his name.
- Crossed, walking (or pushed) onto one that is on: inside the walk's sequence,
  after its 129, `jwe 307` on the portal entered, `1181` off for it and for the
  way out, `jwe 4` in the walker's name, `jwe 307` on the way out; the walk ends
  there. A Teleportal (1182) is the same without the first 307. The chain goes
  from each portal to the nearest usable one not yet in it, the newest between
  two as near; the last one is the way out, and only it and the first go off.
- Cast at a portal that is on, a spell -- not one about portals: Neutral aimed at
  one switches it off -- lands at the way out plus the vector from the caster to
  the portal, and its `jwe 300` names the chain in its spell's `f1`. The
  Selatrop's passive then goes off (PST).
- Not measured: what a spell through portals gains -- the effect's text, "+#3%
  daños, +#1% por casilla que separe entre 2 portales", is applied as a final
  percent on its damage and healing.
