# The wire protocol

How the Dofus **3.6.10.10** client talks to a server, from the first TCP byte to standing on a map.

Everything below was measured. The figures come from the **242 `.pcapng` captures** of real sessions
(**103,808 frames** reassembled), from the emulator's own source, from `datos/` and from the
client's own `Player.log`. Where something is a guess, it says so. Where a claim can be re-checked
with a command, the command is given.

---

## 1. The services

Five TCP listeners are started in `Jondo.Unity.Launcher/Program.cs`. The client only ever speaks to
four of them.

| Port | Started by | Transport | What it is for |
|---|---|---|---|
| 8888 | `HaapiServer` | HTTP | Ankama's account API, and the config document that tells the client where everything else lives |
| 15881 | `ZaapServer` | Thrift binary over TCP **and** over a named pipe called `15881`; also answers HTTP and WebSocket on the same port | The local launcher service. The client asks it for a game token |
| 5555 | `GameServerProxy` | Length-prefixed protobuf | Both the connection server and the game server (see §6) |
| 5556 | `GameNodeProxy` | Same | Listener only. Nothing is ever sent there |
| 6337 | `ChatServer` | TLS over TCP | Accepts the handshake, logs what arrives, answers `{"success":true}` to anything carrying a `token` |

Port 5556 is dead weight. `GameServerProxy` hands the ticket reply to
`ConnectionProtocol.BuildServerSelected(lang, ticket, "127.0.0.1", Program.gamePort, Program.gamePort)`
— both port slots are 5555 — so the client reconnects to 5555 and `GameNodeProxy.Start(5556)`
never sees a connection. The session logic still lives in `GameNodeProxy`; only its listener is
unused.

### How the client finds all of this

One HTTP GET does it. `HaapiServer` serves `/config/dofus3.json`:

```json
"connectionHosts": ["JMBouftou:127.0.0.1:5555"],
"chatServerHost": "127.0.0.1",
"chatServerPort": 6337,
"haapiAnkamaUrl": "http://127.0.0.1:8888/json/Ankama/v5/",
"login": { "ports": [5555], "hosts": ["127.0.0.1"] }
```

The client would fetch that from `haapi.ankama.com` over HTTPS. `JondoFix/Class1.cs` is a
MelonLoader mod that rewrites the URL to `http://127.0.0.1:8888` before the request goes out. That
single redirect is what brings the whole client onto the emulator: every other address it uses
comes out of the document above.

---

## 2. Framing

TCP port 5555, in the clear. No compression, no encryption, no handshake of its own.

A **frame** is a varint length followed by that many bytes. The bytes are a protobuf message with
**exactly one field** — that held for all 103,808 frames measured, without exception. So one frame
carries one message, and a message never spans frames.

The single root field wraps this:

```
root { <1|2|3> { f1: Any, f2: request id (client and answers only) } }

Any { f1: "type.ankama.com/" + three-letter opcode
      f2: the payload, raw bytes }
```

The opcode is always three letters. `NetworkEnvelope.BuildGameNodePacket` prints a warning if it is
not, because a wrong length is the one framing mistake that produces no error on the client at all —
it simply ignores the message.

### A real frame, byte by byte

A server telling the client which map to load. 33 bytes on the wire, taken from
`Movimiento/movimiento a mapa de abajo.pcapng`:

```
20 0a 1e 0a 1c 0a 13 74 79 70 65 2e 61 6e 6b 61 6d 61 2e 63 6f 6d 2f 6a 72 75 12 05 10 82 8a b8 49
```

| Bytes | Meaning |
|---|---|
| `20` | varint 32 — the frame body is 32 bytes long |
| `0a 1e` | root field **1**, wire type 2, length 30 — a message the server pushed on its own |
| `0a 1c` | field 1, length 28 — the `Any` |
| `0a 13` | field 1, length 19 — the type URL |
| `74 79 70 65 … 6a 72 75` | `type.ankama.com/jru` |
| `12 05` | field 2, length 5 — the payload |
| `10 82 8a b8 49` | inside the payload: field 2, varint = **154010882**, the map id |

### What the envelope costs

The type URL is 19 bytes and it travels on every single message. The smallest possible server
message is therefore 26 bytes on the wire and carries no information beyond its own name — `lva`,
the "that is the whole actor list" marker, is exactly that:

```
19 0a 17 0a 15 0a 13 74 79 70 65 2e 61 6e 6b 61 6d 61 2e 63 6f 6d 2f 6c 76 61
```

Of the 103,776 frames that carry a type URL, only 89,337 also carry a payload. About one message in
seven is a bare opcode: proto3 omits empty fields, and plenty of these messages have nothing to say
beyond the fact that they happened.

---

## 3. The three root fields

The root field number carries the direction and the kind of the message. It is not decoration —
put a message on the wrong one and the client drops it silently.

| Root field | Who sends it | Meaning | Frames measured |
|---|---|---|---|
| `f1` | server | Push. Sent on the server's own initiative | 83,146 |
| `f2` | client | Request | 15,583 |
| `f3` | server | Answer to a request, carrying its id | 5,047 |

(The remaining 32 frames belong to the connection server, which does not use this envelope at
all — see §6.)

Ninety-four per cent of what the server sends is unsolicited push. Entering the world is one long
push; only a handful of exchanges are request/answer.

A client request, 37 bytes — "I have reached the edge of the map, may I leave?":

```
24 12 22 0a 15 0a 13 74 79 70 65 2e 61 6e 6b 61 6d 61 2e 63 6f 6d 2f 6a 71 69 10 ff ff ff ff ff ff ff ff ff 01
   ^^^^^ root field 2                                                        ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ request id
```

There is no `12` after the type URL, so `jqi` carries no payload. The trailing
`10 ff ff ff ff ff ff ff ff ff 01` is field 2 of the wrapper: a varint of `0xFFFFFFFFFFFFFFFF`,
which is **-1** read as a signed 64-bit integer.

The answer, also 37 bytes:

```
24 1a 22 0a 15 0a 13 74 79 70 65 2e 61 6e 6b 61 6d 61 2e 63 6f 6d 2f 6a 73 71 10 ff ff ff ff ff ff ff ff ff 01
   ^^^^^ root field 3
```

Same shape, different root field, and the request id repeated back.

**The root field is not interchangeable.** `jsq` is the go-ahead for a map change. Sent on field 1
instead of field 3, the client ignores it, never sends the `jqk` that names the map it wants, and
the character stands on the border for good. `ConnectionProtocolSelfTest.CheckWorldMessages`
compares the emulator's `jsq` against the captured bytes for that reason.

---

## 4. The request id, and why answers pair by order

**The client sends -1 for almost everything.** Of the 15,583 client requests measured, **15,416
(98.9 %) carry -1**. Of the 5,047 answers, 5,000 carry -1 back.

That costs eleven bytes on every client message to say nothing, and it takes away the only
mechanism the envelope has for matching an answer to its request. What is left is **arrival order**.
It works because a single TCP connection preserves order and the server answers in the order it was
asked. It is also the reason three separate things go wrong:

- **Two requests of the same kind in flight.** Nothing in the reply says which one it belongs to.
  `tools/extraer_apariencias.py` sweeps the whole cosmetics catalogue by sending one `lys` per
  garment and reading the `lxc` that comes back; its only defence is
  `if len(peticiones) != len(respuestas): raise SystemExit(...)`. If the counts ever drift, the
  whole sweep is thrown away rather than silently mislabelled.
- **A push that looks like an answer.** The server pushes constantly. Anything that pairs "next
  message received" with "answer to what I just sent" will eventually pair a request with an
  unrelated push. Pair on the root field first, then on order.
- **One missing or reordered answer desynchronises everything after it**, and nothing reports it.
  There is no id to notice the gap with.

### The exceptions, which matter

The id is *not* always -1. **167 client requests carry a real, incrementing id**, spread over 16
opcodes:

| Opcode | Requests with a real id |
|---|---|
| `lqf` | 76 |
| `ify` | 17 |
| `krl` | 14 |
| `kro` | 14 |
| `igy` | 11 |
| `krv` | 9 |
| `kum` | 7 |
| `lux` | 5 |
| `kxh` | 4 |
| `iio` | 3 |
| `iha` | 2 |
| `luy`, `ihf`, `ihs`, `iin`, `iiy` | 1 each |

The server echoes them: 47 answers carry an id other than -1 (`iic` 17, `ija` 11, `ltd` 5, `lad` 4,
`ihq` 3 and six more). The largest single group — 71 of the 214 — comes from one capture, the one
that creates and edits spell and item sets. That is a screen where several edits really are in
flight at once, which is presumably why the id is used there and nowhere else.

So: **never hardcode -1 in an answer.** Read the id off the request and send it back.
`ConnectionProtocol.RequestId(frame)` does the reading, `ConnectionProtocol.Answer(opcode, payload,
requestId)` the writing, and every one of the emulator's nine answer sites goes through them.

---

## 5. The 131,072-byte ceiling

The client refuses any message larger than 128 KiB. This is not inferred; it is in the client's own
log:

```
EXCEPTION (TcpConnectionLayer:395) - System.ArgumentException:
Message size (1953658213) exceeds maximum allowed size (131072).
```

Two things to read out of that line. The limit, 131,072, is real and is enforced inside the client's
TCP layer. The size, 1953658213, is `0x74727565` — the ASCII bytes `true` — so that occurrence is
the client reading four bytes of plain text as a message length, not a genuinely oversized message.
The limit is what matters; the number is noise.

**Why it bites.** The envelope has no fragmentation. One message is one frame, and there is no
continuation field anywhere in it. So anything the protocol models as a single message has to fit,
however much of it there is. The inventory is the obvious case: the whole thing travels in one
`ivx`, item by item, every time the character enters the world.

Measured sizes:

| What | Bytes | Share of the cap |
|---|---|---|
| Largest frame in any capture (a fight capture) | 88,022 | 67 % |
| `ivi` in the world-entry capture (also the largest frame in `world_etapa3_mapa.bin`) | 87,878 | 67 % |
| Largest `ivx` the emulator has logged (1,722 items) | 96,713 | 74 % |
| Largest `ivx` in the captures (616 items) | 20,691 | 16 % |

The two `ivx` figures give the per-item cost, and they differ:

- Real server, 616 items in 20,691 bytes → **34 bytes per item**.
- This emulator, 1,722 items in 96,713 bytes → **56 bytes per item**.

Ours are heavier because `ConnectionProtocol.BuildInventory` writes out every effect of every item
as its own submessage. The correlation is exact: the console log records
`[Equipment] 1722 items from the database` at 19:15:25.087 and the traffic log records a 96,713-byte
`ivx` eleven milliseconds later.

**How to live with it: budget, do not hope.** `tools/dotar_apariencias.py` is the tool that fills a
character's bag with cosmetics, and it is the one place where the cap is a hard constraint. It
assumes 69 bytes an item and keeps a 12,000-byte margin, which caps the bag at
`(131072 - 12000) / 69 = 1725` items, and then spends that budget deliberately: first the garments
whose look the emulator actually knows, then a round-robin across garment types so the bag has some
of everything instead of a thousand hats.

Go over the limit and the client throws inside its own TCP layer, drops the message, and tells the
server nothing. From the server's side the write succeeded. The symptom is a panel that stays empty
with no error anywhere.

---

## 6. Port 5555 carries two protocols

The client opens a fresh connection for each phase, so one port serves both.
`GameServerProxy.HandleGameClient` decides on the **first frame**: if it contains the string
`type.ankama.com` it is a game session and goes to `GameNodeProxy`; otherwise it is the connection
server.

The connection server does not use the envelope at all. It speaks a small, ordinary protobuf schema
— `Jondo.Unity.Launcher/Protocol/GameProtocol.proto`:

```proto
message GameMessage {
    AuthenticationTicketMessage       auth       = 1;   // client -> server
    AuthenticationTicketResultMessage authResult = 2;   // server -> client
}
```

So here the root field means something else entirely: field 1 is the client and field 2 is the
server, and there is no request id. Picking a server is 11 bytes:

```
0a 0a 08 0a 01 31 22 03 08 a2 02
```

| Bytes | Meaning |
|---|---|
| `0a` | varint 10 — body length |
| `0a 08` | field 1, length 8 — `auth` |
| `0a 01 31` | `lang` = `"1"` |
| `22 03` | field 4, length 3 — `selectedServer` |
| `08 a2 02` | `serverId` = 290 |

The reply carries a ticket, a host and a packed list of ports. The emulator issues a single-use
ticket through `SessionRegistry.Issue(accountId, serverId)` and points the client at 127.0.0.1:5555.
The client then closes this connection and opens a new one, presenting the ticket in `kqz`. That
ticket is the only thing binding the game session to an account: without it the character list
would be the same for everybody.

---

## 7. The look block

`EntityLook` is the most reused structure in the protocol. It turns up inside at least **37
different messages** — most often `jss` (the map actor list) and `lxc` (the appearance draft) — and
it is recursive: a look can carry other looks.

| Field | Wire type | Meaning |
|---|---|---|
| `f1` | packed varints | Colours, one per slot, packed as `(slot << 24) \| rgb` |
| `f2` | varint | **3** in almost every block, but not always, and its meaning is unknown — see below |
| `f3` | varint | Bones id — which skeleton to animate |
| `f5` | packed varints | Scale. Where present it is a single value, in all but one block measured |
| `f6` | packed varints | Skins. A **set**, not an ordered list — see below |
| `f7` | message, repeated | Sub-entities: `{ f1: a look block, f4: where it attaches }` |

A note on counting: a look has a short signature, so a scan that walks every submessage in the
capture matches more of them the deeper it goes. The totals below are the ones that do not move —
sub-entity counts, which are anchored on `f7` — rather than a total block count, which does.

The `f7` wrapper carries exactly two fields and never any others: 4,062 occurrences of `f1`, 4,062
of `f4`.

**`f2` is not a constant, whatever it looks like.** A scan that finds looks by `f2 == 3` can only
ever find blocks with a 3 in them, so that measurement proves nothing. Matched on structure
instead — a bones varint plus a packed skin list, without reading `f2` at all — 6,448 of 7,065
blocks carry 3 and **617 do not**, spread over fourteen opcodes; `iqj` carries something other
than 3 in all 73 of its blocks. The sub-entities settle it without any matcher at all, since they
are looks by position: of the 4,062 hanging off an `f7`, **176 carry values other than 3** — 8, 4,
26, 25, 20 and more — while being otherwise ordinary looks, colours and bones and scale and skins.
What the field means is not known. The emulator writes a constant 3 (`BreedLookTable.LookType`)
and nothing has gone wrong yet, which is the only argument for keeping it that way.

**Attachment points**, from `f4`:

| Value | What hangs there | Occurrences |
|---|---|---|
| 1 | Pet | 1,620 |
| 2 | Rider | 2,295 |
| 6 | Aura | 123 |
| 4 | Not identified. Always seen alongside 1 and 6 | 24 |

### Colours

Each colour carries its slot in the high byte, so the list is self-describing and order-independent.
Slot indices **1 to 10** were observed; 1 to 8 are common (between 7,047 and 8,914 occurrences
each) and 9 and 10 appear 512 times each. Sixty more values carry no index at all, which reads as
slot 0: that is the bare form described below. A breed's default palette is six colours — all 38
breed-and-sex entries in `datos/breed_looks.json` carry exactly six — so slots 7 and up come from
somewhere else.
`BreedLookTable.IndexColors` builds the indexed form from bare rgb with
`((i + 1) << 24) | (rgb & 0xFFFFFF)`.

The same six colours travel **without** the index in a saved wardrobe outfit. Sending them indexed
there makes the client fail to build its `ColorSet` and the cosmetics window crashes on opening.
Same numbers, two encodings, one field apart.

### Skins are a set

`f6` is not ordered. Across 2,993 distinct skin multisets in the captures, **26 were seen in more
than one order** — the same character, the same garments, a different sequence on the wire. For
example the multiset `{110, 3118, 3333, 3791, 4029, 5050, 5109, 5203, 5312}` appears in at least
three different orders.

Treat it as a set. Comparing two looks by comparing the byte string of `f6` will report differences
that are not there; `tools/extraer_apariencias.py` sorts before comparing for exactly this reason.

### Mounted: the mount is the root

This is the part that catches an implementation out. When a character rides, the look block is
**not** the character with a mount attached. It is the **mount**, with the character attached at
binding point 2.

The evidence, from the 2,295 blocks that carry a binding-2 sub-entity:

| Check | Result |
|---|---|
| The root carries no skins | 2,138 of 2,295 (the other 157 carry exactly one — an appearance mount's own skin) |
| The root's bones is a large id | 160 distinct values, the smallest 639. Never 1 or 2 |
| The sub-entity at binding 2 carries skins | 2,295 of 2,295 |
| The sub-entity's bones is **2** | 2,283 of 2,295 (the remaining twelve carry large ids, unidentified) |

Bones 2 is the riding pose, not a skeleton id in the same sense as the others: the same character on
foot carries bones 1. `Mounts.RiderBones = 2` and `Mounts.RiderBindingPoint = 2` are the two
constants that encode this.

A real mounted look, 84 bytes, laid out by nesting level:

```
0a 08 85e2ef3f 96d28241      f1  colours: 7:fbf105, 8:20a916   <- the mount's two colours
10 03                        f2  = 3
18 f04a                      f3  = 9584                        <- the mount's skeleton
2a 01 73                     f5  = [115]                       <- the mount's scale
                             (no f6: the mount carries no skins)
3a 40                        f7  sub-entity, 64 bytes
   0a 3c                       f1  the look inside it, 60 bytes
      0a 20 9df3860f …           f1  colours: 1:e1b99d 2:b4a1bb 3:740237
                                       4:ee060d 5:1c6b6b 6:936999 7:fbf105 8:20a916
      10 03                      f2  = 3
      18 02                      f3  = 2                       <- the riding pose
      2a 01 37                   f5  = [55]
      32 11 6ec029ae …           f6  = [110, 5312, 3118, 3333, 3791, 4029, 5050, 5109, 5203]
   20 02                       f4  = 2                         <- attached as the rider
```

Read it top down and the shape is unmistakable: the thing being drawn is a mount, and the character
is cargo.

The consequence for anyone writing a server: you cannot mount a character by patching a field. You
have to build the tree the other way up. `BreedLookTable.BuildLook` does it in that order — assemble
the character's own body first, then, if there is a mount with usable bones, wrap it:
`Mounted(riderBytes, mount, …)` emits the mount's colours, bones and scale at the root and hangs the
finished rider block off `f7 { f1: rider, f4: 2 }`.

Two more rules that fall out of the same structure, both from `BreedLookTable`:

- A **petmount** or an **appearance mount** overrides the root — that is how a dragoturkey ends up
  looking like something else. They replace the root's bones, scale and colours, and only appearance
  mounts also bring a skin of their own.
- On foot the root is the character, so a pet hangs off the character. Mounted, the root is the
  mount, so the pet hangs off the **mount**. Same binding point 1, different parent.

---

## 8. Entering the world

The interleaved timeline below is measured from
`Autenticacion-Servidor-Personaje/desde eleccion servidor a eleccion personaje y carga en world.pcapng`,
reassembled with per-frame timestamps so the two directions can be lined up.

| # | Client sends | Server answers |
|---|---|---|
| 1 | *(connection server, bare protobuf)* server selection | ticket + host + ports |
| 2 | `kqz` (the ticket), `krt` | 8 frames: `kra lqu hoy kqu mgq mgt hpd krs` |
| 3 | `kvc`, `krv` | 6 frames — `mgz kqp kqp kqp` **`kvi`** `jtg`; the fifth is the character list |
| 4 | **`kvw`** (pick a character) | **330 frames** — block 1 |
| 5 | **`lqc`** (block 1 digested) | **4 frames** — block 2 |
| 6 | `kqo` | `kqy` — the heartbeat, answered on its own |
| 7 | 8 more client messages | 4 frames |
| 8 | `kmr` | 11 frames, including **`jru`** — the map |
| 9 | 4 more client messages | 13 frames: `hou`, eleven `ktq`, `lru` |
| 10 | 4 × `ieo` | 4 × `idu` — the quest journal, not interactive elements. See `quests.md` |
| 11 | 5 more client messages | `ivi` (87,878 B), then `kqg`, `koj`, `hol`, `jjs` |
| 12 | `lzh`, `kmv`, **`jrh`** (who is on this map?) | `lzl`, **`jss`** (the actors), `lvb`, **`lva`** (end of list), `hpm` |

Roles worth naming, each confirmed by direction counts across the captures:

| Opcode | Direction | What it is |
|---|---|---|
| `kqz` | C → S | Presents the ticket. Field 2 is the ticket string |
| `kvi` | S → C | The character list |
| `kvw` | C → S | Character selected |
| `kva` | S → C | Which character you are playing: name, level, breed, look |
| `lqc` | C → S | The client has digested block 1 |
| `kqo` / `kqy` | C → S / S → C | Heartbeat, every five seconds. 2,890 sent, 2,881 answered |
| `jru` | S → C | Load this map. Field 2 is the map id |
| `jrh` | C → S | Who is on the map? |
| `jss` / `lva` | S → C | The actor list, then the marker that closes it |
| `jrw` / `jsj` | C → S / S → C | Walking inside a map, and the confirmation |
| `jqi` / `jsq` | C → S / S → C | May I leave the map? Go ahead — the answer on root field 3 |
| `jqk` / `jsd` | C → S / S → C | Take me to this map; you are off the old one |

### Where the emulator differs, and why

- **Block 2 is sent straight after block 1**, without waiting for `lqc`. The client does send `lqc`,
  but only once it has finished digesting block 1, by which time the catalogues are already useful.
  Waiting gains nothing.
- **Block 3 goes out on `lqc`.** It used to go out on the first `kqo`, and that cost 4.8 seconds:
  `kqo` is a heartbeat on a five-second timer, not a request for the map. During that gap the client
  is in the world without knowing which map, so it shows its default scene — which is why Incarnam
  used to flash up before the fade to black, and why its music played on the character screen. The
  `kqo` path is still there as a fallback for clients that never send `lqc`, `tools/cliente_falso.py`
  among them.
- **Block 3 goes out once per entry.** It contains `jru`, and `jru` means "load this map". Sending it
  twice makes the client reload the world in a loop.

`tools/cliente_falso.py` walks this whole sequence without opening the game: it reads a game token
out of `bases/auth.db`, authenticates, takes a ticket, presents it in `kqz`, selects the character
from the `kvi` it gets back, and then checks that the map block arrived, that `jrh` is answered with
`jss` followed by `lva`, and that the next three heartbeats get exactly one `kqy` each and nothing
else.

---

## 9. The three world blocks

`datos/world_etapa1_tras_elegir_personaje.bin`, `..._etapa2_tras_confirmar.bin` and
`..._etapa3_mapa.bin` are the burst the official server sends on entering the world, recorded from a
real session and replayed by `Network/WorldEntry.cs`.

**Why they exist.** Entering the world is 368 messages of which we have a schema for a handful. The
blocks are the only way to get a client fully into the world today. They are the recording that the
emulator is gradually replacing: every message rebuilt from the database is one frame that no longer
has to be replayed.

**Why three and not one.** The real server sends a block, waits for the client, and carries on. Send
it all at once and the client discards the tail: it has not asked for the map yet.

| Block | Frames | Bytes | Largest message |
|---|---|---|---|
| 1 — after choosing the character | 322 | 64,510 | `ivx`, 16,821 B |
| 2 — after the client confirms | 2 | 2,348 | `jtg`, 2,306 B |
| 3 — the map | 31 | 90,935 | `ivi`, 87,878 B |

Block 3 is one message wearing a 31-message coat: `ivi` alone is 97 % of the file.

### What was taken out, and why

The recording is somebody's real session. It carries their contacts, their guild, their alliance,
their spouse, their saved outfits, twenty Ankama accounts with nickname and tag, and player stalls
with the account behind them. Publishing the file as recorded publishes all of that.

Two mechanisms, deliberately kept in step:

- `WorldEntry.NotReplayed` is the runtime filter. The emulator refuses to forward those messages
  even if they are in the file.
- `tools/sanear_world.py` physically deletes the same frames from the file, so the data does not
  travel inside the repository either. The list is duplicated in both places on purpose; if one
  grows, the other has to.

Measured frame by frame against the capture the blocks came from, **13 of the 368 frames were
removed**:

| Block | In the capture | Shipped | Removed |
|---|---|---|---|
| 1 | 330 | 322 | `kub` the character sheet, `jhe` `jhh` `jhk` the guild, `ife` the alliances by name and tag, `jgu` the spouse with its look, `jaa` a player's stall with the account behind it, `lyt` that account's saved outfits |
| 2 | 4 | 2 | `hhy` that account's titles and ornaments, `ihb` its fourteen saved outfits, each carrying a full look |
| 3 | 34 | 31 | `kub` again, `jhh` the guild again, `jjs` a stall |

`WorldEntry.NotReplayed` holds a longer list than that — `kqg` the contact list, `koj` twenty Ankama
accounts with nickname and tag, `hol` the spouse — because those messages arrive just after the map
block in the capture and were never part of the three files to begin with. The filter still names
them, so they cannot come back in by accident if the blocks are ever regenerated.

Running `py tools/sanear_world.py --ver` today reports **322 → 322, 2 → 2, 31 → 31**. That is the
check working: the shipped files are already clean.

Two of those removals also fixed a crash. `jhh` and `jhk` describe a guild whose own message (`jhe`)
was already being dropped, so the client had nothing to attach them to and threw a
`NullReferenceException` on each — visible in its own `Player.log`. Taking them out removed two of
the client's six crashes and one more real name off the wire at the same time.

### What is rebuilt instead of replayed

`WorldEntry.Rebuilt` swaps five messages for ones built from the database, because each of them was
showing the player somebody else's character: `kva` (name, level, breed, look), `irq` (the jobs,
which arrived maxed out), `hms` (the spell list), `ivx` (the inventory) and `itg` (the shortcut
bars). Everything else is rewritten rather than rebuilt: `CaptureRewriter` swaps the captured
character id and name — plus the names signing forgemaged items, which are read out of the block
rather than written into the source — for the ones playing.

### What the sanitisation costs

`kub` is the character sheet, and it is gone from the shipped blocks. That has a measurable effect,
because `WorldEntry.LearnCharacteristicIds` learns two things from the captured `kub`:

- **Which characteristics to declare.** The real `kub` declares **120**, each with its id. With no
  `kub` to read, `ConnectionProtocol.BuildCharacteristics` falls back to a hardcoded list of **25**.
  That matters more than it sounds: the client fills in whatever the server leaves undeclared, and
  that is where the -100 % damage and the blanket 50 % resistances came from.
- **Which container each one travels in.** The real `kub` puts 115 of the 120 in `f4`, ids 1 and 23
  in `f5`, and ids 29, 47 and 96 in `f2`. There is no rule to it that we can see. With no `kub` to
  read, `WorldEntry.ContainerOf` returns 4 for everything — and putting a characteristic in the
  wrong container makes the client throw a `NullReferenceException` and lose the whole sheet.
  `ConnectionProtocolSelfTest` checks those five ids, but skips the check when the list is empty,
  which is exactly the shipped configuration.

This is the honest state of it: the blocks were sanitised because publishing real accounts' data is
not acceptable, and the sheet is the piece that has not been rebuilt from our own data yet.

---

## 10. Checking any of this yourself

Python is `py`, never `python`.

```
py tools/pcap.py <capture.pcapng>                 timeline: root field, opcode, payload size
py tools/pcap.py <capture.pcapng> --raw jru       hex dump of just those messages
py tools/sanear_world.py --ver                    what would be stripped from the world blocks
py tools/cliente_falso.py                         drive the emulator without opening the game
```

`tools/pcap.py` is 212 lines and has no dependencies: a pcapng reader, TCP reassembly keyed on
sequence numbers, a varint reader and an envelope decoder. If something here does not match what you
see, the decoder is small enough to read end to end and argue with.
