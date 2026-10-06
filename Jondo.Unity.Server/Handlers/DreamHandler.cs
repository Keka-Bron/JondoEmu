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
    /// The Infinite Dreams: opening the window, starting, moving between rooms and leaving.
    /// </summary>
    /// <remarks>
    /// The whole cycle, measured on the thirteen captures in <c>Sueños Infinitos/</c>:
    ///
    /// <code>
    ///   C-&gt;S  iwo          using the well, which is an ordinary interactive
    ///   S-&gt;C  iyj          the dream's map: eleven rooms and the graph that joins them
    ///   C-&gt;S  ixf { f1 }   starting at the difficulty it carries inside
    ///   S-&gt;C  izg + jru    the state, and the map change to the first room
    ///   S-&gt;C  ixa          the acknowledgement, empty and through root 3
    ///   C-&gt;S  iwo          choosing a door
    ///   S-&gt;C  izg + jru    new state, and on to the next room
    ///   C-&gt;S  iyx          leaving
    ///   S-&gt;C  jru + ixg + iom  and the iyb «0801» through root 3
    /// </code>
    ///
    /// The good thing about this is how much it rests on what already exists: the well and each
    /// door are <c>iwo</c>, the good old interactive; the rooms are populated with rows of
    /// <see cref="Dreams"/> taken from MapMobs; and each room's modifier is an effect from the
    /// same catalogue that drives the spell engine.
    /// </remarks>
    public static class DreamHandler
    {
        /// <summary>
        /// The Astral Plane, which is where the menu button leads.
        /// </summary>
        /// <remarks>
        /// Measured: the jru that follows the iyc goes to 238551040, which in our own database is
        /// subarea 938, «Dominios de Draconiros». It is the Dreams' lobby, not a room.
        /// </remarks>
        public const long PlanoAstral = 238551040;

        // ═══════════════════════════════════════════════════════════════════
        //  The menu button
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// The Infinite Dreams button of the menu, and the T key (iyc).
        /// </summary>
        /// <remarks>
        /// It does not open the window: it TELEPORTS to the Astral Plane, and there the well is what
        /// opens it. Measured in the capture, where the iyc is followed by a jru to the plane and the
        /// usual iom.
        ///
        /// Where he comes from is noted so he can be sent back: if he is already on the plane nothing is
        /// done, otherwise a second press of the key would store the plane as the place to return to and
        /// the player would stay there forever.
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
        //  Opening the window
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Shows the dream's map. It is what answers using the well.</summary>
        public static async Task ShowAsync(NetworkStream stream)
        {
            var yo = GameState.CharacterId;

            // The dream the character has going, to continue; with none, an empty iyj, and the
            // window only offers a new one -- the long capture, whose player has none, gets zero
            // bytes. A new dream was created here and shown as one to continue, with the header
            // of a capture's: 5 points and 1 MP that were nobody's.
            var sueno = Dreams.De(yo);

            // Release the element first. In the Nightmare II capture the order is exact:
            //
            //   C->S iwo  0887a20110e0f720
            //   S->C iwn  080110e0f72020b80128a28280c8e708
            //   S->C iyj  (618 B)
            //
            // And the order matters: without the iwn the client still considers the well busy and
            // does not open the window that arrives after it. It gives no error; simply nothing
            // happens, which is what was seen when pressing it. The f4 of that iwn is 184, the same
            // skill already announced in the f11.
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
        //  Starting and discarding
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Starting a dream (ixf f1) or going into the existing one (ixf f2).</summary>
        /// <remarks>
        /// f2 was misread for a while: it was taken for «discard» because the captures where it
        /// appears are called «descartar el sueño en curso». The bytes say something else. In
        /// «continuar sueño infinito» and in «Sueño II-descartar», the same «12020801» is followed by
        /// an izg and a jru TO A ROOM: the player GOES IN.
        ///
        ///   C->S ixf  12020801
        ///   S->C izg  (1150 B)
        ///   S->C jru  108080b071      -> 237764608, the room he was in
        ///   S->C ixa  (root 3, empty)
        ///
        /// And discarding has no message of its own: in «Sueño III-descartar» and «paradoja I-descartar»
        /// the client directly sends f1 with the new difficulty. The «you already have a dream in
        /// progress» window is settled in the client; only the start reaches the server.
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
                    // Starting: the difficulty goes in the inner f3.
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

            // The acknowledgement goes through root 3, empty, with the request id.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Answer(Op.Ixa, null, ConnectionProtocol.RequestId(payload)));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Moverse
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// A dream door, pressed. Returns false if that skill belongs to no door.
        /// </summary>
        /// <remarks>
        /// It is called from the interactives handler, before it treats the iwo as it always does:
        /// inside a dream the doors are interactives that do not exist on the roleplay map, so the
        /// normal path would not know what to do with them.
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

            // And a room is not left without clearing it. The guide says it the only way that
            // matters: «it is absolutely impossible to go back» once you go in, and you advance room
            // by room fighting. With the doors open from the start the whole dream could be walked
            // through without striking a blow, collecting the points of every room.
            //
            // The entrance and the fountain have no group, so they do not block; a favour does until
            // one of its three is chosen.
            if (!Dreams.CanLeave(actual))
            {
                Console.WriteLine(actual.EsFavor
                    ? $"[Sueños] The favour of room {actual.Id} is not chosen yet: the door stays shut."
                    : $"[Sueños] La sala {actual.Id} todavía tiene su grupo en pie: no se abre la puerta.");
                return false;
            }

            // The doors are the elements of the room's own map, in their order: the first leads to
            // the first exit, the second to the second. The maps of subarea 904 carry three, which is
            // also the most exits measured in a room.
            for (int cual = 0; cual < actual.Salidas.Count; cual++)
            {
                if (Dreams.PuertaDe(actual, cual) != elementId) continue;

                // Release the door BEFORE the izg and the jru. Measured in the Dream III capture:
                //
                //   C->S iwo  08d0a59f0310f7f620          element 539511
                //   S->C iwn  080110f7f62020b80128…       with skill 184
                //   S->C izg  (833 B)
                //   S->C jru  108090b071
                //
                // It is the same order as the well's, and skipping it has the same price: the
                // client keeps the door as busy and nothing of what comes after happens.
                // Without a single error.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                        elementId, Dreams.HabilidadDelPozo, GameState.CharacterId)));

                await EntrarEnSalaAsync(stream, sueno, actual.Salidas[cual]);
                return true;
            }

            return false;
        }

        /// <summary>Puts the player in a room: the state and the map change.</summary>
        private static async Task EntrarEnSalaAsync(NetworkStream stream, Dreams.Sueno sueno,
                                                    int salaId)
        {
            int pointsBefore = sueno.DreamPoints;
            var sala = Dreams.Enter(sueno, salaId, out var gained);
            if (sala == null) return;
            _enElSueno[GameState.CharacterId] = true;

            // The booster and the points are collected ON ENTERING, before fighting, and only once per room.
            if (gained != null || sueno.DreamPoints != pointsBefore)
            {
                Console.WriteLine($"[Sueños] Sala {sala.Id}: +{sueno.DreamPoints - pointsBefore} dream points, " +
                                  $"{sueno.DreamPoints} in all" +
                                  (gained != null ? $"; bonus {gained.Efecto} of {gained.Valor}, " +
                                                    $"{sueno.Ganados.Count} so far." : "."));
            }

            // Stepping on the Fountain opens the next band, because the fountain is both the last
            // room of this one and the first of the next: «Chaque palier commencera toujours par
            // une Fontaine Onirique». If it is not added here, the player goes into a room with no
            // exits and is locked in, which is what used to happen.
            if (Dreams.Closes(sala) && sala.Salidas.Count == 0)
            {
                Dreams.AnadirFranja(sueno);
                Console.WriteLine($"[Sueños] Franja {sueno.Franja} abierta: " +
                                  $"{sueno.Salas.Count} salas en total.");
            }

            Persist(sueno);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Izg, StateOf(sueno)));

            // And the map change: to the ROOM's map, which is one of the 484 of subarea 904 made
            // for this, not the monster group's. Sending him to the group's is what left him
            // standing in Frigost, walking around the world without a minimap.
            long mapa = sala.MapaDeLaSala;
            if (mapa == 0)
            {
                Console.WriteLine($"[Sueños] La sala {salaId} se ha quedado sin mapa propio.");
                return;
            }

            // The group is placed BEFORE the map change: the jss the client asks for right after
            // is the one carrying the actors, and a group placed a moment late does not appear
            // until the room is entered again.
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
        /// Puts the vendor on the Dream Fountain.
        /// </summary>
        /// <remarks>
        /// No new protocol is needed: the Dreams' fountain is an NPC, full stop. It is placed like
        /// any other and the dialogue engine does the rest; what it offers is written in its answer,
        /// with the percentage of points it gives.
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
        /// Puts in the room the monsters that belong to it, if they are not there already.
        /// </summary>
        /// <remarks>
        /// Without this the room is empty and there is nothing to attack: the client asks for the
        /// fight with an hqa carrying the contextual id of a group on the map, so if there is no group
        /// there is no way to start. In the Dream III capture the hqa with that negative can be seen
        /// just before the kub, and that is as far as the room gets without giving any error: there is
        /// simply no fighting.
        ///
        /// The entrance and the last one carry no group, which is what the nine captures say.
        /// </remarks>
        private static void PlantarElGrupo(Dreams.Sala sala)
        {
            if (sala.Miembros.Count == 0 || sala.MapaDeLaSala == 0) return;

            // Already placed: the same room is entered again when continuing a dream.
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
        /// A room's fight has been won: it is marked done.
        /// </summary>
        /// <remarks>
        /// Returns true if the defeated group was a room's, which is what tells the fight engine NOT to
        /// put another one in its place.
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

        /// <summary>Sends the dream's state again, if there is one and he is in it.</summary>
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
        //  The storm and leaving
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>The astral storm (izh): another group for the room, on another map.</summary>
        /// <remarks>
        /// Measured: the client sends it empty and an izg, a jru and an izj «1001» come back. And what it
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

        /// <summary>Leaving the dream (iyx) and going back to where he was.</summary>
        public static async Task LeaveAsync(NetworkStream stream, byte[] payload)
        {
            var sueno = Dreams.De(GameState.CharacterId);
            if (sueno == null) return;

            // To where he was BEFORE PRESSING THE BUTTON, not to the map the dream was started
            // from: by then that map is the Astral Plane itself, and sending him back there would
            // leave him going round in circles in the lobby.
            var (mapa, casilla) = Dreams.DeDondeViene(sueno.CharacterId);
            if (mapa == 0) { mapa = sueno.MapaDeVuelta; casilla = sueno.CasillaDeVuelta; }

            if (mapa != 0 && mapa != PlanoAstral)
            {
                await TeleportHandler.ToMapAsync(stream, mapa, casilla);
            }
            else
            {
                // With no known place, to the plane: it is where he went in from and it always exists.
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
