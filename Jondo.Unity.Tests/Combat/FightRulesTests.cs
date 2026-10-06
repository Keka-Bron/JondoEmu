using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The table of what changes from one fight type to another.
    /// </summary>
    /// <remarks>
    /// These seven answers were dissolved in sixteen <c>if</c>s spread over five methods
    /// of the engine. Together they fit on one screen, and this is where it is checked that they still say
    /// what the captures say.
    ///
    /// The numbers are not chosen: 4, 0 and 7 are the kam's f2, and 592 is the koliseo kaa's
    /// f5.
    /// </remarks>
    public class FightRulesTests
    {
        [Fact]
        public void Contra_monstruos_es_como_ha_sido_siempre()
        {
            var r = FightRules.ContraMonstruos;

            Assert.True(r.HayRetos);
            Assert.Equal(450, r.RelojDeColocacion);       // 45 segundos
            Assert.Equal(4, r.TipoDelKam);
            Assert.True(r.EnfrenteHayMonstruos);
            Assert.True(r.ReparteBotin);
            Assert.True(r.BorraElGrupoAlGanar);
            Assert.True(r.AvanzaDeSala);
        }

        [Fact]
        public void Un_desafio_no_da_nada_y_no_tiene_reloj()
        {
            var r = FightRules.Desafio;

            // The fight starts when both press ready, not when some time runs out: the
            // real server sends none, and its kaa is six bytes without f5.
            Assert.Equal(0, r.RelojDeColocacion);
            Assert.False(r.KaaConCuentaAtras);

            Assert.Equal(0, r.TipoDelKam);
            Assert.False(r.HayRetos);
            Assert.False(r.EnfrenteHayMonstruos);

            // No loot: winning a challenge once paid kamas for the rival's level, as if
            // you had hunted him.
            Assert.False(r.ReparteBotin);
            Assert.False(r.BorraElGrupoAlGanar);
            Assert.False(r.AvanzaDeSala);
        }

        [Fact]
        public void El_koliseo_es_pvp_pero_con_reloj()
        {
            var r = FightRules.Koliseo;

            // This is the reason there are three rules and not two: the koliseo is PvP in everything except
            // the clock, which it has like a normal fight.
            Assert.False(r.EnfrenteHayMonstruos);
            Assert.Equal(592, r.RelojDeColocacion);
            Assert.True(r.KaaConCuentaAtras);
            Assert.Equal(7, r.TipoDelKam);

            Assert.False(r.HayRetos);
            Assert.False(r.ReparteBotin);
        }

        [Fact]
        public void Un_combate_nace_contra_monstruos()
        {
            // The usual is the usual without saying anything: whoever sets up a new fight without
            // thinking about this gets the rules of fighting creatures.
            var fight = new FightInstance(1, 100);

            Assert.Same(FightRules.ContraMonstruos, fight.Reglas);
            Assert.False(fight.EsPvp);
        }

        [Fact]
        public void Los_dos_de_pvp_se_reconocen_como_tales()
        {
            Assert.True(new FightInstance(1, 100) { Reglas = FightRules.Desafio }.EsPvp);
            Assert.True(new FightInstance(1, 100) { Reglas = FightRules.Koliseo }.EsPvp);
        }

        [Fact]
        public void El_reloj_y_la_cuenta_atras_del_kaa_dicen_lo_mismo()
        {
            // They were two loose decisions and could contradict each other: a kaa with a countdown and no
            // timer behind it, or the other way round. Now the second is deduced from the first.
            foreach (var r in new[] { FightRules.ContraMonstruos, FightRules.Desafio, FightRules.Koliseo })
            {
                Assert.Equal(r.RelojDeColocacion > 0, r.KaaConCuentaAtras);
            }
        }
    }
}
