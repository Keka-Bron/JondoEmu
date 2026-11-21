using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Protozorreur (3828) pulls himself into contact with whoever hits him in line: his
    /// passive's "1019 under D" has the attacker cast Atracción Celular (4967) at himself, whose
    /// "1022 A,F3828" on a straight cross of 63 drags the boss to him. The cross is the straight
    /// one ('Q'), so a hit from a diagonal does not pull him.
    /// </summary>
    public class ProtozorreurPullTests
    {
        private const int Protozorreur = 3828, AtraccionCelular = 4967;
        private const int BossCell = 343;

        private static Fighter Attacker(int cell) => new()
        {
            Id = 10, TeamId = 0, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Boss() => new()
        {
            Id = -1, TeamId = 1, CellId = BossCell, MaxHP = 20000, CurrentHP = 20000, Level = 220,
            IsMonster = true, MonsterId = Protozorreur, MaxAP = 12, CurrentAP = 12, MaxMP = 5, CurrentMP = 5,
        };

        private static Fighter Pull(int attackerCell)
        {
            var fight = new FightInstance(1, 1);
            var boss = Boss();
            var me = Attacker(attackerCell);
            fight.AddMonster(boss); fight.AddPlayer(me);
            fight.TriggeringAttacker = me;
            EffectEngine.Resolver(fight, me, AtraccionCelular, 1, me, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: me.CellId);
            fight.TriggeringAttacker = null;
            return boss;
        }

        [Fact]
        public void A_hit_in_line_pulls_him_into_contact()
        {
            if (SpellEffects.De(AtraccionCelular, 1).Count == 0) return;   // no spell data
            int inLine = Zone.Casillas('Q', 3, BossCell, BossCell, 3).First(MapGeometry.IsValid);
            var boss = Pull(inLine);
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, inLine));
        }

        [Fact]
        public void A_hit_from_a_diagonal_does_not_pull_him()
        {
            if (SpellEffects.De(AtraccionCelular, 1).Count == 0) return;
            int diagonal = BossCell + 42;   // three cells down the screen: a diagonal of the grid
            Assert.False(Zone.Casillas('Q', 63, diagonal, diagonal).Contains(BossCell));
            var boss = Pull(diagonal);
            Assert.Equal(BossCell, boss.CellId);
        }
    }
}
