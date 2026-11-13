using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The tactirelle's and the chalutier's spells are cast on the turret itself (range 0) and do
    /// their work on whoever stands round it: a state, a push or a pull. The tactics weighed them
    /// on the turret only, as worth something on an enemy, so they were worth nothing and the two
    /// turrets passed every turn. In the game they cast them every turn.
    /// </summary>
    [Collection("MapManager")]
    public class SteamerSelfCastTurretTests
    {
        private const int Centre = 300;

        private static (MonsterTactics.Board Board, Fighter Turret, List<MonsterTactics.Spell> Spells)
            Turret(int template, int spell, int enemyDistance)
        {
            var fight = new FightInstance(9_998_000 + template, 1, 1);
            var turret = new Fighter
            {
                Id = -1, TeamId = 0, CellId = Centre, MaxHP = 600, CurrentHP = 600, IsMonster = true, MonsterId = template,
                MaxAP = 6, CurrentAP = 6, MaxMP = 0, CurrentMP = 0, Level = 200, Invocador = 10,
                HechizosDeInvocado = new[] { (spell, 1) },
            };
            var enemy = new Fighter
            {
                Id = 8_990_000_101, CellId = Enumerable.Range(0, MapGeometry.MaxCells)
                    .First(c => MapGeometry.Distance(Centre, c) == enemyDistance),
                MaxHP = 4500, CurrentHP = 4500, Level = 200, CurrentAP = 12, CurrentMP = 6, MaxAP = 12, MaxMP = 6,
            };
            fight.AddOpponent(enemy);
            var board = new MonsterTactics.Board
            {
                Fighters = new[] { turret, enemy },
                Reach = (row, caster, from, aim) => EffectEngine.ReachOf(fight, caster, row, from, aim),
                CanSummon = (c, t, g) => FightHandler.FitsTheSummonLimit(fight, c, t, g),
            };
            return (board, turret, FightHandler.TacticsOf(turret));
        }

        [Theory]
        [InlineData(5837, 13844)]   // the tactirelle's
        [InlineData(5832, 13883)]   // the chalutier's
        public void A_turret_casts_its_own_spell_with_an_enemy_round_it(int template, int spell)
        {
            var (board, turret, spells) = Turret(template, spell, 1);
            if (spells.All(s => s.Id != spell)) return;   // no spell data

            var options = MonsterTactics.Options(board, turret, spells);

            var weighed = spells.Single(s => s.Id == spell);
            Assert.True(options.Any(o => o.Spell.Id == spell && o.Score > 0),
                $"cost {weighed.Cost} range {weighed.MinRange}-{weighed.MaxRange} utility {weighed.Utility} " +
                $"onEnemies {weighed.UtilityOnEnemies} offensive {weighed.Offensive}; " +
                $"options: {string.Join(", ", options.Select(o => $"{o.Spell.Id}@{o.TargetCell}={o.Score}"))}");
        }

        /// <summary>In the game they cast it every turn, whoever stands round them.</summary>
        [Fact]
        public void Even_with_nobody_round_it()
        {
            var (board, turret, spells) = Turret(5837, 13844, 20);
            if (spells.All(s => s.Id != 13844)) return;   // no spell data

            var options = MonsterTactics.Options(board, turret, spells);

            Assert.Contains(options, o => o.Spell.Id == 13844 && o.Score > 0);
        }
    }
}
