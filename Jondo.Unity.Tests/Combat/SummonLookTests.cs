using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A summon's look, and when a string sends to another template.
    /// </summary>
    /// <remarks>
    /// The base's string is <c>{bone|skins|colours|scale}</c> and there are three forms. Only two are
    /// referrals, and confusing them is what left the Cra's Baliza de Supervivencia drawn as
    /// a blue square: its trail is 8348 → «{8152}» → «{1|91,…}», and on reaching the third
    /// the 1 was read as if it were another template and followed to template 1, which is some other creature.
    ///
    /// What says where to stop is measured in «ocra-baliza de supervivencia»: its jwe sends
    /// «f3{f2=3, f3=8152}», that is the good one is 8152.
    /// </remarks>
    public class SummonLookTests
    {
        [Fact]
        public void Una_cadena_pelada_manda_a_otra_plantilla()
        {
            // What 8348, the beacon, has.
            Assert.True(Summons.EsReenvio("{8152}", out int hacia));
            Assert.Equal(8152, hacia);
        }

        [Fact]
        public void Un_reenvio_con_escala_tambien_manda()
        {
            // What the «Regalo animado», 3106, has. No skins and no colours: it is not a
            // look, it is a referral that also changes the size.
            Assert.True(Summons.EsReenvio("{446|||120}", out int hacia));
            Assert.Equal(446, hacia);
        }

        [Fact]
        public void El_aspecto_de_verdad_no_manda_a_ninguna_parte()
        {
            // 8152's string, which is where to stop. Its first number is the BONE, not
            // a template; following it leads to template 1 and from there to the blue square.
            Assert.False(Summons.EsReenvio(
                "{1|91,5239,4977|1=#FFFFFF,2=#62A1C9,3=#4482A0,4=#2F374D,5=#C4CFD3,6=#E9CE99|52}",
                out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{}")]
        [InlineData("{no-es-un-numero}")]
        [InlineData("{0}")]
        public void Lo_que_no_es_un_reenvio_se_deja_en_paz(string cadena)
        {
            Assert.False(Summons.EsReenvio(cadena, out _));
        }
    
        [Fact]
        public void La_referencia_pelada_de_una_bomba_es_su_aspecto_y_no_otra_plantilla()
        {
            // The Rogue's four bombs carry a bare reference, and what goes in the
            // packet is the number inside. Three of them -- 1561, 1562, 1563 -- are not
            // templates of anything; the fourth, the Sismobomba, carries «{2865}», and 2865 IS a
            // template: the Ventozador. Following the trail drew it as a Ventozador.
            foreach (var (cadena, numero) in new[]
                     {
                         ("{1562}", 1562),   // Explobomba
                         ("{1563}", 1563),   // Tornabomba
                         ("{1561}", 1561),   // Bomba de agua
                         ("{2865}", 2865),   // Sismobomba
                     })
            {
                Assert.True(Summons.EsReenvio(cadena, out int hacia));
                Assert.Equal(numero, hacia);
            }
        }
    }
}
