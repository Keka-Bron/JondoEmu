using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Protozorreur (3828) lays his Infection glyph (4968, a 1165 casting 4966) under each
    /// character who takes damage. Every blow of a round laid another on the same cell, and each
    /// copy went off on whoever stood there; one is left on a spot, a new cell gets its own.
    /// </summary>
    [Collection("MapManager")]
    public class ProtozorreurGlyphTests
    {
        private const int Layer = 4968;

        [Fact]
        public void A_second_blow_on_the_same_cell_lays_no_second_glyph()
        {
            if (!SpellEffects.De(Layer, 1).Any(row => row.EffectId == 1165)) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 215, MaxHP = 20000, CurrentHP = 20000, Level = 200,
                IsMonster = true, MonsterId = 3828,
            };
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            };
            fight.AddMonster(boss); fight.AddPlayer(player);
            boss.CellId = 215; player.CellId = 300;

            for (int blow = 0; blow < 3; blow++)
                EffectEngine.Resolver(fight, boss, Layer, 1, boss, EffectEngine.AlLanzar, fight.RoundNumber,
                                      celdaApuntada: 300);
            Assert.Single(fight.Glifos, g => g.Hechizo == 4966 && g.Centro == 300);

            EffectEngine.Resolver(fight, boss, Layer, 1, boss, EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: 314);
            Assert.Equal(2, fight.Glifos.Count(g => g.Hechizo == 4966));
        }
    }
}
