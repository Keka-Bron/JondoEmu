# Running the desktop tools on Linux

Launcher, Server, Studio and Deobfuscator use Avalonia and .NET 10 on Linux x64.
Take `JondoEmu-<tag>-linux-x64.zip` from a release -- the launcher, the server and
Studio ready to run, with the data they read -- or run them from a source checkout or
local publish output.

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
  version **3.6.10.10 or 3.6.10.11**. Choose `Dofus.x64`; `Dofus.exe` is for the
  [Wine way](#mods-on-linux) only.

The native client uses its normal Linux graphics/audio dependencies separately.
The launcher requires MelonLoader on Linux for JondoFix. Follow [Mods on Linux](#mods-on-linux)
to install it manually.
The separate chat server is unsupported for this Linux workflow and is not a
launch/readiness prerequisite.

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
   MelonLoader arguments, and drop `WAYLAND_DISPLAY`: on a Wayland session the client's
   screen plugin would take its Wayland path, which cannot tell where the window is, and
   the client would stop on its loading screen (`DisplayConfiguration.GetDisplayInfo(-1)`).
   Unity draws through XWayland either way. Each launch gets a separate Unity `player.log` under
   `<emulator-root>/logs/client-<instance>-<unique-id>/`.

Jondo's HTTP/config/control endpoint uses `8888`, TCP Zaap
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

## Mods on Linux

The mods run wherever MelonLoader does. The launcher picks the way from the client
selected in Settings. In both it installs the release copy of `JondoFix.dll` into the
client's `Mods/`, uses the server manifest for optional DLLs when available, and supplies
the account's role and tokens.

### Native client with MelonLoader

When `MelonLoader.Bootstrap.so` and `MelonLoader/` are beside `Dofus.x64`, the launcher
preloads the loader (`LD_PRELOAD=./MelonLoader.Bootstrap.so`, relative to the client's
folder because `LD_PRELOAD` splits on spaces) and links `Dofus.x64_Data` to `Dofus_Data`,
the name Cpp2IL looks for on the first run.

1. Unpack `MelonLoader.Linux.x64.zip` into the client's folder.
2. Put a .NET 6 runtime where MelonLoader looks for a portable one, `<client>/dotnet`
   (`dotnet-install.sh --runtime dotnet --channel 6.0 --install-dir "<client>/dotnet"`),
   unless one is installed system-wide.
3. Start the client once from the launcher: MelonLoader generates its assemblies (two to
   three minutes), then the mods load.

This needs an Il2CppInterop that resolves `MetadataCache::GetTypeInfoFromTypeDefinitionIndex`
on ELF builds ([BepInEx/Il2CppInterop#296](https://github.com/BepInEx/Il2CppInterop/pull/296)).
MelonLoader 0.7.3 and the 0.7.4 CI builds bundle one without it, and the client dies with
`SIGSEGV` in `GameAssembly.so` right after `Class::Init signatures have been exhausted`;
until a MelonLoader release carries the fix, `MelonLoader/net6/Il2CppInterop.Runtime.dll`
has to be a build of that branch.

Keep the game's **Display Mode** on *Windowed*. In *Full screen* the native client's window
stops receiving the mouse under KWin/XWayland (keys still work), mods or not; it is the
shared `windowDisplayMode` option (`0` full screen, `1` windowed) in
`<client preferences>/unity3d/Ankama/Dofus/RELEASE/Shared/dofus.json`, so a client stuck
that way is set back with the game closed.

### Windows client through Wine

Select `Dofus.exe` in Settings and the launcher starts it with `wine`, in a prefix of its
own (`$XDG_DATA_HOME/Jondo/wine`). The client's folder needs MelonLoader for Windows
(`MelonLoader.x64.zip`, its `version.dll` beside `Dofus.exe`) and a Windows .NET 6 runtime in
`<client>/dotnet`. The Windows client shares nearly every file with the Linux one: only its
`win32_x64` fragment differs.

What the launcher sets, each for a launch that failed without it:

| Setting | Why |
| --- | --- |
| `WINEDLLOVERRIDES=version=n,b` | Otherwise Wine loads its own `version.dll` and MelonLoader never starts. `mscoree` must stay enabled: Cpp2IL cannot load its plugin without it. |
| `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` | Cpp2IL dies on Wine's ICU (`Cannot get symbol u_charsToUChars`). |
| `DOTNET_GCHeapHardLimit` | .NET misreads the memory Wine reports and the interop generation runs out of it. |
| `JONDO_EMULATOR_ROOT` as `Z:\…` | The mods read the emulator's data through a Windows path. |

`JONDO_WINE` names another Wine, `JONDO_WINEPREFIX` another prefix, and
`JONDO_WINE_DESKTOP=1` puts each client in a virtual desktop of its own.

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
