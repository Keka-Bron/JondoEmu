# Jondo Server desktop on Windows and Linux

Jondo Server uses the same Avalonia desktop on Windows and Linux. It owns its window;
the log, counters, settings, language choice and restart controls are part of the server.
The native Linux game client is available separately.

## Running

From a source checkout with the .NET 10 SDK installed, on Linux x64:

```sh
sh Jondo.Unity.Server/run.sh
```

The script publishes the server with tests skipped and executes the result.
Arguments are forwarded; `sh Jondo.Unity.Server/run.sh --headless` runs without a desktop.
For development on either platform:

```sh
dotnet run --project Jondo.Unity.Server/Jondo.Unity.Server.csproj
dotnet run --project Jondo.Unity.Server/Jondo.Unity.Server.csproj -- --headless
```

From an unpacked Linux server release:

```sh
./"Jondo Server"
./"Jondo Server" --headless
```

Windows releases retain `Jondo Server.exe`. Closing the desktop or pressing Stop asks
for confirmation, including the number of connected players, before shutting services
down. A startup failure stays in the desktop log until it is closed. Headless failures
are logged and exit with a nonzero status. Ctrl+C and Linux SIGTERM request graceful
shutdown. Linux without `DISPLAY` selects headless mode automatically. Desktop sessions
use Avalonia's platform-detected Linux backend (X11/XWayland with the current Avalonia
version). Wayland sessions therefore need XWayland for this desktop.

Framework-dependent releases require the **.NET 10 runtime**, not the Windows Desktop
runtime on Linux. A Linux desktop also needs the normal X11, fontconfig and Skia system
dependencies. On Debian/Ubuntu these include `libx11-6`, `libice6`, `libsm6`,
`libfontconfig1` and a readable font such as `fonts-dejavu-core`. Native Skia, HarfBuzz
and SQLite binaries are bundled and extracted by .NET when needed. `--headless` does
not initialize Avalonia or require a display. For sandboxed/service installations,
ensure .NET's native-library extraction directory is writable (or set
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a writable directory).

To run the server headless in a container instead, see [Jondo Server in Docker](docker.md).

## Data and publishing

The server still uses the emulator's `.jondo-root`, `datos/` and `content/` directories.
Its own artwork and icon are embedded; `launcher_assets/` is not needed by the server
GUI. Databases, backups, logs and configuration must be writable. The root resolver
searches from the executable and working directory for `.jondo-root`, allowing the
packaged executable to be started from another working directory.

```sh
dotnet publish Jondo.Unity.Server/Jondo.Unity.Server.csproj \
  -c Release -r linux-x64 --artifacts-path host-artifacts
```

The executable is at
`host-artifacts/publish/Jondo.Unity.Server/release_linux-x64/Jondo Server`.
Place it in the emulator root next to `.jondo-root`, `datos/` and `content/`, or use
the Linux zip from a release. A Windows publish uses `-r win-x64`.
Publishing runs the general, server/desktop and connection-protocol tests on both Linux
and Windows. `-p:SkipRootDeploy=true` prevents
a Windows publish from copying the executable to the checkout root.

Each release carries `JondoEmu-<tag>-linux-x64.zip`: the server, the launcher and Studio
built for Linux, with the data they read. It requires the .NET runtime. The Windows zip
carries the same three for Windows.

## The window

The window has four pages, reached from the sidebar or with Ctrl+1 to Ctrl+4. The top bar
shows whether the world is online, how long it has run and how many players are in it.

- **Console**: the world, network, machine and loaded-data counters, and the log with a
  filter, auto-scroll and Clear.
- **Players**: the characters in the world with their level, map and whether they are
  fighting; a search box, a box to also list simulated players, Kick (with confirmation;
  the character is saved as it leaves) and an announcement sent to every player's chat in
  their own language.
- **Events**: each yearly event (Kwismas Island, Al Howin, Vulkania, St. Ballotwine's Day,
  Fleaster and Me Hearty Party) on its calendar, held open or held shut, and whether the event monsters
  come out while it runs, with the chance for each group. These apply at once, without a
  restart, and are saved to `config/events.json`. An open event opens its boat or passage
  replies, its NPCs, quests and dungeon; a shut one hides them even on its own days. The
  monsters are listed in `content/world/event_monsters.json` (Al Howin Gobballs and Tofus
  with pumpkin masks, the Kwismas Minotoball and Dragoturkeys in Frigost).
- **Settings**: the settings that apply at the next start, available once the world is up.

## Settings and restart

Settings are saved to `config/server_settings.json`. Editing never changes the rules
of the current run. Save marks changes as pending; Save and Restart requires a second
confirmation and announces the restart to players. The countdown is 60 seconds with
players in the world and 3 seconds when empty. Restart preserves headless mode and
works with both an apphost and `dotnet "Jondo Server.dll"` deployment.

Spanish, English and French are supported. The window language is saved immediately
and does not require a restart. Clear only clears the displayed log, preserving disk
logs and the read cursor. The log retains up to 4,000 rendered rows and virtualizes
offscreen rows. Right-clicking a packet row opens the existing name-binding picker.

## Verification

```sh
dotnet test Jondo.Unity.Server.Tests/Jondo.Unity.Server.Tests.csproj
```

The suite checks TCP transport, headless Skia rendering, embedded assets, settings
editing and validation, loot dependencies, restart confirmation, close/stop confirmation,
bounded multiline logs, launch/restart arguments, desktop/backend shutdown coordination,
and every page of the window in each language (navigation, kicks, announcements, event
modes, the log filter and the settings editor) against an injected fake server.
It does not start world services or edit operator settings. CI tests on Windows and
Linux and publishes the server for both RIDs. Actual desktop smoke testing should also
cover scaling, a long-running log, process restart and connecting the native client.

The larger general suite is also portable:

```sh
dotnet test Jondo.Unity.Tests/Jondo.Unity.Tests.csproj
```

Its database-backed cases need a writable `bases/world.db` extracted from
`datos/world.zip`. Both CI platforms prepare that fixture before running tests.
Cases that depend on optional installed-client assets still return early when those
assets are unavailable. Alliance and guild store collections run without overlapping
other collections because their temporary database overrides are process-wide.
