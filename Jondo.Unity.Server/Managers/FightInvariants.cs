using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What must always hold in a fight, whatever was cast: checked after every action of the
    /// cross-class fights -- the tests' and the live ones between test characters -- so that an
    /// interaction nobody thought of shows up as a broken rule with who, what and how much.
    /// </summary>
    /// <remarks>
    /// Two kinds. The state rules hold at any moment: the dead have died, life within its
    /// maximum and the maximum the one the fight started with less the erosion plus the vitality
    /// rows, one fighter a cell, no points below zero, no summon outliving its owner, no row
    /// run out and still on. The cast rules compare the board before and after one cast: an
    /// unmovable fighter moved, an invulnerable one hurt, an incurable one healed.
    /// </remarks>
    public static class FightInvariants
    {
        /// <summary>One broken rule: which, on whom, and what was seen.</summary>
        public sealed record Violation(string Rule, long Fighter, string Detail)
        {
            public override string ToString() => $"{Rule} on {Fighter}: {Detail}";
        }

        private sealed class Ledger
        {
            /// <summary>
            /// Each fighter's maximum life with no erosion and no vitality row: what the rest moves.
            /// By the fighter, not his id: a summon's id is free again once it has gone, and the
            /// next one to get it is another creature.
            /// </summary>
            public readonly Dictionary<Fighter, int> BaseLife = new(ReferenceEqualityComparer.Instance);

            /// <summary>The casts the effects engine choked on.</summary>
            public readonly List<Violation> Chokes = new();
        }

        private static readonly ConditionalWeakTable<FightInstance, Ledger> Ledgers = new();

        private static Ledger Of(FightInstance fight) => Ledgers.GetValue(fight, _ => new Ledger());

        /// <summary>The effects engine threw on a spell: kept for the next check.</summary>
        public static void Choked(FightInstance fight, int spell, Exception ex)
        {
            if (fight == null) return;
            lock (Of(fight)) Of(fight).Chokes.Add(new Violation("engine-choked", 0, $"spell {spell}: {ex.GetType().Name}: {ex.Message}"));
        }

        private static int VitalityOf(Fighter f) => f.Buffs.Puestos.Where(Fighter.IsVitalityRow).Sum(r => r.Cuanto);

        /// <summary>
        /// The state rules. <paramref name="withTime"/> when turns have really passed -- a live
        /// fight -- so that a row left on after its round is a broken rule; the tests' casts
        /// with no turns in between leave every row on.
        /// </summary>
        public static List<Violation> Check(FightInstance fight, bool withTime = false)
        {
            var found = new List<Violation>();
            var ledger = Of(fight);
            lock (ledger)
            {
                found.AddRange(ledger.Chokes);
                ledger.Chokes.Clear();
            }
            var everybody = fight.Azul.Concat(fight.Rojo).Where(f => f != null).ToList();
            foreach (var f in everybody)
            {
                if (!f.IsAlive)
                {
                    if (!fight.Muertos.Contains(f))
                        found.Add(new("dead-not-gone", f.Id, $"life {f.CurrentHP} but its death was never told"));
                    continue;
                }
                if (f.CurrentHP > f.MaxHP)
                    found.Add(new("life-over-max", f.Id, $"{f.CurrentHP}/{f.MaxHP}"));
                if (f.MaxHP < 1)
                    found.Add(new("max-life", f.Id, $"maximum {f.MaxHP}"));

                int vitality = VitalityOf(f);
                if (!ledger.BaseLife.TryGetValue(f, out int baseLife))
                    ledger.BaseLife[f] = f.MaxHP + f.VidaErosionada - vitality;
                else if (f.MaxHP != Math.Max(1, baseLife - f.VidaErosionada + vitality))
                    found.Add(new("max-life", f.Id, $"maximum {f.MaxHP}, expected {baseLife} - {f.VidaErosionada} eroded + {vitality} of vitality"));

                if (f.CurrentAP < 0 || f.CurrentMP < 0)
                    found.Add(new("negative-points", f.Id, $"{f.CurrentAP} AP, {f.CurrentMP} MP"));
                if (f.PuntosDeEscudo < 0)
                    found.Add(new("negative-shield", f.Id, $"{f.PuntosDeEscudo}"));

                if (f.EsInvocado && fight.Buscar(f.Invocador) is { } owner && !owner.IsAlive)
                    found.Add(new("orphan-summon", f.Id, $"template {f.MonsterId}: its owner {owner.Id} (template {owner.MonsterId}, summoned by {owner.Invocador}) is dead"));

                if (withTime)
                    foreach (var row in f.Buffs.Puestos.Where(r => !r.Pendiente && r.CaducaEnRonda >= 0 && r.CaducaEnRonda < fight.RoundNumber - 1))
                    {
                        var by = fight.Buscar(row.Quien);
                        string who = by == null ? $"{row.Quien}, not in the fight" : $"{by.Id}, {(by.IsAlive ? "alive" : "dead")}{(by.EsInvocado ? $", summon of {by.Invocador}" : "")}{(by.JuegaTurno ? "" : ", takes no turn")}";
                        found.Add(new("row-outlived", f.Id, $"row {row.Numero} (effect {row.EffectId} of spell {row.HechizoOrigen}, put by {who}, trigger {row.Disparador}) ran out in round {row.CaducaEnRonda}, now {fight.RoundNumber}"));
                    }
            }

            // One fighter a cell: a carried one stands on his carrier's.
            foreach (var group in everybody.Where(f => f.IsAlive && f.CarriedBy == 0 && !f.EstaCargado).GroupBy(f => f.CellId).Where(g => g.Count() > 1))
                found.Add(new("shared-cell", group.First().Id, $"cell {group.Key}: {string.Join(", ", group.Select(f => f.Id))}"));

            return found;
        }

        /// <summary>The board one cast is about to change.</summary>
        public sealed class Before
        {
            internal readonly Dictionary<long, (int Cell, int Life, int Vitality, string Pinned, bool ShieldedFromCaster, bool Incurable)> Fighters = new();
            internal long Caster;
            internal int Spell;
        }

        /// <summary>«Indesplazable», the state no displacement moves.</summary>
        private const int Indesplazable = 97;

        /// <summary>
        /// The states that keep him from being moved by anybody else, named: 97 and the client's
        /// cantBeMoved. The ones that only keep pushes and pulls off (cantBePushed) or swaps
        /// (cantSwitchPosition) are not here: a teleport still moves those.
        /// </summary>
        private static string Unmovable(Fighter f)
        {
            var named = f.Buffs.Estados.Where(s => s == Indesplazable || SpellStates.Of(s)?.CantBeMoved == true).ToList();
            return named.Count == 0 ? null : string.Join("+", named);
        }

        /// <summary>What the cast rules need, taken right before a cast.</summary>
        public static Before Snapshot(FightInstance fight, Fighter caster, int spell)
        {
            var before = new Before { Caster = caster?.Id ?? 0, Spell = spell };
            foreach (var f in fight.Azul.Concat(fight.Rojo).Where(f => f != null && f.IsAlive))
            {
                int distance = caster == null ? 0 : Jondo.Unity.World.Maps.MapGeometry.Distance(caster.CellId, f.CellId);
                // Invulnerable from every distance: the melee and ranged ones are judged by where the
                // blow comes from -- a push's crash counts as melee --, which the board does not say.
                before.Fighters[f.Id] = (f.CellId, f.CurrentHP, VitalityOf(f), Unmovable(f),
                                         caster != null && f != caster && SpellStates.ShieldsFromBlow(f, 1) && SpellStates.ShieldsFromBlow(f, 2),
                                         SpellStates.IsIncurable(f));
            }
            return before;
        }

        /// <summary>
        /// The cast rules, against the board <paramref name="before"/>: one that could not be
        /// moved and moved, one the caster could not hurt from where he stood and was hurt, one
        /// that could not be healed and was. Only on the fighters that kept the state all along.
        /// </summary>
        public static List<Violation> AfterCast(FightInstance fight, Before before)
        {
            var found = new List<Violation>();
            foreach (var (id, was) in before.Fighters)
            {
                var f = fight.Buscar(id);
                if (f == null) continue;
                if (was.Pinned != null && id != before.Caster && f.IsAlive && f.CellId != was.Cell && f.CarriedBy == 0 && Unmovable(f) != null)
                    found.Add(new("moved-while-unmovable", id, $"spell {before.Spell} of {before.Caster} moved it {was.Cell} -> {f.CellId} (states {was.Pinned})"));

                int lifeMoved = f.CurrentHP - was.Life - (VitalityOf(f) - was.Vitality);
                // Not a death: a summon struck down (141) is killed, not hurt.
                if (was.ShieldedFromCaster && lifeMoved < 0 && f.IsAlive)
                    found.Add(new("hurt-while-invulnerable", id, $"spell {before.Spell} of {before.Caster} took {-lifeMoved} (life {was.Life} -> {f.CurrentHP})"));
                if (was.Incurable && lifeMoved > 0 && SpellStates.IsIncurable(f))
                    found.Add(new("healed-while-incurable", id, $"spell {before.Spell} of {before.Caster} gave {lifeMoved} (life {was.Life} -> {f.CurrentHP})"));
            }
            return found;
        }
    }
}
