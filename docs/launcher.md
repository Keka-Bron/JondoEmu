# Native launcher and eight-client teams

How the WinForms launcher stores a team of accounts, starts up to eight independent Dofus
processes and preserves account identity through Zaap, authentication and the game socket.

The game-session implementation is documented in `sessions.md`.

---

## 1. User workflow

The launcher no longer represents one "last logged-in account". It maintains a persistent team of
up to eight account profiles.

The intended workflow is:

1. sign in to add an account to the team;
2. use **Add another account** to return to the login form without discarding the team;
3. click account rows to select any subset;
4. use **Select all / Deselect all** when appropriate;
5. press **Launch selected** to start one Dofus process per selected inactive account.

Selection and connection state are separate. A selected account is one the user wants to launch;
an account marked `in game` already owns a running Dofus process and is skipped. The same account
cannot be launched twice concurrently.

Selected inactive accounts may be removed from the team. Active accounts are protected from
removal until their client process exits.

---

## 2. Persistent preferences

File: `Jondo.Unity.Launcher/UI/LauncherPreferences.cs`

Preferences live outside the emulator installation at:

```text
%APPDATA%\Jondo\lanzador.cfg
```

The file remembers:

- launcher/game language (`es`, `en` or `fr`);
- an explicitly selected `Dofus.exe` path;
- at most eight account profiles;
- each profile's account id, login, nickname, token and selection state.

The account list is serialized as JSON and Base64-encoded into the `cuentas` setting. Base64 is a
storage encoding, not encryption. Passwords are not stored, but saved tokens must still be treated
as private user data.

On startup, valid profiles are restored and their tokens are registered with
`ClientLaunchRegistry`. A malformed account payload is ignored rather than preventing the launcher
from opening. A stored client path is used only while the file still exists.

---

## 3. Team UI state

File: `Jondo.Unity.Launcher/UI/LauncherWindow.cs`

Each row displays the nickname and account id, its selection checkbox and an `in game` marker when
`ClientLaunchRegistry.IsActive(accountId)` is true. The summary distinguishes saved accounts,
selected accounts and active Dofus processes.

The maximum is checked while adding a profile and while launching. Re-authenticating an account
already in the list refreshes its token instead of creating a duplicate row.

`LaunchGame` takes a snapshot of the selected rows and calls `LauncherService.LaunchClient` once
for each inactive account. A failure for one account is reported with that nickname and does not
erase or silently substitute the other accounts.

---

## 4. Authentication in the launcher

File: `Jondo.Unity.Launcher/LauncherService.cs`

`SignIn` validates credentials against `auth.db`. On success it:

1. generates a random launcher token;
2. persists the game token for that account;
3. registers `token -> accountId` in `ClientLaunchRegistry`;
4. returns the account id, nickname and token to the native UI.

There is deliberately no `ActiveAccount` field. Every later operation starts from the token of the
specific team row being launched.

Account creation is also called directly through `LauncherService`. The old web-launcher login,
register and launch HTTP routes have been removed from `HaapiServer`.

---

## 5. Starting a client process

`LauncherService.LaunchClient(token)`:

1. resolves the token to one account id;
2. finds the configured `Dofus.exe`;
3. rejects a duplicate active account or a ninth active client;
4. generates a fresh random hash;
5. registers a `ClientLaunchRegistry.Launch` with a unique `InstanceId`;
6. starts Dofus with that instance and hash;
7. removes the launch if process creation fails or when the process exits.

Important arguments include:

```text
--instanceId <unique integer>
--hash <random per-launch hash>
--port 15881
--connectionPort 5555
--langCode <es|en|fr>
```

Equivalent `ZAAP_PORT`, `ZAAP_HASH`, `ZAAP_GAME`, `ZAAP_RELEASE`, `ZAAP_INSTANCE_ID` and
`ZAAP_CAN_AUTH` environment variables are set for the child. Every client receives its own hash
and instance id even though all clients use the same local listeners.

The process starts with the primary screen's working dimensions. Once Unity creates its window,
the launcher waits briefly and maximizes it. Launching a game client also stops launcher music.

---

## 6. Identity flow from row to socket

```text
saved team row
  -> launcher token
  -> Launch(AccountId, InstanceId, Hash)
  -> local Zaap connect(InstanceId, Hash)
  -> random Zaap gameSession
  -> auth_getGameToken(gameSession)
  -> account-specific game token
  -> connection-server authentication on port 5555
  -> single-use Ticket(AccountId, ServerId)
  -> fresh game TCP connection on port 5555
  -> GameSession owning that NetworkStream
  -> ticket redemption and BindAccount(AccountId, ServerId)
  -> account-owned character selection
```

Each arrow is keyed by data belonging to that launch. None means "use the last account that logged
in".

### Local Zaap service

File: `Jondo.Unity.Launcher/Network/ZaapServer.cs`

`connect` accepts only a registered `(InstanceId, Hash)` pair and creates a random `gameSession`.
`auth_getGameToken`, `userInfo_get` and language settings resolve that session back to the same
launch and account.

### Connection and game service

File: `Jondo.Unity.Launcher/Network/GameServerProxy.cs`

Port 5555 auto-detects two phases. The first connection authenticates and issues the single-use
ticket. The second contains wrapped game messages; it creates a `GameSession`, binds it to that
socket and passes both to the game dispatcher. See `sessions.md` for state and packet isolation.

---

## 7. Limits and lifecycles

Eight is a hard maximum:

- `LauncherPreferences` stores no more than eight profiles;
- `LauncherWindow` refuses a ninth profile;
- `ClientLaunchRegistry` refuses a ninth active process and a duplicate active account;
- `SessionRegistry` refuses a ninth registered game socket.

The profile list and active connection list are different. A profile survives launcher restarts;
an active launch exists while its Dofus process runs; a game session exists while its game socket
is connected. Closing one client does not alter the other seven profiles, processes or sockets.

---

## 8. Troubleshooting

### A selected account does not launch

- Confirm that the server status is online and `Dofus.exe` still exists.
- If the account is marked `in game`, close its existing process first.
- Sign in to that profile again if its stored token no longer resolves.

### Two clients show the same character or map

Treat this as a socket/session binding bug, not an account-row UI bug. Check for a distinct line per
client:

```text
[Game Server] Socket bound to session <UUID>
```

A real game connection must never enter the dispatcher through the socketless
`SessionContext.Suelta` fallback.

### The ninth client is rejected

This is expected. Close one active Dofus process before launching another account.

### A closed client remains marked active briefly

The launcher marker follows the process exit event, while the world session follows the TCP
connection. They normally disappear together but are intentionally independent.

---

## 9. Regression checks

`RegressionGuardTests` checks launch identity isolation and the eight-client boundary at startup.
Runtime validation should select at least two profiles, alternate movement, map changes and
interactions, and confirm a different socket-session UUID and correct character for each client.
