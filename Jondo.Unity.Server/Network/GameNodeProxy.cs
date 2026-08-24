using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Google.Protobuf;
using Jondo.Unity.Launcher.Handlers;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Network
{
    public static class GameNodeProxy
    {
        private static TcpListener? _tcpListener;
        private static bool _isRunning;

        private static CancellationTokenSource? _cts;

        /// <summary>
        /// Las conexiones vivas ahora mismo, una por cliente. Es lo que permite mandarle algo a
        /// uno concreto o a todos los de un mapa sin pasar el socket de mano en mano.
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, GameSession>
            SesionesVivas = new System.Collections.Concurrent.ConcurrentDictionary<Guid, GameSession>();

        public static void Start(int port)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();

            _tcpListener = new TcpListener(ServerBinding.TcpAddress, port);
            _tcpListener.Start();

            Console.WriteLine($"[+] Emulating Game Node on TCP port {port} (Online)");

            _ = Task.Run(async () =>
            {
                while (_isRunning && _tcpListener != null)
                {
                    try
                    {
                        var client = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                        _ = HandleGameNodeConnection(client);
                    }
                    catch (Exception ex)
                    {
                        if (!_isRunning) break;
                        Console.WriteLine($"[Game Node Accept Error] {ex.Message}");
                    }
                }
            });
        }

        public static void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts?.Cancel();
            _tcpListener?.Stop();
            _tcpListener = null;
        }

        private static async Task HandleGameNodeConnection(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Console.WriteLine($"[+] Client connected to Game Node! ({client.Client.RemoteEndPoint})");
                    var stream = client.GetStream();

                    // La sesión de ESTA conexión, atada al hilo antes de leer nada.
                    //
                    // Sin esto no funcionaba nada: hay 295 sitios que piden SessionContext.State y
                    // no había ni un solo Push en todo el proyecto, así que el primero que pedía
                    // el estado se llevaba una excepción por delante y la conexión se cerraba. Es
                    // el "No game session is bound to the current async flow" que salía nada más
                    // elegir personaje.
                    //
                    // Va aquí y envolviendo el bucle entero porque AsyncLocal se hereda hacia
                    // dentro: todo lo que se espere desde este punto ve la misma sesión sin que
                    // haya que pasarla a mano por doscientas firmas.
                    var sesion = new GameSession(stream);
                    if (!SessionRegistry.Register(sesion))
                    {
                        Console.WriteLine("[Game Node] Rejected connection: the 8-client limit is reached.");
                        return;
                    }
                    SesionesVivas[sesion.Id] = sesion;

                    try
                    {
                        using (SessionContext.Push(sesion))
                        {
                            byte[] payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
                            if (payload == null) return;

                            string payloadStr = Encoding.UTF8.GetString(payload);
                            await HandleGameNodeSessionAsync(sesion, stream, payload, payloadStr);
                        }
                    }
                    finally
                    {
                        if (sesion.IsInWorld)
                        {
                            try
                            {
                                await SessionRegistry.BroadcastToMapAsync(
                                    sesion.MapId,
                                    ConnectionProtocol.BuildActorLeft(sesion.CharacterId),
                                    sesion.Id);
                            }
                            catch { }
                            sesion.LeaveWorld();
                        }

                        // Guardar al cerrar, que no se hacía en ninguna parte: hasta ahora el
                        // personaje sólo se escribía cuando algo lo provocaba de paso, así que
                        // cerrar el cliente sin más perdía la última posición y los kamas.
                        if (sesion.State.CharacterId > 0)
                        {
                            try
                            {
                                using (SessionContext.Push(sesion)) DatabaseManager.SaveCurrentCharacter();
                                Console.WriteLine($"[Game Node] {sesion.State.CharacterName} saved on the " +
                                                  $"way out: map {sesion.State.MapId}, cell {sesion.State.CellId}.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Game Node] Could not save on disconnect: {ex.Message}");
                            }
                        }

                        SesionesVivas.TryRemove(sesion.Id, out _);
                        SessionRegistry.Unregister(sesion);
                        Console.WriteLine($"[Game Node] Session {sesion.Id} closed; " +
                                          $"{SesionesVivas.Count} still connected.");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[-] Game Node Connection Closed: {e.Message}");
                }
            }
        }

        public static async Task HandleGameNodeSessionAsync(GameSession session, NetworkStream stream,
                                                            byte[] firstPayload, string firstPayloadStr)
        {
            if (!ReferenceEquals(session.Stream, stream))
                throw new InvalidOperationException("The game stream does not belong to this session.");

            byte[] payload = firstPayload;
            string payloadStr = firstPayloadStr;
            bool isAuthenticated = false;
            bool hasSentIthBurst = false;

            // The map block goes out once per entry into the world. kqo, which used to trigger it,
            // turns out to be a heartbeat that repeats every five seconds.
            bool hasSentMapBlock = false;

            // Account and server for this session, resolved when redeeming the ticket the client
            // presents in kqz. Without this the character list would be the same for everyone.
            long sessionAccountId = 0;
            int sessionServerId = 0;

            if (payloadStr.Contains(Op.Uri(Op.Lqu)) || payloadStr.Contains(Op.Uri(Op.Hoy)) || payloadStr.Contains(Op.Uri(Op.Hmt)) || payloadStr.Contains("type.ankama.com/knx"))
            {
                byte[] hoyFrame = NetworkEnvelope.ConvertHexStringToByteArray("1D-1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-68-6F-79-12-04-08-1E-10-01");
                await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, hoyFrame);
                Console.WriteLine("[Game Node 3.6.10.10] Sent Game Server Hello (hoy)");
            }

            while (_isRunning)
            {
                // Never trust a context inherited across the lifetime of a connection here.
                // Several connections execute this loop concurrently, and every packet must be
                // rebound from the GameSession that OWNS this exact NetworkStream before any
                // GameState/SessionContext facade is read. This is deliberately repeated for
                // every packet rather than relying on the outer connection scope.
                using var packetSession = SessionContext.Push(session);

                GameServerProxy.LogTraffic("GAME_C->S", payload, payload.Length);

                if (payloadStr.Contains(Op.Uri(Op.Kqz)))
                {
                    // The client presents the ticket handed to it by the connection server. From
                    // here on the session knows which account it serves, and answers it with the
                    // burst that ends in the character list.
                    isAuthenticated = true;
                    if (HandleTicketPresentation(payload, ref sessionAccountId, ref sessionServerId))
                    {
                        var characters = DatabaseManager.GetCharactersByAccountId(sessionAccountId, sessionServerId);
                        foreach (byte[] frame in ConnectionProtocol.BuildWelcomeBurst(characters))
                        {
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
                        }
                        Console.WriteLine($"[Game Node] Burst sent to account {sessionAccountId}: " +
                                          $"{characters.Count} character(s) on server {sessionServerId}.");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("[Game Node] Invalid or expired ticket. Closing the session.");
                        Console.ResetColor();
                        return;
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/krt"))
                {
                    // Comes along with kqz and expects no response of its own.
                }
                else if (payloadStr.Contains("type.ankama.com/kqq"))
                {
                    // Going back to the character list or to the server list. In the real capture
                    // the server only answers kqr and it is the client that closes the connection
                    // and redoes the handshake with the connection server. Both ways back are
                    // handled the same: the client decides which of the two screens it lands on.
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Kqr, BuildKqrPayload()));
                    // Si se sale estando en un combate, hay que devolverlo al mapa de superficie:
                    // el de arena es de instancia y quedarse ahí es quedarse encerrado.
                    FightHandler.LeaveFight();
                    if (SessionContext.Current.IsInWorld)
                    {
                        await SessionRegistry.BroadcastToMapAsync(
                            SessionContext.State.MapId,
                            ConnectionProtocol.BuildActorLeft(SessionContext.State.CharacterId),
                            SessionContext.Current.Id);
                        SessionContext.Current.LeaveWorld();
                    }
                    hasSentMapBlock = false;
                    Console.WriteLine("[Game Node] Client is going back: sent kqr and released the session.");
                }
                else if (!isAuthenticated && (payloadStr.Contains(Op.Uri(Op.Hmt)) || payloadStr.Contains(Op.Uri(Op.Ise)) || payloadStr.Contains("type.ankama.com/jtk") || payloadStr.Contains("type.ankama.com/knx") || payloadStr.Contains(Op.Uri(Op.Hoy))))
                {
                    isAuthenticated = true;
                    await CharacterSelectionHandler.HandleAuthRequest(stream, payload, payloadStr);
                }
                // Careful: kqu no longer belongs here. In 3.6.10.10 it is a message pushed by the
                // server inside the welcome burst, not a client request.
                else if (payloadStr.Contains(Op.Uri(Op.Jto)) || payloadStr.Contains("type.ankama.com/kpc") || payloadStr.Contains(Op.Uri(Op.Ksx)) || payloadStr.Contains(Op.Uri(Op.Kpa)))
                {
                    await CharacterSelectionHandler.HandleCharacterListRequest(stream, payload, payloadStr, sessionAccountId, sessionServerId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kvz)))
                {
                    // Crear un personaje.
                    await CharacterCreationHandler.CreateAsync(stream, payload, sessionAccountId,
                                                               sessionServerId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kvk)))
                {
                    // El botón del dado: un nombre al azar.
                    await CharacterCreationHandler.SuggestNameAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kvw)) || payloadStr.Contains(Op.Uri(Op.Ksl))
                         || payloadStr.Contains(Op.Uri(Op.Kvl)))
                {
                    // Character selection. We check that it belongs to this session's account:
                    // the client picks the id, so it cannot be trusted.
                    //
                    // El kvl es el mismo paso pero recién creado el personaje: en la captura de una
                    // creación que sale bien, el cliente manda kvl justo detrás del kvi y entra al
                    // mundo sin pasar por la lista.
                    if (!CharacterSelectionHandler.HandleCharacterSelectionRequest(payload, sessionAccountId))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("[Game Node] Character selection rejected. Closing the session.");
                        Console.ResetColor();
                        return;
                    }

                    // Block 1 of the world entry, replayed from the 3.6.10.10 capture with the
                    // identity rebuilt from the database. The real server stops here and waits
                    // for the client to confirm with lqc before sending anything else.
                    var chosen = DatabaseManager.GetCharacterById(GameState.CharacterId);
                    if (chosen == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[Game Node] Character {GameState.CharacterId} is not in the database.");
                        Console.ResetColor();
                        return;
                    }

                    // A fresh entry into the world: the map block is owed again, and the
                    // inventory is read from the database for this character.
                    hasSentMapBlock = false;
                    Managers.Equipment.LoadFrom(chosen.Id);
                    Managers.SpellChoices.LoadFrom(chosen.Id);

                    SessionContext.Current.EnterWorld();
                    await SessionRegistry.BroadcastToMapAsync(
                        SessionContext.State.MapId,
                        ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                            chosen, SessionContext.State.CellId, SessionContext.State.Orientation,
                            SessionContext.Current.AccountId)),
                        SessionContext.Current.Id);

                    await WorldEntry.SendAfterCharacterAsync(stream, chosen);

                    // Block 2 goes out straight after. In the capture the client asks for it with
                    // lqc, and it does send that lqc here too, only later: it comes once the client
                    // has digested block 1, by which time ours has already sent block 2. Waiting
                    // for it would leave the client without the catalogues for no reason.
                    await WorldEntry.SendAfterConfirmAsync(stream, chosen);
                }
                else if ((payloadStr.Contains("type.ankama.com/jrh")
                          || payloadStr.Contains(Op.Uri(Op.Kmv)))
                         && FightHandler.PendingPreparation() != null)
                {
                    // En combate, quien está en el mapa no se manda con un jss: son las jxg de la
                    // preparación, y sólo cuando el cliente las pide. Ese es el orden de la
                    // captura, y mandarlas antes del cambio de mapa hace que las descarte.
                    //
                    // Y las pide con kmv, no con jrh. Al cargar un mapa normal el cliente manda los
                    // dos, así que enganchar el jrh bastaba ahí; pero al entrar en combate manda
                    // ijm y kmv y nada más, y kmv estaba en la lista de mensajes que se ignoran sin
                    // decir nada. Por eso el combate salía en el registro del servidor y en pantalla
                    // no pasaba nada.
                    await FightHandler.SendPreparationAsync(stream, FightHandler.PendingPreparation()!);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.BuildActorsComplete());
                }
                else if (payloadStr.Contains("type.ankama.com/jrh"))
                {
                    // Peleando, el mapa ya está puesto: mandarle el jss del mapa de superficie lo
                    // sacaría del combate.
                    //
                    // Y esto NO puede ser un continue. La lectura de la trama siguiente está al
                    // FINAL del cuerpo de este while, así que saltar a la condición se la salta:
                    // el payload sigue siendo el mismo, se vuelve a entrar por esta misma rama, y
                    // se vuelve a saltar. Para siempre, sin un solo await por medio, o sea girando
                    // a plena máquina y sin volver a leer un byte de ese cliente nunca más.
                    var here = GameState.IsInFight
                        ? null
                        : DatabaseManager.GetCharacterById(GameState.CharacterId);

                    // The client asks who is on the map. Without an answer it draws an empty map:
                    // no avatar, no NPCs, no monsters.
                    if (here != null)
                    {
                        byte[] actors = ConnectionProtocol.Push(Op.Jss,
                            ConnectionProtocol.BuildMapActors(GameState.MapId, here,
                                                              GameState.CellId, GameState.Orientation,
                                                              sessionAccountId));
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, actors);

                        // And straight behind it, the mark that says there are no more actors. In
                        // every capture that loads a map lva comes immediately after jss, and
                        // without it the client never counts the map as loaded: two seconds later
                        // it asks again with knm, kno and kny and goes round once more.
                        // Dentro del merkasako van además los muebles y los permisos, que en la
                        // captura salen entre el jss y el lva.
                        if (Managers.Merkasako.IsHavenBag(GameState.MapId))
                        {
                            await MerkasakoHandler.SendFurnitureAsync(stream);
                        }


                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.BuildActorsComplete());

                        Console.WriteLine($"[Game Node] Actors of map {GameState.MapId} sent: " +
                                          $"{here.Name} on cell {GameState.CellId}.");
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lqc)))
                {
                    // lqc es el cliente diciendo que ya ha digerido el primer bloque. Aquí es donde
                    // toca darle el mapa.
                    //
                    // Antes esperábamos al primer kqo, que es el latido y llega cada cinco segundos:
                    // en el registro del cliente real pasaron 4,8 s entre elegir personaje y recibir
                    // el mapa. Ese hueco es el destello de Incarnam vacío antes del fundido a negro:
                    // el cliente ya está en el mundo, no sabe todavía en qué mapa, y mientras tanto
                    // enseña su escena por defecto, que es la de Incarnam. Por eso sonaba también su
                    // música en la pantalla de personajes.
                    Console.WriteLine("[Game Node] Client confirmed with lqc.");
                    if (await SendMapBlockOnceAsync(stream, hasSentMapBlock, Op.Lqc)) hasSentMapBlock = true;
                }
                // ─── 3.6.10.10 world messages. The joi/jos/jpp branches further down belong to
                // an earlier version of the protocol and this client never sends them.
                else if (payloadStr.Contains(Op.Uri(Op.Jrw)))
                {
                    // Andar es el mismo mensaje dentro y fuera del combate. Peleando lo resuelve el
                    // manejador de combate, que además gasta puntos de movimiento; si cayera aquí,
                    // el personaje se movería por el tablero gratis y sin avisar a nadie.
                    //
                    // Va por HandleFightMessageAsync y no directo a WalkAsync: ese es el que coge
                    // el candado de la sesión. Llamando a WalkAsync a pelo, andar era lo ÚNICO del
                    // combate que se saltaba el candado, así que podía cruzarse con el reloj de
                    // turno —que también toca el combate y escribe en el socket— y dejar el estado
                    // a medias. Y de paso hacía inalcanzable la rama del jrw que ya existía dentro
                    // del manejador de combate.
                    if (GameState.IsInFight) await FightHandler.HandleFightMessageAsync(stream, payload, payloadStr);
                    else await WorldMoveHandler.ConfirmMovementAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jqi"))
                {
                    await WorldMoveHandler.AllowMapExitAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jqk)))
                {
                    hasSentMapBlock = true;   // the map block belongs to entering the world, not to this
                    await WorldMoveHandler.ChangeMapAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kum)))
                {
                    await CharacteristicsHandler.SpendAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/kuh"))
                {
                    await CharacteristicsHandler.ResetAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iuk)))
                {
                    await EquipmentHandler.MoveAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ktm)))
                {
                    // Chat. With one player on the server there is nobody else to hand it to, so
                    // it comes straight back to whoever said it — which is also what the real
                    // server does with your own lines, and what makes them appear in the window.
                    byte[]? ktm = ConnectionProtocol.ReadPayload(payload, Op.Ktm);
                    if (ktm != null)
                    {
                        string text = "";
                        int channel = 0;
                        foreach (var f in ProtoMessage.Parse(ktm).Fields)
                        {
                            if (f.FieldNumber == 2 && f.WireType == 2) text = Encoding.UTF8.GetString(f.BytesValue);
                            else if (f.FieldNumber == 3 && f.WireType == 0) channel = (int)f.VarIntValue;
                        }

                        // Los comandos de administración se escriben por aquí, por cualquier canal,
                        // y NO se publican: si el manejador los reconoce, la línea se queda en el
                        // servidor y nunca llega a salir por el chat. Vale para todos los canales
                        // porque lo que decide no es el canal, es el texto.
                        bool consumed = text.Length > 0 &&
                            await CommandHandler.TryHandleAsync(stream, text, channel, sessionAccountId);

                        if (text.Length > 0 && !consumed)
                        {
                            byte[] linea = ConnectionProtocol.Push(Op.Kti,
                                ConnectionProtocol.BuildChatLine(GameState.CharacterName,
                                    GameState.CharacterId, sessionAccountId, text, channel));
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, linea);

                            // Y a los demás. Aquí se acababa: la línea volvía a quien la escribía y
                            // nadie más la veía nunca, que con un solo jugador no se notaba.
                            //
                            // El canal general es el del MAPA, no el del servidor: lo oye quien
                            // está delante. La línea es la misma para todos —lleva dentro el
                            // nombre y el id de quien habla—, así que se reparte tal cual. Los
                            // demás canales (comercio, reclutamiento) son de servidor entero y
                            // todavía no se reparten.
                            int oidos = channel == 0
                                ? await SessionRegistry.BroadcastToMapAsync(
                                      SessionContext.State.MapId, linea, SessionContext.Current.Id)
                                : 0;
                            Console.WriteLine($"[Chat] channel {channel}: {text}" +
                                              (oidos > 0 ? $"   (oído por {oidos} más)" : ""));
                        }
                    }
                }
                // Los grupos. Se invita por nombre y se acepta por id de grupo, asi que cada uno
                // tiene su mensaje; ver Handlers.PartyHandler.
                else if (payloadStr.Contains(Op.Uri(Op.Ime)))
                {
                    await Handlers.PartyHandler.InviteAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ijx)))
                {
                    await Handlers.PartyHandler.AcceptAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iki)))
                {
                    await Handlers.PartyHandler.RefuseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Inh)))
                {
                    await Handlers.PartyHandler.LeaveAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ili)))
                {
                    await Handlers.PartyHandler.KickAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ima)))
                {
                    await Handlers.PartyHandler.PromoteAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ktb)))
                {
                    await Handlers.PrivateMessageHandler.WhisperAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iwo)))
                {
                    // Todos los elementos pasan por el mismo registro; él decide qué acción hay
                    // detrás sin mezclar datos entre mapas ni entre sockets.
                    await InteractiveActionHandler.UseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbn)))
                {
                    // El botón del merkasako, y la tecla H.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.EnterFromOutsideAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbl)))
                {
                    // Cambiarse de decorado dentro del merkasako.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.ChangeThemeAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jbv"))
                {
                    // Abrir el menú de gestión, para colocar muebles.
                    await MerkasakoHandler.OpenEditorAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbg)))
                {
                    // Un trozo de la habitación. Se junta y se guarda al cerrar el menú.
                    MerkasakoHandler.CollectFurniture(payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jbk")
                         || payloadStr.Contains("type.ankama.com/jav")
                         || payloadStr.Contains("type.ankama.com/jaw"))
                {
                    // Cerrar el menú de gestión. Los tres llegan seguidos al aceptar.
                    await MerkasakoHandler.CloseEditorAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kcr)))
                {
                    // Mover un objeto entre la bolsa y el cofre.
                    await ChestHandler.MoveAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/lyk"))
                {
                    // Abrir la ventana de apariencias.
                    await AppearanceHandler.OpenAsync(stream, sessionAccountId);
                }
                else if (payloadStr.Contains("type.ankama.com/lyy"))
                {
                    // El estado de esa ventana.
                    await AppearanceHandler.SendStateAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lys)))
                {
                    // Ponerse una prenda; el hueco lo resuelve el servidor.
                    await AppearanceHandler.WearAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lyf)))
                {
                    // Poner o quitar en un hueco concreto.
                    await AppearanceHandler.AssignAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxg)))
                {
                    // Enseñar u ocultar una prenda.
                    await AppearanceHandler.ToggleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxw)))
                {
                    // El aura.
                    await AppearanceHandler.AuraAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lze)))
                {
                    // Elegir título en la ventana de apariencia. Solo toca el borrador.
                    await WardrobeHandler.ChooseTitleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lwm)))
                {
                    // Elegir ornamento.
                    await WardrobeHandler.ChooseOrnamentAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxs)))
                {
                    // El botón Guardar de esa ventana.
                    await WardrobeHandler.SaveAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iuw)))
                {
                    // Destruir un objeto del inventario.
                    await DestroyItemHandler.DestroyAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/kla"))
                {
                    // El botón de cerrar del diálogo. Va vacío y espera respuesta: khd si lo que
                    // está abierto es el cofre o la tienda de un NPC, kld si es la lista del zaap.
                    //
                    // El cliente manda el kla DOS veces seguidas al cerrar una tienda, con menos de
                    // un milisegundo entre medias, y el servidor real contesta un solo khd. Como el
                    // primero ya deja la tienda cerrada, el segundo cae en el zaap y se va con un
                    // kld que el cliente ignora, igual que hoy.
                    if (ChestHandler.IsOpen) await ChestHandler.CloseAsync(stream);
                    else if (NpcHandler.IsShopOpen) await NpcHandler.CloseShopAsync(stream);
                    else await ZaapTravelHandler.CloseAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Hjc)))
                {
                    // Ha elegido destino en la lista del zaap.
                    hasSentMapBlock = true;   // el bloque del mapa es de entrar al mundo, no de esto
                    await ZaapTravelHandler.TravelAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hmt)))
                {
                    // Cambiar un hechizo por su variante. Antes caía en la lista de mensajes que
                    // se ignoran en silencio, que es por lo que elegir una variante no hacía nada.
                    await SpellHandler.HandleVariantAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Itz)))
                {
                    // Editing a slot of the shortcut bar. The server answers with the very same
                    // entry it was given, y además se apunta dónde quedó: si no, la barra se
                    // rehace igual en cada sesión y lo que el jugador coloque se pierde al salir.
                    //
                    //   itz: f2 { f2: hueco, f6 { f2: hechizo } }, f3: qué barra
                    byte[]? itz = ConnectionProtocol.ReadPayload(payload, Op.Itz);
                    if (itz != null)
                    {
                        RememberShortcut(itz);
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.Ivk, itz));
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kqo)))
                {
                    // kqo is a heartbeat, not a request for the map. The client sends it every five
                    // seconds for as long as it is in the world and the real server answers it with
                    // kqy alone: twenty-four in a row, 5.000 ms apart, in the tutorial capture.
                    //
                    // Answering it with the map block is what made the client reload the world over
                    // and over: the block carries jru, and jru means "load this map". So the block
                    // goes out on the first kqo of the entry and the heartbeat gets its own answer
                    // from then on. The block already opens with a kqy of its own, which is why the
                    // first one is not answered twice.
                    // El lqc suele haberlo mandado ya, así que esto no hace nada; sigue aquí porque
                    // no todo lo que se conecta manda lqc —el cliente de pruebas, sin ir más lejos—
                    // y sin mapa no hay mundo.
                    if (await SendMapBlockOnceAsync(stream, hasSentMapBlock, "primer kqo"))
                    {
                        hasSentMapBlock = true;
                    }
                    else
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.BuildHeartbeatAnswer());
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Loy)))
                {
                    Console.WriteLine("[Game Node] Received loy (World Load Ack) from client. Map loaded successfully. Sending lok and jdj...");
                    
                    // Send lok (SelectedServerData / Game State configuration)
                    byte[] lokBytes = NetworkEnvelope.ConvertHexStringToByteArray("1A-1E-0A-1C-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6C-6F-6B-12-05-10-01-18-CD-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lokBytes);
                    
                    // Send jdj (Server date / Maintenance synchronization)
                    byte[] jdjBytes = NetworkEnvelope.ConvertHexStringToByteArray("12-3A-12-2D-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-64-6A-12-16-12-14-32-30-32-36-2D-30-36-2D-33-30-54-30-35-3A-30-30-3A-30-30-5A-18-FF-FF-FF-FF-FF-FF-FF-FF-FF-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, jdjBytes);
                    
                    Console.WriteLine("[Game Node] Sent lok and jdj status packets successfully.");
                }
                else if (payloadStr.Contains("type.ankama.com/kkn"))
                {
                    Console.WriteLine("[Game Node] Received kkn from client. Sending initialization burst...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKkpMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKkmMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKrbMessage());
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIlcMessage());
                    
                    // Patch joh dynamically with character's map ID
                    byte[] patchedJoh = PatchJohPacket(TransitionPacketsBuilder.BuildJohMessage(), GameState.MapId);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, patchedJoh);
                    
                    int subAreaId = 1;
                    try
                    {
                        var mapInfo = MapManager.GetMapInfo(GameState.MapId);
                        if (mapInfo != null)
                        {
                            subAreaId = mapInfo.SubAreaId;
                        }
                    }
                    catch { }
                    if (subAreaId == 444) subAreaId = 20663;

                    foreach (var lor in TransitionPacketsBuilder.BuildLorList())
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, lor);
                    }
                    
                    // Dynamically send character's real stats (kri)
                    byte[]? updatedKri = StatsHandler.BuildUpdatedKriPacket();
                    if (updatedKri != null)
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, updatedKri);
                    }
                    
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildHmdMessage());
                    
                    foreach (var itp in TransitionPacketsBuilder.BuildItpList())
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, itp);
                    }
                    Console.WriteLine("[Game Node] Initialization burst sent successfully.");
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lpj)))
                {
                    Console.WriteLine("[Game Node] Received lpj from client. Sending lpe response...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildLpeMessage());
                }
                else if (payloadStr.Contains("type.ankama.com/hmv"))
                {
                    Console.WriteLine("[Game Node] Received hmv from client. Sending official hnk and kqm chat channel lists...");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPayloads.hnk);
                    
                    int subAreaId = 1;
                    try
                    {
                        var mapInfo = MapManager.GetMapInfo(GameState.MapId);
                        if (mapInfo != null)
                        {
                            subAreaId = mapInfo.SubAreaId;
                        }
                    }
                    catch { }

                    if (subAreaId == 444)
                    {
                        subAreaId = 20663;
                    }

                    foreach (var kqm in TransitionPayloads.kqmList)
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, kqm);
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/ibt"))
                {
                    if (!hasSentIthBurst)
                    {
                        hasSentIthBurst = true;
                        Console.WriteLine("[Game Node] Received ibt from client. Sending final initialization burst (ith, icg, klt, klp)...");
                        
                        int subAreaId = 1;
                        try
                        {
                            var mapInfo = MapManager.GetMapInfo(GameState.MapId);
                            if (mapInfo != null)
                            {
                                subAreaId = mapInfo.SubAreaId;
                            }
                        }
                        catch { }
                        if (subAreaId == 444) subAreaId = 20663;

                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIcgMessage());
                        
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildIthMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKltMessage());
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, TransitionPacketsBuilder.BuildKlpMessage());
                        Console.WriteLine("[Game Node] Final initialization burst sent successfully.");
                    }
                    else
                    {
                        Console.WriteLine("[Game Node] Received duplicate ibt from client. Ignored.");
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kkr)) || payloadStr.Contains(Op.Uri(Op.Jqf)) || payloadStr.Contains("type.ankama.com/igx"))
                {
                    await MapLoadHandler.HandleMapLoadRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/joi"))
                {
                    // CAREFUL: this branch and the fight-message one (further down) both match 'joi'.
                    // Since this is an if/else-if chain, the first match wins. During a fight the
                    // movement must be resolved by FightHandler (it expands the path, spends MP and
                    // emits jud/joo/jvm/juc); if it fell through to here, the player teleported.
                    if (GameState.IsInFight)
                    {
                        await FightHandler.HandleFightMessageAsync(stream, payload, payloadStr);
                    }
                    else
                    {
                        await MapChangeHandler.HandleMovementRequest(stream, payload);
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jos)))
                {
                    await MapChangeHandler.HandleMapChangeRequest(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jpp"))
                {
                    await MapChangeHandler.HandleMovementConfirm(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Isi)))
                {
                    await InventoryHandler.HandleItemMovementRequest(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iov)))
                {
                    // Ha clicado un NPC: según la acción, se le abre la tienda o el diálogo.
                    await NpcHandler.InteractAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ioy)))
                {
                    // Ha elegido una respuesta del diálogo.
                    await NpcHandler.ReplyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kea)))
                {
                    // Comprarle algo al NPC que tiene la tienda abierta.
                    await NpcHandler.BuyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Krc)))
                {
                    await StatsHandler.HandleStatsUpgradeRequest(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Hqa)))
                {
                    // Atacar a un grupo de monstruos. Es lo que manda el cliente de verdad al
                    // lanzar un combate: lleva el id contextual del grupo.
                    await FightHandler.AttackAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jzy)) || payloadStr.Contains(Op.Uri(Op.Kaq))
                         || payloadStr.Contains("type.ankama.com/jwz") || payloadStr.Contains("type.ankama.com/jxy")
                         || payloadStr.Contains(Op.Uri(Op.Jwh))
                         || payloadStr.Contains(Op.Uri(Op.Jwn))
                         || payloadStr.Contains(Op.Uri(Op.Jti))
                         || payloadStr.Contains(Op.Uri(Op.Hoy))
                         || payloadStr.Contains(Op.Uri(Op.Kwr)) || payloadStr.Contains(Op.Uri(Op.Kwj))
                         || payloadStr.Contains(Op.Uri(Op.Kwv)) || payloadStr.Contains(Op.Uri(Op.Kwi))
                         || payloadStr.Contains(Op.Uri(Op.Kwo)) || payloadStr.Contains(Op.Uri(Op.Kxb)))
                {
                    // Colocarse, declararse listo, las opciones del combate y los RETOS. Los demás
                    // que había aquí —jxx, jyk, jyz, jza, jwe, jrb, jub, jxw— o no existen en la
                    // 3.6.10.10 o los manda el servidor, no el cliente.
                    //
                    // Los seis de retos estaban atendidos dentro del manejador de combate pero no
                    // aquí, así que no llegaban: esta puerta es una lista cerrada. El sintoma era
                    // que el boton de aceptar el reto no hacia nada y que al empezar el combate
                    // salia un reto distinto de los dos ofrecidos, porque el kwv de elegir se
                    // perdia por el camino y el servidor acababa rellenando el hueco el solo.
                    await FightHandler.HandleFightMessageAsync(stream, payload, payloadStr);
                }
                else if (payloadStr.Contains("type.ankama.com/kqn"))
                {
                    await ChatHandler.HandleChatMessage(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains("type.ankama.com/itn"))
                {
                    byte[] rawItt = NetworkEnvelope.ConvertHexStringToByteArray("22-22-08-FF-FF-FF-FF-FF-FF-FF-FF-FF-01-12-15-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-69-74-77");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawItt);
                }
                else if (payloadStr.Contains("type.ankama.com/jte"))
                {
                    byte[] rawJtf = NetworkEnvelope.ConvertHexStringToByteArray("0A-1B-12-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6A-74-6F-12-02-10-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawJtf);
                    Console.WriteLine("[Game Node] Sent jtf response");
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kod)))
                {
                    Console.WriteLine("[Game Node] Received Heartbeat/Ping Request (kod) [3.6]");
                    byte[] rawKns = NetworkEnvelope.ConvertHexStringToByteArray("1A-1B-0A-19-0A-13-74-79-70-65-2E-61-6E-6B-61-6D-61-2E-63-6F-6D-2F-6B-6E-73-12-02-08-01");
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, rawKns);
                    Console.WriteLine("[Game Node] Sent Heartbeat/Pong Response (kns)");
                }
                else
                {
                    // Clean and silence known client-side notification payloads that don't require responses
                    // (e.g. UI logs, almanax requests, heartbeats, recipes) to prevent console flooding.
                    string cleanPayload = payloadStr.Replace("?", "").Trim();
                    if (cleanPayload.Contains(Op.Kmw) || cleanPayload.Contains("klw") || cleanPayload.Contains("knb") || 
                        cleanPayload.Contains("klo") || cleanPayload.Contains("kmt") || cleanPayload.Contains(Op.Jgv) || 
                        cleanPayload.Contains(Op.Jct) || cleanPayload.Contains(Op.Jfc) || cleanPayload.Contains(Op.Kqk) || 
                        cleanPayload.Contains(Op.Itr) || cleanPayload.Contains(Op.Knc) || cleanPayload.Contains("kna") || 
                        cleanPayload.Contains(Op.Hmt) || cleanPayload.Contains("lxi") || cleanPayload.Contains(Op.Jqf) ||
                        // kmv comes with jrh on every map load and expects nothing back; hnn is the
                        // client saying which spell the pointer is on.
                        cleanPayload.Contains(Op.Kmv) || cleanPayload.Contains(Op.Hnn))
                    {
                        // Silenciado, pero no perdido. La lista de arriba son diecisiete opcodes
                        // escritos a mano hace tiempo para que la consola no se inundara, y no hay
                        // ninguna medida detras de que ninguno de ellos necesite respuesta: lo que
                        // hay es que un dia molestaban. Apuntarlos aparte permite volver a mirarlos
                        // sin volver a llenar la pantalla.
                        UnknownPackets.RecordFrame(payload, UnknownPackets.Kind.Silenced);
                    }
                    else
                    {
                        UnknownPackets.RecordFrame(payload, UnknownPackets.Kind.Unhandled);

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n======================================================================");
                        Console.WriteLine($"[Game Node] 🔍 UNHANDLED CLIENT PACKET DETECTED: {payloadStr.Replace("\n", " ").Replace("\r", "")}");
                        Console.WriteLine($"======================================================================");
                        try
                        {
                            var parsedMsg = ProtoMessage.Parse(payload);
                            Console.WriteLine(parsedMsg.DumpFieldsToString("  "));
                            ReportSpellIds(payload);
                        }
                        catch
                        {
                            string hex = BitConverter.ToString(payload).Replace("-", " ");
                            if (hex.Length > 120) hex = hex.Substring(0, 120) + "...";
                            Console.WriteLine($"  Raw Hex[{payload.Length} B]: {hex}");
                        }
                        Console.WriteLine($"======================================================================\n");
                        Console.ResetColor();
                    }
                }

                payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
                if (payload == null) break;
                payloadStr = Encoding.UTF8.GetString(payload);
            }
        }

        /// <summary>
        /// Manda el bloque del mapa, una sola vez por entrada al mundo.
        ///
        /// El bloque lleva un jru, y jru quiere decir "carga este mapa": mandarlo dos veces hace
        /// que el cliente recargue el mundo una y otra vez. Devuelve si lo ha mandado.
        /// </summary>
        private static async Task<bool> SendMapBlockOnceAsync(NetworkStream stream, bool alreadySent,
                                                              string reason)
        {
            if (alreadySent) return false;

            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character == null) return false;

            // El personaje y el mapa van en la traza a proposito: cuando dos clientes entran a la
            // vez, es lo primero que hay que mirar para saber si se han cruzado.
            Console.WriteLine($"[Game Node] Sending the map block ({reason}): " +
                              $"{GameState.CharacterName} en el mapa {GameState.MapId}.");
            await WorldEntry.SendMapAsync(stream, character, GameState.MapId);

            // Y lo que uno tiene de adorno, que el servidor real manda una sola vez, aquí: los
            // títulos y ornamentos disponibles, y cuál lleva puesto.
            await WardrobeHandler.SendOwnedAsync(stream, SessionContext.Current.AccountId);
            return true;
        }

        /// <summary>
        /// Dice si un mensaje que no sabemos manejar lleva dentro el id de un hechizo que hace
        /// pareja con otro. El cambio de variante tiene que ser uno de estos, y así se identifica
        /// el mensaje la primera vez que alguien cambia una variante en vez de adivinarlo.
        /// </summary>
        private static void ReportSpellIds(byte[] payload)
        {
            if (!Managers.SpellTable.IsLoaded) return;

            var found = new List<string>();
            foreach (long value in AllVarInts(payload))
            {
                if (value <= 0 || value > int.MaxValue) continue;
                var pair = Managers.SpellTable.PairOf((int)value);
                if (pair != null) found.Add($"{value} (pareja {pair.Id}: {pair.Base}/{pair.Variant})");
            }

            if (found.Count == 0) return;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⇒ lleva hechizos de pareja: {string.Join(", ", found)}");
            Console.WriteLine("     si esto ha salido al cambiar una variante, este es el mensaje que la cambia.");
            Console.ResetColor();
        }

        /// <summary>Todos los números del mensaje, entrando en los submensajes que lo parezcan.</summary>
        private static IEnumerable<long> AllVarInts(byte[] message, int depth = 0)
        {
            if (depth > 6) yield break;

            List<ProtoField> fields;
            try { fields = new List<ProtoField>(ProtoMessage.Parse(message).Fields); }
            catch { yield break; }

            foreach (var field in fields)
            {
                if (field.WireType == 0) yield return field.VarIntValue;
                else if (field.WireType == 2 && field.BytesValue != null && field.BytesValue.Length > 0)
                {
                    foreach (long value in AllVarInts(field.BytesValue, depth + 1)) yield return value;
                }
            }
        }

        /// <summary>
        /// Apunta el hueco de la barra que el cliente acaba de mover.
        ///
        ///   itz: f2 { f2: hueco, f6 { f2: hechizo } }, f3: qué barra
        ///
        /// Leído de una captura real de arrastrar tres hechizos del panel a la barra: el cliente
        /// manda un itz por cada uno y el servidor devuelve el mismo contenido en un ivk. El hueco
        /// cero no viaja, como todo cero en proto3, y una entrada sin f6 es un hueco que se vacía.
        /// Guardarlo es lo que hace que la barra siga igual en la siguiente sesión.
        /// </summary>
        private static void RememberShortcut(byte[] itz)
        {
            try
            {
                int bar = 0;
                byte[]? shortcut = null;
                foreach (var field in ProtoMessage.Parse(itz).Fields)
                {
                    if (field.FieldNumber == 2 && field.WireType == 2) shortcut = field.BytesValue;
                    else if (field.FieldNumber == 3 && field.WireType == 0) bar = (int)field.VarIntValue;
                }

                if (shortcut == null || bar != ConnectionProtocol.SpellBar) return;

                int slot = 0, spellId = 0;
                foreach (var field in ProtoMessage.Parse(shortcut).Fields)
                {
                    if (field.FieldNumber == 2 && field.WireType == 0) slot = (int)field.VarIntValue;
                    else if (field.FieldNumber == 6 && field.WireType == 2)
                    {
                        foreach (var inner in ProtoMessage.Parse(field.BytesValue).Fields)
                        {
                            if (inner.FieldNumber == 2 && inner.WireType == 0)
                                spellId = (int)inner.VarIntValue;
                        }
                    }
                }

                Managers.SpellChoices.PutInBar(slot, spellId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game Node] No se pudo leer el itz: {ex.Message}");
            }
        }

        /// <summary>
        /// Redeems the ticket the client presents in kqz and binds the session to an account and
        /// a server. The ticket travels in field 2 of the message.
        /// </summary>
        private static bool HandleTicketPresentation(byte[] payload, ref long accountId, ref int serverId)
        {
            try
            {
                byte[]? kqz = ConnectionProtocol.ReadPayload(payload, Op.Kqz);
                if (kqz == null || kqz.Length == 0) return false;

                var msg = ProtoMessage.Parse(kqz);
                var ticketField = msg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                if (ticketField == null) return false;

                string ticket = Encoding.UTF8.GetString(ticketField.BytesValue);
                var session = SessionRegistry.Redeem(ticket);
                if (session == null) return false;

                accountId = session.AccountId;
                serverId = session.ServerId;
                SessionContext.Current.BindAccount(accountId, serverId);
                return true;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Game Node] Error redeeming the ticket: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reply to the "go back" request. In the capture it carries a session id and a one;
        /// the id is generated fresh every time.
        /// </summary>
        private static byte[] BuildKqrPayload()
        {
            return Pb.New()
                .Str(1, Guid.NewGuid().ToString())
                .Var(4, 1)
                .Build();
        }

        // Aquí vivía PatchJpvEnteringPacket, que abría el jpv que salía hacia el cliente, buscaba
        // en él tres ids de personaje de las capturas con las que se arrancó el emulador, escritos
        // a mano, y los cambiaba por el del jugador. Uno de los tres es de los que el guardia de
        // RegressionGuardTests tiene prohibidos, así que ni se repiten aquí.
        //
        // No lo llamaba nadie: el jpv hace tiempo que se construye en MapLoadHandler con el id
        // bueno desde el principio, así que no había nada que parchear. Fuera, junto con los tres
        // números.

        private static byte[] PatchJohPacket(byte[] packetPayload, long mapId)
        {
            try
            {
                var rootMsg = ProtoMessage.Parse(packetPayload);
                var rootField = rootMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                if (rootField == null) return packetPayload;

                var wrapperMsg = ProtoMessage.Parse(rootField.BytesValue);
                var wrapperField = wrapperMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                if (wrapperField == null) return packetPayload;

                var anyMsg = ProtoMessage.Parse(wrapperField.BytesValue);
                var anyValueField = anyMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                if (anyValueField == null) return packetPayload;

                var johMsg = ProtoMessage.Parse(anyValueField.BytesValue);
                var mapIdField = johMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0);
                if (mapIdField != null)
                {
                    mapIdField.VarIntValue = mapId;
                }
                else
                {
                    johMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = mapId });
                }

                anyValueField.BytesValue = johMsg.ToByteArray();
                wrapperField.BytesValue = anyMsg.ToByteArray();
                rootField.BytesValue = wrapperMsg.ToByteArray();

                return rootMsg.ToByteArray();
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error patching joh packet: {ex.Message}");
                return packetPayload;
            }
        }

    }
}
