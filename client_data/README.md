# Client-data snapshots

Each subdirectory is an immutable, version-pinned extraction from an installed Dofus client. It is a staging area, not a live server database.

`3.6.10.10/catalogs/` contains 204 raw static DataRoot catalogs, including achievements, objectives, rewards, quests, NPC-related catalogs, items, monsters, jobs, skills, recipes, dungeons, maps, and interactives. `3.6.10.10/world/` contains the separately validated map interactive-element, graph-transition, and house-template extracts.

Run `py tools/extract_client_snapshot.py` to create a fresh snapshot. A server feature may consume a file only after its protocol, ownership/state rules, and regression tests are implemented; static client data does not by itself prove NPC placement, dialogue, quest progression, transaction rules, or combat mechanics.
