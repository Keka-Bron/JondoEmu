using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class CraFulminatingArrowTests
    {
        private const int FulminatingArrow = 32450;
        private const int ReboundSpell = 32452;

        [Fact]
        public void Current_catalogue_uses_nearest_target_execution_for_the_rebound()
        {
            var rebound = Assert.Single(SpellEffects.De(ReboundSpell, 1),
                effect => effect.EffectId == EffectSupport.NearestTargetExecuteSpell);

            Assert.Equal(ReboundSpell, rebound.DiceNum);
            Assert.Equal(1, rebound.DiceSide);
            Assert.Equal(2, rebound.Tamano);
            Assert.Contains("E3551", rebound.TargetMask);
            Assert.Contains("e570", rebound.TargetMask);
            Assert.Contains(EffectSupport.NearestTargetExecuteSpell,
                            EffectSupport.HandledDirectly);
        }

        [Fact]
        public void Fulminating_arrow_hits_a_nearest_chain_and_caps_its_bonus()
        {
            var fight = new FightInstance(1, 1);
            var cra = Alive(1, team: 0, cell: 300);
            var cells = ConnectedCells(origin: 100, count: 7);
            var enemies = cells.Select((cell, index) => Alive(-index - 1, 1, cell)).ToList();
            fight.AddPlayer(cra);
            foreach (var enemy in enemies) fight.AddMonster(enemy);

            var outcomes = EffectEngine.Resolver(
                fight, cra, FulminatingArrow, 1, enemies[0], EffectEngine.AlLanzar,
                fight.RoundNumber, celdaApuntada: enemies[0].CellId);
            var damage = outcomes.Where(outcome => outcome.NestedDamage).ToList();

            Assert.Equal(7, damage.Count);
            Assert.Equal(enemies[0], damage[0].Sobre);
            Assert.Equal(new[] { 0, 15, 30, 45, 60, 60, 60 },
                         damage.Select(outcome => outcome.DamageSpellBonus));
            Assert.All(damage, outcome =>
            {
                Assert.Equal(ReboundSpell, outcome.HechizoOrigen);
                Assert.Equal(2, outcome.DamageElement);
                Assert.False(outcome.CriticalDamage);
            });
            Assert.Equal(7, damage.Select(outcome => outcome.Sobre.Id).Distinct().Count());
            Assert.Equal(cra, damage[0].AnimationCaster);
            Assert.Equal(enemies.Take(6),
                         damage.Skip(1).Select(outcome => outcome.AnimationCaster));
            Assert.All(enemies, enemy =>
            {
                Assert.False(enemy.Buffs.TieneEstado(3551));
                Assert.False(enemy.Buffs.TieneEstado(570));
            });
            Assert.Equal(0, cra.Buffs.DelHechizo(
                ReboundSpell, SpellAspect.DanoBase, fight.RoundNumber));
        }

        [Fact]
        public void Fulminating_arrow_can_rebound_through_a_cra_beacon_but_not_an_ally()
        {
            var fight = new FightInstance(1, 1);
            var cra = Alive(1, team: 0, cell: 300);
            var target = Alive(-1, team: 1, cell: 100);
            int adjacent = FirstCellAtDistance(target.CellId, 1, new HashSet<int>());
            int otherAdjacent = FirstCellAtDistance(
                target.CellId, 1, new HashSet<int> { adjacent });
            var beacon = Alive(-2, team: 0, cell: adjacent);
            beacon.IsMonster = true;
            beacon.MonsterId = 8348;
            beacon.Invocador = cra.Id;
            var ally = Alive(2, team: 0, cell: otherAdjacent);
            fight.AddPlayer(cra);
            fight.AddPlayer(ally);
            fight.AddMonster(target);
            fight.AddMonster(beacon);

            var damage = EffectEngine.Resolver(
                    fight, cra, FulminatingArrow, 1, target, EffectEngine.AlLanzar,
                    fight.RoundNumber, celdaApuntada: target.CellId)
                .Where(outcome => outcome.NestedDamage)
                .Select(outcome => outcome.Sobre)
                .ToList();

            Assert.Contains(target, damage);
            Assert.Contains(beacon, damage);
            Assert.DoesNotContain(ally, damage);
        }

        [Fact]
        public void Critical_fulminating_arrow_uses_the_critical_rebound_row()
        {
            var fight = new FightInstance(1, 1);
            var cra = Alive(1, team: 0, cell: 300);
            var target = Alive(-1, team: 1, cell: 100);
            fight.AddPlayer(cra);
            fight.AddMonster(target);

            var damage = Assert.Single(EffectEngine.Resolver(
                    fight, cra, FulminatingArrow, 1, target, EffectEngine.AlLanzar,
                    fight.RoundNumber, celdaApuntada: target.CellId, critico: true),
                outcome => outcome.NestedDamage);

            Assert.True(damage.CriticalDamage);
            Assert.Equal(31, damage.Efecto.DiceNum);
            Assert.Equal(35, damage.Efecto.DiceSide);
        }

        private static List<int> ConnectedCells(int origin, int count)
        {
            var cells = new List<int> { origin };
            while (cells.Count < count)
            {
                int next = FirstCellAtDistance(cells[^1], 1, cells.ToHashSet());
                cells.Add(next);
            }
            return cells;
        }

        private static int FirstCellAtDistance(int origin, int distance, HashSet<int> excluded)
            => Enumerable.Range(0, MapGeometry.MaxCells)
                .First(cell => !excluded.Contains(cell) &&
                               MapGeometry.Distance(origin, cell) == distance);

        private static Fighter Alive(long id, int team, int cell)
            => new Fighter
            {
                Id = id,
                TeamId = team,
                CellId = cell,
                MaxHP = 1000,
                CurrentHP = 1000,
            };
    }
}
