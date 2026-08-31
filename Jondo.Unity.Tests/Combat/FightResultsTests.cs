using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// La pantalla de fin de combate (jyg).
    /// </summary>
    /// <remarks>
    /// Medido en el jyg del koliseo 2 contra 2, que es el único final de un combate ENTRE PERSONAS
    /// que hay capturado. Sus cuatro entradas se parten en dos y dos, y de ahí salen las dos reglas
    /// que se sujetan aquí:
    ///
    ///   - las cuatro traen su bloque de experiencia con el nivel dentro (227, 354, 447…), ganen o
    ///     pierdan
    ///   - las dos que pierden NO traen ni el f3 de dentro ni el f4 de la victoria
    ///
    /// La primera es la que importa de verdad: el cliente entiende que una entrada sin nivel es un
    /// monstruo, y como del rival no tenía monstruo que dibujar, pintaba una interrogación donde
    /// iba su retrato.
    /// </remarks>
    public class FightResultsTests
    {
        private static List<ProtoField> Entradas(byte[] jyg)
            => ProtoMessage.Parse(jyg).Fields.Where(f => f.FieldNumber == 2).ToList();

        private static ProtoMessage Dentro(ProtoField campo, int numero)
            => ProtoMessage.Parse(ProtoMessage.Parse(campo.BytesValue).Fields
                                              .First(f => f.FieldNumber == numero).BytesValue);

        private static bool Tiene(ProtoMessage m, int numero)
            => m.Fields.Any(f => f.FieldNumber == numero);

        [Fact]
        public void El_que_pierde_no_lleva_la_marca_de_victoria()
        {
            byte[] jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = 10, Winner = true, Level = 200, Xp = 1 },
                new FightProtocol.FightResult { Fighter = 20, Winner = false, Level = 50, Xp = 1 },
            }, 1000);

            var entradas = Entradas(jyg);
            Assert.Equal(2, entradas.Count);

            var ganador = ProtoMessage.Parse(entradas[0].BytesValue);
            var perdedor = ProtoMessage.Parse(entradas[1].BytesValue);

            // El f4 de la victoria.
            Assert.True(Tiene(ganador, 4));
            Assert.False(Tiene(perdedor, 4));

            // El f3 de dentro NO distingue: lo llevan los dos. Lo quité creyendo que era del
            // ganador —en el jyg del koliseo las dos personas que pierden no lo traen— y la
            // guardia de regresión lo cazó contra una captura contra monstruos, donde el bicho
            // que pierde sí lo lleva. Qué significa sigue sin saberse.
            Assert.True(Tiene(Dentro(entradas[0], 3), 3));
            Assert.True(Tiene(Dentro(entradas[1], 3), 3));
        }

        [Fact]
        public void El_que_pierde_si_lleva_su_nivel()
        {
            // Ésta es la de la interrogación: sin nivel, el cliente cree que es un monstruo.
            byte[] jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = 20, Winner = false, Level = 50, Xp = 1 },
            }, 1000);

            var quien = Dentro(Entradas(jyg)[0], 3);
            var ficha = ProtoMessage.Parse(quien.Fields.First(f => f.FieldNumber == 2).BytesValue);

            Assert.Equal(50, (int)ficha.Fields.First(f => f.FieldNumber == 2).VarIntValue);
        }

        [Fact]
        public void Un_monstruo_va_sin_ficha()
        {
            // Nivel cero: sólo quién es y si ganó. Es lo que separa a un bicho de una persona.
            byte[] jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = -1, Winner = false },
            }, 1000);

            Assert.False(Tiene(Dentro(Entradas(jyg)[0], 3), 2));
        }
    }
}
