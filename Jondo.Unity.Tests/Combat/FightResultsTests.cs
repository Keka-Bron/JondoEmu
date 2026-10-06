using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The end-of-fight screen (jyg).
    /// </summary>
    /// <remarks>
    /// Measured on the jyg of the 2 versus 2 koliseo, which is the only end of a fight BETWEEN PEOPLE
    /// captured. Its four entries split two and two, and from there come the two rules
    /// held here:
    ///
    ///   - all four bring their experience block with the level inside (227, 354, 447…), win or
    ///     lose
    ///   - the two that lose bring neither the inner f3 nor the victory's f4
    ///
    /// The first is the one that really matters: the client understands that an entry without a level is a
    /// monster, and since it had no monster to draw for the rival, it drew a question mark where
    /// his portrait went.
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

            // The victory's f4.
            Assert.True(Tiene(ganador, 4));
            Assert.False(Tiene(perdedor, 4));

            // The inner f3 does NOT distinguish: both carry it. I removed it thinking it was the
            // winner's —in the koliseo's jyg the two people who lose do not bring it— and the
            // regression guard caught it against a capture against monsters, where the creature
            // that loses does carry it. What it means is still unknown.
            Assert.True(Tiene(Dentro(entradas[0], 3), 3));
            Assert.True(Tiene(Dentro(entradas[1], 3), 3));
        }

        [Fact]
        public void El_que_pierde_si_lleva_su_nivel()
        {
            // This is the question-mark one: without a level, the client believes it is a monster.
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
            // Level zero: only who it is and whether it won. It is what separates a creature from a person.
            byte[] jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = -1, Winner = false },
            }, 1000);

            Assert.False(Tiene(Dentro(Entradas(jyg)[0], 3), 2));
        }
    }
}
