# World Editor — architecture planning

> Architecture document. It started as an exploration, before a single line was written; phases
> 0, 1, 2 and 3 are already done and what they taught is noted below, next to what this
> document said and did not come true.
>
> **Decided:** the framework is **Avalonia**, the editor is an **executable of its own** that works without a
> server, our layer is **text files in `content/`**, and the **launcher inherits that
> same framework**.

---

## 1. What is wanted

A tool that allows **creating content for the emulator without touching code or regenerating
files by hand**. Specifically:

| | |
|---|---|
| **Traffic** | sniff, see the client-server dialog live, and record the packets we do not know how to handle |
| **Maps** | paint cells —walkable, line of sight, blocked in fights—, see neighbours, edit the scenery |
| **Interactives** | create elements of any type, above all **teleports**, and **tie them to another map** (houses) |
| **NPCs** | place them, give them a look, actions and **dialogs** |
| **Quests** | create them from scratch, with their steps, objectives and rewards |
| **Spells** | edit spells and their effects |
| **Monsters** | place groups, edit templates |
| **Launcher** | and while at it, a more professional one |

---

## 2. The underlying problem, which has to be solved before anything else

Today the emulator's data lives in **three places that do not talk to each other**:

```
dofus3_data/          436 MB of raw client dump. Ankama's truth. Read only.
   ↓  tools/*.py
datos/*.json          63 GENERATED files. They can be redone at any moment.
bases/world.db        240 MB, 41 tables. GENERATED. Distributed compressed.
```

**None of the three can be safely edited by hand.** If someone edits `datos/npcs_reales.json`
and tomorrow `tools/extraer_npcs_reales.py` is run again, the work disappears without warning. And
`world.db` is a 240 MB binary: editing it is invisible in git, cannot be reviewed in a pull
request and cannot be merged if two people touch different things.

This has already happened: the README's warning about push damage was lost in a commit that
rewrote a section, and nobody noticed until it was looked for on purpose fourteen days later.

### The decision that unblocks everything: three layers and per-row provenance

```
  layer 1   BASE        generated from the client       can be redone, nobody edits it
  layer 2   MEASURED    learnt from the captures        can be redone, nobody edits it
  layer 3   OURS        decided by a person             ONLY THIS is written by the editor
                                                        never regenerated, always wins
```

They are merged at startup, in that order. And **each row carries where it came from**, which is exactly what
has to be shown in the interface:

```
  MAP          CELL     DIRECTION    PROVENANCE
  241438721    260      3            capture banque-20260820-091807
  241439745    246      3            decided here, 24/08/2026
```

Without that column, in six months nobody will know whether a number is a measurement or an invention, and that is
exactly the mistake this project has spent a year avoiding.

### Where our layer lives

**Proposal: text files in a new `content/`, versioned in git.** Not in `world.db`.

Reasons:

- A readable git diff is what lets DragonLord —or anyone— send content by pull
  request and have it reviewed. A 240 MB blob, no.
- Two people can touch different maps without treading on each other.
- It is small: what is decided by hand is hundreds of rows, not millions.
- If something goes wrong, a commit is reverted instead of restoring a whole database.

Suggested format, one file per domain and per area so the diffs are small:

```
content/
  npcs/         spawns.json, dialogues.json
  maps/         cells.json           (only the CHANGED cells, not all 560)
  interactives/ elements.json, teleports.json
  quests/       *.json
  spells/       tweaks.json
  monsters/     groups.json
```

**Hard rule: our layer stores DELTAS, not copies.** If a map has 560 cells and three are
changed, the file carries three. Copying the whole map means the next regeneration of the database never
reaches that map.

---

## 3. Where the editor lives

**Decided: an executable of its own, that knows how to work without a server and knocks on the door if there is one.**

It is the house's third executable, next to the launcher and the server:

```
  Jondo Emulator Launcher.exe   the player's window
  Jondo Server.exe              the world
  Jondo Studio.exe              the editor           <- new
```

And it works in **two modes**, which is what makes it comfortable:

| mode | what it does | when |
|---|---|---|
| **standalone** | opens `content/` and the databases, edits and saves. It does not need anything running. | most of the time |
| **attached** | also tells a live server to reload what you have just touched | when you want to see it in the game without restarting |

The admin channel is **thin on purpose**: it does not carry the whole editing API, only
«reload this domain». Everything edited goes through the files, and the server rereads them. That
keeps the surface minimal and means the editor cannot leave the server in a state the
files do not explain.

Non-negotiable conditions for that channel:

- **Only `127.0.0.1`.** No `0.0.0.0`, not even behind a firewall.
- **One token per startup**, written in the server's console, which the editor has to
  present.
- **Off by default**, and turned on with an argument (`--studio`) or from the server's
  window.
- **A port different from the game's**, and not a single admin route hanging from the game's socket.
- The regression guard that already sweeps the code looking for security marks should learn a
  ninth: that no admin route is registered without a token check.

---

## 4. What it is drawn with

The editor has to paint **isometric grids of 560 cells with colour layers, drag to
paint in batches, and dialog graphs**. That rules out WinForms, which is what there is today.

| | isometric map | dialog graph | cross-platform | learning curve |
|---|---|---|---|---|
| **WinForms** | by hand over `Graphics`, painful | by hand, very painful | no | already known |
| **WPF** | decent | decent | no | medium |
| **Avalonia** | decent | decent | yes | medium |
| **Local web** | `<canvas>`, its home ground | ready-made libraries | yes, free | medium, but known |

**Decided: Avalonia.** And the weighty reason is not the drawing, it is another one:

**There is no serialisation boundary.** The editor references `Jondo.Unity.World` and
`Jondo.Unity.Contract` as projects and uses `MapGeometry`, `Fighter`, `SpellEffect` and `Outcome`
**directly**. With a web interface, each of those types has to be mirrored in JSON by hand and
both sides kept in sync forever: the day `SpellEffect` gains a field —as it
gained `Delay` this week—, the editor finds out when it runs, not when it compiles.

And of this document's eight modules, **seven handle domain objects** and only one paints
pixels. It does not pay to build a whole HTTP layer for one module out of eight.

What is lost, and worth knowing beforehand:

- The isometric painting and the dialog graph are **more verbose** than in a `<canvas>`. They are done
  with `DrawingContext` over Skia and 560 rhombuses are no performance problem at all, but they have to be
  written.
- **There are no browser developer tools** to inspect the interface.
- It adds a dependency of some 30-40 MB to the deployment.

What is gained besides the types: a single language, a single solution, a single `dotnet build`, and
**the same framework serves the launcher**, which is the other half of the job.

About style: Avalonia supports XAML and also building the interface **from code**, which is how
today's launcher is made. One can start there and not learn XAML until it is needed.

---

## 5. The modules, one by one

For each one: **what there already is** —which is more than it seems— and **what is missing**.

### 5.1 Traffic and unknown packets

**There already is.** `GameNodeProxy` sees every frame. `Op.cs` knows each opcode's name.
`Network/UnknownPackets.cs` already deduplicates the unknown by the protobuf's **shape signature**.
`logs/gameserver_traffic.log` keeps 108 MB of traffic with hexadecimal. `tools/pcap.py` decodes
Wireshark captures. `tools/timeline.py` draws timelines.

**Missing.** A tap in the proxy that emits each frame as *server-sent events* to the browser, and a
timeline view filtered by opcode, direction and session. And for the unknowns registry to
go from being an in-memory list to a table with: shape, how many times, first and last time,
raw sample, and a **status** — unknown → named → documented → handled.

**The idea that makes this valuable in the long run: the key is the SHAPE, not the three letters.** Ankama
renames the opcodes in some patches. If the registry is stored by `jxw`, all the accumulated
knowledge evaporates on patch day. Stored by shape signature, **it survives**, and in fact it
becomes one more input for `protocolbuilder`'s matcher: a message we already know how to
identify by its shape is a free anchor. That code already exists and already computes the signature.

And comparing live against a real capture: taking one of our opcodes and the same one from the captures and
showing them field by field. It is exactly what has been done by hand five times this week.

### 5.2 Maps and cells

**There already is.** `MapGeometry` with the grid, the neighbours and the precomputed distances.
`datos/map_walkable_cells.json` with 17,211 maps, `map_fight_cells.json` with 17,222,
`map_neighbours.json` with the connections. `MapManager` serves them.

**Missing.** Painting. Four independent layers over the same grid —walkable, line of sight, blocked
in fights, blocked outside fights—, click to toggle and drag to paint a run. And
jumping to the four neighbours, which is how the world is really walked.

**Warning**: the scenery —a map's 2,181 elements, each one with its graphic and its matrix— is a
separate module and much more expensive. **It does not go into the first version.** It can be seen without being editable.

### 5.3 Interactives and teleports

This is, by far, **the one with the most value per hour invested**, and the one the emulator itself is
crying out for: today we declare the 3,719 passages with the zaap skill (114) and type 0,
when the real server uses 184, 339 and 361 with their own types; and 1,010 of the 1,124 missing passages
are discarded for not having a return element.

**There already is.** The `InteractiveTeleports` table with 3,815 rows, `TeleportManager`,
`datos/interactive_elements.json` with 9,840 maps, `datos/tipos_interactivos_3.6.10.10.json`, and two
navigation graph catalogues.

**Missing.** A view of two maps side by side: pick a cell on one, another on the other, pick the
type and the skill, and **tie them** — with the return created automatically, which is exactly what is missing
in the 1,010 discarded. That is the piece that makes possible houses with their own interior, new
passages and any custom content that does not exist on Ankama's map.

### 5.4 NPCs, actions and dialogs

**There already is.** 6,468 templates, 422 placed where Ankama has them on 202 maps with the cell and
direction from the captures, `Npcs.cs`, `Vendors.cs`, `TokenShops.cs`, and `datos/npc_shops.json`.

**Missing.** Placing a new one with the mouse. Changing its action — and there is something already learnt
in this project there: **the same NPC can be spawned with different actions**, so the action belongs
to the *spawn*, not to the template, and the data model has to reflect it.

And the dialogs, which have an important nuance that the rival tool itself points out well: **the
client keeps every line an NPC can say and every answer that can be given to it,
but never which goes with which.** That pairing has always belonged to the server. That is: the dialog
editor is not a luxury, it is the only place where that data can exist.

Besides, **the opening line is per map**: the same character in two places need not
say the same.

### 5.5 Quests

**There already is.** The catalogues: `quests.json`, `quest_steps.json`, `quest_objectives.json`,
`quest_objective_types.json`, `quest_step_rewards.json`, `quest_categories.json`.

**Missing.** Everything else. There is neither a quest engine nor a per-character progress table. This **is not
an editor module, it is a server feature** that also needs an editor. It is the
most expensive item on the list and it is better treated as a project of its own, not as one more tab.

### 5.6 Spells and effects

**There already is.** 17,113 spells, 34,823 levels, `SpellLevels.EffectsJson`, the effects catalogue, and an
engine that does not have a single hand-written spell: everything comes from the data.

**Missing.** Editing `EffectsJson` with an interface instead of by hand, and —the really useful part— a
view that says **which effects the engine knows how to apply and which fall into the «only for the
panel» branch**. Today that is only known by reading `EffectEngine.cs`, and it is the information that decides whether a
spell really works. Effect 108, healing, is the example: it seems to work and heals
nobody.

A simulator —casting a spell at a test target and seeing the consequences without setting up a
fight— is worth more than the editor itself.

### 5.7 Monsters and groups

**There already is.** 5,134 monsters, 38,744 placed groups, respawn, radius-2 validation on appearing.

**Missing.** Placing a group by hand on a specific map and picking its members. And something this week's
measurement made clear: a view of **which monsters cannot do anything** —the 401 without
spells, and the ones with their whole arsenal out of range— because they are content bugs that are not
seen while playing until you get one.

---

## 6. The launcher

It goes separately. Today it is hand-drawn WinForms, Windows only, and it does its job: identity chain per
client, eight accounts, embedded log, three languages.

What «more professional» would mean, specifically and in order of value:

1. **Cross-platform.** Today it does not start outside Windows.
2. **Automatic updates.** Today it is distributed by hand.
3. **Ranking and world status** — how many people are connected, who is first. Data the
   server already has and does not expose.
4. **News or server bulletin**, to tell what has changed without writing it on Discord.
5. **Decent account management**: recovery, password change, roles.

With Avalonia decided for the editor, the launcher **inherits the framework**: the same controls, the
same theme, the same window model. Porting it stops being a project and becomes an afternoon,
because the hard part —the identity chain, starting eight clients, the embedded log— is already
written and is not interface code.

And it settles point 1 of the list above in one go: Avalonia runs on macOS and Linux.

**It is not urgent.** The current launcher works; the editor does not exist. It goes last, but when it
comes it will be cheap.

---

## 7. Where to start

Ordered by *what it unblocks*, not by what one feels like doing.

| Phase | What | Why there |
|---|---|---|
| **0** ✅ | The content layer and per-row provenance. No interface: only the loader that merges the three layers and a couple of example files written by hand. | Nothing else can be saved until this exists. If done afterwards, every module has to be rewritten. |
| **1** ✅ | The framework and **read-only** views of maps and NPCs. | Zero risk, immediate value: today, to see why a creature does not attack, a Python script has to be written. And it validates the framework before letting it write anything. |
| **2** ✅ | Live traffic and the unknowns registry by shape. | It reuses what already exists and it is what speeds up day-to-day work the most. |
| **3** ✅ | Writing: NPC spawns, dialogs, monster groups. Not the actions: see below. | The cheapest content to create and the one most noticed while playing. |
| **4** ✅ | Interactives and teleports, with the automatic return. | Unblocks houses and custom content. It could be brought forward if that weighs more. |
| **5** ✅ | Map cells. | Useful, but only once there is content to place on top. |
| **6** ✅ | Spells, with the simulator. | |
| **7** | Quests. | A project of its own: it needs a server engine, not just an editor. |
| **8** | Launcher. | |

---

## 7 bis. What phase 2 taught

Three things this document took as good and were not.

### The unknowns registry had not noted anything down for months

Section 5.1 said «`Network/UnknownPackets.cs` already deduplicates the unknown by shape signature».
It deduplicated, yes, but over nothing: it opened the envelope with `ExtractGameNodePayload`, which **only looks at
the root's field 3**, and with `GetMessageTypeUrl`, which looks at 1 and 3. The client's frames go in
field **2**. Measured over the 72,879 frames of the traffic log:

```
  root 1 → 1 → 1     56,073   the server saying something
  root 2 → 1 → 1      8,974   the client asking
  root 3 → 1 → 1        481   the server answering
  root 1, loose       4,605   an Any recorded without its outer envelope
  root 1 → 1             41   the same, one layer further down
```

So every packet going through there came in without an opcode and with an empty body. After weeks
of play the table had **two rows, both «(sin opcode)» over an empty body**. The dispatcher
never noticed because it looks for the opcodes as *text* inside the frame, and that works whatever
the envelope.

The lesson is not the bug, it is how it hid: the only test there was checked that the code did
what was written. Now there are five that run against the real traffic file, and one of
them checks the two directions separately, which is exactly what a total count covered up.

### The key cannot be only the shape

Section 5.1 said «the key is the SHAPE, not the three letters». Measured, it does not hold as is.
Over the same 72,879 frames: **834 (opcode, shape) pairs** across **242 opcodes** and **664
shapes**.

- **Shape alone will not do.** Only 10 of the 664 shapes are shared by several opcodes — but
  they are the trivial ones (`(empty)`, `1:v`, `1:v,2:v`…) and between them they take **180 of the 242
  opcodes**. Filing by shape would dump half the protocol into ten drawers.
- **Opcode alone will not do either.** **59 of the 242** appear with more than one shape, and `jss` alone
  has **185**. Filing by opcode would hide exactly the variety the list is opened to see.

The key is **opcode + shape**. What the shape really does is *survive the patch*, but not by
being the key: when Ankama rotates the names, `protocolbuilder`'s structural matcher
produces the old-to-new table —that is where `datos/mapeo_3.6.10.10_a_3.6.10.11.tsv` came from— and the keys
are rewritten with it. A shape matching on both sides is what makes that mapping reliable.

And there is a valve: shape `*` means «this is about the opcode, whatever it carries», which is the
sensible way of saying something about the 185 shapes of `jss` at once.

### The HTTP frame tap was not needed

Section 5.1 asked for «a tap in the proxy that emits each frame as *server-sent events*». It is the
right answer when the one looking is a browser. Here it is not: the server **already writes every
frame** to `logs/gameserver_traffic.log` —that is where the 110 MB come from—, so the tap would be a
second copy of the same bytes, plus a socket to secure, plus a protocol to keep in
step, plus depending on the server being up.

Reading the file gives three things for free that the tap does not have: **it works with the server stopped**,
**it can look at what happened before the editor was opened**, and **it adds no surface**. What it costs is
polling instead of a push, which for a person reading a list makes no difference.

A measured and necessary detail: the log is written from two places that do not agree.
**27,565 of the 72,879 rows carry a length prefix** and the rest do not. Reading only one of the two
forms throws away a third of the file.

---

## 7 ter. What phase 3 taught

### The texts CAN be read, and that changes the dialog editor

Section 5.4 took for granted that a dialog editor would work with numbers. With numbers it is of
no use: nobody can decide that answer 6016 goes under line 3312 without reading either of the
two. It turns out the text is at hand, by two different paths because Ankama keeps the two
halves differently:

```
  an answer   dialogReplies [6016, 23739]  ->  Translations[23739]  ->  "Informarse sobre..."
  a line      dialogData    messageId 6169 ->  NpcMessagesDataRoot  ->  Translations[...]
```

`world.db` already carries **339,175 translations** in the `Translations` table. The answer carries its key
next to the id and resolves on its own. The line does not: its `messageId` is a `NpcMessageData` id, and it
has to go through `NpcMessagesDataRoot`, which is 16.8 MB of the dump. `tools/extraer_dialogos_npc.py`
distils it to **55,037 pairs, 1 MB**, which can be handed out.

With that, Snori Nairb stops being «3 messages and 39 answers» and becomes a readable list:
*«¡Alto ahí! Yo soy el que vigila esta ciudad...»* with *«¿Qué puedes contarme del conflicto entre
Bonta y Brakmar?»* under it. Now it can be decided.

### The tree needed session state, not just a file

The `ioy` with which the client picks an answer carries **the answer's id and nothing else**: neither which
NPC it comes from nor which line. Without noting down where the conversation is, there is no way of knowing which
line it leads to, and that is why the dialog could only have one line however much tree had been
written.

It goes in the session state and not in a static: with eight clients at once, a static would make one
player's answer advance another's conversation.

### Per-spawn actions are not what this document said

Section 5.4 held that «the same NPC can be spawned with different actions, so the
action belongs to the *spawn*». Measured against the wire, that **cannot be done from the server as it
is**: the right-click menu is painted by the client with the *template*'s `actions[]`, and the
`iov`'s `f1` is one of those numbers —it matches in the capture's 51 shop NPCs, 51 of 51—.
An NPC that does not declare the action does not even offer it.

So a per-spawn action can only **remove** from what the template already declares, not add.
Adding would require the map load to carry per-actor actions, and that has to be measured in a
capture before writing a line. It is left pending and marked as such, instead of half
implemented.

### Monster groups: two numbers, not the difference

A small and real detail. Startup said «N groups from content/» with placed minus
removed, and in the first real test —one group placed and another removed— the difference came out zero and the
line did not show. Exactly the startup where it is most needed to see that `content/` has touched something.

---

## 8. Risks, and what defuses them

| Risk | What defuses it |
|---|---|
| A regeneration wipes the hand work | Our layer is never regenerated, and it stores deltas |
| `world.db` becomes the place where things are edited | Explicit rule: the editor **does not write to `world.db`** |
| The measured gets mixed with the invented | Per-row provenance, visible in the interface, not in a comment |
| The editor is left exposed | Localhost, token, off by default, regression guard |
| The opcode registry dies with the next patch | Keyed by **shape**, not by the three letters |
| The editor becomes a second emulator | It only reads the server's state; it does not reimplement game rules |
| Two people edit at once | Out of scope: a single user, and git resolves the clashes |
| One starts with the flashy part —the scenery, the map— and abandons it | Phases 0 and 1 have nothing flashy, and they are the ones holding up the rest |

---

## 9. What it must NOT do

- **Not reimplement the client's catalogue.** The 21,748 items, the 17,113 spells and the 5,134
  monsters are Ankama's and are read. The editor edits **decisions**, not facts.
- **Not become an admin panel for the running game** —giving kamas, teleporting
  players—. Those are commands, they already exist, and mixing them in makes the editor a target.
- **Not replace `tools/`.** The Python scripts that extract from the client dump are still
  the way to redo the base layer. The editor is the layer on top.
- **Not make up data that can be measured.** If something is in the captures, it is measured; the editor is for
  what Ankama does not say.

---

## 10. What has to be decided before writing code

1. ~~Local web or Avalonia?~~ **Avalonia**, because of the shared types. The only thing that would turn it:
   wanting to open the editor from another machine, or handing it to someone without installing anything.
2. ~~Does the launcher share the framework?~~ **Yes**, it falls out of the previous decision.
3. **The content layer in versioned JSON or in a `studio.db`?** The recommendation is JSON, because of
   the pull requests.
4. **Interactives before NPCs?** It depends on whether being able to build houses or populating the world weighs more.
5. **Do quests go into this project or are they another one?** Here it is held that they are another one.
