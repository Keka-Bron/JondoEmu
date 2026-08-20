# Changelog

## [3.6.10.10-admin.7] - 2026-08-20

### Fixed
- **The server and the launcher did not start anymore.** The startup self-check added with the professions update treated the professions catalogue as mandatory and crashed the whole server when it was empty — which it is whenever the optional dofusdude dump files are not present — so the server died before opening a single port, and the launcher then spent up to 90 seconds waiting for it before showing anything. The self-check now skips the professions part when the dumps are absent (the same graceful behaviour the importer itself already had, announced in the startup log) and still enforces it fully whenever the dumps are present. Verified: the server reaches "all services online" with its five ports listening, and the launcher attaches to it immediately.

## [3.6.10.10-admin.6] - 2026-08-20

### Added
- **A full data audit** of the emulator's data layer, documented in `docs/data-audit.md` and re-runnable at any time with `tools/auditoria_datos.py`. Every data file and database table was counted, and every cross-reference between the client dump and the dofusdude catalogues was followed: item sets, cosmetics, mounts and mob members all resolve with zero orphans — the two data sources agree wherever they overlap. The audit also found five real gaps: the professions tables are empty because the dofusdude dump files (`datos/JsonFromDofusDude/`) are absent; only 53 of 6,468 NPC templates are actually placed in the world; dungeons 144 and 157 have rooms on maps that don't exist; 379 spell levels reference spells that aren't in the spells table; and 11 maps have fight geometry but no walkable cells.
- Note on dofusdb.fr: it was **not** used as a comparison source. Its licence expressly forbids feeding its data to AI agents or AI-driven pipelines, and its catalogue is Dofus 2, not the Dofus 3 Unity this emulator runs. The dofusdude dumps — already consumed by the project — are the correct Dofus 3 reference, and the audit validates against them.

## [3.6.10.10-admin.5] - 2026-08-20

### Fixed
- **"The server is not responding" shown while the server was running.** The launcher's status check reused pooled HTTP connections that the server's web layer closes after a period of inactivity; when a poll happened to grab one of those dead sockets, the request failed instantly and the launcher concluded the server was down. Requests now open a fresh connection each time (negligible on localhost) and a transport failure is retried once; the pre-launch liveness check additionally asks twice before giving up. One confirmation is still enough to declare the server up.

### Other
- Merged the upstream pull request (jobs, recipes/skills, interactive-object dispatch, catalog lookups, traffic-log naming, and a security bump of the SQLite native library). The merge keeps every change listed below — the admin tool, interface zoom, packet decoding and the log improvements — with the metrics column layout taken from upstream and our version label, log filter and zoom buttons carried on top of it.

## [3.6.10.10-admin.4] - 2026-08-20

### Fixed
- **Cell edits now really save on Enter.** The row-update statement was built without a WHERE clause: it tried to write every row of the table at once, collided with the primary-key constraint, failed and reloaded the table — so the cell visibly went back to its old value and nothing reached the database. Updates now target exactly one row (by primary key, or by the hidden row id on tables without one), which also repairs deletion on those tables.

### Changed
- Pressing Enter after an edit now saves and stays on the same cell instead of jumping to the row below; Escape still cancels without writing.

## [3.6.10.10-admin.3] - 2026-08-20

### Fixed
- **The admin tool's table editing is stable again.** Double-clicking a cell could break the whole grid ("reentrant call to SetCurrentCellAddressCore", a red-crossed empty panel and, at worst, a crash dialog), which is what blocked changing an account's role from the table. The editor now opens after the click has fully finished, and reloading a table first closes any open edit and releases the current cell. Editing an account's `Role` to `4` (administrator) works from the grid and takes effect immediately, since the server re-reads the role from the database on every request.

### Added
- **Tables without a declared primary key are now editable too**: they are anchored on SQLite's built-in `rowid` (shown hidden, read-only), so every table in both databases can be edited and deleted from, not just the well-declared ones.
- **Server window: log filter.** A filter field next to the auto-scroll checkbox; with text in it, only matching lines enter the console — for finding one error in a long session. The log file on disk keeps everything.
- **Server window: the console log now renders in the selected language.** With EN or FR chosen, the lines the emulator writes in Spanish are translated as they are displayed (startup, fights, movement, control-channel messages); fragments not yet in the table show as written. The log file itself stays in the original Spanish.
- **Launcher and server polish**: hover tooltips on the music, zoom and language buttons (each language named in itself); the launcher's client-path button reveals the full path on hover instead of the middle-truncated one; a version mark ("JONDO EMULATOR · v3.6.10.10") in the launcher's bottom corner and under the server window's logo — the first thing worth seeing in a version-locked emulator.

## [3.6.10.10-admin.2] - 2026-08-20

### Added
- **Decoded and handled 26 client messages** that the server logged as "UNHANDLED CLIENT PACKET" during a full live session. Four have measured answers from the official protocol and are now answered like the real server does: the world-content request (`kmr`) gets the map block (with the once-per-entry guard), the world-entry closing (`lzh`) gets its empty `lzl` reply, each progress request (`ieo`) gets an `idu` reply, and the map-load retry family (`knm`/`kno`/`kny`) gets the map actors and the completion mark again. The remaining 22 are now recognised by name and answered with silence — which is what the real server does to them — instead of dumping their bytes to the log as errors. Findings are documented in `docs/opcodes.md` §4.4.
- **Admin tool: direct cell editing.** Double-click a cell to edit it, press Enter to save to the database, Escape to cancel without writing, Del to delete the row. Rows are still identified by the table's primary key, and a save with an unchanged value writes nothing.

## [3.6.10.10-admin] - 2026-08-20

### Added
- **Jondo Admin**, a new management tool for the emulator (`Jondo.Unity.Admin`). It has two tabs:
  - **Database**: browse and edit `world.db` and `auth.db` directly. Table list, paged row view, cell editing (rows are identified by each table's primary key), row deletion, an optional WHERE filter, and a free-form SQL console for anything else.
  - **Live server**: connects to the running emulator through the control channel using an administrator account. Shows every live session (account, character, level, map, cell, whether in world or in combat), can kick a session, run live commands on a connected character (`.kamas`, `.level`, `.teleport`, `.size`, `.shop`), broadcast a chat line to everyone in the world, and follow the server log in real time.
- Live administration routes on the server (`/api/admin/sesiones`, `expulsar`, `comando`, `difundir`), all requiring the administrator role and checked against the database on every call, like the existing shutdown and role routes.
- **Interface zoom** for the launcher, the server window and the admin tool: new `A–` / `A+` buttons that scale the whole interface (text and layout) for high-resolution screens where everything rendered too small. The chosen size is remembered separately by each program, applies immediately, and the admin tool starts from the size already chosen in the launcher or server window.

### Changed
- All admin tool text is now in English (tabs, buttons, labels, grid columns, hints and status messages).
- The server window's metrics, buttons and log used to scale text twice on displays above 100% scaling (text grew larger than its container). Fonts now scale once, consistently with the layout.
- The server preferences file (`servidor.cfg`) now stores settings as `key=value` pairs instead of a bare language code; existing files are converted automatically on first save.

### Fixed
- Closing the launcher window now ends the launcher process. Previously the window closed but the process could stay alive in the background (held by the audio engine's own threads), leaving nothing to click and requiring a force close from the Task Manager. The launcher now shuts itself down as its final act once its window is closed and its files released.
- The server log no longer reports a "Thrift processing error" every time a client closes its connection — a normal disconnect is now logged as such instead of as an error.
- The `jsq` message (the "you may cross the map border" reply) is no longer labeled `[Unknown]` in the traffic log; the emulator itself sends it on every map-edge exit.
- `emulator_console.log` is now written with a UTF-8 byte order mark, so packet-dump symbols no longer appear as garbled text (`ðŸ“¦`) when the file is opened in a text editor.

### Notes
- Database edits are read by the server when data is loaded: to change a character that is currently connected, use the live commands in the admin tool's "Live server" tab, which reach the player's own session immediately.
- The admin tool deploys to the emulator root next to the other executables ("Jondo Admin.exe") when its project is published, matching how the launcher and the server deploy.
- Two known gaps surfaced by the logs remain open and are tracked as work in progress in the README: the unimplemented spell effects 406/120 (glyph and trap families) and the client message `jqe`, which appears in captures but has not been identified yet.
