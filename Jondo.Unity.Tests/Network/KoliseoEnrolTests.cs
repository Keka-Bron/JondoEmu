using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// De qué campo sale el índice de modalidad al apuntarse al koliseo.
    /// </summary>
    /// <remarks>
    /// Son DOS peticiones para el mismo botón y el índice no viaja en el mismo sitio, que es lo
    /// que hacía que estando en grupo no pasara nada al pulsar «encontrar una partida»:
    ///
    ///   solo      luy { f2 = índice }    medido en la captura del koliseo completo: «1001»
    ///   en grupo  lsm { f1 = índice }    medido sobre nuestro cliente: «0801»
    ///
    /// Los dos con el MISMO índice para la misma modalidad — el 1 es el 2 contra 2, la entrada 1
    /// del ltd — así que lo único que cambia es el número de campo. Leer el 2 en un lsm devuelve
    /// «no hay índice», el manejador se calla, y el cliente se queda esperando un acuse que no
    /// llega y sin un solo error por ninguna parte.
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
            // «1001» tal cual sale de la captura, apuntándose a un 2 contra 2.
            var luy = new byte[] { 0x10, 0x01 };

            Assert.Equal(1, IndiceDe(luy, 2));
            Assert.Equal(-1, IndiceDe(luy, 1));
        }

        [Fact]
        public void En_grupo_va_en_el_campo_1()
        {
            // «0801», lo que manda nuestro cliente con el grupo hecho, en el mismo 2 contra 2.
            var lsm = new byte[] { 0x08, 0x01 };

            Assert.Equal(1, IndiceDe(lsm, 1));
            Assert.Equal(-1, IndiceDe(lsm, 2));
        }

        [Fact]
        public void El_uno_contra_uno_es_el_indice_cero_y_no_viaja()
        {
            // El 1 contra 1 es la primera entrada del ltd, o sea el indice 0, y protobuf no manda
            // los ceros: la carga llega VACIA. Leyendo eso como «no hay indice» -que es lo que
            // hacia, empezando en menos uno- el 1 contra 1 caia en «modalidad no abierta» y el
            // cliente se quedaba esperando un acuse que no llegaba, sin un solo aviso.
            Assert.Equal(0, KoliseoHandler.IndiceDeModalidad(System.Array.Empty<byte>(), 1));
            Assert.Equal(0, KoliseoHandler.IndiceDeModalidad(System.Array.Empty<byte>(), 2));

            // Y con el campo puesto, lo que ponga. El 1 es el 2 contra 2.
            Assert.Equal(1, KoliseoHandler.IndiceDeModalidad(new byte[] { 0x08, 0x01 }, 1));
            Assert.Equal(1, KoliseoHandler.IndiceDeModalidad(new byte[] { 0x10, 0x01 }, 2));

            // Un campo que no es el suyo no cuenta: leer el 2 en un lsm daria cero, no el valor.
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
