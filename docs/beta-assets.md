# November beta assets

The `3.8.1.2` protocol target pairs with the **3.8.1.2 beta** client. For
development, stage the data in a separate root so the beta has its own settings,
SQLite saves and logs. `beta-run.sh` looks for that root under
`host-artifacts/beta-data/3.8.1.2` by default.

## Stage and run

From a JondoEmu checkout with Python 3.11+ and the November client installed:

```sh
mkdir -p host-artifacts/beta-data
python3 -m venv host-artifacts/asset-venv
host-artifacts/asset-venv/bin/python -m pip install -r AssetImport/requirements.txt
client=/absolute/path/to/november-client
host-artifacts/asset-venv/bin/python AssetImport/import-beta.py --client "$client"

export JONDO_CLIENT_EXECUTABLE="$client/Dofus.x64"
Jondo.Unity.Server/beta-run.sh --headless
```

The importer checks the client version and build date, then uses UnityPy to
read the type trees embedded in `Content/Data` and `Content/Map/Data`. It checks
map coverage and stages the result only after SQLite validation. It refuses
to overwrite an existing root. To refresh
later, use `--output` to stage a new root and set `JONDO_EMULATOR_ROOT` to its
absolute path before running `beta-run.sh`. Both variants use the same ports;
stop the default server before starting the beta.

For Studio or a launcher built from source, set the same `JONDO_CLIENT_EXECUTABLE`
and `JONDO_EMULATOR_ROOT` so text, icons, portraits, and catalogues come from
the beta install and root. The renderer reads November's linked animation
assets and uses the staged body catalogue to select breed 21's rig. Build the
Linux launcher with `-p:JondoProtocolTarget=3.8.1.2` to validate the installed
`3.8.1.2` client.
Start the beta server before starting that launcher. The release installer still
installs the default client.

## Coverage

This refresh stages breed looks and characteristic costs, heads and bodies,
breed 21's spell pairs and ranks, Spanish translations, and the November map
metadata, border destinations, walkability, sight and team placements. The
import excludes farm cells from roleplay/fight walkability and haven-bag cells
from fight walkability. Imported spell effects use the source `actionId`.

The staged SQLite seed starts from the shipped `datos/world.zip`. Authored
`content/`, existing map names/classifications, and old routes with no new
destination are carried forward. Items, monsters, interactives, and older
classes' spell ranks still come from the shipped seed; importing those is a
separate catalogue update. The asset report is `beta-assets.json` at the root.
