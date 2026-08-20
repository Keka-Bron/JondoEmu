using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Network
{
    public static class ChatServer
    {
        private static TcpListener? _listener;
        private static bool _isRunning;
        private static X509Certificate2? _certificate;

        public static void Start(int port)
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                Console.WriteLine("[Chat Server] Generating self-signed SSL/TLS certificate...");
                _certificate = GenerateSelfSignedCertificate();
                Console.WriteLine("[Chat Server] Certificate generated successfully.");
                Console.WriteLine($"  - Subject: {_certificate.Subject}");
                Console.WriteLine($"  - Issuer: {_certificate.Issuer}");
                Console.WriteLine($"  - Serial Number: {_certificate.SerialNumber}");
                Console.WriteLine($"  - Thumbprint: {_certificate.Thumbprint}");
                Console.WriteLine($"  - Valid From: {_certificate.NotBefore}");
                Console.WriteLine($"  - Valid To: {_certificate.NotAfter}");
                Console.WriteLine($"  - Has Private Key: {_certificate.HasPrivateKey}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Error generating certificate: {ex.Message}");
            }

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[+] Chat Server (Mock/TLS) listening on TCP port {port}");
            Console.ResetColor();

            _ = AcceptConnectionsAsync();
        }

        public static void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _listener?.Stop();
            _listener = null;
            _certificate?.Dispose();
            _certificate = null;
        }

        private static async Task AcceptConnectionsAsync()
        {
            while (_isRunning && _listener != null)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client);
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Console.WriteLine($"[Chat Server Error] {ex.Message}");
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Chat Server] Client connected from {client.Client.RemoteEndPoint}. Initiating TLS handshake...");
            Console.ResetColor();

            if (_certificate == null)
            {
                Console.WriteLine("[-] Chat Server: Cannot proceed, certificate is null.");
                client.Close();
                return;
            }

            try
            {
                // Wrap in SslStream with a callback that accepts any certificate
                var sslStream = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) => true);
                
                // Authenticate as server
                await sslStream.AuthenticateAsServerAsync(_certificate);
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[Chat Server] TLS handshake completed successfully with {client.Client.RemoteEndPoint}!");
                Console.ResetColor();

                _ = ReadLoopAsync(client, sslStream);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[-] Chat Server: TLS handshake failed with {client.Client.RemoteEndPoint}:");
                Console.WriteLine(ex.ToString());
                Console.ResetColor();
                client.Close();
            }
        }

        private static async Task ReadLoopAsync(TcpClient client, SslStream sslStream)
        {
            using (client)
            using (sslStream)
            {
                byte[] buffer = new byte[4096];
                while (_isRunning && client.Connected)
                {
                    try
                    {
                        int read = await sslStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            Console.WriteLine("[Chat Server] Client disconnected from TLS session.");
                            break;
                        }

                        string hex = BitConverter.ToString(buffer, 0, read);
                        string ascii = Encoding.ASCII.GetString(buffer, 0, read);
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"[Chat Server] Received {read} bytes decrypted.");
                        Console.WriteLine($"[Chat Server] Hex: {hex}");
                        Console.WriteLine($"[Chat Server] ASCII: {ascii}");
                        Console.ResetColor();

                        // Respond to client authentication message
                        if (ascii.Contains("\"token\""))
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[Chat Server] Auth Token detected! Sending Success Response...");
                            Console.ResetColor();
                            
                            string jsonResponse = "{\"success\":true}";
                            byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonResponse);
                            byte[] responseBuffer = new byte[4 + 1 + jsonBytes.Length];
                            
                            int len = jsonBytes.Length;
                            responseBuffer[0] = (byte)((len >> 24) & 0xFF);
                            responseBuffer[1] = (byte)((len >> 16) & 0xFF);
                            responseBuffer[2] = (byte)((len >> 8) & 0xFF);
                            responseBuffer[3] = (byte)(len & 0xFF);
                            
                            responseBuffer[4] = 0; // MessageType.Application = 0
                            
                            Array.Copy(jsonBytes, 0, responseBuffer, 5, jsonBytes.Length);
                            
                            await sslStream.WriteAsync(responseBuffer, 0, responseBuffer.Length);
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("[Chat Server] Sent Auth Success Response!");
                            Console.ResetColor();
                        }


                        // Simple HTTP/WebSocket detection to help client if it's a websocket connection
                        if (ascii.Contains("GET ") && ascii.Contains("Upgrade: websocket"))
                        {
                            Console.WriteLine("[Chat Server] WebSocket Handshake detected! Sending response...");
                            string response = BuildWebSocketResponse(ascii);
                            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                            await sslStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                            Console.WriteLine("[Chat Server] WebSocket Handshake response sent.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Chat Server] TLS connection error: {ex.Message}");
                        break;
                    }
                }
            }
        }

        private static string BuildWebSocketResponse(string request)
        {
            string key = "";
            string[] lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(key)) return "";

            string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            string concat = key + guid;
            byte[] sha1Bytes;
            using (var sha1 = SHA1.Create())
            {
                sha1Bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(concat));
            }
            string acceptKey = Convert.ToBase64String(sha1Bytes);

            return "HTTP/1.1 101 Switching Protocols\r\n" +
                   "Upgrade: websocket\r\n" +
                   "Connection: Upgrade\r\n" +
                   $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
        }

        private static X509Certificate2 GenerateSelfSignedCertificate()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=127.0.0.1",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddIpAddress(IPAddress.Parse("127.0.0.1"));
                sanBuilder.AddDnsName("localhost");
                request.CertificateExtensions.Add(sanBuilder.Build());

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));

                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        false));

                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                        false));

                var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(5));
                return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.MachineKeySet);
            }
        }
    }
}
