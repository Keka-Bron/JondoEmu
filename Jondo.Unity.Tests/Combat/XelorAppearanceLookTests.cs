using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Xelor's looks the 3.6 client's data no longer carries, which the server has to send:
    /// Momificación's and 25ª Hora's mummy (2849) and the syncro's five growth stages (2735 to 2739,
    /// Fin de los Tiempos at grade 3). Unknown, «Appearance 2849 has no compatible bones; the look
    /// is not changed», and the Xelor never turned into a mummy.
    /// </summary>
    public class XelorAppearanceLookTests
    {
        private const int FinDeLosTiempos = 13270, ChangeAppearance = 149;

        [Fact]
        public void The_mummy_is_the_soulless_mummy_s_look()
            => Assert.Equal("{113}", Cosmetics.AppearanceLook(2849));

        /// <summary>The syncro's own bones are 8139; its five stages are the five bones after them.</summary>
        [Theory]
        [InlineData(2735, "{8140}")]
        [InlineData(2736, "{8141}")]
        [InlineData(2737, "{8142}")]
        [InlineData(2738, "{8143}")]
        [InlineData(2739, "{8144}")]
        public void Each_syncro_stage_takes_the_next_bones(int appearance, string look)
            => Assert.Equal(look, Cosmetics.AppearanceLook(appearance));

        [Fact]
        public void Every_look_the_syncro_grows_into_is_known()
        {
            var rows = SpellEffects.De(FinDeLosTiempos, 3).Where(e => e.EffectId == ChangeAppearance).ToList();
            if (rows.Count == 0) return;   // no spell data

            Assert.Equal(5, rows.Count);
            Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(Cosmetics.AppearanceLook(row.Value)),
                                                 $"appearance {row.Value} ({row.TargetMask}) has no look"));
        }
    }
}
