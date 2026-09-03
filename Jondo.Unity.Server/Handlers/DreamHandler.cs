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
        /// El Plano Astral, que es a donde lleva el boton del menu.
        /// </summary>
        /// <remarks>
        /// Medido: el jru que sigue al iyc va al 238551040, que en nuestra propia base es la
        /// subarea 938, «Dominios de Draconiros». Es el vestibulo de los Suenos, no una sala.
        /// </remarks>
        public const long PlanoAstral = 238551040;

        // ═══════════════════════════════════════════════════════════════════
        //  El boton del menu
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// El boton de Suenos Infinitos del menu, y la tecla T (iyc).
        /// </summary>
        /// <remarks>
        /// No abre la ventana: TELETRANSPORTA al Plano Astral, y alli el pozo es el que la abre.
        /// Medido en la captura, donde al iyc le siguen un jru al plano y el iom de siempre.
        ///
        /// Se apunta de donde viene para poder devolverlo: si ya esta en el plano no se hace nada,
        /// que si no un segundo toque a la tecla se guardaria el plano como sitio de vuelta y el
        /// jugador se quedaria alli para siempre.
        /// </remarks>
        public static async Task ToAstralPlaneAsync(NetworkStream stream)
        {
            if (GameState.MapId == PlanoAstral)
            {
                Console.WriteLine("[Sueños] Ya está en el Plano Astral.");
                return;
            }

            Dreams.RecordarDeDondeViene(GameState.CharacterId, GameState.MapId, GameState.CellId);

            int aterriza = await TeleportHandler.ToMapAsync(stream, PlanoAstral, 0);
            Console.WriteLine($"[Sueños] {GameState.CharacterId} al Plano Astral, casilla {aterriza}.");
        }

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

            // Primero soltar el elemento. En la captura de Pesadilla II el orden es exacto:
            //
            //   C->S iwo  0887a20110e0f720
            //   S->C iwn  080110e0f72020b80128a28280c8e708
            //   S->C iyj  (618 B)
            //
            // Y el orden importa: sin el iwn el cliente sigue teniendo el pozo por ocupado y no
            // abre la ventana que le llega detrás. No da ningún error; simplemente no pasa nada,
            // que es lo que se vio al pulsarlo. El f4 de ese iwn es 184, la misma habilidad que
            // ya se anuncia en el f11.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    Dreams.ElementoDelPozo, Dreams.HabilidadDelPozo, yo)));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iyj, DreamProtocol.BuildDreamMap(sueno)));

            Console.WriteLine($"[Sueños] Mapa ofrecido a {yo}: {sueno.Salas.Count} salas.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Empezar y descartar
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Empezar un sueño (ixf f1) o entrar en el que ya hay (ixf f2).</summary>
        /// <remarks>
        /// El f2 se leyó mal durante un tiempo: se tomó por «descartar» porque las capturas donde
        /// aparece se llaman «descartar el sueño en curso». Los bytes dicen otra cosa. En
        /// «continuar sueño infinito» y en «Sueño II-descartar», el mismo «12020801» va seguido de
        /// un izg y de un jru A UNA SALA: el jugador ENTRA.
        ///
        ///   C->S ixf  12020801
        ///   S->C izg  (1150 B)
        ///   S->C jru  108080b071      -> 237764608, la sala en la que estaba
        ///   S->C ixa  (raíz 3, vacío)
        ///
        /// Y descartar no tiene mensaje propio: en «Sueño III-descartar» y «paradoja I-descartar»
        /// el cliente manda directamente el f1 con la dificultad nueva. La ventana de «ya tienes
        /// un sueño en curso» se resuelve en el cliente; al servidor sólo le llega el comienzo.
        /// </remarks>
        public static async Task StartOrDiscardAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ixf = ConnectionProtocol.ReadPayload(payload, Op.Ixf);
            if (ixf == null) return;

            int dificultad = 0;
            bool continuar = false;

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
                    continuar = true;
                }
            }

            long yo = GameState.CharacterId;

            if (continuar)
            {
                var enCurso = Dreams.De(yo);
                if (enCurso == null)
                {
                    Console.WriteLine($"[Sueños] {yo} quiere continuar y no tiene sueño en curso.");
                    return;
                }

                Console.WriteLine($"[Sueños] {yo} continúa en la sala {enCurso.Actual}.");

                await EntrarEnSalaAsync(stream, enCurso, enCurso.Actual);

                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Answer(Op.Ixa, null, ConnectionProtocol.RequestId(payload)));
                return;
            }

            if (dificultad <= 0 || dificultad > Dreams.MaximaDificultad)
            {
                Console.WriteLine($"[Sueños] Dificultad {dificultad} fuera de la escalera de 1 a " +
                                  $"{Dreams.MaximaDificultad}. ixf: " +
                                  Convert.ToHexString(ixf).ToLowerInvariant());
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

            // Y no se sale de una sala sin haberla limpiado. La guía lo dice de la única manera
            // que importa: «es absolutamente imposible volver atrás» una vez entras, y se avanza
            // sala a sala peleando. Con las puertas abiertas desde el principio se podía recorrer
            // el sueño entero sin dar un golpe, cobrando los puntos de todas las salas.
            //
            // La entrada y la última no tienen grupo, así que no bloquean.
            if (!SalaSuperada(actual))
            {
                Console.WriteLine($"[Sueños] La sala {actual.Id} todavía tiene su grupo en pie: " +
                                  "no se abre la puerta.");
                return false;
            }

            // Las puertas son los elementos del propio mapa de la sala, en su orden: la primera
            // lleva a la primera salida, la segunda a la segunda. Los mapas de la subárea 904
            // traen tres, que es también el máximo de salidas que se ha medido en una sala.
            for (int cual = 0; cual < actual.Salidas.Count; cual++)
            {
                if (Dreams.PuertaDe(actual, cual) != elementId) continue;

                // Soltar la puerta ANTES del izg y del jru. Medido en la captura de Sueño III:
                //
                //   C->S iwo  08d0a59f0310f7f620          el elemento 539511
                //   S->C iwn  080110f7f62020b80128…       con la habilidad 184
                //   S->C izg  (833 B)
                //   S->C jru  108090b071
                //
                // Es el mismo orden que el del pozo, y saltárselo tiene el mismo precio: el
                // cliente se queda con la puerta por ocupada y no pasa nada de lo que venga
                // detrás. Sin un solo error.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                        elementId, Dreams.HabilidadDelPozo, GameState.CharacterId)));

                await EntrarEnSalaAsync(stream, sueno, actual.Salidas[cual]);
                return true;
            }

            return false;
        }

        /// <summary>¿Se puede salir ya de esta sala?</summary>
        /// <remarks>
        /// Una sala sin grupo —la entrada y la última— se cruza sin más. Una de pelea hace falta
        /// haberla ganado: <see cref="Dreams.Sala.Hecha"/> lo pone el final del combate.
        ///
        /// Se mira también si el grupo sigue plantado, y no sólo la marca, porque son dos cosas
        /// distintas: la marca dice que se ganó, y el grupo en pie dice que sigue ahí. Con sólo
        /// una de las dos, un sueño continuado tras reconectar dejaría pasar sin pelear.
        /// </remarks>
        private static bool SalaSuperada(Dreams.Sala sala)
        {
            if (sala.Miembros.Count == 0) return true;
            if (sala.Hecha) return true;
            return sala.Plantado == 0;
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

            // Y el cambio de mapa: al mapa DE LA SALA, que es uno de los 484 de la subárea 904
            // hechos para esto, no al del grupo de monstruos. Mandarle al del grupo es lo que le
            // dejaba de pie en Frigost, andando por el mundo y sin minimapa.
            long mapa = sala.MapaDeLaSala;
            if (mapa == 0)
            {
                Console.WriteLine($"[Sueños] La sala {salaId} se ha quedado sin mapa propio.");
                return;
            }

            // El grupo se planta ANTES del cambio de mapa: el jss que el cliente pide justo
            // después es el que lleva los actores, y un grupo plantado un instante tarde no
            // aparece hasta que se vuelve a entrar.
            if (sala.EsFavor) PlantarElFavor(sala); else PlantarElGrupo(sala);

            int aterriza = await TeleportHandler.ToMapAsync(stream, mapa, 0);

            Console.WriteLine($"[Sueños] Sala {salaId} (fila {sala.Fila}): mapa {mapa}, " +
                              $"grupo {sala.Grupo} del mapa {sala.MapaId} con " +
                              $"{sala.Miembros.Count} monstruo(s), efecto {sala.Efecto} de " +
                              $"{sala.Valor}. Aterriza en {aterriza}.");
        }

        /// <summary>
        /// Pone en la sala al Rey Gob, que es la tienda.
        /// </summary>
        /// <remarks>
        /// No hace falta protocolo nuevo: la fuente de los Sueños es un NPC y punto. Se coloca
        /// como cualquier otro y el motor de diálogos hace el resto; lo que ofrece va escrito en
        /// su respuesta, con el porcentaje de puntos que da.
        /// </remarks>
        private static void PlantarElFavor(Dreams.Sala sala)
        {
            if (sala.MapaDeLaSala == 0) return;

            Managers.Npcs.PonerDelSueno(sala.MapaDeLaSala, Dreams.ReyGob,
                                        Dreams.CasillaDelReyGob, Dreams.OrientacionDelReyGob);
        }

        /// <summary>
        /// Pone en la sala los monstruos que le tocan, si no están ya.
        /// </summary>
        /// <remarks>
        /// Sin esto la sala está vacía y no hay nada que atacar: el cliente pide la pelea con un
        /// hqa que lleva el id contextual de un grupo del mapa, así que si no hay grupo no hay
        /// manera de empezar. En la captura de Sueño III se ve el hqa con ese negativo justo antes
        /// del kub, y hasta ahí llega la sala sin dar ningún error: simplemente no se puede pelear.
        ///
        /// La entrada y la última no llevan grupo, que es lo que dicen las nueve capturas.
        /// </remarks>
        private static void PlantarElGrupo(Dreams.Sala sala)
        {
            if (sala.Miembros.Count == 0 || sala.MapaDeLaSala == 0) return;

            // Ya plantado: se vuelve a entrar en la misma sala al continuar un sueño.
            if (sala.Plantado != 0
                && MobSpawnManager.GetMobGroupById(sala.Plantado) != null) return;

            var grupo = MobSpawnManager.SpawnComposed(sala.MapaDeLaSala, sala.Miembros);
            if (grupo == null)
            {
                Console.WriteLine($"[Sueños] La sala {sala.Id} no ha podido plantar su grupo.");
                return;
            }

            sala.Plantado = grupo.MobId;
        }

        /// <summary>
        /// Se ha ganado la pelea de una sala: la marca hecha y suma sus puntos.
        /// </summary>
        /// <remarks>
        /// Devuelve verdadero si el grupo derrotado era el de una sala, que es lo que le dice al
        /// motor de combate que NO reponga otro en su sitio.
        ///
        /// Los puntos son el f1 de la sala en el iyj —lo que la ventana enseña debajo de cada
        /// una—, medido entre 4 y 40 en las 89 salas de las nueve capturas. Sumarlos aquí es lo
        /// que hace que la moneda del sueño exista: sin esto la ventana promete cinco puntos por
        /// sala y limpiarla no da ninguno.
        /// </remarks>
        public static bool SalaLimpiada(long grupoDerrotado)
        {
            if (grupoDerrotado == 0) return false;

            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return false;

            foreach (var sala in sueno.Salas)
            {
                if (sala.Plantado != grupoDerrotado) continue;

                sala.Plantado = 0;
                if (sala.Hecha) return true;

                sala.Hecha = true;
                sueno.Puntos += sala.Puntos;

                Console.WriteLine($"[Sueños] Sala {sala.Id} limpiada: +{sala.Puntos} puntos, " +
                                  $"{sueno.Puntos} en total.");
                return true;
            }

            return false;
        }

        /// <summary>Vuelve a mandar el estado del sueño, si es que hay uno.</summary>
        public static async Task RefrescarEstadoAsync(NetworkStream stream)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, DreamProtocol.BuildDreamState(sueno)));
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

            // A donde estaba ANTES DE PULSAR EL BOTON, no al mapa desde el que empezo el sueno:
            // a esas alturas ese mapa ya es el propio Plano Astral, y devolverlo alli lo dejaria
            // dando vueltas por el vestibulo.
            var (mapa, casilla) = Dreams.DeDondeViene(sueno.CharacterId);
            if (mapa == 0) { mapa = sueno.MapaDeVuelta; casilla = sueno.CasillaDeVuelta; }

            if (mapa != 0 && mapa != PlanoAstral)
            {
                await TeleportHandler.ToMapAsync(stream, mapa, casilla);
            }
            else
            {
                // Sin sitio conocido, al plano: es de donde se entro y siempre existe.
                await TeleportHandler.ToMapAsync(stream, PlanoAstral, 0);
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
