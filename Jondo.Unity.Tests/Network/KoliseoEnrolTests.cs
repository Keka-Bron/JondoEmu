using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// Which field the mode index comes from on signing up for the koliseo.
    /// </summary>
    /// <remarks>
    /// They are TWO requests for the same button and the index does not travel in the same place, which is
    /// what made nothing happen when in a party on pressing «encontrar una partida»:
    ///
    ///   alone     luy { f2 = index }     measured in the complete koliseo capture: «1001»
    ///   in party  lsm { f1 = index }     measured on our client: «0801»
    ///
    /// Both with the SAME index for the same mode — 1 is the 2 versus 2, entry 1
    /// of the ltd — so the only thing that changes is the field number. Reading 2 in an lsm returns
    /// «no index», the handler keeps quiet, and the client is left waiting for an acknowledgement that never
    /// arrives and without a single error anywhere.
    /// </remarks>
    public class KoliseoEnrolTests
    {
        private static int IndiceDe(byte[] carga, int campo)
        {
            foreach (var field in ProtoMessage.Parse(carga).Fields)
            {
                if (field.FieldNumber == campo && field.WireType == 0) return (int)field.VarIntValue;
            }
            return -1;
        }

        [Fact]
        public void Solo_el_indice_va_en_el_campo_2()
        {
            // «1001» just as it comes out of the capture, signing up for a 2 versus 2.
            var luy = new byte[] { 0x10, 0x01 };

            Assert.Equal(1, IndiceDe(luy, 2));
            Assert.Equal(-1, IndiceDe(luy, 1));
        }

        [Fact]
        public void En_grupo_va_en_el_campo_1()
        {
            // «0801», what our client sends with the party formed, in the same 2 versus 2.
            var lsm = new byte[] { 0x08, 0x01 };

            Assert.Equal(1, IndiceDe(lsm, 1));
            Assert.Equal(-1, IndiceDe(lsm, 2));
        }

        [Fact]
        public void El_uno_contra_uno_es_el_indice_cero_y_no_viaja()
        {
            // The 1 versus 1 is the ltd's first entry, that is index 0, and protobuf does not send
            // zeros: the payload arrives EMPTY. Reading that as «no index» -which is what it
            // did, starting at minus one- the 1 versus 1 fell into «mode not open» and the
            // client was left waiting for an acknowledgement that never came, without a single warning.
            Assert.Equal(0, KoliseoHandler.IndiceDeModalidad(System.Array.Empty<byte>(), 1));
            Assert.Equal(0, KoliseoHandler.IndiceDeModalidad(System.Array.Empty<byte>(), 2));

            // And with the field set, whatever it says. 1 is the 2 versus 2.
            Assert.Equal(1, KoliseoHandler.IndiceDeModalidad(new byte[] { 0x08, 0x01 }, 1));
            Assert.Equal(1, KoliseoHandler.IndiceDeModalidad(new byte[] { 0x10, 0x01 }, 2));

            // A field that is not its own does not count: reading 2 in an lsm would give zero, not the value.
            Assert.Equal(0, KoliseoHandler.IndiceDeModalidad(new byte[] { 0x08, 0x02 }, 2));
        }

        [Fact]
        public void Los_dos_opcodes_existen_y_no_se_confunden()
        {
            Assert.Equal("luy", Op.Luy);
            Assert.Equal("lsm", Op.Lsm);
            Assert.NotEqual(Op.Luy, Op.Lsm);
        }
    }
}
