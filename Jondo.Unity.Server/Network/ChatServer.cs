using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The Ankama chat service, where the friends window's first section lives: the Ankama friends,
    /// their invitations, whether they are connected and what they are playing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client reaches it at the chatServerHost/chatServerPort the Haapi configuration gives
    /// (<see cref="HaapiServer"/>), over TLS, with Ankama's Spin transport (package
    /// com.ankama.spinconnection, read in the client):
    /// </para>
    /// <code>
    ///   frame    [length, 4 bytes big-endian, counting itself] [type, 1 byte] [payload]
    ///   types    0 application, 1 ping, 2 pong, 3 heartbeat, 4 compressed application, 5 capabilities
    /// </code>
    /// <para>
    /// The client's first application message is its credentials, in JSON: the game token the
    /// launcher handed it (Zaap's auth_getGameToken for the chat's application id) and its device.
    /// SpinProtocol.CheckAuthentication reads the answer as JSON too: <c>{"success":true}</c>, or
    /// <c>{"success":false,"errCode":N}</c> with a ConnectionErrors value (1 bad credentials). The
    /// client pings every three seconds when it keeps statistics and expects the payload back in a
    /// pong; it sends a heartbeat when it has been quiet. A ping from the server it answers itself.
    /// </para>
    /// <para>
    /// After that every application message is a protobuf Frame of the client's
    /// Core.Services.ChatService.AnkamaChatService.Protocol (field numbers checked against its
    /// FieldNumber constants):
    /// </para>
    /// <code>
    ///   Frame    { 1 Request { 1 CorrelationId, 2 Payload }, 2 Response { 1 CorrelationId,
    ///              2 Status, 3 Payload }, 3 Event Payload }
    ///   Payload  { 1 Id, 2 Data }      Id names the message: the client's table (esh) of ids
    ///   Status   1 success, 2 forbidden, 3 bad request, 4 timeout, 5 failure
    /// </code>
    /// <para>
    /// The client matches a response to its request by the correlation id and takes its payload
    /// already parsed by the id (ers.bbtz); a list it asked for comes back only with status success
    /// and the payload of the right type, or it asks again. A user is { UserId, Name, Tag }: the
    /// account id -- the client's own comes from its player service, which has it from the
    /// authentication --, the nickname and the tag the game shows next to it.
    /// </para>
    /// </remarks>
    public static class ChatServer
    {
        internal const byte Application = 0, Ping = 1, Pong = 2, Heartbeat = 3, Compressed = 4;

        internal const int Success = 1, Forbidden = 2, BadRequest = 3, Failure = 5;

        /// <summary>DeleteFriendInviteCmd.Reason.</summary>
        internal const int Accepted = 0, Rejected = 1, Canceled = 2;

        internal const int Online = 0, Offline = 1;

        /// <summary>The Frame.Payload ids of the messages used here, from the client's table.</summary>
        internal static class Ids
        {
            public const uint ChannelMessage = 3225307124, ChannelMessageCreatedEvt = 804239649,
                CreateChannelMessageCmd = 1835442294, CreateFriendInviteCmd = 3628550494,
                DeleteFriendInviteCmd = 1707706475, FriendInvite = 1982397216,
                FriendInviteCreatedEvt = 775219117, FriendInviteDeletedEvt = 4014599252,
                FriendInviteList = 425860746, DeleteUserFriendCmd = 926307863,
                FriendList = 3144175698, ListUserFriendInvitesCmd = 669955095,
                ListUserFriendsCmd = 2361284692, UpdateUserEndpointPropertiesCmd = 4221616398,
                UpdateUserStatusCmd = 172854262, UserEndpointPropertiesUpdatedEvt = 1572432262,
                UserFriendCreatedEvt = 1611743244, UserFriendDeletedEvt = 2708229109,
                UserPresenceUpdatedEvt = 4166108330, UserStatusUpdatedEvt = 3069597458,
                ListUserBlockedUsersCmd = 937824261, UserList = 3164896919,
                ListUserFriendGroupsCmd = 1348064163, FriendGroupList = 4207114165;
        }

        /// <summary>The longest frame taken; the client's own limit is of the same order.</summary>
        private const int MaxFrame = 1 << 20;

        private static TcpListener? _listener;
        private static bool _isRunning;
        private static X509Certificate2? _certificate;

        private static readonly ConcurrentDictionary<Guid, Connection> _connections = new();
        private static readonly ConcurrentDictionary<long, int> _status = new();

        /// <summary>One client connected to the service.</summary>
        internal sealed class Connection
        {
            private readonly Stream _stream;
            private readonly SemaphoreSlim _write = new SemaphoreSlim(1, 1);

            public Guid Id { get; } = Guid.NewGuid();
            public long AccountId { get; set; }

            /// <summary>The last EndpointProperties this client declared, as it sent them.</summary>
            public byte[]? Properties { get; set; }

            public Connection(Stream stream) => _stream = stream;

            public async Task SendAsync(byte type, byte[] payload)
            {
                var frame = new byte[5 + payload.Length];
                int length = frame.Length;
                frame[0] = (byte)(length >> 24);
                frame[1] = (byte)(length >> 16);
                frame[2] = (byte)(length >> 8);
                frame[3] = (byte)length;
                frame[4] = type;
                Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
                await _write.WaitAsync();
                try
                {
                    await _stream.WriteAsync(frame, 0, frame.Length);
                    await _stream.FlushAsync();
                }
                finally
                {
                    _write.Release();
                }
            }

            public Task SendFrameAsync(Pb frame) => SendAsync(Application, frame.Build());
        }

        public static void Start(int port)
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                _certificate = GenerateSelfSignedCertificate();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] Chat Server: could not generate its certificate: {ex.Message}");
            }

            _listener = new TcpListener(ServerBinding.TcpAddress, port);
            _listener.Start();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[+] Chat Server (Spin over TLS) listening on TCP port {port}");
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
                    client.NoDelay = true;   // see GameNodeProxy: no frame waits on an ACK
                    _ = HandleClientAsync(client);
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Console.WriteLine($"[Chat Server Error] {ex.Message}");
                }
            }
        }

        private static Task HandleClientAsync(TcpClient client)
            => HandleClientAsync(client, _certificate, Console.WriteLine);

        internal static async Task HandleClientAsync(TcpClient client, X509Certificate2? certificate, Action<string> log)
        {
            using (client)
            {
                if (certificate == null)
                {
                    log("[-] Chat Server: no certificate, the connection is closed.");
                    return;
                }

                string peer = "unknown";
                try
                {
                    // Disposing TLS closes its underlying socket before the catch block runs.
                    // Keep diagnostics independent of that socket, including early disposal races.
                    peer = client.Client?.RemoteEndPoint?.ToString() ?? peer;
                    using var tls = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) => true);
                    await tls.AuthenticateAsServerAsync(certificate);
                    await ServeAsync(tls);
                }
                catch (Exception ex)
                {
                    log($"[Chat Server] Connection from {peer} ended: {ex.Message}");
                }
            }
        }

        /// <summary>A client's whole session on the service, on a stream already past TLS.</summary>
        internal static async Task ServeAsync(Stream stream)
        {
            var connection = new Connection(stream);
            try
            {
                while (true)
                {
                    var message = await ReadAsync(stream);
                    if (message == null) break;
                    var (type, payload) = message.Value;

                    if (connection.AccountId == 0)
                    {
                        if (type != Application) continue;
                        if (!await AuthenticateAsync(connection, payload)) break;
                        continue;
                    }

                    switch (type)
                    {
                        case Ping:
                            await connection.SendAsync(Pong, payload);
                            break;
                        case Application:
                            await HandleFrameAsync(connection, payload);
                            break;
                        case Compressed:
                            Console.WriteLine("[Chat Server] A compressed message was not read: none was offered.");
                            break;
                        // A heartbeat only says the client is there; a pong answers nothing of ours.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
            {
                // The client went away.
            }
            finally
            {
                _connections.TryRemove(connection.Id, out _);
                long account = connection.AccountId;
                if (account > 0 && !IsConnected(account))
                {
                    await TellFriendsAsync(account, Ids.UserPresenceUpdatedEvt,
                        Pb.New().Var(1, account).Var(2, Offline));
                    Console.WriteLine($"[Chat Server] Account {account} left the chat service.");
                }
            }
        }

        // ─── the transport ───────────────────────────────────────────────────────────────────

        private static async Task<(byte Type, byte[] Payload)?> ReadAsync(Stream stream)
        {
            var header = new byte[4];
            if (!await ReadExactAsync(stream, header)) return null;
            int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (length < 5 || length > MaxFrame) throw new InvalidDataException($"Spin frame of {length} bytes.");

            var body = new byte[length - 4];
            if (!await ReadExactAsync(stream, body)) return null;
            return (body[0], body.AsSpan(1).ToArray());
        }

        private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, read, buffer.Length - read);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        /// <summary>The credentials: the launcher's token names the account, or nobody.</summary>
        private static async Task<bool> AuthenticateAsync(Connection connection, byte[] payload)
        {
            string token = "";
            try
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.TryGetProperty("token", out var value) && value.ValueKind == JsonValueKind.String)
                    token = value.GetString() ?? "";
            }
            catch (JsonException)
            {
            }

            long account = ClientLaunchRegistry.ResolveToken(token);
            if (account <= 0)
            {
                await connection.SendAsync(Application, Encoding.UTF8.GetBytes("{\"success\":false,\"errCode\":1}"));
                Console.WriteLine("[Chat Server] A client came with a token nobody was given: refused.");
                return false;
            }

            bool first = !IsConnected(account);
            connection.AccountId = account;
            _connections[connection.Id] = connection;
            await connection.SendAsync(Application, Encoding.UTF8.GetBytes("{\"success\":true}"));
            Console.WriteLine($"[Chat Server] Account {account} joined the chat service.");

            if (first)
                await TellFriendsAsync(account, Ids.UserPresenceUpdatedEvt, Pb.New().Var(1, account).Var(2, Online));
            return true;
        }

        // ─── the requests ────────────────────────────────────────────────────────────────────

        private static async Task HandleFrameAsync(Connection connection, byte[] payload)
        {
            var request = Field(ProtoMessage.Parse(payload), 1);
            if (request == null) return;
            int correlation = (int)Var(request, 1);
            var inner = Field(request, 2);
            if (inner == null) return;
            uint id = (uint)Var(inner, 1);
            var data = ProtoMessage.Parse(Bytes(inner, 2));
            long me = connection.AccountId;

            switch (id)
            {
                case Ids.ListUserFriendsCmd:
                {
                    var list = Pb.New();
                    foreach (long friend in AnkamaFriends.FriendsOf(me)) list.Msg(1, Pb.New().Msg(1, User(friend)));
                    await RespondAsync(connection, correlation, Success, Ids.FriendList, list);
                    await TellPresencesAsync(connection);
                    break;
                }
                case Ids.ListUserFriendInvitesCmd:
                {
                    var list = Pb.New();
                    foreach (var (inviter, recipient) in AnkamaFriends.InvitesOf(me)) list.Msg(1, Invite(inviter, recipient));
                    await RespondAsync(connection, correlation, Success, Ids.FriendInviteList, list);
                    break;
                }
                case Ids.ListUserBlockedUsersCmd:
                    await RespondAsync(connection, correlation, Success, Ids.UserList, Pb.New());
                    break;
                case Ids.ListUserFriendGroupsCmd:
                    await RespondAsync(connection, correlation, Success, Ids.FriendGroupList, Pb.New());
                    break;
                case Ids.CreateFriendInviteCmd:
                    await InviteAsync(connection, correlation, data);
                    break;
                case Ids.DeleteFriendInviteCmd:
                    await AnswerInviteAsync(connection, correlation, data);
                    break;
                case Ids.DeleteUserFriendCmd:
                    await UnfriendAsync(connection, correlation, data);
                    break;
                case Ids.UpdateUserStatusCmd:
                {
                    int status = (int)Var(data, 2);
                    _status[me] = status;
                    await RespondAsync(connection, correlation, Success);
                    await TellFriendsAsync(me, Ids.UserStatusUpdatedEvt, Pb.New().Var(1, me).Var(2, status));
                    break;
                }
                case Ids.UpdateUserEndpointPropertiesCmd:
                    connection.Properties = Bytes(data, 2);
                    await RespondAsync(connection, correlation, Success);
                    await TellFriendsAsync(me, Ids.UserEndpointPropertiesUpdatedEvt, PropertiesOf(me));
                    break;
                case Ids.CreateChannelMessageCmd:
                    await SayAsync(connection, correlation, data);
                    break;
                default:
                    // Nothing else is sent by the 3.6 client; whatever it is is acknowledged.
                    await RespondAsync(connection, correlation, Success);
                    Console.WriteLine($"[Chat Server] Request {id} of account {me} acknowledged without content.");
                    break;
            }
        }

        /// <summary>CreateFriendInviteCmd: by the recipient's id, or by his nickname and tag.</summary>
        private static async Task InviteAsync(Connection connection, int correlation, ProtoMessage command)
        {
            long me = connection.AccountId;
            long recipient = Var(command, 2);
            var byName = Field(command, 4);
            if (recipient <= 0 && byName != null)
            {
                string name = Text(byName, 1), tag = Text(byName, 2);
                long found = DatabaseManager.AccountIdOfNickname(name);
                if (found > 0 && (tag.Length == 0 || tag == GameServerProxy.BuildAccountTag(found))) recipient = found;
            }

            if (recipient <= 0 || recipient == me)
            {
                await RespondAsync(connection, correlation, BadRequest);
                return;
            }
            if (AnkamaFriends.AreFriends(me, recipient) || AnkamaFriends.IsInvited(me, recipient))
            {
                await RespondAsync(connection, correlation, Failure);
                return;
            }

            // Invited by the other already: inviting back is accepting.
            if (AnkamaFriends.IsInvited(recipient, me))
            {
                await RespondAsync(connection, correlation, Success, Ids.FriendInvite, Invite(me, recipient));
                await AcceptAsync(recipient, me);
                return;
            }

            AnkamaFriends.Invite(me, recipient);
            await RespondAsync(connection, correlation, Success, Ids.FriendInvite, Invite(me, recipient));
            var created = Pb.New().Msg(1, Invite(me, recipient));
            await TellAsync(me, Ids.FriendInviteCreatedEvt, created);
            await TellAsync(recipient, Ids.FriendInviteCreatedEvt, created);
            Console.WriteLine($"[Chat Server] Account {me} invited {recipient} to be Ankama friends.");
        }

        /// <summary>DeleteFriendInviteCmd: the recipient accepts or rejects, the inviter withdraws.</summary>
        private static async Task AnswerInviteAsync(Connection connection, int correlation, ProtoMessage command)
        {
            long me = connection.AccountId;
            long inviter = Var(command, 1), recipient = Var(command, 2);
            int reason = (int)Var(command, 3);
            bool mine = me == inviter || me == recipient;
            if (!mine || !AnkamaFriends.IsInvited(inviter, recipient) || (reason == Accepted && me != recipient))
            {
                await RespondAsync(connection, correlation, Forbidden);
                return;
            }

            await RespondAsync(connection, correlation, Success);
            if (reason == Accepted)
            {
                await AcceptAsync(inviter, recipient);
                return;
            }

            AnkamaFriends.Withdraw(inviter, recipient);
            var deleted = Pb.New().Msg(1, Invite(inviter, recipient)).Var(2, reason);
            await TellAsync(inviter, Ids.FriendInviteDeletedEvt, deleted);
            await TellAsync(recipient, Ids.FriendInviteDeletedEvt, deleted);
        }

        /// <summary>An accepted invitation: both are friends, and each sees the other connected or not.</summary>
        private static async Task AcceptAsync(long inviter, long recipient)
        {
            AnkamaFriends.Withdraw(inviter, recipient);
            AnkamaFriends.Withdraw(recipient, inviter);
            AnkamaFriends.Befriend(inviter, recipient);

            var deleted = Pb.New().Msg(1, Invite(inviter, recipient)).Var(2, Accepted);
            await TellAsync(inviter, Ids.FriendInviteDeletedEvt, deleted);
            await TellAsync(recipient, Ids.FriendInviteDeletedEvt, deleted);

            foreach (var (one, other) in new[] { (inviter, recipient), (recipient, inviter) })
            {
                await TellAsync(one, Ids.UserFriendCreatedEvt, Pb.New().Var(1, one).Msg(2, Pb.New().Msg(1, User(other))));
                await TellAsync(one, Ids.UserPresenceUpdatedEvt,
                    Pb.New().Var(1, other).Var(2, IsConnected(other) ? Online : Offline));
            }
            Console.WriteLine($"[Chat Server] Accounts {inviter} and {recipient} are Ankama friends.");
        }

        /// <summary>DeleteUserFriendCmd: the friendship ends for both.</summary>
        private static async Task UnfriendAsync(Connection connection, int correlation, ProtoMessage command)
        {
            long me = connection.AccountId;
            long friend = Var(command, 2);
            if (!AnkamaFriends.Unfriend(me, friend))
            {
                await RespondAsync(connection, correlation, Failure);
                return;
            }
            await RespondAsync(connection, correlation, Success);
            await TellAsync(me, Ids.UserFriendDeletedEvt, Pb.New().Var(1, me).Var(2, friend));
            await TellAsync(friend, Ids.UserFriendDeletedEvt, Pb.New().Var(1, friend).Var(2, me));
            Console.WriteLine($"[Chat Server] Accounts {me} and {friend} are no longer Ankama friends.");
        }

        /// <summary>
        /// CreateChannelMessageCmd: a line to a friend. The client names the two's channel
        /// «DM_{lower id}_{higher id}» (ers.bbua).
        /// </summary>
        private static async Task SayAsync(Connection connection, int correlation, ProtoMessage command)
        {
            long me = connection.AccountId;
            string channel = Text(command, 1), content = Text(command, 3);
            var members = MembersOf(channel);
            if (!members.Contains(me) || content.Length == 0)
            {
                await RespondAsync(connection, correlation, Forbidden);
                return;
            }

            var message = Pb.New()
                .Str(1, Guid.NewGuid().ToString("N"))
                .Str(2, channel)
                .Var(3, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Str(4, content)
                .Msg(5, User(me));
            await RespondAsync(connection, correlation, Success, Ids.ChannelMessage, message);
            foreach (long member in members)
                await TellAsync(member, Ids.ChannelMessageCreatedEvt, Pb.New().Msg(1, message));
        }

        private static List<long> MembersOf(string channel)
        {
            var parts = channel.Split('_');
            var members = new List<long>();
            if (parts.Length == 3 && parts[0] == "DM" && long.TryParse(parts[1], out long a) && long.TryParse(parts[2], out long b)
                && AnkamaFriends.AreFriends(a, b))
            {
                members.Add(a);
                members.Add(b);
            }
            return members;
        }

        // ─── what friends see of each other ──────────────────────────────────────────────────

        /// <summary>After his list, the newcomer learns which friends are connected, their status and their game.</summary>
        private static async Task TellPresencesAsync(Connection connection)
        {
            foreach (long friend in AnkamaFriends.FriendsOf(connection.AccountId))
            {
                bool connected = IsConnected(friend);
                await connection.SendFrameAsync(Event(Ids.UserPresenceUpdatedEvt,
                    Pb.New().Var(1, friend).Var(2, connected ? Online : Offline)));
                if (!connected) continue;
                if (_status.TryGetValue(friend, out int status))
                    await connection.SendFrameAsync(Event(Ids.UserStatusUpdatedEvt, Pb.New().Var(1, friend).Var(2, status)));
                await connection.SendFrameAsync(Event(Ids.UserEndpointPropertiesUpdatedEvt, PropertiesOf(friend)));
            }
        }

        private static Pb PropertiesOf(long account)
        {
            var evt = Pb.New().Var(1, account);
            foreach (var c in ConnectionsOf(account))
                if (c.Properties != null) evt.Bytes(2, c.Properties);
            return evt;
        }

        // ─── building ────────────────────────────────────────────────────────────────────────

        /// <summary>User: the account, its nickname and the tag shown next to it.</summary>
        internal static Pb User(long accountId)
        {
            var (name, tag) = Handlers.HouseHandler.AccountTagOf(accountId);
            return Pb.New().Var(1, accountId).Str(2, name).Str(3, tag);
        }

        private static Pb Invite(long inviter, long recipient)
            => Pb.New().Msg(1, User(inviter)).Msg(2, User(recipient));

        private static Pb Payload(uint id, Pb data) => Pb.New().Var(1, (int)id).Bytes(2, data.Build());

        internal static Pb Event(uint id, Pb data) => Pb.New().Msg(3, Payload(id, data));

        private static Task RespondAsync(Connection connection, int correlation, int status, uint id = 0, Pb? data = null)
        {
            var response = Pb.New().Var(1, correlation).Var(2, status);
            if (data != null) response.Msg(3, Payload(id, data));
            return connection.SendFrameAsync(Pb.New().Msg(2, response));
        }

        // ─── sending to accounts ─────────────────────────────────────────────────────────────

        internal static bool IsConnected(long account) => _connections.Values.Any(c => c.AccountId == account);

        private static IEnumerable<Connection> ConnectionsOf(long account)
            => _connections.Values.Where(c => c.AccountId == account).ToArray();

        private static async Task TellAsync(long account, uint id, Pb data)
        {
            byte[] frame = Event(id, data).Build();
            foreach (var c in ConnectionsOf(account))
            {
                try
                {
                    await c.SendAsync(Application, frame);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Chat Server] Could not reach account {account}: {ex.Message}");
                }
            }
        }

        private static async Task TellFriendsAsync(long account, uint id, Pb data)
        {
            foreach (long friend in AnkamaFriends.FriendsOf(account))
                await TellAsync(friend, id, data);
        }

        // ─── reading ─────────────────────────────────────────────────────────────────────────

        private static ProtoMessage? Field(ProtoMessage message, int number)
        {
            var f = message.Fields.FirstOrDefault(x => x.FieldNumber == number && x.WireType == 2);
            return f == null ? null : ProtoMessage.Parse(f.BytesValue);
        }

        private static byte[] Bytes(ProtoMessage message, int number)
            => message.Fields.FirstOrDefault(x => x.FieldNumber == number && x.WireType == 2)?.BytesValue ?? Array.Empty<byte>();

        private static long Var(ProtoMessage message, int number)
            => message.Fields.FirstOrDefault(x => x.FieldNumber == number && x.WireType == 0)?.VarIntValue ?? 0;

        private static string Text(ProtoMessage message, int number)
        {
            try { return Encoding.UTF8.GetString(Bytes(message, number)).Trim(); }
            catch (Exception) { return ""; }
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
