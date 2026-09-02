using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Los Sueños Infinitos: abrir la ventana, empezar, moverse de sala y salir.
    /// </summary>
    /// <remarks>
    /// El ciclo entero, medido sobre las trece capturas de <c>Sueños Infinitos/</c>:
    ///
    /// <code>
    ///   C-&gt;S  iwo          usar el pozo, que es un interactivo corriente
    ///   S-&gt;C  iyj          el mapa del sueño: once salas y el grafo que las une
    ///   C-&gt;S  ixf { f1 }   empezar en la dificultad que lleva dentro
    ///   S-&gt;C  izg + jru    el estado, y el cambio de mapa a la primera sala
    ///   S-&gt;C  ixa          el acuse, vacío y por la raíz 3
    ///   C-&gt;S  iwo          elegir puerta
    ///   S-&gt;C  izg + jru    estado nuevo, y a la sala siguiente
    ///   C-&gt;S  iyx          salir
    ///   S-&gt;C  jru + ixg + iom  y el iyb «0801» por la raíz 3
    /// </code>
    ///
    /// Lo bueno de esto es cuánto se apoya en lo que ya hay: el pozo y cada puerta son
    /// <c>iwo</c>, el interactivo de toda la vida; las salas se pueblan con filas de
    /// <see cref="Dreams"/> sacadas de MapMobs; y la modificación de cada sala es un efecto del
    /// mismo catálogo que mueve el motor de hechizos.
    /// </remarks>
    public static class DreamHandler
    {
        /// <summary>
        /// El elemento interactivo de la puerta que lleva a una sala.
        /// </summary>
        /// <remarks>
        /// Es el ELEMENTO, no la habilidad, y conviene no confundirlos porque el iwo lleva los dos:
        /// su f1 es la habilidad y su f2 el elemento. En la captura la puerta a la sala «1» sale
        /// listada en el izg con el 539509, y el iwo que la pulsa trae ese mismo 539509 en su f2 y
        /// un 6.809.520 en el f1. O sea que lo que identifica la puerta es el f2.
        ///
        /// Aquí se deriva del personaje y de la sala: lo único que hace falta es que sea estable
        /// mientras dure el sueño y distinto entre puertas.
        /// </remarks>
        public static int ElementoDePuerta(long characterId, int sala)
            => 539500 + (int)((characterId % 1000) * 100) + sala;

        // ═══════════════════════════════════════════════════════════════════
        //  Abrir la ventana
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Enseña el mapa del sueño. Es lo que contesta al usar el pozo.</summary>
        public static async Task ShowAsync(NetworkStream stream)
        {
            var yo = GameState.CharacterId;
            var sueno = Dreams.De(yo);

            // Sin sueño en curso se enseña uno nuevo, que es lo que hace la ventana: ofrece.
            sueno ??= Dreams.Crear(yo, GameState.CharacterName, GameState.CharacterLevel, 1,
                                   GameState.MapId, GameState.CellId);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iyj, DreamProtocol.BuildDreamMap(sueno)));

            Console.WriteLine($"[Sueños] Mapa ofrecido a {yo}: {sueno.Salas.Count} salas.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Empezar y descartar
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Empezar el sueño (ixf f1) o descartar el que hubiera (ixf f2).</summary>
        public static async Task StartOrDiscardAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ixf = ConnectionProtocol.ReadPayload(payload, Op.Ixf);
            if (ixf == null) return;

            int dificultad = 0;
            bool descartar = false;

            foreach (var field in ProtoMessage.Parse(ixf).Fields)
            {
                if (field.WireType != 2 || field.BytesValue == null) continue;

                if (field.FieldNumber == 1)
                {
                    // Empezar: la dificultad va en el f3 de dentro.
                    foreach (var dentro in ProtoMessage.Parse(field.BytesValue).Fields)
                    {
                        if (dentro.FieldNumber == 3 && dentro.WireType == 0)
                        {
                            dificultad = (int)dentro.VarIntValue;
                        }
                    }
                }
                else if (field.FieldNumber == 2)
                {
                    descartar = true;
                }
            }

            long yo = GameState.CharacterId;

            if (descartar)
            {
                Dreams.Olvidar(yo);
                Console.WriteLine($"[Sueños] {yo} descarta el sueño en curso.");
                return;
            }

            if (dificultad <= 0 || dificultad > Dreams.MaximaDificultad)
            {
                Console.WriteLine($"[Sueños] Dificultad {dificultad} fuera de la escalera de 1 a " +
                                  $"{Dreams.MaximaDificultad}.");
                return;
            }

            var sueno = Dreams.Crear(yo, GameState.CharacterName, GameState.CharacterLevel,
                                     dificultad, GameState.MapId, GameState.CellId);

            Console.WriteLine($"[Sueños] {yo} empieza en dificultad {dificultad}: " +
                              $"{sueno.Salas.Count} salas.");

            await EntrarEnSalaAsync(stream, sueno, 0);

            // El acuse va por la raíz 3, vacío, con el id de la petición.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Ixa, null, ConnectionProtocol.RequestId(payload)));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Moverse
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Una puerta del sueño, pulsada. Devuelve falso si esa habilidad no es de ninguna puerta.
        /// </summary>
        /// <remarks>
        /// Se llama desde el manejador de interactivos, antes de que trate el iwo como lo que trata
        /// siempre: dentro de un sueño las puertas son interactivos que no existen en el mapa de
        /// rol, así que el camino normal no sabría qué hacer con ellas.
        /// </remarks>
        public static async Task<bool> TryDoorAsync(NetworkStream stream, int elementId)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return false;

            var actual = sueno.SalaActual;
            if (actual == null) return false;

            foreach (int destino in actual.Salidas)
            {
                if (ElementoDePuerta(sueno.CharacterId, destino) != elementId) continue;

                await EntrarEnSalaAsync(stream, sueno, destino);
                return true;
            }

            return false;
        }

        /// <summary>Mete al jugador en una sala: el estado y el cambio de mapa.</summary>
        private static async Task EntrarEnSalaAsync(NetworkStream stream, Dreams.Sueno sueno,
                                                    int salaId)
        {
            var sala = sueno.Buscar(salaId);
            if (sala == null) return;

            sueno.Actual = salaId;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, DreamProtocol.BuildDreamState(sueno)));

            // Y el cambio de mapa. La sala de entrada no tiene grupo ni mapa propio, así que se
            // usa el de la primera que sí lo tenga: el cliente necesita un mapa al que ir.
            long mapa = sala.MapaId != 0 ? sala.MapaId : PrimerMapa(sueno);
            if (mapa != 0)
            {
                int aterriza = await TeleportHandler.ToMapAsync(stream, mapa,
                    sala.Casilla > 0 ? sala.Casilla : 0);

                Console.WriteLine($"[Sueños] Sala {salaId} (fila {sala.Fila}): mapa {mapa}, " +
                                  $"grupo {sala.Grupo}, efecto {sala.Efecto} de {sala.Valor}. " +
                                  $"Aterriza en {aterriza}.");
            }
        }

        private static long PrimerMapa(Dreams.Sueno sueno)
        {
            foreach (var s in sueno.Salas) if (s.MapaId != 0) return s.MapaId;
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  La tormenta y la salida
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>La tormenta astral (izh): te mueve de sala sin pelear.</summary>
        /// <remarks>
        /// Medido: el cliente lo manda vacío y vuelven un izg, un jru y un izj «1001». Lo que la
        /// tormenta HACE no está medido —en la captura el jugador la usa y acaba en otro sitio—,
        /// así que aquí lleva a la primera salida de la sala en la que esté, que es lo que
        /// reproduce lo observable sin inventar reglas.
        /// </remarks>
        public static async Task AstralStormAsync(NetworkStream stream)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return;

            var actual = sueno.SalaActual;
            if (actual != null && actual.Salidas.Count > 0)
            {
                await EntrarEnSalaAsync(stream, sueno, actual.Salidas[0]);
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izj, DreamProtocol.BuildStorm()));

            Console.WriteLine($"[Sueños] Tormenta astral de {sueno.CharacterId}.");
        }

        /// <summary>Salir del sueño (iyx) y volver a donde se estaba.</summary>
        public static async Task LeaveAsync(NetworkStream stream, byte[] payload)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return;

            if (sueno.MapaDeVuelta != 0)
            {
                await TeleportHandler.ToMapAsync(stream, sueno.MapaDeVuelta, sueno.CasillaDeVuelta);
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ixg));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iom));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Iyb, DreamProtocol.BuildLeft(),
                                          ConnectionProtocol.RequestId(payload)));

            Console.WriteLine($"[Sueños] {sueno.CharacterId} sale del sueño en la sala " +
                              $"{sueno.Actual} con {sueno.Puntos} punto(s).");

            Dreams.Olvidar(sueno.CharacterId);
        }
    }
}
