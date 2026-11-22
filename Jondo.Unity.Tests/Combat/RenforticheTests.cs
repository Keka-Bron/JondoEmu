using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Bolacayo's Renfortiche (15167) leaves an ally at 100% resistance (state 1557) and arms on
    /// Bolacayo himself a counter (15168, states 1555, 1554, 1553) waiting on enemy blows: the
    /// third blow on him takes Renfortiche off his ally. Blows on the shielded ally count for
    /// nothing.
    /// </summary>
    [Collection("MapManager")]
    public class RenforticheTests
    {
        private const int Renfortiche = 15167, Shielded = 1557;

        private static readonly SpellEffect Hundred = new()
        {
            EffectId = 97, EffectUid = 900131, DiceNum = 100, DiceSide = 100, Element = 1, TargetMask = "A",
        };

        [Fact]
        public async Task Three_enemy_blows_on_Bolacayo_take_Renfortiche_off_his_ally()
        {
            if (SpellEffects.De(Renfortiche, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var hitter = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                                       MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var caster = new Fighter { Id = -1, TeamId = 1, CellId = 200, MaxHP = 9000, CurrentHP = 9000, Level = 200,
                                       IsMonster = true, MonsterId = 5983, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            var ally = new Fighter { Id = -2, TeamId = 1, CellId = 330, MaxHP = 9000, CurrentHP = 9000, Level = 200,
                                     IsMonster = true, MonsterId = 494, MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4 };
            fight.AddPlayer(hitter); fight.AddMonster(caster); fight.AddMonster(ally);

            var outcomes = EffectEngine.Resolver(fight, caster, Renfortiche, 1, ally, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: ally.CellId);
            FightHandler.EngancharLoPendiente(outcomes, Renfortiche, 1, caster.Id, fight.RoundNumber);
            Assert.True(ally.Buffs.TieneEstado(Shielded));

            for (int blow = 1; blow <= 3; blow++)
            {
                await FightHandler.HurtAsync(null, fight, hitter, 1, 1, caster, caster.CellId, tirada: new[] { Hundred });
                Assert.Equal(blow < 3, ally.Buffs.TieneEstado(Shielded));
            }
        }
    }
}
