# Incarnam's portal to Astrub

The exit on map **153880835 [4,-3]** is a clickable actor, not one of the map's interactive
elements. Its template **4398**, named **Portail vers Astrub**, and look **{4892}** already exist
in the 3.6.10.10/3.6.10.11 client. The emulator was missing its placement and dialogue.

## Capture evidence

Source: `SpellBySpellWrireshark-3-6-10-10/PortailIncarnam.pcapng`, supplied as an official
**3.7.12.12** capture. Packet numbers below count every captured packet, including acknowledgements.
TCP reassembly yields 137 protobuf messages without gaps in either game stream.

| Packet | Direction | 3.7 opcode | Meaning |
|---|---|---|---|
| 22, 169 | Server | `jnj` | Map 153880835: actor -20001, NPC template 4398, cell 430, facing 3, look bone 4892. Anemo is a separate actor, -20000/template 4360. |
| 106 | Client | `ilt` | Action 3 on actor -20001 on map 153880835. |
| 108 | Server | `ilf` | Question 30637, reply 36979. |
| 112 | Client | `iky` | Reply 36979. |
| 113 | Server | `ilf` | Question 30638, replies 36977 and 36978. |
| 121 | Client | `iky` | Reply 36977: decide to leave for Astrub. |
| 122 | Server | `kis` | Dialogue closes, reason 1. |
| 125 | Server | `jow` | Load map 192416776. |
| 135 | Server | `jnj` | The captured player's own actor arrives on cell 483, facing 2. |

Packets 82-102 show the same portal dialogue with reply 36978 instead: the dialogue closes and
the player stays on Incarnam. Packets 42-80 cover Anemo; none of those replies crosses the portal.
The observed crossing does not send an interactive-use request for graphic 806080 or skill 339.

The 3.7 wire layout is **not** interchangeable with 3.6: requests use root field 4, and multiple
payload fields have moved. For example `jnj` carries actors in field 3 and interactives in field 7;
3.6 `jss` carries them in fields 5 and 11. The fix uses the existing 3.6 `iov/ioc/ios/ioy/kld/jru`
builders. The NPC, look, dialogue and reply identifiers above were checked against the local 3.6
template before adding the placement.

## Configuration

* `content/npcs/spawns.json` places template 4398 at cell 430, facing 3. Actor cells need not be
  walkable: 430 is an actor/rendering position, not a cell the player must reach.
* `content/npcs/dialogues.json` reproduces the captured 30637 -> 30638 conversation. Accepting
  reply 36977 references passage 806080; cancelling with 36978 closes without moving.
* `content/interactives/teleports.json` retains the route and its arrival **192416776@483**.
  `npc: 4398` makes its `element: 806080` a server-side passage key, not a native interaction ID.
  Its decorative `gfx`, `type` and `skill` do not create a map interactive or floor trigger.

The handler verifies that the passage belongs to the current dialogue's NPC and source map,
closes the dialogue, then calls the normal teleport path with the configured arrival cell.
Anemo's behaviour and other routes are unchanged.

The startup registry guard also distinguishes these actor-owned routes: they do not count as
f11 interactives. It checks the actor's map/cell, talk action and passage reply, and rejects an
actor route registered as an interactive or floor trigger. The interactive checks still apply
to all other passages. `IncarnamPortalTests` calls this same startup guard to catch regressions
before publishing.
