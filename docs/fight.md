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
