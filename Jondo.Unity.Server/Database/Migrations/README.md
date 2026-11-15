# Character-look storage and registered migration

There is one SQL file: [`20261112-character-colors.sql`](20261112-character-colors.sql),
one `CharacterColorsMigration.Apply` call, one transaction and one completion marker:
`20261112-character-colors` in the world database's `JondoMigrations` table.

`DatabaseManager.Initialize` calls it after the verified backup and base schema
setup, **before** default-character insertion. Old databases have a NOT NULL `Look`
column; the migration extracts its palette and removes it before the new insert,
which writes only semantic data. It runs once on the next initialization;
registering or compiling it does not execute it. Verification runs the migration
against isolated in-memory databases, without initializing the server databases.

## Schema and backfill

- Add exactly six nullable integer columns: `Characters.Color1` through `Color6`.
  Each contains plain RGB in the range `0..16777215`, without a packed slot index.
  For example, `Color1 = 1122867` represents slot 1, RGB `#112233`.
- Backfill from the first nonempty packed field 1 in the historical `Look` blob,
  using the historical stored-look format. If absent, recognize the original seed
  layout (scalar field 1 = 1 and scalar field 3 = 3) and read its packed field 4.
  Newer field-1 palettes take precedence. Only the migration decoder reads these blobs.
- Assign bare-RGB palette entries to columns by position. Assign indexed palette
  entries by their encoded slot, so sparse or reordered slots keep their identity.
- Leave missing/default slots NULL; `-1` is a default sentinel, not an RGB value.
  For a bare-RGB palette containing `-1`, later entries retain their positions.
- Missing, malformed or unrecognized old-format looks leave all six columns NULL,
  preserving the breed/sex fallback without freezing its defaults into stored RGB.
- More than six entries, duplicate explicit indexed slots, or indexed slots outside
  `1..6` fail the migration. A fixed-slot schema cannot preserve those values;
  the migration does not silently truncate, overwrite or invent slots.
- Drop `Look` and record completion in the same transaction as the column additions
  and backfill. A failure at any point restores the original schema and data.
- Fresh databases create the six columns directly without a blob. The helper
  verifies that all six columns exist, then executes only the same SQL file's
  `Migration history` section. Existing colour choices are preserved; no separate
  baseline migration or additional completion marker is needed.

## Execution boundary

The SQL requires the connection-local `jondo_look_color(Look, slot)` function,
implemented by `CharacterColorsMigration.DecodeColor`. It returns one nullable RGB
integer for a slot in `1..6`. SQLite itself cannot decode protobuf.
The helper opens no database. Startup supplies its existing open world connection,
after the verified backup and before seeding. The default-character seed explicitly
stores its original six RGB choices: `#E6C5A2`, `#BDB2CB`, `#2E70A4`, `#121312`,
`#080608`, `#92DBF5`. Fresh databases and migrated historical seeds retain that palette.

`Apply` acquires an immediate transaction and checks the migration name in the
world database's `JondoMigrations` table. If already recorded, it makes no changes.
Otherwise it registers the decoder, executes the applicable SQL, and explicitly
consumes every result set before committing. Batched schema changes can return
results; relying on `ExecuteNonQuery` would finish later statements during reader
disposal, where Microsoft.Data.Sqlite suppresses their errors. The six columns,
backfill, blob removal and completion marker commit together. An error rolls back the transaction;
schema or storage errors are not treated as malformed saved palettes. Startup does
not catch or suppress these failures. There is no separate command-line entry point.

On restart, the single completion marker is checked before referring to the removed
column. Later starts never re-extract a palette or overwrite new colour choices.

## Look ownership and composition

- `CharacterColors` is an immutable six-member value; NULL inherits, and explicit
  values must be `0..0xFFFFFF`. Packed indices are not persisted in these columns.
- Both character queries read all six columns. `CharacterLookBuilder.SavedColors`
  resolves per-slot defaults and constructs indexed look colours from that model.
- `BreedLookCatalog` owns immutable breed/sex bones, skins, scales and exactly six
  RGB defaults. Lazy publication prevents readers from observing a partial load.
- Creation inserts only the nullable RGB choices and existing identity/gameplay data.
- Recolouring writes positional RGB/default choices. Outfit application converts
  the saved outfit's encoded slots to the same fixed model before making changes.
- `SaveCharacterColors` writes all six choices atomically, with no look construction
  or serialization in storage. Equipment, wardrobe, mount and size keep their owners.
- Inventory builds semantic preview and map appearances separately after equipment
  changes. Its existing packet wrappers serialize only at the send boundary; the
  old four-item skin table and byte mutation are removed.
- Map loading uses the shared actor builder. Fighter-show uses the normal/current
  appearance of the requested fighter, including existing bot/incarnation policies,
  instead of taking the ambient session's look.
- `LookHex`, `LookBytes`, `PlayerActorDetails` and `ReconstructActorDetails` are
  removed. Selection no longer constructs an intermediate actor-detail blob.

## Verification

- In-memory migration coverage includes fresh-schema handling, both historical seed
  blobs, sparse palettes in either layout, restart, backfill failure, invalid palettes,
  drop dependency failure, completion-marker failure and full rollback.

Remaining checks:

- Run database-backed existing/new-character, recolouring and outfit regressions.
- Perform real-client selection, inventory, map-change and fight appearance checks,
  including equipment, cosmetics, mounts/riders/pets, mutations and disguises.

Focused model tests and storage tests use a hand-built in-memory schema without
calling either migration or `DatabaseManager.Initialize`. Database-backed gameplay
and real-client checks remain pending.
