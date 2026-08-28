# Database backups

Before `DatabaseManager.Initialize()` creates tables, alters columns, extracts `world.zip` or runs
a data migration, the server creates a consistent snapshot of every existing game database.

## Location and contents

Backup sets live under `bases/backups/` in UTC timestamp directories:

```text
bases/backups/20260827-123456-789/
  auth.db
  world.db
  manifest.json
```

The SQLite online backup API creates each snapshot, rather than copying the database file directly.
This matters when a database has committed pages in its WAL file. Every copied database must then
pass `PRAGMA quick_check`; the manifest records its size, source modification time and result.

The directory is first built with a `.tmp` suffix and renamed only after every database passes.
A failed or corrupt snapshot is removed and database initialization stops before touching the
original files.

## Rotation and restore

The five newest complete sets are retained. Older timestamp directories are removed only after a
new set has been verified and published. Temporary directories never count as valid sets.

To restore while the server is stopped:

1. choose a timestamp directory whose `manifest.json` lists `quickCheck` as `ok`;
2. keep the current `bases/auth.db` and `bases/world.db` somewhere recoverable;
3. **delete `bases/*.db-wal` and `bases/*.db-shm`**, see below;
4. copy both database files from that same backup set into `bases/`;
5. start the server and confirm the database initialization log.

Step 3 is the one that is easy to skip and expensive to skip. The databases run in WAL mode, so a
stopped server leaves `world.db-wal` and `world.db-shm` next to `world.db` — right now there is a
2 MB `world.db-wal` sitting in `bases/` with nothing running. Those files belong to the database
that was there BEFORE the restore, and SQLite replays the journal against whatever `world.db` it
finds on the next open. Copy the backup in without removing them and the first start quietly plays
somebody else's transactions on top of it: the restore looks like it worked and the data is neither
the backup nor what was there before.

The backup side does not have this problem and does not need the journal files. `BackupOne` uses
SQLite's own `BackupDatabase`, which writes one consistent file with everything that was in the WAL
already folded in — which is also why a backup set holds exactly two `.db` files and no companions.

Do not mix `auth.db` from one set with `world.db` from another: account and character changes may
then refer to different points in time.
