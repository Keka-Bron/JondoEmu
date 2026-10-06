# From one player to many

> **Historical document.** The session plan of the first sections is already implemented and the
> authoritative description of the current code lives in `sessions.md`; the eight-account launcher is
> documented in `launcher.md`. This file is kept for the reasoning behind the phases still left,
> above all truly multiplayer fights, but its statements about the absence of
> `GameSession` or of broadcasts no longer describe the current state of the project.

A plan for the emulator to hold several sessions at once: several clients connected, each one
with its account and its character, seeing each other on the map and sharing the monsters; and a
launcher from which to open more than one account.

It was written from the state of the code before the sessions refactor. The lines and the
diagnoses below are a historical snapshot, not current references.

---

## 0. About `sessions.md`

`sessions.md` already describes the current implementation: `GameSession`, `SessionState`,
`SessionContext`, the active registry, the per-map broadcasts, the per-socket serialisation and the
real entry through port 5555. `launcher.md` completes the processes and accounts part. What follows
explains why that model was chosen and keeps the plan for the multiplayer aspects that are still not
finished.

---

## 1. What prevented it when the plan was written, in dependency order

The order mattered: each blocker hid the next one. Several entries of this list are already
solved; see `sessions.md` for the current state.

1. **`GameState` is static.** `GameState.cs:6`, with identity, position, kamas, experience,
   characteristics, inventory and equipment. As soon as a second client picks a character,
   `DatabaseManager.LoadCharacter` (`DatabaseManager.cs:1358`) overwrites the first one's identity.
2. **There is no registry of sessions or sockets.** No `NetworkStream` is kept anywhere:
   it is a local variable (`Network/GameNodeProxy.cs:71`) passed to the handlers. **Today it is
   literally impossible to send a packet to another player.**
3. **There is no per-map broadcast.** The `jss` only carries one's own actor
   (`Network/GameNodeProxy.cs:253`) and the `jpv` starts with `int totalActors = 1;`
   (`Handlers/MapLoadHandler.cs:104`).
4. **The fight is for one.** `GetCurrentFight()` returns `_activeFights.Values.FirstOrDefault()`
   (`Handlers/FightHandler.cs:884`), and `InitiateFightFromMobCollision` does `_activeFights.Clear()`
   before starting (`Handlers/FightHandler.cs:40`). On top of that the round, the action counter and the
   per-turn cast limits are global statics (`:1059`, `:1062`, `:2133`, `:2134`).
5. **`SaveCurrentCharacter()` is not told whom to save.** `DatabaseManager.cs:1412` builds the
   `UPDATE` by reading `GameState`. With two players this does not lose data: it **mixes it between
   accounts**, which is worse.

And two that are not in that chain but bite all the same:

6. ~~**Actor ids are recomputed on every map load and per client.** The NPCs
   start at `-20000` and go down (`Handlers/MapLoadHandler.cs:112`) and the monsters carry on the count
   from where the NPCs left it (`:122`). Two players on the same map would get different numbers
   for the same creature, so no "mob X has moved" notice would be
   coherent.~~ Done, phase 4. And it was worse than it says here: the same client already got two
   different numbers for the same group, one in the `jss` and another in the `jpv`.
7. **SQLite is in WAL (`DatabaseManager.cs:27` and `:98`) but without `busy_timeout`.** With two
   writers, the second gets an immediate `SQLITE_BUSY` instead of waiting. And the item uid
   is taken with `SELECT MAX(Uid)` (`Handlers/NpcHandler.cs:326`), which is a textbook race.

---

## 2. The model to get to

```
one game TCP connection
   └── one Session
         ├── one account and one server      (from the ticket, already exists)
         ├── zero or one character
         ├── one CharacterState              (what GameState is today)
         └── one socket with its write lock
```

And above them, three WORLD registries, which belong to nobody in particular:

```
World
 ├── Sessions          who is connected, and on which map
 ├── Maps              which actors there are on each map: players, NPCs, monster groups
 └── Fights            the fights in progress, each one with its participants and its sessions
```

The rule that decides where each piece of data goes, and that is worth keeping at hand when migrating each file:

* If the player changes it and it only affects them, it belongs to the **session** (position, inventory, kamas,
  open dialogs, drafts).
* If two players have to see it the same, it belongs to the **world** (a map's monster groups,
  who is on a map, a fight).
* If it never changes, it is a **table** and can stay static (templates, translations,
  the experience table, the effects catalogue).

---

## 3. The phases

Each phase leaves the emulator working with one player. That is non-negotiable: no three-week
refactor with the server broken in the middle.

### Phase 1 — The session, without changing behaviour

Put the state into an object, without touching anything multiplayer yet.

* `Session` is born with `Id`, `Socket`, `AccountId`, `ServerId`, `Character` (the `CharacterState`)
  and `InWorld`.
* `GameState` **is not deleted in one go**: it becomes a façade that forwards to the current session.
  That way its 425 uses keep compiling and get migrated file by file, starting with the ones that have
  the most: `FightHandler` (96), `DatabaseManager` (47), `StatsHandler` (44), `GameNodeProxy` (37).
* The session reaches the handlers **as a parameter**, not through `AsyncLocal`.

  Here I disagree with my colleague's document. `AsyncLocal` compiles without touching signatures, yes, but
  it breaks exactly where it is needed most: the monsters' AI, the turn timers
  (`Handlers/FightHandler.cs:1820`) and group respawning run OUTSIDE the socket's thread and would
  be left without context. And those are precisely the three background jobs a multiplayer
  server needs. Changing signatures is heavier and it is the right thing.
* `SaveCurrentCharacter()` becomes `SaveCharacter(CharacterState)`. It is a small change and
  it removes the worst corruption point.

Proof that the phase is right: a player keeps playing exactly the same, and `GameState` no longer
has any mutable field of its own.

### Phase 2 — The session registry and safe writes

* `SessionRegistry` (`Network/SessionRegistry.cs`, only tickets today) grows a second
  dictionary of live sessions, and an `OnMap(mapId)` returning a **copy**.
* In `GameNodeProxy`, registration on connecting and removal in a `finally`, saving the character on closing
  —which today is done nowhere.
* **One write lock per socket.** Today `Protocol/NetworkMessage.cs:108-109` writes the
  length and the body in two calls: with two threads writing to the same client you get
  `length A, length B, body A, body B` and the client goes out of sync for good. It is
  fixed by building a single buffer and serialising with one semaphore per socket. Without this, any
  broadcast corrupts the connection.

### Phase 3 — Seeing each other on the map

* A `MapRegistry` that knows who is on each map, fed on entering and on leaving.
* `jsn` to the others when someone arrives; `jsd` to the others when they leave —today the `jsd` exists but
  is sent to oneself (`Handlers/WorldMoveHandler.cs:204`, `Handlers/ZaapTravelHandler.cs:170`).
* The entry `jpv`/`jss` stops carrying one actor and carries whoever is there
  (`Handlers/MapLoadHandler.cs:104`).
* The movement `jsj` is broadcast. The comment in `Handlers/WorldMoveHandler.cs:56`
  already says the `jsj` is how another client finds out "and there is nobody else here": well, now
  there is.
* The `kqp` chat stops being an echo (`Handlers/ChatHandler.cs:33`) and goes to the map or the channel.

### Phase 4 — Stable actor ids — DONE

This has to be fixed before touching the monsters or nothing will match between clients.

* A single actor id allocator per world, **assigned on appearing** and not on loading the map.
* The ranges, separate and non-overlapping: players by their `CharacterId`, NPCs in one band, mobs in
  another, summons in another.
* The mobs keep their id in the `MobGroup`, they do not compute it in `MapLoadHandler`.

What there was, and what turned out to be broken **already with one player**: the client asks for both
actor messages when loading a map —the `jss` with the `jrh` and the `jpv` with the `kkr`— and both handed out
different numbers for the same creature. On the Amakna NPCs map, measured: the `jss` gave
the two monster groups −1011567 and −1011566, which are their `MobId`, and the `jpv` gave them
−20052 and −20053, because it numbered them by their position behind the 52 NPCs. The client sends back
the last one it got, so attacking fell into a `mobs.FirstOrDefault()` in
`Handlers/FightHandler.cs` and the player fought another group —and on winning it was that other one that
disappeared from the map—.

How it ends up:

* `Managers/ActorIds.cs` is the only one handing them out, with the bands and with `EsJugador`/`EsNpc`/
  `EsMonstruo`. Monsters are requested with `Interlocked`: the spawner of an empty map runs on the
  thread of the player arriving, and two arriving at once did the same `_id--`.
* At startup, `ReservarMonstruosHasta` moves the cursor below the lowest `MobId` written
  in the database, so the groups spawned on the fly do not tread on the seeded ones. Before they were
  two fixed bands, −1000000 and −2000000, and with more than a million seeded groups they crossed.
* The `jpv` comes out of `MapLoadHandler.ConstruirJpv`, separated from the sending so that it can be compared with the
  `jss` in the test bench. It no longer computes any id: the group's is its `MobId` and the NPC's is
  the one `Managers.Npcs` gave it at startup.
* The NPCs were read twice, and the ids came out of both reads: `Managers/Npcs.cs` with
  `ORDER BY MapId, Id` and `DatabaseManager.GetNpcSpawnsForMap` **with no `ORDER BY` at all**. Whether
  they matched depended on the plan SQLite chose. Now there is a single list, and on the way the
  `jpv` saves a whole walk over `NpcSpawns` on every map load.
* Out goes `PatchJpvEnteringPacket` from `Network/GameNodeProxy.cs`, which nobody called and which carried three
  character ids from the captures written by hand.

And two things from other phases that were brought forward because they were in the way here: `MobSpawnManager`'s
`_mapMobs` goes behind a lock —it is a bare `Dictionary` touched from each
player's thread— and the `_activeFights.Clear()` in `Handlers/FightHandler.cs:40` now removes only
the player's own fight. The rest of phases 5 and 6 is still untouched.

### Phase 5 — Shared monsters

* `MobSpawnManager` goes from plain `Dictionary`s (`Managers/MobSpawnManager.cs:40-41`, mutated without
  a lock from three places) to concurrent collections, or better, to a single actor per map that
  serialises the changes.
* A group in a fight is marked as busy: today `GetMobAtCell` (`:361`) would give the same group to
  two players clicking at once.
* When a group dies, `jsd` of the actor to those on the map; on respawning, `jsn`.
* The randomly generated groups (`GenerateDynamicMobsForMap`, `:265`) are never written to the database.
  Decide whether that is fine —I would say yes, let them be regenerated at startup— but leave it said.

### Phase 6 — Fights for several

* Remove the `_activeFights.Clear()` in `Handlers/FightHandler.cs:40` and `:265`.
* `GetCurrentFight()` dies; the fight is looked up by session or by fighter.
* The round, the action counter and the per-turn limits (`:1059`, `:1062`, `:2133`, `:2134`)
  move inside `FightInstance`, which is already one object per fight and only lacks this and the list
  of sessions.
* A send to the whole fight, instead of writing to the socket of whoever acted.
* And what does not exist at all today: spectators, joining a fight already started, and placement with
  several per side.

### Phase 7 — The database for real

* `busy_timeout` in the connection string. Today there is none in the whole tree.
* A single logical writer per character, or transactions that group the multi-step operations
  —moving an item to a chest, buying— that today go loose.
* Out goes the `SELECT MAX(Uid)` in `Handlers/NpcHandler.cs:326`: a counter with `Interlocked` or an
  autoincrement column.
* Saving on disconnect and periodic saving.

### Phase 8 — The multi-account launcher

What ties it to one account today:

* `HaapiServer.ActiveAccount` (`Network/HaapiServer.cs:207`) is **a single static field** that
  each `SignIn` overwrites (`LauncherService.cs:70`), and every HAAPI and Zaap answer comes out of
  it. The second login changes the first one's account underneath it.
* `Accounts.GameToken` is one column per account: a new login invalidates the previous token.
* `--instanceId 1` and `ZAAP_INSTANCE_ID=1` are literals (`LauncherService.cs:149`, `:165`), just
  like `--port 15881`; Zaap's named pipe is named after the port number
  (`Network/ZaapServer.cs:178`), so two clients fight over the same channel.
* The window keeps one `_token` and one `_account` (`UI/LauncherWindow.cs:37-38`).
* `logs/gameserver_traffic.log` is written with `File.AppendAllText` **without a lock**
  (`Network/GameServerProxy.cs:281`): with two sessions it interleaves and gets lost.

The plan:

1. The window goes from one session to a **list of connected accounts**, each one with its token, its
   client process and its state.
2. HAAPI and Zaap stop having an active account: they resolve **by the token or by the instanceId** that
   reaches them. That is the underlying change of this phase.
3. `instanceId` and the Zaap port, one per session. It is worth looking at `zaap-start.bat`, which is what
   Ankama uses for multi-account and already passes a `-logFile` per account; today it is not used.
4. The traffic log, one per session, or a single one with a lock and with the session on each line.
5. Check whether `Dofus.exe`/MelonLoader carry an instance guard of their own and whether the
   `Cliente 3.6.10.10` folder can take two processes (they share `MelonLoader/Latest.log` and
   `UserData/MelonPreferences.cfg`). This has to be **tested**, not assumed.

---

## 4. Where I would start

Phases 1 and 2 together, and touch nothing multiplayer until they are done. They are 80% of the boring work
and 100% of the risk: while `GameState` is static and the socket writes are not
serialised, anything built on top inherits both bugs.

Phase 4 (actor ids) is small and goes before 3 and 5 even if it does not look like it: without stable
ids, the notices between clients are incoherent and debugging is done blind.

And a test worth setting up early: **two fake clients** —there is already a
`tools/cliente_falso.py`— connected at once, walking the same map, checking that each one
receives the other's actors. Without it, each phase is tested by hand and each round takes longer.
