# Monster and boss mechanics

How the fight engine runs what the monsters' spells say — a boss's invulnerability and what lifts
it, the glyph that kills whoever stands in line, the confusion that turns a player's aim. None of it
is written per boss: it is the monsters' own spell data, the same rows the class spells are made of,
run by the one effect engine (`EffectEngine`, `FightHandler`).

Sources: the client's data in `bases/world.db` (`SpellLevels.EffectsJson`, `StatesCriterion`,
`MonsterTemplates`), the client's own texts in `Translations`, the captures, and the
dofuspourlesnoobs guide for what each boss is meant to do. Where a reading is inferred and not
measured, it says so below.

## The behaviour spell

Every monster grade names a `startingSpellId` — a `SpellLevels.Id`, its **behaviour spell**. 157 of
the 209 bosses have one; it is where a boss keeps what makes him one. Conde Kontatrás's Carrillón
(3642) makes him invulnerable for the whole fight, lays the time glyph at each of his turn starts,
confuses every player at theirs and alternates his odd and even turns.

It is cast when the fight begins (and when a monster joins one: a dream wave, a monster's summon),
announced like any cast. It is not an attitude: its triggered rows are **armed** (below), for good.

## Armed rows

A monster spell's row that waits on a trigger (`triggers` other than `I`) is armed at the cast on
**every fighter its mask and zone name**, and goes off on that fighter alone when the trigger happens
to him — the model of the game's own triggered buffs. The guide on Klim puts it plainly:
Carcassetagne "is applied to Klime and every monster: at the start of each of THEIR turns". Its
conditions (states, life, the attacker `O`, the telefragged `T`…) are read when it goes off; its
side, kind and zone when it is armed. A row's `delay` holds the trigger back ("not before turn 3"),
it does not delay what the row does. Rows armed by the behaviour spell with no duration last the
whole fight; others their duration, or to the next round.

The class spells keep the hooks their captures measured. The line is drawn at the spell:
`PlayerSpells` is every class spell and everything it chains, lays or summons; the rest are the
monsters'. So no class capture changes.

## Triggers

Fired now: `I`, `TB`, `TE`, `X` (death, with the killer as `O`), `DBE`, `DM`, `DR`, `D`, `DN DE DF DW
DA` (damage by element), `DBA` (by an ally), `DCAC` (melee), `DI` (by a summon), `CDN CDE CDF CDW CDA`
and `CDM` (the bearer deals damage), `H` / `CH` (healed / heals), `EON<n>` / `EOFF<n>` (a state goes on
/ comes off, including by expiry and by its caster's death), `P` (pushed), `TP` (teleported), `M`
(moved), `PD` (collision, pushed), `PPD` / `PMD` (somebody pushed into him), `APA` / `MPA` (AP / MP
lost), `TR<spell>` (a life threshold reached), `EK:<mask>` (someone the mask names died),
`EC:<op><n>:<mask>` (the count of what the mask names came true), `CCMPARR`, `Y`.

A hit on an **invulnerable** fighter still fires `D`, its element, `DM`/`DR` — Kontatrás is
invulnerable all fight and it is being hit that lifts it. Not `DBE`, which the Influencia capture
shows untouched.

Not fired yet: `DTB`, `DTE` (as triggers), `DS`, `DG`, `DT`, `DV`, `VE`, `CI`, `MS`, `MA`, `R`,
`EACT<n>`, `SREF`, `CMPARR`, `CDS`, `CC`, `DIS`, `CT`, `CDBE`, `XDM`, `CDCAC`, `DCCBE`, `LPU`.

Meanings that are **inferred**, not measured: `PMD` taken as the same collision as `PPD` (Obsidiantre's
lift is written on it and the guide lifts him by pushing into him); `DCAC` read as melee.

## Effects

Besides everything the classes use:

| Effect | What it does |
|---|---|
| 952 | Switches a state off while its row lives — how 15 bosses lose their invulnerability |
| 793, 1018, 1019 | Sub-casts: the candidate casts (793); the trigger's source casts at the candidate (1018) or at itself (1019) |
| delayed sub-casts, 950, 951 | wait their delay and happen when their round comes |
| 1099, 784, 1100, 1104-1106, 1101 | Teleports; onto an occupied cell they **swap** (telefrag), unless one is Indesplazable |
| 2872 | Life threshold: the blow stops at #1 % of the maximum and fires `TR<spell>` |
| 780, 1034, 147 | The last ally to fall comes back as the caster's summon, Zombi (state 74) |
| 402, 1165 | End-of-turn glyph; glyph |
| 2018 | Dispels the caster's glyphs (of the named spell) |
| 2023, 1026 | Sets the caster's runes / glyphs off now |
| 132 | Dispels every dispellable row |
| 140 | The bearer's next turn is lost |
| 1021, 1022, 1023 | Forced push, pull, swap: the Indesplazable too |
| 1036, 1045 | A spell's cooldown cut / set |
| 275-279, 85-89 | Damage: % of the caster's missing / current life |
| 1063-1066 | Fixed damage |
| 1067-1071 | Damage: % of the target's life |
| 1123-1128 | Damage: % of the blow that set it off |
| 1131-1140 | Damage: #2 per #1 AP / MP the target spent |

Glyphs laid by spells are shown to the client with the format measured on the classes' glyphs, in
the colour their row carries (Kontatrás's is 0, black); they last their caster's turns and go when he
dies. A monster's aura (1091) gives while one stands in it: leaving it takes its spell's rows away.

## Masks

`T` — telefragged by this spell ("se generan cuando dos entidades intercambian posiciones debido a
los efectos de teletransportación de un hechizo", the Xelor's sheet). `W` — a teleport of this spell
found no cell to land on: **inferred**, the reading that makes the guide true on Kontatrás ("if a
required symmetric cell does not exist, the fight ends with all characters dead"). `U` — the fighter
this cast summoned or revived, applied once it is on the board. `H` — enemy characters, as `L`.
The kind letters `m`/`M` do not include the caster.

## Other rules

- A spell level's `StatesCriterion` (`HS=56`, `HS!56`, `&`, `|`, brackets) is honoured by the monsters.
- A monster's spell runs its rows in the order they are written, blows included: "952, then the
  blow, then 406" lands its blow on a boss made vulnerable by the 952.
- Monsters respect their spells' initial cooldowns and "needs a free cell", and cast their mechanics
  spells (states, glyphs, teleports) once a turn.
- A monster's summons play their own turns.
- When a monster dies, the rows it put on others and the rows it armed go.
- A Pacifista deals no damage.

## Zones

Every letter is drawn the way the client's zone factory builds it (`Zone`, read from the shape
classes of the 3.6.10 GameAssembly): the cross family `P X Q + # *` (Q and # without their centre,
param2 counted in steps along each ray), the circles `C O I`, the lines `L /`, `T -` and `l` (its
param1 the first step and param2 the length), `U` (a V bent back towards the caster), the cone `V`,
the fork `F`, the squares `G W`, the boomerang `B`, the checkerboard `D` (D60 and D61 its two
colours), the rectangle `R`, the outside circle `Z`, and `;`, the cells named outright.

## What is not there yet

Waves and altars of the newest dungeons; effects 202, 1044, 2020, 786, 107 (damage returned), 1189, 2184, 2188, 2027, 2194; the
confusion effects 770-775 are shown but do not turn a player's aim.
