using System.Globalization;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Las tramas de los Sueños Infinitos, con la forma que traen las capturas.
    /// </summary>
    /// <remarks>
    /// Dos mensajes llevan el peso, y los dos identifican las salas <b>por cadena</b> — «0», «1»,
    /// «2» — no por número. Eso no es un detalle de estilo: mandarlas como varint deja al cliente
    /// sin mapa y sin un solo error.
    /// </remarks>
    public static class DreamProtocol
    {
        /// <summary>
        /// El iyj: la ventana del sueño — cabecera, salas y grafo.
        /// </summary>
        /// <remarks>
        /// Toda la partida va DENTRO del f17. Ése es el detalle que importa y el que se tuvo mal:
        /// mandando las salas como f17 repetidos del padre y el grafo como f4 hermanos, el
        /// servidor contestaba, el cliente no se quejaba y no se abría nada.
        ///
        /// <code>
        ///   f1 {
        ///     f2   ¿?  medido 5, 10, 15 y 20        f3   el nombre
        ///     f7 { f1 { f4:1, f11:128 }, f2:1 }     f8   el nivel
        ///     f13  la cuenta de sueños              f14  1     f15  1     f16  "1" o "2"
        ///     f17 {
        ///       f1 (repetido)  una SALA:  f1 su número como cadena
        ///                                 f2 { f1 puntos, f3 clase, f4 { el grupo },
        ///                                      f5 1, f6 la fila, f7 señalada }
        ///       f3   la sala en la que se está, como cadena
        ///       f4 (repetido)  una ARISTA: f1 el origen, f2 { f1 cada destino }
        ///     }
        ///   }
        /// </code>
        ///
        /// Las tres formas de sala, contadas sobre las 89 de las nueve capturas:
        ///
        ///   la entrada (9 de 9)    f2 { f7: 0 } y nada más
        ///   las de pelea (71)      f1 4..40, f3 5 ó 15, f4 el grupo, f5 1, f6 la fila, f7 0 ó 1
        ///   la última (9 de 9)     f2 { f5: 3, f6: 4, f7: 0 }
        ///
        /// Y las cinco filas siempre: una sala, luego dos a cuatro por fila, luego una.
        /// </remarks>
        public static byte[] BuildDreamMap(Dreams.Sueno sueno)
        {
            var dentro = Pb.New()
                .Var(2, PorExplicar)
                .Str(3, sueno.Nombre)
                .Msg(7, Pb.New().Msg(1, Pb.New().Var(4, 1).Var(11, 128)).Var(2, 1))
                .Var(8, sueno.Nivel)
                .Var(13, sueno.Cuenta)
                .Var(14, 1)
                .Var(15, 1)
                .Str(16, "1");

            return Pb.New().Msg(1, dentro.Msg(17, Partida(sueno))).Build();
        }

        /// <summary>
        /// El grafo del sueño: las salas, en cuál se está y las aristas.
        /// </summary>
        /// <remarks>
        /// Viaja DOS veces y con la misma forma: como f17 del iyj —la ventana que ofrece el
        /// sueño— y como f16 del izg —el estado de dentro—. Que se repita no es un descuido de
        /// Ankama: son dos momentos distintos y el segundo es el que alimenta el mapa del sueño y
        /// los paneles mientras se juega. Mandar el izg sin él deja al jugador dentro de la sala
        /// sin mapa, sin lista de bonos y sin bestiario, que es lo que se veía.
        /// </remarks>
        private static Pb Partida(Dreams.Sueno sueno)
        {
            var partida = Pb.New();

            foreach (var sala in sueno.Salas)
            {
                var cuerpo = Pb.New();

                if (sala.Fila == 0)
                {
                    // La entrada. Va casi vacía en las nueve capturas: sólo el f7.
                    cuerpo.Var(7, 0);
                }
                else if (sala.Fila == UltimaFila)
                {
                    cuerpo.Var(5, 3).Var(6, sala.Fila).Var(7, 0);
                }
                else
                {
                    cuerpo.Var(1, sala.Puntos)
                          .Var(3, sala.Clase)
                          .Var(5, 1)
                          .Var(6, sala.Fila)
                          .Var(7, sala.Senalada ? 1 : 0);
                }

                partida.Msg(1, Pb.New()
                    .Str(1, Texto(sala.Id))
                    .Msg(2, cuerpo));
            }

            partida.Str(3, Texto(sueno.Actual));

            foreach (var sala in sueno.Salas)
            {
                if (sala.Salidas.Count == 0) continue;

                var destinos = Pb.New();
                foreach (int destino in sala.Salidas) destinos.Str(1, Texto(destino));

                partida.Msg(4, Pb.New()
                    .Str(1, Texto(sala.Id))
                    .Msg(2, destinos));
            }

            return partida;
        }

        /// <summary>La última fila, la que lleva una sola sala. Cinco filas en las nueve capturas.</summary>
        private const int UltimaFila = 4;

        /// <summary>
        /// El f2 de la cabecera, que no se ha sabido qué es.
        /// </summary>
        /// <remarks>
        /// Nueve muestras y sólo cuatro valores —5, 10, 15 y 20—, siempre múltiplo de cinco, del
        /// mismo personaje y siempre a nivel 200. No sigue a la dificultad, que se manda después
        /// en el ixf, ni a la cuenta de sueños del f13. Se manda el menor de los medidos.
        /// </remarks>
        private const int PorExplicar = 5;

        /// <summary>
        /// El izg: el estado del sueño en curso.
        /// </summary>
        /// <remarks>
        /// Medido en la captura de Pesadilla II, donde el f2 vale 9 — la misma dificultad que se
        /// mandó en el ixf, que es lo que ata los dos mensajes:
        ///
        /// <code>
        ///   f1 { f1 el nombre, f3 el id del personaje }
        ///   f2   la dificultad
        ///   f4 (repetido)  una PUERTA:  f1 la sala a la que lleva, como cadena
        ///                               f2 el elemento interactivo que el cliente pulsara
        ///   f8   los puntos      f13  la sala en la que se está, como cadena
        /// </code>
        /// </remarks>
        public static byte[] BuildDreamState(Dreams.Sueno sueno)
        {
            var quien = Pb.New()
                .Str(1, sueno.Nombre)
                .Var(3, sueno.CharacterId)
                .Var(4, 11);

            var izg = Pb.New()
                .Msg(1, quien)
                .Var(2, sueno.Dificultad);

            // LAS TRES PUERTAS, no sólo las que llevan a algún sitio. En la captura de Pesadilla
            // II la sala de entrada lista las tres y la de en medio va sin destino:
            //
            //   f4 { f1: "1", f2: 539509,          f5: 3 }
            //   f4 {          f2: 539510, f4: 1          }   ← ésta no lleva a ninguna parte
            //   f4 { f1: "2", f2: 539511, f4: 2,   f5: 3 }
            //
            // El f4 de dentro es el número de puerta, y el cero no se escribe. Mandando sólo las
            // que tienen destino, el cliente no sabe cuál de las tres está muerta y las pinta a
            // las tres igual: pulsas una y no pasa nada, sin saber por qué.
            var actual = sueno.SalaActual;
            if (actual != null)
            {
                for (int cual = 0; cual < Dreams.PuertasPorSala; cual++)
                {
                    int puerta = Dreams.PuertaDe(actual, cual);
                    if (puerta == 0) continue;

                    var entrada = Pb.New();
                    bool lleva = cual < actual.Salidas.Count;

                    if (lleva) entrada.Str(1, Texto(actual.Salidas[cual]));
                    entrada.Var(2, puerta);
                    if (cual != 0) entrada.Var(4, cual);
                    if (lleva) entrada.Var(5, TipoDePortal);

                    izg.Msg(4, entrada);
                }
            }

            // El f7 es el número de TORMENTAS ASTRALES que quedan: en la captura larga va 1, luego
            // desaparece —cero no se escribe— y más tarde vuelve como 2, que es el número que el
            // cliente pinta en su botón. El f19 es la Arena de Draconiros, con la que se reintenta
            // una pelea perdida. Los dos siguen sin gastarse ni ganarse; van fijos, y queda dicho.
            izg.VarIfNotZero(7, sueno.Tormentas)
               .Var(8, sueno.Puntos)
               .Str(13, Texto(sueno.Actual))
               .Msg(16, Partida(sueno))
               .Var(17, 1)
               .VarIfNotZero(19, sueno.Arena)
               .Var(20, sueno.Nivel)
               .Var(22, sueno.PuntosDeSalida);

            return izg.Build();
        }

        /// <summary>
        /// El f5 de una puerta que lleva a algún sitio. Medido en 3.
        /// </summary>
        /// <remarks>
        /// La guía del juego dice que el color del portal cambia según lo que haya al otro lado
        /// —naranja las de combate, azul la fuente, verde el favor—, así que esto es seguramente
        /// eso. En las capturas sólo se ha visto el 3, y todas las salas que salen en ellas son de
        /// combate, o sea que cuadra sin demostrarlo: es una suposición razonable con una sola
        /// clase medida, no una medición de las tres.
        /// </remarks>
        private const int TipoDePortal = 3;

        /// <summary>El izj que acompaña a la tormenta astral: «1001» de la captura.</summary>
        public static byte[] BuildStorm() => Pb.New().Var(2, 1).Build();

        /// <summary>El iyb de la salida: «0801» de la captura.</summary>
        public static byte[] BuildLeft() => Pb.New().Var(1, 1).Build();

        /// <summary>
        /// Los números de sala viajan como CADENA, y por eso pasan por aquí.
        /// </summary>
        /// <remarks>
        /// Con la cultura invariante a propósito: con una cultura que use otro separador, un
        /// número de sala saldría escrito de otra forma y el cliente no lo casaría con su grafo.
        /// </remarks>
        private static string Texto(int n) => n.ToString(CultureInfo.InvariantCulture);

    }
}
