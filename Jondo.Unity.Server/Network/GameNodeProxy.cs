using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Google.Protobuf;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    public static class GameNodeProxy
    {
        private static TcpListener? _tcpListener;
        private static bool _isRunning;

        private static CancellationTokenSource? _cts;

        /// <summary>
        /// The connections alive right now, one per client. It is what allows sending something to
        /// a specific one or to everyone on a map without passing the socket from hand to hand.
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

                    // The session of THIS connection, bound to the thread before reading anything.
                    //
                    // Without this nothing worked: there are 295 places asking for SessionContext.State and
                    // there was not a single Push in the whole project, so the first one asking for
                    // the state got an exception thrown at it and the connection closed. It is
                    // the "No game session is bound to the current async flow" that came out right after
                    // choosing a character.
                    //
                    // It goes here and wrapping the whole loop because AsyncLocal is inherited
                    // inwards: everything awaited from this point sees the same session without
                    // having to pass it by hand through two hundred signatures.
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
                                await SessionRegistry.RemoveFromMapAsync(
                                    sesion.MapId, sesion.CharacterId, sesion.Id);
                            }
                            catch { }
                            sesion.LeaveWorld();
                        }

                        // A commission half done ends for the one left behind too.
                        try { await CommissionHandler.AbandonAsync(sesion); } catch { }
                        try { await TradeHandler.AbandonAsync(sesion); } catch { }
                        try { await ArtisanHandler.LeftAsync(sesion); } catch { }

                        // Saving on close, which was not done anywhere: until now the
                        // character was only written when something triggered it in passing, so
                        // closing the client outright lost the last position and the kamas.
                        if (sesion.State.CharacterId > 0)
                        {
                            try
                            {
                                using (SessionContext.Push(sesion))
                                {
                                    // Off the arena first. A client closed in the middle of a
                                    // fight was being saved on the tactical map, and came back
                                    // to it on the next login, fight music and all.
                                    FightHandler.BackToRoleplayMap();
                                    DatabaseManager.SaveCurrentCharacter();
                                }
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

                        // One of them still in a fight: the burst stops short of the list, and
                        // the list goes out with the kvd behind it when the client asks (kvc).
                        // Sending the list inside the burst and the kvd after it -- behind the
                        // jtg -- put the client on the character screen anyway.
                        bool backIntoAFight = characters.Any(c => FightHandler.FightToRejoin(c.Id) != null);
                        foreach (byte[] frame in ConnectionProtocol.BuildWelcomeBurst(characters, withList: !backIntoAFight))
                        {
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
                        }
                        Console.WriteLine($"[Game Node] Burst sent to account {sessionAccountId}: " +
                                          $"{characters.Count} character(s) on server {sessionServerId}" +
                                          (backIntoAFight ? ", one of them still in a fight." : "."));
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
                        ConnectionProtocol.Push(Op.Kqr, BuildKqrPayload(sessionAccountId)));
                    // If he leaves while in a fight, he has to be returned to the surface map:
                    // the arena one is an instance and staying there is staying locked in.
                    //
                    // But the fight itself stays, the same as when the socket simply dies: the
                    // character is still in it, the next character list carries the kvd, and he
                    // can come back. LeaveFight -- which throws the whole fight away -- is only
                    // for a fight he could not go back to anyway.
                    if (FightHandler.FightToRejoin(SessionContext.State.CharacterId) != null)
                    {
                        FightHandler.BackToRoleplayMap();
                        SessionContext.State.IsInFight = false;
                        SessionContext.State.FightId = 0;
                    }
                    else
                    {
                        FightHandler.LeaveFight();
                    }
                    if (SessionContext.Current.IsInWorld)
                    {
                        await SessionRegistry.RemoveFromMapAsync(
                            SessionContext.State.MapId, SessionContext.State.CharacterId,
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
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kvz)))
                {
                    // Creating a character. isAuthenticated is the first of the two guards: the
                    // second is inside CreateAsync, which rejects an unresolved account. Both
                    // go because this same branch is also reached from GameServerProxy.
                    await CharacterCreationHandler.CreateAsync(stream, payload, sessionAccountId,
                                                               sessionServerId);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Luy)))
                {
                    // Accepting or declining the koliseo match just found. It is NOT
                    // signing up: its field 2 is a boolean, not a mode index.
                    await KoliseoHandler.AnswerOfferAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Lsm)))
                {
                    // The same, but with a party already formed.
                    await KoliseoHandler.EnrolPartyAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Lte)))
                {
                    await KoliseoHandler.ReturnAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Lsi)))
                {
                    // The Koliseo window's "leave the queue".
                    await KoliseoHandler.LeaveQueueAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lux)))
                {
                    // The client asks for the koliseo modes.
                    await KoliseoHandler.SendModesAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hph)))
                {
                    // Challenging another player.
                    await ChallengeDuelHandler.OfferAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hpu)))
                {
                    // And its answer: with f2 it accepts, without it it declines.
                    await ChallengeDuelHandler.AnswerAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kwa)))
                {
                    // The bin. Without this answer the confirmation popup never gets to open.
                    await CharacterDeletionHandler.AskAsync(stream, payload, sessionAccountId,
                                                            sessionServerId);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kvu)))
                {
                    // Deleting a character. The usual two guards: isAuthenticated here and the
                    // resolved account inside, because the client picks the id.
                    await CharacterDeletionHandler.DeleteAsync(stream, payload, sessionAccountId,
                                                               sessionServerId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kvh)))
                {
                    await CharacterDeletionHandler.CloseAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kvk)))
                {
                    // The dice button: a random name.
                    await CharacterCreationHandler.SuggestNameAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kvw)) || payloadStr.Contains(Op.Uri(Op.Ksl))
                         || payloadStr.Contains(Op.Uri(Op.Kvl)))
                {
                    // Character selection. We check that it belongs to this session's account:
                    // the client picks the id, so it cannot be trusted.
                    //
                    // The kvl is the same step but right after the character was created: in the capture of a
                    // creation that goes well, the client sends kvl right after the kvi and enters the
                    // world without going through the list.
                    if (!CharacterSelectionHandler.HandleCharacterSelectionRequest(payload, sessionAccountId))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("[Game Node] Character selection rejected. Closing the session.");
                        Console.ResetColor();
                        return;
                    }

                    // Picking a character that is still in a fight the ordinary way -- not the
                    // kwb of "go on then" -- is turning the fight down: he gives it up, the
                    // others get his surrender, and he enters the world where he left it.
                    var turnedDown = FightHandler.FightToRejoin(GameState.CharacterId);
                    if (turnedDown != null)
                    {
                        await FightHandler.AbandonFromOutsideAsync(turnedDown, GameState.CharacterId);
                    }

                    // A fresh entry into the world: the map block is owed again.
                    hasSentMapBlock = false;
                    if (!await EnterWorldAsync(stream)) return;
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kvc)))
                {
                    // The client asks for the list. On an ordinary login it already has it; when
                    // a character is still in a fight this is where it goes, with the empty kvd
                    // behind it: "do not stop here". The client answers kwb, handled below.
                    var characters = DatabaseManager.GetCharactersByAccountId(sessionAccountId, sessionServerId);
                    var stillFighting = characters.FirstOrDefault(c => FightHandler.FightToRejoin(c.Id) != null);
                    if (stillFighting != null)
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.Kvi, ConnectionProtocol.BuildCharactersList(characters)));
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.Kvd));
                        Console.WriteLine($"[Game Node] {stillFighting.Name} is still in a fight: kvi and kvd sent.");
                    }
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kwb)))
                {
                    // "Go on then": the answer to the kvd. The character is the one of this
                    // account still in a fight; the message itself names nobody.
                    long back = 0;
                    Jondo.Unity.World.Fights.FightInstance? fightToRejoin = null;
                    foreach (var candidate in DatabaseManager.GetCharactersByAccountId(sessionAccountId, sessionServerId))
                    {
                        fightToRejoin = FightHandler.FightToRejoin(candidate.Id);
                        if (fightToRejoin != null) { back = candidate.Id; break; }
                    }
                    if (fightToRejoin == null
                        || !CharacterSelectionHandler.SelectCharacter(back, sessionAccountId))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("[Game Node] kwb without a fight to go back to. Closing the session.");
                        Console.ResetColor();
                        return;
                    }

                    hasSentMapBlock = false;
                    FightHandler.RejoinState(fightToRejoin);
                    if (!await EnterWorldAsync(stream)) return;
                }
                else if ((payloadStr.Contains(Op.Uri(Op.Ijm)) || payloadStr.Contains(Op.Uri(Op.Kmv)))
                         && FightHandler.PendingResume() != null)
                {
                    // Back into a fight already running: the board as it is now, once, and the
                    // client picks the fight up from there. The placement case is not this: it
                    // goes through the preparation below, from scratch, as the capture does.
                    await FightHandler.ResumeForOneAsync(stream, FightHandler.PendingResume()!);
                }
                else if ((payloadStr.Contains("type.ankama.com/jrh")
                          || payloadStr.Contains(Op.Uri(Op.Kmv)))
                         && FightHandler.PendingPreparation() != null)
                {
                    // In a fight, who is on the map is not sent with a jss: it is the preparation's jxg,
                    // and only when the client asks for them. That is the capture's
                    // order, and sending them before the map change makes it discard them.
                    //
                    // And it asks for them with kmv, not with jrh. On loading a normal map the client sends
                    // both, so hooking the jrh was enough there; but on entering a fight it sends
                    // ijm and kmv and nothing else, and kmv was on the list of messages ignored without
                    // a word. That is why the fight appeared in the server log and on screen
                    // nothing happened.
                    await FightHandler.SendPreparationAsync(stream, FightHandler.PendingPreparation()!);
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.BuildActorsComplete());
                }
                else if (payloadStr.Contains("type.ankama.com/jrh"))
                {
                    // While fighting, the map is already set: sending it the surface map's jss would
                    // take it out of the fight.
                    //
                    // And this CANNOT be a continue. Reading the next frame is at the
                    // END of this while's body, so jumping to the condition skips it:
                    // the payload is still the same, this same branch is entered again, and
                    // it jumps again. Forever, without a single await in between, that is spinning
                    // at full speed and never reading another byte from that client again.
                    var here = GameState.IsInFight
                        ? null
                        : DatabaseManager.GetCharacterById(GameState.CharacterId);

                    // The client asks who is on the map. Without an answer it draws an empty map:
                    // no avatar, no NPCs, no monsters.
                    if (here != null)
                    {
                        // The green marks go out FIRST, before the actor list. That is not a style
                        // choice: in all 20 captures that load a map, iom comes before jss and
                        // never after -- empty ones and full ones alike, "iom (0)" then "jss
                        // (3589)", "iom (96)" then "jss (3895)". This server sent it after the
                        // actors were complete, and the client drew nothing, which is exactly what
                        // it looks like when the marks arrive for a map the client considers
                        // finished. Same frames, same contents, wrong side of the actor list.
                        //
                        // Nothing open survives a map change either: otherwise the X of the new
                        // map's zaap is taken by a conversation the player left behind.
                        NpcHandler.Forget();
                        WorkshopHandler.Forget();
                        MarketplaceHandler.Forget();
                        await Handlers.DreamHandler.OnMapLoadedAsync(stream);
                        await Managers.Quests.SendMarksAsync(stream, GameState.MapId);

                        byte[] actors = ConnectionProtocol.Push(Op.Jss,
                            ConnectionProtocol.BuildMapActors(GameState.MapId, here,
                                                              GameState.CellId, GameState.Orientation,
                                                              sessionAccountId));
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, actors);

                        // How many fights the map has, right behind its actors when it has any.
                        await FightHandler.SendFightCountAsync(stream, GameState.MapId);

                        // And straight behind it, the mark that says there are no more actors. In
                        // every capture that loads a map lva comes immediately after jss, and
                        // without it the client never counts the map as loaded: two seconds later
                        // it asks again with knm, kno and kny and goes round once more.
                        // Inside the haven bag the furniture and the permissions also go, which in the
                        // capture come out between the jss and the lva.
                        if (Managers.Merkasako.IsHavenBag(GameState.MapId))
                        {
                            await MerkasakoHandler.SendFurnitureAsync(stream);
                        }


                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.BuildActorsComplete());

                        // And what depends on having arrived here: the objectives met
                        // by stepping on a map or a zone.
                        //
                        // HERE and not in MapLoadHandler, which is where it was and it did not work. The kkr
                        // that one handles only arrives on the world's initial load; walking from one map
                        // to another does not send it, and it is seen in the log: four map changes
                        // in a row —154011397, 154010885, 154010884, 154010883— and not one call to
                        // the marks, so the NPC that did have a quest to give came out without the
                        // exclamation mark above it. This block, on the other hand, is where the five
                        // ways of reaching a map go through, because the client always asks for the actors.
                        var mapaInfo = MapManager.GetMapInfo(GameState.MapId);
                        await Managers.Quests.OnMapEnteredAsync(stream, GameState.MapId,
                                                                mapaInfo?.SubAreaId ?? 0);

                        // And the achievements that walking here earns: the zone explored, and
                        // the level and the bag looked at again. Same place, same reason.
                        await Managers.Achievements.OnMapEnteredAsync(stream, GameState.MapId,
                                                                      mapaInfo?.SubAreaId ?? 0);

                        Console.WriteLine($"[Game Node] Actors of map {GameState.MapId} sent: " +
                                          $"{here.Name} on cell {GameState.CellId}.");
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lqc)))
                {
                    // lqc is the client saying it has already digested the first block. This is where
                    // it is time to give it the map.
                    //
                    // Before, we waited for the first kqo, which is the heartbeat and arrives every five seconds:
                    // in the real client's log 4.8 s passed between choosing a character and receiving
                    // the map. That gap is the flash of empty Incarnam before the fade to black:
                    // the client is already in the world, does not yet know on which map, and meanwhile
                    // shows its default scene, which is Incarnam's. That is why its
                    // music also played on the character screen.
                    Console.WriteLine("[Game Node] Client confirmed with lqc.");
                    if (await SendMapBlockOnceAsync(stream, hasSentMapBlock, Op.Lqc)) hasSentMapBlock = true;
                }
                // ─── 3.6.10.10 world messages. The joi/jos/jpp branches further down belong to
                // an earlier version of the protocol and this client never sends them.
                else if (payloadStr.Contains(Op.Uri(Op.Jrw)))
                {
                    // Walking is the same message inside and outside a fight. While fighting the
                    // fight handler resolves it, which also spends movement points; if it fell here,
                    // the character would move around the board for free and without telling anyone.
                    //
                    // It goes through HandleFightMessageAsync and not straight to WalkAsync: that is the one that takes
                    // the session's lock. Calling WalkAsync bare, walking was the ONLY thing in the
                    // fight that skipped the lock, so it could cross with the turn
                    // clock —which also touches the fight and writes to the socket— and leave the state
                    // half done. And on top of that it made unreachable the jrw branch that already existed inside
                    // the fight handler.
                    if (GameState.IsInFight) await FightHandler.HandleFightMessageAsync(stream, payload, payloadStr);
                    else await WorldMoveHandler.ConfirmMovementAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jqi"))
                {
                    await WorldMoveHandler.AllowMapExitAsync(stream, payload);

                    // A party member whose leader opened a fight while he was walking goes in
                    // when his walk ends, as the follow capture does.
                    await FightHandler.AfterWalkAsync(stream);
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

                        // Administration commands are written through here, on any channel,
                        // and they are NOT published: if the handler recognises them, the line stays on the
                        // server and never goes out through the chat. It holds for all channels
                        // because what decides is not the channel, it is the text.
                        bool consumed = text.Length > 0 &&
                            await CommandHandler.TryHandleAsync(stream, text, channel, sessionAccountId);

                        // A prisoner speaks on the general channel only; private messages come by
                        // ktb and are not stopped. See Managers.Jail.
                        bool muted = text.Length > 0 && !consumed
                                     && !Managers.Jail.MaySpeakOn(SessionContext.State.CharacterId, channel);
                        if (muted)
                        {
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(
                                    Handlers.CommandTexts.Get("jail.channel"))));
                        }

                        if (text.Length > 0 && !consumed && !muted)
                        {
                            byte[] linea = ConnectionProtocol.Push(Op.Kti,
                                ConnectionProtocol.BuildChatLine(GameState.CharacterName,
                                    GameState.CharacterId, sessionAccountId, text, channel));
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, linea);

                            // And to the others. It ended here: the line went back to whoever wrote it and
                            // nobody else ever saw it, which with a single player was not noticed.
                            //
                            // The general channel is the MAP's, not the server's: whoever is
                            // present hears it. The line is the same for everyone —it carries inside the
                            // name and id of whoever speaks—, so it is handed out as is. The
                            // other channels (trade, recruitment) are server-wide and
                            // are not handed out yet.
                            int oidos = channel == 0
                                ? await SessionRegistry.BroadcastToMapAsync(
                                      SessionContext.State.MapId, linea, SessionContext.Current.Id)
                                : 0;
                            Console.WriteLine($"[Chat] channel {channel}: {text}" +
                                              (oidos > 0 ? $"   (oído por {oidos} más)" : ""));
                        }
                    }
                }
                // Parties. One invites by name and accepts by party id, so each one
                // has its message; see Handlers.PartyHandler.
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
                // Following the party leader: see Handlers.PartyFollowHandler.
                else if (payloadStr.Contains(Op.Uri(Op.Imh)))
                {
                    await Handlers.PartyFollowHandler.FollowAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Imo)))
                {
                    await Handlers.PartyFollowHandler.UnfollowAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ktb)))
                {
                    await Handlers.PrivateMessageHandler.WhisperAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjg)))
                {
                    // Crear un gremio.
                    await Handlers.GuildHandler.CreateAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jho)))
                {
                    // Leaving the guild.
                    await Handlers.GuildHandler.LeaveAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlx)))
                {
                    // The applications tab. It goes before the jml on opening the window.
                    await Handlers.GuildHandler.ApplicationsAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jml)))
                {
                    // The guild window's members.
                    await Handlers.GuildHandler.MembersAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlk)))
                {
                    // The guild window opens: the chest's tabs and the header. The window used to
                    // come out black when the client sent jlk and jii and waited: it was waiting
                    // for these, and for the jfp's jff as an answer.
                    await Handlers.GuildHandler.OpenWindowAsync(stream);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jii)))
                {
                    // A tab of the guild window: the real server never answers it (26 of 28
                    // captured, the other two answered by their neighbours). It was answered with
                    // the guild again, jgw first, and every tab printed "acabas de unirte".
                }
                // The guild window's tabs whose contents this server does not keep, answered empty
                // as the captures of a new guild answer them (see Op.Jfv to Op.Hxm).
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jfv)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jfs, 0);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jeu)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jei, 3);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jga)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jfz, 1);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jgr)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jgq, 1);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jet)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jdb, 0);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jfw)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jfr, 0);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hzc)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Ice, 1);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hvx)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Hxm, 0);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jew)))
                {
                    // When the week starts again: asked at world entry too.
                    await Handlers.GuildHandler.WeeklyResetAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jiy)))
                {
                    // The directory sheet or the contributions left, depending on the tab.
                    await Handlers.GuildHandler.TabAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jfp)))
                {
                    await Handlers.GuildHandler.BenefitsAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jcs)))
                {
                    await Handlers.GuildHandler.RanksAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jct)))
                {
                    await Handlers.GuildHandler.EditRankAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jck)))
                {
                    await Handlers.GuildHandler.SetRightsAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jcv)))
                {
                    await Handlers.GuildHandler.CreateRankAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjj)))
                {
                    await Handlers.GuildHandler.NoteAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jim)))
                {
                    await Handlers.GuildHandler.LogAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jcc)))
                {
                    await Handlers.GuildHandler.SetProfileAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjm)))
                {
                    await Handlers.GuildHandler.SearchAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlt)))
                {
                    // Viewing an application.
                    await Handlers.GuildHandler.ApplicationDetailAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjn)))
                {
                    // Accepting an application.
                    await Handlers.GuildHandler.AcceptApplicationAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jiz)))
                {
                    // Answering a guild invitation.
                    await Handlers.GuildHandler.AnswerInvitationAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jki)))
                {
                    // Opening the guild shop.
                    await Handlers.GuildHandler.OpenShopAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jkw)))
                {
                    // Buying an oracle.
                    await Handlers.GuildHandler.BuyOracleAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jky)))
                {
                    // Activarlo.
                    await Handlers.GuildHandler.ActivateOracleAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlb)))
                {
                    // Contributing to the guild.
                    await Handlers.GuildHandler.ContributeAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iyc)))
                {
                    // The menu's Infinite Dreams button, and the T key: to the Astral Plane.
                    await DreamHandler.ToAstralPlaneAsync(stream);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ixf)))
                {
                    // Starting a dream, or discarding the one there was.
                    await DreamHandler.StartOrDiscardAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iym)))
                {
                    // Buying at the fountain (inferred).
                    await DreamHandler.BuyAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ixq)))
                {
                    // The loot table of the dream's room.
                    await DreamHandler.DropTableAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kaz)))
                {
                    // Where a fight here would place everybody.
                    await DreamHandler.PositionsAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Izh)))
                {
                    // The astral storm.
                    await DreamHandler.AstralStormAsync(stream);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iyx)))
                {
                    // Leaving the dream.
                    await DreamHandler.LeaveAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iwo)))
                {
                    // All the elements go through the same registry; it decides which action is
                    // behind without mixing data between maps or between sockets.
                    await InteractiveActionHandler.UseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Izv)))
                {
                    // A house's plaque, asked for by the sale window.
                    await HouseHandler.InfoAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jan)))
                {
                    // The house sale window's answer: on sale at a price, or off sale.
                    await HouseHandler.SellAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jad)) || payloadStr.Contains(Op.Uri(Op.Jal)))
                {
                    // A buyer's yes, by inference: see HouseHandler.BuyAsync.
                    await HouseHandler.BuyAsync(stream, payload, payloadStr.Contains(Op.Uri(Op.Jad)) ? Op.Jad : Op.Jal);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Khv)))
                {
                    // An owner's code keypad: the door's or a chest's.
                    await HouseHandler.ChangeCodeAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Khw)))
                {
                    // A stranger's code keypad: a locked door or chest.
                    await HouseHandler.UseCodeAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jll)))
                {
                    // Another tab of the guild chest.
                    await GuildChestHandler.SelectTabAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbn)))
                {
                    // The haven bag button, and the H key.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.EnterFromOutsideAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbl)))
                {
                    // Switching theme inside the haven bag.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.ChangeThemeAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jbv"))
                {
                    // Opening the management menu, to place furniture.
                    await MerkasakoHandler.OpenEditorAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbg)))
                {
                    // A piece of the room. It is gathered and stored on closing the menu.
                    MerkasakoHandler.CollectFurniture(payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jbk")
                         || payloadStr.Contains("type.ankama.com/jav")
                         || payloadStr.Contains("type.ankama.com/jaw"))
                {
                    // Closing the management menu. All three arrive in a row on accepting.
                    await MerkasakoHandler.CloseEditorAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kcr)))
                {
                    // Moving an item between the bag and the chest. The same kcr lays a stack on a
                    // commission's offer, on a workshop's bench, or on a magus table.
                    // And in a marketplace open to sell, it takes a lot back off sale.
                    // And a house chest, a bin or the guild chest.
                    if (!await CommissionHandler.OfferAsync(stream, payload)
                        && !await TradeHandler.MoveAsync(stream, payload)
                        && !await WorkshopHandler.MoveAsync(stream, payload)
                        && !await BankHandler.MoveAsync(stream, payload)
                        && !await StorageHandler.MoveAsync(stream, payload)
                        && !await MarketplaceHandler.WithdrawAsync(payload))
                        await ChestHandler.MoveAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kdk)))
                {
                    // A marketplace: follow an item type, or stop.
                    await MarketplaceHandler.TypeAsync(payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Keh)))
                {
                    // A marketplace: follow an item and see its offers, or stop.
                    await MarketplaceHandler.ItemAsync(payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kbm)))
                {
                    // A marketplace: buy a lot.
                    await MarketplaceHandler.BuyAsync(payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kbz)))
                {
                    // A marketplace open to sell: an item's prices.
                    await MarketplaceHandler.PriceAsync(payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kge)))
                {
                    // A marketplace open to sell: a lot goes on sale.
                    await MarketplaceHandler.SellAsync(payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kch)))
                {
                    // A marketplace open to sell: a new price for a lot on sale.
                    await MarketplaceHandler.ChangePriceAsync(payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lar)))
                {
                    // The sales history window opened: the account's history.
                    await MarketplaceHandler.SalesHistoryAsync();
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kew)))
                {
                    // A recipe picked in the workshop's list.
                    await WorkshopHandler.SelectRecipeAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kdx)))
                {
                    // How many times to craft it.
                    await WorkshopHandler.CountAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kep)))
                {
                    // Ready: a commission's customer, or in a workshop the craft button.
                    if (!await CommissionHandler.ReadyAsync(stream, payload)
                        && !await TradeHandler.ReadyAsync(stream, payload))
                        await WorkshopHandler.CraftAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kcj)))
                {
                    // A rune on the magus table.
                    await WorkshopHandler.RuneAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kbj)))
                {
                    // Break what is on the grinder.
                    await WorkshopHandler.BreakAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kbl)))
                {
                    // An invitation to a commission, from the magus or from the customer.
                    await CommissionHandler.InviteAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kgi)))
                {
                    // Accepting it, or a trade.
                    if (!await CommissionHandler.AcceptAsync(stream)) await TradeHandler.AcceptAsync();
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kgd)))
                {
                    // The magus moves one of the customer's stacks onto the table or back.
                    await CommissionHandler.MoveAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Irl)))
                {
                    // A job's settings as an artisan.
                    await ArtisanHandler.SettingsAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kef)))
                {
                    // In or out of the public artisans' list.
                    await ArtisanHandler.ToggleListingAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Isr)))
                {
                    // One job's artisans.
                    await ArtisanHandler.ListAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kee)))
                {
                    // Kamas in an exchange: a commission's payment, the bank's, or a trade's.
                    if (!await CommissionHandler.PaymentAsync(stream, payload)
                        && !await BankHandler.KamasAsync(stream, payload)
                        && !await HouseHandler.KamasAsync(stream))
                        await TradeHandler.KamasAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jzx)))
                {
                    // A side's fight option switched: no spectators, party only, closed, help.
                    await FightHandler.FightOptionAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Keu)))
                {
                    // Asking another player to trade.
                    await TradeHandler.RequestAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Itr)))
                {
                    // The client asks for its inventory again. Silenced until now; the real
                    // server answers ivx and hlm the twelve times it is captured.
                    await WorkshopHandler.InventoryAsync(stream);
                }
                else if (payloadStr.Contains("type.ankama.com/lyk"))
                {
                    // Opening the appearance window.
                    await AppearanceHandler.OpenAsync(stream, sessionAccountId);
                }
                else if (payloadStr.Contains("type.ankama.com/lyy"))
                {
                    // That window's state.
                    await AppearanceHandler.SendStateAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lys)))
                {
                    // Putting on a garment; the server resolves the slot.
                    await AppearanceHandler.WearAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lyf)))
                {
                    // Poner o quitar en un hueco concreto.
                    await AppearanceHandler.AssignAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxg)))
                {
                    // Showing or hiding a garment.
                    await AppearanceHandler.ToggleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxw)))
                {
                    // The aura.
                    await AppearanceHandler.AuraAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lze)))
                {
                    // Choosing a title in the appearance window. It only touches the draft.
                    await WardrobeHandler.ChooseTitleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lwm)))
                {
                    // Elegir ornamento.
                    await WardrobeHandler.ChooseOrnamentAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxs)))
                {
                    // That window's Save button.
                    await WardrobeHandler.SaveAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iuw)))
                {
                    // Destruir un objeto del inventario.
                    await DestroyItemHandler.DestroyAsync(stream, payload);
                }
                else if (payloadStr.Contains("type.ankama.com/kla"))
                {
                    // The dialogue's close button. It goes empty and expects an answer: khd if what
                    // is open is the chest or an NPC's shop, kld if it is the zaap list.
                    //
                    // The client sends the kla TWICE in a row on closing a shop, with less than
                    // a millisecond in between, and the real server answers a single khd. Since the
                    // first already leaves the shop closed, the second falls into the zaap and goes off with a
                    // kld the client ignores, the same as today.
                    //
                    // And THE CONVERSATION, which was missing here. There was a second branch for the kla further
                    // down, with NpcHandler.CloseAsync, and it was never reached: this one catches it
                    // first and it went off through the zaap, which sends the kld with reason 10. The one for closing
                    // a conversation is 1 —98 of the captured kld carry it— and with 10 the
                    // client leaves the window up. That is why the cross never closed.
                    //
                    // It goes BEFORE the zaap because the zaap is the default case and has no guard
                    // of its own: with the conversation open, any order that puts the zaap first
                    // takes the X that belonged to the dialogue.
                    if (await CommissionHandler.CloseAsync()) { }
                    else if (await TradeHandler.CloseAsync()) { }
                    else if (MarketplaceHandler.IsOpen) await MarketplaceHandler.CloseAsync();
                    else if (WorkshopHandler.IsOpen) await WorkshopHandler.CloseAsync(stream);
                    else if (BankHandler.IsOpen) await BankHandler.CloseAsync(stream);
                    else if (await HouseHandler.CloseDialogAsync(stream)) { }
                    else if (StorageHandler.IsOpen) await StorageHandler.CloseAsync(stream);
                    else if (ChestHandler.IsOpen) await ChestHandler.CloseAsync(stream);
                    else if (NpcHandler.IsShopOpen) await NpcHandler.CloseShopAsync(stream);
                    else if (NpcHandler.IsDialogueOpen) await NpcHandler.CloseAsync(stream, payload);
                    else await ZaapTravelHandler.CloseAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Hjc)))
                {
                    // He has chosen a destination in the zaap list.
                    hasSentMapBlock = true;   // the map block belongs to entering the world, not to this
                    await ZaapTravelHandler.TravelAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hmt)))
                {
                    // Swapping a spell for its variant. Before, it fell into the list of messages that
                    // are silently ignored, which is why choosing a variant did nothing.
                    await SpellHandler.HandleVariantAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Itz)))
                {
                    // Editing a slot of the shortcut bar. The server answers with the very same
                    // entry it was given, and it also records where it ended up: otherwise, the bar is
                    // rebuilt the same every session and whatever the player places is lost on leaving.
                    //
                    //   itz: f2 { f2: slot, f6 { f2: spell } }, f3: which bar
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
                    // The lqc has usually sent it already, so this does nothing; it stays here because
                    // not everything that connects sends lqc —the test client, for one—
                    // and without a map there is no world.
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
                    // He has clicked an NPC: depending on the action, the shop or the dialogue opens for him. With
                    // a marketplace open and no NPC, it is its buy or sell button.
                    if (!await MarketplaceHandler.ModeAsync(payload))
                        await NpcHandler.InteractAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ioy)))
                {
                    // He has chosen a reply in the dialogue.
                    await NpcHandler.ReplyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kea)))
                {
                    // Buying something from the NPC with the shop open.
                    await NpcHandler.BuyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ieo)))
                {
                    // Which step is this quest on? It is answered with the idu.
                    await QuestHandler.StepAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Idw)))
                {
                    // The client considers an objective met. It is the one who knows: the free-text
                    // ones ask for pressing something in the interface and nothing of that is seen here.
                    await QuestHandler.ObjectiveAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iec)))
                {
                    // He asks about one of his quests, right after taking it.
                    await QuestHandler.DetailAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Mga)))
                {
                    // He has pressed the button to claim an achievement. -1 is «all the ones you owe me».
                    await AchievementHandler.ClaimAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Mfe)))
                {
                    // The achievement window opening: the ones closest to being earned.
                    await AchievementHandler.OpenedAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Mfp)))
                {
                    await AchievementHandler.SecondRequestAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Mff)))
                {
                    // A category of the achievement window.
                    await AchievementHandler.CategoryAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Mfm)))
                {
                    await AchievementHandler.DetailsAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Khl)))
                {
                    // An emote from the emote bar, sitting included.
                    await EmoteHandler.PlayAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Hov)))
                {
                    await EmoteHandler.SmileyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Hor)))
                {
                    await EmoteHandler.MoodAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Krc)))
                {
                    await StatsHandler.HandleStatsUpgradeRequest(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Hqa)))
                {
                    // Attacking a monster group. It is what the real client sends on
                    // starting a fight: it carries the group's contextual id.
                    await FightHandler.AttackAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kay)))
                {
                    // Into somebody else's fight during its placement: the swords on the map or
                    // the party window.
                    await FightHandler.JoinRequestAsync(stream, payload);
                }
                else if (FightHandler.IsAutoOptionRequest(payloadStr))
                {
                    // The party window's automatic entry and automatic ready.
                    await FightHandler.AutoOptionAsync(stream, payload, payloadStr);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jzy)) || payloadStr.Contains(Op.Uri(Op.Kaq))
                         || payloadStr.Contains("type.ankama.com/jwz") || payloadStr.Contains("type.ankama.com/jxy")
                         || payloadStr.Contains(Op.Uri(Op.Jwh))
                         || payloadStr.Contains(Op.Uri(Op.Jwn))
                         || payloadStr.Contains(Op.Uri(Op.Jti))
                         || payloadStr.Contains(Op.Uri(Op.Kme))
                         || payloadStr.Contains(Op.Uri(Op.Hoy))
                         || payloadStr.Contains(Op.Uri(Op.Kwr)) || payloadStr.Contains(Op.Uri(Op.Kwj))
                         || payloadStr.Contains(Op.Uri(Op.Kwv)) || payloadStr.Contains(Op.Uri(Op.Kwi))
                         || payloadStr.Contains(Op.Uri(Op.Kwo)) || payloadStr.Contains(Op.Uri(Op.Kxb)))
                {
                    // Placing, declaring ready, the fight options and the CHALLENGES. The others
                    // that were here —jxx, jyk, jyz, jza, jwe, jrb, jub, jxw— either do not exist in
                    // 3.6.10.10 or are sent by the server, not the client.
                    //
                    // The six challenge ones were handled inside the fight handler but not
                    // here, so they did not arrive: this door is a closed list. The symptom was
                    // that the accept-challenge button did nothing and that on starting the fight
                    // a challenge different from the two offered came out, because the choosing kwv
                    // got lost on the way and the server ended up filling the gap on its own.
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
                        cleanPayload.Contains(Op.Jfc) || cleanPayload.Contains(Op.Kqk) || 
                        cleanPayload.Contains(Op.Itr) || cleanPayload.Contains(Op.Knc) || cleanPayload.Contains("kna") || 
                        cleanPayload.Contains(Op.Hmt) || cleanPayload.Contains("lxi") || cleanPayload.Contains(Op.Jqf) ||
                        // kmv comes with jrh on every map load and expects nothing back; hnn is the
                        // client saying which spell the pointer is on.
                        cleanPayload.Contains(Op.Kmv) || cleanPayload.Contains(Op.Hnn))
                    {
                        // Silenced, but not lost. The list above is seventeen opcodes
                        // written by hand a while ago so that the console would not flood, and there is
                        // no measurement behind none of them needing an answer: what there
                        // is is that one day they were a nuisance. Recording them apart allows looking at them again
                        // without filling the screen again.
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
        /// What follows a selection, whoever made it: the character's things are read from the
        /// database, the session enters the world, and blocks 1 and 2 go out. False when the
        /// character is not in the database, which closes the session.
        /// </summary>
        private static async Task<bool> EnterWorldAsync(NetworkStream stream)
        {
            // Block 1 of the world entry, replayed from the 3.6.10.10 capture with the identity
            // rebuilt from the database. The real server stops here and waits for the client to
            // confirm with lqc before sending anything else.
            var chosen = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (chosen == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Game Node] Character {GameState.CharacterId} is not in the database.");
                Console.ResetColor();
                return false;
            }

            Managers.Equipment.LoadFrom(chosen.Id);
            Managers.SpellChoices.LoadFrom(chosen.Id);
            Managers.Quests.LoadFrom(chosen.Id);
            Managers.Achievements.LoadFrom(chosen.Id);
            Managers.Emotes.LoadFrom(chosen.Id);
            SessionContext.State.ElementsUsed = DatabaseManager.LoadElementsUsed(chosen.Id);

            SessionContext.Current.EnterWorld();

            // Somebody going back into a fight is not on a roleplay map for anybody to see.
            if (!GameState.IsInFight)
            {
                await SessionRegistry.BroadcastToMapAsync(
                    SessionContext.State.MapId,
                    ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                        chosen, SessionContext.State.CellId, SessionContext.State.Orientation,
                        SessionContext.Current.AccountId)),
                    SessionContext.Current.Id);
            }

            await WorldEntry.SendAfterCharacterAsync(stream, chosen);

            // Block 2 goes out straight after. In the capture the client asks for it with lqc,
            // and it does send that lqc here too, only later: it comes once the client has
            // digested block 1, by which time ours has already sent block 2. Waiting for it
            // would leave the client without the catalogues for no reason.
            await WorldEntry.SendAfterConfirmAsync(stream, chosen);
            return true;
        }

        /// <summary>
        /// Sends the map block, only once per entry into the world.
        ///
        /// The block carries a jru, and jru means "load this map": sending it twice makes
        /// the client reload the world again and again. Returns whether it sent it.
        /// </summary>
        private static async Task<bool> SendMapBlockOnceAsync(NetworkStream stream, bool alreadySent,
                                                              string reason)
        {
            if (alreadySent) return false;

            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character == null) return false;

            // The character and the map go in the trace on purpose: when two clients enter at
            // once, it is the first thing to look at to know whether they have crossed.
            Console.WriteLine($"[Game Node] Sending the map block ({reason}): " +
                              $"{GameState.CharacterName} en el mapa {GameState.MapId}.");
            await WorldEntry.SendMapAsync(stream, character, GameState.MapId,
                                          GameState.IsInFight ? FightHandler.FightOf(GameState.CharacterId) : null);

            // And what one has as adornment, which the real server sends only once, here: the
            // available titles and ornaments, and which one is worn.
            await WardrobeHandler.SendOwnedAsync(stream, SessionContext.Current.AccountId);

            // The account's houses (jaa): the capture's no longer travels, this one is ours.
            await HouseHandler.SendAccountHousesAsync(stream);

            // And his quest journal, for the same reason: the capture's no longer travels.
            await Managers.Quests.SendJournalAsync(stream);

            // And their emotes and achievements, for the same reason: the replayed block carried
            // the recorded account's 47 emotes and 954 achievements.
            await Managers.Emotes.SendListAsync(stream);
            await Managers.Achievements.SendListAsync(stream);

            // And the green mark over whoever has something to offer on this map.
            await Managers.Quests.SendMarksAsync(stream, GameState.MapId);
            return true;
        }

        /// <summary>
        /// Says whether a message we do not know how to handle carries inside the id of a spell that is
        /// paired with another. The variant swap has to be one of these, and that way the
        /// message is identified the first time someone swaps a variant instead of guessing it.
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

        /// <summary>All the message's numbers, going into the submessages that look like ones.</summary>
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
        /// Records the bar slot the client has just moved.
        ///
        ///   itz: f2 { f2: slot, f6 { f2: spell } }, f3: which bar
        ///
        /// Read from a real capture of dragging three spells from the panel to the bar: the client
        /// sends one itz for each and the server returns the same content in an ivk. Slot
        /// zero does not travel, like every zero in proto3, and an entry without f6 is a slot being emptied.
        /// Storing it is what keeps the bar the same in the next session.
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
        /// Redeems the ticket the client presents in kqz and binds the session to an account,
        /// server and language. The ticket travels in field 2 of the message.
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
                SessionContext.Current.BindAccount(accountId, serverId, session.Language);
                return true;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Game Node] Error redeeming the ticket: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reply to the "go back" request: a session id and a one.
        /// </summary>
        /// <remarks>
        /// THE ID IS REGISTERED BEFORE SENDING IT, and this was what left «Cambiar de
        /// servidor» hanging.
        ///
        /// The shape was right: the real server sends here a GUID WITH HYPHENS, 36 characters
        /// —«desde world a eleccion servidor.pcapng» opens with
        /// <c>kqr (40) 0a24 b00dae9b-e88d-4b5c-9110-e54f3ffaeb40 2001</c>— and we sent
        /// one just like it. What was missing is that nobody knew ours afterwards.
        ///
        /// What the client does with it is measured in the player's log: it closes the
        /// connection, opens another, and presents THAT SAME id as its identity. The last one we sent
        /// was b52b…16eb and it is exactly the one that came back and we rejected, with
        /// «The presented token does not match any account». All the tokens this server
        /// minted were 32 characters —Guid "N" or sixteen bytes in hexadecimal— so an
        /// id of 36 could not match any of them by definition.
        ///
        /// Registering it opens nothing: the server mints it, it goes through that account's
        /// already authenticated socket, and it is good for the same as the game token already handed out.
        /// </remarks>
        private static byte[] BuildKqrPayload(long accountId)
        {
            string sessionId = Guid.NewGuid().ToString();

            if (accountId > 0)
            {
                ClientLaunchRegistry.RegisterToken(accountId, sessionId);
                Console.WriteLine($"[Game Node] Vuelta atrás de la cuenta {accountId}: se le da el " +
                                  "id de sesión y queda reconocido para cuando vuelva a conectar.");
            }
            else
            {
                Console.WriteLine("[Game Node] Vuelta atrás sin cuenta identificada: el id de sesión " +
                                  "no se registra y la reconexión será rechazada.");
            }

            return Pb.New()
                .Str(1, sessionId)
                .Var(4, 1)
                .Build();
        }

        // Here lived PatchJpvEnteringPacket, which opened the jpv going out to the client, looked
        // in it for three character ids from the captures the emulator was started with, written
        // by hand, and swapped them for the player's. One of the three is among those the
        // RegressionGuardTests guard forbids, so they are not even repeated here.
        //
        // Nobody called it: the jpv has long been built in MapLoadHandler with the right id
        // from the start, so there was nothing to patch. Out, together with the three
        // numbers.

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
