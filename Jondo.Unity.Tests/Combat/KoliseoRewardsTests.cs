using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Lo que cobra el que gana un koliseo, contra el jyg de la captura.
    /// </summary>
    /// <remarks>
    /// Las cuatro entradas del jyg de «koliseo completo con invitacion-koli 2vs2», medidas:
    ///
    ///   ganan    nivel 227  3.400 kamas  260 × 12736  2 × 34478  4.722.600 de experiencia
    ///            nivel 290  2.800 kamas  230 × 12736  2 × 34478  7.496.344 de experiencia
    ///   pierden  nivel 354 y nivel 447, botín vacío y sin experiencia ganada
    ///
    /// De dos ganadores no sale una fórmula, así que los kamas y las monedas son constantes y la
    /// experiencia es una parte de la banda del nivel. Lo que estas pruebas fijan es que las
    /// constantes sigan siendo las medidas y que la experiencia siga cayendo donde caía en los dos
    /// puntos que hay: entre el 6 % y el 7,3 % de la banda.
    /// </remarks>
    public class KoliseoRewardsTests
    {
        [Fact]
        public void Las_monedas_son_las_de_la_captura()
        {
            Assert.Equal(12736, KoliseoRewards.Kolicha);
            Assert.Equal(34478, KoliseoRewards.Vitoricha);

            var botin = KoliseoRewards.Botin();
            Assert.Equal(KoliseoRewards.KolichasPorVictoria, botin[KoliseoRewards.Kolicha]);
            Assert.Equal(2, botin[KoliseoRewards.Vitoricha]);

            // Las kolichas medidas son 260 y 230; la constante tiene que quedarse entre las dos.
            Assert.InRange(KoliseoRewards.KolichasPorVictoria, 230, 260);
            Assert.InRange(KoliseoRewards.KamasPorVictoria, 2800, 3400);
        }

        [Theory]
        [InlineData(227)]
        [InlineData(290)]
        [InlineData(1)]
        [InlineData(200)]
        public void La_experiencia_cae_donde_la_medida(int nivel)
        {
            // La tabla se lee de un fichero y el servidor la carga al arrancar; aquí no hay
            // arranque. Sin esto la banda sale cero y la prueba mide el vacío.
            ExperienceTable.Initialize();

            long suelo = ExperienceTable.LevelFloor(nivel);
            long banda = ExperienceTable.NextLevelFloor(nivel) - suelo;
            Assert.True(banda > 0, $"el nivel {nivel} no tiene banda");

            long gana = KoliseoRewards.Experiencia(nivel);

            // 7,22 % y 6,12 % son los dos puntos medidos. Se deja el margen justo alrededor.
            Assert.InRange(gana, banda * 60 / 10000, banda * 730 / 10000);
        }

        [Fact]
        public void Solo_el_koliseo_paga_kolichas()
        {
            Assert.True(FightRules.Koliseo.PagaElKoliseo);
            Assert.False(FightRules.Desafio.PagaElKoliseo);
            Assert.False(FightRules.ContraMonstruos.PagaElKoliseo);

            // Y no por eso reparte las tablas de los monstruos: enfrente no hay monstruos.
            Assert.False(FightRules.Koliseo.ReparteBotin);
        }
    }
}
