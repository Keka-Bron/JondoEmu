using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

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

            // The dream the character has going, to continue; with none, an empty iyj, and the
            // window only offers a new one -- the long capture, whose player has none, gets zero
            // bytes. A new dream was created here and shown as one to continue, with the header
            // of a capture's: 5 points and 1 MP that were nobody's.
            var sueno = Dreams.De(yo);

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

            Console.WriteLine(sueno == null
                ? $"[Sueños] Well of {yo}: no dream to continue."
                : $"[Sueños] Well of {yo}: dream of difficulty {sueno.Dificultad} in room {sueno.Actual}, " +
                  $"{sueno.DreamPoints} dream points.");
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
                                     dificultad, GameState.MapId, GameState.CellId, GameState.Breed);
            Persist(sueno);

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
            // A dream's doors and fountain are on its own maps: out in the world, a dream left
            // to be continued has nothing to say about what the player clicks.
            if (!Dreams.IsDreamMap(GameState.MapId)) return false;
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return false;

            var actual = sueno.SalaActual;
            if (actual == null) return false;

            // The Fontaine onirique of a fountain room: the shop, not a way out.
            if (actual.EsFuente && elementId == Dreams.FountainOf(actual))
            {
                await ShopAsync(stream, sueno, elementId);
                return true;
            }

            // Y no se sale de una sala sin haberla limpiado. La guía lo dice de la única manera
            // que importa: «es absolutamente imposible volver atrás» una vez entras, y se avanza
            // sala a sala peleando. Con las puertas abiertas desde el principio se podía recorrer
            // el sueño entero sin dar un golpe, cobrando los puntos de todas las salas.
            //
            // La entrada y la fuente no tienen grupo, así que no bloquean; a favour does until
            // one of its three is chosen.
            if (!Dreams.CanLeave(actual))
            {
                Console.WriteLine(actual.EsFavor
                    ? $"[Sueños] The favour of room {actual.Id} is not chosen yet: the door stays shut."
                    : $"[Sueños] La sala {actual.Id} todavía tiene su grupo en pie: no se abre la puerta.");
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

        /// <summary>Mete al jugador en una sala: el estado y el cambio de mapa.</summary>
        private static async Task EntrarEnSalaAsync(NetworkStream stream, Dreams.Sueno sueno,
                                                    int salaId)
        {
            int pointsBefore = sueno.DreamPoints;
            var sala = Dreams.Enter(sueno, salaId, out var gained);
            if (sala == null) return;
            _enElSueno[GameState.CharacterId] = true;

            // El potenciador y los puntos se cobran AL ENTRAR, antes de pelear, y una sola vez por sala.
            if (gained != null || sueno.DreamPoints != pointsBefore)
            {
                Console.WriteLine($"[Sueños] Sala {sala.Id}: +{sueno.DreamPoints - pointsBefore} dream points, " +
                                  $"{sueno.DreamPoints} in all" +
                                  (gained != null ? $"; bonus {gained.Efecto} of {gained.Valor}, " +
                                                    $"{sueno.Ganados.Count} so far." : "."));
            }

            // Pisar la Fuente abre la franja siguiente, porque la fuente es a la vez la última
            // sala de ésta y la primera de la que viene: «Chaque palier commencera toujours par
            // une Fontaine Onirique». Si no se añade aquí, el jugador entra en una sala sin
            // salidas y se queda encerrado, que es lo que pasaba.
            if (Dreams.Closes(sala) && sala.Salidas.Count == 0)
            {
                Dreams.AnadirFranja(sueno);
                Console.WriteLine($"[Sueños] Franja {sueno.Franja} abierta: " +
                                  $"{sueno.Salas.Count} salas en total.");
            }

            Persist(sueno);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));

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
            if (sala.EsFuente) { if (sala.HasReyGob) PlantarLaTienda(sala); }
            else if (sala.EsFavor) PlantFavorNpc(sala);
            else PlantarElGrupo(sala);

            int aterriza = await TeleportHandler.ToMapAsync(stream, mapa, 0);

            Console.WriteLine($"[Sueños] Sala {salaId} (fila {sala.Fila}): mapa {mapa}, " +
                              $"grupo {sala.Grupo} del mapa {sala.MapaId} con " +
                              $"{sala.Miembros.Count} monstruo(s), efecto {sala.Efecto} de " +
                              $"{sala.Valor}. Aterriza en {aterriza}.");
        }

        /// <summary>
        /// Pone al vendedor en la Fuente Onírica.
        /// </summary>
        /// <remarks>
        /// No hace falta protocolo nuevo: la fuente de los Sueños es un NPC y punto. Se coloca
        /// como cualquier otro y el motor de diálogos hace el resto; lo que ofrece va escrito en
        /// su respuesta, con el porcentaje de puntos que da.
        /// </remarks>
        private static void PlantarLaTienda(Dreams.Sala sala)
        {
            if (sala.MapaDeLaSala == 0) return;

            Managers.Npcs.PonerDelSueno(sala.MapaDeLaSala, Dreams.ReyGob,
                                        Dreams.CasillaDelReyGob, Dreams.OrientacionDelReyGob);
        }

        /// <summary>
        /// The Dispensador de favores in a dream favour: an NPC like the Rey Gob, placed for the
        /// room and talked to as any other. His cell is walkable-checked: it is INFERRED (see
        /// <see cref="Dreams.FavorNpc"/>).
        /// </summary>
        private static void PlantFavorNpc(Dreams.Sala sala)
        {
            if (sala.MapaDeLaSala == 0) return;
            int cell = MapManager.IsCellWalkable(sala.MapaDeLaSala, Dreams.FavorNpcCell)
                ? Dreams.FavorNpcCell
                : MapManager.GetNearestWalkableCell(sala.MapaDeLaSala, Dreams.FavorNpcCell);
            Managers.Npcs.PonerDelSueno(sala.MapaDeLaSala, Dreams.FavorNpc, cell, Dreams.FavorNpcOrientation);
        }

        /// <summary>
        /// "Acepto el favor.": the three choices of the favour one stands in, in the izg's f6,
        /// and the ixm that opens the client's shop window on them -- in favour mode, which the
        /// client picks itself from the room's kind. See <see cref="Dreams.Buy"/>.
        /// </summary>
        /// <remarks>
        /// The ixm is the fountain's (read off the client: its handlers are the only ones that
        /// raise the event the shop window's Setup listens to). That the favour is opened the same
        /// way is INFERRED from the window: InfiniteDreamShopUi.Setup is where "isFavor" and the
        /// "ui.infiniteDreams.dreamFavor" title are decided, and it has no other way in.
        /// </remarks>
        internal static async Task<bool> OfferFavorAsync(NetworkStream stream)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || !sala.EsFavor || sala.FavorChosen)
            {
                Console.WriteLine($"[Sueños] No favour to offer to {GameState.CharacterId} here.");
                return false;
            }

            sala.Offers ??= Dreams.DrawFavor();
            Persist(sueno);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ixm));
            Console.WriteLine($"[Sueños] Favour of room {sala.Id}: " +
                              string.Join(", ", sala.Offers.Select(o => o.Tag)) + ".");
            return true;
        }

        /// <summary>Whether the favour of the room one stands in is still to be chosen: what the Dispensador says.</summary>
        internal static bool FavorPending()
        {
            var sala = Dreams.De(GameState.CharacterId)?.SalaActual;
            return sala != null && sala.EsFavor && !sala.FavorChosen;
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
        /// Se ha ganado la pelea de una sala: la marca hecha.
        /// </summary>
        /// <remarks>
        /// Devuelve verdadero si el grupo derrotado era el de una sala, que es lo que le dice al
        /// motor de combate que NO reponga otro en su sitio.
        ///
        /// Winning pays nothing: the room paid its dream points when it was entered. In the long
        /// capture the izg that follows a win carries the same f11 as the one of the entrance,
        /// and only the f18 and f19 change. This used to add the room's score to the f8, which is
        /// the bonus to experience and loot: 220% became 275% in three rooms.
        /// </remarks>
        public static bool SalaLimpiada(long grupoDerrotado, long mapa)
        {
            if (grupoDerrotado == 0 || !Dreams.IsDreamMap(mapa)) return false;

            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return false;

            foreach (var sala in sueno.Salas)
            {
                if (sala.Plantado != grupoDerrotado) continue;

                sala.Plantado = 0;
                if (sala.Hecha) return true;

                sala.Hecha = true;
                Persist(sueno);

                Console.WriteLine($"[Sueños] Sala {sala.Id} limpiada; {sueno.DreamPoints} dream points.");
                return true;
            }

            return false;
        }

        /// <summary>Vuelve a mandar el estado del sueño, si es que hay uno y se está en él.</summary>
        /// <remarks>
        /// Only on one of the dream's maps. The izg is what turns the client's dream interface on
        /// -- the panel, and the band and depth in place of the map's name and coordinates -- and
        /// a dream left behind is still there to be continued: sent after any fight, it put the
        /// dream's interface over a dungeon's exit.
        /// </remarks>
        public static async Task RefrescarEstadoAsync(NetworkStream stream)
        {
            if (!Dreams.IsDreamMap(GameState.MapId)) return;
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));
        }

        /// <summary>The izg of a dream, with the bestiary of the room one stands in.</summary>
        private static byte[] StateOf(Dreams.Sueno sueno)
            => DreamProtocol.BuildDreamState(sueno, BestiaryOf(sueno.SalaActual,
                   sueno.SalaActual?.EsFinal == true ? Dreams.FinalRulesOf(sueno.Dificultad).BaseLevel : 0));

        // ═══════════════════════════════════════════════════════════════════
        //  The bestiary
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// The monsters of a room as the fight will have them: each built by the fight's own
        /// <see cref="FightHandler.BuildMonsterFighter"/>, on the cell the fight will place it on.
        /// Empty when the room has no fight left to win.
        /// </summary>
        /// <remarks>
        /// What the real server lists are its dream's monsters, scaled to the dream's level --
        /// monster 209, 580 life points at its fifth grade, has 5,510 in the bestiary. The monsters
        /// here are the world group the room plants, at their own grades, and the bestiary shows
        /// them as they are: showing scaled figures for unscaled monsters would be a lie. The
        /// Fin du rêve's first wave is the exception, because its fight does scale it: there the
        /// bestiary gets its level too.
        /// </remarks>
        internal static List<DreamProtocol.Beast> BestiaryOf(Dreams.Sala? sala, int level = 0)
        {
            var beasts = new List<DreamProtocol.Beast>();
            if (sala == null || sala.Miembros.Count == 0 || sala.Hecha) return beasts;

            var group = MobSpawnManager.ComposeOffMap(sala.Miembros);
            if (group == null) return beasts;

            var cells = FightHandler.DefenderPlacement(sala.MapaDeLaSala);
            for (int i = 0; i < group.Members.Count; i++)
            {
                var member = group.Members[i];
                int cell = i < cells.Count ? cells[i] : cells.FirstOrDefault();
                var fighter = FightHandler.BuildMonsterFighter(member, -(i + 1), cell);
                if (level > 0) Dreams.ScaleTo(fighter, level);
                beasts.Add(new DreamProtocol.Beast(cell, fighter.MonsterId, fighter.Level,
                                                   Dreams.IsBoss(fighter.MonsterId), StatsOf(fighter)));
            }
            return beasts;
        }

        /// <summary>
        /// A monster's characteristics as the bestiary lists them, in the order of the captures:
        /// life, AP, the five resistances that are not zero, initiative, evasion and lock, MP when
        /// it has some, and the two dodges. Evasion and lock are what the fight gives a monster --
        /// nothing, today -- and not a figure of their own.
        /// </summary>
        internal static List<(int Characteristic, int Value)> StatsOf(Fighter monster)
        {
            var stats = new List<(int, int)> { (LifePoints, monster.MaxHP), (ActionPoints, monster.MaxAP) };
            foreach (var (id, value) in new[]
                     {
                         (EarthResistance, monster.EarthResPct), (FireResistance, monster.FireResPct),
                         (WaterResistance, monster.WaterResPct), (AirResistance, monster.AirResPct),
                         (NeutralResistance, monster.NeutralResPct),
                     })
            {
                if (value != 0) stats.Add((id, value));
            }
            stats.Add((Initiative, monster.Initiative));
            stats.Add((Evasion, monster.Otras.GetValueOrDefault(Evasion)));
            stats.Add((Lock, monster.Otras.GetValueOrDefault(Lock)));
            if (monster.MaxMP != 0) stats.Add((MovementPoints, monster.MaxMP));
            stats.Add((EffectEngine.EsquivaPA, monster.Otras.GetValueOrDefault(EffectEngine.EsquivaPA)));
            stats.Add((EffectEngine.EsquivaPM, monster.Otras.GetValueOrDefault(EffectEngine.EsquivaPM)));
            return stats;
        }

        // The characteristics of datos/characteristics.json the bestiary lists.
        private const int LifePoints = 0;
        private const int ActionPoints = 1;
        private const int MovementPoints = 23;
        private const int EarthResistance = 33;
        private const int FireResistance = 34;
        private const int WaterResistance = 35;
        private const int AirResistance = 36;
        private const int NeutralResistance = 37;
        private const int Initiative = 44;
        private const int Evasion = 78;
        private const int Lock = 79;

        // ═══════════════════════════════════════════════════════════════════
        //  The fountain's shop
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// The Fontaine onirique used: its element let go (iwn, skill 355), the state again with
        /// the shop's offers in its f6, and ixm, which opens the shop's window.
        /// </summary>
        /// <remarks>
        /// ixm is read off the client, not a capture -- the one capture at a fountain never
        /// touches it. Its handlers raise the client event bxv, and the three methods of the
        /// dream's window manager that take bxv are the three that call InfiniteDreamShopUi.Setup.
        /// Nothing else raises bxv. Without it, the fountain was clicked and nothing opened.
        /// </remarks>
        private static async Task ShopAsync(NetworkStream stream, Dreams.Sueno sueno, int elementId)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, Dreams.FountainSkill, GameState.CharacterId)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ixm));
            Console.WriteLine($"[Sueños] The fountain of room {sueno.Actual}: " +
                              $"{sueno.SalaActual?.Offers?.Count ?? 0} offer(s), {sueno.DreamPoints} dream points.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  The loot table and the positions
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>ixq: the loot table of the room one stands in, answered by izo.</summary>
        public static async Task DropTableAsync(NetworkStream stream, byte[] payload)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            var drops = DropsOf(sueno?.SalaActual, sueno?.Bonus ?? 100);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Izo, DreamProtocol.BuildDropTable(drops),
                                          ConnectionProtocol.RequestId(payload)));
            Console.WriteLine($"[Sueños] Loot table of room {sueno?.Actual}: {drops.Count} line(s).");
        }

        /// <summary>
        /// What the room's fight can drop, as the fight rolls it: the Jondo coin of every monster,
        /// always and added up, then the dream's own loot table at the room's loot bonus -- the
        /// reflections, the astral runes, the legends -- with its criteria.
        /// </summary>
        /// <remarks>
        /// The table is the real server's, the 61 lines of the izo of the captures (see
        /// <see cref="DreamData.Loot"/>); its percents are the capture's at the bonus it was
        /// read at, the reflections x17 at 168 as in the invitation capture. The monsters' own
        /// world loot is not in it: a dream's fight pays the dream's, as the jyg of the invitation
        /// capture does -- reflections and a rune, no kamas, nothing of the monsters'.
        /// </remarks>
        internal static List<(string Criterion, int Item, int Quantity, double Percent)> DropsOf(Dreams.Sala? sala, int lootBonus = 100)
        {
            var lines = new List<(string Criterion, int Item, int Quantity, double Percent)>();
            if (sala == null || sala.Miembros.Count == 0) return lines;

            var group = MobSpawnManager.ComposeOffMap(sala.Miembros);
            if (group == null) return lines;

            int coins = group.Members.Sum(member => JondoCoin.RewardFor(member.Level));
            if (coins > 0) lines.Add(("", JondoCoin.TemplateId, coins, 100.0));
            lines.AddRange(Dreams.LootTableAt(lootBonus));
            return lines;
        }

        /// <summary>
        /// kaz: where a fight on this map would place everybody, answered by jxj -- the cells a
        /// fight here is placed from, computed as the fight computes them.
        /// </summary>
        public static async Task PositionsAsync(NetworkStream stream, byte[] payload)
        {
            long map = GameState.MapId;
            long arena = MapManager.ResolveArenaMapId(map);
            var (attackers, defenders) = FightHandler.Placement(map);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Jxj, DreamProtocol.BuildPositions(arena, map, attackers, defenders),
                                          ConnectionProtocol.RequestId(payload)));
            Console.WriteLine($"[Sueños] Positions of map {map}: {attackers.Count} and {defenders.Count} cells.");
        }

        /// <summary>
        /// iym: buying at the fountain. Its price off the dream points, its bonuses gained, and the
        /// izg again with what the shop has left.
        /// </summary>
        /// <remarks>
        /// Read off the client: the shop's reward line, clicked, raises the command dbo, and the
        /// client's dream sender turns dbo into iym. Measured in our own game since: clicking
        /// "Psst Psst" sends iym { f1: 149 }, the f10 of that offer. The answer to it is not known:
        /// the izg that follows is what tells the client.
        /// </remarks>
        public static async Task BuyAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iym = ConnectionProtocol.ReadPayload(payload, Op.Iym);
            if (iym == null) return;
            Console.WriteLine($"[Sueños] iym (buy, inferred): {Convert.ToHexString(iym).ToLowerInvariant()}");

            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null)
            {
                Console.WriteLine("[Sueños] iym with no dream going.");
                return;
            }

            int which = 0;
            foreach (var f in ProtoMessage.Parse(iym).Fields)
                if (f.FieldNumber == 1 && f.WireType == 0) which = (int)f.VarIntValue;

            var bought = Dreams.Buy(sueno, which, out string refusal);
            if (bought == null)
                Console.WriteLine($"[Sueños] Nothing bought with {which}: {refusal}.");
            else
                Console.WriteLine($"[Sueños] Bought reward {bought.Id} for {bought.Price}: " +
                                  $"{string.Join(", ", bought.Bonuses.Select(b => $"{b.Efecto} of {b.Valor}"))}; " +
                                  $"{sueno.DreamPoints} dream points left.");

            if (bought != null) Persist(sueno);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  La tormenta y la salida
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>La tormenta astral (izh): another group for the room, on another map.</summary>
        /// <remarks>
        /// Medido: el cliente lo manda vacío y vuelven un izg, un jru y un izj «1001». And what it
        /// does is measured too, in the Paradoja II capture that uses two: the room stays "1", its
        /// bestiary changes, the jru goes to another map, and f7 -- the storms left -- goes from 2
        /// to 1 to nothing. The guide says the same: "changer un groupe de monstres d'une salle".
        /// It used to take the player to the room's first exit, fight or no fight.
        ///
        /// Not in a fight: pressed there, the map changed under a fight still going on. Not with
        /// none left, and not where there is no group to change -- renewing a fountain's offers
        /// is its other use, and there are only the five measured ones to offer.
        /// </remarks>
        public static async Task AstralStormAsync(NetworkStream stream)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return;

            var actual = sueno.SalaActual;

            // At a favour not chosen yet the storm draws its two bonuses again: the client's own
            // text for it, "reiniciar ... así como el favor", and the favour window's reroll
            // button ("ui.infiniteDreams.rerollFavor"). Its answer is INFERRED to be the izg with
            // the new three, as the storm on a fight answers with the izg of the new group.
            if (actual != null && actual.EsFavor && Network.SessionContext.State.FightId == 0
                && sueno.Tormentas > 0 && Dreams.RerollFavor(actual))
            {
                sueno.Tormentas--;
                Persist(sueno);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Izj, DreamProtocol.BuildStorm()));
                Console.WriteLine($"[Sueños] Astral storm on the favour of room {actual.Id}: " +
                                  $"{string.Join(", ", actual.Offers!.Select(o => o.Tag))}; {sueno.Tormentas} left.");
                return;
            }

            string? refusal =
                Network.SessionContext.State.FightId != 0 ? "in a fight"
                : sueno.Tormentas <= 0 ? "no storm left"
                : actual == null || actual.Miembros.Count == 0 || actual.Hecha ? "no group to change here"
                : null;
            if (refusal != null)
            {
                Console.WriteLine($"[Sueños] Astral storm of {sueno.CharacterId} refused: {refusal}.");
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));
                return;
            }

            Dreams.Reroll(sueno, actual!);
            sueno.Tormentas--;
            await EntrarEnSalaAsync(stream, sueno, actual!.Id);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izj, DreamProtocol.BuildStorm()));

            Console.WriteLine($"[Sueños] Astral storm of {sueno.CharacterId}: room {actual.Id} rerolled, " +
                              $"{sueno.Tormentas} left.");
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
            MarkLeft();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iom));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Iyb, DreamProtocol.BuildLeft(),
                                          ConnectionProtocol.RequestId(payload)));

            Console.WriteLine($"[Sueños] {sueno.CharacterId} sale del sueño en la sala " +
                              $"{sueno.Actual} con {sueno.DreamPoints} dream point(s).");

            // Leaving keeps the dream: the well offers it to continue, in the captures and here.
            // Only its groups leave their maps.
            Dreams.Unplant(sueno);
            Persist(sueno);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  The Fin du rêve, and the end of a dream
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>A Fin du rêve going on: whose, its rules, and the waves that have fallen.</summary>
        private sealed class FinalFight
        {
            public long CharacterId { get; init; }
            public Dreams.FinalRules Rules { get; init; } = Dreams.FinalRulesOf(1);
            public int Cleared { get; set; }
        }

        private static readonly ConcurrentDictionary<long, FinalFight> _finales = new();

        /// <summary>
        /// A fight has just been built. On the dream's last room it is the Fin du rêve: its first
        /// wave is brought to its level, and its waves are counted from here.
        /// </summary>
        internal static void OnFightCreated(Jondo.Unity.World.Fights.FightInstance fight)
        {
            if (!Dreams.IsDreamMap(fight.RoleplayMapId)) return;
            var sueno = Dreams.De(GameState.CharacterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || !sala.EsFinal || sala.MapaDeLaSala != fight.RoleplayMapId) return;

            var rules = Dreams.FinalRulesOf(sueno.Dificultad);
            foreach (var monster in fight.Rojo) Dreams.ScaleTo(monster, rules.BaseLevel);
            _finales[fight.FightId] = new FinalFight { CharacterId = sueno.CharacterId, Rules = rules };
            Console.WriteLine($"[Sueños] Fin du rêve of {sueno.CharacterId}: wave 1 at level {rules.BaseLevel}, " +
                              $"{rules.MinWaves} to win, {(rules.MaxWaves > 0 ? rules.MaxWaves.ToString() : "no")} most.");
        }

        /// <summary>
        /// A wave of the Fin du rêve has fallen: the next one -- its monsters, its level and its
        /// number -- or null when there is none, and the fight ends.
        /// </summary>
        internal static (List<(int Monstruo, int Grado)> Members, int Level, int Wave)? NextWave(
            Jondo.Unity.World.Fights.FightInstance fight)
        {
            if (!_finales.TryGetValue(fight.FightId, out var state)) return null;
            state.Cleared++;
            if (state.Rules.MaxWaves > 0 && state.Cleared >= state.Rules.MaxWaves) return null;

            int wave = state.Cleared + 1;
            int level = state.Rules.BaseLevel + state.Rules.Step * state.Cleared;
            Console.WriteLine($"[Sueños] Fin du rêve: wave {state.Cleared} down, wave {wave} at level {level}.");
            return (Dreams.FinalWave(wave), level, wave);
        }

        /// <summary>
        /// What a fight in a dream's room pays, worked out before anybody is shown the end: the
        /// dream's difficulty, the row and loot bonus of the room, and -- at the Fin du rêve, once
        /// it is won or its minimum of waves has fallen -- the dream fragments of the waves that
        /// fell. Null for a fight that is not a dream's.
        /// </summary>
        internal sealed record DreamPay(int Difficulty, int Row, int LootBonus, bool Finished, int Waves, int Fragments);

        internal static DreamPay? PayOf(Jondo.Unity.World.Fights.FightInstance fight, bool won)
        {
            if (!Dreams.IsDreamMap(fight.RoleplayMapId)) return null;

            // The dream fought in: the first of its people's whose room is on this fight's map.
            Dreams.Sueno? sueno = null;
            foreach (var fighter in fight.Azul.Concat(fight.Rojo))
            {
                if (fighter.IsMonster) continue;
                var dream = Dreams.De(fighter.Id);
                if (dream?.SalaActual?.MapaDeLaSala != fight.RoleplayMapId) continue;
                sueno = dream;
                break;
            }
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null) return null;

            int waves = 0;
            bool finished = false;
            if (_finales.TryGetValue(fight.FightId, out var final))
            {
                waves = final.Cleared;
                finished = won || waves >= final.Rules.MinWaves;
            }
            return new DreamPay(sueno.Dificultad, sala.Fila, sueno.Bonus, finished, waves,
                                finished ? Dreams.FragmentsFor(sueno.Dificultad, waves) : 0);
        }

        /// <summary>
        /// A fight in a dream's room is over: where the player goes, and what he is told.
        /// </summary>
        /// <remarks>
        /// The Fin du rêve ends the dream won once its minimum of waves has fallen -- 1 in a Rêve,
        /// 3 in a Paradoxe and a Cauchemar -- whether the fight ends by the last wave or by the
        /// player falling after that. Any other loss ends the dream, unless a Draconiros arena is
        /// left: then it is spent and the room can be tried again, which is what the capture of
        /// "Sueño III-pelear-morir-reaparecer en sala" shows -- the f17 gone after the death, the
        /// player back in the room. A dream that ends sends the player out, where he came from.
        /// </remarks>
        internal static (long MapOut, int CellOut, string? Notice, bool Ended) AfterTheFight(
            Jondo.Unity.World.Fights.FightInstance fight, bool won)
        {
            _finales.TryRemove(fight.FightId, out var final);
            if (!Dreams.IsDreamMap(fight.RoleplayMapId)) return (0, 0, null, false);
            var sueno = Dreams.De(GameState.CharacterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || sala.MapaDeLaSala != fight.RoleplayMapId) return (0, 0, null, false);

            if (final != null)
            {
                int cleared = final.Cleared;
                if (won || cleared >= final.Rules.MinWaves)
                {
                    // The dream fragments of the waves were paid with the fight's spoils
                    // (FightHandler.PlanRewards); the notice says how many.
                    int fragments = Dreams.FragmentsFor(sueno.Dificultad, cleared);
                    var (map, cell) = End(sueno);
                    return (map, cell, CommandTexts.Get("dream.completed", cleared, fragments), true);
                }
            }

            if (won) return (0, 0, null, false);

            if (sueno.Arena > 0)
            {
                sueno.Arena--;
                Persist(sueno);
                return (0, 0, CommandTexts.Get("dream.arena"), false);
            }

            var (outMap, outCell) = End(sueno);
            return (outMap, outCell, CommandTexts.Get("dream.over"), true);
        }

        /// <summary>A dream over: forgotten, off the base, and where the player goes out to.</summary>
        private static (long Map, int Cell) End(Dreams.Sueno sueno)
        {
            var (mapa, casilla) = Dreams.DeDondeViene(sueno.CharacterId);
            if (mapa == 0) { mapa = sueno.MapaDeVuelta; casilla = sueno.CasillaDeVuelta; }
            if (mapa == 0 || Dreams.IsDreamMap(mapa)) { mapa = PlanoAstral; casilla = 0; }

            Dreams.Olvidar(sueno.CharacterId);
            DatabaseManager.DeleteDream(sueno.CharacterId);
            Console.WriteLine($"[Sueños] The dream of {sueno.CharacterId} is over; out to map {mapa}.");
            return (mapa, casilla);
        }

        /// <summary>
        /// ".sueno": the dream carried forward by <see cref="Dreams.SkipTo"/>, and the player into
        /// the room it reached -- the groups it left planted gone first, and, when he was out in
        /// the world, that map noted as the way back, for the dream to send him there at its end.
        /// </summary>
        internal static async Task<(Dreams.SkipOutcome Outcome, Dreams.Sala? Room, int Skipped)> SkipToAsync(
            NetworkStream stream, Dreams.Sueno sueno, int? row)
        {
            var result = Dreams.SkipTo(sueno, row);
            if (result.Outcome != Dreams.SkipOutcome.Done || result.Room == null) return result;

            Dreams.Unplant(sueno);
            if (!Dreams.IsDreamMap(GameState.MapId) && GameState.MapId != PlanoAstral)
                Dreams.RecordarDeDondeViene(sueno.CharacterId, GameState.MapId, GameState.CellId);

            Console.WriteLine($"[Sueños] {sueno.CharacterId} skips to room {result.Room.Id} (row {result.Room.Fila}): " +
                              $"{result.Skipped} fight(s) counted as won on the way.");
            await EntrarEnSalaAsync(stream, sueno, result.Room.Id);
            return result;
        }

        /// <summary>Whether the last map each character loaded was one of the dream's.</summary>
        private static readonly ConcurrentDictionary<long, bool> _enElSueno = new();

        /// <summary>The dream's interface already closed by whoever sent the ixg.</summary>
        internal static void MarkLeft() => _enElSueno[GameState.CharacterId] = false;

        /// <summary>
        /// A map loaded, however the player got there. Out of the dream's maps by any way but its
        /// own exit -- the Merkasako and its zaap, a teleport, a command -- the client still has the
        /// dream's interface up, and only the ixg takes it down: the real server sends it on its
        /// own, with no iyx before it, in "sueño infinito largo" and "recibir invitacion a
        /// sueños". The dream is kept, as the exit keeps it, and its groups leave their maps.
        /// </summary>
        public static async Task OnMapLoadedAsync(NetworkStream stream)
        {
            long yo = GameState.CharacterId;
            bool aqui = Dreams.IsDreamMap(GameState.MapId);
            bool antes = _enElSueno.TryGetValue(yo, out bool estaba) && estaba;
            _enElSueno[yo] = aqui;

            // And back onto a dream's map from outside it -- H out of the Merkasako lands where it
            // was opened -- is the same as waking there: the room again, its group planted back
            // and the interface up, or the Plano Astral when there is no dream to go back to.
            // Without it the room stood empty, and a room with no group counts as passed.
            if (!antes && aqui)
            {
                await OnWorldEntryAsync(stream);
                return;
            }
            if (!antes || aqui) return;

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ixg));
            var sueno = Dreams.De(yo);
            if (sueno != null)
            {
                Dreams.Unplant(sueno);
                Persist(sueno);
            }
            Console.WriteLine($"[Sueños] {yo} left the dream's maps without its exit: the interface closed.");
        }

        /// <summary>Saves a dream, so a disconnection or a restart does not lose it.</summary>
        internal static void Persist(Dreams.Sueno sueno)
            => DatabaseManager.SaveDream(sueno.CharacterId, Dreams.Serialize(sueno));

        /// <summary>
        /// The world entered on a dream's map: the dream again -- its state, its room's group, its
        /// panel -- or, with no dream to go back to, the Plano Astral. A player who logged in
        /// there after a restart stood in a room with no dream around it and no way out.
        /// </summary>
        public static async Task OnWorldEntryAsync(NetworkStream stream)
        {
            if (!Dreams.IsDreamMap(GameState.MapId)) return;

            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno?.SalaActual == null)
            {
                Console.WriteLine($"[Sueños] {GameState.CharacterId} woke in a dream's map with no dream: " +
                                  "to the Plano Astral.");
                await TeleportHandler.ToMapAsync(stream, PlanoAstral, 0);
                return;
            }

            Console.WriteLine($"[Sueños] {GameState.CharacterId} back in room {sueno.Actual} of the dream.");
            await EntrarEnSalaAsync(stream, sueno, sueno.Actual);
        }
    }
}
