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
                        // NAGLE OFF. A cast goes out as a burst of a dozen small frames written in
                        // five milliseconds -- jto, jwe 300, the sheet, the AP, the blows, jwi --
                        // and with Nagle on, the first left at once and the rest waited for its ACK,
                        // which Windows holds back up to 200 ms: the animation and the damage came
                        // that much after the click, against the bots and the monsters alike.
                        client.NoDelay = true;
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
                        // Saving on close, off a fight's arena, out of whatever was left half
                        // done: the same way out as port 5555's (SessionTeardown).
                        await SessionTeardown.LeaveAsync(sesion, "Game Node");
                        Console.WriteLine($"[Game Node] Session {sesion.Id} closed; " +
                                          $"{SessionRegistry.Count} still connected.");
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
            string payloadStr = RoutingTextOf(payload, firstPayloadStr);
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

                // And the session's turn, for the whole packet. It was taken only for the fight's
                // packets, so what runs beside this loop on the same character -- a gathering that
                // finishes, the jail, the monsters' aggression, the control API -- could change his
                // bag or his sheet while a packet was halfway through it. It is given back before
                // waiting for the next packet, never held across it.
                using var packetTurn = await session.EnterTurnAsync();

                GameServerProxy.LogTraffic("GAME_C->S", payload, payload.Length);

                if (payloadStr.Contains(Op.Uri(Op.Kqz)))
                {
                    // The client presents the ticket handed to it by the connection server. From
                    // here on the session knows which account it serves, and answers it with the
                    // burst that ends in the character list. A valid ticket is the only way in.
                    if (HandleTicketPresentation(payload, ref sessionAccountId, ref sessionServerId))
                    {
                        isAuthenticated = true;
                        // The same account in another connection goes, and its character is saved,
                        // before this one reads its characters.
                        int replaced = await SessionRegistry.CloseOthersOfAsync(session, TimeSpan.FromSeconds(10));
                        if (replaced > 0)
                            Console.WriteLine($"[Game Node] Account {sessionAccountId} came in again: " +
                                              $"{replaced} older connection(s) closed.");

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
                // Careful: kqu no longer belongs here. In 3.6.10.10 it is a message pushed by the
                // server inside the welcome burst, not a client request.
                else if (payloadStr.Contains(Op.Uri(Op.Jto)) || payloadStr.Contains("type.ankama.com/kpc") || payloadStr.Contains(Op.Uri(Op.Ksx)))
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
                else if (payloadStr.Contains(Op.Uri("kwd")))
                {
                    // The dice button: a random name. The client asks with kwd and the name comes
                    // back in a kvk («crear personaje - borrar personaje», three times); this branch
                    // used to wait for the kvk itself, which the client never sends.
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
                         && FightHandler.PendingSpectatorBoard() != null)
                {
                    // A spectator's client has the arena: the fight as it stands, once.
                    var watched = FightHandler.PendingSpectatorBoard()!;
                    await FightHandler.InTurnAsync(watched, () => FightHandler.SendSpectatorBoardAsync(stream, watched));
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kme)) && SessionContext.State.SpectatingFightId != 0)
                {
                    // «Abandonar el modo espectador»: home, as at the fight's end.
                    await FightHandler.StopWatchingAsync(SessionContext.Current);
                }
                else if ((payloadStr.Contains(Op.Uri(Op.Ijm)) || payloadStr.Contains(Op.Uri(Op.Kmv)))
                         && FightHandler.PendingResume() != null)
                {
                    // Back into a fight already running: the board as it is now, once, and the
                    // client picks the fight up from there. The placement case is not this: it
                    // goes through the preparation below, from scratch, as the capture does.
                    var resumed = FightHandler.PendingResume()!;
                    await FightHandler.InTurnAsync(resumed, () => FightHandler.ResumeForOneAsync(stream, resumed));
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
                    var prepared = FightHandler.PendingPreparation()!;
                    await FightHandler.InTurnAsync(prepared, async () =>
                    {
                        await FightHandler.SendPreparationAsync(stream, prepared);
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.BuildActorsComplete());
                    });
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
                        Handlers.AnomalyHandler.BeforeMapLoaded(GameState.MapId);
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

                        // And on a guild raid's map, what the raid has shown there: its stagings,
                        // which a map load resets in the client.
                        await Managers.GuildRaidStagings.OnMapLoadedAsync(stream, GameState.CharacterId, GameState.MapId);
                        // And in the Travelers' Tower's portal room, the portal as it stands.
                        await Handlers.TravelersTowerHandler.OnMapLoadedAsync(stream, GameState.MapId);
                        // And on Osavora's maps, the look of the season.
                        await Handlers.OsavoraSeasonHandler.OnMapLoadedAsync(stream, GameState.MapId);
                        // And in a temporal anomaly's zone, the anomaly; on its zaap, the vestige.
                        await Handlers.AnomalyHandler.OnMapLoadedAsync(stream, GameState.MapId);
                        // And in Gargandias's room, the fight of whoever the grate let through.
                        await Handlers.GargandiasHandler.OnMapLoadedAsync(stream, GameState.MapId);
                        // And in a guild hall, its banners: the guild's own, or the neutral ones.
                        await Handlers.GuildHallHandler.OnMapLoadedAsync(stream, GameState.CharacterId, GameState.MapId);

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

                        // And the conquests: walking into a vulnerable prism's zone, or out of it,
                        // changes an AvA fighter's status.
                        await ConquestHandler.OnMapEnteredAsync(stream, GameState.CharacterId,
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
                    else
                    {
                        // What «desaparece al desplazarse» goes with the first step (SlotLookHandler).
                        await SlotLookHandler.OnMovingAsync(stream);
                        await WorldMoveHandler.ConfirmMovementAsync(stream, payload);
                    }
                }
                else if (payloadStr.Contains("type.ankama.com/hir"))
                {
                    await ZaapTravelHandler.TellCostAsync(stream, payload);
                }
                // The friends window's in-game lists, and adding to and removing from them. kpa used
                // to be the character list request of an earlier protocol; in 3.6 it is «ignore».
                else if (payloadStr == Op.Uri("knm")) await FriendsHandler.SendMutualAsync(stream);
                else if (payloadStr == Op.Uri("kno")) await FriendsHandler.SendContactsAsync(stream);
                else if (payloadStr == Op.Uri("kny")) await FriendsHandler.SendBlockedAsync(stream);
                else if (payloadStr == Op.Uri("koq")) await FriendsHandler.AddContactAsync(stream, payload);
                else if (payloadStr == Op.Uri("kog")) await FriendsHandler.RemoveContactAsync(stream, payload);
                else if (payloadStr == Op.Uri("kok")) await FriendsHandler.BlockAsync(stream, payload);
                else if (payloadStr == Op.Uri("kqd")) await FriendsHandler.UnblockAsync(stream, payload);
                else if (payloadStr == Op.Uri(Op.Kpa)) await FriendsHandler.IgnoreAsync(stream, payload);
                else if (payloadStr == Op.Uri("kpl")) await FriendsHandler.UnignoreAsync(stream, payload);
                // The alliance's nugget pot: its tab opening (mdw) and closing (mdt), and handing it out.
                else if (payloadStr == Op.Uri("mdw")) await RecycleHandler.ShowPotAsync(stream);
                else if (payloadStr == Op.Uri("meb")) await RecycleHandler.DistributeAsync(stream, payload);
                else if (payloadStr == Op.Uri("mdt"))
                {
                    // The nuggets tab closing. Nothing to answer.
                }
                // The map panel's «leave the dungeon» and «leave the haven bag».
                else if (payloadStr == Op.Uri("kjw")) await DungeonHandler.LeaveAsync(stream);
                else if (payloadStr == Op.Uri("jaq")) await MerkasakoHandler.ExitAsync(stream);
                else if (payloadStr == Op.Uri("jlh"))
                {
                    // The guild window closing. Nothing to answer.
                }
                // The shop's gifts: one, or all of them, to a character.
                else if (payloadStr == Op.Uri("jwm")) await GiftHandler.ClaimAsync(stream, payload);
                else if (payloadStr == Op.Uri("jth")) await GiftHandler.ClaimAllAsync(stream, payload);
                // A quest no longer followed.
                else if (payloadStr == Op.Uri("idv")) QuestHandler.Unfollow(payload);
                else if (payloadStr == Op.Uri("iek")) QuestHandler.Reorder(payload);
                // A document's «start the quest» button.
                else if (payloadStr == Op.Uri("ido")) await QuestHandler.StartFromDocumentAsync(stream, payload);
                // The item tooltips' average prices.
                else if (payloadStr == Op.Uri("ivp")) await MarketplaceHandler.AveragePricesAsync(stream);
                // The bank's transfer buttons: everything, what is already there, a selection.
                else if (payloadStr == Op.Uri("kez") || payloadStr == Op.Uri("kgx") || payloadStr == Op.Uri("kcg")
                         || payloadStr == Op.Uri("kck") || payloadStr == Op.Uri("kdd"))
                    await BankHandler.TransferAsync(stream, payload, payloadStr.Substring(payloadStr.LastIndexOf('/') + 1));
                // The zaap window's «Guardar»: the position saved at that zaap.
                else if (payloadStr == Op.Uri("hip")) await ZaapTravelHandler.SaveAsync(stream);
                // The guidebook's suggestions: the cards asked for, and a card locked.
                else if (payloadStr == Op.Uri("hmo")) await ActivitySuggestionHandler.SuggestAsync(stream, payload);
                else if (payloadStr == Op.Uri("hmp")) await ActivitySuggestionHandler.LockAsync(stream, payload);
                // A shortcut bar emptied whole.
                else if (payloadStr == Op.Uri("iuz")) await ItemBarHandler.ClearAsync(stream, payload);
                // The party leader's «que me sigan».
                else if (payloadStr == Op.Uri("ikb")) await Handlers.PartyFollowHandler.LeaderSwitchAsync(stream, payload);
                // A cell marked for one's side in a fight.
                else if (payloadStr == Op.Uri("kmh")) await FightHandler.MarkCellAsync(stream, payload);
                // A party invitation withdrawn, and the party window's lock.
                else if (payloadStr == Op.Uri("inq")) await PartyHandler.CancelInvitationAsync(stream, payload);
                else if (payloadStr == Op.Uri("ila")) await PartyHandler.PledgeLoyaltyAsync(stream, payload);
                // A guild's new name or emblem, with its potion.
                else if (payloadStr == Op.Uri(Op.Jhv)) await GuildHandler.RenameAsync(stream, payload);
                else if (payloadStr == Op.Uri(Op.Jhz)) await GuildHandler.ReEmblemAsync(stream, payload);
                // The alignment tab's PvP switch.
                else if (payloadStr == Op.Uri("hnu")) await AlignmentHandler.PvpAsync(stream, payload);
                // A swap of cells with an ally during the placement, accepted or refused.
                else if (payloadStr == Op.Uri(Op.Kap)) await FightHandler.AcceptSwapAsync(stream, payload);
                else if (payloadStr == Op.Uri(Op.Kav)) await FightHandler.RefuseSwapAsync(stream, payload);
                // A player's card («Mostrar perfil»).
                else if (payloadStr == Op.Uri("kxh")) await PlayerCardHandler.ShowAsync(stream, payload);
                // «Informaciones» on a player's menu: the date and who he is, in the chat.
                else if (payloadStr == Op.Uri("lqw")) await WhoisHandler.AskAsync(stream, payload);
                // A ping on somebody standing on the map.
                else if (payloadStr == Op.Uri(Op.Ijg)) await EntityPingHandler.PingAsync(stream, payload);
                // Its background, picked in the card's own window.
                else if (payloadStr == Op.Uri("kye")) await PlayerCardHandler.SetBackgroundAsync(payload);
                // The chat's community, acknowledged.
                else if (payloadStr == Op.Uri("hos")) await ChatHandler.CommunityAsync(stream, payload);
                // The cosmetics collection: items into it and back out.
                else if (payloadStr == Op.Uri("lyn")) await CosmeticCollectionHandler.AddAsync(stream, payload);
                else if (payloadStr == Op.Uri("lxh")) await CosmeticCollectionHandler.RemoveAsync(stream, payload, sessionAccountId);
                else if (payloadStr == Op.Uri(Op.Hmj)) await MimicryHandler.CreateAsync(stream, payload);
                // The «Conjuntos» window: the character's builds.
                else if (payloadStr == Op.Uri("ifg")) await PresetHandler.PlayerStateAsync(stream);
                else if (payloadStr == Op.Uri("igp")) await PresetHandler.CreateAsync(stream, payload);
                else if (payloadStr == Op.Uri("ihf")) await PresetHandler.RenameAsync(stream, payload);
                else if (payloadStr == Op.Uri("iid")) await PresetHandler.IconAsync(stream, payload);
                else if (payloadStr == Op.Uri("igy")) await PresetHandler.SpellsAsync(stream, payload);
                else if (payloadStr == Op.Uri("iha")) await PresetHandler.PointsAsync(stream, payload);
                else if (payloadStr == Op.Uri("ify")) await PresetHandler.PlaceItemAsync(stream, payload);
                else if (payloadStr == Op.Uri("ihs")) await PresetHandler.SaveAsync(stream, payload);
                else if (payloadStr == Op.Uri("igo")) await PresetHandler.DiscardAsync(stream, payload);
                else if (payloadStr == Op.Uri("iin")) await PresetHandler.UpdateAsync(stream, payload);
                else if (payloadStr == Op.Uri("iiy")) await PresetHandler.DeleteAsync(stream, payload);
                else if (payloadStr == Op.Uri("iio")) await PresetHandler.EquipAsync(stream, payload, sessionAccountId);
                else if (payloadStr == Op.Uri("ihg")) await PresetHandler.OutfitAsync(stream, payload);
                // The Rename window's new name, after a name change potion.
                else if (payloadStr == Op.Uri("lvw")) await RemodelHandler.RenameAsync(stream, payload, sessionAccountId);
                else if (payloadStr == Op.Uri("hom"))
                {
                    // The spouse's information, asked with the contact lists: nobody here is
                    // married, and the real server answers nothing to a player who is not.
                }
                else if (payloadStr == Op.Uri("kon"))
                {
                    // The window's own setting, sent as it opens. The real server answers nothing.
                }
                else if (payloadStr.Contains("type.ankama.com/jqe"))
                {
                    if (!GameState.IsInFight) WorldMoveHandler.StopMovement(payload);
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
                else if (payloadStr.Contains(Op.Uri(Op.Kts)))
                {
                    await ExperienceLimitHandler.SetAsync(stream, payload);
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
                            //
                            // The guild's (2) and the alliance's (3) go to their members connected.
                            int oidos = channel switch
                            {
                                0 => await SessionRegistry.BroadcastToMapAsync(
                                         SessionContext.State.MapId, linea, SessionContext.Current.Id),
                                2 => await Handlers.GuildHandler.SayAsync(GameState.CharacterId, linea),
                                3 => await Handlers.AllianceHandler.SayAsync(GameState.CharacterId, linea),
                                _ => 0,
                            };
                            Console.WriteLine($"[Chat] channel {channel}: {text}" +
                                              (oidos > 0 ? $"   (heard by {oidos} more)" : ""));
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
                else if (payloadStr.Contains(Op.Uri(Op.Imd)))
                {
                    // The invitation's «Detalles».
                    await Handlers.PartyHandler.DetailsAsync(stream, payload);
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
                else if (payloadStr.Contains(Op.Uri(Op.Imr)))
                {
                    await Handlers.PartyFollowHandler.FollowMemberAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ikx)))
                {
                    await Handlers.PartyFollowHandler.UnfollowMemberAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Ktb)))
                {
                    await Handlers.PrivateMessageHandler.WhisperAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kus)))
                {
                    await Handlers.ChatStatusHandler.SetAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjg)))
                {
                    // Creating a guild.
                    await Handlers.GuildHandler.CreateAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jho)))
                {
                    // Leaving the guild.
                    await Handlers.GuildHandler.LeaveAsync(stream, payload);
                }
                // The breeding window: see Handlers.BreedingHandler.
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hqx)))
                    await Handlers.BreedingHandler.WindowOpenedAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hue)))
                    await Handlers.BreedingHandler.PaddockTabShownAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hrl)))
                    await Handlers.BreedingHandler.PaddockPickedAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hqq)))
                    Handlers.BreedingHandler.PaddockTabHidden();
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hse)))
                    await Handlers.BreedingHandler.TransferFromBagAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hsn)))
                    await Handlers.BreedingHandler.SendToBagAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Huh)))
                    await Handlers.BreedingHandler.MoveAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hut)))
                    await Handlers.BreedingHandler.RenameAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hun)))
                    await Handlers.BreedingHandler.EnableGaugeAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hqw)))
                    await Handlers.BreedingHandler.DisableGaugeAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hub)))
                    await Handlers.BreedingHandler.FillGaugesAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Htl)))
                    await Handlers.BreedingHandler.SimulateFuelAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hsv)))
                    await Handlers.BreedingHandler.ExtractAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hqv)))
                    await Handlers.BreedingHandler.CloneAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hsa)))
                    await Handlers.BreedingHandler.PreviewMatingAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Htu)))
                    await Handlers.BreedingHandler.BreedAsync(stream, payload);
                // The alliance: see Handlers.AllianceHandler.
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mdk)))
                    await Handlers.AllianceHandler.CreateAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mac)))
                    await Handlers.AllianceHandler.JoinAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mcc)))
                    await Handlers.AllianceHandler.ExpelAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mcf)))
                    await Handlers.AllianceHandler.SheetAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mah)))
                    await Handlers.AllianceHandler.ApplicationsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mai)))
                    await Handlers.AllianceHandler.ApplyAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mae)))
                    await Handlers.AllianceHandler.RewriteApplicationAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mau)))
                    await Handlers.AllianceHandler.DeleteApplicationAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Maf)))
                    await Handlers.AllianceHandler.AnswerApplicationAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbg)))
                    await Handlers.AllianceHandler.RanksAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbj)))
                    await Handlers.AllianceHandler.CreateRankAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbd)))
                    await Handlers.AllianceHandler.EditRankAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbk)))
                    await Handlers.AllianceHandler.RemoveRankAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbn)))
                    await Handlers.AllianceHandler.ReorderRanksAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbl)))
                    await Handlers.AllianceHandler.SetRightsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbo)))
                    await Handlers.AllianceHandler.SetMemberRankAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mcs)))
                    await Handlers.AllianceHandler.SetMessageAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mdg)))
                    await Handlers.AllianceHandler.SetBulletinAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mas)))
                    await Handlers.AllianceHandler.RecruitmentCardAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Lzy)))
                    await Handlers.AllianceHandler.SetRecruitmentAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mck)))
                    await Handlers.AllianceHandler.CardAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mcg)))
                    await Handlers.AllianceHandler.RenameAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mcz)))
                    await Handlers.AllianceHandler.ReEmblemAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mcy)))
                    await Handlers.AllianceHandler.ModifyAllAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbr)))
                    await Handlers.AllianceHandler.InviteAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mbu)))
                    await Handlers.AllianceHandler.AnswerInvitationAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mdj)))
                    Handlers.AllianceHandler.SetConnectionWarnings(true);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mda)))
                    Handlers.AllianceHandler.SetConnectionWarnings(false);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mes)))
                    await Handlers.AllianceHandler.AvaSwitchAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mba)))
                    await Handlers.AllianceHandler.DirectoryOpenedAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mde)))
                    await Handlers.AllianceHandler.DirectoryAsync(stream, payload);
                // The tax collectors and the alliance's fights: see Handlers.TaxCollectorHandler.
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hkc)))
                    await Handlers.TaxCollectorHandler.AttackAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kbu)))
                    await Handlers.TaxCollectorHandler.CollectAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hkt)))
                    await Handlers.TaxCollectorHandler.ConsultAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ker)))
                    await Handlers.TaxCollectorHandler.ModifyAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hle)))
                    await Handlers.TaxCollectorHandler.SetSpellAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hjx)))
                    await Handlers.TaxCollectorHandler.RemoveSpellAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hke)))
                    await Handlers.TaxCollectorHandler.MoveSpellAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hjy)))
                    await Handlers.TaxCollectorHandler.PresetsAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hko)))
                    await Handlers.TaxCollectorHandler.SetPresetSpellAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hli)))
                    await Handlers.TaxCollectorHandler.RemovePresetSpellAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hkw)))
                    await Handlers.TaxCollectorHandler.MovePresetSpellAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hkl)))
                {
                    // The personalization tab closed: nothing to answer, each change was saved as made.
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Meq)))
                    await Handlers.AllianceFights.ListenAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mdy)))
                {
                    // The fights tab closed: the list keeps coming to every member all the same.
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mer)))
                    await Handlers.AllianceFights.JoinAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mea)))
                    await Handlers.AllianceFights.LeaveAsync(stream, payload);
                // The prisms: see Handlers.PrismHandler.
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iez)))
                    await Handlers.PrismHandler.AttackAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ifd)))
                    await Handlers.PrismHandler.TeleportListAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iey)))
                    await Handlers.PrismHandler.CrystalWindowAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Med)))
                    await Handlers.AllianceFights.SwapAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kug)))
                    await Handlers.ConquestHandler.FreeSoulAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Mab)))
                {
                    // Listening to the applications: nothing is answered in the capture.
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
                    await Handlers.GuildHandler.MissionsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jeq)))
                    await Handlers.GuildHandler.RerollMissionAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jej)))
                    await Handlers.GuildHandler.InterestAsync(stream, payload);
                // A divine dimension's world portal crossed (DimensionPortalHandler).
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hhu)))
                    await Handlers.DimensionPortalHandler.UseAsync(stream, payload);
                // The treasure hunts: flags, digs and give-ups (TreasureHuntHandler).
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hhv)))
                    await Handlers.TreasureHuntHandler.FlagAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hhh)))
                    await Handlers.TreasureHuntHandler.RemoveFlagAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hgj)))
                    await Handlers.TreasureHuntHandler.DigAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hgn)))
                    await Handlers.TreasureHuntHandler.GiveUpAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hhn)))
                    await Handlers.TreasureHuntHandler.LegendaryAskedAsync(stream, payload);
                // The automatic dungeon search (MatchmakingHandler).
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kjx)))
                    await Handlers.MatchmakingHandler.StateAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kke)))
                    await Handlers.MatchmakingHandler.SearchAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kkm)))
                    await Handlers.MatchmakingHandler.StopAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kkl)))
                    await Handlers.MatchmakingHandler.AcceptAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kij)))
                    await Handlers.MatchmakingHandler.RefuseAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Kjs)))
                    await Handlers.MatchmakingHandler.SurrenderAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ikn)))
                    await Handlers.MatchmakingHandler.KickNoAsync(stream, payload);
                // The party search's board and announcements (LobbyHandler).
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jmu)))
                    await Handlers.LobbyHandler.BoardAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jop)))
                    await Handlers.LobbyHandler.CloseAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jpr)))
                    await Handlers.LobbyHandler.MineAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jnv)))
                    await Handlers.LobbyHandler.PostAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jov)))
                    await Handlers.LobbyHandler.EditAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jog)))
                    await Handlers.LobbyHandler.DeleteAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Joc)))
                    await Handlers.LobbyHandler.ApplyAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jny)))
                    await Handlers.LobbyHandler.LeaveAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jom)))
                    await Handlers.LobbyHandler.AnswerAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Joh)))
                    await Handlers.LobbyHandler.FreeAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jga)))
                    await Handlers.GuildHandler.WeeklyRewardsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jfy)))
                    await Handlers.GuildHandler.CollectWeeklyRewardsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jgr)))
                    await Handlers.GuildHandler.MissionPreferencesAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jet)))
                    await Handlers.GuildHandler.ChooseTierAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjz)))
                    await Handlers.GuildHallHandler.ChooseHallAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjw)))
                    await Handlers.GuildHallHandler.ChooseThemeAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jha)))
                    await Handlers.GuildHandler.WarnConnectionsAsync(true);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jhf)))
                    await Handlers.GuildHandler.WarnConnectionsAsync(false);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jhn)))
                    await Handlers.GuildHandler.JoinDirectlyAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jhp)))
                    await Handlers.GuildHandler.ShowGuildAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jey)))
                    await Handlers.GuildHandler.SaveTierAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jgg)))
                    await Handlers.GuildHandler.SaveMissionPreferencesAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jfw)))
                    await Handlers.GuildHandler.EmptyTabAsync(stream, payload, Op.Jfr, 0);
                // The Raids tab and the raids' own requests (see GuildRaidHandler).
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hzc)))
                    await Handlers.GuildRaidHandler.ShowAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Idm)))
                    await Handlers.GuildRaidHandler.PurchaseAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hzi)))
                    await Handlers.GuildRaidHandler.JoinAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hym)))
                    await Handlers.GuildRaidHandler.LeaveAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hxu)))
                    await Handlers.GuildRaidHandler.UpdateCaptainAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hzk)))
                    await Handlers.GuildRaidHandler.UpdateDescriptionAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hyb)))
                    await Handlers.GuildRaidHandler.RemoveParticipantAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ida)))
                    await Handlers.GuildRaidHandler.UnblockAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hyt)))
                    await Handlers.GuildRaidHandler.MoveGroupAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ibr)))
                    await Handlers.GuildRaidHandler.StartAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hzd)))
                    await Handlers.GuildRaidHandler.AnswerAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iaz)))
                    await Handlers.GuildRaidHandler.ShowRunningAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hvw)))
                    await Handlers.GuildRaidHandler.StopListeningAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Icm)))
                    await Handlers.GuildRaidHandler.FinishAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ibz)))
                    await Handlers.GuildRaidHandler.RestartAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ibf)))
                    await Handlers.GuildRaidHandler.RunningLeaveAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iaj)))
                    await Handlers.GuildRaidHandler.RunningCaptainAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hvx)))
                    await Handlers.GuildRaidHandler.PlayerRaidsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Icn)))
                    await Handlers.GuildRaidHandler.StopListeningPlayerAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ibw)))
                    await Handlers.GuildRaidHandler.ClaimRewardsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hzq)))
                    await Handlers.GuildRaidHandler.LadderAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ibl)))
                    await Handlers.GuildRaidHandler.StopLadderAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ipz)))
                    await Handlers.LadderHandler.ExperienceListAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqa)))
                    await Handlers.LadderHandler.ExperienceOwnAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqd)))
                    await Handlers.LadderHandler.ExperienceSearchAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Irg)))
                    await Handlers.LadderHandler.AchievementListAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Irj)))
                    await Handlers.LadderHandler.AchievementOwnAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Irb)))
                    await Handlers.LadderHandler.AchievementSearchAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqy)))
                    await Handlers.LadderHandler.KoliseoListAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqu)))
                    await Handlers.LadderHandler.KoliseoOwnAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqz)))
                    await Handlers.LadderHandler.KoliseoSearchAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqq)))
                    await Handlers.LadderHandler.DreamListAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iql)))
                    await Handlers.LadderHandler.DreamOwnAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iqk)))
                    await Handlers.LadderHandler.DreamSearchAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hfz)))
                    await Handlers.LadderHandler.WorldEventsAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hfv)))
                    await Handlers.LadderHandler.WorldEventInstancesAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Hwg)))
                    await Handlers.GuildRaidHandler.InviteAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iag)))
                    await Handlers.GuildRaidHandler.InvitationAnswerAsync(stream, payload);
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
                    await Handlers.GuildHandler.WeekAsync(stream, payload);
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
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jcn)))
                {
                    await Handlers.GuildHandler.DeleteRankAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jcu)))
                {
                    await Handlers.GuildHandler.UpdateRanksAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjd)))
                {
                    await Handlers.GuildHandler.SetMessageOfTheDayAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jik)))
                {
                    await Handlers.GuildHandler.SetBulletinAsync(stream, payload);
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
                    // Accepting or refusing an application.
                    await Handlers.GuildHandler.AnswerApplicationAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jjn)))
                {
                    // Inviting somebody to the guild.
                    await Handlers.GuildHandler.InviteFromClientAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jgv)))
                {
                    // A member's rank, from the members window.
                    await Handlers.GuildHandler.AssignRankAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlv)))
                {
                    // Withdrawing one's own application.
                    await Handlers.GuildHandler.WithdrawApplicationAsync(stream);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlw)))
                {
                    // Applying to a guild from the directory.
                    await Handlers.GuildHandler.ApplyFromClientAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jmo)))
                {
                    // Rewriting one's own application.
                    await Handlers.GuildHandler.RewriteApplicationAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jlu)))
                {
                    // One's own pending application, on opening the directory.
                    await Handlers.GuildHandler.OwnApplicationAsync(stream);
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
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Iwt)))
                    await DreamHandler.InviteGroupAsync(stream);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ixk)))
                    await DreamHandler.AnswerInvitationAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Ixn)))
                    await DreamHandler.ChooseRoomAsync(stream, payload);
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
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jab)))
                {
                    // «Ir» from a list of houses: one's own, or the guild's.
                    await HouseHandler.GoToHouseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jan)))
                {
                    // The house sale window's answer: on sale at a price, or off sale.
                    await HouseHandler.SellAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jad)))
                {
                    // A buyer's yes: see HouseHandler.BuyAsync.
                    await HouseHandler.BuyAsync(stream, payload, Op.Jad);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jal)))
                {
                    // A buyer's yes, with the price, or an owner throwing somebody out of his house.
                    if (!await HouseHandler.BuyAsync(stream, payload, Op.Jal))
                        await HouseHandler.KickAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jai)))
                {
                    // The guild house window asking a house's guild rights.
                    await HouseHandler.GuildRightsAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jao)))
                {
                    // A house shared with the guild, or not any more.
                    await HouseHandler.ShareWithGuildAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jaj)))
                {
                    // The estate agency's search.
                    await EstateAgencyHandler.SearchAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jam)))
                {
                    // Another page of it.
                    await EstateAgencyHandler.PageAsync(stream, payload);
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
                else if (payloadStr.Contains(Op.Uri("kgy")))
                {
                    // An item sent to another tab of the guild chest.
                    await GuildChestHandler.TransferAsync(stream, payload);
                }
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jau)))
                    await MerkasakoHandler.ShareAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jbb)))
                    await MerkasakoHandler.InviteAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jas)))
                    await MerkasakoHandler.AnswerInvitationAsync(stream, payload);
                else if (isAuthenticated && payloadStr.Contains(Op.Uri(Op.Jay)))
                    await MerkasakoHandler.KickAsync(stream, payload);
                else if (payloadStr.Contains(Op.Uri(Op.Jbn)))
                {
                    // The haven bag button, and the H key.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.EnterFromOutsideAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbl)))
                {
                    // Going to another room of the haven bag.
                    hasSentMapBlock = true;
                    await MerkasakoHandler.ChangeRoomAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jbt)))
                {
                    // The room's theme changed.
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
                    // A piece of the room, stored as it comes with the rest of its upload.
                    MerkasakoHandler.CollectFurniture(payload);
                }
                else if (payloadStr.Contains("type.ankama.com/jbk"))
                {
                    // Accepting the room: its upload starts, the pieces follow.
                    MerkasakoHandler.UploadStarts();
                }
                else if (payloadStr.Contains("type.ankama.com/jav")
                         || payloadStr.Contains("type.ankama.com/jaw"))
                {
                    // Closing the management menu.
                    await MerkasakoHandler.CloseEditorAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kcr)))
                {
                    // Moving an item between the bag and the chest. The same kcr lays a stack on a
                    // commission's offer, on a workshop's bench, or on a magus table.
                    // And in a marketplace open to sell, it takes a lot back off sale.
                    // And a house chest, a bin or the guild chest.
                    // And a tax collector's equipment open to be modified.
                    if (!await TaxCollectorHandler.MoveItemAsync(stream, payload)
                        && !await PrismHandler.MoveCrystalAsync(stream, payload)
                        && !await CommissionHandler.OfferAsync(stream, payload)
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
                else if (payloadStr.Contains(Op.Uri(Op.Lyy)))
                {
                    // Putting on an outfit; the worn one's is the window's state.
                    await AppearanceHandler.EquipAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lwk)))
                {
                    // A new outfit from the drafted one.
                    await AppearanceHandler.CreateOutfitAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lzd)))
                {
                    // A copy of an outfit.
                    await AppearanceHandler.DuplicateOutfitAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxt)))
                {
                    // An outfit's name, icon or favourite mark.
                    await AppearanceHandler.UpdateOutfitAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lyr)))
                {
                    // Throwing an outfit away.
                    await AppearanceHandler.DeleteOutfitAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lys)))
                {
                    // Putting on a garment; the server resolves the slot.
                    await AppearanceHandler.WearAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lyf)))
                {
                    // Putting on or taking off at a specific slot.
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
                else if (payloadStr.Contains(Op.Uri(Op.Lyl)))
                {
                    // The colours tab.
                    await AppearanceHandler.ColorsAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lwu)))
                {
                    // An idle animation from the idles tab.
                    await AppearanceHandler.IdleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lyo)))
                {
                    // A body from the bodies tab.
                    await AppearanceHandler.BodyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lwl)))
                {
                    // A face from the faces tab.
                    await AppearanceHandler.FaceAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lww)))
                {
                    // The cosmetics window's draft switch.
                    await AppearanceHandler.DraftAsync(stream);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lze)))
                {
                    // Choosing a title in the appearance window. It only touches the draft.
                    await WardrobeHandler.ChooseTitleAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lwm)))
                {
                    // Choosing an ornament.
                    await WardrobeHandler.ChooseOrnamentAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lxs)))
                {
                    // That window's Save button.
                    await WardrobeHandler.SaveAsync(stream, payload, sessionAccountId);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iuw)))
                {
                    // Destroying an inventory item.
                    await DestroyItemHandler.DestroyAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iuu)))
                {
                    // Using an item: a box, an alliance's potion or prism, a travel item.
                    await ItemUseHandler.UseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Itw)))
                {
                    // A pet fed from the «Alimentar» window.
                    await PetFeedHandler.FeedAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kso)))
                {
                    // The option chosen in a choice: a fight's -- a trial's guardian or hero -- or
                    // the one an item proposed, an ankla's dungeon.
                    if (!await FightHandler.AnswerChoiceAsync(stream, payload))
                        await TravelItemHandler.ChooseAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Lzh)))
                {
                    // The alterations, asked for on entering the world: this character's, from the base.
                    await AlterationHandler.ListAsync(stream);
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
                    if (await TaxCollectorHandler.CloseExchangeAsync(stream)) { }
                    else if (await PrismHandler.CloseCrystalWindowAsync(stream)) { }
                    else if (await CommissionHandler.CloseAsync()) { }
                    else if (await TradeHandler.CloseAsync()) { }
                    else if (MarketplaceHandler.IsOpen) await MarketplaceHandler.CloseAsync();
                    else if (WorkshopHandler.IsOpen) await WorkshopHandler.CloseAsync(stream);
                    else if (BankHandler.IsOpen) await BankHandler.CloseAsync(stream);
                    else if (await HouseHandler.CloseDialogAsync(stream)) { }
                    else if (await EstateAgencyHandler.CloseAsync(stream)) { }
                    else if (StorageHandler.IsOpen) await StorageHandler.CloseAsync(stream);
                    else if (ChestHandler.IsOpen) await ChestHandler.CloseAsync(stream);
                    else if (NpcHandler.IsShopOpen) await NpcHandler.CloseShopAsync(stream);
                    else if (NpcHandler.IsDialogueOpen) await NpcHandler.CloseAsync(stream, payload);
                    else if (await TaxCollectorHandler.CloseTalkAsync(stream)) { }
                    // A document -- a book, a notice -- closes with the empty kld of the book
                    // capture. Last but the zaap: a book left open must not take another window's X.
                    else if (await ReadingItemHandler.CloseAsync(stream)) { }
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
                    // Setting a slot of the shortcut bar. The server answers with the very same
                    // entry it was given, and it also records where it ended up: otherwise, the bar is
                    // rebuilt the same every session and whatever the player places is lost on leaving.
                    //
                    //   itz: f2 { f2: slot, f6 { f2: spell } }, f3: which bar
                    // Without f6 the slot is being emptied — answer with ivr, not an emptied ivk.
                    byte[]? itz = ConnectionProtocol.ReadPayload(payload, Op.Itz);
                    // The item bar's: an item, an emote or a build, kept as it came (ItemShortcuts).
                    if (itz != null && (ProtoMessage.Parse(itz).Fields
                            .FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 0)?.VarIntValue ?? 0) == Managers.ItemShortcuts.Bar)
                    {
                        await ItemBarHandler.SetAsync(stream, itz);
                    }
                    else if (itz != null)
                    {
                        int clearedSlot = RememberShortcut(itz);
                        if (clearedSlot >= 0)
                            await WriteShortcutRemovedAsync(stream, clearedSlot);
                        else
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                                ConnectionProtocol.Push(Op.Ivk, itz));
                    }
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iul)))
                {
                    // Right-click → remove from the bar.
                    //
                    //   iul: f1: which bar, f2: slot
                    //   answer: ivr { f1: bar, f2: slot } — not an emptied ivk
                    await ClearShortcutAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Iuv)))
                {
                    // Drag one slot onto another.
                    //
                    //   iuv: f1: first slot, f2: second slot, f3: which bar
                    //   answer: ivk per filled slot, ivr per emptied one (ghost icon otherwise)
                    await SwapShortcutsAsync(stream, payload);
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
                    if (await SendMapBlockOnceAsync(stream, hasSentMapBlock, "first kqo"))
                    {
                        hasSentMapBlock = true;
                    }
                    else
                    {
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.BuildHeartbeatAnswer());

                        // And the clock of the timed alterations: the ones whose date has come are lost.
                        await AlterationHandler.TickAsync(stream);
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
                else if (payloadStr.Contains(Op.Uri(Op.Jrv)))
                {
                    // The spectator window's list of the map's fights.
                    await FightHandler.RunningFightsAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Jrz)))
                {
                    // And one of them, its people.
                    await FightHandler.FightDetailsAsync(stream, payload);
                }
                else if (payloadStr.Contains(Op.Uri(Op.Kal)))
                {
                    // Watching a friend's, a guild mate's or an alliance mate's fight.
                    await FightHandler.WatchCharacterAsync(stream, payload);
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
                         || payloadStr.Contains(Op.Uri(Op.Ltf)) || payloadStr.Contains(Op.Uri(Op.Lsp))
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
                    // Known client notifications that need no answer (UI logs, almanax requests,
                    // heartbeats, recipes), kept off the console.
                    if (Silenced.Contains(payloadStr))
                    {
                        // Silenced, but not lost. The Silenced list is opcodes written by hand a
                        // while ago so that the console would not flood, and there is no measurement
                        // behind none of them needing an answer: what there is is that one day they
                        // were a nuisance -- the jgv was among them, and it is a guild rank being
                        // given. Recording them apart allows looking at them again without filling
                        // the screen again.
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

                packetTurn.Dispose();
                payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
                if (payload == null) break;
                payloadStr = RoutingTextOf(payload, Encoding.UTF8.GetString(payload));
            }
        }

        /// <summary>
        /// What a packet is routed by: the type of the message it carries, "type.ankama.com/xxx".
        /// </summary>
        /// <remarks>
        /// The routing looked for each type in the WHOLE frame, text and all, and so did the reading
        /// of the payload: a chat line saying "type.ankama.com/kqq" went to the branch that takes the
        /// player back to the character list, and the line itself was never said. The frame's own
        /// type goes alone now; a frame whose envelope cannot be read keeps the old whole text, so
        /// nothing that routed before stops routing.
        /// </remarks>
        /// <summary>
        /// The client's messages that are let go without an answer, by their exact type. kmv comes
        /// with jrh on every map load and expects nothing back; hnn is the client saying which spell
        /// the pointer is on. The list was searched for as three-letter pieces anywhere in the frame,
        /// and three of its entries -- itr, hmt, jqf -- had a branch of their own above it, so they
        /// never reached it.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> Silenced = new()
        {
            Op.Uri(Op.Kmw), Op.Uri("klw"), Op.Uri("knb"), Op.Uri("klo"), Op.Uri("kmt"), Op.Uri(Op.Jfc),
            Op.Uri(Op.Kqk), Op.Uri(Op.Knc), Op.Uri("kna"), Op.Uri("lxi"), Op.Uri(Op.Kmv), Op.Uri(Op.Hnn),
        };

        internal static string RoutingTextOf(byte[] payload, string wholeText)
        {
            string? type = NetworkEnvelope.GetMessageTypeUrl(payload);
            return type != null && type.StartsWith(ConnectionProtocol.UriPrefix, StringComparison.Ordinal) ? type : wholeText;
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

            // His guild raid sees him connected, and a running one comes back with its panel.
            await Handlers.GuildRaidHandler.OnEnterWorldAsync(chosen.Id);

            // His guild sees him on line, and whoever asked is warned.
            await Handlers.GuildHandler.TellConnectionAsync(chosen.Id, online: true);

            // His mutual friends see him playing.
            await Handlers.FriendsHandler.AnnouncePresenceAsync(SessionContext.Current.AccountId);

            // A name change potion used last time: the Rename window.
            await Handlers.RemodelHandler.OnEnterWorldAsync(stream, chosen);

            // And the server's welcome line, if its settings have one.
            if (!string.IsNullOrWhiteSpace(ServerSettings.Current.WelcomeMessage))
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice(ServerSettings.Current.WelcomeMessage.Trim())));
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
                              $"{GameState.CharacterName} on map {GameState.MapId}.");
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

            // And his builds, for the same reason: the replayed block carried that account's fourteen.
            await PresetHandler.SendListAsync(stream);

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
                if (pair != null) found.Add($"{value} (pair {pair.Id}: {pair.Base}/{pair.Variant})");
            }

            if (found.Count == 0) return;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⇒ carries pair spells: {string.Join(", ", found)}");
            Console.WriteLine("     if this came out when changing a variant, this is the message that changes it.");
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
        /// Records the bar slot the client has just set.
        ///
        ///   itz: f2 { f2: slot, f6 { f2: spell } }, f3: which bar
        ///
        /// Read from a real capture of dragging three spells from the panel to the bar: the client
        /// sends one itz for each and the server returns the same content in an ivk. Slot
        /// zero does not travel, like every zero in proto3, and an entry without f6 is a slot being emptied.
        /// Storing it is what keeps the bar the same in the next session.
        /// </summary>
        /// <returns>The cleared slot when the itz emptied one, otherwise -1.</returns>
        private static int RememberShortcut(byte[] itz)
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

                if (shortcut == null || bar != ConnectionProtocol.SpellBar) return -1;

                int slot = 0, spellId = 0;
                bool hasSpell = false;
                foreach (var field in ProtoMessage.Parse(shortcut).Fields)
                {
                    if (field.FieldNumber == 2 && field.WireType == 0) slot = (int)field.VarIntValue;
                    else if (field.FieldNumber == 6 && field.WireType == 2)
                    {
                        hasSpell = true;
                        foreach (var inner in ProtoMessage.Parse(field.BytesValue).Fields)
                        {
                            if (inner.FieldNumber == 2 && inner.WireType == 0)
                                spellId = (int)inner.VarIntValue;
                        }
                    }
                }

                Managers.SpellChoices.PutInBar(slot, spellId);
                return !hasSpell || spellId == 0 ? slot : -1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game Node] Could not read the itz: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Clears one spell-bar slot (iul) and tells the client with ivr.
        /// </summary>
        private static async Task ClearShortcutAsync(NetworkStream stream, byte[] frame)
        {
            try
            {
                byte[]? body = ConnectionProtocol.ReadPayload(frame, Op.Iul);
                if (body == null) return;

                // Proto iul / ShortcutBarRemoveRequest: f1 bar, f2 slot. Traffic always has
                // f1 = 1 (spell bar) and f2 = the slot being cleared. Reading them the other
                // way around made bar == slot and the handler bail out on every right-click.
                int bar = 0, slot = 0;
                foreach (var field in ProtoMessage.Parse(body).Fields)
                {
                    if (field.FieldNumber == 1 && field.WireType == 0) bar = (int)field.VarIntValue;
                    else if (field.FieldNumber == 2 && field.WireType == 0) slot = (int)field.VarIntValue;
                }

                if (bar == Managers.ItemShortcuts.Bar)
                {
                    await ItemBarHandler.RemoveAsync(stream, slot);
                    return;
                }
                if (bar != ConnectionProtocol.SpellBar) return;

                Managers.SpellChoices.PutInBar(slot, 0);
                await WriteShortcutRemovedAsync(stream, slot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game Node] Could not read the iul: {ex.Message}");
            }
        }

        /// <summary>
        /// Swaps two spell-bar slots (iuv). Filled slots get an ivk; emptied ones get an ivr
        /// so the source icon does not stay behind after a drag onto an empty slot.
        /// </summary>
        private static async Task SwapShortcutsAsync(NetworkStream stream, byte[] frame)
        {
            try
            {
                byte[]? body = ConnectionProtocol.ReadPayload(frame, Op.Iuv);
                if (body == null) return;

                int first = 0, second = 0, bar = 0;
                foreach (var field in ProtoMessage.Parse(body).Fields)
                {
                    if (field.FieldNumber == 1 && field.WireType == 0) first = (int)field.VarIntValue;
                    else if (field.FieldNumber == 2 && field.WireType == 0) second = (int)field.VarIntValue;
                    else if (field.FieldNumber == 3 && field.WireType == 0) bar = (int)field.VarIntValue;
                }

                if (bar == Managers.ItemShortcuts.Bar)
                {
                    await ItemBarHandler.SwapAsync(stream, first, second);
                    return;
                }
                if (bar != ConnectionProtocol.SpellBar) return;

                Managers.SpellChoices.SwapBarSlots(first, second);

                await WriteShortcutSlotAsync(stream, first);
                if (second != first) await WriteShortcutSlotAsync(stream, second);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Game Node] Could not read the iuv: {ex.Message}");
            }
        }

        private static async Task WriteShortcutSlotAsync(NetworkStream stream, int slot)
        {
            int spell = Managers.SpellChoices.SpellInSlot(slot);
            if (spell == 0)
            {
                await WriteShortcutRemovedAsync(stream, slot);
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivk, ConnectionProtocol.BuildShortcutChanged(slot, spell)));
        }

        private static async Task WriteShortcutRemovedAsync(NetworkStream stream, int slot)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivr, ConnectionProtocol.BuildShortcutRemoved(slot)));
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
        /// <c>kqr (40) 0a24 00000000-0000-4000-8000-00000000000a 2001</c>— and we sent
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
                Console.WriteLine($"[Game Node] Going back for account {accountId}: it is given the " +
                                  "session id and stays recognised for when it connects again.");
            }
            else
            {
                Console.WriteLine("[Game Node] Going back without an identified account: the session id " +
                                  "is not registered and the reconnection will be rejected.");
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
