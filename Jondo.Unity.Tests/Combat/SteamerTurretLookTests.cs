using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Steamer's turrets change their look at Evolución II and III (13851, 13852, effect 149) to
    /// appearances the client's data does not carry: the server has to send the look, and it had
    /// none for them -- «Appearance 2546 has no compatible bones; the look is not changed».
    /// </summary>
    public class SteamerTurretLookTests
    {
        private const int EvolutionII = 13851, EvolutionIII = 13852, ChangeAppearance = 149;

        [Theory]
        [InlineData(EvolutionII)]
        [InlineData(EvolutionIII)]
        public void Every_turret_look_an_evolution_puts_on_is_known(int evolution)
        {
            var rows = SpellEffects.De(evolution, 1).Where(e => e.EffectId == ChangeAppearance).ToList();
            if (rows.Count == 0) return;   // no spell data

            Assert.Equal(6, rows.Count);
            Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(Cosmetics.AppearanceLook(row.Value)),
                                                 $"appearance {row.Value} ({row.TargetMask}) has no look"));
        }

        [Fact]
        public void The_arponera_takes_the_next_two_bones()
        {
            Assert.Equal("{7755}", Cosmetics.AppearanceLook(2545));
            Assert.Equal("{7756}", Cosmetics.AppearanceLook(2546));
        }
    }
}
