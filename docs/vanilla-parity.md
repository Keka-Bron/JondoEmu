# Vanilla-parity sources and order of work

There is no trustworthy single website that describes every server-side Dofus rule.  Static game
data, player-facing walkthroughs, and exact network/state behaviour are different kinds of
evidence.  Jondo therefore uses each source for the part it can prove.

| Source | Use it for | Do not use it as proof of |
|---|---|---|
| Installed pinned 3.6.10.10 client and its extracted bundles | maps, elements, transitions, templates, NPC/monster/item/job/quest catalogues, protocol and client expectations | account-owned state, economy, ownership, NPC dialogue branching or combat outcomes that are server-authoritative |
| [Dofusdu.de Dofus 3 API](https://docs.dofusdu.de/dofus3/v1/) | version-checked, multilingual item families, sets, mounts and Almanax; use as an import cross-check and localization supplement | NPC/monster/quest catalogues, placement, dialogue, quest progression, dynamic state, mechanics hidden in server scripts |
| [dofus-sqlite](https://github.com/ledouxm/dofus-sqlite) | disposable snapshot/reference database for quests, achievements, translations, map positions and interactable locations | a version-pinned source of truth; it updates independently of the emulator client |
| [Dofus pour les Noobs](https://www.dofuspourlesnoobs.com/) | human-authored quest, dungeon, boss, achievement and unusual-mechanic specifications | packet shapes, database IDs or state mutations without a client/server capture |
| [Official Dofus source map](official-dofus-source-map.md), patch notes and in-game testing | dated rule changes, published gameplay intent and final gameplay validation | a replacement for the client data or an executable protocol specification |

The current client version remains the authoritative *version lock*: every import must match
`Dofus_Data/StreamingAssets/version` before it is accepted.  Community references are a design
brief, not a reason to invent packets.

## Delivery order

1. **Fresh character and Incarnam.** A new character starts clean at Incarnam: level 1, zero
   kamas, zero scrolls, no equipment, no quests and no achievements.  Implement the tutorial only
   after capturing its NPC dialogue, quest-state changes and rewards.
2. **World access.** Import exact map transitions and interactives; model bank, zaap, workshop,
   marketplace, house and dungeon entrances by their measured C2S/S2C flows.
3. **NPC services.** Add a persistent dialogue/service engine.  Start with bank NPCs, then shops,
   workshops and quest NPCs.  Each service needs an authenticated character context, proximity
   check, transaction and persistence.
4. **Quest and achievements.** Store definitions separately from per-character state.  Implement
   a measured empty journal, start/step/complete/reward packets, criteria evaluation and replay
   only the active character's own state.
5. **Combat mechanics.** Import monster spells/data first, then add mechanics as named, tested
   encounter scripts.  Dungeon bosses, invulnerability phases, wave fights, puzzles and special
   deaths must not be approximated from a guide alone.
6. **Economy and social systems.** Banks, inventory, jobs, recipes, exchanges, markets, houses,
   guilds and mounts need server-authoritative transactions and durable per-character/account
   data.

The parity tracker should only mark a function implemented when its client action, validation,
persistence and observed reply are all known.  A guide can tell us *what the mechanic should do*;
the client data and captures establish *how this client asks for it*.

## DofusDude import snapshot

Run `py tools/fetch_dofusdude_dofus3.py` to download the documented Dofus 3 API catalogues to
`client_data/3.6.10.10/dofusdude/en/`.  The tool refuses a response unless its meta endpoint
reports exactly `3.6.10.10`; it stores source URLs, counts and SHA-256 hashes in its manifest.
Those files are an external static-data supplement, never an automatic `world.db` migration.
