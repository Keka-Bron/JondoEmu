# Game sessions and map broadcasts

How Jondo supports several game clients at the same time without sharing one character's state
with another.

This document describes the session model introduced in August 2026. It covers the launcher-to-game
identity chain, the game protocol on port 5555 and the socket/session lifecycle after the connection
server issues a ticket. The team UI is described in `launcher.md`; authentication and framing are
described separately in `NOTAS_MIGRACION_AUTH.md` and `protocol.md`.

---

## 1. The problem this solves

The original emulator had one static `GameState`. Its properties represented *the* current
character:

- account and character identity;
- map, cell and orientation;
- level, experience, characteristics and kamas;
- inventory and equipped items;
- fight state;
- temporary UI state such as an open zaap, NPC shop or wardrobe draft.

That model works while exactly one client is connected. With two clients, loading the second
character replaces the first character's values. A packet later handled for the first socket then
reads the second character's id, map or inventory. Locks around the collections cannot fix this:
they prevent simultaneous writes to one collection, but both clients still address the same
collection.

The required ownership rule is now:

```text
one TCP game connection
    -> one GameSession
        -> one account and server
        -> zero or one selected character
        -> one SessionState
        -> one NetworkStream
```

Server-wide data remains shared. Map definitions, item catalogues, database access, listeners and
the ticket store do not belong to one player and therefore stay static.

---

## 2. The types involved

### 2.1 `ClientLaunchRegistry`

File: `Jondo.Unity.Launcher/Network/ClientLaunchRegistry.cs`

This is the launcher-process registry, not the map-session registry. Every Dofus process receives
its own `InstanceId`, random hash, account id, launcher token and language. The local Zaap service
validates the `(InstanceId, Hash)` pair and creates a random `gameSession` that still resolves to
that exact launch. There is no process-wide "last account" lookup.

It enforces at most eight active Dofus processes and refuses to launch the same account twice. The
entry is removed when the child process exits. See `launcher.md` for the full UI and identity flow.

### 2.2 `SessionState`

File: `Jondo.Unity.Launcher/SessionState.cs`

`SessionState` is an ordinary sealed class. None of its player properties are static. Every
`GameSession` constructs its own instance.

It owns:

- the selected character's identity and look;
- position and kamas;
- experience and characteristics;
- fight flags;
- inventory and equipped-item caches;
- session-local dialog state for zaaps, chests, NPC shops, the haven-bag editor and wardrobe drafts;
- session-owned equipment, spell-choice and spell-bar caches.

Inventory and equipment operations retain an instance lock. The lock now protects one character's
collections instead of serialising unrelated clients around one global collection.

`GameState.cs` still exists as a compatibility facade for legacy handlers, but stores no player
fields. Every property forwards to `SessionContext.State`.

### 2.3 `GameSession`

File: `Jondo.Unity.Launcher/Network/GameSession.cs`

`GameSession` is the connection-level object. It contains:

| Member | Meaning |
|---|---|
| `Id` | Fresh internal UUID for this connection |
| `Stream` | The `NetworkStream` belonging to this client's socket |
| `AccountId` | Account obtained by redeeming the connection ticket |
| `ServerId` | Selected game server from the same ticket |
| `State` | This session's `SessionState` instance |
| `CharacterId` / `MapId` | Convenience views over `State` |
| `IsAuthenticated` | The ticket has bound an account |
| `HasCharacter` | A character has been loaded into the state |
| `IsInWorld` | The character has entered the world and belongs to a map audience |

The three stages are deliberately separate. An open socket is not necessarily authenticated, and
an authenticated client may still be on the character list. Only `IsInWorld` sessions receive map
broadcasts.

`BindAccount` rejects invalid account ids. `EnterWorld` rejects a session without both an account
and a loaded character. These checks keep invalid partial sessions out of map-level operations.

`GameSession.SendAsync(packet)` is the standard primitive for sending one framed packet to one
specific client.

### 2.4 `SessionContext`

The current handler surface predates `GameSession`: most methods accept a `NetworkStream` and a
payload, not a session. Changing every handler and every packet builder signature in one step would
make the protocol migration unnecessarily risky.

`SessionContext` is the compatibility bridge. It stores the current `GameSession` in an
`AsyncLocal<GameSession>`. The context flows through `await`, but each asynchronous connection flow
has a different value.

Important distinction: `SessionContext` is static, but it stores **no global character data**. It
only resolves which session owns the currently executing handler. The mutable data remains in that
session's `SessionState`.

Code inside the game pipeline reads:

```csharp
SessionContext.State.CharacterId
SessionContext.State.MapId
SessionContext.Current.AccountId
```

Startup code, catalogue loaders and tests may run without a client and use the socketless fallback
session named `Suelta`. A real game connection must never use it. Both game entry paths create a
`GameSession`, and the dispatcher explicitly pushes the stream-owning session again before every
packet. It also rejects a `GameSession` whose `Stream` is not the stream passed to the dispatcher.

New APIs may accept `GameSession` explicitly. Existing APIs can be migrated gradually because both
forms address the same session object.

### 2.5 `SessionRegistry`

File: `Jondo.Unity.Launcher/Network/SessionRegistry.cs`

The registry now owns two independent concurrent dictionaries:

```text
ticket string -> short-lived Ticket
session UUID  -> active GameSession
```

Both use `System.Collections.Concurrent.ConcurrentDictionary`. Tickets remain single-use and expire
after five minutes. Active sessions remain registered until their socket loop ends.

Registration is independently capped at eight game sockets. This protects the server even if a
client reaches port 5555 without having been started by the native launcher.

The public active-session operations are:

- `Register(session)`;
- `Unregister(session)`;
- `TryGet(sessionId, out session)`;
- `FindByCharacter(characterId)`;
- `OnMap(mapId)`;
- `BroadcastToMapAsync(mapId, packet, exceptSessionId)`.

`OnMap` returns an array snapshot. Callers never enumerate a mutable dictionary view while awaiting
network writes.

---

## 3. Session lifecycle

Implemented jointly by `Jondo.Unity.Launcher/Network/GameServerProxy.cs` and
`Jondo.Unity.Launcher/Network/GameNodeProxy.cs`.

### 3.1 Connection and registration

The real client reaches the game through port 5555. `GameServerProxy` first distinguishes the bare
connection-server protocol from wrapped `type.ankama.com/...` game messages. When the second,
game-phase TCP connection arrives, `HandleBoundGameSessionAsync`:

1. creates `new GameSession(stream)`;
2. registers it in `SessionRegistry` and `GameNodeProxy.SesionesVivas`;
3. binds it to `SessionContext`;
4. passes both the session and its owning stream to the game dispatcher;
5. saves, broadcasts departure and unregisters it from a `finally` block.

The dedicated game-node listener on port 5556 performs the same creation, registration and cleanup
before calling the shared dispatcher.

Inside `HandleGameNodeSessionAsync`, every loop iteration pushes the explicit session again before
examining that packet. This prevents a real socket from falling through to the shared socketless
fallback. The session/stream pair is validated when the dispatcher starts.

This detail fixed the most serious multi-client failure: port 5555 previously called the dispatcher
without creating a `GameSession`. Both clients therefore used `Suelta`, and the last character
loaded supplied the identity, appearance and map for packets arriving on either socket.

Registration happens before authentication so the registry accurately represents open game
connections. Unauthenticated sessions cannot enter `OnMap`, because they are not `IsInWorld`.

### 3.2 Ticket to account

The connection server first issues a single-use ticket containing `AccountId` and `ServerId`. The
new game connection presents it in `kqz`.

`SessionRegistry.Redeem` atomically removes the ticket. On success, `GameNodeProxy` calls
`session.BindAccount(accountId, serverId)`. An invalid, expired or already consumed ticket closes
the session.

This is why account identity is not inferred from a process-wide "last login": every game socket
is bound by its own ticket.

### 3.3 Character selection

The selected character id is checked against the session's account before loading it. Database and
inventory loading write into `SessionContext.State`, which is the selecting session's state.

After the character exists and has loaded successfully, `session.EnterWorld()` marks it as a map
participant. Before that point it cannot receive movement or actor broadcasts selected by
`SessionRegistry.OnMap`.

### 3.4 Leaving and disconnecting

Returning to character selection and losing the connection both perform the same departure work:

1. capture the current map and character id;
2. broadcast actor-left to the remaining sessions, explicitly excluding the departing UUID;
3. set `IsInWorld` to false;
4. save with that session explicitly pushed;
5. unregister the session when the connection itself ends.

---

## 4. Sending packets safely

### 4.1 One session

Given a known `GameSession`:

```csharp
await session.SendAsync(packet);
```

The packet is the protobuf envelope, without the TCP length prefix. `NetworkMessage.WriteFrameAsync`
adds that prefix.

### 4.2 Why socket writes are serialised

A handler response and a map broadcast can target the same socket at the same time. Previously the
byte-array overload wrote the length and payload in two separate calls. Two concurrent writers
could therefore produce this invalid stream:

```text
length A, length B, payload A, payload B
```

`Protocol/NetworkMessage.cs` now:

1. assembles the prefix and payload into one frame buffer;
2. obtains a `SemaphoreSlim` associated with the destination `Stream` through a
   `ConditionalWeakTable`;
3. writes the complete frame while holding that stream's semaphore.

Different sockets still write concurrently. Only writes to the same socket are ordered. The weak
table does not keep a closed stream alive.

`WriteFrameAsync` accepts a bare protobuf envelope and adds the VarInt length prefix. Captured data
that already contains its prefix uses `WriteRawFrameAsync`. Both APIs share the same per-stream
gate, including `GameSession.SendAsync`. Direct `NetworkStream.WriteAsync` is forbidden for framed
game traffic because it bypasses that protection.

---

## 5. Broadcasting on a map

The generic API is:

```csharp
int delivered = await SessionRegistry.BroadcastToMapAsync(
    mapId,
    packet,
    exceptSessionId: optionalSessionId);
```

It works as follows:

1. take an `OnMap(mapId)` snapshot;
2. optionally remove one session, normally the sender;
3. send to all selected sockets concurrently;
4. count successful deliveries;
5. unregister a target whose send fails.

The optional exclusion matters for packets already sent directly to the moving client, such as an
actor leaving its old map. Movement confirmations include the sender because the client expects to
receive its own authoritative event.

### Current map broadcasts

| Event | Packet | Recipients |
|---|---|---|
| Current movement protocol | `jsj` | Every in-world session on the map, including mover |
| Legacy movement protocol | `joo` | Every in-world session on the map, including mover |
| Map transition | `jsd` actor-left | Other sessions on the old map; mover receives its direct transition sequence |
| Arrival / actor discovery | `jsn` | Existing players learn about the arrival; the arrival learns about existing players |
| Character-list return or disconnect | `jsd` actor-left | Remaining sessions on the old map |

Map membership is evaluated from the live `SessionState.MapId` plus `IsInWorld`; there is no second
map index to update and accidentally leave stale.

---

## 6. Writing a multi-session-safe handler

For a handler already called inside `GameNodeProxy`, use the bound session:

```csharp
public static async Task HandleSomething(NetworkStream stream, byte[] payload)
{
    GameSession session = SessionContext.Current;
    SessionState state = session.State;

    state.CellId = ReadCell(payload);
    DatabaseManager.SaveCurrentCharacter();

    byte[] packet = BuildSomething(state.CharacterId, state.CellId);
    await SessionRegistry.BroadcastToMapAsync(state.MapId, packet);
}
```

For a helper that can reasonably accept dependencies explicitly, prefer:

```csharp
public static byte[] BuildSomething(GameSession session)
{
    return BuildPacket(session.CharacterId, session.State.CellId);
}
```

Use these rules:

1. Never add a static field for a current account, character, map, dialog or inventory.
2. Put persistent character data in the database and its live copy in `SessionState`.
3. Put connection-only state in `GameSession` or `SessionState`.
4. Keep immutable catalogues and genuinely server-wide registries shared.
5. Bind the stream-owning `GameSession` before legacy code reads `GameState`.
6. Use `session.SendAsync` for a known recipient.
7. Use `BroadcastToMapAsync` for an event visible to nearby players.
8. Decide explicitly whether the sender belongs in the audience.
9. Do not call `NetworkStream.WriteAsync` directly for a framed game packet.
10. Pass `GameSession` explicitly to background work that outlives a packet scope.

### What may remain static

Static does not automatically mean unsafe. These are valid shared concerns:

- TCP listeners and server cancellation tokens;
- immutable protocol constants and packet templates;
- map, spell, item and appearance catalogues;
- the concurrent ticket/session registry;
- world-level fight and mob registries, when their own keys identify independent instances.

The test is ownership: if changing the value for player A would be observable by player B without
an intentional broadcast or shared-world rule, it belongs to a session.

---

## 7. Concurrency guarantees and limits

The implementation guarantees:

- at most eight active launcher processes and eight registered game sockets;
- one `SessionState` instance per game connection;
- a validated `GameSession` / `NetworkStream` pair at dispatcher entry;
- explicit session rebinding before every incoming game packet;
- atomic ticket consumption;
- thread-safe active-session registration and snapshots;
- ordered, non-interleaved frame writes per socket;
- concurrent delivery across different sockets;
- removal of failed or disconnected recipients;
- a compatibility `GameState` facade with no player storage of its own.

It does not make every property update transactional. Each socket loop normally handles that
session's client packets sequentially. Collection operations that can be observed by other work
remain locked inside `SessionState`; database operations retain SQLite as the persistence boundary.

`SessionContext` relies on normal .NET execution-context flow. Code that deliberately suppresses
`ExecutionContext`, or queues work outside the session lifetime, must pass `GameSession` explicitly.
Long-running background jobs must never assume that a session context still exists.

---

## 8. Validation

`RegressionGuardTests` checks:

- isolation of two launcher accounts through distinct Zaap game sessions;
- rejection of a ninth launcher client;
- separation of equipment, spell choices, spell bars and dialog state between two sessions;
- non-overlapping concurrent raw and dynamically framed writes to one stream.

The project is also checked with `git diff --check` and compiled with its project references on
.NET 10.

The build completed with zero errors. The remaining warnings were unrelated package-audit/network
warnings, an existing redundant `System.Text.Json` package reference, and an existing unused local
variable in the traffic logger.

A useful runtime regression test is to connect two accounts on the same map and verify, in order:

1. each client keeps its own inventory, stats and position;
2. each sees the other actor after `jrh`;
3. movement and normal chat appear on both clients;
4. changing map removes the actor only from the old map;
5. disconnecting one client removes its actor without affecting the other session.

When diagnosing a map mismatch, the movement log includes the socket-session UUID, character id,
character name and session map. Distinct clients must also show distinct
`[Game Server] Socket bound to session ...` lines on port 5555.
