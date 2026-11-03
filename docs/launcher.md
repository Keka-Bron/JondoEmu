# Native launcher and eight-client teams

How the Avalonia launcher stores a team of accounts, starts up to eight independent Dofus
processes and preserves account identity through Zaap, authentication and the game socket.

It runs on Windows and Linux. See [Linux desktop setup](linux-desktop.md) for native-client
configuration, and `sessions.md` for the game-session implementation.

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

Preferences live outside the emulator installation:

- Windows: `%APPDATA%\Jondo\lanzador.cfg`.
- Linux: `$XDG_CONFIG_HOME/Jondo/lanzador.cfg`, normally `~/.config/Jondo/lanzador.cfg`.

The file remembers:

- launcher/game language (`es`, `en` or `fr`);
- an explicitly selected client executable (`Dofus.exe` on Windows, `Dofus.x64` on Linux);
- the Linux client's shared preferences directory (`clientConfig`, passed as `XDG_CONFIG_HOME`);
- at most eight account profiles;
- each profile's account id, login, nickname, token and selection state.

The account list is serialized as JSON and encrypted into the `cuentas` setting by the shared
`Jondo.Unity.Contract/Security/SecretStore.cs`: DPAPI on Windows, AES-GCM with a separate owner-only
`clave.bin` in the configuration directory on Linux. Older Base64-only account lists are read and
rewritten encrypted. Passwords are not stored; saved session and refresh tokens remain private user data.

After the window opens, saved profiles are restored and their tokens checked with the server through
`LauncherService.RememberSession`. Expired sessions are marked for sign-in. A malformed account payload
is ignored rather than preventing the launcher from opening. A stored client path is used only while
the file still exists; `JONDO_CLIENT_EXECUTABLE` can override discovery for all desktop tools.

---

## 3. Team UI state

Files: `Jondo.Unity.Launcher/UI/MainWindow.axaml` and `MainWindow.axaml.cs`.

Each row displays the nickname and account id, its selection checkbox and an `in game` marker when
`LauncherService.IsActive(accountId)` is true. This combines the server's latest active-account
response with processes started by this launcher. The summary distinguishes saved accounts,
selected accounts and active Dofus processes.

The maximum is checked while adding a profile and while launching. Re-authenticating an account
already in the list refreshes its token instead of creating a duplicate row.

`JugarAsync` takes a snapshot of the selected rows and calls `LauncherService.LaunchClient` once
for each inactive account. A failure for one account is reported with that nickname and does not
erase or silently substitute the other accounts.

---

## 4. Authentication in the launcher

File: `Jondo.Unity.Launcher/LauncherService.cs`

`SignIn` sends the `entrar` operation through `Jondo.Unity.Launcher/ControlClient.cs` to the server's
HTTP control API on port 8888. `Jondo.Unity.Server/Network/ControlApi.cs` owns credential validation,
database updates and registration of the account's session token. On success the launcher:

1. receives the account id, nickname, token and role;
2. sets the control session token used by subsequent window operations;
3. returns the account details to the UI, which adds or refreshes that account's team row.

There is deliberately no `ActiveAccount` field. Every later operation starts from the token of the
specific team row being launched.

Account creation uses the same boundary: `LauncherService.RegisterAccount` sends `crear-cuenta` to
the control API. The launcher does not read `auth.db` or reference the server implementation.

---

## 5. Starting a client process

`LauncherService.LaunchClient(token)`:

1. resolves the configured client executable and validates native Linux client settings;
2. checks that the server is answering;
3. sends `lanzamiento` with the selected row's token and language to the control API;
4. receives an account id, unique `InstanceId`, hash and role from the server, which owns registration
   and rejects duplicate active accounts or the address's ninth active client;
5. builds the platform-specific `ProcessStartInfo` and starts Dofus;
6. reports release once if launch preparation/process creation fails or when the process exits,
   using that account's token, the allocated instance ID and the random launch hash. A delayed
   notification cannot release a replacement launch, including after a server restart reuses an
   instance number. `fin-de-lanzamiento` requires `token`, `instancia` and `hash`; account IDs
   supplied by callers do not authorize a release.

Important arguments include:

```text
--instanceId <unique integer>
--hash <random per-launch hash>
--port 15881
--connectionPort 5555
--langCode <es|en|fr>
```

The texture-pack arguments (`--hdReady`, `--4kReady`) follow the connection arguments, and only for a
pack the player turned on in Settings → Graphics that is installed and verified for the client's
own version and platform. How the packs are downloaded from Ankama's CDN and verified is in
`client-graphics.md`.

Equivalent `ZAAP_PORT`, `ZAAP_HASH`, `ZAAP_GAME`, `ZAAP_RELEASE`, `ZAAP_INSTANCE_ID` and
`ZAAP_CAN_AUTH` environment variables are set for the child. Every client receives its own hash
and instance id even though all clients use the same local listeners.

The process starts with the launcher screen's working dimensions, falling back to 1920×1080 when
no screen is available. On Windows it retains Direct3D/MelonLoader options, installs the shipped
JondoFix when MelonLoader is present, and waits briefly to maximize the client window. Linux uses
the native client without those options or a mod loader; each launch has a unique `player.log`
directory and shares the configured client preferences directory. Launching stops launcher music.

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

File: `Jondo.Unity.Server/Network/ZaapServer.cs`

`connect` accepts only a registered `(InstanceId, Hash)` pair and creates a random `gameSession`.
`auth_getGameToken`, `userInfo_get` and language settings resolve that session back to the same
launch and account.

### Connection and game service

File: `Jondo.Unity.Server/Network/GameServerProxy.cs`

Port 5555 auto-detects two phases. The first connection authenticates and issues the single-use
ticket. The second contains wrapped game messages; it creates a `GameSession`, binds it to that
socket and passes both to the game dispatcher. See `sessions.md` for state and packet isolation.

---

## 7. Limits and lifecycles

The profile and per-address launch limits are eight:

- `LauncherPreferences` stores no more than eight profiles;
- `MainWindow` refuses a ninth profile;
- the server's `ClientLaunchRegistry` refuses a ninth active launch per address and a duplicate active account.

`SessionRegistry` separately enforces the server-wide game-session capacity (`Contract.ClientesEnTotal`).

The profile list and active connection list are different. A profile survives launcher restarts;
an active launch exists while its Dofus process runs; a game session exists while its game socket
is connected. Closing one client does not alter the other seven profiles, processes or sockets.

---

## 8. Troubleshooting

### A selected account does not launch

- Confirm that the server status is online and the selected client executable still exists.
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
connection. They normally disappear together but are intentionally independent. Closing the launcher
leaves the server and client processes running; a remote relay ends with the launcher, so remote
clients depend on it remaining open.

---

## 9. Regression checks

`RegressionGuardTests` checks launch identity isolation and the eight-client boundary at startup.
Runtime validation should select at least two profiles, alternate movement, map changes and
interactions, and confirm a different socket-session UUID and correct character for each client.
