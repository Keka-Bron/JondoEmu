using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Draconiros, en la puerta de los Sueños Infinitos.
    /// </summary>
    /// <remarks>
    /// El cliente trae su plantilla pero no dónde está, así que la colocación sale de la capa
    /// medida —datos/npcs_reales.json—, que la había sacado del jss del mapa 238553348: plantilla
    /// 4638, casilla 206, orientación 3. El iov de la captura confirma el mapa.
    ///
    /// Estaba colocado desde el principio. Lo que faltaba era poder llegar: su sala no es vecina
    /// de la del pozo en la rejilla y las cuatro arcadas que llevan a ella no estaban declaradas,
    /// así que estaba puesto y era inalcanzable. Eso lo guarda DreamPlaneTests; esto guarda que
    /// siga puesto.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamNpcTests
    {
        private const int Draconiros = 4638;
        private const long SuMapa = 238553348;
        private const int SuCasilla = 206;

        [Fact]
        public void Draconiros_esta_colocado_a_la_puerta_del_pozo()
        {
            Npcs.Initialize();

            var suyos = Npcs.OnMap(SuMapa);
            Assert.Contains(suyos, s => s.NpcId == Draconiros && s.Cell == SuCasilla);
        }

        [Fact]
        public void Su_conversacion_es_la_medida_en_la_captura()
        {
            // El cliente trae las frases y las respuestas, nunca cuál va con cuál. Este árbol sale
            // del ios/ioy de la captura, así que si alguien lo reescribe a ojo, esto lo dice.
            Npcs.Initialize();

            var charla = NpcDialogues.For(Draconiros, SuMapa);
            Assert.NotNull(charla);
            Assert.Equal(32574, charla!.Opening);

            var apertura = charla.Line(32574);
            Assert.NotNull(apertura);
            Assert.Equal(5, apertura!.Choices.Count);

            // «Intentar comprender dónde estás» abre la cadena de seis frases hasta el pozo.
            Assert.Equal(32599, apertura.Choice(39607)!.Next);

            // Y «unirte al sueño del crisol onírico» no dice nada: mueve de mapa.
            var alCrisol = apertura.Choice(50321);
            Assert.NotNull(alCrisol);
            Assert.Equal(0, alCrisol!.Next);
            Assert.Equal(200804356, alCrisol.TeleportsTo);
        }

        [Fact]
        public void El_mapa_de_Draconiros_es_vecino_del_pozo()
        {
            // No sirve de nada colocarlo si no se llega andando desde donde está el pozo.
            Assert.NotEqual(Dreams.MapaDelPozo, SuMapa);
            Assert.Equal(238551040, Dreams.MapaDelPozo);
        }
    }
}
