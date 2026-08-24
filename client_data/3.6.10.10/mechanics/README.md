# External mechanics

This directory contains editable, version-pinned mechanic maps. It is deliberately separate from
`bases/*.db`: it holds static/game-design input; databases hold accounts, characters, inventory,
ownership, quest progress and other player/runtime state.

`manifest.json` is the only entry point. The server rejects the entire manifest if its
`clientVersion` is not `3.6.10.10`, a listed file escapes this directory, a mechanic ID is
duplicated, or its source URL is not HTTPS. Restart the server (or call the future admin reload)
after edits.

Each mechanic must be `Draft` until all of the following are recorded:

1. the exact Dofus pour les Noobs guide URL and a concise paraphrase of the rule;
2. matching client dungeon, map, monster, spell and/or effect IDs;
3. a captured client request/reply sequence for the flow; and
4. an executable server handler plus acceptance test.

`Verified` means the evidence is reviewed. It **does not** automatically execute a combat rule:
the fight handler must explicitly implement the rule kind. This stops a guide page from silently
changing fights or generating guessed packets.

To create a non-active draft from a specific guide page, run:

```powershell
py tools/build_mechanic_map.py --url "https://www.dofuspourlesnoobs.com/<page>.html"
```

The tool stores only the URL, title and retrieval timestamp; it does not copy the guide article.

## Monster data and dynamic AI policy

`monsters/client-baseline.json` is generated from the pinned client import with
`py tools/export_monster_mechanic_baseline.py`. It contains every monster's numeric ID, name ID,
grades, resistances, spells, observed roleplay map locations and group-size distribution. It is
reference data, not a guessed combat rule.

For an encounter-specific rule, copy `monsters/monster-mechanic-template.json`, add it to the
manifest and keep it `Draft` until the guide, client IDs, protocol capture and test are recorded.
A `Verified` `monster-mechanic-map` can currently configure only the generic AI's
`spellPriorities` and `fleeBelowHpPercent`; those values are loaded at runtime. New effects,
invulnerability, wave, summon, positional or state rules require a measured generic engine rule
before they may be enabled.

## Incarnam coverage

`incarnam/content-coverage.json` is the generated area-45 contract. It lists every client map,
monster, spell/effect set, dungeon room, relevant quest definition and quest-start NPC template.
It also lists runtime gaps. `Verified` on this file means the inventory and the gap declarations
were checked against the pinned client; it does **not** mean NPC placement, quest state, glyphs,
invisibility, or special dungeon rules have been invented or silently enabled.

Two reviewed server-owned binding documents complement that client inventory:

- `incarnam/workshops.json` contains 30 workshop placements from Giny.NETCore. Every row is
  accepted only when its map, element, cell and graphic match the exact 3.6.10.10 extraction and
  its skill/recipe count matches the pinned profession catalogues. One stale source row is kept in
  the rejection log instead of being loaded.
- `incarnam/npc-spawns.json` contains the five current Incarnam placements found in that public
  server dataset. Historical Symbioz 2.38 placements are intentionally not imported: several
  conflict with the current source's NPC or cell on the same map.

The 30 stations are registered at runtime and open the native craft interface through the exact
3.6.10.10 `kgq` client path. Static recipes are resolvable, but executing a recipe remains disabled
until the current ingredient/change/result request aliases are proven. The five NPC bindings add
authoritative placement only; dialogue branches and quest mutations are separate server mechanics.
