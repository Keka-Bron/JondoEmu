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
    public static class GameServerProxy
    {
        private static TcpListener? _tcpListener;
        private static bool _isRunning;
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

                    // Read first frame to detect protocol
                    byte[] firstPayload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(clientStream);
                    if (firstPayload == null) return;

                    LogTraffic("C->S", firstPayload, firstPayload.Length);
                    string firstPayloadStr = Encoding.UTF8.GetString(firstPayload);

                    if (firstPayloadStr.Contains("type.ankama.com/"))
                    {
                        Console.WriteLine("[+] Detected Game Node protocol on port 5555!");
                        // Hand off to GameNodeProxy
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
            while (_isRunning)
            {
                try
                {
                    var req = Jondo.Protocol.GameMessage.Parser.ParseFrom(payload);
                    if (req.Auth != null)
                    {
                        if (req.Auth.Ticket != null)
                        {
                            Console.WriteLine($"[Game Server] Received Auth: Token={req.Auth.Ticket.TokenData.Token}");

                            var authAccepted = GetModifiedAuthAcceptedMessage();

                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(clientStream, authAccepted);
                            Console.WriteLine("[Game Server] Sent Auth Accepted and ServersList!");
                        }
                        else if (req.Auth.SelectedServer != null)
                        {
                            int selectedServerId = req.Auth.SelectedServer.ServerId;
                            Console.WriteLine($"[Game Server] Client selected server ID: {selectedServerId}");

                            // Port 5555 twice varint bytes: 5555 (0xB3, 0x2B), 5555 (0xB3, 0x2B)
                            var portsBytes = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0xB3, 0x2B, 0xB3, 0x2B });

                            var selectResponse = new Jondo.Protocol.GameMessage
                            {
                                AuthResult = new Jondo.Protocol.AuthenticationTicketResultMessage
                                {
                                    Lang = "1",
                                    SelectedServer = new Jondo.Protocol.SelectedServerData
                                    {
                                        Info = new Jondo.Protocol.ServerHostInfo
                                        {
                                            Ticket = Guid.NewGuid().ToString("N"),
                                            Address = "127.0.0.1",
                                            Ports = portsBytes
                                        }
                                    }
                                }
                            };

                            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(clientStream, selectResponse);
                            Console.WriteLine($"[Game Server] Sent SelectedServerData redirecting to dofus2-ga-talkasha.ankama-games.com:5555");
                            return; // Close Connection Server session immediately to prevent client timeout/deadlock
                        }
                    }
                }
                catch (Exception protoEx)
                {
                    Program.LogDebug($"[Game Server] Handled ancillary/probing packet: {protoEx.Message}");
                }

                payload = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(clientStream);
                if (payload == null) break;
                LogTraffic("C->S", payload, payload.Length);
            }
            Console.WriteLine("[-] Connection Server session closed.");
        }

        private static Jondo.Protocol.GameMessage GetModifiedAuthAcceptedMessage()
        {
            string hex = "92-03-12-8F-03-0A-01-30-1A-89-03-0A-86-03-08-E5-84-8C-5A-12-05-42-72-75-78-61-1A-04-34-36-31-37-22-DC-02-0A-07-0A-03-08-A7-02-10-01-0A-0B-0A-07-08-8E-07-10-01-18-04-10-03-0A-35-0A-05-08-A2-02-18-01-1A-2C-0A-05-42-72-75-78-61-10-08-18-01-20-02-2A-1D-32-30-32-36-2D-30-36-2D-32-32-54-32-33-3A-34-35-3A-30-31-2E-34-30-38-2B-30-32-3A-30-30-0A-0B-0A-07-08-91-07-10-01-18-04-10-03-0A-0B-0A-07-08-93-07-10-01-18-04-10-03-0A-07-0A-05-08-DF-02-18-03-0A-0B-0A-07-08-95-07-10-01-18-04-10-03-0A-09-0A-05-08-E3-02-18-02-10-01-0A-07-0A-05-08-A5-02-18-01-0A-07-0A-05-08-A6-02-18-01-0A-09-0A-05-08-E2-02-18-02-10-01-0A-07-0A-05-08-DE-02-18-03-0A-07-0A-05-08-A4-02-18-01-0A-0B-0A-07-08-94-07-10-01-18-04-10-03-0A-09-0A-05-08-E1-02-18-02-10-01-0A-0B-0A-07-08-92-07-10-01-18-04-10-03-0A-0B-0A-07-08-8D-07-10-01-18-04-10-03-0A-0B-0A-07-08-8F-07-10-01-18-04-10-03-0A-0B-0A-07-08-90-07-10-01-18-04-10-03-0A-06-0A-04-08-63-18-04-0A-0B-0A-07-08-96-07-10-01-18-04-10-03-0A-07-0A-05-08-E0-02-18-03-0A-08-0A-04-08-32-18-05-10-01-0A-07-0A-05-08-A3-02-18-01-12-02-10-05-12-04-08-01-10-05-12-04-08-02-10-05-12-04-08-03-10-05-12-04-08-04-10-05-12-04-08-05-10-05-12-04-08-06-10-05-2A-11-31-39-37-30-2D-30-31-2D-30-31-54-30-30-3A-30-30-5A-32-00";
            byte[] hexBytes = NetworkEnvelope.ConvertHexStringToByteArray(hex);
            byte[] protoPayload = new byte[hexBytes.Length - 2];
            Array.Copy(hexBytes, 2, protoPayload, 0, protoPayload.Length);
            var msg = Jondo.Protocol.GameMessage.Parser.ParseFrom(protoPayload);

            var dbChars = DatabaseManager.GetCharactersByAccountId(188940901);

            // Rename Bruxa to [#CADERNIS#] in all servers and character lists, keeping the original structure intact to prevent client UI crashes
            if (msg.AuthResult?.Result?.Accepted != null)
            {
                var accepted = msg.AuthResult.Result.Accepted;
                accepted.AccountName = "CADERNIS";
                accepted.AccountTag = "2026";
                accepted.SubscriptionEndDate = "2035-01-01T00:00:00Z";
                if (accepted.Servers != null)
                {
                    foreach (var sInfo in accepted.Servers.Servers)
                    {
                        if (sInfo.Characters != null && dbChars.Count > 0)
                        {
                            var dbChar = dbChars[0];
                            if (sInfo.Characters.Count > 0)
                            {
                                var firstChar = sInfo.Characters[0];
                                firstChar.Name = dbChar.Name;
                                firstChar.Level = dbChar.Level;
                                firstChar.Breed = dbChar.Breed;
                                firstChar.Gender = dbChar.Sex;
                            }
                        }
                    }
                }
            }
            return msg;
        }

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
