using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What the winner of a koliseo is paid, against the capture's jyg.
    /// </summary>
    /// <remarks>
    /// The four entries of the jyg of «koliseo completo con invitacion-koli 2vs2», measured:
    ///
    ///   win      level 227  3,400 kamas  260 × 12736  2 × 34478  4,722,600 experience
    ///            level 290  2,800 kamas  230 × 12736  2 × 34478  7,496,344 experience
    ///   lose     level 354 and level 447, empty loot and no experience earned
    ///
    /// No formula comes out of two winners, so the kamas and the coins are constants and the
    /// experience is a part of the level's band. What these tests pin is that the
    /// constants stay the measured ones and that the experience keeps falling where it fell at the two
    /// points there are: between 6 % and 7.3 % of the band.
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

            // The measured kolichas are 260 and 230; the constant has to stay between the two.
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
            // The table loads itself the first time it is asked, so here it no longer has to be
            // started: before it was needed, and starting it from here emptied it under
            // FightResultsTests, which reads it through FightProtocol and runs in parallel.
            long suelo = ExperienceTable.LevelFloor(nivel);
            long banda = ExperienceTable.NextLevelFloor(nivel) - suelo;
            Assert.True(banda > 0, $"el nivel {nivel} no tiene banda");

            long gana = KoliseoRewards.Experiencia(nivel);

            // 7.22 % and 6.12 % are the two measured points. Just the right margin is left around them.
            Assert.InRange(gana, banda * 60 / 10000, banda * 730 / 10000);
        }

        [Fact]
        public void Solo_el_koliseo_paga_kolichas()
        {
            Assert.True(FightRules.Koliseo.PagaElKoliseo);
            Assert.False(FightRules.Desafio.PagaElKoliseo);
            Assert.False(FightRules.ContraMonstruos.PagaElKoliseo);

            // And that does not make it hand out the monsters' tables: there are no monsters opposite.
            Assert.False(FightRules.Koliseo.ReparteBotin);
        }
    }
}
