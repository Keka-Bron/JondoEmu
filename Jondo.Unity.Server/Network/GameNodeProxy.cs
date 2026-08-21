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

            Console.WriteLine($"[+] Emulating Game Node on TCP port {port} " +
                              $"(Online, {ServerBinding.Description})");

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

            if (payloadStr.Contains(Op.Uri(Op.BasicTimeMessage)) || payloadStr.Contains(Op.Uri(Op.HelloGameMessage)) || payloadStr.Contains(Op.Uri(Op.SpellVariantActivationRequestMessage)) || payloadStr.Contains("type.ankama.com/knx"))
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

                if (payloadStr.Contains(Op.Uri(Op.AuthenticationTicketMessage)))
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
                else if (payloadStr.Contains("type.ankama.com/iuz"))
                {
                    // UIActionBar::ClearBarAction.  The client asks the authoritative server to
                    // clear the bar, then redraws from a ShortcutBarContentMessage.  There is no
                    // dedicated iuz acknowledgement, but an empty itg is the state replacement.
                    //
                    // Decoded iuz body: f2: 1, the spell action-bar type.  The enclosing UI
                    // wrapper carries its own trailing f2:-1 action marker.
                    Managers.SpellChoices.ClearBar();
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.ShortcutBarContentMessage,
                            ConnectionProtocol.BuildEmptySpellBar()));
                    Console.WriteLine("[Game Node] Sent empty spell shortcut bar after iuz.");
                }
                else if (payloadStr.Contains("type.ankama.com/iul"))
                {
                    // UIActionBar's contextual "Remove" menu action.  The live traffic proves
                    // iul is { f1: bar type, f2: slot }: for example f1:1/f2:11 is remove slot 11
                    // from the spell bar.  It used to be silently ignored with the fight-entry
                    // family, leaving both the interface and CharacterSpellBar unchanged.
                    byte[]? iul = ConnectionProtocol.ReadPayload(payload, "iul");
                    if (iul != null && TryReadShortcutBarSlot(iul, out int bar, out int slot) &&
                        bar == ConnectionProtocol.SpellBar)
                    {
                        Managers.SpellChoices.PutInBar(slot, 0);
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.ShortcutBarContentMessage,
                                ConnectionProtocol.BuildSpellBar(GameState.Breed, GameState.CharacterLevel)));
                        Console.WriteLine($"[Game Node] Removed spell shortcut slot {slot} after iul.");
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/iuv"))
                {
                    // UIActionBar drag and drop: iuv { f1: source slot, f2: target slot,
                    // f3: bar type }.  The packet appears when rearranging shortcuts already on
                    // the spell bar, unlike itz which adds an entry from the spell book.
                    byte[]? iuv = ConnectionProtocol.ReadPayload(payload, "iuv");
                    if (iuv != null && TryReadShortcutBarMove(iuv, out int source, out int target, out int bar) &&
                        bar == ConnectionProtocol.SpellBar)
                    {
                        bool moved = Managers.SpellChoices.MoveBarSlot(source, target);
                        // A full itg is the authoritative state replacement.  It also restores
                        // the visible layout if the client optimistically dragged a stale/empty
                        // source slot and the server rejected that move.
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.ShortcutBarContentMessage,
                                ConnectionProtocol.BuildSpellBar(GameState.Breed, GameState.CharacterLevel)));
                        Console.WriteLine($"[Game Node] {(moved ? "Moved" : "Rejected")} spell shortcut " +
                                          $"{source} -> {target} after iuv.");
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/kqq"))
                {
                    // Going back to the character list or to the server list. In the real capture
                    // the server only answers kqr and it is the client that closes the connection
                    // and redoes the handshake with the connection server. Both ways back are
                    // handled the same: the client decides which of the two screens it lands on.
                    if (sessionAccountId <= 0)
                    {
                        Console.WriteLine("[Game Node] Ignored kqq from an unbound game session.");
                        return;
                    }

                    // kqr.f1 is consumed by the client as the token for its next connection-
                    // server authentication. Sending a random GUID here made both menu actions
                    // close the game socket, then fail the new connection as an unknown account.
                    string returnToken = ClientLaunchRegistry.IssueReturnGameToken(sessionAccountId);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.LogoutResultMessage, BuildKqrPayload(returnToken)));
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
                    Console.WriteLine("[Game Node] Client is going back: sent a resumable kqr token and released the session.");
                }
                else if (!isAuthenticated && (payloadStr.Contains(Op.Uri(Op.SpellVariantActivationRequestMessage)) || payloadStr.Contains(Op.Uri(Op.Ise)) || payloadStr.Contains("type.ankama.com/jtk") || payloadStr.Contains("type.ankama.com/knx") || payloadStr.Contains(Op.Uri(Op.HelloGameMessage))))
                {
                    isAuthenticated = true;
                    await CharacterSelectionHandler.HandleAuthRequest(stream, payload, payloadStr);
                }
                // Careful: kqu no longer belongs here. In 3.6.10.10 it is a message pushed by the
                // server inside the welcome burst, not a client request.
                else if (payloadStr.Contains(Op.Uri(Op.Jto)) || payloadStr.Contains("type.ankama.com/kpc") || payloadStr.Contains(Op.Uri(Op.Ksx)) || payloadStr.Contains(Op.Uri(Op.CharactersListRequestMessage)))
                {
                    await CharacterSelectionHandler.HandleCharacterListRequest(stream, payload, payloadStr, sessionAccountId, sessionServerId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.CharacterCreationRequestMessage)))
                {
                    // Crear un personaje.
                    await CharacterCreationHandler.CreateAsync(stream, payload, sessionAccountId,
                                                               sessionServerId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.CharacterCanBeCreatedRequestMessage)))
                {
                    // kwb is the empty "open another character slot" request emitted by the
                    // selection UI.  The matching empty kvd releases that UI state.
                    await CharacterCreationHandler.ConfirmCanCreateAsync(stream, sessionAccountId,
                                                                          sessionServerId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.CharacterNameSuggestionRequestMessage)))
                {
                    // El botón del dado: un nombre al azar.
                    await CharacterCreationHandler.SuggestNameAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.CharacterSelectionMessage)) || payloadStr.Contains(Op.Uri(Op.Ksl))
                         || payloadStr.Contains(Op.Uri(Op.CharacterFirstSelectionMessage)))
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
                        ConnectionProtocol.Push(Op.GameContextRefreshEntityLookMessage, ConnectionProtocol.BuildActorRefreshed(
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
                else if (payloadStr.Contains(Op.Uri(Op.Kmv)))
                {
                    // Fuera de la preparación de combate, kmv es la señal de sincronización que
                    // acompaña a jrh en cada carga de mapa. Las capturas no muestran una respuesta
                    // directa: jrh es quien pide jss/lva. Se conserva como no-reply conocido para
                    // que no vuelva a aparecer como una laguna de compatibilidad.
                    UnknownPacketStore.RecordKnownNoReplyGamePacket(payload, session);
                }
                else if (payloadStr.Contains("type.ankama.com/jrh"))
                {
                    // Peleando, el mapa ya está puesto: mandarle el jss del mapa de superficie lo
                    // sacaría del combate.
                    if (GameState.IsInFight) continue;

                    await SendActorsAndCompleteAsync(stream, "jrh");
                }
                else if (payloadStr.Contains(Op.Uri(Op.GameContextCreateRequestMessage)))
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
                    if (await SendMapBlockOnceAsync(stream, hasSentMapBlock, Op.GameContextCreateRequestMessage)) hasSentMapBlock = true;
                }
                else if (payloadStr.Contains("type.ankama.com/kmr"))
                {
                    // kmr es, según la captura, la petición de mapa de la entrada al mundo: el
                    // servidor real le contesta con once tramas, entre ellas el jru del mapa
                    // (mapeo_3.6.10.10_a_DofusClient.tsv). Nosotros ya se lo dimos en el lqc, así
                    // que aquí sólo hace falta si el lqc no llegó a verse: el mismo bloque, con su
                    // guarda de una vez, que reutiliza la de arriba. Sin guarda sería mandar dos
                    // jru, y dos jru es el bucle de recargar el mundo.
                    if (await SendMapBlockOnceAsync(stream, hasSentMapBlock, "kmr")) hasSentMapBlock = true;
                }
                else if (payloadStr.Contains("type.ankama.com/lzh"))
                {
                    // El cierre de la entrada al mundo: va justo antes del kmv y el jrh, y el
                    // servidor real le contesta un lzl vacío (mapeo_3.6.10.10_a_DofusClient.tsv).
                    // El lzl lleva una lista dentro, pero en este punto de la conversación la
                    // captura no le mete nada: se manda tal cual.
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push("lzl", Array.Empty<byte>()));
                    Console.WriteLine("[Game Node] Answered lzh with an empty lzl.");
                }
                else if (payloadStr.Contains("type.ankama.com/ieo"))
                {
                    // Cuatro ieo seguidos en la entrada al mundo, y el servidor real contesta
                    // cuatro idu, uno por cada ieo (mapeo_3.6.10.10_a_DofusClient.tsv). El ieo
                    // lleva un número —un 1869 medido— y el idu una pareja mensaje/valor que no se
                    // ha llegado a descifrar: se contesta vacío, que es el «nada que contar» de un
                    // idu sin campos.
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push("idu", Array.Empty<byte>()));
                }
                else if (payloadStr.Contains("type.ankama.com/knm")
                         || payloadStr.Contains("type.ankama.com/kno")
                         || payloadStr.Contains("type.ankama.com/kny"))
                {
                    // knm, kno y kny son lo que reenvía el cliente a los dos segundos cuando no le
                    // llegó el lva y no da el mapa por cargado (docs/opcodes.md). Se le da lo mismo
                    // que se le da al jrh —los actores y detrás la marca de que ya no hay más—,
                    // que es exactamente lo que le faltaba. En combate no: el mapa ya está y un jss
                    // de superficie lo sacaría de la pelea.
                    if (!GameState.IsInFight) await SendActorsAndCompleteAsync(stream, "reintento knm/kno/kny");
                }
                // ─── 3.6.10.10 world messages. The joi/jos/jpp branches further down belong to
                // an earlier version of the protocol and this client never sends them.
                else if (payloadStr.Contains(Op.Uri(Op.GameMapMovementRequestMessage)))
                {
                    // Andar es el mismo mensaje dentro y fuera del combate. Peleando lo resuelve el
                    // manejador de combate, que además gasta puntos de movimiento; si cayera aquí,
                    // el personaje se movería por el tablero gratis y sin avisar a nadie.
                    if (GameState.IsInFight) await FightHandler.WalkAsync(stream, payload);
                    else await WorldMoveHandler.ConfirmMovementAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jqi"))
                {
                    await WorldMoveHandler.AllowMapExitAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.ChangeMapMessage)))
                {
                    hasSentMapBlock = true;   // the map block belongs to entering the world, not to this
                    await WorldMoveHandler.ChangeMapAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.StatsUpgradeRequestMessage)))
                {
                    await CharacteristicsHandler.SpendAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/kuh"))
                {
                    await CharacteristicsHandler.ResetAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.ObjectSetPositionMessage)))
                {
                    await EquipmentHandler.MoveAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.ChatClientMultiMessage)))
                {
                    // Chat. With one player on the server there is nobody else to hand it to, so
                    // it comes straight back to whoever said it — which is also what the real
                    // server does with your own lines, and what makes them appear in the window.
                    byte[]? ktm = ConnectionProtocol.ReadPayload(payload, Op.ChatClientMultiMessage);
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
                            byte[] linea = ConnectionProtocol.Push(Op.ChatServerMessage,
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
                else if (payloadStr.Contains(Op.Uri(Op.InteractiveUseRequestMessage)))
                {
                    // Todos los elementos pasan por el mismo registro; él decide qué acción hay
                    // detrás sin mezclar datos entre mapas ni entre sockets.
                    await InteractiveActionHandler.UseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.HouseBuyRequestMessage)))
                {
                    // jal only repeats the price shown by khr. House identity and offer state are
                    // recovered from this session's exact door-backed pending context.
                    await HouseHandler.ConfirmPurchaseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.EnterHavenBagRequestMessage)))
                {
                    // El botón del merkasako, y la tecla H.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.EnterFromOutsideAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.HavenBagThemeChangeRequestMessage)))
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
                else if (payloadStr.Contains(Op.Uri(Op.HavenBagFurnituresUpdateRequestMessage)))
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
                else if (payloadStr.Contains(Op.Uri(Op.ExchangeObjectMoveMessage)))
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
                else if (payloadStr.Contains(Op.Uri(Op.AppearanceItemWearRequestMessage)))
                {
                    // Ponerse una prenda; el hueco lo resuelve el servidor.
                    await AppearanceHandler.WearAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.AppearanceSlotSetRequestMessage)))
                {
                    // Poner o quitar en un hueco concreto.
                    await AppearanceHandler.AssignAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.AppearanceSlotVisibilityRequestMessage)))
                {
                    // Enseñar u ocultar una prenda.
                    await AppearanceHandler.ToggleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.AppearanceAuraRequestMessage)))
                {
                    // El aura.
                    await AppearanceHandler.AuraAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.TitleSelectRequestMessage)))
                {
                    // Elegir título en la ventana de apariencia. Solo toca el borrador.
                    await WardrobeHandler.ChooseTitleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.OrnamentSelectRequestMessage)))
                {
                    // Elegir ornamento.
                    await WardrobeHandler.ChooseOrnamentAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.AppearanceSaveRequestMessage)))
                {
                    // El botón Guardar de esa ventana.
                    await WardrobeHandler.SaveAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.ObjectDeleteMessage)))
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
                else if (payloadStr.Contains(Op.Uri(Op.TeleportRequestMessage)))
                {
                    // Ha elegido destino en la lista del zaap.
                    hasSentMapBlock = true;   // el bloque del mapa es de entrar al mundo, no de esto
                    await ZaapTravelHandler.TravelAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.SpellVariantActivationRequestMessage)))
                {
                    // Cambiar un hechizo por su variante. Antes caía en la lista de mensajes que
                    // se ignoran en silencio, que es por lo que elegir una variante no hacía nada.
                    await SpellHandler.HandleVariantAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.ShortcutBarAddRequestMessage)))
                {
                    // Editing a slot of the shortcut bar. The server answers with the very same
                    // entry it was given, y además se apunta dónde quedó: si no, la barra se
                    // rehace igual en cada sesión y lo que el jugador coloque se pierde al salir.
                    //
                    //   itz: f2 { f2: hueco, f6 { f2: hechizo } }, f3: qué barra
                    byte[]? itz = ConnectionProtocol.ReadPayload(payload, Op.ShortcutBarAddRequestMessage);
                    if (itz != null)
                    {
                        RememberShortcut(itz);
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.ShortcutBarRefreshMessage, itz));
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.BasicPingMessage)))
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
                         || payloadStr.Contains(Op.Uri(Op.Jti))
                         || payloadStr.Contains(Op.Uri(Op.HelloGameMessage)))
                {
                    // Colocarse, declararse listo y las opciones del combate. Los demás que había
                    // aquí —jxx, jyk, jyz, jza, jwe, jrb, jub, jxw— o no existen en la 3.6.10.10 o
                    // los manda el servidor, no el cliente.
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

                    // Los descodificados de la tanda de la entrada al mundo y del combate: qué son
                    // se sabe —de las notas del mapeo contra el cliente y de la propia captura—,
                    // pero el servidor real no les contesta nada, y a ellos no hay que contestarles
                    // nada. Sin esta lista, cada uno de estos llenaba el registro con su rótulo de
                    // UNHANDLED, que es justo lo que no es: son conocidos.
                    //
                    //   kvc, krv   la pareja que va detrás de la ráfaga de bienvenida; el emulador
                    //              deliberadamente no devuelve el krv aunque lleve id de petición
                    //   kaz, koc   vacíos, sin contestación medida
                    //   jiy        un número pequeño (un 3 medido); sin contestación medida
                    //   hom, jha, koe, kpb, jew, hos, lrd, ivp, kon, ktn, kus
                    //              la riada de la entrada al mundo: catálogos y ajustes que el
                    //              cliente pide y que ya le llegaron por los bloques de entrada
                    //   ijm        entra con kmv al combate; kmv ya cubre el flujo
                    //   jqe        un número de casilla, justo antes del jqi/jqk del borde; el
                    //              estado de casilla ya lo lleva el jrw. Significado exacto sin
                    //              establecer
                    if (ProtocolCatalog.IsKnownNoReply(cleanPayload))
                    {
                        // Known and evidenced as having no reply.  Keep one durable record so the
                        // protocol map is complete, but do not make it an actionable unknown.
                        UnknownPacketStore.RecordKnownNoReplyGamePacket(payload, session);
                    }
                    else if (ProtocolCatalog.IsLegacyObservationOnly(cleanPayload))
                    {
                        // This is legacy behaviour, not a proof that the packet is unnecessary.
                        // Keep it visible to the compatibility queue until it has a handler or an
                        // evidence-backed no-reply classification.
                        long queueId = UnknownPacketStore.RecordLegacyIgnoredGamePacket(payload, session);
                        if (queueId > 0)
                            Console.WriteLine($"[Packet Telemetry] Legacy ignored packet queued as #{queueId}.");
                    }
                    else
                    {
                        long queueId = UnknownPacketStore.RecordUnhandledGamePacket(payload, session);
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n======================================================================");
                        Console.WriteLine($"[Game Node] 🔍 UNHANDLED CLIENT PACKET DETECTED" +
                                          (queueId > 0 ? $" (telemetry #{queueId})" : "") + ": " +
                                          payloadStr.Replace("\n", " ").Replace("\r", ""));
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
        /// <summary>
        /// Los actores del mapa y detrás la marca de que ya no hay más.
        ///
        /// Es lo que pide el jrh al cargar cualquier mapa y también lo que piden los knm/kno/kny
        /// cuando el lva se les perdió: la misma respuesta para los dos, de modo que el reintento
        /// del cliente termina en el mismo sitio que la primera vez.
        /// </summary>
        private static async Task SendActorsAndCompleteAsync(NetworkStream stream, string razon)
        {
            long sessionAccountId = SessionContext.Current.AccountId;

            // The client asks who is on the map. Without an answer it draws an empty map:
            // no avatar, no NPCs, no monsters.
            var here = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (here == null) return;

            byte[] actors = ConnectionProtocol.Push(Op.MapComplementaryInformationsDataMessage,
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

            Console.WriteLine($"[Game Node] Actors of map {GameState.MapId} sent ({razon}): " +
                              $"{here.Name} on cell {GameState.CellId}.");
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

            Console.WriteLine($"[Game Node] Sending the map block ({reason}).");
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
        /// iul is the compact remove-shortcut request: f1 is the action-bar type and f2 the
        /// slot.  Unlike itz it carries no nested shortcut payload because the slot alone is
        /// sufficient to remove it.
        /// </summary>
        private static bool TryReadShortcutBarSlot(byte[] iul, out int bar, out int slot)
        {
            bar = 0;
            slot = 0;
            try
            {
                foreach (var field in ProtoMessage.Parse(iul).Fields)
                {
                    if (field.WireType != 0) continue;
                    if (field.FieldNumber == 1) bar = (int)field.VarIntValue;
                    else if (field.FieldNumber == 2) slot = (int)field.VarIntValue;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game Node] No se pudo leer el iul: {ex.Message}");
                return false;
            }
        }

        /// <summary>Parses iuv: f1 source slot, f2 target slot, f3 shortcut-bar type.</summary>
        private static bool TryReadShortcutBarMove(byte[] iuv, out int sourceSlot, out int targetSlot, out int bar)
        {
            sourceSlot = 0;
            targetSlot = 0;
            bar = 0;
            try
            {
                foreach (var field in ProtoMessage.Parse(iuv).Fields)
                {
                    if (field.WireType != 0) continue;
                    if (field.FieldNumber == 1) sourceSlot = (int)field.VarIntValue;
                    else if (field.FieldNumber == 2) targetSlot = (int)field.VarIntValue;
                    else if (field.FieldNumber == 3) bar = (int)field.VarIntValue;
                }

                // The observed client bar spans 0..48.  Treat anything outside the action-bar
                // range as malformed instead of allowing a packet to create arbitrary DB rows.
                return sourceSlot is >= 0 and <= 48 && targetSlot is >= 0 and <= 48 &&
                       sourceSlot != targetSlot;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game Node] No se pudo leer el iuv: {ex.Message}");
                return false;
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
                byte[]? kqz = ConnectionProtocol.ReadPayload(payload, Op.AuthenticationTicketMessage);
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
        /// Reply to the "go back" request. The client presents f1 to the connection server on
        /// its next handshake; it therefore has to be a registered game token.
        /// </summary>
        private static byte[] BuildKqrPayload(string returnGameToken)
        {
            return Pb.New()
                .Str(1, returnGameToken)
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

        private static byte[] PatchKtwPacket(byte[] packetPayload)
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

                var ktwMsg = ProtoMessage.Parse(anyValueField.BytesValue);
                
                // In Dofus 3.6, the real CharacterSelectedSuccessMessage is wrapped in Field 1 of the Any value.
                var field1 = ktwMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                if (field1 != null)
                {
                    var successMsg = ProtoMessage.Parse(field1.BytesValue);
                    
                    // Inside successMsg, Field 3 = characterBaseInfoMsg (CharacterBaseInformations)
                    var field3 = successMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                    if (field3 != null)
                    {
                        var characterBaseInfoMsg = ProtoMessage.Parse(field3.BytesValue);
                        
                        // Inside characterBaseInfoMsg:
                        // Field 2 = characterId (VarInt)
                        // Field 1 = details (CharacterMinimalPlusLookInformations)
                        
                        // 1. Patch characterId (Field 2)
                        var idField = characterBaseInfoMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0);
                        if (idField != null)
                        {
                            idField.VarIntValue = GameState.CharacterId;
                            Program.LogDebug($"[KTW Patch] Patched character ID to: {GameState.CharacterId}");
                        }
                        else
                        {
                            characterBaseInfoMsg.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 0, VarIntValue = GameState.CharacterId });
                        }
                        
                        // 2. Patch details (Field 1)
                        var detailsField = characterBaseInfoMsg.Fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                        if (detailsField != null)
                        {
                            var detailsMsg = ProtoMessage.Parse(detailsField.BytesValue);
                            
                            // Inside detailsMsg:
                            // Field 3 = characterName (String)
                            // Field 6 = characterLevel (VarInt)
                            // Field 2 = entityLook (Message)
                            
                            // Patch name (Field 3)
                            var nameField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 2);
                            if (nameField != null)
                            {
                                nameField.BytesValue = Encoding.UTF8.GetBytes(GameState.CharacterName);
                                Program.LogDebug($"[KTW Patch] Patched character name to: {GameState.CharacterName}");
                            }
                            else
                            {
                                detailsMsg.Fields.Add(new ProtoField { FieldNumber = 3, WireType = 2, BytesValue = Encoding.UTF8.GetBytes(GameState.CharacterName) });
                            }
                            
                            // Patch character level (Field 6)
                            var levelField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 6 && f.WireType == 0);
                            if (levelField != null)
                            {
                                levelField.VarIntValue = GameState.CharacterLevel;
                                Program.LogDebug($"[KTW Patch] Patched character level to: {GameState.CharacterLevel}");
                            }
                            else
                            {
                                detailsMsg.Fields.Add(new ProtoField { FieldNumber = 6, WireType = 0, VarIntValue = GameState.CharacterLevel });
                            }
                            
                            // Patch entityLook (Field 2)
                            var lookField = detailsMsg.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                            if (lookField != null)
                            {
                                try
                                {
                                    var lookWrapper = ProtoMessage.Parse(lookField.BytesValue);
                                    var entityLookField = lookWrapper.Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
                                    
                                    byte[] defaultLookBytes = NetworkEnvelope.ConvertHexStringToByteArray("08-01-18-03-22-18-A2-8B-9B-0F-CB-E5-F6-15-A4-E1-B9-19-92-A6-C8-20-88-8C-A0-28-F5-B7-CB-34-2A-03-5B-E4-10-42-01-34-32-02-20-01-38-09");
                                    byte[] entityLookBytes = defaultLookBytes;
                                    if (GameState.LookBytes != null && GameState.LookBytes.Length > 0)
                                    {
                                        entityLookBytes = GameState.LookBytes;
                                    }

                                    if (entityLookField != null)
                                    {
                                        entityLookField.BytesValue = entityLookBytes;
                                    }
                                    else
                                    {
                                        lookWrapper.Fields.Add(new ProtoField { FieldNumber = 2, WireType = 2, BytesValue = entityLookBytes });
                                    }
                                    
                                    lookField.BytesValue = lookWrapper.ToByteArray();
                                    Program.LogDebug("[KTW Patch] Patched EntityLook inside lookWrapper.");
                                }
                                catch (Exception lookEx)
                                {
                                    Program.LogDebug($"[-] Error patching EntityLook in KTW: {lookEx.Message}");
                                }
                            }
                            
                            detailsField.BytesValue = detailsMsg.ToByteArray();
                        }
                        
                        field3.BytesValue = characterBaseInfoMsg.ToByteArray();
                        field1.BytesValue = successMsg.ToByteArray();
                        anyValueField.BytesValue = ktwMsg.ToByteArray();
                        wrapperField.BytesValue = anyMsg.ToByteArray();
                        rootField.BytesValue = wrapperMsg.ToByteArray();
                        
                        Program.LogDebug("[KTW Patch] Successfully patched ktw packet.");
                        return rootMsg.ToByteArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[-] Error in PatchKtwPacket: {ex.Message}");
            }
            return packetPayload;
        }
    }
}
