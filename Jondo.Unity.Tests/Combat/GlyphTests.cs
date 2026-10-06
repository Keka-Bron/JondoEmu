using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What is laid on the ground: glyphs, traps and runes.
    /// </summary>
    /// <remarks>
    /// The catalogue's four families —1091 the aura glyph with 316 spells, 401 the turn-start
    /// one with 142, 400 the trap with 100 and 2022 the rune with 65— have the same
    /// measured shape and differ only in when they fire. That is why there is a single type with an enum and
    /// not four classes with the same body.
    /// </remarks>
    public class GlyphTests
    {
        private static Glifo Poner(Disparo cuando, int caduca = 0, params int[] casillas)
            => new Glifo(dueno: 1, casillas: casillas, hechizo: 6382, grado: 1,
                         color: 16777215, caducaEnRonda: caduca, mascara: "a,A", cuando: cuando);

        [Fact]
        public void La_trampa_se_dispara_al_pisarla_y_no_al_empezar_el_turno()
        {
            var trampa = Poner(Disparo.AlPisar, 0, 100, 101);

            Assert.True(trampa.SeDisparaAlPisar);
            Assert.False(trampa.SeDisparaAlEmpezarElTurno);
            Assert.True(trampa.SeGastaAlDispararse);
            Assert.True(trampa.Cubre(101));
            Assert.False(trampa.Cubre(102));
        }

        [Fact]
        public void El_glifo_de_inicio_de_turno_es_al_reves()
        {
            var glifo = Poner(Disparo.AlEmpezarElTurno, 0, 200);

            Assert.False(glifo.SeDisparaAlPisar);
            Assert.True(glifo.SeDisparaAlEmpezarElTurno);
            Assert.False(glifo.SeGastaAlDispararse);
        }

        [Fact]
        public void El_de_aura_y_la_runa_se_disparan_con_las_dos_cosas()
        {
            var aura = Poner(Disparo.AlPisarYAlEmpezar, 0, 300);

            Assert.True(aura.SeDisparaAlPisar);
            Assert.True(aura.SeDisparaAlEmpezarElTurno);
            Assert.False(aura.SeGastaAlDispararse);
        }

        [Fact]
        public void Un_gastado_ya_no_se_dispara_con_nada()
        {
            var trampa = Poner(Disparo.AlPisar, 0, 100);
            trampa.Gastado = true;

            Assert.False(trampa.SeDisparaAlPisar);
            Assert.False(trampa.SeDisparaAlEmpezarElTurno);
        }

        [Fact]
        public void El_combate_reparte_identificadores_y_barre_los_caidos()
        {
            var combate = new FightInstance(1, 1, 1);

            var permanente = combate.Poner(Poner(Disparo.AlPisarYAlEmpezar, 0, 10));
            var corto = combate.Poner(Poner(Disparo.AlEmpezarElTurno, caduca: 2, casillas: 20));
            var gastada = combate.Poner(Poner(Disparo.AlPisar, 0, 30));

            // Each its own: two with the same number would be a single one for the client.
            Assert.Equal(3, new[] { permanente.Id, corto.Id, gastada.Id }.Distinct().Count());
            Assert.Equal(3, combate.Glifos.Count);

            gastada.Gastado = true;
            var caidos = combate.BarrerLosGlifos();

            Assert.Contains(gastada, caidos);
            Assert.Equal(2, combate.Glifos.Count);

            // The one with duration -1 arrived as zero and does not drop; the two-round one does, when its time comes.
            Assert.Equal(0, permanente.CaducaEnRonda);
            Assert.Contains(permanente, combate.Glifos);
        }

        [Fact]
        public void Se_encuentra_por_la_casilla_que_se_pisa()
        {
            var combate = new FightInstance(1, 1, 1);
            combate.Poner(Poner(Disparo.AlPisar, 0, 100, 101, 102));
            combate.Poner(Poner(Disparo.AlEmpezarElTurno, 0, 101));

            // Stepping on 101 fires the trap, not the turn-start one.
            Assert.Single(combate.LosQuePisa(101));
            Assert.Single(combate.LosQueEmpiezan(101));
            Assert.Empty(combate.LosQuePisa(500));
        }
    }
}
