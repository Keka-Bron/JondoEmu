using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Thrift;
using Thrift.Protocol;
using Thrift.Transport;
using Thrift.Transport.Client;
using Thrift.Transport.Server;
using Zaap;

namespace Jondo.Unity.Server.Network
{
    public class ZaapHandler : ZaapService.IAsync
    {
        public Task<string> connect(string gameName, string releaseName, int instanceId, string hash, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[Thrift] connect(gameName: {gameName}, releaseName: {releaseName}, instanceId: {instanceId}, hash: {Censura.Valor(hash)})");
            if (!ClientLaunchRegistry.TryConnect(instanceId, hash, out string gameSession))
                throw new TApplicationException(TApplicationException.ExceptionType.InternalError,
                                                "Unknown launcher instance or invalid launch hash");
            return Task.FromResult(gameSession);
        }

        public Task<string> auth_getGameToken(string gameSession, int gameId, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[Thrift] auth_getGameToken(gameSession: {Censura.Valor(gameSession)}, gameId: {gameId})");
            if (!ClientLaunchRegistry.TryGetByGameSession(gameSession, out var launch) || launch == null)
                throw new TApplicationException(TApplicationException.ExceptionType.InternalError,
                                                "Unknown or expired game session");
            long accId = launch.AccountId;
            string token = Guid.NewGuid().ToString("N");
            DatabaseManager.SetGameToken(accId, token);
            ClientLaunchRegistry.RegisterToken(accId, token);
            return Task.FromResult(token);
        }

        public Task<string> settings_get(string gameSession, string key, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[Thrift] settings_get(gameSession: {Censura.Valor(gameSession)}, key: {key})");
            if (key == "autoConnectType") return Task.FromResult("0");
            if (key == "language" && ClientLaunchRegistry.TryGetByGameSession(gameSession, out var launch) && launch != null)
                return Task.FromResult(launch.Language);
            if (key == "connectionPort") return Task.FromResult("5555");
            throw new TApplicationException(TApplicationException.ExceptionType.MissingResult, "Setting not found");
        }

        public Task<string> userInfo_get(string gameSession, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[Thrift] userInfo_get(gameSession: {Censura.Valor(gameSession)})");
            if (!ClientLaunchRegistry.TryGetByGameSession(gameSession, out var launch) || launch == null)
                throw new TApplicationException(TApplicationException.ExceptionType.InternalError,
                                                "Unknown or expired game session");
            long accId = launch.AccountId;
            var account = DatabaseManager.GetAccountById(accId);
            if (account == null)
                throw new TApplicationException(TApplicationException.ExceptionType.InternalError,
                                                "Account no longer exists");
            string login = account.Login;
            string nick = account.Nickname;
            string json = $"{{\"id\":{accId},\"type\":\"ANKAMA\",\"login\":\"{login}\",\"nickname\":\"{nick}\",\"firstname\":\"{nick}\",\"lastname\":\"User\",\"nicknameWithTag\":\"{nick}#2026\",\"tag\":\"2026\",\"security\":[\"SHIELD\"],\"addedDate\":\"2026-06-21T22:51:08+02:00\",\"locked\":\"0\",\"parentEmailStatus\":null,\"avatar\":\"https://avatar.ankama.com/users/{accId}.png\",\"isGuest\":false,\"isErrored\":false,\"needRefresh\":true,\"isMain\":true,\"active\":true,\"acceptedTermsVersion\":14,\"all\":{{\"CGU\":\"14\"}},\"gameList\":[{{\"isFreeToPlay\":false,\"isFormerSubscriber\":false,\"isSubscribed\":true,\"totalPlayTime\":4065,\"endOfSubscribe\":\"2035-01-01T00:00:00Z\",\"id\":1}}]}}";
            return Task.FromResult(json);
        }

        public Task<string> updater_isUpdateAvailable(string gameSession, CancellationToken cancellationToken = default)
        {
            Console.WriteLine($"[Thrift] updater_isUpdateAvailable(gameSession: {Censura.Valor(gameSession)})");
            return Task.FromResult("");
        }
    }

    public class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _baseStream;
        private int _position;

        public PrefixedStream(byte[] prefix, int prefixLength, Stream baseStream)
        {
            _prefix = new byte[prefixLength];
            Array.Copy(prefix, _prefix, prefixLength);
            _baseStream = baseStream;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _baseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            if (_position < _prefix.Length)
            {
                int toCopy = Math.Min(count, _prefix.Length - _position);
                Array.Copy(_prefix, _position, buffer, offset, toCopy);
                _position += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
            }

            if (count > 0)
            {
                int read = _baseStream.Read(buffer, offset, count);
                totalRead += read;
            }

            return totalRead;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            if (_position < _prefix.Length)
            {
                int toCopy = Math.Min(count, _prefix.Length - _position);
                Array.Copy(_prefix, _position, buffer, offset, toCopy);
                _position += toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;
            }

            if (count > 0)
            {
                return _baseStream.ReadAsync(buffer, offset, count, cancellationToken).ContinueWith(t => totalRead + t.Result);
            }

            return Task.FromResult(totalRead);
        }

        public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _baseStream.WriteAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public static class ZaapServer
    {
        private static TcpListener? _tcpListener;
        private static bool _isRunning;
        public static bool IsRunning => _isRunning;
        private static CancellationTokenSource? _cts;

        public static void Start(int port)
        {
            if (_isRunning) return;
            _cts = new CancellationTokenSource();

            // La bandera se levanta DESPUÉS de que el puerto esté cogido de verdad. Estaba antes,
            // así que si el bind fallaba —el caso normal cuando ya hay otro servidor arriba—
            // IsRunning decía que sí y el semáforo del lanzador pintaba «en línea» con el listener
            // muerto. Con un solo proceso no se notaba porque el fallo mataba el emulador entero.
            _tcpListener = new TcpListener(ServerBinding.TcpAddress, port);
            _tcpListener.Start();
            _isRunning = true;

            Console.WriteLine($"[+] Emulating Ankama Zaap Server on TCP port {port} (auto-detect Thrift/WebSocket)");

            // 1. Start TCP listener loop
            _ = Task.Run(async () =>
            {
                while (_isRunning && _tcpListener != null)
                {
                    try
                    {
                        var client = await _tcpListener.AcceptTcpClientAsync(_cts.Token);
                        _ = Task.Run(() => HandleTcpClient(client));
                    }
                    catch (Exception ex)
                    {
                        if (!_isRunning) break;
                        Console.WriteLine($"[Zaap Accept Error] {ex.Message}");
                    }
                }
            });

            // 2. Start Named Pipe listener loop
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[Zaap Pipe] Listening on pipe: {port}");
                    while (_isRunning)
                    {
                        var pipeServer = new NamedPipeServerStream(port.ToString(), PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await pipeServer.WaitForConnectionAsync(_cts.Token);
                        Console.WriteLine("[+] Client connected to Zaap via Named Pipe!");

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var handler = new ZaapHandler();
                                var processor = new ZaapService.AsyncProcessor(handler);
                                var transport = new TStreamTransport(pipeServer, pipeServer, new TConfiguration());
                                var protocol = new TBinaryProtocol(transport);

                                Console.WriteLine("[Zaap Pipe] Processing Thrift connection...");
                                while (pipeServer.IsConnected && _isRunning)
                                {
                                    bool result = await processor.ProcessAsync(protocol, protocol, _cts.Token);
                                    if (!result) break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Zaap Pipe Error] {ex.Message}");
                            }
                            finally
                            {
                                pipeServer.Close();
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        Console.WriteLine($"[Zaap Pipe Listener Error] {ex.Message}");
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

        private static async Task HandleTcpClient(TcpClient client)
        {
            try
            {
                Console.WriteLine($"\n[+] Client connected to Zaap! ({client.Client.RemoteEndPoint})");
                var stream = client.GetStream();

                // Peek at first bytes to detect protocol
                byte[] peek = new byte[4];
                int peekRead = await stream.ReadAsync(peek, 0, 4);
                string peekStr = Encoding.ASCII.GetString(peek, 0, peekRead);

                if (peekStr.StartsWith("GET ") || peekStr.StartsWith("POST"))
                {
                    // HTTP/WebSocket - MelonLoader feedback or WebSocket client
                    Console.WriteLine($"[Zaap] HTTP request detected (MelonLoader feedback) - reading and ignoring");
                    byte[] httpBuf = new byte[4096];
                    int httpRead = await stream.ReadAsync(httpBuf, 0, httpBuf.Length);
                    string fullReq = peekStr + Encoding.UTF8.GetString(httpBuf, 0, httpRead);
                    string firstLine = fullReq.Split('\n')[0].Trim();
                    Console.WriteLine($"[Zaap] HTTP: {firstLine}");

                    if ((firstLine.Contains("/v2/feedbacks") || firstLine.Contains("/feedbacks")) &&
                        !fullReq.Contains("Upgrade: websocket") && !fullReq.Contains("Upgrade: WebSocket"))
                    {
                        string httpResp = "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: 2\r\n\r\n{}";
                        byte[] respBytes = Encoding.UTF8.GetBytes(httpResp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length);
                        Console.WriteLine("[Zaap] Sent 200 OK to feedbacks request and closed connection.");
                        return;
                    }

                    if (fullReq.Contains("Upgrade: websocket") || fullReq.Contains("Upgrade: WebSocket"))
                    {
                        var keyMatch = Regex.Match(fullReq, @"Sec-WebSocket-Key:\s*(.+?)\r?\n");
                        if (keyMatch.Success)
                        {
                            string key = keyMatch.Groups[1].Value.Trim();
                            string acceptKey = Convert.ToBase64String(
                                System.Security.Cryptography.SHA1.HashData(
                                    Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                            string wsResponse = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";
                            byte[] wsBytes = Encoding.UTF8.GetBytes(wsResponse);
                            await stream.WriteAsync(wsBytes, 0, wsBytes.Length);
                            Console.WriteLine("[Zaap] WebSocket handshake complete (MelonLoader)");

                            // Read WebSocket frames
                            try
                            {
                                while (client.Connected && _isRunning)
                                {
                                    byte[] header = new byte[2];
                                    int readHeader = await stream.ReadAsync(header, 0, 2);
                                    if (readHeader < 2) break;

                                    bool fin = (header[0] & 0b10000000) != 0;
                                    int opcode = header[0] & 0b00001111;
                                    bool mask = (header[1] & 0b10000000) != 0;
                                    int payloadLen = header[1] & 0b01111111;

                                    long length = payloadLen;
                                    if (payloadLen == 126)
                                    {
                                        byte[] extLen = new byte[2];
                                        await stream.ReadExactlyAsync(extLen, 0, 2);
                                        length = (extLen[0] << 8) | extLen[1];
                                    }
                                    else if (payloadLen == 127)
                                    {
                                        byte[] extLen = new byte[8];
                                        await stream.ReadExactlyAsync(extLen, 0, 8);
                                        if (BitConverter.IsLittleEndian) Array.Reverse(extLen);
                                        length = BitConverter.ToInt64(extLen, 0);
                                    }

                                    // The cap, BEFORE the allocation. With payloadLen == 127 the
                                    // length is eight bytes the client chooses, and the whole array
                                    // was reserved here before a single body byte had been read:
                                    // sending 0x7FFFFFFFFFFFFFFF and then nothing left the server
                                    // committing memory and waiting in the loop below. It takes no
                                    // authentication, only finishing the WebSocket upgrade, and it
                                    // does not count against the eight-client limit.
                                    //
                                    // Same bar as the game frames, 8 MB: what travels here is Thrift
                                    // of a few KB, so there is room to spare.
                                    if (length < 0 || length > Jondo.Protocol.NetworkMessage.MaxFrameLength)
                                    {
                                        Console.WriteLine($"[Zaap] Trama de {length} bytes: pasa del " +
                                                          "tope. Se cierra la conexión.");
                                        break;
                                    }

                                    byte[] maskingKey = new byte[4];
                                    if (mask)
                                    {
                                        await stream.ReadExactlyAsync(maskingKey, 0, 4);
                                    }

                                    byte[] payload = new byte[length];
                                    int totalRead = 0;
                                    while (totalRead < length)
                                    {
                                        int r = await stream.ReadAsync(payload, totalRead, (int)length - totalRead);
                                        if (r == 0) break;
                                        totalRead += r;
                                    }

                                    if (mask)
                                    {
                                        for (int i = 0; i < length; i++)
                                        {
                                            payload[i] = (byte)(payload[i] ^ maskingKey[i % 4]);
                                        }
                                    }

                                    if (opcode == 1)
                                    { // Text
                                        string msg = Encoding.UTF8.GetString(payload);
                                        Console.WriteLine($"[Zaap WS] <<< Received TEXT: {msg}");

                                        string reply = "{}";
                                        byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                                        byte[] frame = new byte[2 + replyBytes.Length];
                                        frame[0] = 0b10000001; // FIN + Text
                                        frame[1] = (byte)replyBytes.Length;
                                        Array.Copy(replyBytes, 0, frame, 2, replyBytes.Length);
                                        await stream.WriteAsync(frame, 0, frame.Length);
                                    }
                                    else if (opcode == 2)
                                    { // Binary
                                        Console.WriteLine($"[Zaap WS] <<< Received THRIFT BINARY! Length: {length}");
                                        try
                                        {
                                            using var memStream = new MemoryStream(payload);
                                            using var outStream = new MemoryStream();
                                            var inTransport = new TStreamTransport(memStream, memStream, new TConfiguration());
                                            var outTransport = new TStreamTransport(outStream, outStream, new TConfiguration());
                                            var inProtocol = new TBinaryProtocol(inTransport);
                                            var outProtocol = new TBinaryProtocol(outTransport);
                                            var processor = new ZaapService.AsyncProcessor(new ZaapHandler());

                                            bool result = await processor.ProcessAsync(inProtocol, outProtocol, _cts?.Token ?? CancellationToken.None);

                                            if (outStream.Length > 0)
                                            {
                                                byte[] respPayload = outStream.ToArray();
                                                int respLen = respPayload.Length;
                                                byte[] wsHeader;
                                                if (respLen <= 125)
                                                {
                                                    wsHeader = new byte[2];
                                                    wsHeader[0] = 0b10000010;
                                                    wsHeader[1] = (byte)respLen;
                                                }
                                                else
                                                {
                                                    wsHeader = new byte[4];
                                                    wsHeader[0] = 0b10000010;
                                                    wsHeader[1] = 126;
                                                    wsHeader[2] = (byte)(respLen >> 8);
                                                    wsHeader[3] = (byte)(respLen & 0xFF);
                                                }
                                                await stream.WriteAsync(wsHeader, 0, wsHeader.Length);
                                                await stream.WriteAsync(respPayload, 0, respPayload.Length);
                                                Console.WriteLine($"[Zaap WS] >>> Sent THRIFT BINARY reply! Length: {respLen}");
                                            }
                                        }
                                        catch (Exception thriftEx)
                                        {
                                            Console.WriteLine($"[Zaap WS] Thrift parsing error: {thriftEx}");
                                        }
                                    }
                                    else if (opcode == 8)
                                    { // Close
                                        Console.WriteLine($"[Zaap WS] <<< Received CLOSE");
                                        break;
                                    }
                                    else if (opcode == 9)
                                    { // Ping
                                        Console.WriteLine($"[Zaap WS] <<< Received PING");
                                        byte[] frame = new byte[2];
                                        frame[0] = 0b10001010; // FIN + Pong
                                        frame[1] = 0;
                                        await stream.WriteAsync(frame, 0, 2);
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[Zaap WS] <<< Received OPCODE {opcode}, length {length}");
                                    }
                                }
                            }
                            catch (Exception wsEx)
                            {
                                Console.WriteLine($"[Zaap WS] Connection lost: {wsEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        string httpResp = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}";
                        byte[] respBytes = Encoding.UTF8.GetBytes(httpResp);
                        await stream.WriteAsync(respBytes, 0, respBytes.Length);
                    }
                }
                else
                {
                    // Binary data - Dofus Client Zaap Thrift!
                    Console.WriteLine($"[Zaap] THRIFT BINARY detected! First 4 bytes: {BitConverter.ToString(peek, 0, peekRead)}");

                    var combinedStream = new PrefixedStream(peek, peekRead, stream);

                    try
                    {
                        var transport = new TStreamTransport(combinedStream, stream, new TConfiguration());
                        var protocol = new TBinaryProtocol(transport);
                        var processor = new ZaapService.AsyncProcessor(new ZaapHandler());
                        Console.WriteLine("[Zaap] Processing Thrift request...");
                        while (_isRunning)
                        {
                            if (!await processor.ProcessAsync(protocol, protocol, _cts?.Token ?? CancellationToken.None)) break;
                        }
                    }
                    catch (Exception thriftEx)
                    {
                        Console.WriteLine($"[Zaap] Thrift processing error: {thriftEx.GetType().Name}: {thriftEx.Message}");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[-] Zaap Connection Error: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                client.Close();
            }
        }
    }
}
