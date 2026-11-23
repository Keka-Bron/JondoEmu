using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Chienchien (4809)'s Escaramuza (8521) takes 100 MP from "H,M,D,C" -- the other side by
    /// kind, with no "A". Its AI read enemies from "A" alone, weighed the spell as nothing and only
    /// ever cast Podenco.
    /// </summary>
    [Collection("MapManager")]
    public class ChienchienSkirmishTests
    {
        [Fact]
        public void Next_to_an_enemy_the_chienchien_casts_escaramuza()
        {
            if (!SpellEffects.De(8521, 1).Any(row => row.EffectId == 169)) return;   // no spell data
            MobSpawnManager.EnsureMonsterData();
            var dog = new Fighter
            {
                Id = -1, Name = "B", IsMonster = true, MonsterId = 4809, Level = 190, MaxHP = 3000, CurrentHP = 3000,
                TeamId = 1, MaxAP = 11, CurrentAP = 11, MaxMP = 5, CurrentMP = 5,
            };
            foreach (int spell in new[] { 8520, 8521 }) { dog.SpellIds.Add(spell); dog.SpellGrades[spell] = 1; }
            var player = new Fighter
            {
                Id = 9_977_001, Name = "P", Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var fight = new FightInstance(4809, 990_004_809);
            fight.AddPlayer(player); fight.AddMonster(dog);
            player.CellId = 285; dog.CellId = 299;
            fight.StartFight();

            var spells = FightHandler.TacticsOf(dog);
            Assert.True(spells.Single(s => s.Id == 8521).Removal > 0);
            var action = MonsterTactics.Next(FightHandler.BoardOf(fight), dog, spells, new List<MonsterTactics.Spell>());
            Assert.Equal(8521, action?.Spell.Id);
            Assert.Same(player, action!.Target);
        }
    }
}
