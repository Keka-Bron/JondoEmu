# Forgelance combat visuals and the Immortal Lance

Updated 22 October 2026. Evidence: the 45 files under
`SpellBySpellWrireshark-3-6-10-10/Forgelance/NonVariants` and `Variants`, supplied outside
the repository. TCP streams were reassembled without missing segments or undecoded bytes.
The low-level comparison covers 22 official casts of Trident de la mer, Effondrement,
Estoc brulant and Volee d'airain. The captures use rank 3; a level-4 character uses rank 1
of the same base spells, not their variants.

## Scope

`ForgelanceCombat`, `ForgelanceProtocol` and `FightForgelance` contain the class-specific rules.
The shared handler and resolver opt into them only for breed 20 or its own summoned lance
(monster template 7139). Other classes retain their existing damage, appearance, sequence
and script-marker paths. No spell, variant, characteristic or account database is rewritten.

## Spell animations

An official manual cast starts an action sequence of type 0. Characteristic updates use
nested sequences of type 3. Jondo previously used 3 for both. Only the Forgelance's action
sequences now use 0; the shared sequence constants are unchanged.

The following immediate markers are visual commands, not buffs:

| Base spell | Spell ID | `3792` marker values |
|---|---:|---|
| Trident de la mer | 23393 | 21070, 21071 |
| Estoc brulant | 23754 | 21226, 21227 |
| Volee d'airain | 23741 | 21072, 21073 |

The command is `jwe { f3: caster, f14: 3792, f25 { f1: critical if true,
f2: rank, f3: aimed cell, f4: spell ID, f5: marker value } }`.
In TridentDeLaMer packet 5, EstocBrulant packet 5 and VoleeDairain packet 8, the two
commands follow AP spending and precede damage. Rank is 1 at low level, never a
`SpellLevels.Id`. A Forgelance resolves these effects and its damage in one ordered path;
the old damage pass does not also run. The existing damage calculator still applies the hit.

Child spells get their own cast announcement before their effects. They carry no manual
`f8` flag and retain their aimed cell, including an empty one. Effondrement packet 64
announces both 23736 and its child 30711 on cell 453 without a target ID. Previously Jondo
announced the child per damaged victim, moved its visual origin to that victim's cell,
added `f8=1`, and announced nothing when the aimed area hit nobody. Its empty-cell fallback
is restricted to Forgelance 2960 sub-casts with the `a,A` mask and caster conditions;
it does not bypass a required summon/template target.

## Armed, disarmed and guard appearance

| Situation | Root bones | Skin 3650 |
|---|---:|---|
| Armed, appearance 2061 | 1 | Present once |
| Disarmed, state 3361 | 1 | Absent |
| Armed guard, appearance 2062 | 7156 | Present once |
| Returning from guard | 1 | Present once |

LancePierre packets 20/55 show the same colours, equipment, scale and follower, with only
skin 3650 removed/restored. Effondrement packets 22/49 show guard bones 7156 followed by
bones 1 before the next turn. Appearance 2061 cannot be treated as an unresolved cosmetic
bones lookup: abandoning that update left the client stuck on guard bones. The new
Forgelance builder preserves all the other look fields and broadcasts the result to the fight.
The on-foot character look is used so the lance skeleton is not applied to a mount.

## Lethal hits and remaining target effects

The ordered Forgelance pipeline prepares its outcomes before applying damage. A target
that was alive during preparation can die before its remaining state or displacement
outcomes are announced. Recheck the target's life for every outcome: discard later
target effects, remove their prepared buff rows, and omit deferred life/stat/range
refreshes for dead targets. Living targets and death hooks affecting the living owner
continue normally, including retrieving the Immortal Lance. A child cast whose parent
killed its target still announces its aimed cell, but no longer names the dead actor.

The 22 October Kardorim-room report exposed this sequence in the preceding fight:
at 14:31:46.409 monster -3 died to Estoc brulant, then at 14:31:46.410 Jondo still sent
its displacement from 369 to 396. The client subsequently failed repeatedly in
`EntityPhysics` / `EntityDisplay` while accessing an invalid `EntitySprite` transform.
That packet order is a confirmed server defect; the log does not identify the invalid
client entity, so the direct link to the rendering failure remains unconfirmed.

## Lance indicators and lifecycle

The server sends state buffs; the client supplies the lance icon artwork. The disarmed
indicator uses state 3361, while the nearby-lance state is 3589. Both travel as visible
`jxm` state effects (`950`, family 2), not hidden machinery. LancePierre packet 20 carries
3361 from spell 23286; packet 24 carries 3589 from the lance's spell 24391, rank 2.
These are the states used for the blue/disarmed and green/nearby indicators.

Only the owner's living lance grants proximity. The eight surrounding cells count:
one cell in a line or a diagonal. Walking and displacement refresh proximity; leaving
removes the corresponding state buff and fires its EOFF hook. Summoning also refreshes it.
The existing lance aura's temporary effects are removed when a fighter leaves it, using
the same cleanup as monster auras but restricted to the Forgelance lance's spell 24391.

The spell data still defines the mechanics: passive 24387, armed spell 23385, disarmed
spell 23286, guard 24390, lance aura 24391 and rearming power 30736. The lance's attitude
retains its actual starting-spell rank so its death/retrieval hooks run at the correct rank.
The integration test exercises throwing a lance, becoming disarmed, proximity, retrieval,
returning to armed and the +30 power buff. Recovery uses the spell hooks, not a duplicate
hand-written damage or power formula.

## Verification

`ForgelanceTests` checks captured script bytes (normal and critical), rank-1 messages,
appearance round trips, broadcasts to both fighters, the three low-level marker pairs
before a single damage line, Effondrement at an empty cell, diagonal proximity and icon
removal, another player's lance, and the throw/retrieval lifecycle. Existing combat and
protocol tests cover the unchanged paths of the other classes.

Lethal and nonlethal Trident de la mer / Estoc brulant casts check that survivors still
move, while a killed target receives no subsequent displacement or state buff. Estoc's
prepared post-damage state is also removed from the dead fighter's server-side buffs.
Mixed-area and child-cast tests also cover surviving targets and an aimed cell left empty
by a lethal parent hit.

Packet and state tests verify what is transmitted. The final artwork and animation playback
are rendered by the Dofus client and should be checked in game after restarting the server.
