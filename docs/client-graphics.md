# Client texture packs and DirectX

How the Dofus 3 client picks its scenery textures (Default, HD, 4K) and its Windows renderer when
it is started outside the Ankama Launcher. Read from the 3.6.10.11 client: its `zaap.yml` and
`zaap-windows.yml`, its own texts, and the Cytrus manifests Ankama's CDN serves for it.

---

## 1. A pack is shown when three things hold

1. **It is on disk.** `Dofus_Data/StreamingAssets/Content/Map/Textures/2x/` for HD, `4x/` for 4K,
   each with `catalog_1.0.bin`, `catalog_1.0.hash` and the `mapgfx_<n>x_*_assets_all.bundle`
   files. Default (`1x/`) comes in the `map_common` fragment, so every client has it.
2. **The client is told it is there.** `--hdReady` / `--4kReady` on the command line, with no
   value. They select nothing: they say the pack has been downloaded. It is what `zaap.yml` does
   when the Ankama Launcher's "Download the DOFUS HD Pack (Experimental)" (or 4K) box is ticked:
   it adds the fragment `map_textures_2x` (or `map_textures_4x`) to the install and the argument
   to the launch.
3. **The player picks it in the game's options**, under "(Experimental) Texture packs for
   scenery": Default, HD or 4K. The choice is saved as `currentTexturePack` in
   `%USERPROFILE%\AppData\LocalLow\Ankama\Dofus\RELEASE\Shared\dofus.json` and, in the client's own
   words, "will be effective on the next map change". A pack whose flag was not passed answers
   "You must download this texture pack (in the Launcher) before activating it."

The flags are read when the client starts; changing them needs a new client. The in-game choice
does not.

Never pass a flag for a pack that is not on disk. `catalogs.json` lists `Textures/4x` whether 4K
is installed or not: it names the catalogues the client understands, not the ones it has.

---

## 2. DirectX

Independent of the pack. `zaap-windows.yml` turns the Launcher's `directxVersion` into nothing
(DirectX 12, the default on Windows) or `-force-d3d11`. Both combine:

```text
Dofus.exe -force-d3d11 --hdReady ...
```

The renderer, like the flags, is chosen at start.

---

## 3. The packs on Ankama's CDN

Both packs are fragments of the client's Cytrus manifest:

```text
https://cytrus.cdn.ankama.com/dofus/releases/dofus3/windows/6.0_<version>.manifest
```

`<version>` is the client's own, from `Dofus_Data/StreamingAssets/version` (`Version=3.6.10.11`),
never a constant and never a neighbouring release: a pack is taken from the manifest of the client
it goes into. The CDN still serves older manifests, so the version always resolves.

| Fragment | Folder | Files | Size |
|---|---|---|---|
| `map_textures_2x` | `Textures/2x/` | 31 | 3.81 GiB |
| `map_textures_4x` | `Textures/4x/` | 31 | 8.05 GiB |

Sizes as listed in the 3.6.10.10 manifest; 3.6.10.11 changed the game data, the texts and the
pictograms, not `map_common` nor `map_textures_2x`.

The manifest is a FlatBuffer: fragments, their files (name, size, SHA-1, chunks) and their bundles.
A bundle is fetched from `https://cytrus.cdn.ankama.com/dofus/bundles/<first two hex>/<sha1>`
and accepts Range requests; a chunk's hash is the SHA-1 of its bytes and its offset is where they go
in the file. A file is installed when its whole SHA-1 is the manifest's.

---

## 4. Jondo

The launcher's **Settings** has a **Graphics** block with one row per pack (`MainWindow.Packs.cs`,
`Jondo.Unity.Launcher/Packs/`). A row shows the pack's size and state and one button that reads
Download, Resume or Verify as the state asks, and Cancel while it works; Remove; and a box to use
the pack in the game, which can be ticked only once the pack is verified.

- **The version is the client's.** `ClientVersion.Read` reads `Dofus_Data/StreamingAssets/version`
  of the client the launcher starts, and nothing else: not `LauncherService.Version`, which names
  the protocol. No readable version, no download; the manifest of that version not on the CDN, no
  download either -- never another version's.
- **Downloading.** The manifest is cached per version in `%LOCALAPPDATA%\Jondo\cytrus\`. Each bundle
  is one streamed Range request, four at a time, with retries; every chunk is checked against its
  SHA-1 before it is written at its offset into `<file>.part`, a journal keeps the bundles already
  done so a stop or a crash resumes, and every whole file is checked before it takes its name.
  Free space is checked first, and nothing is written while a `Dofus.exe` of that client runs.
- **Installed** means verified: `jondo-pack.json` in the pack's folder records the version and
  every file's size and SHA-1, written only after the whole check. A pack that is already on disk
  (the 2x one Ankama's launcher leaves) starts as unverified, and Verify hashes it without
  downloading anything if it is whole. When the client's version changes, the pack is unverified
  again until it is verified for the new one.
- **Launching.** `--hdReady` / `--4kReady` are added (`packHd`, `pack4k` in
  `%APPDATA%\Jondo\lanzador.cfg`) only for a pack the player ticked AND installed for the version
  of the client being started. The pack is then picked in the game's options.

`lanzador.cfg` may name a client other than the one next to the launcher: the packs are those of
the client it names.

To list the packs a client has, without touching it:

```powershell
Get-ChildItem '<client-root>\Dofus_Data\StreamingAssets\Content\Map\Textures' -Directory
```

---

## 5. Cost

The packs change map assets, not the network or the emulator. They cost disk, VRAM and map loading
time, and every client Jondo starts loads its own: with a full team of HD or 4K clients that adds
up. DirectX 11 can be kept for compatibility without giving up a pack.
