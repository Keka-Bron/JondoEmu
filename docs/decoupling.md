# Splitting the server from the launcher

Being able to open the launcher, use it and close it without the server noticing. Today they are the same
process and closing the window kills everybody's game.

It is written from what there is in the code TODAY. Each statement carries its file and its line.
What is my opinion is marked as such.

---

## 0. The conclusion first: it is smaller than it looks

Six maps of the coupling were drawn up separately and then crossed against the code. Out of some
fifty apparent ties, **four real knots** are left, and the whole crossing surface between
the `UI/` folder and the rest of the emulator is **eight lines**:

```
LauncherService.cs:124, 139, 146, 147, 188, 212
Network/ClientLaunchRegistry.cs:50, 52
Program.cs:110
```

Eight. There are no more. It is worth knowing before starting, because instinct says "this is a
three-week refactor" and it is not.

---

## 1. The four knots

### 1.1 The process's life hangs from a `Form`

`Main` stops at `await _shutdown.Task` (`Program.cs:123`). That `TaskCompletionSource` is only
completed by `RequestShutdown` (`Program.cs:151`), and `RequestShutdown` is called from three places, of
which the one that matters is the window's thread when `Application.Run` returns
(`UI/LauncherWindow.cs:1475`). It does not matter how it is closed —the X, Alt+F4, `Application.Exit`—: all the
exits go through there, because `OnFormClosed` (`:619`) shuts nothing down, it only stops the timers.

And there is no switch: `UI.LauncherWindow.OpenOnDedicatedThread()` is called unconditionally
(`Program.cs:110`), and `Main`'s `args` (`Program.cs:19`) **is not read even once**. That is the
short answer to "what prevents starting without a window?": nobody has written the fork.

### 1.2 The startup handshake lives in RAM

It is the big functional knot. `LauncherService.LaunchClient` makes up a hash (`:138`), notes it down in
`ClientLaunchRegistry.Register` (`:140`) and passes it to `Dofus.exe` on the command line and in
environment variables (`:155`, `:168`). Then the client presents it to Zaap, which looks it up **in
that same memory** (`Network/ZaapServer.cs:24`).

With two processes, the launcher writes in its memory and Zaap looks in the server's: it does not
find it and the client never manages to connect. **The launcher has to ask the server for the hash, not
make it up.**

### 1.3 There is no channel left to talk to the server through

And the curious thing is that it existed. The code itself says so, in `Network/HaapiServer.cs:82-84`:

> *The `/api/login`, `/api/register`, `/api/launch`, `/api/status` and `/api/logs` routes used to
> live here. Only the web launcher called them; the native window talks to LauncherService
> directly, so they were dead weight.*

When going from the web interface to the native window, exactly the surface that is now
needed was deleted. Today there is neither an HTTP command route, nor a named control pipe, nor a stop order: the
only `NamedPipeServerStream` in the tree is Zaap's, speaking Thrift with the client
(`ZaapServer.cs:192`).

### 1.4 The window reads the server's memory and calls it a "query"

Three places, all three trivial to fix but all three broken on day one:

* **Status.** `GetStatus` does `ZaapServer.IsRunning && GameServerProxy.IsRunning`
  (`LauncherService.cs:272`), two static `bool`s of the process. In the launcher they would always be `false`
  and it would say "offline" with the server perfectly alive.
* **Console.** `ConsoleLogBuffer` hijacks `Console.Out` (`ConsoleLogBuffer.cs:23-28`) and keeps the
  lines in a static queue (`:18`) that the window reads through `LauncherService.GetLogs` (`:292`). The
  panel would stay blank.
* **"In game".** The window queries `ClientLaunchRegistry.IsActive` and `ActiveCount` in seven
  places (`LauncherWindow.cs:936, 1071, 1087, 1101, 1121, 1242, 1267`). It would paint every account
  as free.

---

## 2. The underlying decision: a single executable, two modes

Here I disagree with what was taken for granted when drawing up the map. The conclusion that came out was "two
processes = two executables = two `Main`s, and an assembly only allows one, so the
82 logic files have to be taken out into a library before anything else". **That is true for two executables and false
for two processes.** The same `.exe` started twice with different arguments is already two
processes.

That is what I propose: **one `Jondo Emulator Launcher.exe`, two modes.**

```
Jondo Emulator Launcher.exe              → launcher mode: window, no services
Jondo Emulator Launcher.exe --server     → server mode: services, no window
```

Why it seems better to me than splitting into two executables:

* **The root stays as it is.** It is a written restriction of the project, in
  `Jondo.Unity.Launcher.csproj:16-20` itself: *"The emulator's root must not have loose files: whoever
  downloads it has to see the .exe and folders, without doubting what to open."* A second executable goes
  right against it.
* **The build problem evaporates.** Without splitting assemblies, the eight
  crossing lines do not have to be touched to *start*. And the hardest tie of all —that `LauncherPreferences`
  (`UI/LauncherPreferences.cs:18`) and `LauncherTexts` (`:20`) are `internal`, so splitting the
  assembly breaks the build on visibility before WinForms even enters the conversation—
  stops blocking.
* **The user's goal is reached in the first phase**, not in the last.

What is paid: the server process is still a `WinExe` that loads WinForms even though it draws
nothing, and it cannot run as a Windows service nor without a desktop session. For two processes on
the same machine —which is what there is, and I argue it below— it does not get in the way. The day a truly
headless server is wanted, phase 4 leaves it ready.

---

## 3. Who keeps what

Answered following what the client really speaks:

| Service | Port | Spoken by | Goes to the |
|---|---|---|---|
| HaapiServer | 8888 | the Dofus client | **server** |
| ZaapServer | 15881 (TCP + named pipe "15881") | the Dofus client | **server** |
| GameServerProxy | 5555 | the Dofus client | **server** |
| GameNodeProxy | 5556 | the Dofus client | **server** |
| ChatServer | 6337 | the Dofus client | **server** |

**All five go to the server.** The launcher speaks none of them: the only thing it does with the ports is
pass them to `Dofus.exe` as arguments (`LauncherService.cs:155, 157, 167`).

The launcher is left with three things, and only three: **the window**, **the preferences**
(`%APPDATA%\Jondo\lanzador.cfg`) and **starting the client process** (`LauncherService.cs:177`,
with its user32 `ShowWindow` at `:251`).

And a consequence worth leaving written: **these are two processes on the same machine, not a
network server.** The launcher starts `Dofus.exe` and handles its window; that only works
locally. Besides, Zaap and GameServerProxy only listen on `127.0.0.1` (`ZaapServer.cs:161`,
`GameServerProxy.cs:37`) and HAAPI on `localhost` (`HaapiServer.cs:21-22`). A remote server would
be five `bind`s and six more literals, and it is another project.

---

## 4. The phases

Each phase leaves the emulator working. No leaving it broken in the middle.

### Phase 0 — The two modes, and the launcher can already be closed

It is the phase that settles the request. The rest is finishing.

1. `Main` reads `args`. With `--server`: database, managers, the five services, and off it goes; without
   arguments: only the window.
2. In server mode, the process's life stops hanging from `_shutdown` and hangs from Ctrl+C and
   a stop order. In launcher mode, remove the `RequestShutdown` in
   `UI/LauncherWindow.cs:1475` and the suicide in `Program.cs:117`.
3. The launcher mode, on opening, **probes 8888**. If it does not answer, it starts itself with
   `--server` in a detached process and waits for it to respond.
4. **An explicit "Stop the server" button** in the window. Today the only way to stop it is the
   X, and if the X stops shutting it down another door has to be given.
5. **Single-instance guard** per mode (a named mutex). Today there is none: two servers
   fight over 8888 and over the "15881" pipe, and the second dies in `Program.cs:73-87`
   writing the error to a console that does not exist in a `WinExe`. A double click that does nothing.
6. On the way, fix a lie: `ZaapServer.Start` sets `_isRunning = true` (`:158`) **before**
   `_tcpListener.Start()` (`:163`), and `GameServerProxy` does the same (`:34` before `:38`). If the
   `bind` fails, `IsRunning` says yes. Today it is covered up because the process dies; with two processes and
   retries, it is not.

### Phase 1 — The command channel

Rebuild what was deleted in `HaapiServer.cs:82-84`. **Opinion: HTTP over 8888**, because
`HaapiServer` is already an `HttpListener`, because it is the port the mod probes anyway, and
because it can be tested with a browser. Bound to `127.0.0.1`.

The verbs, in order of need:

| Verb | What for | Without it |
|---|---|---|
| `estado` | the window's traffic light | the launcher always says "offline" |
| `lanzamiento` | ask for hash and instanceId | **not a single client starts** |
| `activos` | who is in game, how many | the "In game" dot and the cap of 8 |
| `entrar` / `crear-cuenta` | login and registration | two processes writing `auth.db` |
| `registro?desde=N` | the console panel | blank panel |
| `apagar` | the stop button | it can only be killed by hand |

The `estado` one can be even cheaper: probing 5555 from outside, which is exactly what the mod already
does (`JondoFix/Class1.cs:471-477`).

For the log, **doing a `tail` of `logs/emulator_console.log` will not do**: the dump to file
(`ConsoleLogBuffer.cs:43`) does not rebuild the entries and the sequence ids only
exist in RAM (`:55`), and the window uses them to ask only for what is new. It has to go through the channel.

### Phase 2 — The launch registry moves to the server

`ClientLaunchRegistry` becomes the server's entirely. The launcher asks for `{hash, instanceId}` through the
channel, starts `Dofus.exe` with whatever it is given, and reports the removal when the process dies
(`LauncherService.cs:192`).

And give it **expiry on the server side**, which it has none of today:

* Hook it into the `finally` that **already exists** in `GameServerProxy.cs:161-162`, where the
  session is removed when the socket dies. The server already knows which account it was (`GameSession.cs:28`); the only thing
  missing is the wire to `ClientLaunchRegistry.Remove`.
* Finally give a use to `CreatedAtUtc` (`ClientLaunchRegistry.cs:61`) as an expiry, for the client that
  starts and never reaches 5555. Today that field is written and nobody reads it —just like
  `LauncherToken`—.

Without this, closing the launcher leaves hanging accounts and `Register` rejects them forever
(`ClientLaunchRegistry.cs:49-50`).

### Phase 3 — A single owner of the database

The launcher touches `DatabaseManager` in **four places**: `LauncherService.cs:68, 71, 96, 271`. If
those four are asked of the server, the launcher is left without a database and without SQLite, and on the way the
per-IP attempt counter (`DatabaseManager.cs:836`) becomes a single one again instead of two.

And with two writers `busy_timeout` has to go into the connection string —today there is none in
the whole tree, only `journal_mode=WAL` (`DatabaseManager.cs:27` and `:98`)—, so the second one waits
instead of getting an immediate `SQLITE_BUSY`. This was already pending as phase 7 of the
multiplayer plan; here it gets collected.

### Phase 4 — Hygiene, so that some day it can be a real server

The eight crossing lines, and only then the split into assemblies:

* Error codes instead of sentences in `ClientLaunchRegistry.cs:50, 52` and `LauncherService.cs:124,
  188`; let the launcher translate. **Those two in `ClientLaunchRegistry` I put in myself today** when moving
  the French texts to the catalogue: I fixed one problem and created a smaller one, a piece of
  server that reads the user's language preferences.
* The language and the client path, decided by the caller (`LauncherService.cs:139, 212`).
* `Screen.PrimaryScreen` and `System.Drawing.Rectangle` out of `LauncherService` (`:146-147`). They are
  the **only two lines of WinForms** in code that is not in `UI/`; the rest of the `UI/` folder
  is strings and files, without a single reference to `System.Windows.Forms`.
* And then yes: a library with the logic, and two entry points if wanted.

---

## 5. Watch out for these five

Things that do not show today because everything starts in the same order, and that bite as soon as there are two
processes.

1. **The client goes to Ankama's servers if the emulator is not up.** It is the worst of
   all. The mod decides **only once**, when initialising, whether to redirect: `UseLocalRedirect =
   IsEmulatorActive()` (`JondoFix/Class1.cs:121`), and `IsEmulatorActive` is a
   `TcpClient.BeginConnect` to `127.0.0.1:8888` with a **100 ms** wait (`:471-477`). If the probe
   fails, the client gives no error: it connects outwards. Today it never happens because
   `Program.cs:75` brings HAAPI up before the window from which the client is launched exists. **The
   launcher has to check that 8888 answers before calling `Process.Start`**, and not
   trust having started it itself.
2. **Clean install with the launcher opened before the server.** The schema is only created by
   `DatabaseManager.Initialize` (`DatabaseManager.cs:31-41`), and its only caller is
   `Program.cs:35`. Creating an account before that blows up with `no such table: Accounts`, and that text is
   shown as is (`LauncherService.cs:105` → `LauncherWindow.cs:1215`).
3. **The log format is a de facto API.** The window colours the lines by looking for
   `"[DatabaseManager]"` and `"[World]"` inside the text (`LauncherWindow.cs:1375`). Whatever is built
   to carry the console to the other process has to keep the text as is.
4. **The startup guards touch the real registry.** `RegressionGuardTests.Run()`
   (`Program.cs:67`) calls `AssertTwoClientsAreIsolated` and `AssertEightClientLimit`, which register
   ten fake launches and delete them, leaving `_nextInstanceId` at 10. With the registry on the
   server side, that must only run in the process that owns it.
5. **The server dying with clients inside.** Nobody warns. The launcher would find out two
   seconds later through `CheckStatus` (`LauncherWindow.cs:1013`). On restarting, the dictionaries come out
   empty and the token `TokenResponse` handed out (`HaapiServer.cs:208-211`) disappears altogether,
   because that path does not write to the database. And on a clean close the character is saved
   (`GameServerProxy.cs:153`), but on the process dying it is not.

---

## 6. Where I would start

The whole of phase 0, and stop. It is a handful of lines —reading `args`, not calling `RequestShutdown` when
closing the window, a mutex and a probe— and it already does what was asked: open the launcher, use it,
close it, and have the game carry on. Everything else on the list is things that **look wrong** with the
launcher closed —the traffic light, the console panel, the "In game" dot— but that do not prevent
playing.

Phase 1 goes right after because without a channel the launcher cannot start a
client again against a server that was already alive, and that is the normal case from phase 0 on.
