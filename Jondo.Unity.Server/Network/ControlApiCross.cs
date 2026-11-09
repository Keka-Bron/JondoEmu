using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using EffectSupport = Jondo.Unity.World.Combat.EffectSupport;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The harness's scenarios that cross a class's spells with what other classes, monsters and
    /// items put on the board: they come out of the data, so every class plays them with its own
    /// spells. The rules are the client's own: a state's cantBeMoved / cantBePushed /
    /// cantSwitchPosition flags (SpellStateData), a spell level's maxStack.
    /// </summary>
    public static partial class ControlApi
    {
        /// <summary>The scenarios every class plays, after its own.</summary>
        private static readonly string[] CrossScenarios = { "cross.unmovable", "cross.stacking" };

        /// <summary>What a spell does to where a foe stands, as its effects at the caster's grade write it.</summary>
        private enum Displacement { None, Push, Pull, Swap }

        private static Displacement DisplacementOf(Fighter caster, int spell)
        {
            int grade = FightHandler.CastingGradeOf(caster, spell);
            foreach (var e in SpellEffects.De(spell, grade))
            {
                // On foes, at once, on no condition: what the scenario can stage for sure.
                if (e.Delay != 0 || !(e.Triggers.Length == 0 || e.Triggers == "I")) continue;
                var mask = (e.TargetMask ?? "").Split(',').Select(t => t.Trim()).ToList();
                if (!mask.Contains("A") || Gated(e.TargetMask)) continue;
                if (e.EffectId is EffectSupport.Push or EffectSupport.PushWithoutDamage) return Displacement.Push;
                if (e.EffectId is EffectSupport.Pull) return Displacement.Pull;
                if (e.EffectId is EffectSupport.SwapPositions) return Displacement.Swap;
            }
            return Displacement.None;
        }

        /// <summary>
        /// A foe that may not be moved stays where he is: each of the class's spells that pushes,
        /// pulls or swaps with a foe cast at one holding, in turn, the four states that pin a
        /// fighter -- Indesplazable (97, cantBeMoved), Inquebrantable (157, cantBePushed), Pesante
        /// (7, cantSwitchPosition), Arraigado (6, cantBePushed and cantSwitchPosition) --, and at
        /// one holding none first, to know the spell moves him at all.
        /// </summary>
        private static void CrossUnmovable(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            const int Indesplazable = 97, Inquebrantable = 157, Pesante = 7, Arraigado = 6;
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderByDescending(f => f.CurrentHP).FirstOrDefault();
            if (foe == null) { sc.Check("a foe to move", false, "none"); return; }

            var spells = SpellTable.PairsOf(me.Breed).SelectMany(p => new[] { p.Base, p.Variant })
                                   .Where(s => s != 0 && SpellTable.GradeFor(s, me.Level) > 0).Distinct().ToList();
            // One spell of each kind, the first that moves an unpinned foe.
            var tried = new HashSet<Displacement>();
            foreach (int spell in spells)
            {
                var kind = DisplacementOf(me, spell);
                if (kind == Displacement.None || tried.Contains(kind)) continue;

                // Which states keep this kind of move from happening, as the client flags them.
                var pins = new List<(int State, bool Stays)>
                {
                    (Indesplazable, true),
                    (Inquebrantable, kind != Displacement.Swap),
                    (Pesante, kind == Displacement.Swap),
                    (Arraigado, true),
                };

                int? baseline = null;
                foreach (var (state, stays) in new[] { (0, false) }.Concat(pins))
                {
                    FreshTurn(session, fight, me, keep: false, withoutSummons: true);
                    if (!StageForMove(session, fight, me, foe, spell, kind)) { sc.Note($"spell {spell}: no room to stage a {kind}"); break; }
                    if (state != 0) GiveState(fight, me, foe, state, 2);
                    int was = foe.CellId;
                    var lines = CastAt(session, spell, foe.CellId);
                    bool cast = lines.Any(l => l.Contains("Casts spell"));
                    bool moved = foe.CellId != was;
                    string name = state == 0 ? "no pinning state" : $"state {state} {SpellStates.Of(state)?.Name}";
                    if (!cast) { sc.Note($"spell {spell} ({kind}) at a foe with {name}: not cast -- {string.Join(" | ", lines.Where(l => l.Contains("[Fight]")).TakeLast(2))}"); if (state == 0) break; continue; }
                    if (state == 0)
                    {
                        baseline = moved ? 1 : 0;
                        if (!moved) { sc.Note($"spell {spell} ({kind}) did not move a foe with no pinning state: {string.Join(" | ", lines.Where(l => l.Contains("[Fight]")).TakeLast(3))}"); break; }
                        tried.Add(kind);
                        sc.Check($"spell {spell} ({kind}) moves a foe with no pinning state", true, $"cell {was} -> {foe.CellId}");
                        continue;
                    }
                    sc.Check($"spell {spell} ({kind}) {(stays ? "does not move" : "still moves")} a foe with {name}", moved != stays,
                             moved ? $"moved {was} -> {foe.CellId}" : $"stayed on {was}");
                    // The state's row off again, or the next cast meets it.
                    foreach (var row in foe.Buffs.Puestos.Where(r => r.Estado == state).ToList()) foe.Buffs.QuitarFila(row);
                }
            }
            if (tried.Count == 0) sc.Note("the class has no spell that moves a foe on no condition");
        }

        /// <summary>
        /// A lasting effect cast again on the same fighter piles up only as far as its spell
        /// level's maxStack: a cap of one or none, the new row replaces the old (with its fresh
        /// duration); a cap above one, they live together up to it; no cap (-1), every cast adds.
        /// Three of the class's spells, each cast one time more than its cap, a turn apart.
        /// </summary>
        private static void CrossStacking(GameSession session, FightInstance fight, Fighter me, Scenario sc)
        {
            var foe = fight.Todos.Where(f => f.IsAlive && f.TeamId != me.TeamId && !f.EsInvocado)
                                 .OrderByDescending(f => f.CurrentHP).FirstOrDefault();
            if (foe == null) { sc.Check("a foe", false, "none"); return; }
            var catalogue = EffectCatalogue.Value;
            var spells = SpellTable.PairsOf(me.Breed).SelectMany(p => new[] { p.Base, p.Variant })
                                   .Where(s => s != 0 && SpellTable.GradeFor(s, me.Level) > 0).Distinct().ToList();
            int played = 0;
            foreach (int spell in spells)
            {
                if (played >= 3) break;
                int grade = FightHandler.CastingGradeOf(me, spell);
                // A characteristic for some turns, at once, on no condition, on a foe or on the caster.
                var lasting = SpellEffects.De(spell, grade).FirstOrDefault(e =>
                    e.Duration > 0 && e.Delay == 0 && (e.Triggers.Length == 0 || e.Triggers == "I") && !Gated(e.TargetMask) && e.EffectId is not (950 or 951 or 952)
                    // Points that can be dodged, or taken, land by the dice: not a count to hold to.
                    && !(catalogue.TryGetValue(e.EffectId, out var t) && (t.Text.Contains(" PA") || t.Text.Contains(" PM")))
                    && catalogue.TryGetValue(e.EffectId, out var c) && c.Characteristic > 0
                    && ((e.TargetMask ?? "").Split(',').Contains("A") || (e.TargetMask ?? "").Split(',').Any(t => t is "C" or "c")));
                if (lasting == null) continue;
                bool onFoe = (lasting.TargetMask ?? "").Split(',').Contains("A");
                var target = onFoe ? foe : me;
                int cap = lasting.MaxStack;
                int casts = cap > 1 ? cap + 1 : 3;
                int expected = cap > 1 ? cap : cap is 0 or 1 ? 1 : casts;
                int done = 0;
                FreshTurn(session, fight, me, keep: false, withoutSummons: true);
                for (int k = 0; k < casts; k++)
                {
                    // A turn apart: a spell cast once a turn, or once a turn on the same target.
                    if (k > 0) FreshTurn(session, fight, me, keep: true);
                    if (onFoe && !StageForMove(session, fight, me, foe, spell, Displacement.Push)) break;
                    var lines = CastAt(session, spell, onFoe ? foe.CellId : me.CellId);
                    if (lines.Any(l => l.Contains("Casts spell"))) done++;
                }
                if (done < casts) { sc.Note($"spell {spell}: cast {done} of {casts} times"); if (done < 2) continue; expected = cap > 1 ? Math.Min(cap, done) : cap is 0 or 1 ? 1 : done; }
                var keptAs = KeptAs.TryGetValue(lasting.EffectId, out var kept) ? kept : Array.Empty<int>();
                var rows = target.Buffs.Puestos.Where(r => r.HechizoOrigen == spell && (r.EffectId == lasting.EffectId || keptAs.Contains(r.EffectId))
                                                           && r.Quien == me.Id).ToList();
                sc.Check($"spell {spell}'s {lasting.EffectId} cast {done} times on the {(onFoe ? "foe" : "caster")} keeps {expected} row(s) (maxStack {cap})",
                         rows.Count == expected,
                         $"{rows.Count} row(s): {string.Join(", ", rows.Select(r => $"{r.Cuanto:+0;-0;0} until {r.CaducaEnRonda}"))}");
                played++;
            }
            if (played == 0) sc.Note("the class has no lasting characteristic on a foe or on itself on no condition");
        }

        /// <summary>
        /// The caster on open ground and the foe in a straight row from him at the spell's reach:
        /// for a push, room behind the foe; for a pull, room between them.
        /// </summary>
        private static bool StageForMove(GameSession session, FightInstance fight, Fighter me, Fighter foe, int spell, Displacement kind)
        {
            var reach = FightHandler.ReachOf(fight, me, spell);
            int at = Math.Clamp(kind == Displacement.Pull ? 4 : 2, Math.Max(1, reach.Min), Math.Max(1, reach.Max));
            if (!ToOpenGround(session, fight, me, at + 3)) return false;
            var board = FightHandler.BoardOf(fight);
            foreach (var (dx, dy) in Rows)
            {
                int cell = Along(me.CellId, dx, dy, at);
                if (cell < 0 || !(FreeCell(fight, cell) || cell == foe.CellId) || !board.Sees(me.CellId, cell)) continue;
                bool room = Enumerable.Range(1, 3).All(k => Along(cell, dx, dy, k) is int c && c >= 0 && (FreeCell(fight, c) || c == foe.CellId));
                if (!room) continue;
                return PutAll(session, fight, (foe, cell));
            }
            return false;
        }
    }
}
