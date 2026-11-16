using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A removal of points that waits is rolled against the dodge when its round comes. The
    /// Cerdo serrano's Flecha Localizadora, «-2 PM» two rounds later: rolled at the cast, a
    /// dodge left no waiting row, and one cast in three took nothing, ever.
    /// </summary>
    public class DelayedRemovalTests
    {
        private const int FlechaLocalizadora = 352;

        private static Fighter Person(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4,
        };

        [Fact]
        public void A_waiting_removal_is_rolled_when_its_round_comes()
        {
            var fight = new FightInstance(1, 1);
            var player = Person(10, 0, 229);
            var pig = Person(-3, 1, 259);
            fight.AddPlayer(player); fight.AddOpponent(pig);
            try
            {
                // Every point dodged at the cast: the row waits all the same, with both points.
                EffectEngine.DadoDeRetirada = () => 0.99;
                EffectEngine.Resolver(fight, pig, FlechaLocalizadora, 1, player, EffectEngine.AlLanzar,
                                      fight.RoundNumber, celdaApuntada: player.CellId);
                var waiting = Assert.Single(player.Buffs.Puestos, b => b.EffectId == 1080 && b.Pendiente);
                Assert.Equal(-2, waiting.Cuanto);
                Assert.Equal(fight.RoundNumber + 2, waiting.EmpiezaEnRonda);

                // Its round: both points land now, as a -2 MP row.
                EffectEngine.DadoDeRetirada = () => 0.0;
                var due = Assert.Single(EffectEngine.ActivateDuePending(fight, waiting.EmpiezaEnRonda));
                Assert.Equal(-2, due.Live.Cuanto);
                Assert.Equal(169, due.WireEffect);
                Assert.Equal(0, due.PointsDodged);
            }
            finally { EffectEngine.DadoDeRetiradaPorDefecto(); }
        }

        [Fact]
        public void A_waiting_removal_dodged_whole_when_its_round_comes_leaves_no_row()
        {
            var fight = new FightInstance(1, 1);
            var player = Person(10, 0, 229);
            var pig = Person(-3, 1, 259);
            fight.AddPlayer(player); fight.AddOpponent(pig);
            try
            {
                EffectEngine.Resolver(fight, pig, FlechaLocalizadora, 1, player, EffectEngine.AlLanzar,
                                      fight.RoundNumber, celdaApuntada: player.CellId);
                var waiting = Assert.Single(player.Buffs.Puestos, b => b.EffectId == 1080 && b.Pendiente);

                EffectEngine.DadoDeRetirada = () => 0.99;
                var due = Assert.Single(EffectEngine.ActivateDuePending(fight, waiting.EmpiezaEnRonda));
                Assert.Null(due.Live);
                Assert.Equal(2, due.PointsDodged);
                Assert.DoesNotContain(player.Buffs.Puestos, b => b.EffectId == 1080);
            }
            finally { EffectEngine.DadoDeRetiradaPorDefecto(); }
        }
    }
}
