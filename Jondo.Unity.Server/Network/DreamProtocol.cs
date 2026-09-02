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
        /// El iyj: la cabecera, las once salas y el grafo.
        /// </summary>
        /// <remarks>
        /// La forma sale de la captura de Paradoja I, leída campo a campo:
        ///
        /// <code>
        ///   f1 {
        ///     f3  el nombre del personaje      f8  su nivel
        ///     f17 (repetido)  una sala:  f1 su número como cadena
        ///                                f2 { f6 la fila, f9 el grupo,
        ///                                     f10 el efecto, f11 su valor }
        ///     f4  (repetido)  una conexión: f1 el origen, f2 { f1 el destino }
        ///   }
        /// </code>
        ///
        /// El f17 y el f4 conviven dentro del mismo f1: primero todas las salas y después todas
        /// las conexiones, que es el orden en que llegan.
        /// </remarks>
        public static byte[] BuildDreamMap(Dreams.Sueno sueno)
        {
            var dentro = Pb.New()
                .Var(1, 1)
                .Var(2, sueno.Dificultad)
                .Str(3, sueno.Nombre)
                .Var(4, 2)
                .Var(8, sueno.Nivel)
                .Var(13, 3)
                .Var(14, 1)
                .Var(15, 1)
                .Str(16, Texto(sueno.Dificultad));

            foreach (var sala in sueno.Salas)
            {
                var cuerpo = Pb.New();

                // La sala de entrada viaja casi vacía en la captura: sólo un f7.
                if (sala.Grupo == 0)
                {
                    cuerpo.Var(7, 0);
                    if (sala.Fila > 0) cuerpo.Var(6, sala.Fila);
                }
                else
                {
                    cuerpo.Var(1, 8)
                          .Var(3, 5)
                          .Var(6, sala.Fila)
                          .VarIfNotZero(9, sala.Grupo)
                          .VarIfNotZero(10, sala.Efecto)
                          .VarIfNotZero(11, sala.Valor);
                }

                dentro.Msg(17, Pb.New()
                    .Str(1, Texto(sala.Id))
                    .Msg(2, cuerpo));
            }

            foreach (var sala in sueno.Salas)
            {
                if (sala.Salidas.Count == 0) continue;

                var destinos = Pb.New();
                foreach (int destino in sala.Salidas) destinos.Str(1, Texto(destino));

                dentro.Msg(4, Pb.New()
                    .Str(1, Texto(sala.Id))
                    .Msg(2, destinos));
            }

            return Pb.New().Msg(1, dentro).Build();
        }

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

            var actual = sueno.SalaActual;
            if (actual != null)
            {
                foreach (int destino in actual.Salidas)
                {
                    izg.Msg(4, Pb.New()
                        .Str(1, Texto(destino))
                        .Var(2, PuertaDe(sueno.CharacterId, destino))
                        .Var(5, 3));
                }
            }

            izg.Var(7, 1)
               .VarIfNotZero(8, sueno.Puntos)
               .Str(13, Texto(sueno.Actual));

            return izg.Build();
        }

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

        /// <summary>El elemento interactivo de la puerta que lleva a una sala.</summary>
        private static int PuertaDe(long characterId, int sala)
            => Handlers.DreamHandler.ElementoDePuerta(characterId, sala);
    }
}
