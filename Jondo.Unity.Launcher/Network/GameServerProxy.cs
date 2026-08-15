using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Launcher.Network
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
            _isRunning = true;
            _cts = new CancellationTokenSource();

            _tcpListener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
            _tcpListener.Start();

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
                        await GameNodeProxy.HandleGameNodeSessionAsync(clientStream, firstPayload, firstPayloadStr);
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
                            var ticket = SessionRegistry.Issue(accountId, selectedServerId);

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
                long byToken = DatabaseManager.GetAccountIdByToken(token);
                if (byToken > 0)
                {
                    Console.WriteLine($"[Connection Server] Token recognized: account {byToken}.");
                    return byToken;
                }
                Console.WriteLine("[Connection Server] The presented token does not match any account.");
            }

            // Fallback: the account that just logged in on the launcher. Useful when the client
            // starts up without going through token issuance.
            long active = HaapiServer.ActiveAccount?.Id ?? 0;
            if (active > 0)
            {
                Console.WriteLine($"[Connection Server] Falling back to the launcher's active account: {active}.");
            }
            return active;
        }

        /// <summary>
        /// Builds the authentication response from the servers in the database and the account's
        /// real characters, each one hanging off its own server.
        /// </summary>
        private static byte[] BuildAuthenticationAccepted(long accountId, string lang)
        {
            var account = DatabaseManager.GetAccountById(accountId);
            string nickname = account?.Nickname ?? HaapiServer.ActiveAccount?.Nickname ?? "Jondo";

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

        public static void LogTraffic(string direction, byte[] data, int length)
        {
            string hex = BitConverter.ToString(data, 0, length);
            string str = Encoding.UTF8.GetString(data, 0, length).Replace("\r", "\\r").Replace("\n", "\\n");
            string logLine = $"[{DateTime.Now:HH:mm:ss.fff}] {direction} ({length} bytes)\nHex: {hex}\nStr: {str}\n--------------------------------------------------\n";
            try
            {
                File.AppendAllText(Paths.TrafficLog, logLine);
            }
            catch { }
        }
    }
}
