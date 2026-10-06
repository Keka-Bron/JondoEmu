using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Draconiros, at the door of the Infinite Dreams.
    /// </summary>
    /// <remarks>
    /// The client brings his template but not where he is, so the placement comes from the
    /// measured layer —datos/npcs_reales.json—, which had taken it from the jss of map 238553348: template
    /// 4638, cell 206, orientation 3. The capture's iov confirms the map.
    ///
    /// He was placed from the start. What was missing was being able to get there: his room is not a neighbour
    /// of the well's in the grid and the four arches leading to it were not declared,
    /// so he was placed and unreachable. DreamPlaneTests keeps that; this keeps him
    /// placed.
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
            // The client brings the sentences and the replies, never which goes with which. This tree comes
            // from the capture's ios/ioy, so if someone rewrites it by eye, this says so.
            Npcs.Initialize();

            var charla = NpcDialogues.For(Draconiros, SuMapa);
            Assert.NotNull(charla);
            Assert.Equal(32574, charla!.Opening);

            var apertura = charla.Line(32574);
            Assert.NotNull(apertura);
            Assert.Equal(5, apertura!.Choices.Count);

            // «Intentar comprender dónde estás» opens the chain of six sentences up to the well.
            Assert.Equal(32599, apertura.Choice(39607)!.Next);

            // And «unirte al sueño del crisol onírico» says nothing: it changes map.
            var alCrisol = apertura.Choice(50321);
            Assert.NotNull(alCrisol);
            Assert.Equal(0, alCrisol!.Next);
            Assert.Equal(200804356, alCrisol.TeleportsTo);
        }

        [Fact]
        public void El_mapa_de_Draconiros_es_vecino_del_pozo()
        {
            // Placing him is no use if he cannot be reached on foot from where the well is.
            Assert.NotEqual(Dreams.MapaDelPozo, SuMapa);
            Assert.Equal(238551040, Dreams.MapaDelPozo);
        }
    }
}
