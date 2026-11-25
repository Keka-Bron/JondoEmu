using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Game = Jondo.Unity.Protocol.Game;
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
        public static async Task ToAstralPlaneAsync(GameSession session)
        {
            if (session.MapId == PlanoAstral)
            {
                Console.WriteLine("[Dreams] Already on the Plano Astral.");
                return;
            }

            Dreams.RecordarDeDondeViene(session.CharacterId, session.MapId, session.State.CellId);

            int aterriza = await TeleportHandler.ToMapAsync(session, PlanoAstral, 0);
            Console.WriteLine($"[Dreams] {session.CharacterId} to the Plano Astral, cell {aterriza}.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Opening the window
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Shows the dream's map. It is what answers using the well.</summary>
        public static async Task ShowAsync(GameSession session)
        {
            var yo = session.CharacterId;

            // The dream the character has going, to continue; with none, an empty iyj, and the
            // window only offers a new one -- the long capture, whose player has none, gets zero
            // bytes. A new dream was created here and shown as one to continue, with the header
            // of a capture's: 5 points and 1 MP that were nobody's.
            var sueno = Dreams.De(yo);

            // Release the element first. In the Nightmare II capture the order is exact:
            //
            //   C->S iwo  0887a20110e0f720
            //   S->C iwn  080110e0f72020b8012881f092cbdd08
            //   S->C iyj  (618 B)
            //
            // And the order matters: without the iwn the client still considers the well busy and
            // does not open the window that arrives after it. It gives no error; simply nothing
            // happens, which is what was seen when pressing it. The f4 of that iwn is 184, the same
            // skill already announced in the f11.
            await session.SendAsync(
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    Dreams.ElementoDelPozo, Dreams.HabilidadDelPozo, yo)));

            await session.SendAsync(
                ConnectionProtocol.Push(Op.Iyj, DreamProtocol.BuildDreamMap(sueno)));

            Console.WriteLine(sueno == null
                ? $"[Dreams] Well of {yo}: no dream to continue."
                : $"[Dreams] Well of {yo}: dream of difficulty {sueno.Dificultad} in room {sueno.Actual}, " +
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
        ///
        /// A start is { f1: the code, f2: training, f3: the difficulty, f4: 1 }, the client's
        /// own building of it (efs::djw, from the well's "Commencer"): the code and the training
        /// box only when a code was typed -- or a trial chosen, whose code it is --, the
        /// difficulty and the 1 always.
        /// </remarks>
        public static async Task StartOrDiscardAsync(GameSession session, byte[] payload)
        {
            byte[]? ixf = ConnectionProtocol.ReadPayload(payload, Op.Ixf);
            if (ixf == null) return;

            int dificultad = 0;
            bool continuar = false;
            string? seed = null;
            bool training = false;

            foreach (var field in ProtoMessage.Parse(ixf).Fields)
            {
                if (field.WireType != 2 || field.BytesValue == null) continue;

                if (field.FieldNumber == 1)
                {
                    // Starting: the difficulty goes in the inner f3, a code in the f1 and the
                    // training box in the f2.
                    foreach (var dentro in ProtoMessage.Parse(field.BytesValue).Fields)
                    {
                        if (dentro.FieldNumber == 3 && dentro.WireType == 0)
                        {
                            dificultad = (int)dentro.VarIntValue;
                        }
                        else if (dentro.FieldNumber == 1 && dentro.WireType == 2 && dentro.BytesValue != null)
                        {
                            seed = System.Text.Encoding.UTF8.GetString(dentro.BytesValue).Trim();
                        }
                        else if (dentro.FieldNumber == 2 && dentro.WireType == 0)
                        {
                            training = dentro.VarIntValue != 0;
                        }
                    }
                }
                else if (field.FieldNumber == 2)
                {
                    continuar = true;
                }
            }

            long yo = session.CharacterId;

            if (continuar)
            {
                var enCurso = Dreams.De(yo);
                if (enCurso == null)
                {
                    Console.WriteLine($"[Dreams] {yo} wants to continue and has no dream in progress.");
                    return;
                }

                Console.WriteLine($"[Dreams] {yo} continues in room {enCurso.Actual}.");

                await EntrarEnSalaAsync(session, enCurso, enCurso.Actual);

                await session.SendAsync(
                    ConnectionProtocol.Answer(Op.Ixa, null, ConnectionProtocol.RequestId(payload)));
                return;
            }

            if (dificultad <= 0 || dificultad > Dreams.MaximaDificultad)
            {
                Console.WriteLine($"[Dreams] Difficulty {dificultad} outside the ladder from 1 to " +
                                  $"{Dreams.MaximaDificultad}. ixf: " +
                                  Convert.ToHexString(ixf).ToLowerInvariant());
                return;
            }

            if (!string.IsNullOrEmpty(seed) && !Dreams.IsSeed(seed))
            {
                Console.WriteLine($"[Dreams] {yo} typed «{seed}», which is no dream's code.");
                await session.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice(CommandTexts.Get(session.State.Language, "dream.bad_code", seed))));
                return;
            }

            var sueno = Dreams.Crear(yo, session.State.CharacterName, session.State.CharacterLevel,
                                     dificultad, session.MapId, session.State.CellId, session.State.Breed,
                                     seed, training);
            Persist(sueno);

            Console.WriteLine($"[Dreams] {yo} starts at difficulty {dificultad}, code {sueno.Seed}" +
                              (sueno.FromSouvenir ? " (a souvenir, no rewards" + (sueno.Training ? ", training)" : ")") : "") +
                              $": {sueno.Salas.Count} rooms.");

            await EntrarEnSalaAsync(session, sueno, 0);

            // The acknowledgement goes through root 3, empty, with the request id.
            await session.SendAsync(
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
        public static async Task<bool> TryDoorAsync(GameSession session, int elementId)
        {
            // A dream's doors and fountain are on its own maps: out in the world, a dream left
            // to be continued has nothing to say about what the player clicks.
            if (!Dreams.IsDreamMap(session.MapId)) return false;
            var sueno = Dreams.De(session.CharacterId);
            if (sueno == null) return false;

            var actual = sueno.SalaActual;
            if (actual == null) return false;

            // The Fontaine onirique of a fountain room: the shop, not a way out.
            if (actual.EsFuente && elementId == Dreams.FountainOf(actual))
            {
                await ShopAsync(session, sueno, elementId);
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
                    ? $"[Dreams] The favour of room {actual.Id} is not chosen yet: the door stays shut."
                    : $"[Dreams] Room {actual.Id} still has its group standing: the door does not open.");
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
                await session.SendAsync(
                    ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                        elementId, Dreams.HabilidadDelPozo, session.CharacterId)));

                await EntrarEnSalaAsync(session, sueno, actual.Salidas[cual]);
                return true;
            }

            return false;
        }

        /// <summary>
        /// A room clicked on the dream's map (ixn { f1: the room, as the map names it }). The map's
        /// InfiniteDreamMap.OnRoomClick sends it; it moves the player there the way that room's
        /// door would, and under the same rules: standing in the dream, the room stood in cleared,
        /// and the one clicked among its exits. Nothing for anything else.
        /// </summary>
        /// <remarks>
        /// INFERRED from the client: no capture clicks the map. The room ids are the strings the
        /// map was given (<see cref="DreamProtocol"/>), so the click names one of them.
        /// </remarks>
        public static async Task ChooseRoomAsync(GameSession session, byte[] payload)
        {
            byte[]? ixn = ConnectionProtocol.ReadPayload(payload, Op.Ixn);
            if (ixn == null || !Dreams.IsDreamMap(session.MapId)) return;
            var sueno = Dreams.De(session.CharacterId);
            if (sueno == null) return;

            string clicked = "";
            foreach (var field in ProtoMessage.Parse(ixn).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2)
                    clicked = System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }

            int? room = RoomChosen(sueno, clicked);
            if (room == null)
            {
                Console.WriteLine($"[Dreams] Room «{clicked}» clicked on the map: not a way out of where he stands.");
                return;
            }
            await EntrarEnSalaAsync(session, sueno, room.Value);
        }

        /// <summary>The room a click on the map leads to, if the room stood in lets him go there.</summary>
        public static int? RoomChosen(Dreams.Sueno sueno, string clicked)
        {
            if (!int.TryParse(clicked, System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int id)) return null;
            var actual = sueno.SalaActual;
            if (actual == null || !Dreams.CanLeave(actual) || !actual.Salidas.Contains(id)) return null;
            return id;
        }

        /// <summary>Puts the player in a room: the state and the map change.</summary>
        private static async Task EntrarEnSalaAsync(GameSession session, Dreams.Sueno sueno,
                                                    int salaId)
        {
            int pointsBefore = sueno.DreamPoints;
            var sala = Dreams.Enter(sueno, salaId, out var gained);
            if (sala == null) return;
            _enElSueno[session.CharacterId] = true;

            // The booster and the points are collected ON ENTERING, before fighting, and only once per room.
            if (gained != null || sueno.DreamPoints != pointsBefore)
            {
                Console.WriteLine($"[Dreams] Room {sala.Id}: +{sueno.DreamPoints - pointsBefore} dream points, " +
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
                Console.WriteLine($"[Dreams] Band {sueno.Franja} open: " +
                                  $"{sueno.Salas.Count} rooms in all.");

                // And the band it closes is done: the guild missions that asked for it.
                await GuildMissionWatcher.DreamBandDoneAsync(session, sueno.Dificultad, sala.Franja);
            }

            Persist(sueno);

            await session.SendAsync(StateOf(sueno));

            // And the map change: to the ROOM's map, which is one of the 484 of subarea 904 made
            // for this, not the monster group's. Sending him to the group's is what left him
            // standing in Frigost, walking around the world without a minimap.
            long mapa = sala.MapaDeLaSala;
            if (mapa == 0)
            {
                Console.WriteLine($"[Dreams] Room {salaId} has been left without a map of its own.");
                return;
            }

            // The group is placed BEFORE the map change: the jss the client asks for right after
            // is the one carrying the actors, and a group placed a moment late does not appear
            // until the room is entered again.
            // A room won keeps its group off: re-entered -- a dream continued, Gobledore's way
            // back -- it planted it again, a fight already won to be fought.
            if (sala.EsFavor) PlantFavorNpc(sala);
            else if (!sala.EsFuente && !sala.Hecha) PlantarElGrupo(sala);
            PlantGoblin(sala);

            int aterriza = await TeleportHandler.ToMapAsync(session, mapa, 0);

            // And his guests follow him in.
            if (sueno.CharacterId == session.CharacterId) await BringGuestsAsync(sueno);

            Console.WriteLine($"[Dreams] Room {salaId} (row {sala.Fila}): map {mapa}, " +
                              $"group {sala.Grupo} of map {sala.MapaId} with " +
                              $"{sala.Miembros.Count} monster(s), effect {sala.Efecto} of " +
                              $"{sala.Valor}. Lands on {aterriza}.");
        }

        /// <summary>
        /// Puts the room's goblin on its map, when there is one and his power was not taken --
        /// the goblins another dream left on this map taken off first: the room maps are shared.
        /// </summary>
        /// <remarks>
        /// No new protocol is needed: a goblin is an NPC, full stop. It is placed like any other
        /// and the dialogue engine does the rest; what his accepting reply does is the actions it
        /// carries (<see cref="GoblinPowerAsync"/>).
        /// </remarks>
        private static void PlantGoblin(Dreams.Sala sala)
        {
            if (sala.MapaDeLaSala == 0) return;
            foreach (int goblin in Dreams.Goblins) Managers.Npcs.QuitarDelSueno(sala.MapaDeLaSala, goblin);
            if (sala.Goblin == 0 || sala.GoblinGone) return;

            Managers.Npcs.PonerDelSueno(sala.MapaDeLaSala, sala.Goblin,
                                        Dreams.GoblinCell(sala), Dreams.OrientacionDelReyGob);
        }

        // The client's dream actions a goblin's reply carries (its ActionId).
        internal const int RerollDreamRoom = 4032;
        internal const int ClearDreamRoom = 4033;
        internal const int TeleportToPrecedentDreamRoom = 4034;
        internal const int MultiplyCurrentResourceDream = 4035;
        internal const int HideDreamNpc = 4037;

        /// <summary>Whether a reply carries a goblin's power: one of the client's actions 4032 to 4037.</summary>
        internal static bool CarriesGoblinPower(IReadOnlyList<long> actions)
            => actions.Any(a => a is >= RerollDreamRoom and <= HideDreamNpc);

        /// <summary>
        /// A goblin's accepted power: the actions of his reply, the client's own. 4037
        /// HideDreamNpc takes him off ("le Gobelin disparaît"); 4032 renews the room -- the
        /// fountain's offers, or a fight's group as a storm does; 4033 wins it; 4034 goes back to
        /// the room before. 4035, the Rey Gob's, is the reply's dream points (NpcHandler).
        /// </summary>
        internal static async Task GoblinPowerAsync(GameSession session, IReadOnlyList<long> actions)
        {
            var sueno = Dreams.De(session.CharacterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || sala.Goblin == 0 || sala.GoblinGone)
            {
                Console.WriteLine($"[Dreams] No goblin of {session.CharacterId}'s dream to take a power from here.");
                return;
            }
            int goblin = sala.Goblin;

            if (actions.Contains(HideDreamNpc))
            {
                sala.GoblinGone = true;
                foreach (long actor in Managers.Npcs.QuitarDelSueno(sala.MapaDeLaSala, goblin))
                    await session.SendAsync(new Jondo.Unity.Protocol.Game.ContextRemoveElementEvent(actor));
            }

            if (actions.Contains(ClearDreamRoom))
            {
                long group = Dreams.ClearRoom(sala);
                if (group != 0)
                    await session.SendAsync(new Jondo.Unity.Protocol.Game.ContextRemoveElementEvent(group));
            }

            if (actions.Contains(TeleportToPrecedentDreamRoom) && Dreams.GoBack(sueno) is { } before)
            {
                Console.WriteLine($"[Dreams] Goblin {goblin}: back from room {sala.Id} to room {before.Id}.");
                await EntrarEnSalaAsync(session, sueno, before.Id);
                return;
            }

            if (actions.Contains(RerollDreamRoom))
            {
                if (sala.EsFuente || sala.EsFavor) Dreams.RerollOffers(sueno, sala);
                else if (sala.Miembros.Count > 0 && !sala.Hecha)
                {
                    Dreams.Reroll(sueno, sala);
                    Console.WriteLine($"[Dreams] Goblin {goblin}: room {sala.Id} rerolled.");
                    await EntrarEnSalaAsync(session, sueno, sala.Id);
                    return;
                }
            }

            Console.WriteLine($"[Dreams] Goblin {goblin}'s power in room {sala.Id}: {string.Join(", ", actions)}.");
            Persist(sueno);
            await RefrescarEstadoAsync(session);
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
        internal static async Task<bool> OfferFavorAsync(GameSession session)
        {
            var sueno = Dreams.De(session.CharacterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || !sala.EsFavor || sala.FavorChosen)
            {
                Console.WriteLine($"[Dreams] No favour to offer to {session.CharacterId} here.");
                return false;
            }

            sala.Offers ??= Dreams.DrawFavor(sueno, sala);
            Persist(sueno);
            await session.SendAsync(StateOf(sueno));
            await session.SendAsync(ConnectionProtocol.Push(Op.Ixm));
            Console.WriteLine($"[Dreams] Favour of room {sala.Id}: " +
                              string.Join(", ", sala.Offers.Select(o => o.Tag)) + ".");
            return true;
        }

        /// <summary>Whether the favour of the room one stands in is still to be chosen: what the Dispensador says.</summary>
        internal static bool FavorPending(long characterId)
        {
            var sala = Dreams.De(characterId)?.SalaActual;
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
                Console.WriteLine($"[Dreams] Room {sala.Id} could not plant its group.");
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
        public static bool SalaLimpiada(long characterId, long grupoDerrotado, long mapa)
        {
            if (grupoDerrotado == 0 || !Dreams.IsDreamMap(mapa)) return false;

            var sueno = Dreams.De(characterId);
            if (sueno == null) return false;

            foreach (var sala in sueno.Salas)
            {
                if (sala.Plantado != grupoDerrotado) continue;

                sala.Plantado = 0;
                if (sala.Hecha) return true;

                sala.Hecha = true;
                Persist(sueno);

                Console.WriteLine($"[Dreams] Room {sala.Id} cleared; {sueno.DreamPoints} dream points.");
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
        public static async Task RefrescarEstadoAsync(GameSession session)
        {
            if (!Dreams.IsDreamMap(session.MapId)) return;
            var sueno = Dreams.De(session.CharacterId);
            if (sueno == null) return;

            await session.SendAsync(StateOf(sueno));
        }

        /// <summary>The izg of a dream, with the bestiary of the room one stands in.</summary>
        private static Game.InfiniteDreamStateEvent StateOf(Dreams.Sueno sueno)
            => DreamProtocol.BuildDreamState(sueno, BestiaryOf(sueno.SalaActual,
                   sueno.SalaActual?.EsFinal == true ? Dreams.FinalRulesOf(sueno.Dificultad).BaseLevel : Dreams.MonsterLevel(sueno)));

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
        internal static List<Game.InfiniteDreamMonster> BestiaryOf(Dreams.Sala? sala, int level = 0)
        {
            var beasts = new List<Game.InfiniteDreamMonster>();
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
                beasts.Add(new(cell, fighter.MonsterId, Dreams.IsBoss(fighter.MonsterId)
                    ? Game.InfiniteDreamMonsterKind.Boss : Game.InfiniteDreamMonsterKind.Normal, StatsOf(fighter)));
            }
            return beasts;
        }

        /// <summary>
        /// A monster's characteristics as the bestiary lists them, in the order of the captures:
        /// life, AP, the five resistances that are not zero, initiative, evasion and lock, MP when
        /// it has some, and the two dodges. Evasion and lock are what the fight gives a monster --
        /// nothing, today -- and not a figure of their own.
        /// </summary>
        private static Dictionary<int, int> StatsOf(Fighter monster)
        {
            var stats = new Dictionary<int, int> { [LifePoints] = monster.MaxHP, [ActionPoints] = monster.MaxAP };
            foreach (var (id, value) in new[]
                     {
                         (EarthResistance, monster.EarthResPct), (FireResistance, monster.FireResPct),
                         (WaterResistance, monster.WaterResPct), (AirResistance, monster.AirResPct),
                         (NeutralResistance, monster.NeutralResPct),
                     })
            {
                if (value != 0) stats.Add(id, value);
            }
            stats.Add(Initiative, monster.Initiative);
            stats.Add(Evasion, monster.Otras.GetValueOrDefault(Evasion));
            stats.Add(Lock, monster.Otras.GetValueOrDefault(Lock));
            if (monster.MaxMP != 0) stats.Add(MovementPoints, monster.MaxMP);
            stats.Add(EffectEngine.EsquivaPA, monster.Otras.GetValueOrDefault(EffectEngine.EsquivaPA));
            stats.Add(EffectEngine.EsquivaPM, monster.Otras.GetValueOrDefault(EffectEngine.EsquivaPM));
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
        private static async Task ShopAsync(GameSession session, Dreams.Sueno sueno, int elementId)
        {
            await session.SendAsync(
                ConnectionProtocol.Push(Op.Iwn, ConnectionProtocol.BuildElementInUse(
                    elementId, Dreams.FountainSkill, session.CharacterId)));
            await session.SendAsync(StateOf(sueno));
            await session.SendAsync(ConnectionProtocol.Push(Op.Ixm));
            Console.WriteLine($"[Dreams] The fountain of room {sueno.Actual}: " +
                              $"{sueno.SalaActual?.Offers?.Count ?? 0} offer(s), {sueno.DreamPoints} dream points.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  The loot table and the positions
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>ixq: the loot table of the room one stands in, answered by izo.</summary>
        public static async Task DropTableAsync(GameSession session, byte[] payload)
        {
            var sueno = Dreams.De(session.CharacterId);
            var drops = DropsOf(sueno?.SalaActual, sueno?.Bonus ?? 100);
            await session.SendAsync(
                ConnectionProtocol.Answer(Op.Izo, DreamProtocol.BuildDropTable(drops),
                                          ConnectionProtocol.RequestId(payload)));
            Console.WriteLine($"[Dreams] Loot table of room {sueno?.Actual}: {drops.Count} line(s).");
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
        internal static async Task PositionsAsync(GameSession session, int uid)
        {
            long map = session.MapId;
            long arena = MapManager.ResolveArenaMapId(map);
            var (attackers, defenders) = FightHandler.NextPlacement(map, arena);
            await session.SendAsync(new Game.FightMapInformationResponse(arena, map,
                new Game.FightStartingPositions(attackers.ToArray(), defenders.ToArray())), uid);
            Console.WriteLine($"[Dreams] Positions of map {map}: {attackers.Count} and {defenders.Count} cells.");
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
        public static async Task BuyAsync(GameSession session, byte[] payload)
        {
            byte[]? iym = ConnectionProtocol.ReadPayload(payload, Op.Iym);
            if (iym == null) return;
            Console.WriteLine($"[Dreams] iym (buy, inferred): {Convert.ToHexString(iym).ToLowerInvariant()}");

            var sueno = Dreams.De(session.CharacterId);
            if (sueno == null)
            {
                Console.WriteLine("[Dreams] iym with no dream going.");
                return;
            }

            int which = 0;
            foreach (var f in ProtoMessage.Parse(iym).Fields)
                if (f.FieldNumber == 1 && f.WireType == 0) which = (int)f.VarIntValue;

            var bought = Dreams.Buy(sueno, which, out string refusal);
            if (bought == null)
                Console.WriteLine($"[Dreams] Nothing bought with {which}: {refusal}.");
            else
                Console.WriteLine($"[Dreams] Bought reward {bought.Id} for {bought.Price}: " +
                                  $"{string.Join(", ", bought.Bonuses.Select(b => $"{b.Efecto} of {b.Valor}"))}; " +
                                  $"{sueno.DreamPoints} dream points left.");

            if (bought != null) Persist(sueno);
            await session.SendAsync(StateOf(sueno));
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
        /// none left, and not where there is no group to change. At a fountain or a favour it
        /// renews the offers instead: see <see cref="Dreams.RerollOffers"/>.
        /// </remarks>
        public static async Task AstralStormAsync(GameSession session)
        {
            var sueno = Dreams.De(session.CharacterId);
            if (sueno == null) return;

            var actual = sueno.SalaActual;

            // At a fountain, or a favour not chosen yet, the storm draws the offers again: the
            // client's own text for it, "reiniciar ... todos los artículos de la fuente, así como
            // el favor", and the favour window's reroll button ("ui.infiniteDreams.rerollFavor").
            // Its answer is INFERRED to be the izg with the new offers, as the storm on a fight
            // answers with the izg of the new group.
            if (actual != null && (actual.EsFavor || actual.EsFuente) && session.State.FightId == 0
                && sueno.Tormentas > 0 && Dreams.RerollOffers(sueno, actual))
            {
                sueno.Tormentas--;
                Persist(sueno);
                await session.SendAsync(StateOf(sueno));
                await session.SendAsync(
                    ConnectionProtocol.Push(Op.Izj, DreamProtocol.BuildStorm()));
                Console.WriteLine($"[Dreams] Astral storm on the offers of room {actual.Id}: " +
                                  $"{string.Join(", ", actual.Offers!.Select(o => o.Tag))}; {sueno.Tormentas} left.");
                return;
            }

            string? refusal =
                session.State.FightId != 0 ? "in a fight"
                : sueno.Tormentas <= 0 ? "no storm left"
                : actual == null || actual.Miembros.Count == 0 || actual.Hecha ? "no group to change here"
                : null;
            if (refusal != null)
            {
                Console.WriteLine($"[Dreams] Astral storm of {sueno.CharacterId} refused: {refusal}.");
                await session.SendAsync(StateOf(sueno));
                return;
            }

            Dreams.Reroll(sueno, actual!);
            sueno.Tormentas--;
            await EntrarEnSalaAsync(session, sueno, actual!.Id);

            await session.SendAsync(
                ConnectionProtocol.Push(Op.Izj, DreamProtocol.BuildStorm()));

            Console.WriteLine($"[Dreams] Astral storm of {sueno.CharacterId}: room {actual.Id} rerolled, " +
                              $"{sueno.Tormentas} left.");
        }

        /// <summary>Leaving the dream (iyx) and going back to where he was.</summary>
        public static async Task LeaveAsync(GameSession session, byte[] payload)
        {
            // A guest leaves his host's dream, as the invitation capture's does: the way out (jru),
            // ixg, and the iyb «0801».
            if (IsGuest(session.CharacterId))
            {
                await GuestOutAsync(session);
                await session.SendAsync(
                    ConnectionProtocol.Answer(Op.Iyb, DreamProtocol.BuildLeft(), ConnectionProtocol.RequestId(payload)));
                return;
            }

            var sueno = Dreams.De(session.CharacterId);
            if (sueno == null) return;

            // To where he was BEFORE PRESSING THE BUTTON, not to the map the dream was started
            // from: by then that map is the Astral Plane itself, and sending him back there would
            // leave him going round in circles in the lobby.
            var (mapa, casilla) = Dreams.DeDondeViene(sueno.CharacterId);
            if (mapa == 0) { mapa = sueno.MapaDeVuelta; casilla = sueno.CasillaDeVuelta; }

            if (mapa != 0 && mapa != PlanoAstral)
            {
                await TeleportHandler.ToMapAsync(session, mapa, casilla);
            }
            else
            {
                // With no known place, to the plane: it is where he went in from and it always exists.
                await TeleportHandler.ToMapAsync(session, PlanoAstral, 0);
            }

            await session.SendAsync(new Jondo.Unity.Protocol.Game.InfiniteDreamClearEvent());
            MarkLeft(session.CharacterId);
            await session.SendAsync(new Jondo.Unity.Protocol.Game.NpcsMapQuestStatusUpdateEvent([]));

            await session.SendAsync(
                ConnectionProtocol.Answer(Op.Iyb, DreamProtocol.BuildLeft(),
                                          ConnectionProtocol.RequestId(payload)));

            Console.WriteLine($"[Dreams] {sueno.CharacterId} leaves the dream in room " +
                              $"{sueno.Actual} with {sueno.DreamPoints} dream point(s).");

            // Leaving keeps the dream: the well offers it to continue, in the captures and here.
            // Only its groups leave their maps -- and its guests, who were in it with him.
            Dreams.Unplant(sueno);
            Persist(sueno);
            await SendGuestsOutAsync(sueno.CharacterId);
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

            /// <summary>The turn the last wave fell on: the ladder's «Turno global».</summary>
            public int ClearedAtRound { get; set; }
        }

        private static readonly ConcurrentDictionary<long, FinalFight> _finales = new();

        /// <summary>
        /// A fight has just been built in a dream's room. Its two passives whose spell does
        /// nothing are done here (DreamCatalogue's special): with Viento a Favor the dream's side
        /// plays first, with Suelta it is placed anywhere -- its placement cells every cell of the
        /// arena the monsters' are not. And on the dream's last room it is the Fin du rêve: its
        /// first wave is brought to its level, and its waves are counted from here.
        /// </summary>
        internal static void OnFightCreated(long characterId, Jondo.Unity.World.Fights.FightInstance fight)
        {
            if (!Dreams.IsDreamMap(fight.RoleplayMapId)) return;
            var sueno = DreamOf(fight) ?? Dreams.De(characterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || sala.MapaDeLaSala != fight.RoleplayMapId) return;

            // A room's monsters at the dream's level: "votre niveau et celui des monstres augmentent".
            if (!sala.EsFinal)
                foreach (var monster in fight.Rojo) Dreams.ScaleTo(monster, Dreams.MonsterLevel(sueno));

            if (DreamCatalogue.Holds(sueno, DreamCatalogue.AlliesPlayFirst)) fight.BlueTakesInitiative = true;
            if (!sala.EsFinal)
                foreach (var monster in fight.Rojo) Dreams.AsTrialWants(monster, sueno);
            if (DreamCatalogue.Holds(sueno, DreamCatalogue.PlaceAnywhere))
            {
                var red = fight.RedPlacementCells.ToHashSet();
                var anywhere = FightHandler.PlacementGround(fight.MapId).Where(c => !red.Contains(c)).OrderBy(c => c).ToList();
                if (anywhere.Count > 0) fight.SetPlacementCells(anywhere, fight.RedPlacementCells.ToList());
            }

            if (!sala.EsFinal) return;

            var rules = Dreams.FinalRulesOf(sueno.Dificultad);
            foreach (var monster in fight.Rojo)
            {
                Dreams.ScaleTo(monster, rules.BaseLevel);
                Dreams.AsTrialWants(monster, sueno);
            }
            _finales[fight.FightId] = new FinalFight { CharacterId = sueno.CharacterId, Rules = rules };
            Console.WriteLine($"[Dreams] Fin du rêve of {sueno.CharacterId}: wave 1 at level {rules.BaseLevel}, " +
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
            state.ClearedAtRound = fight.RoundNumber;
            if (state.Rules.MaxWaves > 0 && state.Cleared >= state.Rules.MaxWaves) return null;

            int wave = state.Cleared + 1;
            int level = state.Rules.BaseLevel + state.Rules.Step * state.Cleared;
            Console.WriteLine($"[Dreams] Fin du rêve: wave {state.Cleared} down, wave {wave} at level {level}.");
            var dream = Dreams.De(state.CharacterId);
            return (Dreams.FinalWave(wave, dream != null ? Dreams.DiceOf(dream, "wave", wave) : new Random(), DreamTrials.Of(dream)),
                    level, wave);
        }

        /// <summary>
        /// What a fight in a dream's room pays, worked out before anybody is shown the end: the
        /// dream's difficulty, the row and loot bonus of the room, and -- at the Fin du rêve, once
        /// it is won or its minimum of waves has fallen -- the dream fragments of the waves that
        /// fell. Null for a fight that is not a dream's.
        /// </summary>
        internal sealed record DreamPay(int Difficulty, int Row, int LootBonus, bool Finished, int Waves, int Fragments,
                                        bool Souvenir = false, int Trial = 0);

        internal static DreamPay? PayOf(Jondo.Unity.World.Fights.FightInstance fight, bool won)
        {
            var sueno = DreamOf(fight);
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
                                finished && !sueno.FromSouvenir ? Dreams.FragmentsFor(sueno.Dificultad, waves) : 0,
                                sueno.FromSouvenir, DreamTrials.Of(sueno)?.Id ?? 0);
        }

        /// <summary>
        /// The dream a fight is fought in: the first of its people's whose room is on the fight's
        /// map. Null for a fight that is not a dream's.
        /// </summary>
        internal static Dreams.Sueno? DreamOf(Jondo.Unity.World.Fights.FightInstance fight)
        {
            if (fight == null || !Dreams.IsDreamMap(fight.RoleplayMapId)) return null;
            foreach (var fighter in fight.Azul.Concat(fight.Rojo))
            {
                if (fighter.IsMonster) continue;
                var dream = Dreams.De(fighter.Id);
                if (dream?.SalaActual?.MapaDeLaSala == fight.RoleplayMapId) return dream;
            }
            return null;
        }

        /// <summary>
        /// The dream's state to its owner, wherever the change came from -- a charge spent by
        /// another of its people, say. Only on a dream's map, as <see cref="RefrescarEstadoAsync"/>.
        /// </summary>
        internal static async Task SendStateToOwnerAsync(Dreams.Sueno sueno)
        {
            var owner = SessionRegistry.FindByCharacter(sueno.CharacterId);
            if (owner == null || !Dreams.IsDreamMap(owner.State.MapId)) return;
            try
            {
                await owner.SendAsync(StateOf(sueno));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dreams] The state could not reach {sueno.CharacterId}: {ex.Message}");
            }
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
        internal static (long MapOut, int CellOut, string? Notice, bool Ended, Game.InfiniteDreamEndEvent? Result) AfterTheFight(
            GameSession session, Jondo.Unity.World.Fights.FightInstance fight, bool won)
        {
            if (!Dreams.IsDreamMap(fight.RoleplayMapId)) { _finales.TryRemove(fight.FightId, out _); return (0, 0, null, false, null); }

            // A guest's fight is his host's: he goes back to the room, or out when the dream is over.
            if (_dreamOver.TryRemove(session.CharacterId, out _))
            {
                var (guestMap, guestCell) = GuestWayOut(session.CharacterId);
                return (guestMap, guestCell, null, true, null);
            }
            if (IsGuest(session.CharacterId)) return (0, 0, null, false, null);

            _finales.TryRemove(fight.FightId, out var final);
            var sueno = Dreams.De(session.CharacterId);
            var sala = sueno?.SalaActual;
            if (sueno == null || sala == null || sala.MapaDeLaSala != fight.RoleplayMapId) return (0, 0, null, false, null);

            // The time it took is the time of its fights: the result's "cumulatedDuration".
            sueno.FightMs += Math.Max(0, (long)(DateTime.UtcNow - fight.StartedAt).TotalMilliseconds);

            // And who fought it, for the ladder: only who has fought them all.
            var fought = PlayersOf(fight);
            sueno.Regulars = sueno.Regulars == null ? fought : sueno.Regulars.Intersect(fought).ToList();

            if (final != null)
            {
                int cleared = final.Cleared;
                if (won || cleared >= final.Rules.MinWaves)
                {
                    // The dream fragments of the waves were paid with the fight's spoils
                    // (FightHandler.PlanRewards); the notice says how many.
                    int fragments = sueno.FromSouvenir ? 0 : Dreams.FragmentsFor(sueno.Dificultad, cleared);
                    var result = ResultOf(sueno, fight, won: true, final);
                    RankTheDream(sueno, cleared, final.ClearedAtRound);

                    // A trial won: «Vencer en el combate final de la prueba» (EJ) for whoever fought it.
                    if (DreamTrials.Of(sueno) is { Achievement: > 0 } trial)
                        foreach (long who in fought) CountTrialWon(who, trial.Achievement);
                    var (map, cell) = End(sueno);
                    return (map, cell, CommandTexts.Get(session.State.Language, "dream.completed", cleared, fragments), true, result);
                }
            }

            if (won)
            {
                Persist(sueno);
                return (0, 0, null, false, null);
            }

            if (sueno.Arena > 0)
            {
                // A training dream's sands are never spent: "un nombre infini".
                if (!sueno.Training) sueno.Arena--;
                sueno.SpentSands = true;
                Persist(sueno);
                return (0, 0, CommandTexts.Get(session.State.Language, "dream.arena"), false, null);
            }

            var lost = ResultOf(sueno, fight, won: false, final);
            var (outMap, outCell) = End(sueno);
            return (outMap, outCell, CommandTexts.Get(session.State.Language, "dream.over"), true, lost);
        }

        /// <summary>The characters who fought a fight: no monster, summon, illusion or bot.</summary>
        /// <summary>A trial's achievement counted for one of its people, in his own session; looked at back on the map.</summary>
        private static void CountTrialWon(long characterId, int achievement)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            _ = Achievements.CountAsync(session, "EJ", achievement, defer: true);
        }

        private static List<long> PlayersOf(Jondo.Unity.World.Fights.FightInstance fight)
            => fight.Azul.Concat(fight.Rojo)
                    .Where(f => !f.IsMonster && !f.EsInvocado && !f.EsIlusion && !f.IsBot)
                    .Select(f => f.Id)
                    .Distinct()
                    .ToList();

        /// <summary>
        /// A dream won: its line on the ladder window's dreams tab for each of its people who fought
        /// every one of its fights, when it is better than his best (Ladders). Neither a souvenir's
        /// dream nor one skipped through is ranked: «Los recuerdos y las pruebas de sueño no aparecen».
        /// </summary>
        private static void RankTheDream(Dreams.Sueno sueno, int waves, int turn)
        {
            if (sueno.FromSouvenir || sueno.SkippedFights > 0 || sueno.Regulars == null) return;
            var now = DateTime.UtcNow;
            var kept = Ladders.RecordDream(sueno.Regulars.Select(id =>
                new Ladders.DreamRecord(id, id == sueno.CharacterId, sueno.Dificultad, waves, turn, now)));
            if (kept.Count > 0)
                Console.WriteLine($"[Dreams] On the ladder: intensity {sueno.Dificultad}, {waves} wave(s), turn {turn}, " +
                                  $"for {string.Join(", ", kept.Select(k => k.CharacterId))}.");
        }

        /// <summary>
        /// The result window of a dream that ends (iya, DreamProtocol.BuildResult): its people --
        /// the characters of the fight that ended it --, and the last wave of its end and the
        /// round it fell on. How many dreams each has won is not kept: one, this one -- INFERRED.
        /// </summary>
        private static Game.InfiniteDreamEndEvent ResultOf(Dreams.Sueno sueno, Jondo.Unity.World.Fights.FightInstance fight, bool won, FinalFight? final)
        {
            var players = fight.Azul.Concat(fight.Rojo)
                               .Where(f => !f.IsMonster && !f.EsInvocado && !f.EsIlusion && !f.IsBot)
                               .Select(f => new DreamProtocol.ResultPlayer(f.Id, f.Name ?? "", f.Sex, f.Breed, 1))
                               .ToList();
            return DreamProtocol.BuildResult(sueno, won, players, final?.Cleared ?? 0, final?.ClearedAtRound ?? 0);
        }

        /// <summary>A dream over: forgotten, off the base, and where the player goes out to.</summary>
        private static (long Map, int Cell) End(Dreams.Sueno sueno)
        {
            var (mapa, casilla) = Dreams.DeDondeViene(sueno.CharacterId);
            if (mapa == 0) { mapa = sueno.MapaDeVuelta; casilla = sueno.CasillaDeVuelta; }
            if (mapa == 0 || Dreams.IsDreamMap(mapa)) { mapa = PlanoAstral; casilla = 0; }

            Dreams.Olvidar(sueno.CharacterId);
            DatabaseManager.DeleteDream(sueno.CharacterId);
            Console.WriteLine($"[Dreams] The dream of {sueno.CharacterId} is over; out to map {mapa}.");
            _ = DreamOverForGuestsAsync(sueno.CharacterId);
            return (mapa, casilla);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Dreams in a group
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Who is in somebody else's dream: guest → the dream's owner.</summary>
        private static readonly ConcurrentDictionary<long, long> _guests = new();

        /// <summary>The invitations waiting for their answer: guest → the owner, until when.</summary>
        private static readonly ConcurrentDictionary<long, (long Owner, DateTime Until)> _invited = new();

        /// <summary>Guests whose dream ended under them in a fight: they leave with its end.</summary>
        private static readonly ConcurrentDictionary<long, bool> _dreamOver = new();

        /// <summary>«No puedes entrar en el pozo de los sueños porque ya se ha invitado a tres aventureros».</summary>
        public const int MaxGuests = 3;

        /// <summary>
        /// The iyd's f2: -300 in both of «recibir invitacion a sueños», the popup's countdown
        /// (fyo.bjcv). The invitation is kept as long.
        /// </summary>
        public const int InvitationSeconds = 300;

        // The invitations' info messages (InfoMessagesDataRoot, type 1).
        private const int Warning = 1, OtherInvitation = 582, DreamFull = 585, BadMap = 592, InAFight = 593,
                          OnlyTheLeader = 595;

        public static bool IsGuest(long characterId) => _guests.ContainsKey(characterId);

        /// <summary>The owner of the dream a guest is in, or 0.</summary>
        public static long HostOf(long characterId) => _guests.TryGetValue(characterId, out long host) ? host : 0;

        public static List<long> GuestsOf(long owner) => _guests.Where(g => g.Value == owner).Select(g => g.Key).ToList();

        /// <summary>
        /// Only the dream's owner starts its fights: «No puedes iniciar un combate en el pozo de los
        /// sueños si no eres jefe de grupo» (1/595). True when the character in session may not.
        /// </summary>
        internal static async Task<bool> GuestMayNotFightAsync(GameSession session)
        {
            if (!Dreams.IsDreamMap(session.MapId) || !IsGuest(session.CharacterId)) return false;
            await session.SendAsync(
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(Warning, OnlyTheLeader)));
            return true;
        }

        /// <summary>
        /// «Invitar al grupo» (iwt, empty; InfiniteDreamTrackingUi.InviteGroup): every member of the
        /// owner's party gets «{0} te invita a que te unas a su pozo de los sueños.» (iyd { f1 his
        /// name, f2 -300 }), the capture's two -- unless he is fighting (1/593), on a map that does
        /// not let him (1/592) or has another invitation waiting (1/582).
        /// </summary>
        public static async Task InviteGroupAsync(GameSession host)
        {
            long me = host.CharacterId;
            var sueno = Dreams.De(me);
            var party = Managers.Parties.Of(me);
            if (sueno?.SalaActual == null || party == null || !Dreams.IsDreamMap(host.MapId)) return;

            string name = host.State.CharacterName;
            var now = DateTime.UtcNow;
            foreach (long member in party.Members.ToList())
            {
                if (member == me || IsGuest(member)) continue;
                var session = SessionRegistry.FindByCharacter(member);
                if (session == null) continue;

                int refusal = session.State.IsInFight ? InAFight
                            : Dreams.IsDreamMap(session.State.MapId) || Managers.Merkasako.IsHavenBag(session.State.MapId) ? BadMap
                            : _invited.TryGetValue(member, out var waiting) && waiting.Until > now && waiting.Owner != me ? OtherInvitation
                            : 0;
                if (refusal != 0)
                {
                    await session.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(Warning, refusal, name)));
                    continue;
                }

                _invited[member] = (me, now.AddSeconds(InvitationSeconds));
                await session.SendAsync(ConnectionProtocol.Push(Op.Iyd,
                    Pb.New().Str(1, name).Var(2, -InvitationSeconds).Build()));
            }
            Console.WriteLine($"[Dreams] {name} invites his party into his dream.");
        }

        /// <summary>
        /// The invitation's answer (ixk { f1 accepts }; its popup's buttons): a yes takes the guest
        /// into the owner's room with the owner's dream state (izg), as «recibir invitacion a sueños»
        /// shows after its ixk -- unless the dream already has its three guests (1/585).
        /// </summary>
        public static async Task AnswerInvitationAsync(GameSession session, byte[] payload)
        {
            long me = session.CharacterId;
            byte[]? ixk = ConnectionProtocol.ReadPayload(payload, Op.Ixk);
            if (ixk == null || !_invited.TryRemove(me, out var invitation) || invitation.Until < DateTime.UtcNow) return;
            bool accepts = ProtoMessage.Parse(ixk).Fields.Any(f => f.FieldNumber == 1 && f.WireType == 0 && f.VarIntValue != 0);
            if (!accepts) return;

            var owner = Dreams.De(invitation.Owner);
            if (owner?.SalaActual == null || SessionRegistry.FindByCharacter(invitation.Owner) == null) return;
            if (session.State.IsInFight) return;
            if (GuestsOf(invitation.Owner).Count >= MaxGuests)
            {
                await session.SendAsync(
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(Warning, DreamFull)));
                return;
            }

            if (!Dreams.IsDreamMap(session.MapId)) Dreams.RecordarDeDondeViene(me, session.MapId, session.State.CellId);
            _guests[me] = invitation.Owner;
            Console.WriteLine($"[Dreams] {session.State.CharacterName} joins the dream of {invitation.Owner}.");
            await JoinRoomAsync(session, owner);
        }

        /// <summary>A guest into his host's room: the host's dream state (izg) and the room's map.</summary>
        private static async Task JoinRoomAsync(GameSession guest, Dreams.Sueno owner)
        {
            var sala = owner.SalaActual;
            if (sala == null || sala.MapaDeLaSala == 0) return;
            _enElSueno[guest.CharacterId] = true;
            await guest.SendAsync(StateOf(owner));
            await TeleportHandler.ToMapAsync(guest, sala.MapaDeLaSala, 0);
        }

        /// <summary>
        /// The owner went into another room: his guests with him, each with the state and the map
        /// change -- the invitation capture's guest follows his host room after room without asking.
        /// </summary>
        private static async Task BringGuestsAsync(Dreams.Sueno owner)
        {
            foreach (long guest in GuestsOf(owner.CharacterId))
            {
                var session = SessionRegistry.FindByCharacter(guest);
                if (session?.Stream == null || session.State.IsInFight) continue;
                await JoinRoomAsync(session, owner);
            }
        }

        /// <summary>Where a guest goes out to: where he came in from, or the Plano Astral.</summary>
        private static (long Map, int Cell) GuestWayOut(long characterId)
        {
            var (mapa, casilla) = Dreams.DeDondeViene(characterId);
            if (mapa == 0 || Dreams.IsDreamMap(mapa)) { mapa = PlanoAstral; casilla = 0; }
            return (mapa, casilla);
        }

        /// <summary>A guest out of the dream: back where he came from, the interface closed (ixg).</summary>
        private static async Task GuestOutAsync(GameSession guest)
        {
            _guests.TryRemove(guest.CharacterId, out _);
            var (mapa, casilla) = GuestWayOut(guest.CharacterId);
            _enElSueno[guest.CharacterId] = false;
            await TeleportHandler.ToMapAsync(guest, mapa, casilla);
            await guest.SendAsync(new Jondo.Unity.Protocol.Game.InfiniteDreamClearEvent());
        }

        /// <summary>The owner left his dream: his guests leave it too.</summary>
        private static async Task SendGuestsOutAsync(long owner)
        {
            foreach (long guest in GuestsOf(owner))
            {
                var session = SessionRegistry.FindByCharacter(guest);
                if (session?.Stream == null || session.State.IsInFight) { _guests.TryRemove(guest, out _); continue; }
                await GuestOutAsync(session);
            }
        }

        /// <summary>
        /// The dream is over: a guest still fighting leaves with his fight's end (AfterTheFight), the
        /// others now.
        /// </summary>
        private static async Task DreamOverForGuestsAsync(long owner)
        {
            foreach (long guest in GuestsOf(owner))
            {
                var session = SessionRegistry.FindByCharacter(guest);
                _guests.TryRemove(guest, out _);
                if (session?.Stream == null) continue;
                if (session.State.IsInFight) { _dreamOver[guest] = true; continue; }
                await GuestOutAsync(session);
            }
        }

        /// <summary>For tests: every guest and invitation forgotten.</summary>
        internal static void ForgetGroups()
        {
            _guests.Clear();
            _invited.Clear();
            _dreamOver.Clear();
        }

        /// <summary>
        /// ".sueno": the dream carried forward by <see cref="Dreams.SkipTo"/>, and the player into
        /// the room it reached -- the groups it left planted gone first, and, when he was out in
        /// the world, that map noted as the way back, for the dream to send him there at its end.
        /// </summary>
        internal static async Task<(Dreams.SkipOutcome Outcome, Dreams.Sala? Room, int Skipped)> SkipToAsync(
            GameSession session, Dreams.Sueno sueno, int? row)
        {
            var result = Dreams.SkipTo(sueno, row);
            if (result.Outcome != Dreams.SkipOutcome.Done || result.Room == null) return result;
            sueno.SkippedFights += Math.Max(1, result.Skipped);

            Dreams.Unplant(sueno);
            if (!Dreams.IsDreamMap(session.MapId) && session.MapId != PlanoAstral)
                Dreams.RecordarDeDondeViene(sueno.CharacterId, session.MapId, session.State.CellId);

            Console.WriteLine($"[Dreams] {sueno.CharacterId} skips to room {result.Room.Id} (row {result.Room.Fila}): " +
                              $"{result.Skipped} fight(s) counted as won on the way.");
            await EntrarEnSalaAsync(session, sueno, result.Room.Id);
            return result;
        }

        /// <summary>Whether the last map each character loaded was one of the dream's.</summary>
        private static readonly ConcurrentDictionary<long, bool> _enElSueno = new();

        /// <summary>The dream's interface already closed by whoever sent the ixg.</summary>
        internal static void MarkLeft(long characterId) => _enElSueno[characterId] = false;

        /// <summary>
        /// A map loaded, however the player got there. Out of the dream's maps by any way but its
        /// own exit -- the Merkasako and its zaap, a teleport, a command -- the client still has the
        /// dream's interface up, and only the ixg takes it down: the real server sends it on its
        /// own, with no iyx before it, in "sueño infinito largo" and "recibir invitacion a
        /// sueños". The dream is kept, as the exit keeps it, and its groups leave their maps.
        /// </summary>
        public static async Task OnMapLoadedAsync(GameSession session)
        {
            long yo = session.CharacterId;
            bool aqui = Dreams.IsDreamMap(session.MapId);
            bool antes = _enElSueno.TryGetValue(yo, out bool estaba) && estaba;
            _enElSueno[yo] = aqui;

            // And back onto a dream's map from outside it -- H out of the Merkasako lands where it
            // was opened -- is the same as waking there: the room again, its group planted back
            // and the interface up, or the Plano Astral when there is no dream to go back to.
            // Without it the room stood empty, and a room with no group counts as passed.
            if (!antes && aqui)
            {
                await OnWorldEntryAsync(session);
                return;
            }
            if (!antes || aqui) return;

            await session.SendAsync(new Jondo.Unity.Protocol.Game.InfiniteDreamClearEvent());
            _guests.TryRemove(yo, out _);
            var sueno = Dreams.De(yo);
            if (sueno != null)
            {
                Dreams.Unplant(sueno);
                Persist(sueno);
                await SendGuestsOutAsync(yo);
            }
            Console.WriteLine($"[Dreams] {yo} left the dream's maps without its exit: the interface closed.");
        }

        /// <summary>Saves a dream, so a disconnection or a restart does not lose it.</summary>
        internal static void Persist(Dreams.Sueno sueno)
            => DatabaseManager.SaveDream(sueno.CharacterId, Dreams.Serialize(sueno));

        /// <summary>
        /// The world entered on a dream's map: the dream again -- its state, its room's group, its
        /// panel -- or, with no dream to go back to, the Plano Astral. A player who logged in
        /// there after a restart stood in a room with no dream around it and no way out.
        /// </summary>
        public static async Task OnWorldEntryAsync(GameSession session)
        {
            if (!Dreams.IsDreamMap(session.MapId)) return;

            // A guest back while his host still dreams: to the host's room.
            if (Dreams.De(HostOf(session.CharacterId)) is { SalaActual: not null } hosts)
            {
                await JoinRoomAsync(session, hosts);
                return;
            }

            var sueno = Dreams.De(session.CharacterId);
            if (sueno?.SalaActual == null)
            {
                Console.WriteLine($"[Dreams] {session.CharacterId} woke in a dream's map with no dream: " +
                                  "to the Plano Astral.");
                await TeleportHandler.ToMapAsync(session, PlanoAstral, 0);
                return;
            }

            Console.WriteLine($"[Dreams] {session.CharacterId} back in room {sueno.Actual} of the dream.");
            await EntrarEnSalaAsync(session, sueno, sueno.Actual);
        }
    }
}
