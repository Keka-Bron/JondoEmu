# The Jondo Coin

A server-exclusive currency. Every monster drops it, the amount scales with the monster's level,
and NPC shops can charge in it instead of kamas.

## It is not a new item, and it cannot be

The client is Ankama's binary. It only knows how to draw and name the items that ship in its own
data — icons come from `Content/Picto/Items/item_assets_{1x,2x}.bundle`, names from
`Content/I18n/es.bin`, both indexed by ids that belong to Ankama. An invented item id has no icon,
no name and no tooltip.

This was measured, not assumed: across 38 captures and 179,425 server-to-client frames, the server
never sends an item's name or description. The only opcode carrying item text is `jtg`, which is the
Ankama store catalogue, not the inventory. Renaming is therefore a **client-side** problem, and it
lives in `JondoFix`.

So the Jondo Coin is an existing Ankama item, renamed.

## The item

| | |
|---|---|
| Template id | **20440** |
| Ankama's name | Moneda onírica minúscula (Tiny dream coin) |
| Icon id | 148013 — a turquoise coin with sparkles |
| Type | 131, the resource type monsters drop |
| **Weight** | **0** |
| Recipes using it | none |

The weight is the load-bearing property. A player can accumulate fifty thousand coins without ever
touching their pods; with almost any other resource they would have been pinned at around two
hundred. The icon matters too: turquoise reads as clearly *not kamas* at a glance, which yellow
tokens do not.

Choosing it costs the ability to ever use 20440 as the real dream coin. The Infinite Dreams content
is not implemented, so that costs nothing today.

Three siblings share the same icon and also weigh nothing — **20441**, **20442**, **20443** — if a
higher-tier coin is ever wanted. No new art needed.

## How much it drops

`Managers/JondoCoin.cs`. One coin per band of 25 monster levels:

```
level   1 -  25  ->  1 coin
level  26 -  50  ->  2 coins
level  51 -  75  ->  3 coins
...
level 201 - 225  ->  9 coins   <- last band
level 226 +      ->  9 coins   <- capped
```

The drop is **not** a roll. It is added unconditionally in `RollFightLoot`, per monster, before that
monster's own drop table is rolled.

### Why the cap at 225

Taken literally, "one more coin every 25 levels" pays 96 coins for a single level-2400 monster.
Measured over the game's 26,969 monster-and-grade combinations: median level 140, 99th percentile
220, and only 188 combinations — from 51 templates out of 5,134 — exceed 225. The ones above are
bosses or development entries (`[!] Willorque` at 2400, `[!] Mureine` at 1800).

Band 9 is therefore the last one. It honours the rule across 99 % of the game and puts a door on the
1 % that would break it. `JondoCoin.HighestBandedLevel` is the single number to change.

### What a fight actually pays

Simulated over the 38,744 monster groups the server seeds into the world:

| | coins |
|---|---|
| minimum | 1 |
| median | **16** |
| mean | 20.6 |
| 95th percentile | 56 |
| maximum | 72 (a group of eight) |

Roughly 6 fights for 100 coins, 62 for 1,000.

### Summons do not pay

A summoned creature joins its summoner's team with `IsMonster` set, so a monster that summons would
put its creature straight into the loot loop — its own drop table *and* its coins. The filter is
`!m.EsInvocado`.

The same hole existed for kamas and was fixed at the same time: `fight.Team1.Sum(m => 10 + m.Level * 5)`
summed over every fighter in the enemy team, summons included. Experience never noticed because a
summon carries no `XpReward`.

## How it gets its name

`JondoFix/Class1.cs`, table `JondoRenames.ById`. Four patches, because one is not enough:

| patch | why |
|---|---|
| `ItemData.get_name` | the inventory and the encyclopedia |
| `ItemData.get_description` | the tooltip |
| `ItemData.get_unDiacriticalName` | the market search — without it, searching "Jondo Coin" finds nothing |
| `LocalizationAccessor.TryGetLocalization` | the safety net |

Patching only the localization accessor was tried before and was not enough. The reason is now
known: `ItemData` memoises `name`, `description` and `unDiacriticalName` in its nested
`MemoizedValues` class and never asks the accessor again after the first call. The property getters
have to be patched directly.

The accessor patch stays anyway, filtered to the two text keys in the table (777279 and 777280), to
cover the paths that never touch `ItemData` — the combat log's own cache, item links in chat, and
`$item{n}` interpolation inside info messages.

`get_unDiacriticalName` is patched **manually**, from `PatchUnDiacriticalName()`, not with an
attribute: the client metadata contains both `unDiacriticalName` and `undiacriticalName` and it is
not clear which one `ItemData` carries. A `[HarmonyPatch]` on a member that does not exist takes the
whole mod down at load; the manual version tries both spellings and, failing both, logs a warning and
carries on. The only thing lost is the market search.

There is an alternative that would cover every path with no patching at all: overwrite the string
in `es.bin` directly — "Jondo Coin" is shorter than "Moneda onírica minúscula", so the offsets would
not move. It was rejected because `es.bin` is listed in `manifest.json` with its SHA1, and a launcher
repair would silently put the old name back.

## Shops that charge in coins

**The client already supports this natively.** Nothing was invented.

The shop-opening message `kbd` carries an optional field 3 holding the GID of the item that acts as
currency. Measured across the 305 captures: of 60 `kbd` messages, 58 carry only fields 1 and 2 and
charge kamas; two carry field 3 —

```
f3 = 13052   "Sebuscalón"   (the Travellers' Tower shop)
f3 = 30529   "Fidelicha"    (a Pandala shop)
```

With that field set, the client draws the token instead of the kamas symbol and confirms the
purchase in tokens.

The purchase itself differs in exactly two messages, both measured in the Travellers' Tower capture:

| kamas | tokens | |
|---|---|---|
| `lqn` 252 | `lqn` **364** | six parameters: item gid, item uid, quantity, price, token gid, token uid. Measured: `798, 1055401001, 1, 20, 13052, 0` |
| `ivf` | `ivj` | `f3 { f2: token stack uid, f3: **what is left** }` |

That `ivj`'s field 3 is the new total and not the amount spent is settled by a different capture: in
the rune market the same stack goes `107 -> 117 -> 217 -> 1217`.

### Configuring one

`datos/tiendas_en_fichas.json`, hand-written. It is deliberately **not** part of `npc_shops.json`,
which `tools/extraer_tiendas.py` regenerates from captures — anything added there is lost on the
next measurement pass.

```json
{
  "tiendas": {
    "5510": {
      "moneda": 20440,
      "precios": { "15779": 25, "8992": 40 }
    }
  }
}
```

The key is the NPC **template** id, the same one `npc_shops.json` uses. `moneda` is the currency
item. `precios` is the price in coins per item template; anything in the vendor's catalogue without
a price here costs `TokenShops.DefaultPrice` (1 coin) rather than being free.

This changes only *what* a shop charges in, never *what* it sells: the catalogue still comes from
`npc_shops.json`, and only items in it can be bought.

Ship it empty and nothing changes — every shop charges kamas exactly as before.

## What the guards pin

`RegressionGuardTests.cs`:

- **`AssertJondoCoinPaysByBand`** — the twelve band boundaries, including both edges of each band.
  A `/ 25` without the `- 1` in front moves exactly levels 25 and 26 and nothing else, which nobody
  notices until someone kills a level-25 monster and gets two coins. Also checks that a zero or
  negative level still pays at least one, and that template 20440 is still in `ItemTemplates` — the
  number is written by hand, and a regenerated `world.zip` without it would make the coin vanish in
  silence.
- **`AssertShopCurrencyIsOptional`** — a kamas shop produces byte-identical output to before the
  feature existed, and never emits field 3. If someone turns the `VarIfNotZero` into a `Var`, all 51
  normal shops would start sending `f3 = 0`; the client would read "the currency is item 0" and the
  result is not an error but a shop where nothing can be bought.
