using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// El aspecto de una invocación, y cuándo una cadena manda a otra plantilla.
    /// </summary>
    /// <remarks>
    /// La cadena de la base es <c>{hueso|pieles|colores|escala}</c> y hay tres formas. Sólo dos son
    /// reenvíos, y confundirlas es lo que dejó a la Baliza de Supervivencia del Ocra pintada como
    /// un cuadrado azul: su rastro es 8348 → «{8152}» → «{1|91,…}», y al llegar a la tercera se
    /// leía el 1 como si fuera otra plantilla y se seguía hasta la 1, que es otro bicho cualquiera.
    ///
    /// Lo que dice dónde parar está medido en «ocra-baliza de supervivencia»: su jwe manda
    /// «f3{f2=3, f3=8152}», o sea que la buena es la 8152.
    /// </remarks>
    public class SummonLookTests
    {
        [Fact]
        public void Una_cadena_pelada_manda_a_otra_plantilla()
        {
            // Lo que tiene la 8348, la baliza.
            Assert.True(Summons.EsReenvio("{8152}", out int hacia));
            Assert.Equal(8152, hacia);
        }

        [Fact]
        public void Un_reenvio_con_escala_tambien_manda()
        {
            // Lo que tiene el «Regalo animado», la 3106. Sin pieles y sin colores: no es un
            // aspecto, es un reenvío que además cambia el tamaño.
            Assert.True(Summons.EsReenvio("{446|||120}", out int hacia));
            Assert.Equal(446, hacia);
        }

        [Fact]
        public void El_aspecto_de_verdad_no_manda_a_ninguna_parte()
        {
            // La cadena de la 8152, que es donde hay que pararse. Su primer número es el HUESO, no
            // una plantilla; seguirlo lleva a la 1 y de ahí al cuadrado azul.
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
    }
}
