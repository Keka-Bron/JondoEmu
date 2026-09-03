using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// La tienda de los Sueños: el Rey Gob, que es un NPC y no un protocolo nuevo.
    /// </summary>
    /// <remarks>
    /// Medido en «sueño infinito largo». La fuente no manda un mensaje propio: el jugador habla
    /// con el npc 7850 igual que con cualquiera, y lo que compra va DENTRO de la respuesta.
    ///
    ///   C->S iov  el npc, contextual -157447 en el mapa 237783053
    ///   S->C ios  08c0d203 1204088b8305                  «¡REY GOB!» + una respuesta
    ///   C->S ioy  088b8305
    ///   S->C ios  08d4d403 120e088a83051a0308c51f1a0308c31f 1204088c8305
    ///   C->S ioy  088a8305
    ///   S->C kld  0801
    ///
    /// Y esa respuesta larga es la clave: f2 { f1: 82314, f3 { f1: 4037 }, f3 { f1: 4035 } }. Los
    /// dos f3 son números que el cliente mete en su propio texto, «Multiplicar los puntos de
    /// sueño por 1,5».
    /// </remarks>
    [Collection("MapManager")]
    public class DreamShopTests
    {
        [Fact]
        public void El_rey_gob_ofrece_lo_que_dice_la_captura()
        {
            Npcs.Initialize();

            var charla = NpcDialogues.For(Dreams.ReyGob, 0);
            Assert.NotNull(charla);
            Assert.Equal(59712, charla!.Opening);

            // Se saluda, y eso lleva a la frase que ofrece.
            var saludo = charla.Line(59712);
            Assert.NotNull(saludo);
            Assert.Equal(59988, Assert.Single(saludo!.Choices).Next);

            var oferta = charla.Line(59988);
            Assert.NotNull(oferta);
            Assert.Equal(2, oferta!.Choices.Count);

            // «Multiplicar los puntos de sueño por 1,5», con sus dos parámetros.
            var bono = oferta.Choice(82314);
            Assert.NotNull(bono);
            Assert.Equal(150, bono!.DreamPointsPercent);
            Assert.Equal(new long[] { 4037, 4035 }, bono.Parameters.ToArray());

            // Y «Huir», que no hace nada.
            var huir = oferta.Choice(82316);
            Assert.NotNull(huir);
            Assert.Equal(0, huir!.DreamPointsPercent);
        }

        [Fact]
        public void El_ios_lleva_los_parametros_de_la_respuesta()
        {
            // La forma exacta de la captura: f2 { f1: 82314, f3 { f1: 4037 }, f3 { f1: 4035 } }.
            var parametros = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.IReadOnlyList<long>>
            {
                [82314] = new long[] { 4037, 4035 },
            };

            byte[] ios = Jondo.Unity.Server.Network.ConnectionProtocol.BuildNpcQuestion(
                59988, new long[] { 82314, 82316 }, parametros);

            Assert.Equal("08d4d403120e088a83051a0308c51f1a0308c31f1204088c8305",
                         System.Convert.ToHexString(ios).ToLowerInvariant());
        }

        [Fact]
        public void Un_sueno_tiene_salas_de_favor_a_partir_de_la_fila_dos()
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var s = Dreams.Crear(1, "Prueba", 200, 5, 100, 200);

            var favores = s.Salas.Where(x => x.EsFavor).ToList();
            Assert.NotEmpty(favores);
            Assert.All(favores, x => Assert.True(x.Fila >= 2));

            // Y en una sala de Favor no se pelea: no lleva grupo.
            Assert.All(favores, x => Assert.Empty(x.Miembros));
        }
    }
}
