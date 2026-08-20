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

| Action kind | Element discovery | Type | Skill | Existing handler |
|---|---|---:|---:|---|
| Zaap | Known zaap graphic, haven-bag zaap or explicit override | 16 | 114 | `ZaapTravelHandler` |
| Chest | Graphic 12367 on a haven-bag map | 85 | 104 | `ChestHandler` |
| Lottery | Graphic 51031 on a haven-bag map | -1 | 184 | `LotteryHandler` |

These values come from the existing 3.6 implementation and captures. The migration does not
reinterpret them.

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
the clickable object to the client.

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

These handlers are the server-authoritative resolution layer, not yet the network execution
layer. Two pieces of 3.6 evidence are still required before registering resource nodes and
workshops in `InteractiveRegistry`:

1. a checked `(mapId, elementId) -> skillId/type` mapping;
2. captures of the 3.6 messages that open a workshop and change/finish a resource state.

`skills.json` does **not** supply the first mapping. In particular, `elementActionId` is an action
animation/category value and is not the interactive type sent in `jss`; treating it as that type
would misdeclare zaaps, chests and resources. Giny 2.68 remains useful for behaviour and database
architecture, but its packet classes and hard-coded element mappings must not be copied as 3.6
protocol truth.
