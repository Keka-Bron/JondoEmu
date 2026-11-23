using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Count Razof (4803)'s Trofeo de Caza (8538) boosts only him ("C": +1 MP, +200 power) and is
    /// aimed at an enemy summon, which it kills. His AI cast it on his own companions for the
    /// boost alone, every turn.
    /// </summary>
    [Collection("MapManager")]
    public class RazofTrophyTests
    {
        [Fact]
        public void Razof_does_not_aim_his_trophy_at_a_companion()
        {
            if (!SpellEffects.De(8538, 1).Any(row => row.EffectId == 141)) return;   // no spell data
            MobSpawnManager.EnsureMonsterData();
            var boss = new Fighter
            {
                Id = -1, Name = "B", IsMonster = true, MonsterId = 4803, Level = 190, MaxHP = 9000, CurrentHP = 9000,
                TeamId = 1, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            foreach (int spell in new[] { 8513, 8514, 8515, 8538 }) { boss.SpellIds.Add(spell); boss.SpellGrades[spell] = 1; }
            var companion = new Fighter
            {
                Id = -4, Name = "A", IsMonster = true, MonsterId = 4801, Level = 190, MaxHP = 9000, CurrentHP = 9000,
                TeamId = 1, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            var player = new Fighter
            {
                Id = 9_977_001, Name = "P", Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var fight = new FightInstance(4803, 990_004_803);
            fight.AddPlayer(player); fight.AddMonster(boss); fight.AddMonster(companion);
            player.CellId = 100; boss.CellId = 285; companion.CellId = 270;
            fight.StartFight();

            var spells = FightHandler.TacticsOf(boss);
            var trophy = spells.Single(s => s.Id == 8538);
            Assert.False(trophy.Supports(boss, companion));
            var action = MonsterTactics.Next(FightHandler.BoardOf(fight), boss, spells, new List<MonsterTactics.Spell>());
            Assert.False(action?.Spell.Id == 8538 && action.Target == companion);
        }
    }
}
