# Administration item commands

Jondo implements the Giny-style `.item` and `.itemset` chat commands. Both commands are
administrator-only: because they are deliberately absent from the lower-role permission table,
`CommandHandler` applies its safe default and requires `Roles.Administrador` (role **5**).

The command can be written in any chat channel. Jondo consumes the message instead of publishing
it, then sends the result back as a private informational chat line in the same tab.

## `.item`

Syntax:

```text
.item <item-template-id> [quantity]
```

Examples:

```text
.item 10784
.item 10784 10
```

The first argument is the item template id (`gid`) from `ItemTemplates` in `world.db`. Quantity is
optional and defaults to `1`; it must be a positive integer.

For a valid template, the server:

1. reads the template's `possibleEffects` and resolves them through `ItemEffects`;
2. creates the item with the maximum factory value of every variable effect;
3. assigns a new unique item id (`uid`);
4. persists it in `CharacterItems`, in the character's inventory bag;
5. updates both server-side inventory representations;
6. immediately pushes the new item and refreshed pods to the client.

An existing template with no effects is valid and produces an effect-less item. An unknown or
non-positive template id is rejected with `No existe la plantilla de objeto <id>.` No object is
created when validation or database insertion fails.

The template id shown beside item names in the administrator encyclopedia can be copied directly
into this command. That display is client-side convenience only; the server still verifies the
template and role itself.

## `.itemset`

Syntax:

```text
.itemset <item-set-id>
```

Example:

```text
.itemset 1
```

The argument is a set id from `datos/item_sets.json`, not an item id. The server creates one copy
of every item template declared by that set. The catalogue includes sets that contain items but
have no bonus table, which is necessary for cosmetic and internal sets.

Each piece follows the same creation path as `.item`, including its maximum factory effects,
persistence and immediate inventory update. The final response reports how many pieces were
created. If a set refers to templates that are absent from `ItemTemplates`, the valid pieces are
still created and the missing template ids are listed in the response.

An unknown set id is rejected with `No existe la panoplia <id>.`

## Data and implementation

- Command parsing, authorization and inventory push: `Jondo.Unity.Server/Handlers/CommandHandler.cs`
- Template lookup and factory-effect creation: `Jondo.Unity.Server/DatabaseManager.cs`
- Complete set-id catalogue: `Jondo.Unity.Server/Managers/ItemSets.cs`
- Set data: `datos/item_sets.json`
- Item templates and effect rows: `bases/world.db`, tables `ItemTemplates` and `ItemEffects`

These commands create inventory objects; they do not equip them. Equipping a created item uses the
normal equipment handler, which then applies the item's own effects and any applicable set bonus.
