# Migration 3.6.10.10 — authentication, servers and characters

Everything here comes from the real captures in
`Wireshark captures from real game/Autenticacion-Servidor-Personaje/`.

The character and account names that appear in those captures are not written here nor in
the code: only the structure matters.

---

## Decoding tools

They are in `tools/`. They filter by ports 5555/5556, which is where the clear traffic goes;
the rest of the capture is HAAPI/Zaap over TLS and cannot be read.

```
py tools/pcap.py <capture.pcapng>                 -> each flow's messages, in order
py tools/pcap.py <capture.pcapng> kvi kra --raw   -> dumps those messages whole
py tools/timeline.py <capture.pcapng>             -> both directions interleaved, with
                                                     the clock in milliseconds from the start
```

`timeline.py` is the one needed to know **what the server answers to what**: `pcap.py`
reassembles each direction separately and the order between them is lost there.

### Testing without opening the game

`tools/cliente_falso.py` speaks the real protocol against the already started emulator: it
authenticates, asks for a ticket, picks a character, enters the world and sends heartbeats. It prints what
the server answers to each thing.

```
py tools/cliente_falso.py
```

It uses the `GameToken` of whatever account is in `auth.db`.

Besides printing, it checks: that the `kub` carries each characteristic in its container, that the
spells are those of the character's class, that `jqi` is answered with a `jsq` **in root field 3**,
that `jqk` starts the map change sequence, that spending points and equipping
answer what they should, and that the database ends up as it should.

It touches the database —it spends points, resets the sheet, changes map—, so **it saves the
character on starting and gives it back as it was on finishing**, whatever happens.

### The client writes its own log

`%LOCALAPPDATA%Low\Ankama\Dofus\Player.log`. Unity leaves it there, outside the game's folder, and
it is the client's diary: when one of our messages blows it up, it notes the exception **with the
message's name**.

```
NullReferenceException
  at giq.bkjt (llp a)      <- a characteristic entry
  at ees.wuc (kub a)       <- processing our kub
```

It is the only source that says what is happening inside the client. After each protocol
change, look there:

```
grep -E "^NullReferenceException|at [a-z]+\.[a-z]+ \([a-z]{3} a\)" Player.log
```

Of the six exceptions from the 14/08 session, four are solved: two from `kub` (wrong
containers) and two from `jhh`/`jhk` (they talked about a guild whose message we did not send). Still pending is
the `jss` one, which is really `MapInfoUI.SetInfoFromSubarea` — the map name widget — and
which does carry the unobfuscated names.

### Taking data out of the client

Two extractors, both with UnityPy:

```
py tools/extract_breed_stats.py       -> breed_stats.json       how much raising each
                                                                characteristic costs in each class
py tools/extract_characteristics.py   -> characteristics.json   what each characteristic id is,
                                                                with its Spanish name
```

The second crosses each characteristic's `nameId` with `world.db`'s `Translations` table, which
has 339,175 entries. It is what has to be looked at before assuming what an id is for.

---

## The complete sequence

```
   ── connection to the connection server (5555), messages without envelope ──
C->S  126 B   f1 { f1: language, f3 { f1: token, f3 { f1: install id }, f5: "3.6.10.10" } }
S->C  727 B   authentication accepted + server list + characters per server
C->S   10 B   f1 { f1: language, f4 { f1: server id } }
S->C   86 B   f2 { f1: language, f4 { f1 { f1: ticket, f2: host, f3: ports } } }

   ── the client closes and opens a new connection, now with envelope ──
C->S  kqz     f2: ticket, f3: language
C->S  krt     empty
S->C  kra lqu hoy kqu mgq mgt hpd krs      (a single burst)
S->C  mgz kqp kqp kqp
S->C  kvi     CHARACTER LIST
S->C  jtg     shop articles catalogue; not needed for the list

   ── the player picks a character, over the SAME connection ──
C->S  kvw     f1: character id
S->C  kqp kub jbf kuf ipc kva ...  and the entry into the world
```

**The root field matters.** In 3.6.10.10 the messages the server pushes go in field 1
of the frame: `f1 { f1 { f1: type_url, f2: payload } }`. Checked in the world entry
capture: 390 of 391 server messages use field 1. The client uses field 2. The
`NetworkEnvelope.BuildGameNodePacket` the emulator carries over wraps in field 3, which is
what the previous version of the protocol did: **the world path is still pending migration**.

---

## Structures

### Authentication accepted

```
f2 { f1: language
     f3 { f1 { f1: account id
               f2: nickname
               f3: tag
               f4 { f1 (repeated): server
                    f2 (repeated): { f1: category, f2: slots } }   7 entries, categories 0..6, 5 slots
               f5: end of subscription
               f6: {} } } }
```

And each server:

```
f1 { f1 { f1: id, f3: category }
     f3 (repeated) { f1: name, f2: breed-1, f3: sex, f4: level, f5: last connection } }
```

Watch out for two details that only show when comparing messages:

- Here the **breed is zero-based** (one less than in the rest of the protocol).
- The **category** groups the servers; it is not the status. In the capture the servers in the
  29x range are category 1, the 35x ones are 2 and 3, and the two old ones are 4 and 5. The
  server's colour on the selection screen does not travel through here, but through HTTP.

### Character list (kvi)

```
f1 (repeated) { f1 { f2: name
                     f3: level
                     f4 { f2: { f3: sex }   present and EMPTY if sex is 0
                          f6: look
                          f7: breed } }
                f2: character id }
```

The level can go past 200: those are the Omega levels.

### Look

The same block in kvi, in kva and in the world messages:

```
f1 : indexed colours, packed varints, each one (index << 24) | rgb
f2 : 3          constant in every sample
f3 : bonesId    1 on a normal character; another value if wearing a disguise
f5 : scales, packed
f6 : skins, packed
f7 : subentities (mount, pet), with the same nested shape
```

The base values come from the client itself: `tools/extract_breed_looks.py` reads the breeds
bundle and generates `breed_looks.json` with bonesId, skins, scales and the six default colours of
each breed and sex. Checked against the character creation capture: male breed 11 gives
skin 110 and scale 55, and that is exactly what the real server returned.

**Pending**: the equipped items' skins. The look sent now is the breed's base one,
so an equipped hat or shield is not seen. `ItemTemplates.Data` has an
`appearanceId` field, but only mounts and pets carry it, not the equipment pieces. There are
captures of the appearances and cosmetics interface not yet analysed, and there are dofusdude's
JSON files for 3.6.10.10; it should come from there.

### Going back

Both ways of going back are the same work for the server:

```
C->S  kqq   empty
S->C  kqr   f1: session id, f4: 1
   ── the client closes the connection and redoes the greeting from the start ──
```

Whether it stays on the character list or on the server list is the client's decision.

### Character creation

```
C->S  kwd   empty                  asks for a suggested name
S->C  kvk   f1: suggested name
C->S  kvz   f1 { f1: name, f2: cosmetic, f3: chosen colours (-1 = default),
                 f5: ?, f7: breed }
S->C  kvb   empty
S->C  kqp x3, kvi (now with the new character), kvl { f1: 1, f2: id }
```

Not implemented yet.

---

## What is already done

- `Network/Pb.cs` — protobuf writer that respects the field order, which is needed
  because these messages repeat the same field number.
- `Network/ConnectionProtocol.cs` — all the builders of this phase.
- `Network/ConnectionProtocolSelfTest.cs` — checks at startup that the messages come out with the
  captured shape. The sizes of the burst's thirteen frames match byte for byte those
  of the capture.
- `Network/SessionRegistry.cs` — single-use tickets that tie the second connection to an
  account and a server.
- `Managers/BreedLookTable.cs` — base look per breed and sex.
- `Servers` table and `ServerId` and `LastConnection` columns in `Characters`.

## What is left

1. Character creation (success, and failure by limit).
2. The equipped items' skins in the look.
3. Migrating the world path: root field 1 instead of 3, new opcodes (`ktw` becomes `kva`,
   `kkr` becomes `jru`) and the prerecorded frames of `BasePayloads`, which are from the previous version.
4. `Jondo.Unity.Protocol/OpcodeRegistry.cs` exists but nobody uses it; the Launcher still has
   some 145 hand-written opcodes, almost all from the previous version.


---

## Entering the world (3.6.10.10)

**It works.** The client loads the map, the HUD, the chat and the minimap.

### How

The real messages from the capture are replayed, extracted with `extraer_world.py` into three
files (`world_etapa*.bin`). They do not go back to back: the real server sends a block, waits for
the client's confirmation and carries on.

```
client kvw  ->   block 1: character, characteristics, quests...  (330 messages)
                 block 2: the four big catalogues                (4 messages)
client kqo  ->   block 3: the map                                (38 messages), ONLY once
client kmv+jrh -> jss (actors) and lva
```

The `lqc` does arrive, but late: the client sends it on finishing digesting block 1, when
ours has already released block 2. That is why blocks 1 and 2 go back to back and the `lqc` is ignored.

### `kqo` is a heartbeat, not a request

This cost an afternoon. The client sends `kqo` **every five seconds while in the world**,
and the real server answers it with **a single message**, `kqy` (`0801`), and nothing else. In the tutorial
capture there are twenty-four in a row, exactly 5,000 ms apart.

The emulator answered each heartbeat with the whole map block. Since that block carries
`jru`, and `jru` means "load this map", the client redid the world load every five
seconds: loading screen, map, HUD, everything again. In a five-minute session twenty
rounds were seen.

The map block now goes out on the entry's first `kqo` and from then on the heartbeat is
answered only with `kqy`. The block already starts with a `kqy` of its own, so the first one is not
answered twice either.

### `lva` closes the actor list

Behind `jss` there is always `lva`, an empty message meaning "there are no more actors". It is
in the four movement captures, in the world entry and in the tutorial, always attached.
The emulator did not send it: it sent `jss` and went quiet. The client waited about two seconds,
asked again with `knm`, `kno` and `kny`, and did not consider the map loaded.

### Map actors (`jss`)

```
f2: map id
f5 (repeated)  an actor
   f1 { f1: cell, f2: direction }
   f2 { f1 { ...what it is... }
        f3 { f1: colours, f2: 3, f3: bonesId, f5: scales, f6: skins } }   the look
   f3: contextual id   (negative for monsters and NPCs)
```

What is inside `f2.f1` says what it is: `f5` player, `f7` NPC, `f4` monster group.
**All three carry their look in `f2.f3`**, the group included.

The group is **one** message, not one per monster:

```
f4 { f1: 1
     f2 { f1 (repeated): follower { f1: id, f2: level, f3: look, f4: grade }
          f2:            leader   { f1: id, f2: level,           f4: grade } }
     f5: -1 }
```

The leader appears only once and without a look of its own, because its look is the group's, the one in
`f2.f3`: that is the sprite the client draws. Checked on nine groups from the fight and movement
captures, and the count always comes out as one leader plus however many followers there are.

**This had the actor list empty from the start.** One `f2` per monster was sent
hanging from `f4`, that is, a varint where the client expects a submessage. A generated parser does not
forgive that: it throws an exception and drops the whole `jss`. That is why nothing was drawn — neither the
monsters, nor the NPCs, nor the character itself, even though its entry was right.

Two details easy to swap: the **level goes in `f2` and the grade (1..5) in `f4`**, not the
other way round, and the group closes with `f5 = -1`.

The player also carries, in the captures, `f3.f1 {…}`, `f3.f5 {f7:1}` (repeated, options) and
`f3.f7`, which we do not send. They do not seem to prevent drawing, but they are unidentified.
**NPCs are not sent yet**: `BuildMapActors` does not build them.

#### `f6` is the subarea, and it is not decoration

The `jss` does not carry only actors. Outside the repeated `f5` there are:

```
f2   map id
f6   subarea id
f11  repeated: the map's interactive elements (doors, resources)
f14  1
f15  repeated: what state each of them is in
```

`f6` is what the client calls `MapComplementaryInformations`, and without it it blows up:

```
at MapInfoUI.SetInfoFromSubarea (System.Int16 subAreaId)
at MapInfoUI.SetMapInfoData (System.Int64 mapId, System.Int16 subAreaId, ...)
at MapInfoUI.OnMapComplementaryInformationsData (ccn message)
at ehl.xxt (jss a)
```

It looks up the subarea, finds nothing because we sent it zero, and throws. And with it goes everything
that widget puts up: **the area and subarea name, the coordinates, the area level, the
loot bonus**, and the minimap's little figure — which is why it stayed painted on the zaap however
much the character walked.

The value comes from `MapPositions`, checked against the capture: map 154010371 travels with 450 and
154010882 with 442, which are exactly their subareas.

`f11` and `f15`, the interactives, are still not sent.

### Characteristics (`kub`)

The **character creation** capture is the missing piece: the `kub` of a freshly
created character shows the game's default values with nothing on top.

```
id 0                      55      base life. 50 + 5 per level; at level 1 it gives exactly 55
id 1                      6 AP    in f5{f1}
id 23                     3 MP    in f5{f1}
id 47                     10000   energy, in f2{f2}, not in f4
id 48, 107, 150           100
id 120..125, 141..143     100
id 75                     10       id 97   -55
```

**The `-100 %` and the 50 % resistances were this.** That whole family of characteristics are
percentages that start at 100 and we sent them at 0; the client reads the 0 and paints the difference
against the 100 it expects. And the 0/0 life was **characteristic 0**, which was not sent: it is the
only entry of the real message that **carries no id**, because proto3 skips the field when it is
zero and zero is its id. `LearnCharacteristicIds` only read the entries with an id, so exactly that one
was lost.

Containers, which are not all the same, and **this is not cosmetic**:

```
f4 { f2: base, f3: from scrolls, f7: from equipment }   almost all
f5 { f1: base, f5: bonus }                              1 (AP) and 23 (MP)
f2 { f2: value }                                        29, 47 (energy) and 96
```

We sent 29 and 96 in `f4`. The client reads a field that is not there, throws `NullReferenceException`
in `giq.bkjt (llp a)` inside `ees.wuc (kub a)` and **is left without the whole character sheet**. That
is what had the characteristics button greyed out while the C key did open the panel: the
client builds the panel with its data, the button is enabled by the handler that never finished.

Which id goes in which container **is read from the captured `kub`**, it is not written in the code:
`WorldEntry.ContainerOf`. At startup it says so on the console.

The `f3` was not the constant 100 it seemed either. The captured character had exactly 100 in
the six primaries because it had drunk **all the scrolls** in the game, which is the cap.
Copying it made each of our characteristics come out a hundred points above what the
database says, and the life bar with it (300 base → 400 on screen).

**Characteristic 3 = points to spend.** Proven by subtraction: 995 before spending fifteen on the
sheet, 980 after. We did not send it, and that is why "PUNTOS RESTANTES" showed 0.

**And none has to be guessed any more.** The client carries the whole table in
`data_assets_characteristicsdataroot.asset.bundle`: id, `nameId` and whether it can be spent or is visible.
Crossing the `nameId` with `world.db`'s `Translations` table gives the 122 Spanish names.
`tools/extract_characteristics.py` → `characteristics.json`.

The missing ones come from there:

```
 3 Characteristic points       27 AP dodge        28 MP dodge
 4 Spell points                82 AP reduction    83 MP reduction
40 Pods                        78 Flee            79 Lock
44 Initiative                  96 Shield          97 Temporary life malus
48 Prospecting                 46 Alignment rank
```

Watch out for those last two: prospecting is **48**, not 46, which had been noted down wrong for a while.

The server computes the six derived ones, because nobody else sends them and the client does not
deduce them by itself: **ten wisdom give one of each dodge and each reduction, and ten agility
one flee and one lock**. Without that the panel shows them at zero however much wisdom is
spent.

**Unidentified**: the body's `f4` (5 on a new character, 30 on the level 154 one) and `f9`
(`{f2: 2, f3{f3: 500}, f5: 1}` freshly created, `{f1: 100, f2: 3, f3{f3: 500}, f5: 200}` on the
154 one). They are sent with the freshly created character's value: it is the only honest thing we have.

### Spending points

```
C->S  kum   f1: intelligence, f2: chance, f3: vitality,
            f4: wisdom,       f5: agility, f6: strength
S->C  iun { f1: what it carries, f3: what it can carry }
S->C  kub (whole, again)

C->S  kuh {}          the reset button
S->C  iun, kub
```

The field order comes from the six captures in `Caracteristicas/`: spending five points on each
one sends `{f1:5, f2:5, f3:5, f4:15, f5:5, f6:5}` and characteristic 3 goes down by **forty**, the sum.

And the important part: **`kum` carries what is PAID, not what goes up**. Wisdom is those fifteen,
because each one costs three points. So the server needs the price table, and it has to
be the client's, because the client has already shown the player the result before sending
anything. It is in the bundles and it is not the usual one: the bands go a hundred at a time, not fifty
at a time.

**And they are totals, not increments.** The captures do not say that, because the character that was recorded
had just reset the sheet and on it both readings give the same. A session of the real client does
say it. Four confirmations in a row, with a reset just before the first one:

```
kum { vitality 10 }
kum { vitality 10, agility 5 }
kum { vitality 20, wisdom 15, agility 5 }
kum { vitality 40, wisdom 15, agility 10, strength 5 }
```

Read as increments, that character bought vitality four times and the capital went down forty
points when the player had only asked for the fifteen of wisdom — which is exactly what
happened. Read as totals, each message is the whole allocation as it stands, the panel simply
repeats itself, and each number ends up where the player put it.

So the field is a TARGET: this characteristic must have this many points put in. Sending the same
twice does nothing the second time, which is the property that matters, because the panel
repeats itself. And the allocation is applied **whole or not at all**: if the sum does not fit in the capital, the part
that fits is not taken. The client computes the cost before asking, so a sum that does not fit
means we do not agree about the sheet, and charging half only makes it worse.

Beware of the per-field clamp, which is how it broke the first time: clipping each characteristic
against the capital *without deducting as it went* allowed spending 180 points with 75, and the character ended up
with double of everything.

```
strength, intelligence, chance, agility  1 up to 100, 2 up to 200, 3 up to 300, 4 onwards
vitality                                 1 always
wisdom                                   3 always
```

`tools/extract_breed_stats.py` → `breed_stats.json` → `Managers/BreedStatCost.cs`.

The reset gives back `5 x (level - 1)`, which is exactly the capital of the character in the database:
75 unspent + 170 spent = 245 = 5 x 49. It charges no kamas, because in the capture the sheet's kamas
are the same before and after.

### No data from the captured account

The emulator is meant to be shared, so it cannot show the address book of whoever
recorded the captures. These messages are no longer replayed:

| Opcode | What it carried |
|---|---|
| `kqg` | The friends list, with real nicknames, levels, guilds and alliances |
| `jhe` | The guild |
| `jhh` | The guild again: founding date, level, how many members |
| `jhk` | The guild's name, written out |
| `koj` | **Twenty Ankama accounts** with their id, nickname and tag: `f2 { f2: id, f4 { f1: nickname, f2: tag }, f5: 3 }` |
| `ife` | The alliances, by name and by acronym |
| `jjs` | A merchant stall planted on the map, with the account behind it: `f5 { f2 { f8 { f1: nickname, f2: tag } } }` |
| `jaa` | The same in its own message |
| `hol` | The character's spouse and guild |
| `jgu` | The spouse again, with their look |
| `ihb` | The fourteen saved sets, each one with that account's looks |
| `ife` | The Ankama friends list (it was already out: it also blew the client up) |

`jhh` and `jhk` were going out until now, and the client's `Player.log` shows what it cost:
a `NullReferenceException` for each one, from the same handler. It makes sense — they describe a
guild whose message (`jhe`) we do not send, so there is nothing to hook them to.

And these are rebuilt from the database instead of being replayed:

| Opcode | What goes out in its place |
|---|---|
| `kva` | The character being played: name, level, class and look |
| `irq` | The jobs: the ids stay, as they are game data, all at level 1 with no experience |
| `hms` | The spells of the character's class and level |
| `itg` | The spell bar. The other `itg`, the items one, is left: it points to the inventory, which is still the capture's |

And there is a name that does not travel in any message of its own but **inside the inventory**: who
forgemagicked each item, which is the tooltip's "Modificado por".

```
ivx: f3 (repeated) { f5 (repeated) { f2 (repeated) { f1: who made it } } }
```

Five of those signatures were the captured character itself and were replaced along with the rest of its
identity; one was someone else's and nobody touched it. Now they are read from the block at startup and
all of them become the name of whoever plays. **All three levels are repeated**, and keeping
the first of each finds none: the signatures are in later entries.

#### Checking it: `tools/leak.py`

The previous version looked for specific names that had to be known beforehand, and that is why it came out
clean while the emulator was sending twenty Ankama accounts with nickname and tag in a `koj` nobody
had looked at. **A leak checker that depends on you already knowing what you are looking for checks
nothing.**

It now sweeps all the readable text of everything the server sends and groups it by opcode:

```
py tools/leak.py                  everything that has gone out, per message
py tools/leak.py --op koj ife     only those, with all their strings
py tools/leak.py --buscar Harmoo  also warns if something specific appears
```

The list **has to be really looked at**: a new opcode with proper names inside is a leak. That is how
`koj`, `ife`, `jjs`, `jaa` and the forgemagic signature came out, after the needle version
had come out clean four times in a row.

Beware too of reading names by eye from the dump: what looked like `EfesiaX` was `Efesia` plus the byte
of the next field. It is better to take them out by parsing the protobuf, not with a regular expression.

### Walking and changing map

The four captures in `Movimiento/` give the whole sequence:

```
C->S  jrw   f1: map id, f2: the packed path, each step  direction << 12 | cell
S->C  jsj   f1: cells, f2: final direction, f5: whose it is
C->S  jqi   {}                      has reached the edge and wants to leave
S->C  jsq   {}                      go ahead        <- root field 3, not 1
C->S  jqk   f2: target map id
S->C  jsd (f2: who), jru (f2: id), lqu, lqn, hjk (f1: packed ids)
C->S  kmv, jrh   both with the map id
S->C  jss (actors), lva
```

**There are three root fields and they are not interchangeable**: 1 is what the server pushes on its
own, 2 what the client sends, and **3 a reply**, which also repeats the id the
request carried (`-1` in everything seen). `jsq` is the first that needed it, and without it the
client never sends the `jqk` and the character stays on the edge forever.
`ConnectionProtocol.Answer` builds that envelope; it comes out byte for byte as the captured one and there is
an automatic test that checks it.

**The `jqk`'s map id is a GUESS, not an order.** The client computes it by arithmetic
on the id it is on, and that is only right where the map next door happens to be the next
id. Two captures show the real server loading a map different from the one it was asked for, and
the 14/08 session shows it from the other side: on 191105028, at [5,-17], leaving downwards, the
client asked for **191105029, which does not exist**. The map below is 188745734, at [5,-16]. We
gave the guess back in the `jru`, the client had nothing to load and the character was left
stuck on the edge — all the following `jrw`s start from the same cell 556.

#### CORRECTION: the neighbours ARE in the client

What the next section says about the neighbours table being server data **is false**, and
it is left written because the reasoning that led to that conclusion was reasonable and it is worth seeing
where it failed: the data root and dofusdude's JSON files were searched, and each map's geometry
bundles were not. They are there:

```
mapData.rightNeighbourId  bottomNeighbourId  leftNeighbourId  topNeighbourId
mapData.rightArrowCellList ...   the cells from which each side is left
mapData.interactiveElements      the map's interactive elements
```

`tools/extract_map_neighbours.py` takes them out: **17,353 maps and 69,410 edges**, against the 3,463 we
had. `MapScrolls` goes from 2,503 rows to 17,353.

But they are not the truth, they are **what the client guesses**:

```
client data root (2,223) vs bundles:       32.2 % disagree
harvested from the REAL SERVER vs bundles: 363 of 384 match (94.5 %)
```

And it shows in the tutorial capture: the bundle says that to the right of 241437185 is 241437697,
the client asks for exactly that, and the real server loads 241438721. So the `jqk` comes out of the
bundle. The order of authority is therefore **real server > exceptions table > bundles**, and that is
why the extractor never overwrites a value already set.

#### Why the data root only has 2,223 maps

Because **the neighbours list is not in the client's data**, and this is checked in four
different ways:

- The `data_assets_mapscrollactionsdataroot` bundle has **exactly 2,223 entries**. Our
  table is a faithful copy; nothing was lost when extracting it.
- Each map's record in `MapTemplates` (and in dofusdude's `maps_information.json`, which is
  the same data) is all of this, without a neighbours field:
  `id, m_flags, nameId, posX, posY, subAreaId, tacticalModeTemplateId, worldMap`.
- In dofusdude's release for 3.6.10.10 there are 85 JSON files and **none is `map_scroll_actions`**. The
  `map_references.json` is 571 named points `{id, mapId, cellId}`, zaaps and places.
- We tested whether `m_flags` encoded which edges are open, bit by bit against the 2,223 we do
  know: the best of the 32 bits is right 71.7 % of the time, that is, nothing.

So the neighbours table is **server data**, like the monster spawns: Ankama has it
and the client does not. Hence the client guessing. It has to be observed while playing, and that is what
`tools/cosechar_mapas.py` is for (see below). The 2,223 the client carries
are genuine —they are all 3.6.10.10 maps and so are 99.9 % of the neighbours they cite—, but
they are spread in patches: they touch 228 subareas out of 532 and only **6 are complete**. In Astrub's
there are 44 maps out of 159. It is a partial harvest, not an extraction.

#### Harvesting the missing ones from the captures

`tools/cosechar_mapas.py`. Each map change in a capture leaves on the wire everything needed:
the `jrw` says which edge is left by (the path's last cell), and the **real server**'s `jru`
says where it leads. That is an exact `MapScrolls` row.

```
py tools/cosechar_mapas.py               looks at every capture and only reports
py tools/cosechar_mapas.py --aplicar     also writes what is new into world.db
```

It never overwrites a value already set: the client's table is game data and what is harvested only fills
gaps. If something contradicted it, it says so and touches nothing.

First pass, over the 215 captures:

```
405 map changes seen, 389 usable, 384 distinct pairs
350 new, 34 confirm what was already there, 0 contradict
MapScrolls: 2,223 -> 2,503 rows, 3,463 -> 3,813 edges with a destination
```

**What yields the most by far is autopilot**: two long routes gave 295 of those 350,
five times more than the other 213 captures together. Two conditions for a capture to be useful: that
one goes **walking** to the edge (without the previous `jrw` there is no knowing which side was left by) and that
it is **on foot and not by zaap**, because each zaap leg skips all the intermediate edges, which
are exactly the ones wanted.

In those 389 transitions the client's guess was right 95.1 % of the time. The 19 misses are exactly
the edges where the player got stuck, and they are now noted down.

Measured against the 3,463 neighbours the client carried:

```
the client's arithmetic guess is right               31.8 %
the lookup by coordinates is right                   71.0 %
guess-if-valid + coordinates                         71.1 %
```

And the ceiling of any coordinates method is that, because **27.2 % of the real neighbours are
not on the cell next door**: a good quarter of the game's edges lead somewhere that is
not simply one step further. For those there is no option but to note them down.

But ignoring the guess will not do either: the map at [5,-16] is one of those that has almost nothing
—only its top neighbour—. Leaving it to the right, the client's guess
(188746246) was **right** and the table had nothing to say.

So the destination is resolved in three steps:

1. **The guess**, if it names a map that exists and that is also on the cell next door in the
   direction being walked. It is the only one of the three that knows which of several maps sharing
   a coordinate the player wants, and at [5,-15] there are two outdoors.
2. **`MapScrolls`**, which is the game's own neighbours list, where it is filled in.
3. **The coordinates**, which do have all 15,360. If there are several on the cell the outdoor one wins,
   among those the one in the same subarea, and among those the one whose id is closest to the one being left: ids are
   handed out in blocks, so a neighbour is usually numerically close.

The guess also serves to break ties at the corners, where the cell alone does not say whether one leaves
sideways or downwards.

Which edge is left by, from the cell where the `jrw` ends:

```
column 13 -> right         column 0 -> left
row <= 1  -> top           row >= 38 -> bottom
```

(The captured exits: 405 and 322 through columns 13 and 0, 23 and 542 through rows 1 and 38.)

And it never moves to a map the world data does not describe: the client cannot
load it either, so it would stay on the edge while the database says it is in a place that
does not exist. That is exactly what happened with 191105029, and the character was saved there; on
loading it this is also checked and it is sent back to the start if needed.

Where one appears on the next map, measured in the four captures:

```
right     405 -> 392    -13    direction 0
left      322 -> 335    +13    direction 4
top        23 -> 555   +532    direction 6
bottom    542 ->  10   -532    direction 2
```

Which way one leaves is decided with the database's `MapScrolls` (each map's four
neighbours); if the map the client asks for is none of them, it is looked up by coordinates, and if
not that either, it stays where it is. The arrival cell is validated against the **fight**
walkability, not against `map_walkable_cells.json`: that one trims the edges on purpose so that
monsters do not appear on them, and the edge is exactly where one arrives.

`jrw` is not answered. The real server sends the `jsj` back because that is how **the other**
clients find out; with a single player there is nobody to tell, and the client walks
on its own without waiting. What is needed is to note down the cell where it ends, because the
map change is computed from there. It used to not be noted down: the character officially stayed where
it connected.

`lqn` is not sent: its only field is a number we cannot explain (197 on entering the world, 24
on changing map, 470 after resetting characteristics) and making it up is worse than not sending it.

`MapChangeHandler` is still there with the previous version's opcodes (`jos`, `joh`, `joi`, `joo`),
unused: this client never sends them. The new code is in `Handlers/WorldMoveHandler.cs`.

### Dungeons

The only map topology the game does publish besides those 2,223.
`tools/extract_dungeons.py` takes it out of `data_assets_dungeonsdataroot.asset.bundle`:

```
187 dungeons
763 distinct rooms    (759 are in MapPositions; 4 are not, and we do not know why)
159 entrance and exit maps
```

Of those 763 rooms, **only 17 had a row in `MapScrolls`**, so almost everything is new.

Two warnings to keep in mind:

- **`entranceMapId` and `exitMapId` are NOT edge neighbours.** The entrance one is the outside map
  where the door is, and in 152 of the 187 the exit is that same map. Putting them into `MapScrolls`
  would be putting in rubbish.
- **The order of `mapIds` is the data's and it is not proven to be the walking order.** The
  Biblioteca del Maestro Cuerbok lists its three rooms at x = -14, -13, -15, which does not move in
  any direction. `DungeonManager.NextRoom` follows it all the same because it is the best there is, but it is
  written in the code that it is an assumption.

It goes into `world.db` in two tables, `Dungeons` and `DungeonRooms` (the latter with each room's position), and
`Managers/DungeonManager.cs` indexes them and offers `OfRoom`, `NextRoom` and `WayOut`.

**Nobody calls it yet.** It is the base for moving the player to the next room when they win and
for leaving them at the entrance and at the exit; the fight is still on the previous protocol version,
so that wiring waits for it to be migrated.

### What the equipment adds to the sheet

The inventory being replayed (`ivx`) already carries everything needed:

```
ivx: f3 (repeated) { f1: slot,
                     f5 { f1: template, f2 (repeated) { f4: value, f11: effect id },
                          f3: how many, f4: uid } }
```

Slots **0 to 15** are what is worn —amulet, weapon, the two rings, belt, boots,
hat, cloak, pet, the six dofus and the shield, one in each— and **63** is the bag, with
the other 593 items.

What each effect does is said by `world.db`'s `Effects` table: `Characteristic` and `BonusType`. That
`BonusType` is 1 or −1 and **it is not decorative**: effect 755 removes lock and carries a
positive number, so adding it blindly would turn a malus into a bonus.

All of that goes into **field 7** of each `kub` entry, which is the one the client shows as "from
equipment". 31 characteristics with a bonus come out, including the missing ones: elemental and fixed damage,
pushback, criticals, resistances, dodges and reductions, and the AP and MP of the exomagicked rings.

And on top go the **set bonuses**, which depend on how many pieces of a set are worn at
once: `tools/extract_item_sets.py` takes them from dofusdude's dump. Watch out for a detail: the value
of a set bonus is in `diceNum`, not in `value`, which comes as zero in all of them.

Checked whole against the real `kub` of that same account, **15 characteristics out of 15 spot on**:
strength 745, vitality 4,270, wisdom 261, intelligence 245, power 249, the five resistances
and initiative at −398.

Getting there needed two more things: the sets, and **the amulet**. Its slot is zero,
proto3 skips the field, and on reading the inventory the item arrived without a position and was taken as
stored in the bag. Thirteen effects that did not count. It is the same bug that prevented equipping it, in
another place: wherever slot zero is valid, the default value has to be zero, never the bag.

And one exception: **characteristic 0, life, carries no equipment bonus**. Several effects point
at it and adding them gave twenty-five thousand life points; the real `kub` puts nothing in its field 7. Life
is the base plus vitality and the client computes it.

### Chat

```
C->S  ktm { f2: the text, f3: the channel }
S->C  kti { f3: when "2026-08-09T20:28:01+02:00", f4: who, f5: their character,
            f6: their account, f7: what they said, f8: {}, f9: the channel }
```

Channels, from the capture that goes through all of them in one sitting: 0 general (omitted for being zero),
1 team, 2 guild, 3 alliance, 4 party, 5 trade, 6 recruitment, and 9, 11, 16, 18 and 19 for the
rest. The private message is another message, `ktb`, and carries whom it is addressed to; not implemented.

With a single player there is nobody to hand it out to, so it goes back to whoever said it — which is also what
the real server does with your own lines, and it is what makes them appear in the window.

### Archmonsters

A monster is an archmonster when it is another's `correspondingMiniBossId`: the client's data
pairs each ordinary monster with its rare version. There are **306** and all 306 declare in `subareas`
the area they belong to.

How the world was before touching it:

```
39.9 % of the groups carried at least one       35,518 placed
up to 8 in the same group                        5,802 maps with more than one
```

The four rules: one per group, one per map, one in every ten groups, and **one of each per area**
— if the archmonster is on a map of its subarea, it is on no other until it is killed.
Result: **298 groups**, one per map and one per area.

Watch out for the arithmetic: the 10 % is not what rules, uniqueness rules. Since there are only 306
archmonsters and each one can be in one place, the whole world has at most 306 however
high the percentage is set.

The draw is **deterministic**, taken from the group's own id, so a map looks the same after
restarting. Nothing is rewritten in the database: the 38,744 groups it carries are left as they
are and thinned out on reading them. And the one that loses its place is not deleted but **downgraded**: it is
swapped for the ordinary monster it is the rare version of, so the group keeps its size and its
level. `Archimonsters.Release(map)` releases the one a map had, for when it is implemented that on
killing it it can reappear elsewhere in its area.

### Spells

```
S->C  hms   f1 repeated { f1: rank, f3: spell id, f4: 1 }       the ones it has
S->C  itg   f1 repeated { f2: slot, f6 { f2: id } }             the bar
C->S  itz   f2 { f2: slot, f6 { f2: id } }, f3: 1               change a slot
S->C  ivk   the same, back
```

Both are built from the database: `SpellVariants` gives each class's spells and
`SpellLevels` the level at which each rank opens. A level 50 Cra has 14; the capture's level 154
Sacrier had 36, and those were the ones that came out before.

The capture's other `itg`, the items one, uses `f9 { f2: template id, f3: uid }` instead of
`f6`, and that is how they are told apart: there is no mark in the message saying which bar it is about.

### Equipping and unequipping

```
C->S  iuk   f1: how many, f2: item uid, f3: where it goes
S->C  ivq   f1: uid, f2: where it ended up
S->C  lym { f1: 206 }, hie { f1: 2 }, hii { f1: 2 }     the same in every capture
S->C  iun                                               the pods, since what is worn also weighs
```

Positions, from the captures and from a real client session: **0 the amulet**, 2 to 5 the rings
and the belt, 6 the hat, 12 to 14 the dofus, and **63 the bag** (where what is taken off goes).

The amulet deserves its own line because it was the only one that could not be equipped. Its slot is
**zero**, so proto3 skips the field and the message arrives with the uid and nothing else. Reading that as
"it has not told me a slot" and answering 63 sent every amulet to the bag.

What it does **not** do yet: change the characteristics. The equipment bonus goes in field 7 of
each `kub` entry, and filling it requires knowing which item each uid is, that is, the inventory
coming out of the database and not out of the capture. Until then the item moves and the sheet does not
follow it: neither the damage, nor the resistances, nor the AP/MP of the exomagicked rings, nor the set
bonuses.

### What was hard to find

**The block was an identity conflict.** With the capture's `kva` the client got in;
with ours it stayed with the hourglass. It was getting two contradictory identities.

A byte-for-byte replacement will not do: the captured id takes 6 bytes as a varint and ours 4,
so it throws off every nested length. `CaptureRewriter` takes the protobuf apart,
substitutes and recomputes the lengths upwards. It only descends into a field if the value
sought is inside, so as not to wreck binary blocks that are not submessages.

The captured character's name **is not in the code**: it is read from the `kva` itself at startup.

### Identified messages

| Opcode | What it is |
|---|---|
| `kva` | Selected character. `f1{f1{f1: details, f2: id}}`, with two more dates than the `kvi` |
| `jru` | Map id, in field 2. Replacing it works |
| `kub` | Characteristics. `f1`: experience, `f7`/`f8`: thresholds, `f10`: kamas, `f11` repeated: one entry per characteristic `{f1: id, f4{f2: value}}` |
| `kqo` → `kqy` | Heartbeat every 5 s. `kqy` is always `0801` |
| `kmv` + `jrh` | "I am on map X", both with the id in field 1. Answered with `jss` |
| `jss` | Map actors. `f2`: map id, `f5` repeated: an actor |
| `lva` | Empty. End of the actor list |
| `lqc` | The client's confirmation after block 1 |
| `ieo` → `idu` | **CORRECTED 27/08/2026: they are quests, not interactive elements.** The client asks which step a quest is on and the server answers with the step and its objectives. What settles it is that it matches by itself: in the 448 `idu` frames of the 401 captures, the step really belongs to the quest they name all 448 times, and the 1,479 objectives really belong to that step. See `docs/quests.md` |
| `hjk` | Packed map ids in field 1 |
| `jrw` → `jsj` | Walking. The client sends the path, the server hands it out to the others |
| `jqi` → `jsq` | "I want to leave the map" and the permission, the latter in root field 3 |
| `jqk` | "Take me to this map", with the id in field 2 |
| `jsd` | Remove an actor from the map. `f2`: who |
| `hms` | The spells the character has: `{f1: rank, f3: id, f4: 1}` |
| `itg` / `itz` → `ivk` | The shortcut bar, and changing one of its slots |
| `kum` / `kuh` | Spending points and resetting the sheet |
| `iun` | Pods: `f1` what it carries, `f3` what it can carry |
| `iuk` → `ivq` | Moving an item to an equipment slot (or to the bag, which is position 63) |
| `lqu` | `f1`: 120, `f2`: the server's clock in milliseconds |

### What is missing compared with the capture

Comparing the world entry frame by frame with the official capture (391 messages), the
emulator sends 370. The difference:

| Opcode | Why it is missing |
|---|---|
| `kqg`, `jhe`, `jhh`, `jhk`, `hol`, `jgu`, `ihb` | On purpose: they name real people. See below |
| `kub`, `irq`, `hms`, `itg` | Not missing: replaced by the ones we build from the database |
| `lzl` (2 KB) | Unidentified. It goes before `jss` on entering the world, but **not** on map changes, so it is not needed to load a map |
| `lvb` (`0807`), `hpm` | Unidentified. They go after `lva` on entering the world and do not appear on map changes either |

### Three that were out because of a wrong identification

`itg`, `ife` and `ivi` went back onto the wire. What was said about them did not hold up to checking:

- **`itg` is the shortcut bar**: two messages, `f6` for spells and `f9` for
  items. Removing it is what left the spell bar empty. The reason was good — it was going to be
  built from the database — and the replacement is already written: the spells one is rebuilt
  and the items one is still replayed while the inventory is the capture's.
- **`ife` is not the friends list.** The contacts are in `kqg`. `ife` is 180 entries with
  guild names and acronyms. The hang blamed on it may have been real, but it was stuck to the
  wrong message.
- **`ivi` is not the inventory.** It is 9,694 `{id, value}` pairs, ids from 44 to 34352 and values
  up to 651 million: it looks like the account's statistics and achievements counter.

The long exclusion list there was before (`mft`, `idr`, `ivx`, `isw`, `irq`, `isd`, `jco`,
`hjk`, `jtg`, `koj`...) is gone too: those messages go out again.

### Status

**Done in this round**: the `kub` containers (and with them the characteristics button),
characteristic 3, the scrolls `f3`, spells and bar from the database, walking and changing
map (with the destination resolved by us, not by the client's guess), the subarea in the
`jss`, spending and resetting points, equipping and unequipping.

**Careful when sharing**: `logs/gameserver_traffic*.log` keeps everything that has gone out on the wire,
and the files from before the privacy filter carry the capture's real names inside.
Empty `logs/` before packaging anything.

**Pending**, in order:

1. Remove the replay. Everything that comes out of `world_etapa*.bin` is another account's data:
   it serves as a reference for what the client expects, not as an answer. What is already built
   from the database is `kva`, `kub`, `jru`, `jss`, `irq`, `hms` and `itg`.
2. **Inventory from the database**, and with it **everything the equipment adds to the sheet**: the
   field 7 of each `kub` entry. Right now nothing goes up — neither elemental, fixed, pushback
   or critical damage, nor resistances, nor the AP and MP of the exomagicked rings, nor the set
   bonuses. The ids of all those characteristics are already in `characteristics.json`; what is
   missing is knowing which item each uid is. The items are already in `CharacterItems`
   (`Uid`, `Gid`, `Quantity`, `Position`, `Effects`) and are loaded on entering, but the one seen
   in the client is the capture's. The message has to be identified: the candidate is `lwt`
   (153 entries `{f1: template id, f2 repeated: effects, f3: quantity, f4: uid}`), but
   **it carries no position**, so either the equipped ones travel separately or the message is another one.
   Hanging from this is the equipment adding to the sheet (field 7 of each `kub` entry) and
   equipping persisting.
   `InventoryHandler` is still entirely on the previous version (`isi`, `iry`, `luy`, `kku`...) and with
   the field-3 envelope.
3. **The map's interactive elements**, which is the biggest hole left: without them there are no
   houses, no kanojedo, no taverns, no zaaps, no zaapis. Investigated but **not implemented**.
   What is already known:

   ```
   jss f11 { f1: 1, f3 { f1: instance uid, f2: skill id },
             f5: element id, f6: type }
   jss f15 { f1: 1, f2: cell, f3: element id, f4: state }
   ```

   And where the data comes from: the map bundles' `mapData.interactiveElements` gives, for each
   element, `m_interactionId` (the id), `cellId` and `gfxId`. **The bundle's cell matches the
   one the real server sends 12 out of 12**, so that part can be trusted.

   What is missing is the **skill**: `f3.f2`. In the captures there are only 36 elements on 16 maps,
   far too few to deduce it. The `gfxId` does determine the type (25 of 26 carry only one), so the
   way is crossing `gfxId` with `skills.json` (368 skills, already downloaded, with `elementActionId`
   and `parentJobId`). Nothing was half implemented on purpose: touching the `jss` without being able to test it
   with the client risks breaking the map load, which works now.
4. **The left-click menu on one's own character** (slapping oneself, turning around). Not
   looked at: the `jss` actor carries in the captures `f3.f1`, `f3.f5` (repeated, options) and `f3.f7`
   which we do not send, and the options could come from there.
6. Quests: they are still the capture's, their opcodes unidentified.
7. The account nickname in the title is still the captured one.
8. Infinite dreams and merkasako infinite and merkasako. They all end in a map change, which already
   works, but each one also has its own message; there are captures of all four.
9. NPCs in `jss`.
10. Fighting, still on the previous protocol version.
11. Unidentified: `lzl`, `lvb`, `hpm`, `lqn`, and the `f4` and `f9` of the `kub`'s body.

### Automatic tests

`ConnectionProtocolSelfTest` runs at startup —**after** loading the blocks, which is where
it reads part of what it compares— and contrasts `kqy`, `lva` and `jsq` byte for byte with the capture,
checks that the five characteristics with an odd container (1, 23, 29, 47, 96) are still where
they should be, and reviews the shape of the connection phase messages.

`tools/cliente_falso.py` walks the whole session against the started emulator: map block only
once, `lva` after `jss`, heartbeats answered only with `kqy`, the `kub` containers,
spells of the right class, the subarea in the `jss`, `jqi` → `jsq` in root field 3, the map
change **with both branches** (a guess that does not exist and another that does), spending points with
their derived ones, that repeating the same `kum` moves nothing, that an impossible allocation is rejected
whole, and equipping **including the amulet with no slot field**. It leaves the `jss` in `jss_emu.hex`
to open it with `tools/pcap.py`.

And `tools/leak.py` after each change in what is replayed.

### The launcher's console

If the events panel gets stuck, it is not that nothing is happening: it is that
`ConsoleLogBuffer.GetLogsJson` has produced a JSON that `LauncherService.GetLogs` could not
read. The window only moves its cursor forward with what it is given, so it asks for the same
broken batch forever. It happened twice for the same reason: raw control characters from the packet
dump, and a lone surrogate (which also has to be **replaced**, not escaped: escaped, the
document parses but blows up when taking out that string, after having collected the previous ones).
Now the escaping is complete and each line is read separately, so a bad one is lost on its
own.

Another detail: the packet tracer builds each line with several `Write`s and closes it with a
`WriteLine`. `InterceptWriter` joins them; keeping only the `WriteLine` left in the window
the tail of the line and nothing else.
