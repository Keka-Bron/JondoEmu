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

            // Igual que en el Zaap: la bandera, después del bind. Si no, IsRunning miente.
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
        /// La direccion del otro extremo del socket, sin el puerto. Si no se puede leer se
        /// devuelve vacio: no saber la IP no es motivo para tirar la conexion.
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

            // De donde viene, para poder decirselo la proxima vez que entre.
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
                        await SessionRegistry.BroadcastToMapAsync(
                            session.MapId,
                            ConnectionProtocol.BuildActorLeft(session.CharacterId),
                            session.Id);
                    }
                    catch { }

                    // Y fuera del grupo, si estaba en uno: a los que se quedan hay que decirselo,
                    // porque si no ven un miembro que ya no existe.
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
                SubscriptionEndDate,
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
        /// it stays stable across sessions.
        /// </summary>
        private static string BuildAccountTag(long accountId) => (accountId % 10000).ToString("D4");

        /// <summary>
        /// Fin del abono. Aquí no caduca nunca, pero el FORMATO importa.
        ///
        /// Iba como "2099-01-01T00:00:00Z", con Z, y el servidor real la manda con desplazamiento
        /// numérico: 25 caracteres, "####-##-##T##:##:##+##:##". Es el mismo tropiezo que ya nos
        /// costó la pantalla de selección de servidor con la fecha de última conexión — el cliente
        /// no traga la Z, se queda sin fecha de abono y trata la cuenta como si no lo tuviera. Una
        /// cuenta sin abono tiene un solo hueco de personaje, y de ahí venía el botón de crear
        /// personaje apagado diciendo que ya estaba lleno.
        ///
        /// El patrón está leído de una captura real de la pantalla de creación de personaje, donde
        /// esa cuenta tenía cuatro personajes de cinco y el botón activo.
        /// </summary>
        private static string SubscriptionEndDate =>
            new DateTimeOffset(2099, 1, 1, 0, 0, 0, DateTimeOffset.Now.Offset)
                .ToString("yyyy-MM-ddTHH:mm:sszzz");

        /// <summary>
        /// Una trama, en crudo, al registro de tráfico.
        ///
        /// Esto es lo que más veces se llama de todo el servidor: dos veces por trama, una por
        /// sentido. Y hacía lo más caro que se puede hacer por llamada —abrir el fichero,
        /// escribir, cerrarlo— más un Directory.Exists de propina, porque la ruta se resolvía
        /// entera cada vez. Ahora va por LogFile, que se queda con el manejador abierto.
        ///
        /// Se puede apagar del todo poniendo JONDO_SIN_REGISTRO_DE_TRAFICO=1 en el entorno. Por
        /// omisión sigue encendido: es la herramienta con la que se saca el protocolo, y apagarla
        /// por sorpresa sería quitarle a alguien lo que estaba usando.
        /// </summary>
        private static readonly bool SeRegistraElTrafico =
            Environment.GetEnvironmentVariable("JONDO_SIN_REGISTRO_DE_TRAFICO") != "1";

        public static void LogTraffic(string direction, byte[] data, int length)
        {
            if (!SeRegistraElTrafico) return;

            string hex = BitConverter.ToString(data, 0, length);
            string str = Encoding.UTF8.GetString(data, 0, length).Replace("\r", "\\r").Replace("\n", "\\n");
            LogFile.Traffic.Write(
                $"[{DateTime.Now:HH:mm:ss.fff}] {direction} ({length} bytes)\nHex: {hex}\nStr: {str}\n" +
                "--------------------------------------------------\n");
        }
    }
}
