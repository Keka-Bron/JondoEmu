using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Connection.Wire;
using Jondo.Unity.Server.Network.Connection;

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
                        client.NoDelay = true;   // see GameNodeProxy: no frame waits on an ACK
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

        /// <summary>
        /// Port 5555 is the route used by the real client for the game phase. It must create the
        /// same socket-owned GameSession as the dedicated GameNode listener. Previously it called
        /// the dispatcher directly, without a GameSession or SessionContext.Push; consequently
        /// every client fell back to SessionContext's single shared "Suelta" state. The last
        /// character loaded then became the identity/map/look seen while processing every socket.
        /// </summary>
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
                // The same way out as the Game Node's: off a fight's arena before saving, and out
                // of whatever was left half done (SessionTeardown).
                await SessionTeardown.LeaveAsync(session, "Game Server");
                Console.WriteLine($"[Game Server] Session {session.Id} released.");
            }
        }

        private static async Task HandleConnectionServerSessionAsync(NetworkStream clientStream, byte[] firstPayload)
        {
            await ConnectionTransport.RunAsync(clientStream, firstPayload,
                new ConnectionService(new ConnectionBackend()), new ConnectionCodec(),
                () => _isRunning, Program.LogDebug);
        }

        /// <summary>
        /// Tag shown next to the nickname in the UI. It is derived from the account id so that
        /// it stays stable across sessions. A house's plaque names its owner with the same pair.
        /// </summary>
        internal static string BuildAccountTag(long accountId) => (accountId % 10000).ToString("D4");

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
