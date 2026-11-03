# Running the desktop tools on Linux

Launcher, Server, Studio and Deobfuscator use Avalonia and .NET 10 on Linux x64.
Run them from a source checkout or local publish output. Linux release archives are
not required for this workflow.

## Requirements

- .NET 10 **SDK** to build; the ordinary .NET 10 runtime to run framework-dependent
  publish output. Linux does not use the Windows Desktop runtime.
- X11, or XWayland inside a Wayland desktop session. The current Avalonia 11.2
  backend requires `DISPLAY`; this is not a native Wayland backend.
- X11/fontconfig libraries and a readable font. On Debian/Ubuntu:

  ```sh
  sudo apt-get install libx11-6 libice6 libsm6 libfontconfig1 \
    libxkbcommon-x11-0 libxrandr2 libxcursor1 libxi6 fonts-dejavu-core
  ```

- Optional launcher music: `sudo apt-get install libmpv2`. It decodes the shipped
  WebM/Opus theme by its content, despite the file's `.mp3` extension. Music is
  unavailable when the library is absent; the rest of the launcher still opens.
- A matching native Dofus installation for playing and client-derived artwork:
  version **3.6.10.10 or 3.6.10.11**. Choose `Dofus.x64`, not `Dofus.exe`.

The native client uses its normal Linux graphics/audio dependencies separately.
No Linux mod loader is installed. JondoFix and its in-game administration panels
remain Windows-only. The separate chat server is unsupported for this Linux
workflow and is not a launch/readiness prerequisite.

## Build and start

From the repository root:

```sh
dotnet build Jondo.Unity.sln -c Release
dotnet run --project Jondo.Unity.Server -c Release
```

Open another terminal for the launcher:

```sh
dotnet run --project Jondo.Unity.Launcher -c Release
```

Studio and Deobfuscator run independently:

```sh
dotnet run --project Jondo.Unity.Studio -c Release
dotnet run --project Jondo.Unity.Deobfuscator -c Release
```

The launcher starts a published `Jondo Server` beside it or in the emulator root
when no server is answering. From a build-only checkout, start the server using
the command above. Closing the launcher leaves the server and clients running;
remote-client relay connections last for the lifetime of the launcher.

For isolated build output, add `--artifacts-path host-artifacts` to build/publish/test
commands. Set `JONDO_EMULATOR_ROOT` to the absolute checkout path when running
output outside the repository. That directory must contain `.jondo-root`.

## Configure the native client

1. In Launcher **Settings**, select the installed `Dofus.x64` file. Ensure it is
   executable (`chmod +x /path/to/Dofus.x64` if necessary).
2. Click **Configure client for Jondo**. This sets the following entry in
   `Dofus_Data/StreamingAssets/version`, preserving the other settings:

   ```ini
   ConfigUrl=http://127.0.0.1:8888/config/dofus3.json
   ```

   The original is retained as `version.jondo-backup`. You can also edit the entry
   manually. The launcher validates the version and configuration before reserving
   an account's launch slot.
3. Choose the shared client preferences directory in Settings. It is passed
   directly as `XDG_CONFIG_HOME` to every client. The default is
   `$XDG_DATA_HOME/Jondo/client-config` (normally `~/.local/share/Jondo/client-config`).
   Point it to an existing configuration base to reuse your client preferences.
4. Sign in, select accounts and launch them. Each process receives its own Jondo
   instance ID/hash and `ZAAP_*` environment. Linux launches omit Direct3D and
   MelonLoader arguments. Each launch gets a separate Unity `player.log` under
   `<emulator-root>/logs/client-<instance>-<unique-id>/`.

Jondo's endpoints differ from Unemu's: HTTP/config/control uses `8888`, TCP Zaap
uses `15881`, connection/game uses `5555`, and the dedicated game node uses `5556`.
The Linux remote relay forwards these four ports. Set `servidor=<host>` in launcher
preferences to use a remote server; client configuration stays on loopback.

Launcher preferences live at `$XDG_CONFIG_HOME/Jondo/lanzador.cfg`, normally
`~/.config/Jondo/lanzador.cfg`. The selected installation is also used by Studio
and the sprite reader. `JONDO_CLIENT_EXECUTABLE=/absolute/path/Dofus.x64` overrides
discovery for all tools, useful for a source checkout or CI with installed assets.
After the configured executable and supported version folders, discovery also checks
`<emulator-root>/client` on Linux and `C:\Jondo\DofusClient` on Windows.
Missing optional client assets are reported by Studio; data editing still works.

Texture-pack installation and removal detect running native clients even when the selected
executable or one of its parent directories is a symlink. Free-space checks use the filesystem
containing the selected client, including separate mounts.

Saved account credentials and model-provider keys use AES-GCM on Linux, with a
separate owner-only `clave.bin` in the configuration directory. Windows retains
DPAPI and reads older Deobfuscator settings. Keep Linux preference files and their
key together when moving a profile.

If character selection has a blank background, test with a new, empty client
preferences directory. The Unemu upgrade notes record stale client configuration
causing that symptom. Keep the original directory and switch back after diagnosis;
do not delete the emulator's databases to reset client preferences.

## Local publishing

```sh
for project in Jondo.Unity.Server Jondo.Unity.Launcher Jondo.Unity.Studio Jondo.Unity.Deobfuscator; do
  dotnet publish "$project/$project.csproj" -c Release -r linux-x64 \
    --artifacts-path host-artifacts -p:SkipRootDeploy=true
done
```

Executables appear under `host-artifacts/publish/<project>/release_linux-x64/`,
without `.exe` suffixes. Keep `.jondo-root`, `datos/`, `content/` and
`launcher_assets/` discoverable through the checkout or `JONDO_EMULATOR_ROOT`.
Writable databases, logs and configuration are needed. Single-file native-library
extraction also needs a writable location; `DOTNET_BUNDLE_EXTRACT_BASE_DIR` can
select one.

Publishing Studio runs its display-independent Skia self-test, attaching and
rendering every section in all three languages. Cross-publishing skips execution
of a foreign apphost. `SkipTests=true` is the explicit escape hatch for publish
gates; `SkipRootDeploy=true` prevents copying apphosts to the checkout root.

## Verification

Prepare the general suite's database fixture on a fresh checkout:

```sh
unzip -n datos/world.zip -d bases
dotnet test Jondo.Unity.Tests -c Release
dotnet test Jondo.Unity.Server.Tests -c Release
dotnet test Jondo.Unity.Protocol.Connection.Tests -c Release
```

The test suites run without a graphical session or Xvfb. CI runs these suites,
local Linux publishes and Windows cross-publishes. Manual desktop checks cover compositor
focus rules, file-picker portals, multi-monitor/fractional scaling, audible music,
native-client world entry and multiple clients against both local and remote servers.
