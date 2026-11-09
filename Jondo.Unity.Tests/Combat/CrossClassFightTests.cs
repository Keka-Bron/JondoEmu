using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Characters of every class fighting each other: three against three, each casting his own
    /// spells -- or his controllable summon's -- at whoever and wherever the spell may be cast,
    /// and after every cast the rules every fight must keep (FightInvariants). This is where one
    /// class's spell meets another's buff: a ranged invulnerability struck from afar, a vitality
    /// bonus stripped, an unmovable fighter pushed into a trap.
    /// </summary>
    /// <remarks>
    /// The seeds make each run the same. JONDO_CROSS_FIGHTS raises the number of fights for a
    /// long local run; the default keeps the battery quick.
    /// </remarks>
    public class CrossClassFightTests
    {
        private static readonly int[] Breeds = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20 };
        private readonly ITestOutputHelper _out;
        public CrossClassFightTests(ITestOutputHelper output) { _out = output; }

        private static Fighter Character(long id, int breed, int cell)
        {
            var f = new Fighter
            {
                Id = id, Name = $"Breed{breed}", Breed = breed, Level = 200, CellId = cell,
                MaxHP = 4000, CurrentHP = 4000, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
                Strength = 400, Intelligence = 400, Chance = 400, Agility = 400, Initiative = 1000 + (int)(id % 97),
            };
            var known = SpellTable.PairsOf(breed)
                .SelectMany(p => new[] { (p.Base, SpellTable.GradeFor(p.Base, 200)), (p.Variant, SpellTable.GradeFor(p.Variant, 200)) })
                .Where(s => s.Item1 > 0 && s.Item2 > 0).ToList();
            int passive = ClassPassives.PassiveOf(breed);
            foreach (var (spell, grade) in ClassPassives.ForFight(breed, known))
                if (spell != passive) f.Buffs.PonerActitud(spell, grade);
            if (passive != 0) f.Buffs.PonerActitud(passive, SpellTable.PassiveGrade(passive, 200));
            return f;
        }

        private static List<(int Spell, int Grade)> SpellsOf(Fighter f)
            => f.EsInvocado
                ? f.HechizosDeInvocado.Select(h => (h.Spell, Math.Max(1, h.Grade))).ToList()
                : SpellTable.PairsOf(f.Breed)
                    .SelectMany(p => new[] { (p.Base, SpellTable.GradeFor(p.Base, 200)), (p.Variant, SpellTable.GradeFor(p.Variant, 200)) })
                    .Where(s => s.Item1 > 0 && s.Item2 > 0).ToList();

        private static bool Occupied(FightInstance fight, int cell)
            => fight.Azul.Concat(fight.Rojo).Any(f => f != null && f.IsAlive && f.CellId == cell);

        /// <summary>Where a spell may be aimed from the caster: a fighter or a cell in its reach, free or taken as it asks.</summary>
        private static (Fighter Target, int Cell)? Aim(Random rng, FightInstance fight, Fighter caster, int spell, int grade)
        {
            var limits = FightHandler.LimitesDeGrado(spell, grade);
            int min = limits.AlcanceMinimo, max = Math.Max(limits.AlcanceMaximo, limits.AlcanceMinimo);
            var (needFree, needTaken) = SpellModifiers.Cells(caster, spell, limits.NeedFreeCell, limits.NeedTakenCell, fight.RoundNumber);
            var cells = Enumerable.Range(0, 560).Where(c => MapGeometry.IsValid(c))
                .Where(c => MapGeometry.Distance(caster.CellId, c) is var d && d >= min && d <= max).ToList();
            if (needTaken) cells = cells.Where(c => Occupied(fight, c)).ToList();
            else if (needFree) cells = cells.Where(c => !Occupied(fight, c)).ToList();
            else if (rng.Next(10) < 7 && cells.Any(c => Occupied(fight, c))) cells = cells.Where(c => Occupied(fight, c)).ToList();
            if (cells.Count == 0) return null;
            int cell = cells[rng.Next(cells.Count)];
            if (FightHandler.LaysATrap(spell, grade) && FightHandler.TrapCells(fight, null).Contains(cell)) return null;
            var target = fight.Azul.Concat(fight.Rojo).FirstOrDefault(f => f != null && f.IsAlive && f.CellId == cell);
            return (target, cell);
        }

        private static readonly bool Trace = Environment.GetEnvironmentVariable("JONDO_CROSS_TRACE") == "1";

        /// <summary>JONDO_CROSS_RULE: only that rule told, to look into one kind of break at a time.</summary>
        private static readonly string OnlyRule = Environment.GetEnvironmentVariable("JONDO_CROSS_RULE") is { Length: > 0 } r ? r : null;

        /// <summary>What the fights did, so that a quiet run is seen for what it is.</summary>
        private int _casts, _skipped, _noAim, _deaths, _summons, _lifeTaken, _turns;

        private async Task<List<string>> Play(int seed, int fights, int turnsEach)
        {
            var rng = new Random(seed);
            var broken = new List<string>();
            for (int n = 0; n < fights; n++)
            {
                var fight = new FightInstance(1000 + n, 1);
                var breeds = Breeds.OrderBy(_ => rng.Next()).Take(6).ToArray();
                var free = Enumerable.Range(0, 560).Where(c => MapGeometry.IsValid(c) && MapGeometry.Distance(300, c) <= 5)
                                     .OrderBy(_ => rng.Next()).ToList();
                for (int i = 0; i < 6; i++)
                {
                    var f = Character(1 + n * 10 + i, breeds[i], free[i]);
                    if (i < 3) fight.AddPlayer(f); else fight.AddOpponent(f);
                    f.CellId = free[i];
                }
                fight.StartFight();
                // Each broken rule once a fighter: a corpse left standing is the cast that left it.
                var told = new HashSet<(string, long)>();
                string lineUp = string.Join(" ", breeds.Take(3)) + " vs " + string.Join(" ", breeds.Skip(3));

                for (int turn = 0; turn < turnsEach && fight.Azul.Any(f => f.IsAlive && !f.EsInvocado) && fight.Rojo.Any(f => f.IsAlive && !f.EsInvocado); turn++)
                {
                    var caster = fight.CurrentFighter;
                    if (caster != null && caster.IsAlive && (!caster.EsInvocado || caster.HechizosDeInvocado.Count > 0))
                    {
                        caster.CurrentAP = Math.Max(caster.CurrentAP, caster.MaxAP);
                        var spells = SpellsOf(caster);
                        int casts = rng.Next(2, 5);
                        for (int c = 0; c < casts && spells.Count > 0 && caster.IsAlive; c++)
                        {
                            var (spell, grade) = spells[rng.Next(spells.Count)];
                            if (!SpellCriteria.Allows(caster, spell, grade)) { _skipped++; continue; }
                            if (Aim(rng, fight, caster, spell, grade) is not { } aim) { _noAim++; continue; }
                            int lifeBefore = fight.Azul.Concat(fight.Rojo).Sum(f => Math.Max(0, f.CurrentHP));
                            int aliveBefore = fight.Azul.Concat(fight.Rojo).Count(f => f.IsAlive);
                            int summonsBefore = fight.Azul.Concat(fight.Rojo).Count(f => f.EsInvocado);
                            _casts++;
                            var before = FightInvariants.Snapshot(fight, caster, spell);
                            string what = $"fight {n} ({lineUp}) round {fight.RoundNumber}: {caster.Id} (breed {caster.Breed}{(caster.EsInvocado ? $", summon {caster.MonsterId}" : "")}) " +
                                          $"casts {spell}/{grade} at {(aim.Target != null ? $"{aim.Target.Id}" : "")} cell {aim.Cell}";
                            // With JONDO_CROSS_TRACE the server's own lines of the cast go with what it broke.
                            var trace = Trace ? new System.IO.StringWriter() : null;
                            var console = Console.Out;
                            if (trace != null) Console.SetOut(trace);
                            try
                            {
                                await FightHandler.CastEffectsAsync(null, fight, caster, spell, grade, aim.Target, aim.Cell, false,
                                                                    EffectEngine.EfectosSorteados(spell, grade, false));
                            }
                            catch (Exception ex)
                            {
                                broken.Add($"exception | {spell} | {what}: {ex.GetType().Name}: {ex.Message}");
                                continue;
                            }
                            finally
                            {
                                if (trace != null) Console.SetOut(console);
                            }
                            string Lines() => trace == null ? "" : "\n        " + string.Join("\n        ",
                                trace.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Contains("[Fight]") || l.Contains("[Combat]") || l.Contains("[POINTS]"))
                                     .Select(l => l.Length > 26 ? l.Substring(26) : l).Take(30));
                            _lifeTaken += Math.Max(0, lifeBefore - fight.Azul.Concat(fight.Rojo).Sum(f => Math.Max(0, f.CurrentHP)));
                            _deaths += Math.Max(0, aliveBefore - fight.Azul.Concat(fight.Rojo).Count(f => f.IsAlive));
                            _summons += Math.Max(0, fight.Azul.Concat(fight.Rojo).Count(f => f.EsInvocado) - summonsBefore);
                            foreach (var v in FightInvariants.Check(fight).Concat(FightInvariants.AfterCast(fight, before)))
                                if ((OnlyRule == null || v.Rule == OnlyRule)
                                    && (told.Add((v.Rule, v.Fighter)) || v.Rule is "hurt-while-invulnerable" or "moved-while-unmovable" or "healed-while-incurable"))
                                    broken.Add($"{v.Rule} | {spell} | {what} -> {v} [states of {v.Fighter}: " +
                                               $"{string.Join(",", fight.Buscar(v.Fighter)?.Buffs.Estados ?? Enumerable.Empty<int>())}]{Lines()}");
                        }
                    }
                    _turns++;
                    fight.NextTurn();
                }
            }
            return broken;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public async Task Classes_fighting_each_other_keep_every_rule(int seed)
        {
            int fights = int.TryParse(Environment.GetEnvironmentVariable("JONDO_CROSS_FIGHTS"), out int many) && many > 0 ? many : 3;
            var broken = await Play(seed, fights, turnsEach: 24);
            _out.WriteLine($"{fights} fights, {_turns} turns, {_casts} casts ({_skipped} refused by their criteria, {_noAim} with nowhere to aim), " +
                           $"{_lifeTaken} life taken, {_deaths} deaths, {_summons} summons");
            foreach (var group in broken.GroupBy(b => string.Join(" | ", b.Split(" | ").Take(2))).OrderByDescending(g => g.Count()))
                foreach (var one in group.Take(3)) _out.WriteLine($"{group.Count(),4}x {group.Key}\n      e.g. {one.Split(" | ", 3)[2]}");
            Assert.Empty(broken);
        }
    }
}
