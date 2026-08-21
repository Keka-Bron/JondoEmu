# Official Dofus evidence map

This map makes official Ankama material useful without making the emulator depend on a changing
website.  It applies to the installed client version **3.6.10.10**.

The machine-readable registry is
[`datos/official_dofus_sources_3.6.10.10.json`](../datos/official_dofus_sources_3.6.10.10.json).
It defines the approved domains, the evidence each source can establish, and the things it must
never be used to infer.

## Source-of-truth order

1. The installed, version-pinned client and `client_data/3.6.10.10/` establish static data,
   identifiers, maps, interactive placement, world transitions and client protocol expectations.
2. Official Dofus release notes establish *what changed*, and on which published date/version.
3. Controlled traffic captures and in-game tests establish the server request, reply, validation,
   persistence and visible state transition.
4. DofusDude and community material supply cross-checks, translations and a human-readable
   gameplay specification only.

An official article can therefore create a parity requirement such as “this dungeon mechanic is
present”, but it cannot authorize an invented packet, a guessed reward, or a direct database
import.

## Monster and dungeon mechanics

The installed, pinned client extraction is the authority for monster IDs, grades, resistances,
spell IDs, spell range/line-of-sight constraints, map placement and observed group composition.
Run `py tools/export_monster_mechanic_baseline.py` to export those facts to
`client_data/<version>/mechanics/monsters/client-baseline.json`.

For human-readable encounter behaviour, [Dofus pour les Noobs](https://www.dofuspourlesnoobs.com/)
is the maintained guide source. Its [Akadémie des Gobs guide](https://www.dofuspourlesnoobs.com/akademie-des-gobs.html)
documents a zone-wide passive and spell presentations, while its
[Autel de la Déchireuse guide](https://www.dofuspourlesnoobs.com/autel-de-la-dechireuse.html)
documents wave and seasonal modifiers. Store a specific URL and a concise paraphrase only; do not
mirror guide text or images into the repository.

Guide pages do not prove packet fields, local client IDs, or hidden server timing. A mechanic can
become active only after its IDs and protocol/state behaviour are captured and a tested generic
rule exists. This keeps mechanics data-driven without turning guide prose or a guess into live
combat code.

## Official sources

| Source | Best use | Boundary |
|---|---|---|
| [Official updates](https://www.dofus.com/en/mmorpg/news/updates) | Dated changes to combat, dungeons, jobs, quests, economy and services | Record its stated version/date. It is not a protocol document. |
| [Official encyclopedia](https://www.dofus.com/en/mmorpg/encyclopedia) | Player-facing names, descriptions and manual QA comparisons | Current presentation may change; local client IDs remain authoritative. |
| [Official news and guides](https://www.dofus.com/en/mmorpg/news) | Tutorial and feature flow, NPC/service entry points, acceptance scenarios | Capture the live client flow before implementation. |

## Recording a page

Record a specific page, never a search result or an arbitrary third-party URL:

```powershell
py tools/fetch_official_dofus_evidence.py `
  --source official-release-notes `
  --url "https://www.dofus.com/en/mmorpg/news/updates/<specific-page>" `
  --tag dungeon --tag mechanic `
  --note "Requirement only; packet flow still needs a 3.6.10.10 capture."
```

The tool permits only HTTPS pages on the approved source host, rejects query strings and
credentials, caps responses at 8 MiB, follows only approved HTTPS redirects, and writes a compact
metadata record plus SHA-256 to `client_data/3.6.10.10/official/evidence/`. It does **not** copy
the article or write to `world.db`.

## Implementation gate

For every mechanic, bank/NPC interaction, quest or tutorial step, create a parity item containing:

- a local client/catalogue reference and the official evidence record, if one exists;
- the C2S action and observed S2C reply;
- proximity, ownership, criterion and anti-forgery validation;
- the exact account/character persistence change; and
- a replayable test from a clean level-1 Incarnam character.

Only then may the parity tracker mark the feature as implemented. This keeps official gameplay
information traceable while preventing website drift from silently changing the emulator.
