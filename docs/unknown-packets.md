# The unknown-packet list

What the client sends us that no handler claims, written down instead of thrown away.

## Why

A packet with no handler used to do one of two things, both bad. Either it printed itself to the
console inside a banner of equals signs — and scrolled out of reach thirty seconds later — or it
landed in `GameNodeProxy`'s silence list: seventeen opcodes hardcoded so they would not flood the
log. The silenced ones are worse than the noisy ones, because they stop existing.

Both are now recorded, with the difference noted. "There is something I don't know about" becomes a
list you can work from: what is missing, how often it happens, and from where.

## The idea worth having: group by shape, not by opcode

One opcode can carry completely different payloads depending on what the player is doing. Counting
them together hides exactly the thing you need to see.

The signature walks the protobuf and writes down field number and wire type, descending into
submessages:

```
jjm  1:v,2:{1:v,3:s}     a number and a string
jjm  1:v,4:{2:v}         same opcode, a different thing entirely
```

`v` is a varint, `f` a fixed-width number, `s` bytes that are not a structure, `{…}` a submessage.

Measured over the 305 captures, across 29,991 client-to-server messages:

| | |
|---|---|
| distinct opcodes the client sends | 243 |
| opcodes carrying more than one shape | 46 |
| rows if grouped by opcode | 243 |
| rows if grouped by shape | **317** (+30 % detail) |

## The trap that nearly shipped

The first version produced **307 distinct "shapes" for `jrw` alone**, from 1,798 captured messages.
`jrw` is the walk packet: its field 2 is the movement path, a block of packed bytes. The submessage
detector was reading that block as a structure, and since the bytes differ with every step the
player takes, every step minted a new row. That is precisely the failure mode the design exists to
avoid — a list that has become a log.

The fix is a ceiling on the field number, and the ceiling is measured, not guessed. The complete
3.6.10.10 protocol extracted from the client declares **8,972 fields, and the highest is 40**;
median 2, 99th percentile 19. `EsSubmensaje` rejects any candidate holding a field above **64**,
which fits every real message with room for whatever Ankama adds next.

`jrw` went from 307 shapes to 12.

Both halves are pinned by `AssertPacketShapesAreTelling` in `RegressionGuardTests`: that different
payloads give different signatures, that identical shapes with different *values* give the same
one, and that a byte blob comes back as `:s` rather than a fabricated structure.

## What it does not do

It does not decode anything, and it must not.

A packet on this list is not permission to invent a reply. Without a capture of the real server
saying what it answers, answering anything is worse than answering nothing: the client walks away
holding a state the server does not have, and nothing reports an error. The list says **where to
look**. What gets looked at is measured against a capture like everything else here.

## Using it

`.packets [n]` in the chat, administrator only, most frequent first:

```
17 forma(s) de 11 opcode(s): 4 sin atender, 13 silenciada(s), 0 ilegible(s)
kmv x412 (silenciado, f2, 6 B) 1:v,2:{1:v}
hnn x88  (silenciado, f2, 3 B) 1:v
jjm x12  (sin atender, f2, 41 B) 1:v,2:{1:v,3:s}
```

Storage is `bases/paquetes.db`, deliberately separate from `world.db` and `auth.db`: it holds
nothing needed to play, it can be deleted to start over, and it can be copied to another machine
for analysis without carrying anyone's characters along. It is reloaded at startup, so an
afternoon of play is not lost to a restart.

Writes are throttled — first sighting, then every hundredth — because a silenced packet can arrive
a hundred times a minute and what matters is that the shape *exists* in the list, not the exact
count. Memory is capped at 1,000 shapes, about ten times what the captures suggest is needed; if
that ceiling is ever reached, something is minting junk signatures and the fix is to repair it, not
to let it eat the server.

## Provenance

The idea came from reading another emulator's implementation. The code here is our own, written
against our dispatcher and our conventions, and the shape-signature scheme and its field-number
ceiling are ours — the ceiling in particular exists because measuring our own captures showed the
naive version failing.
