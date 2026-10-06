using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Port 5555. Two different protocols come through it, and which one it is gets decided by
    /// the first frame of each connection:
    ///
    ///   - Connection server: bare messages. The client authenticates with the account token,
    ///     receives the server list, picks one and receives a ticket.
    ///   - Game server: messages wrapped in type.ankama.com. The client presents the ticket
    ///     (kqz) and from there the session carries on in GameNodeProxy, which answers with
    ///     the character list and then with the world entry, all over the same connection.
    ///
    /// The client opens a fresh connection for each phase, which is why one port serves both.
    /// </summary>
    public static class GameServerProxy
    {
        private static TcpListener? _tcpListener;
        private static bool _isRunning;
        public static bool IsRunning => _isRunning;
        private static CancellationTokenSource? _cts;

        public static void Start(int port)
        {
            if (_isRunning) return;
            _cts = new CancellationTokenSource();

            // Same as in the Zaap: the flag, after the bind. Otherwise, IsRunning lies.
            _tcpListener = new TcpListener(ServerBinding.TcpAddress, port);
            _tcpListener.Start();
            _isRunning = true;

            Console.WriteLine($"[+] Emulating Game Server on TCP port {port} (Binary Protocol)");
            Console.WriteLine($"[+] Game Server logs will be saved to {Paths.TrafficLog}");

            _ = Task.Run(async () =>
            {
                while (_isRunning && _tcpListener != null)
                {
                    try
                    {
                        var client = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                        _ = HandleGameClient(client);
                    }
                    catch (Exception ex)
                    {
                        if (!_isRunning) break;
                        Console.WriteLine($"[Game Server Accept Error] {ex.Message}");
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

        private static async Task HandleGameClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Console.WriteLine($"[+] Client connected to Game Server ({client.Client.RemoteEndPoint})");
                    var clientStream = client.GetStream();

                    byte[] firstPayload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(clientStream);
                    if (firstPayload == null) return;

                    LogTraffic("C->S", firstPayload, firstPayload.Length);
                    string firstPayloadStr = Encoding.UTF8.GetString(firstPayload);

                    if (firstPayloadStr.Contains(ConnectionProtocol.UriPrefix))
                    {
                        // Game phase. It starts with kqz (the ticket) and carries on with
                        // character selection and world entry over this same connection.
                        Console.WriteLine("[+] Detected Game Node protocol on port 5555!");
                        await HandleBoundGameSessionAsync(clientStream, firstPayload, firstPayloadStr);
                    }
                    else
                    {
                        Console.WriteLine("[+] Detected Connection Server protocol on port 5555!");
                        await HandleConnectionServerSessionAsync(clientStream, firstPayload);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[-] Game TCP Connection Closed: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Port 5555 is the route used by the real client for the game phase. It must create the
        /// same socket-owned GameSession as the dedicated GameNode listener. Previously it called
        /// the dispatcher directly, without a GameSession or SessionContext.Push; consequently
        /// every client fell back to SessionContext's single shared "Suelta" state. The last
        /// character loaded then became the identity/map/look seen while processing every socket.
        /// </summary>
        /// <summary>
        /// The address of the socket's other end, without the port. If it cannot be read
        /// empty is returned: not knowing the IP is no reason to drop the connection.
        /// </summary>
        private static string RemoteIp(NetworkStream stream)
        {
            try
            {
                var punto = (stream.Socket?.RemoteEndPoint) as System.Net.IPEndPoint;
                return punto?.Address.ToString() ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static async Task HandleBoundGameSessionAsync(NetworkStream stream,
                                                              byte[] firstPayload,
                                                              string firstPayloadStr)
        {
            var session = new GameSession(stream);
            if (!SessionRegistry.Register(session))
            {
                Console.WriteLine("[Game Server] Rejected game connection: the 8-client limit is reached.");
                return;
            }

            // Where he comes from, to be able to tell him the next time he logs in.
            session.State.ClientIp = RemoteIp(stream);

            GameNodeProxy.SesionesVivas[session.Id] = session;
            Console.WriteLine($"[Game Server] Socket bound to session {session.Id}.");

            try
            {
                using (SessionContext.Push(session))
                {
                    await GameNodeProxy.HandleGameNodeSessionAsync(
                        session, stream, firstPayload, firstPayloadStr);
                }
            }
            finally
            {
                if (session.IsInWorld)
                {
                    try
                    {
                        await SessionRegistry.RemoveFromMapAsync(
                            session.MapId, session.CharacterId, session.Id);
                    }
                    catch { }

                    // And out of the party, if he was in one: the ones who stay have to be told,
                    // because otherwise they see a member who no longer exists.
                    try
                    {
                        using (SessionContext.Push(session))
                            await Handlers.PartyHandler.DisconnectedAsync(session.CharacterId);
                    }
                    catch { }

                    session.LeaveWorld();
                }

                if (session.State.CharacterId > 0)
                {
                    try
                    {
                        using (SessionContext.Push(session)) DatabaseManager.SaveCurrentCharacter();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Game Server] Could not save session {session.Id}: {ex.Message}");
                    }
                }

                GameNodeProxy.SesionesVivas.TryRemove(session.Id, out _);
                SessionRegistry.Unregister(session);
                Console.WriteLine($"[Game Server] Session {session.Id} released.");
            }
        }

        private static async Task HandleConnectionServerSessionAsync(NetworkStream clientStream, byte[] firstPayload)
        {
            byte[] payload = firstPayload;

            // The account is resolved when the token is presented and remembered for the rest
            // of the connection, because the server-selection message no longer carries it.
            long accountId = 0;
            string lang = "0";

            while (_isRunning)
            {
                try
                {
                    var req = Jondo.Protocol.GameMessage.Parser.ParseFrom(payload);
                    if (req.Auth != null)
                    {
                        if (!string.IsNullOrEmpty(req.Auth.Lang)) lang = req.Auth.Lang;

                        if (req.Auth.Ticket != null)
                        {
                            accountId = ResolveAccount(req.Auth.Ticket.TokenData?.Token);
                            if (accountId <= 0)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("[Connection Server] Could not identify the account from the " +
                                                  "token. Closing the connection.");
                                Console.ResetColor();
                                return;
                            }

                            byte[] accepted = BuildAuthenticationAccepted(accountId, lang);
                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(clientStream, accepted);
                        }
                        else if (req.Auth.SelectedServer != null)
                        {
                            int selectedServerId = req.Auth.SelectedServer.ServerId;

                            if (accountId <= 0)
                            {
                                Console.WriteLine("[Connection Server] Server selection with no identified " +
                                                  "account. Closing the connection.");
                                return;
                            }

                            // Closed servers still show up in the list but do not accept players.
                            // We check it here as well, in case the client lets it through.
                            if (!DatabaseManager.IsServerJoinable(selectedServerId))
                            {
                                Console.WriteLine($"[Connection Server] Server {selectedServerId} is not " +
                                                  "accepting connections. No ticket issued.");
                                return;
                            }

                            // The ticket is single-use and binds the next connection to this
                            // account and this server. Without it, the game session would have
                            // no idea who it is serving.
                            // THE LANGUAGE COMES FROM THE LAUNCH, NOT FROM THIS MESSAGE.
                            //
                            // `lang` here is whatever the client put in its authentication
                            // request, and it starts at the string "0": a session issued from it
                            // fell through the normaliser to Spanish every time, so the whole
                            // translation did nothing.
                            //
                            // The real code is two doors back. The launcher starts the client with
                            // --langCode and registers that in ClientLaunchRegistry, keyed by
                            // account, which is the same account we are issuing this ticket for.
                            // Measured in the nine authentication captures: the client does send
                            // its two-letter code, but in kqz field 3 -"es" in all six that carry
                            // it- and not in the field this proxy reads.
                            string idioma = ClientLaunchRegistry.TryGetByAccount(accountId, out var lanzamiento)
                                          && lanzamiento != null
                                ? lanzamiento.Language
                                : lang;

                            var ticket = SessionRegistry.Issue(accountId, selectedServerId, idioma);

                            byte[] response = ConnectionProtocol.BuildServerSelected(
                                lang, ticket.Value, "127.0.0.1", Program.gamePort, Program.gamePort);

                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(clientStream, response);
                            Console.WriteLine($"[Connection Server] Account {accountId} is joining server " +
                                              $"{selectedServerId}. Ticket issued; the client will reconnect " +
                                              $"to port {Program.gamePort}.");

                            // The client closes this connection and opens another one with the ticket.
                            return;
                        }
                    }
                }
                catch (Exception protoEx)
                {
                    Program.LogDebug($"[Connection Server] Unrecognized frame: {protoEx.Message}");
                }

                payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(clientStream);
                if (payload == null) break;
                LogTraffic("C->S", payload, payload.Length);
            }
            Console.WriteLine("[-] Connection Server session closed.");
        }

        /// <summary>
        /// Resolves the account from the game token the client presents. The token is issued by
        /// the launcher on login and stored on the account.
        /// </summary>
        private static long ResolveAccount(string? token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                long byToken = ClientLaunchRegistry.ResolveToken(token);
                if (byToken > 0)
                {
                    Console.WriteLine($"[Connection Server] Token recognized: account {byToken}.");
                    return byToken;
                }
                Console.WriteLine("[Connection Server] The presented token does not match any account.");
            }

            // Never fall back to another launcher's account: an unidentified socket is rejected.
            return 0;
        }

        /// <summary>
        /// Builds the authentication response from the servers in the database and the account's
        /// real characters, each one hanging off its own server.
        /// </summary>
        private static byte[] BuildAuthenticationAccepted(long accountId, string lang)
        {
            var account = DatabaseManager.GetAccountById(accountId);
            string nickname = account?.Nickname ?? "Jondo";

            var servers = DatabaseManager.GetServers();
            var characters = DatabaseManager.GetCharactersByAccountId(accountId);

            byte[] message = ConnectionProtocol.BuildAuthenticationAccepted(
                lang,
                accountId,
                nickname,
                BuildAccountTag(accountId),
                Subscription.EndDateFor(accountId),
                servers,
                characters);

            Console.WriteLine($"[Connection Server] Account {accountId} ({nickname}): " +
                              $"{servers.Count} server(s), {characters.Count} character(s).");
            foreach (var server in servers)
            {
                int onThisServer = 0;
                foreach (var c in characters)
                {
                    if (c.ServerId == server.Id) onThisServer++;
                }
                Console.WriteLine($"    server {server.Id} ({server.Name}): {onThisServer} character(s)");
            }

            return message;
        }

        /// <summary>
        /// Tag shown next to the nickname in the UI. It is derived from the account id so that
        /// it stays stable across sessions. A house's plaque names its owner with the same pair.
        /// </summary>
        internal static string BuildAccountTag(long accountId) => (accountId % 10000).ToString("D4");

        /// <summary>
        /// End of the subscription: a year from now.
        /// </summary>
        /// <remarks>
        /// The format had already been fixed once and the symptom was still there, and this comment
        /// told half the story. The good half: if the client CANNOT READ this date, it treats the
        /// account as if it had no subscription, and an account without a subscription has A SINGLE character
        /// slot —hence the create button greyed out saying it is already full, with one character—. That
        /// time the reason was the Z; the real server sends a numeric offset, 25 characters,
        /// "####-##-##T##:##:##+##:##".
        ///
        /// The other half is the YEAR, which was left at 2099 and cannot be read either:
        ///
        ///   2026-09-06  from the real capture    1,788,645,600 s   fits
        ///   2099-01-01  ours                     4,070,901,600 s   OVERFLOWS
        ///   limit of a 32-bit integer            2,147,483,647 s = 19 January 2038
        ///
        /// That is the good old 2038 problem. All the subscription dates in the captures fall
        /// about eight days ahead, or are the "1970-01-01T00:00Z" sentinel of the account without a subscription;
        /// none comes near 2038.
        ///
        /// A year from now, and not a constant: it is far from any session, far from 2038
        /// and nobody has to remember to touch it. It has been checked in our own log that the
        /// number of slots we send is NOT what greys out the button —at 10:50 it went out with a hundred
        /// slots and zero characters and the client let one be created; with one already created it is still greyed
        /// with a hundred as with five—, so what decides is the subscription.
        ///
        /// One inference remains and it is stated: that 2099 cannot be read is measured, that THAT is
        /// what greys out the button no capture proves. Trying it proves it.
        /// </remarks>
        /// <remarks>
        /// Moved to <see cref="Subscription"/>: the launcher answers the same question over Thrift
        /// and the two have to agree. See that file for why the Z at the end of the launcher's copy
        /// mattered.
        /// </remarks>


        /// <summary>
        /// A frame, raw, to the traffic log.
        ///
        /// This is what is called most often in the whole server: twice per frame, once per
        /// direction. And it did the most expensive thing that can be done per call —open the file,
        /// write, close it— plus a Directory.Exists thrown in, because the path was resolved
        /// whole every time. Now it goes through LogFile, which keeps the handle open.
        ///
        /// It can be switched off entirely by setting JONDO_SIN_REGISTRO_DE_TRAFICO=1 in the environment. By
        /// default it stays on: it is the tool the protocol is extracted with, and switching it off
        /// by surprise would take from someone what they were using.
        /// </summary>
        private static readonly bool SeRegistraElTrafico =
            Environment.GetEnvironmentVariable("JONDO_SIN_REGISTRO_DE_TRAFICO") != "1";

        public static void LogTraffic(string direction, byte[] data, int length)
        {
            if (!SeRegistraElTrafico) return;

            // The session token goes past here in the clear, in both directions, and this file is
            // kept for as long as the disk allows. Scrub masks the 32 hex characters it is made of
            // and leaves every other byte alone -- see TrafficRedaction for why it is done on the
            // bytes rather than on the two strings below.
            byte[] shown = Diagnostics.TrafficRedaction.Scrub(data, length);

            string hex = BitConverter.ToString(shown, 0, length);
            string str = Encoding.UTF8.GetString(shown, 0, length).Replace("\r", "\\r").Replace("\n", "\\n");
            LogFile.Traffic.Write(
                $"[{DateTime.Now:HH:mm:ss.fff}] {direction} ({length} bytes)\nHex: {hex}\nStr: {str}\n" +
                "--------------------------------------------------\n");
        }
    }
}
