using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Trabucador's Aislamiento (3733, Ring of Captain Eskarlata): at the end of each of its
    /// turns (4730) it lays a trap in a ring round itself (4729), whose push (4728) throws an enemy
    /// two cells. Standing still, it piled one trap on another every turn.
    /// </summary>
    public class IsolationTrapTests
    {
        private const int Isolation = 4729;

        [Fact]
        public void The_same_trap_is_laid_once_on_the_same_spot()
        {
            var fight = new FightInstance(2, 1);
            var player = new Fighter { Id = 10, TeamId = 0, CellId = 400, MaxHP = 1000, CurrentHP = 1000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, Level = 150 };
            var trabucador = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 257, MaxHP = 2000, CurrentHP = 2000, MaxAP = 10, CurrentAP = 10,
                MaxMP = 5, CurrentMP = 5, Level = 130, IsMonster = true, MonsterId = 3733,
            };
            fight.AddPlayer(player);
            fight.AddMonster(trabucador);

            void Lay() => EffectEngine.Resolver(fight, trabucador, Isolation, 1, trabucador, EffectEngine.AlLanzar,
                                                fight.RoundNumber, celdaApuntada: trabucador.CellId);
            int Traps() => fight.Glifos.Count(g => g.Tipo == EffectEngine.ColocaUnaTrampa && !g.Gastado);

            if (SpellEffects.De(Isolation, 1).Count == 0) return;   // no spell data to read
            Lay();
            Assert.Equal(1, Traps());

            // The next turn's end, where it stood: no second trap.
            Lay();
            Assert.Equal(1, Traps());

            // Having moved, it lays one round its new cell.
            trabucador.CellId = 300;
            Lay();
            Assert.Equal(2, Traps());
        }
    }
}
