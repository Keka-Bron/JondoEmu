# Game sessions and map broadcasts

How Jondo supports several game clients at the same time without sharing one character's state
with another.

This document describes the session model introduced in August 2026. It covers the connection on
the game protocol (port 5555), after the connection server has issued a ticket. Authentication and
the wire format are described separately in `NOTAS_MIGRACION_AUTH.md` and `protocol.md`.

---

## 1. The problem this solves

The original emulator had one static `GameState`. Its properties represented *the* current
character:

- account and character identity;
- map, cell and orientation;
- level, experience, characteristics and kamas;
- inventory and equipped items;
- fight state;
- temporary UI state such as an open zaap or wardrobe draft.

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

### 2.1 `SessionState`

File: `Jondo.Unity.Launcher/SessionState.cs`

`SessionState` is an ordinary sealed class. None of its player properties are static. Every
`GameSession` constructs its own instance.

It owns:

- the selected character's identity and look;
- position and kamas;
- experience and characteristics;
- fight flags;
- inventory and equipped-item caches;
- session-local dialog state for zaaps, chests, the haven-bag editor and wardrobe drafts.

Inventory and equipment operations retain an instance lock. The lock now protects one character's
collections instead of serialising unrelated clients around one global collection.

The old `GameState.cs` and its static class no longer exist. All former accesses resolve the state
from the current session.

### 2.2 `GameSession`

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

### 2.3 `SessionContext`

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

Access without a bound game session throws immediately. This is intentional: silently falling back
to shared defaults would recreate the original bug.

New APIs may accept `GameSession` explicitly. Existing APIs can be migrated gradually because both
forms address the same session object.

### 2.4 `SessionRegistry`

File: `Jondo.Unity.Launcher/Network/SessionRegistry.cs`

The registry now owns two independent concurrent dictionaries:

```text
ticket string -> short-lived Ticket
session UUID  -> active GameSession
```

Both use `System.Collections.Concurrent.ConcurrentDictionary`. Tickets remain single-use and expire
after five minutes. Active sessions remain registered until their socket loop ends.

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

Implemented in `Jondo.Unity.Launcher/Network/GameNodeProxy.cs`.

### 3.1 Connection and registration

When the game-node protocol starts on a socket:

1. `GameNodeProxy` creates `new GameSession(stream)`.
2. It binds the session to `SessionContext` for the whole asynchronous loop.
3. It registers the session in `SessionRegistry`.
4. A `finally` block always removes it when the loop exits, including exceptions and invalid
   packets.

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
participant. Before that point it cannot receive chat, movement or actor broadcasts.

### 3.4 Leaving and disconnecting

Returning to character selection and losing the connection both call the same departure path:

1. capture the current map and character id;
2. set `IsInWorld` to false;
3. broadcast the actor-left packet to the remaining sessions on the old map;
4. unregister the session when the connection itself ends.

Marking the session out of the world before broadcasting prevents it from being selected as a
recipient and prevents later concurrent map broadcasts from targeting a departing client.

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

All code paths using `NetworkMessage.WriteFrameAsync`, including `GameSession.SendAsync`, share the
same protection.

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
actor leaving its old map. Chat and movement confirmations include the sender because the client
expects to receive its own authoritative event.

### Current map broadcasts

| Event | Packet | Recipients |
|---|---|---|
| Normal chat | `kqp` | Every in-world session on the sender's map, including sender |
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
5. Use `session.SendAsync` for a known recipient.
6. Use `BroadcastToMapAsync` for an event visible to nearby players.
7. Decide explicitly whether the sender belongs in the audience.
8. Do not call `NetworkStream.WriteAsync` directly for a framed game packet.

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

- one `SessionState` instance per game connection;
- atomic ticket consumption;
- thread-safe active-session registration and snapshots;
- ordered, non-interleaved frame writes per socket;
- concurrent delivery across different sockets;
- removal of failed or disconnected recipients;
- no C# dependency on the former global `GameState`.

It does not make every property update transactional. Each socket loop normally handles that
session's client packets sequentially. Collection operations that can be observed by other work
remain locked inside `SessionState`; database operations retain SQLite as the persistence boundary.

`SessionContext` relies on normal .NET execution-context flow. Code that deliberately suppresses
`ExecutionContext`, or queues work outside the session lifetime, must pass `GameSession` explicitly.
Long-running background jobs must never assume that a session context still exists.

---

## 8. Validation

The refactor was checked by:

- searching the C# tree for the former `GameState` dependency;
- checking the Git diff for whitespace errors;
- compiling `Jondo.Unity.Launcher` and its project references on .NET 10.

The build completed with zero errors. The remaining warnings were unrelated package-audit/network
warnings, an existing redundant `System.Text.Json` package reference, and an existing unused local
variable in the traffic logger.

A useful runtime regression test is to connect two accounts on the same map and verify, in order:

1. each client keeps its own inventory, stats and position;
2. each sees the other actor after `jrh`;
3. movement and normal chat appear on both clients;
4. changing map removes the actor only from the old map;
5. disconnecting one client removes its actor without affecting the other session.
