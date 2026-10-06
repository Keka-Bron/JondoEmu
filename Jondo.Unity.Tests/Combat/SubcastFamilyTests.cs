using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The «make another spell be cast» family: nine effects, a single mechanic.
    /// </summary>
    /// <remarks>
    /// Four of the nine do not even have a description in the client's catalogue, and that is why
    /// the captures were needed. Counting all 431 —37,947 jwe frames, 21,307 casts,
    /// two independent readers with the same totals—, each cast announcement was paired
    /// with the parent that produced it:
    ///
    ///   effect   n      same caster      same cell       target==caster
    ///   792      6332   4447             3560            6269
    ///   1160     3826   3783             1772             918
    ///   2160      332    332               54              18
    ///   2792       10      0                2              10
    ///   2793      323     68               74             320
    ///   2794      235    182              228              79
    ///   2795        6      0                6               0
    ///
    /// And 1017 apart: the child's target is the parent's caster in 97 of 97, and the child's
    /// caster is NOT in 97 of 97.
    ///
    /// What this test keeps is the TABLE, because that is where the knowledge lives. Changing a
    /// row changes whom half a class hits, and it would give no error.
    /// </remarks>
    public class SubcastFamilyTests
    {
        [Fact]
        public void Los_nueve_estan_en_la_tabla_y_ninguno_se_ha_quedado_fuera()
        {
            var esperados = new[] { 792, 1160, 2160, 1017, 2792, 2793, 2794, 2795, 2960 };
            foreach (int efecto in esperados)
            {
                Assert.True(EffectEngine.EsDeLaFamiliaDeSublanzar(efecto),
                            $"el efecto {efecto} debería estar en la familia");
            }

            // And that nothing that is not one sneaks in: 141 kills, it does not chain.
            Assert.False(EffectEngine.EsDeLaFamiliaDeSublanzar(141));
            Assert.False(EffectEngine.EsDeLaFamiliaDeSublanzar(5));
        }

        [Theory]
        // 1017 is the only one that returns the spell to the parent's caster.
        [InlineData(1017, true, "AlLanzadorPadre")]
        // 792 and its cousins: the candidate casts it on himself.
        [InlineData(792, true, "AlCandidato")]
        [InlineData(2792, true, "AlCandidato")]
        [InlineData(2793, true, "AlCandidato")]
        [InlineData(2795, true, "AlCandidato")]
        // 1160 does not change caster; it aims at the candidate.
        [InlineData(1160, false, "AlCandidato")]
        // 2160 does not either, and it takes the closest one.
        [InlineData(2160, false, "AlMasCercano")]
        // The two cell ones: 2794 changes caster and 2960 does not.
        [InlineData(2794, true, "ALaCasillaDelPadre")]
        [InlineData(2960, false, "ALaCasillaDelPadre")]
        public void Cada_uno_lanza_y_apunta_como_dicen_las_capturas(int efecto, bool lanzaElCandidato,
                                                                    string apunta)
        {
            var (quien, aQue) = EffectEngine.ComoSublanza(efecto);

            Assert.Equal(lanzaElCandidato, quien);
            Assert.Equal(apunta, aQue);
        }

        [Theory]
        [InlineData(3792)]
        [InlineData(3793)]
        public void Los_dos_marcadores_de_guion_no_hacen_nada(int efecto)
        {
            // Measured on the 164 rows of 3792 that have a template, without an exception: its value
            // is an id of the spell's own boundScriptUsageData, and the rest of the row is at
            // zero. It carries no number to apply to anyone.
            Assert.True(EffectEngine.EsMarcadorDeGuion(efecto));
            Assert.False(EffectEngine.EsDeLaFamiliaDeSublanzar(efecto));
            Assert.False(EffectEngine.EsDeDano(efecto));
        }

        [Fact]
        public void El_3793_no_hace_nada_y_esta_bien_que_no_lo_haga()
        {
            // 430 rows with no text, no characteristic, no dice and no duration. The real server
            // records it and announces it, but it drags nobody along: the effects that go with it
            // fire on their own, with their own trigger. Treating it as a conditional gate
            // would be inventing a mechanic.
            Assert.False(EffectEngine.EsDeLaFamiliaDeSublanzar(3793));
        }
    }
}
