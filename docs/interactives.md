# Interactive elements

How Jondo declares clickable map elements, resolves a client's use request and dispatches it to
the correct game behaviour without mixing maps or game sessions.

This document describes the generic interactive registry introduced in August 2026. It now
routes zaaps, zaapis, haven-bag objects, bins, persistent houses and the criterion-free portion of
the 3.6.10.10 world transition graph. The registry centralises discovery and request validation;
each feature handler still owns its state changes and protocol replies.

The architecture follows the useful separation also found in the Dofus 2.68 Giny server:
an element definition, an action attached to it, and one dispatcher for use requests. Jondo does
not copy Giny's protocol classes or database model because Jondo targets Dofus 3.6.10.10 and uses
the protobuf messages measured for that client.

---

## 1. What an interactive element is

The map data already tells the client that a graphical element exists. For each element Jondo
extracts three values into the pinned `datos/interactive_elements_3.6.10.10.json`:

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

The raw client elements and zaap lookup live in
`Jondo.Unity.Server/Managers/Interactives.cs`. The server-owned declarations are represented by
`RegisteredInteractive` and `InteractiveAction` in
`Jondo.Unity.Server/Managers/InteractiveRegistry.cs`.

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

`InteractiveActionKind` is an internal routing choice; it is not a value sent over the network.

The first action on an element keeps the historical stable instance formula:

```text
(elementId % 900000) + 10000
```

If a future element has more than one action, subsequent actions receive the next unused instance
ids. The existing three interactives therefore keep exactly the instance ids they had before the
registry.

### 2.3 Current registrations

| Action kind | Element discovery | Type | Skill | Existing handler |
|---|---|---:|---:|---|
| Zaap | Official waypoint joined to its map element, haven-bag zaap or explicit evidence-backed override | 16 | 114 | `ZaapTravelHandler` |
| Chest | Graphic 12367 on a haven-bag map | 85 | 104 | `ChestHandler` |
| Lottery | Graphic 51031 on a haven-bag map | -1 | 184 | `LotteryHandler` |
| Zaapi | Pinned city-transport catalogue | 106 | 157 | `ZaapiTravelHandler` |
| Bin | Pinned bin catalogue | 105 | 153 | `BinHandler` |
| House door | Active server house placement joined to the live map element | 300 | 84 / 97 | `HouseHandler` |
| House exit | Active interior exit joined to the live map element | 316 | 184 | `HouseHandler` |
| World transition | Safe route from the pinned client world graph | -1 unless separately proven | graph skill | `WorldInteractiveTransitionHandler` |

These values come from the existing 3.6 implementation and captures. The migration does not
reinterpret them.

### 2.4 Runtime coverage (3.6.10.10 snapshot)

Registration is not the same as full behavior. The current snapshot contains 46,309 static
interactive placements; 5,419 are registered and 40,890 remain deliberately unsupported until an
exact semantic binding and protocol flow are available.

| Coverage | Elements | Meaning |
|---|---:|---|
| End-to-end in the implemented scope | 4,350 | Ordinary/haven-bag zaaps, chests, zaapis and safe world-graph transitions parse, validate, update state and answer |
| UI only | 97 | 67 bins open empty storage and 30 Incarnam workshops open craft UI; item transfer or recipe execution is not implemented |
| End-to-end with emulator-authored rules | 972 | Anomaly rotation, lottery rewards and part of house placement/pricing/return semantics are not official-server parity |
| Unsupported/unregistered | 40,890 | Resources, markets and ambiguous/criterion/multi-target elements are logged rather than guessed |

Consequently, it is incorrect to claim that every interactive element works. The registry confirms
that a declared click routes consistently; each behavior family still needs its own evidence and
completion criteria.

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
`InteractiveRegistry.OnMap(mapId)` for the registered elements. Each one produces the same two
blocks used before the migration:

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

`f11` describes what the element is and which actions it offers. `f15` places its stated state on
the map. Both are necessary: declaring a skill without its stated element does not fully describe
the clickable object to the client. Native 3.6.10.10 serialization and the official capture show
disabled actions in `f3`, enabled actions in `f4`, the element id in `f5`, and the type in `f6`.
The older extracted schema was shifted because it mistook optional `f2`'s generated `Has` property
for a wire field. The corrected pinned schema preserves that presence field; following the old
shape and sending the action in `f3` makes the object visible but non-interactive.

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

An unresolved request produces both a console line and one deduplicated `New`
`UnhandledInteractiveUse` row in `bases/packet_telemetry.db`. Its fingerprint contains the map,
element, skill instance and additional parameter, so actions that share the `iwo` protobuf shape
do not collapse into one vague row. The telemetry hint preserves the element's client-data cell
and graphic and asks for the surrounding S2C frames—the missing proof for a door, workshop,
resource or HDV handler.

The console line contains the same identity:

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

`mechanics/incarnam/workshops.json` now supplies 30 checked
`(mapId, elementId, cell, gfx) -> (type, skill)` mappings from public Giny.NETCore world rows. The
runtime cross-checks every binding against the exact client map and profession catalogues before
registering it. One source element absent from 3.6.10.10 is recorded as rejected.

Opening is also implemented. Exact client IL2CPP tracing shows `emc::zot(kgq)` forwarding optional
field 1 as the skill id and `fsm::gak` resolving that id before constructing `CraftUi`. The handler
therefore sends `iwn` followed by `kgq { f1: skill }`. Recipe execution is not inferred from the
static catalogue: the current ingredient-change and result messages still need to be identified
before inventory mutation can be enabled. Resource nodes likewise remain unregistered.

`skills.json` does **not** supply the map-element mapping. In particular, `elementActionId` is an action
animation/category value and is not the interactive type sent in `jss`; treating it as that type
would misdeclare zaaps, chests and resources. Giny 2.68 remains useful for behaviour and database
architecture, but its packet classes and hard-coded element mappings must not be copied as 3.6
protocol truth.

## 9. World-graph doors, stairs and ladders

`tools/extraer_transiciones_mundo.py` reads two pinned 3.6.10.10 client sources together:

- every live map element (`mapId`, `m_interactionId`, cell and graphic) from the map bundles;
- `Assets/Content/World/world-graph.asset`, whose pathfinding edges name an interactive skill and
  destination map.

The deterministic output is `datos/world_interactive_transitions_3.6.10.10.json`. The server only
loads `safeRoutes`: path type 32 (`Interactive`), an empty criterion, one skill and one destination
for the exact element. It joins the row back to the live map catalogue by all four identity fields
(map, element, cell and graphic) and rejects the whole element on any conflict. Conditional and
multi-target routes remain in the evidence document but are not guessed into handlers.

For this pinned client, the extractor found 44,072 live interactive elements and 5,503 type-32
graph routes. Only 4,174 routes still join the current map bundles; 1,329 orphan routes are kept as
evidence but excluded. Applying the criterion/single-target/single-skill rule leaves 3,093 source
rows grouped into 3,091 clickable elements. An exact reciprocal edge supplies a derived arrival
for 2,895 rows; the remaining 198 deliberately use the nearest safe centre cell.

Pathfinding type 32 is **not** a `jss` interactive type. A graph route is therefore declared with
type -1 unless a separate, evidence-backed `protocolInteractiveTypeId` is present. These generic
routes register after zaaps, houses, chests, zaapis, bins and the lottery; if one of those richer
handlers already owns the same `(map, element)`, the generic route is deliberately skipped.
Conversely, the graphic-based house heuristic yields to every exact type-32 evidence key,
including criterion-gated keys which are not safe to execute automatically. That prevents an
invented house destination from hiding a known world transition.

On `iwo`, `WorldInteractiveTransitionHandler` revalidates the map, element, skill and source-cell
proximity, sends the ordinary `iwn`, persists the character's new map/cell, and runs the same
`jsd -> jru -> lqu -> hjk` sequence used by border movement. The graph stores no target cell. When
there is one unambiguous exact reciprocal edge its source cell is labelled and used as a derived
arrival candidate; otherwise the server chooses the safe walkable cell nearest the target-map
centre. No reciprocal cell is invented when the evidence is missing.

## 10. House doors and persistent ownership

House doors are specialized registrations, but only after exact world-graph keys have been removed
from the graphic-based candidate set. `casas_mundo_3.6.10.10.json` is a server-owned placement
catalogue: the client supplies house graphics and 261 static house models, but it does not contain
the official server's placement, owner or interior assignments. Those assignments must therefore
not be described as official client data.

The current snapshot begins with 1,437 configured exterior candidates. Joining them to the live
map bundles removes 156 stale elements, and exact graph evidence removes 15 misclassified generic
doors. Interior exits undergo the same checks: 36 are stale and 45 belong to the graph. Removing
houses that would have no supported way out leaves 674 active exterior doors and 106 interiors.
Every accepted exterior `(mapId, elementId)` is materialized once in SQLite `Houses`; its stable
id, owner, price, listing and access flags survive restarts. The official client model catalogue is
stored separately in `HouseTemplates`. Because the client contains no placement-to-model relation,
zero-model legacy rows are migrated to the deterministic lowest positive-price client template;
an administrator's non-zero assignment is preserved.

The map block now emits `jss.f9` as the recovered `lpx -> lpt -> lnx` structure. Door element ids
and house instances are paired, optional price presence expresses whether a house is offered, and
known owners carry their auth-database nickname and stable account tag.

The Cpp2IL-generated `lnx` model also resolves an extractor trap in the pinned `.proto`: `gcfn`
is the presence accessor for optional price f7, not a boolean f8 on the wire. The serialized tail
is f8 account tag, f9 admin lock, f10 room count, f11 skill ids and f12 instance id.

Skill 97 sends `iwn` and `khr`, then stores a one-shot pending offer in that player's
`SessionState`. The following `jal` only contains a proposed price, so the server resolves the
house from the recorded map and element—not from `iwo.f3`. It validates the full offer snapshot
again in the same SQLite transaction that deducts kamas and transfers ownership. Both the initial
click and the confirmation must be on or adjacent to the exact door cell. The supported transfer
is deliberately first-hand only: listed owned houses remain descriptive metadata until a capture
proves where the seller is credited. The only evidenced immediate update is `ivf`; no speculative
house-success opcode is emitted.

Entering skill 84 checks the persistent access policy and sends the measured `iwn -> jqw` house
sequence. The registered interior exit uses skill 184 and the measured `iwn -> jru` sequence back
to the exterior. Both sides persist the character map/cell before announcing the map change.
