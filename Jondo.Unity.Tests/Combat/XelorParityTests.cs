using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The bots' plans play the Xelor's teleports on a copy of the fight (MonsterTactics.Simulation),
    /// and that copy must come out where the engine does: every spell of the class, the enemy one to
    /// three cells away, alone and with a third fighter on the cell the engine sent someone to -- the
    /// telefrag. Who stands where after it, and the Telefrag states (251 an enemy's, 244 an ally's,
    /// 3622 the mark), and the AP the telefrag gives -- once per spell and turn, by a state of the
    /// Xelor's that the sub-spell giving them checks («*e») as the cast began.
    /// </summary>
    /// <remarks>
    /// One known gap, on purpose: Arenas del Tiempo's bounce «desde la posición inicial de cada
    /// objetivo» to the nearest entity is not played on the copy. El Tiempo Vuela's teleport, «en el
    /// siguiente turno», waits on both now.
    /// </remarks>
    [Collection("MapManager")]
    public class XelorParityTests
    {
        private readonly ITestOutputHelper _out;
        public XelorParityTests(ITestOutputHelper output) => _out = output;

        private const int Centre = 300;

        private static int InLineAt(int from, int distance)
            => Enumerable.Range(0, MapGeometry.MaxCells)
                         .First(c => MapGeometry.Distance(from, c) == distance
                                     && MapGeometry.CellToPoint(c).Item2 == MapGeometry.CellToPoint(from).Item2);

        private sealed record World(FightInstance Fight, Fighter Xelor, Fighter Enemy, Fighter? Third);

        private static World Make(int distance, int thirdCell, long id)
        {
            var fight = new FightInstance(id, 1, 1);
            var xelor = new Fighter { Id = 1, TeamId = 1, CellId = Centre, MaxHP = 6000, CurrentHP = 6000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 5, IsBot = true, Strength = 1500, Intelligence = 1500, Chance = 1500, Agility = 1500 };
            var enemy = new Fighter { Id = 2, TeamId = 0, CellId = InLineAt(Centre, distance), MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(enemy);
            fight.AddOpponent(xelor);
            Fighter? third = null;
            if (thirdCell >= 0)
            {
                third = new Fighter { Id = 3, TeamId = 0, CellId = thirdCell, MaxHP = 3000, CurrentHP = 3000, Level = 200, MaxAP = 6, MaxMP = 3, CurrentAP = 6, CurrentMP = 3 };
                fight.AddPlayer(third);
            }
            xelor.CasillaAlEmpezarTurno = InLineAt(Centre, 4);
            enemy.CasillaAlEmpezarTurno = InLineAt(Centre, 5);
            xelor.CasillaAnterior = InLineAt(Centre, 6);
            enemy.CasillaAnterior = InLineAt(Centre, 7);
            return new World(fight, xelor, enemy, third);
        }

        /// <param name="given">
        /// The AP the cast gave the Xelor that the fight, not the engine, puts in his hand: the
        /// engine's Resolver hands them back as outcomes, and FightHandler applies them.
        /// </param>
        private static string Picture(World w, int given = 0)
            => string.Join(" ", new[] { w.Xelor, w.Enemy, w.Third }.Where(f => f != null)
                .Select(f => $"{f!.Id}@{f.CellId}[{string.Join(",", f.Buffs.Estados.Where(s => s is 251 or 244 or 2328 or 3622).OrderBy(s => s))}]"))
               + $" AP+{w.Xelor.CurrentAP - 12 + given}";

        /// <summary>Arenas del Tiempo (bounce): see the remarks.</summary>
        private static readonly HashSet<int> KnownGaps = new() { 13292 };

        [Fact]
        public void The_copy_of_the_fight_teleports_as_the_engine_does()
        {
            if (!SpellTable.IsLoaded) return;
            int same = 0, differ = 0;
            var wrong = new List<string>();
            var pairs = SpellTable.PairsOf(5);
            foreach (var pair in pairs)
            foreach (int spell in new[] { pair.Base, pair.Variant })
            {
                int grade = SpellTable.GradeFor(spell, 200);
                if (grade <= 0) continue;
                var rows = SpellEffects.De(spell, grade);
                foreach (int distance in new[] { 1, 2, 3 })
                {
                    // First without anybody else, then with a third on where the engine sent someone.
                    var plain = Make(distance, -1, 9_980_000 + spell % 10000 * 10 + distance);
                    var before = new[] { plain.Xelor.CellId, plain.Enemy.CellId };
                    EffectEngine.Resolver(plain.Fight, plain.Xelor, spell, grade, plain.Enemy, EffectEngine.AlLanzar, 1, celdaApuntada: plain.Enemy.CellId);
                    var landings = new List<int>();
                    if (plain.Xelor.CellId != before[0]) landings.Add(plain.Xelor.CellId);
                    if (plain.Enemy.CellId != before[1]) landings.Add(plain.Enemy.CellId);

                    foreach (int third in new[] { -1 }.Concat(landings))
                    {
                        long id = 9_985_000 + spell % 10000 * 100 + distance * 10 + (third < 0 ? 0 : 1);
                        var engine = Make(distance, third, id);
                        var outcomes = EffectEngine.Resolver(engine.Fight, engine.Xelor, spell, grade, engine.Enemy, EffectEngine.AlLanzar, 1, celdaApuntada: engine.Enemy.CellId);
                        int given = outcomes.Where(o => o.Sobre == engine.Xelor && o.Caracteristica == 1 && o.Cuanto > 0).Sum(o => o.Cuanto);
                        string byEngine = Picture(engine, given);

                        var copy = Make(distance, third, id + 50);
                        var board = new MonsterTactics.Board
                        {
                            Fighters = new[] { copy.Xelor, copy.Enemy, copy.Third }.Where(f => f != null).Cast<Fighter>().ToList(),
                            Reach = (row, caster, from, aim) => EffectEngine.ReachOf(copy.Fight, caster, row, from, aim),
                            Telefrags = () => copy.Fight.Telefrags,
                        };
                        var tactic = new MonsterTactics.Spell { Id = spell, Grade = grade, Cost = 0, MinRange = 0, MaxRange = 10, Rows = rows };
                        var action = new MonsterTactics.Action(new List<int> { copy.Xelor.CellId }, tactic, copy.Enemy, copy.Enemy.CellId, 0, 0, 12, 6);
                        string bySim;
                        using (var sim = MonsterTactics.Simulation.Replay(board, copy.Xelor, new[] { action }))
                            bySim = Picture(copy);

                        if (byEngine == bySim) same++;
                        else
                        {
                            differ++;
                            _out.WriteLine($"{spell} g{grade} d{distance} third {third}: engine {byEngine} | sim {bySim}");
                            if (!KnownGaps.Contains(spell)) wrong.Add($"{spell} d{distance} third {third}: engine {byEngine}, copy {bySim}");
                        }
                    }
                }
            }
            _out.WriteLine($"same {same}, differ {differ}");
            Assert.True(same > 150, $"only {same} cases compared");
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }
    }
}
