using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// La tabla de lo que cambia de un tipo de combate a otro.
    /// </summary>
    /// <remarks>
    /// Estas siete respuestas estaban disueltas en dieciséis <c>if</c> repartidos por cinco métodos
    /// del motor. Juntas caben en una pantalla, y aquí es donde se comprueba que siguen diciendo lo
    /// que dicen las capturas.
    ///
    /// Los números no son elegidos: el 4, el 0 y el 7 son el f2 del kam, y el 592 es el f5 del kaa
    /// del koliseo.
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

            // El combate empieza cuando los dos pulsan listo, no cuando se acaba un tiempo: el
            // servidor real no manda ninguno, y su kaa son seis bytes sin el f5.
            Assert.Equal(0, r.RelojDeColocacion);
            Assert.False(r.KaaConCuentaAtras);

            Assert.Equal(0, r.TipoDelKam);
            Assert.False(r.HayRetos);
            Assert.False(r.EnfrenteHayMonstruos);

            // Nada de botín: ganar un desafío llegó a pagar kamas por el nivel del rival, como si
            // lo hubieras cazado.
            Assert.False(r.ReparteBotin);
            Assert.False(r.BorraElGrupoAlGanar);
            Assert.False(r.AvanzaDeSala);
        }

        [Fact]
        public void El_koliseo_es_pvp_pero_con_reloj()
        {
            var r = FightRules.Koliseo;

            // Ésta es la razón de que sean tres reglas y no dos: el koliseo es PvP en todo salvo
            // en el reloj, que lo tiene como un combate normal.
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
            // Lo de siempre es lo de siempre sin decir nada: quien monte un combate nuevo sin
            // pensar en esto se lleva las reglas de pelear contra bichos.
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
            // Eran dos decisiones sueltas y podían contradecirse: un kaa con cuenta atrás y ningún
            // temporizador detrás, o al revés. Ahora la segunda se deduce de la primera.
            foreach (var r in new[] { FightRules.ContraMonstruos, FightRules.Desafio, FightRules.Koliseo })
            {
                Assert.Equal(r.RelojDeColocacion > 0, r.KaaConCuentaAtras);
            }
        }
    }
}
