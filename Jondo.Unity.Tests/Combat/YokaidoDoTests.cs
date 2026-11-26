using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// «Yokaido Do» (16932), granted for the library fight of «En la encrucijada de los mundos»
    /// (2150): each cast takes one MP from the yokai it puts to sleep, for the rest of the fight
    /// (169, duration -1), and the losses add up with no cap, so a long fight leaves the yokais
    /// without the legs to keep away.
    /// </summary>
    [Collection("MapManager")]
    public class YokaidoDoTests
    {
        private const int YokaidoDo = 16932;
        private const int Somnolent = 1923;

        [Fact]
        public void Each_cast_takes_one_more_MP_for_the_whole_fight()
        {
            if (SpellEffects.De(YokaidoDo, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 262, MaxHP = 5000, CurrentHP = 5000, Level = 150,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var yokai = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 290, MaxHP = 3600, CurrentHP = 3600, Level = 100,
                IsMonster = true, MonsterId = 6237, MaxAP = 8, CurrentAP = 8, MaxMP = 10, CurrentMP = 10,
            };
            fight.AddPlayer(me); fight.AddMonster(yokai);
            fight.StartFight();

            for (int cast = 1; cast <= 3; cast++)
            {
                // Only an awake yokai takes it (mask e1923): wait out the last sleep.
                for (int guard = 0; guard < 40 && (fight.CurrentFighter != me || yokai.Buffs.Estados.Contains(Somnolent)); guard++)
                {
                    fight.NextTurn();
                    yokai.Buffs.Barrer(fight.RoundNumber);   // the server sweeps at each turn
                }
                Assert.DoesNotContain(Somnolent, yokai.Buffs.Estados);
                int round = fight.RoundNumber;
                EffectEngine.Resolver(fight, me, YokaidoDo, 1, yokai, EffectEngine.AlLanzar, round,
                                      celdaApuntada: yokai.CellId);
                Assert.Contains(Somnolent, yokai.Buffs.Estados);
                var losses = yokai.Buffs.Puestos.Where(b => b.EffectId == 169 && b.HechizoOrigen == YokaidoDo).ToList();
                Assert.Equal(cast, losses.Count);
                Assert.All(losses, b => Assert.True(b.CaducaEnRonda < 0));
                fight.NextTurn();
            }
            while (fight.CurrentFighter != yokai) fight.NextTurn();
            Assert.Equal(7, yokai.CurrentMP);
        }
    }
}
