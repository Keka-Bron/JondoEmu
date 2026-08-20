# Data audit — what the emulator holds, and whether it fits together

Run on **20 August 2026** against the working tree after the professions pull
request. Everything here was counted at that moment by `tools/auditoria_datos.py`,
which re-runs the whole thing in one command.

Two questions were asked. *What is in the data layer?* — counted file by file and
table by table. *Does it fit together?* — every cross-reference between the two
sources (the client dump behind `datos/` + `world.db`, and the dofusdude catalogues
behind `item_sets.json` and the professions import) was followed and the orphans
listed.

A third question — *how does it compare to dofusdb.fr?* — has an answer, but not
the expected one. See the end.

---

## 1. What is there

The counts match the README's claims everywhere the README makes a claim. The
emulator's data comes from the 3.6.10.10 client bundles and packet captures, so
against *this version of the real game* it is the game's own data, not an
approximation of it.

| Category | Where | Count |
|---|---|---:|
| Item templates / effects | `world.db` | 21,748 / 66,294 |
| Item sets | `datos/item_sets.json` (dofusdude) | 929 |
| Spells / levels | `world.db` | 17,113 / 34,823 |
| Monsters | `world.db` | 5,134 |
| Maps / walkable / fight cells | `world.db` + `datos/` | 15,360 / 17,211 / 17,222 |
| Sub-areas | `world.db` | 562 |
| Dungeons / rooms | `world.db` = `datos/dungeons.json` | 187 / 763 |
| Zaaps | `datos/waypoints.json` | 62 |
| NPC templates / shops | `world.db` / `datos/npc_shops.json` | 6,468 / 51 NPCs |
| Cosmetics | `datos/cosmetics.json` | 2,420 (12 types) |
| Mounts / mascoturas / colours | `datos/` | 520 / 22 / 54 |
| Titles / ornaments | `datos/titles_ornaments.json` | 539 / 167 |
| Heads / breed looks / breed stats | `datos/` | 638 / 19 / 19 |
| Haven bag themes / furniture | `datos/havenbag.json` | 48 / 4,083 |
| Experience levels | `datos/character_xp.json` | 1,889 |
| Opcode index | `datos/indice_3.6.10.10.json` | 2,169 |

## 2. Does it fit together — the cross-checks

Every reference between sources was followed. The id spaces of the client dump and
of dofusdude agree everywhere they overlap — which is the independent, external
validation this audit could run.

| Reference | Measured | Orphans |
|---|---:|---:|
| `item_sets` → item templates (dofusdude → client) | 3,597 item refs | **0** |
| `cosmetics` → item templates | 2,420 ids | **0** |
| `mounts` → item templates | 520 ids | **0** |
| `MapMobs` member monsters → templates | 38,744 groups, every member checked | **0** |
| Zaap waypoints → maps | 62 | **0** |

One measurement note, so nobody trips on it again: the JSON files key their
objects by **string** ids (`"7807"`), the database stores **integers**. A naive
set difference reports everything missing. Convert first.

## 3. The gaps — real, and each with its evidence

1. **The professions tables are empty.** `Jobs`, `Skills`, `Recipes`,
   `RecipeIngredients`, `SkillCraftableItems` and `SkillModifiableItemTypes` hold
   0 rows. The pull request that added them reads
   `datos/JsonFromDofusDude/{jobs,skills,recipes}.json` — the dofusdude dumps —
   and that folder does not exist, so the import silently skips (the only trace is
   the startup line `[Skills] Falta …`). **Fix:** drop the three dofusdude dump
   files into `datos/JsonFromDofusDude/` and restart; the import runs by itself.
2. **The world is nearly NPC-empty.** 6,468 NPC templates exist and **53** are
   placed on maps (0.8%). The README's "6,468 templates with spawns" is true of
   the templates, not of the world; any feature that expects NPCs to be *there*
   (quests, shops beyond the 51 with prices) has almost nobody to talk to.
3. **Two dungeons have broken rooms.** Dungeons 144 and 157 have their first two
   rooms (positions 0–1) on maps `232784389`, `232785413`, `232786435`,
   `232787459`, none of which exist in `MapTemplates` — ids beyond the world's
   15,360. Entering those two dungeons will fail at the map change.
4. **379 spell levels point at spells that don't exist** (`SpellLevels.SpellId`
   not in `Spells`: ids 0, 2873, 2874, 2900, 2981, …), and `SpellTemplates`
   carries a placeholder id 0 that `Spells` does not. Harmless today — nothing
   reads them — but they are the first place to look if a spell ever resolves
   wrong.
5. **Map 0** exists in `MapTemplates` with no sub-area (placeholder row), and
   **11 maps** have fight cells but no walkable data — fights would start on
   geometry nobody can walk.

Informational, not a defect: `map_walkable_cells.json` covers **1,856 map ids
beyond** the world's 15,360 — client instance maps the emulator's world does not
use. And `spell_variants.json` is still the raw Unity asset (keys `m_Enabled`,
`m_GameObject`), not a parsed table.

## 4. Why this does not compare against dofusdb.fr

The request was to scrape dofusdb.fr as the reference for "the real game's data".
Two reasons it was not done, neither of them technical laziness:

1. **Their licence forbids it.** The API's own landing page serves the
   LPNC-IA 1.0 / NCPUL-AI 1.0 licence, which expressly prohibits feeding the data
   to AI agents or ingesting it through AI-driven pipelines — which is exactly
   what an automated scraper built here would be. It also excludes projects
   predominantly generated by AI from any licence at all.
2. **It is the wrong game.** DofusDB's catalogue is Dofus 2; this emulator is
   Dofus 3 Unity 3.6.10.10, whose ids, items and spells were rebuilt from zero.
   A count-for-count comparison against Dofus 2 numbers would measure the
   difference between two games, not the emulator's completeness.

What the comparison needed already exists closer to home: **dofusdude** publishes
Dofus 3 dumps, the emulator already consumes them (`item_sets.json`,
`DofusDudeCatalog.cs`, the professions import), and §2 is exactly that
cross-check — passed with zero orphans on every reference it covers. If a wider
manual comparison is ever wanted, the dofusdude dumps are the source that is both
Dofus 3 and licensed for it; fetch them by hand, and `tools/auditoria_datos.py`
will keep validating the joins.

*(If dofusdb data is ever used by hand: their licence requires the attribution
"Data sourced from DofusDB. Use subject to NCPUL-AI 1.0.")*
