# Interactive elements

How Jondo declares clickable map elements, resolves a client's use request and dispatches it to
the correct game behaviour without mixing maps or game sessions.

This document describes the generic interactive registry introduced in August 2026. Zaaps,
haven-bag chests and the haven-bag lottery are the first three actions migrated to it. Their
protocol behaviour did not change: the registry centralises discovery and routing, while their
existing handlers still build the same replies.

The architecture follows the useful separation also found in the Dofus 2.68 Giny server:
an element definition, an action attached to it, and one dispatcher for use requests. Jondo does
not copy Giny's protocol classes or database model because Jondo targets Dofus 3.6.10.10 and uses
the protobuf messages measured for that client.

---

## 1. What an interactive element is

The map data already tells the client that a graphical element exists. For each element Jondo
extracts three values into `datos/interactive_elements.json`:

| Value | Meaning |
|---|---|
| `Element.Id` | The map element's `m_interactionId`; this identifies what was clicked |
| `Element.Cell` | The cell on which its stated element is placed |
| `Element.Gfx` | The graphical identifier used to recognise known objects such as zaaps |

Those values are not enough to make the element usable. The server must additionally tell the
client:

- the interactive type, such as zaap or chest;
- the skill offered by the element, such as use or open;
- a skill-instance identifier that the client returns when it uses that skill;
- the current state of the element.

The client data therefore answers *where and what graphic is present*. The server registry answers
*what the player can do with it*.

The raw client elements and zaap lookup remain in
`Jondo.Unity.Launcher/Managers/Interactives.cs`. The server-owned declarations are represented by
`RegisteredInteractive` and `InteractiveAction` in
`Jondo.Unity.Launcher/Managers/InteractiveRegistry.cs`.

---

## 2. The registered model

### 2.1 `RegisteredInteractive`

One registered element contains:

| Property | Meaning |
|---|---|
| `MapId` | Map on which this registration is valid |
| `Element` | Client element id, cell and graphical id |
| `Type` | Interactive type sent in the map block |
| `Actions` | Skills the element offers |

The real key is `(MapId, Element.Id)`. Element ids are not treated as server-wide identities: the
map is always part of the lookup.

An element can hold several actions even though each migrated element currently offers one. The
map declaration writes every registered action into the element's repeated skill field.

### 2.2 `InteractiveAction`

Each action contains:

| Property | Meaning |
|---|---|
| `Kind` | Internal behaviour selected by the dispatcher |
| `SkillId` | Protocol skill id advertised to and returned to the client |
| `SkillInstanceId` | Instance id used to validate an `iwo` request |

`InteractiveActionKind` currently contains `Zaap`, `Chest` and `Lottery`. This enum is an internal
routing choice; it is not a value sent over the network.

The first action on an element keeps the historical stable instance formula:

```text
(elementId % 900000) + 10000
```

If a future element has more than one action, subsequent actions receive the next unused instance
ids. The existing three interactives therefore keep exactly the instance ids they had before the
registry.

### 2.3 Current registrations

| Action kind | Element discovery | Type | Skill | Handler |
|---|---|---:|---:|---|
| Zaap | Known zaap graphic, haven-bag zaap or explicit override | 16 | 114 | `ZaapTravelHandler` |
| Zaap (vestige) | Graphic 74685 outside a haven bag | **359** | 114 | `ZaapTravelHandler` |
| Chest | Graphic 12367 on a haven-bag map | 85 | 104 | `ChestHandler` |
| Lottery | Graphic 51031 on a haven-bag map | -1 | 184 | `LotteryHandler` |
| Zaapi | Graphics 70520/70521 (Bonta), 304418 (Brakmar) | 106 | 157 | `ZaapiTravelHandler` |
| Bin | Graphics 8438, 46529, 63081, 260022 | 105 | 153 | `BinHandler` |
| HouseDoor | Any of 37 graphics the captures declare type 300 | 300 | 84; 84, 97, 98, 100, 108 on the 37 doors that can be owned, offered per viewer | `HouseHandler` |
| HouseExit | The chosen exit element of a house interior | 316 | 184 | `HouseHandler` |
| HouseChest | Graphics 12367 and 46581 inside a house interior | 85 | 104, 105, offered per viewer | `HouseHandler` |
| GuildChest | Graphic 70671 (the banks' guild chest) | 388 | 184 | `GuildChestHandler` |
| Teleport | Exact `(mapId, elementId)` kept from Giny 2.68 after 3.6 validation | 0 | 114 | `TeleportHandler` |

Every one of those `(type, skill)` pairs is measured, not chosen: `tools/tipos_interactivos.py`
cross-references the 304 packet captures against the client's element dump and yields
`graphic → type` for 415 graphics. **30,706 elements are registered** at startup: 26,987 of them
zaaps, chests, bins, house doors and gatherable resources, plus 3,719 generic teleport routes
(section 11).

Two of those rows deserve a note.

**The vestige is not a zaap.** Graphic 74685 was declared type 16 for a long time and the
captures say 359 — every single time it appears. It is the spot where a temporal anomaly
surfaces, not a switched-off waypoint (see `world.md` 2.6). The exception is the five haven-bag
decors that carry that graphic and no other: inside a bag it *is* the exit zaap, so it stays
type 16 there. `Interactives.TypeOfZaap` draws exactly that line.

**A house door is not just any door.** Type 300 is what carries the three dwelling skills —
enter (84), access code (100) and put up for sale (98, or 108 when already listed). A building
that is scenery comes as type −1 and cannot be clicked at all. Of the 40 graphics ever seen as
type 300, Jondo uses the **37 that were *always* type 300**; the other three have shown a second
type and stay out until a capture settles them.

---

## 3. Startup and registration

`Program.cs` initialises the relevant data in this order:

```text
Interactives.Initialize()
    -> load map elements, waypoints, sub-area levels and zaap overrides

Merkasako.Initialize()
    -> identify haven-bag maps, themes, chests and their local zaaps

InteractiveRegistry.Initialize()
    -> register zaaps
    -> register haven-bag chests
    -> register haven-bag lottery machines
```

The registry must run after both data managers. Running earlier would make haven-bag discovery
incomplete.

`InteractiveRegistry` builds two indexes:

```text
mapId -> ordered list of RegisteredInteractive
(mapId, elementId) -> RegisteredInteractive
```

The first index is used to build a map response and to resolve an instance when proto3 omitted the
element id. The second performs the normal exact lookup.

Registration order is deliberate: zaaps first, then chests, then lottery machines. That preserves
the order previously written into the map's `jss` message.

After startup, packet handling only reads the registry. Zaap, chest and lottery discovery is no
longer repeated inside the network dispatcher.

---

## 4. Declaring interactives in `jss`

When the client requests a map, `ConnectionProtocol.AddInteractiveElements` asks
`InteractiveRegistry.OnMap(mapId)` for the registered elements. Each one produces an `f11`;
stateful elements additionally produce an `f15`:

```text
jss.f11 {
    f1: 1
    f4 repeated {
        f1: skill instance id
        f2: skill id
    }
    f5: element id
    f6: interactive type
}

jss.f15 {
    f1: 1               // current state
    f2: element cell
    f3: element id
}
```

`f11` describes what the element is and which actions it offers. `f15` carries the current state
for stateful elements. Official captures show that `f15` is only a subset of `f11`: exits such as
element 515742 (`gfx 3520`) are declared by `f11` without any `f15`.

The migration changed the source of these fields, not their values or wire order. A zaap still
emits type 16 and skill 114; the chest still emits type 85 and skill 104; the lottery still emits
type -1 and skill 184.

---

## 5. Using an element: `iwo`

All client interactive-use requests enter through the `type.ankama.com/iwo` branch in
`Network/GameNodeProxy.cs`. That branch now calls only
`Handlers/InteractiveActionHandler.UseAsync`.

The relevant request fields are:

```text
iwo {
    f1: skill instance id
    f2: element id
}
```

The dispatcher obtains the map from `SessionContext.State.MapId`, then asks:

```csharp
InteractiveRegistry.TryResolveUse(
    mapId,
    elementId,
    skillInstanceId,
    out interactive,
    out action);
```

Resolution follows these rules:

1. If the element id is present, `(mapId, elementId)` must exist.
2. If the skill instance is also present, it must belong to that registered element.
3. If the element id is absent but the skill instance is present, it must identify exactly one
   action on the current map.
4. If both proto3 fields arrive as zero, the previous zaap fallback is retained: the registered
   zaap on that map may be selected.
5. An unknown, ambiguous or contradictory request is logged and ignored.

The validation matters. Looking only at the last clicked element, or looking up an element without
its map, can execute the wrong action. Looking only at the skill id cannot distinguish two elements
that offer the same operation.

Identifiers outside the signed 32-bit range are rejected before registry lookup.

---

## 6. Dispatch and unchanged behaviour

Once an action has been resolved, `InteractiveActionHandler` selects its existing handler:

```text
InteractiveActionKind.Zaap
    -> ZaapTravelHandler.OpenAsync(...)

InteractiveActionKind.Chest
    -> ChestHandler.OpenAsync(...)

InteractiveActionKind.Lottery
    -> LotteryHandler.DrawAsync(...)
```

The resolved element id and skill id are passed to the concrete handler. The handlers continue to
own their game behaviour:

- `ZaapTravelHandler` opens the destination list, calculates travel cost and changes map;
- `ChestHandler` opens storage and moves items between the chest and inventory;
- `LotteryHandler` creates and announces the prize.

Their first server reply remains an `iwn` element-in-use message with the same element, skill and
character. All following zaap, storage and lottery packets retain their previous builders and
ordering.

The old routing checks inside `ZaapTravelHandler` were removed. A zaap handler no longer needs to
ask whether the click was secretly a chest or a lottery machine; that decision belongs to the
generic dispatcher.

---

## 7. Sessions and socket isolation

The interactive registry is shared because its definitions are immutable world data. Player
interaction state is not shared.

For every incoming `iwo`:

```text
owning NetworkStream
    -> owning GameSession
        -> SessionContext.State.MapId
            -> registry lookup on that exact map
                -> reply written to the same stream
```

The registry stores no current character, current map, open dialog or socket. Those values remain
in the `GameSession` and its `SessionState`:

- `OpenZaapMapId` belongs to the session that opened the zaap;
- `IsChestOpen` belongs to the session that opened the chest;
- character id, inventory and map belong to that same session;
- handler replies use the `NetworkStream` that delivered the request.

Consequently, two accounts can use different interactives at the same time without one client's
element or map becoming the other's. The registry is a read-only catalogue; it is not a replacement
for session ownership. See `sessions.md` for the complete socket/session lifecycle.

---

## 8. Adding another interactive

Buildings, doors, workshops, crafting stations and resource nodes should use the same path. Do not
add another top-level `iwo` branch and do not put its routing inside the zaap handler.

The minimum implementation sequence is:

1. **Identify the element.** Add a reliable data source or lookup that returns the correct
   `Interactives.Element` for a map. A graphical id is sufficient only if it unambiguously means
   the same object in the relevant maps.
2. **Verify the protocol values.** Determine the interactive type, skill id, request fields and
   response sequence from the matching 3.6 client data or captures. Values from a 2.68 emulator
   are architectural hints, not proof for 3.6.
3. **Add an action kind.** Extend `InteractiveActionKind` with the new server behaviour.
4. **Register it at startup.** Add a registration pass to `InteractiveRegistry.Initialize`, after
   the manager that supplies its data has been initialised. Keep ordering intentional.
5. **Implement its handler.** The handler should receive the resolved element/action data and
   should own only that feature's behaviour.
6. **Dispatch it.** Add the new action case to `InteractiveActionHandler`.
7. **Keep state session-local.** Open dialogs, selected recipes, resource timers owned by a player
   and similar mutable values must not be static current-player fields.
8. **Extend regression checks.** Verify that every declared element resolves back to its action and
   that a mismatched instance is rejected.

A future element with several skills should be represented by one `RegisteredInteractive` with
several `InteractiveAction` entries. It must not be declared several times as unrelated elements
with the same `(mapId, elementId)`.

### Data before behaviour

Do not declare every unknown map element as a door, workshop or resource merely to make it
clickable. `interactive_elements.json` proves that an element exists, but not which server action
it offers. A wrong declaration changes the client's cursor and available action and can route an
unrelated decorative element into a game handler.

For a new category, first establish a checked mapping such as:

```text
(map, element or gfx) -> interactive type -> skill -> server action parameters
```

Doors additionally need their destination map and cell. Craft stations need the supported job or
recipe family. Resources need state, respawn timing, skill requirements and a server-authoritative
reward. Those parameters belong to the feature's data model, while the generic registry carries
the common element and action identity.

---

## 9. Validation and diagnostics

`RegressionGuardTests.AssertInteractiveRegistry` runs after all managers and the registry have
initialised. It checks that:

- the registry contains the same unique elements discovered by the migrated zaap, chest and
  lottery providers;
- every registered `(map, element, skill instance)` resolves to the same object and action;
- a deliberately mismatched skill instance is rejected.

An unresolved request produces a log containing its map, element and skill-instance ids:

```text
[Interactives] Uso desconocido: mapa ..., elemento ..., instancia ...
```

When debugging a new interactive, compare that line with the element advertised in the preceding
`jss`. The element id, skill instance and session map must describe the same registration.

The project compiles on .NET 10 after the migration. The remaining compiler warning is an existing
unused local variable in the network traffic logger and is unrelated to interactives.

---

## 10. Profession catalogues (3.6.10.10)

The first data layer for resources and workshops is now imported from the three dofusdude files
stored in `JsonFromDofusDude`: `jobs.json`, `skills.json` and `recipes.json`. `Paths` accepts the
folder either inside the emulator root or immediately beside it. The server reads the Unity export
wrapper (`references.RefIds[].data`) and copies the useful fields into `world.db` on startup.

The main tables are:

- `Jobs`: profession id, translation id, icon and legendary-craft flag;
- `Skills`: owning job, minimum level, gathered item, animation/range and client flags;
- `Recipes`: result, result level/type, owning job and skill.

Arrays are normalized into `RecipeIngredients`, `SkillCraftableItems` and
`SkillModifiableItemTypes`. Their `Position` column preserves the order from the 3.6 export. This
lets handlers query ingredients and quantities without parsing JSON stored inside SQLite.

Imports are transactional per catalogue. A malformed or empty source file rolls its import back
and the manager reloads the last valid catalogue from the database. The startup order is
`JobManager`, `SkillManager`, then `RecipeManager`, matching their dependencies. The current
3.6.10.10 files produce 23 jobs, 368 skills and 4,858 recipes.

The managers expose indexed, read-only catalogue objects:

```text
JobManager.TryGet(jobId)
SkillManager.TryGet(skillId)
SkillManager.ForJob(jobId)
RecipeManager.TryGetByResult(resultItemId)
RecipeManager.ForSkill(skillId)
```

`GatheringHandler.TryResolve` validates that a skill exists, belongs to a known job and actually
produces a gathered resource. `CraftHandler.TryResolve` resolves a workshop skill and its recipe
list; `TryResolveRecipe` additionally prevents a client from asking one workshop skill to execute
a recipe owned by another.

### 10.1 Workshop stations

The two pieces of evidence that section once asked for are in: the element-to-skill mapping comes
out of the captures, and the whole workshop cycle is captured in 3.6.10.10.

`tools/extract_workshops.py` writes `datos/talleres_3.6.10.10.json`, graphic -> type and craft
skills, from four sources in order of trust:

| Source | What it is | Graphics |
|---|---|---:|
| `jss` | a real `jss` declared the element with a craft or forgemagus skill (every skill it offers: the magus table 49506 offers three) | 21 |
| `use` | an `iwo` on the element answered by an `iwn` with the skill (Bonta's oven 49492, the mill 49511) | 2 |
| `pr44` | the Incarnam stations PR #44 read off the `jss` of its own JobsIncarnam captures | 15 |
| `inferred` | a graphic standing in two or more workshop interiors of one job only, that job having one craft skill | 16 |

A workshop interior is a map off the world map (`worldMap` -1) at the coordinates of a
"Taller de X" hint of the client's `HintsDataRoot`. The farmer, miner and maker have several
skills and the magus hint stands for six jobs, so none of them is ever inferred. `Workshops`
reads the table and `InteractiveRegistry` declares every element with one of those graphics:
577 stations. The type is the measured one, or the one most often measured for the same skill.

### 10.2 The cycle

```text
open    C iwo                       S iwn { f1: 1, element, skill, who }, ivx (kamas in f1), hlm {}, kgq { skill }
        C itr { f3: 2 }             S ivx, hlm {}                      (12 of 12 in the captures)
recipe  C kew { f2: result }        S kfb per stack: { f1 { f1: 63, f5: item }, f3: 0.0 }
move    C kcr { f1: ±n, f2: uid }   S kfb in, kex changed, kfs { uid } out
count   C kdx { f1: n }             S kgl { f1: n }
craft   C kep { f1: true, f2: step }
                                    S ium or ivj { f2 { left, 1 }, f3 { uid, left } } and kfs per stack
                                      itf { f1 { 63, item } } for new items, itu { f1 { uid, total } } for a stack
                                      kdr { f2 { f4: item }, f3: 2 }   (one craft: the whole item; several: { gid, n })
                                      isz on a new level, irq, iun, kgl { 1 } after several
        no recipe                   S kdr {}                            (the wrong gift paper)
close   C kla                       S khd { f3: 11 }, ivx, hlm {}
```

`kep`'s f2 is the exchange's step, not a quantity (25 in the tutorial, 1 to 5 in the grinder).
The count is `kdx`. A bench makes a recipe only with exactly its ingredients; the job must be at
the recipe's level, except for the "Base" job 1 (the grinder's fusions), which gives no
experience. An item whose template rolls anything is created one by one, each rolled in its own
ranges; one that rolls nothing joins a stack of the same thing.

Experience per craft is the formula players measured on the official server,
`⌊20 · recipe level / (1 + 0.1 · (job level − recipe level)^1.1)⌋`: the tutorial's level-1 ring
gives 20 at job level 1, as captured. A level-up sends `isz { f1 { f2: job, f4: skills }, f3:
level }` before the `irq`, from the workshop and now from gathering too.

### 10.3 Smithmagic

A magus table is a workshop whose skill `isForgemagus`. The item goes on it with `kcr` (it must
be of one of the skill's `modifiableItemTypeIds`); a rune is applied with `kcj { f1: rune, f3: 1,
f6: true }`, and every rune answers, in the captured order:

```text
kfb (the rune)   irq (only if it entered)   ivj or ium   kfs
kdr { f2 { f1: pool change 0/1/2, f3: pool, f4: item }, f3: 1 failure / 2 success }
kex (the item)   iun   kdb { f2: true }
```

The other way of the captures: the rune laid in its slot with `kcr`, then `kep` applies it (the
same answer without the `kfb` and the `kdb`). A signature rune laid on the table with `kcr` goes
with the next rune, spent either way, and signs only if that rune enters.

What a rune does is in `Forgemagic`. The weights are the client's `effectPowerRate`; the pool
takes the loss first and keeps what a whole point overshoots, as the 114 captured runes show. The
odds are the community's model, with its constants named in the code: 66/34/0 on a weak item,
43/50/7 at the perfect jet, a floor of 15/50/35, a rune's reach of 30·√weight past which it fades
to 1%, over and exo never past a weight of 101, exo AP/MP/range at 1% and only one of them. The
pool is kept as a hidden line of the item's effects (`-1`, hundredths of weight) and never goes on
the wire; with the signatures (988 "Fabricado por", 985 "Modificado por", from the signature rune
7508) it lives in the item's row and goes wherever the item goes.

### 10.4 A commission: maging someone else's item

`CommissionHandler`. Measured whole from the magus' side (two sessions, one of them paid) and from
the customer's up to accepting:

```text
invite   C kbl { f1: other, f2: skill, f3: 10 I am the magus / 11 I am the customer }
         S kgu { f1: other, f2: my role, f3: who invited } + hlm {}, to each of the two
         S kdv { f1: 3 }                       no table with that skill on the magus' map
refuse   C kla   S khd { f3: 11 } + hlm {}
accept   C kgi   customer: iss { the magus' job } + kgw { magus' level, skill }; magus: keg { skill }
offer    magus sees ked { item, f4: true }, kcl { kamas }, kgt { f3: ready, f4: customer }
table    C kgd { f1: ±1, f4: uid }   S keo + kfb onto the table, kfs + ked back to the offer
runes    the magus table's, on the customer's item, from either one's bag
close    C kla   magus: lqs { 64, "+", n } + lqn 594 + ivf when paid, kcl {}, khd, ivx, hlm
```

The customer's view of the magus' work is not captured: it is the magus' messages with the "the
other one did it" flag each of them has (`kfb` f2, `kfs` f2, `kex` f1, `ked` f4), set the way the
trade capture sets it on the side of the one who did not act. The payment comes from `kee`, the
kamas of a trade. The customer pays at the close if they were ready and a rune went onto their
item, which is exactly when the second captured session pays its 5,000.

### 10.5 Breaking items at the grinder

The grinder offers two skills in its `jss`: 121 fuses runes (an ordinary recipe) and 181 breaks.

```text
open     C iwo                        S iwn, kbv {}
lay      C kcr { f1: ±n, f2: uid }    S kfb (no float), kfs
break    C kbj { f2: true, f3: step } S kgt { f3: 1, f4: me }, ium per item, ivj or iua per rune,
                                        iun, kfp { per item: uid, its runes, its coefficient twice }
close    C kla                        S khd { f3: 11 }
```

Every characteristic gives runes of its base rune (the smallest of it), by the community's formula
`(3 · value · weight · level / 200 + 1) · coefficient / rune weight`, the fraction being the
chance of one more. It holds on the seven captured lines, with the coefficients the server itself
reported (40%, 66%, 236%). How the official coefficient moves is not known; here each template
starts at 100%, loses 1% of it per unit broken and grows back 2 points an hour (`Breaking`).

A focus turns the other characteristics into the focused one: its own weight plus half of every
other line's, all given in its rune (the rule players give on the official forum). No capture
focuses: `kbj` leaves two int32 unused, f1 and f4, and whichever arrives set is taken for the
focus -- an effect, or a rune -- with the raw frame written to the console until a real one
settles it.

### 10.6 The artisans' directory

`ArtisanHandler`, measured in four captures:

```text
settings C irl { f1 { f3: job, f4: free, f5: minimum level } }
         S isd { f1 (repeated) { f3: job, f4: free, f5: minimum level } }   every job, every time
listing  C kef { f2: [job] }          S iro { f1 { f1: job, f2: listed } }   a toggle
the book C iwo                        S iwn, kfj { f2: [the workshop's jobs] }
a job    C isr { f2: job }            S isf { entries }; isv when one comes, isq when one goes
close    C kla                        S khd { f3: 11 }
entry    { f1 { f2: job, f3: minimum level, f4: free, f5: job level },
           f2 { f1 { f1: 1 }, f2: name, f3: breed, f4: id, f5: sex, f7 { f1: map } } }
```

The settings live in `CharacterCrafterSettings` and replace, at the entry into the world, the
captured `isd` -- which gave every character the capturer's own minimum levels. A job never
touched is free with a minimum level of 1, as the capture's untouched jobs are. The directory
lists the base job and every job with a recipe, a resource or a magus table: exactly the 21 of
the real `isd`, the six magi in, Pergamago and Bestiólogo left out. The book of a workshop (graphic 13493, "Consultar", skill 170)
opens the jobs of the stations on its map, or of the workshop hint at its coordinates: the
farmers' book [28], the magi's the six magus jobs, as the captures show. Only connected artisans
are listed; the list is told when one joins or leaves it, or disconnects, but not when one
changes map.

### 10.7 Transcendence runes

The Infinite Dreams' "Runa Ta/Buta/Suta" (item type 211, 81 of them) say what they do in their
own template: the characteristic, 2827 "100% de probabilidades de éxito", 2825 "Ninguna forjamagia
futura", and a 2826 without text, 40, 60 or 80. The client's help text: they "cannot fail and make
the item insensitive to any other smithmagic". The rules, the way players describe them and the
way the 2826 fits the community's table to the unit:

- never on an item with an over or an exo, and never twice (the first one leaves 2825);
- a line the item already has may weigh at most 101 minus the 2826: 61, 41 and 21 strength; 8,
  5 and 3 AP reduction; 12, 8 and 4 elemental damage; 6 and 2 critical;
- they go on whole, nothing else lost, and the item then takes no rune;
- refused, the item stays as it was and the rune is not spent.

On a commission only the customer's own transcendence is accepted: the real game asks the
customer first ("¿Aceptas que el artesano fusione este objeto con una runa de trascendencia?")
and that question is not captured.

### 10.8 Forgegod mode

`.forjadios on | off` (`.forgegod` in English, `.forgedieu` in French), administrators only (role
5), for the session that types it until it is turned off or the character logs out. At the forge
of that character, and on a commission's table where it is the magus:

- every rune goes in clean: no failure, no loss, the pool untouched;
- no cap: an over or an exo past a weight of 101, two AP of exo or more, any number of exos;
- a transcendence goes on anything, over, exo or already transcended, and never fails;
- an item "sin forjamagia futura" takes runes again;
- a magus table takes any item, whatever its type;
- a recipe asks no job level.

Corruption runes, the double-edged ones of the same help text, do not exist in the 3.6.10 game
data: no item and no item type carries them. Ankama took them out of the Infinite Dreams'
rewards in 2.51, and only the text is left.

---

## 11. Generic teleport routes imported from Giny

Houses are deliberately excluded. A house entrance uses `jqw`, remembers the outside map in the
player's session and stays owned by `Houses`/`HouseHandler`; treating one as a plain `jru` teleport
would lose both that protocol and the return state.

The routes come from a phpMyAdmin export of Giny 2.68's `interactive_skills` table. Rows whose
action is `Teleport` are joined against the 3.6 `interactive_elements.json` by the exact
`(mapId, elementId)` pair, house doors and interior exits are removed, identical duplicates are
merged, and any source element for which Giny gives conflicting targets is disabled. The result is
the versioned `datos/interactive_teleports_giny_2.68.json`.

**Why a Dofus 2 dump works at all.** Dofus 3 inherited the map- and element-id space from Dofus 2.
4,657 of the 5,124 distinct maps in the dump (91 %) exist in this emulator's `world.db`, and every
cell id in it falls inside 0–559. The ids are not the problem; stale routes are, and those are what
the validation removes.

The file holds **1,678 candidate routes, 1,623 of them marked enabled**. Startup then runs the 3.6
checks the offline join cannot do: the source element's cell and graphic must still match, the
destination map must exist, the target cell must be in range, and the element must not already be a
zaap, zaapi, chest, lottery machine or bin. **1,585 survive** — the rest fall to 55 ambiguous
sources and 37 destination maps that no longer exist.

`TeleportManager.Initialize` transactionally replaces the `InteractiveTeleports` table from that
JSON and reloads only `Enabled=1` rows into immutable indexes. Rejected rows stay in SQLite with
their `ValidationStatus`, so a route that disappears can be looked up rather than guessed at. Note
that the table is rebuilt from the JSON on **every** start: editing a row by hand does nothing.

`InteractiveRegistry` declares each route with skill 114 and interactive type 0, exactly as Giny's
`.sun` command does. The `GfxId` is never used as the interactive type — it stays the map graphic
attached to the element. On an `iwo`, `InteractiveActionHandler` resolves the registered element and
calls `TeleportHandler.UseAsync`, which sends `iwn`, then `iwi`, an `iwf` state 0 and an enabled
`iwm` before loading the destination. Those three are not decoration: leaving only the `iwn` lets
the client cache the element as busy, and its graphic is gone the next time the player returns.

### What is deliberately not here

**No end-of-movement trigger.** The routes are also indexed by `(map, sourceCell)`, and the guard
uses that index to catch two routes landing on one cell — but nothing fires on stepping. The
original implementation hooked `jqi`, which the client only sends when walking into a **map edge**.
By the project's own cell geometry only **109 of the 1,585 routes (6.9 %)** sit on an edge cell, so
the other 1,476 could never fire; and the hook returned before answering `jsq`, which leaves the
character stuck on the border for good in those 109. It will come back when there is a real
end-of-movement signal to hang it on.

**No change to how other elements are declared.** `f11` is emitted only for elements with a
registered action, and `f15` only alongside it, exactly as before. Declaring an `f15` for all 46,309
elements in the world would be a protocol change affecting 9,840 maps, and it needs its own captures
to back it up.

### What the guard pins

Three concrete routes, with their numbers, so a change in the catalogue cannot pass unnoticed:
Astrub to the temple (191106048/515837 → 192416776 cell 534), the Astrub jeweller workshop round
trip, and a **stair** (graphic 62018) that takes exactly the same generic path as a sun — which is
the point: the graphic never decides anything. On top of that, every route must resolve to a
clickable `f11`/`f15` interactive, and the total element count must match, which is what makes an
accidental collision with a house door or a resource stop the server instead of corrupting a map.

Landing uses `MapManager.GetNearestWalkableCell`, so a destination cell that is no longer walkable
cannot strand the character. 314 of the routes (20 %) do land on a non-walkable cell and are
rescued that way, which means one route in five puts the character slightly off the mark.

### A second catalogue: the Dofus 2.73 world graph

Giny's table covers 1,585 routes. A second source fills part of the rest: `world-graph.binary`,
the navigation graph the Dofus 2 client uses to plan routes across the world. It is parsed by
`tools/extraer_world_graph.py` into `datos/interactive_teleports_worldgraph_2.73.json`, which
`TeleportManager` imports after Giny's — Giny always wins a conflict, because it carries a
measured arrival cell and the graph does not.

The format was worked out by hand and is documented in the tool. It holds 30,742 edges and 31,103
transitions; **5,719 of them are interactive** (type 32, skill 184). Three independent checks say
the reading is right: the parser lands **exactly** on the last byte of the file; every cell in it
falls inside 0–559; and where the graph and Giny describe the same route, **1,091 of 1,105 agree
on the source cell** and none agrees with the destination cell — which is also how we know the
graph's cell is the source one.

The graph carries an element id per interactive transition, and **5,563 of 5,719 match one of our
own elements on the very same cell**, so routes are keyed by element and not guessed from geometry.

**What it does not carry is the arrival cell.** It says which map you end up on, not where you
appear. That is approximated with the cell of the element that makes the return trip, and the
server snaps it to a walkable cell on landing. Measured against the 884 routes where Giny does give
the exact cell: 71 % land within one cell, 90 % within two, 96 % within three. Good enough to put
the character somewhere sensible, not good enough to be called exact — which is why those rows
carry `confidence = reverse-element-approx` and stay in their own file.

Routes with no return path are dropped rather than guessed: 1,357 of them. Together with 764 whose
maps are not in our world, 1,196 already covered by Giny and a handful of mismatches, **2,137
usable routes** come out of the graph, of which 2,134 survive import. Total: **3,719 active routes
across 2,655 maps.**

### Why none of this comes from the 3.6.10.10 client

It was looked for, and it is not there. The client ships the whole machinery —
`Core.PathFinding.WorldPathfinding` with `Vertex`, `Edge`, `Transition`, `AStar` and
`PathFindingData`, names and source paths perfectly readable in the IL2CPP metadata — but not the
graph itself. None of the ~200 `DataRoot` assets it distributes holds transitions or destinations.
Each map bundle gives every interactive its `m_interactionId`, `cellId` and `gfxId` — who it is,
where it stands and what it looks like — but never where it leads.

So a teleport destination is server data. That is why a Dofus 2 dump is the only practical source,
and why chasing a deobfuscated build would not help: the names are not the obstacle, the absence of
the data is.
