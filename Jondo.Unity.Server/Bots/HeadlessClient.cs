using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Connection;
using Jondo.Unity.Protocol.Connection.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// A client without a screen for one of the test harness's slots: the slot's account comes in
    /// as the client does -- a token, the connection server, a ticket, the character list, the
    /// world -- over a socket of its own, and then stays there doing nothing of its own accord.
    /// </summary>
    /// <remarks>
    /// Asked for on 2026-11-17: the debugging tools (Jondo.Unity.Mcp) play the server through the
    /// /api/test routes, and those need the slot's character in the world. Without this, that meant
    /// starting the Dofus client and waiting for Unity; with it, a character is in within a second.
    ///
    /// It is a <see cref="SimulatedPlayer"/> with no wits: it answers what a client must answer for
    /// the server to go on -- the maps it is put on, «got it» before every turn (jwz), every sequence
    /// closed (jti), the heartbeat, and «ready» in a placement when asked to -- and nothing else.
    /// Its turns are played by whoever drives it, through the test routes or <see cref="SendAsync"/>.
    ///
    /// Unlike the JondoBots, its socket is not a bot's (<see cref="SimulatedPlayers.IsBotSocket"/>)
    /// and its lines are not quieted: the server logs it as any player, which is the point.
    /// </remarks>
    internal sealed class HeadlessClient
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        /// <summary>How many of the frames the server sent it are kept, the newest.</summary>
        private const int FramesKept = 2000;

        private static readonly ConcurrentDictionary<int, HeadlessClient> BySlot = new();

        private readonly int _slot;
        private readonly long _accountId;
        private readonly string _login;
        private readonly string _wanted;
        private readonly SemaphoreSlim _writing = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<Frame> _frames = new();
        private long _sequence;

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private volatile bool _closed;
        private bool _fightMapComing;

        private TaskCompletionSource<List<(long Id, string Name)>>? _list;
        private TaskCompletionSource<bool>? _selected;

        /// <summary>A frame the server sent: its order, when, its op and its payload.</summary>
        internal sealed record Frame(long Id, DateTime At, string Op, byte[] Payload);

        private HeadlessClient(int slot, long accountId, string login, string character, bool autoReady)
        {
            _slot = slot;
            _accountId = accountId;
            _login = login;
            _wanted = character;
            AutoReady = autoReady;
        }

        /// <summary>Its character, once chosen.</summary>
        public long CharacterId { get; private set; }

        public string CharacterName { get; private set; } = "";

        /// <summary>Says «ready» (kaq) on its own when a fight's placement starts.</summary>
        public bool AutoReady { get; set; }

        public bool Connected => !_closed && _stream != null;

        /// <summary>The newest frame's number: what <see cref="FramesSince"/> is asked from.</summary>
        public long LastFrame => Interlocked.Read(ref _sequence);

        public static HeadlessClient? Of(int slot)
            => BySlot.TryGetValue(slot, out var client) && client.Connected ? client : null;

        /// <summary>
        /// The slot's account in the world with <paramref name="character"/> (its name, or the first
        /// one of the account when empty). The one there before for the slot is let go first.
        /// Returns the error's code, or null once the character stands on its map.
        /// </summary>
        public static async Task<(HeadlessClient? Client, string? Error)> ConnectAsync(int slot, long accountId, string login,
                                                                                      string character, bool autoReady)
        {
            Disconnect(slot);
            var client = new HeadlessClient(slot, accountId, login, character, autoReady);
            BySlot[slot] = client;
            string? error;
            try { error = await client.EnterAsync(); }
            catch (Exception ex) when (ex is SocketException or System.IO.IOException or ObjectDisposedException)
            {
                error = "socket: " + ex.Message;
            }
            if (error != null)
            {
                client.Close();
                BySlot.TryRemove(new KeyValuePair<int, HeadlessClient>(slot, client));
                return (null, error);
            }
            Console.WriteLine($"[Headless] Slot {slot}: {client.CharacterName} is in the world.");
            return (client, null);
        }

        /// <summary>The slot's headless client out: its socket closed, as a player who leaves.</summary>
        public static bool Disconnect(int slot)
        {
            if (!BySlot.TryRemove(slot, out var client)) return false;
            client.Close();
            Console.WriteLine($"[Headless] Slot {slot}: disconnected.");
            return true;
        }

        private void Close()
        {
            _closed = true;
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            try { _tcp?.Close(); } catch { }
        }

        // ─── The way in ──────────────────────────────────────────────────────────────────────

        private async Task<string?> EnterAsync()
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            ClientLaunchRegistry.RegisterToken(_accountId, token);

            string? ticket;
            using (var connection = await DialAsync())
                ticket = await TicketAsync(connection.GetStream(), token);
            if (ticket == null) return "ticket";

            _tcp = await DialAsync();
            _stream = _tcp.GetStream();
            _ = Task.Run(ReadAsync);

            _list = new TaskCompletionSource<List<(long, string)>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _selected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendAsync(Op.Kqz, Pb.New().Str(2, ticket).Str(3, "es").Build());
            await SendAsync("krt");

            var characters = await WithinAsync(_list.Task);
            if (characters == null) return "no-character-list";
            var mine = _wanted.Length == 0
                ? characters.FirstOrDefault()
                : characters.FirstOrDefault(c => string.Equals(c.Name, _wanted, StringComparison.OrdinalIgnoreCase));
            if (mine.Id == 0) return characters.Count == 0 ? "no-characters" : $"no-character-{_wanted}";
            CharacterId = mine.Id;
            CharacterName = mine.Name;

            // A character still in a fight is not selected: the kvd's «go on» (kwb) put it back already.
            if (!_selected.Task.IsCompleted) await SendAsync(Op.Kvw, Pb.New().Var(1, CharacterId).Build());
            if (!await WithinAsync(_selected.Task)) return "not-selected";
            await SendAsync(Op.Lqc, Pb.New().Var(1, 1).Build());

            for (int waited = 0; waited < 300; waited++)
            {
                if (SessionRegistry.FindByCharacter(CharacterId)?.IsInWorld == true) break;
                await Task.Delay(100, _stop.Token);
            }
            if (SessionRegistry.FindByCharacter(CharacterId)?.IsInWorld != true) return "not-in-world";

            _ = Task.Run(HeartbeatAsync);
            return null;
        }

        private static async Task<TcpClient> DialAsync()
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, Program.gamePort);
            return tcp;
        }

        /// <summary>The connection server's exchange: the token, the server picked, its ticket.</summary>
        private async Task<string?> TicketAsync(NetworkStream stream, string token)
        {
            var codec = new ConnectionCodec();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, codec.Encode(new ConnectionRequest("1",
                new IdentificationRequest(_login, new TokenIdentification(token, null)))));
            if (await ReplyAsync(stream, codec) is not IdentificationResponse { Result: IdentificationSuccess }) return null;

            int server = DatabaseManager.IsServerJoinable(DatabaseManager.DefaultServerId)
                ? DatabaseManager.DefaultServerId
                : DatabaseManager.GetServers().Select(s => s.Id).FirstOrDefault(DatabaseManager.IsServerJoinable);
            if (server == 0) return null;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, codec.Encode(new ConnectionRequest("2",
                new SelectServerRequest(server))));
            return await ReplyAsync(stream, codec) is SelectServerResponse { Result: SelectServerSuccess selected }
                ? selected.Ticket
                : null;
        }

        private static async Task<ResponseContent?> ReplyAsync(NetworkStream stream, ConnectionCodec codec)
        {
            var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
            if (await Task.WhenAny(read, Task.Delay(Patience)) != read) return null;
            byte[] frame = await read;
            if (frame == null) return null;
            return codec.Decode(frame) is ConnectionResponse response ? response.Content : null;
        }

        // ─── The socket ──────────────────────────────────────────────────────────────────────

        /// <summary>A message of the client's, wrapped as the client wraps it: { f2 { f1 Any, f2 -1 } }.</summary>
        public async Task SendAsync(string op, byte[]? payload = null)
        {
            var stream = _stream;
            if (stream == null || _closed) throw new InvalidOperationException("disconnected");
            var any = Pb.New().Str(1, ConnectionProtocol.UriPrefix + op);
            if (payload != null && payload.Length > 0) any.Bytes(2, payload);
            byte[] frame = Pb.New().Msg(2, Pb.New().Bytes(1, any.Build()).Var(2, -1)).Build();

            await _writing.WaitAsync();
            try { await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame); }
            finally { _writing.Release(); }
        }

        /// <summary>The frames received after <paramref name="since"/>, oldest first.</summary>
        public List<Frame> FramesSince(long since) => _frames.Where(f => f.Id > since).ToList();

        private async Task ReadAsync()
        {
            try
            {
                while (!_closed && _stream != null)
                {
                    byte[] frame = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(_stream);
                    if (frame == null) break;
                    string? op = ConnectionProtocol.ReadOpcode(frame);
                    if (op == null) continue;
                    byte[] payload = ConnectionProtocol.ReadPayload(frame, op) ?? Array.Empty<byte>();
                    _frames.Enqueue(new Frame(Interlocked.Increment(ref _sequence), DateTime.UtcNow, op, payload));
                    while (_frames.Count > FramesKept) _frames.TryDequeue(out _);
                    try { await OnFrameAsync(op, payload); }
                    catch (Exception ex) { Program.LogDebug($"[Headless] Slot {_slot} on {op}: {ex.Message}"); }
                }
            }
            catch (Exception) { }
            finally
            {
                if (!_closed) Console.WriteLine($"[Headless] Slot {_slot}: the server closed the connection.");
                _closed = true;
                _list?.TrySetResult(new List<(long, string)>());
                _selected?.TrySetResult(false);
            }
        }

        /// <summary>Only what a client must answer for the server to go on.</summary>
        private async Task OnFrameAsync(string op, byte[] payload)
        {
            switch (op)
            {
                case Op.Kvi:
                    _list?.TrySetResult(ReadCharacters(payload));
                    break;
                case Op.Kvd:
                    // Its character is still in a fight: it goes on with it.
                    await SendAsync(Op.Kwb);
                    break;
                case Op.Kva:
                    _selected?.TrySetResult(true);
                    break;
                case Op.Kmp:
                    // The next map is a fight's: it is asked for with ijm and kmv, not with jrh.
                    _fightMapComing = Var(payload, 1) != 0;
                    break;
                case Op.Jru:
                    long map = Var(payload, 2);
                    if (_fightMapComing)
                    {
                        await SendAsync(Op.Ijm);
                        await SendAsync(Op.Kmv, Pb.New().Var(1, map).Build());
                    }
                    else
                    {
                        await SendAsync(Op.Kmv, Pb.New().Var(1, map).Build());
                        await SendAsync("jrh", Pb.New().Var(1, map).Build());
                    }
                    _fightMapComing = false;
                    break;
                case Op.Jxh:
                    // "Got it": until a client says so the turn does not start.
                    await SendAsync("jwz", Pb.New().Var(1, 1).Build());
                    break;
                case Op.Jwi:
                    // Every sequence closed is acknowledged; the end of a fight waits for it.
                    await SendAsync(Op.Jti, Pb.New().Var(1, 1).Var(2, Var(payload, 1)).Build());
                    break;
                case Op.Kam:
                    if (AutoReady) _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        await SendAsync(Op.Kaq, Pb.New().Var(1, 1).Build());
                    });
                    break;
            }
        }

        private async Task HeartbeatAsync()
        {
            try
            {
                while (!_closed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                    await SendAsync(Op.Kqo, Pb.New().Var(2, 1).Build());
                }
            }
            catch (Exception) { }
        }

        /// <summary>The kvi's characters: { f1 { f1 { f2 name … }, f2 id } } each.</summary>
        private static List<(long Id, string Name)> ReadCharacters(byte[] kvi)
        {
            var list = new List<(long, string)>();
            foreach (var entry in ProtoMessage.Parse(kvi).Fields.Where(f => f.FieldNumber == 1 && f.WireType == 2))
            {
                var fields = ProtoMessage.Parse(entry.BytesValue).Fields;
                long id = fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
                var details = fields.FirstOrDefault(f => f.FieldNumber == 1 && f.WireType == 2);
                string name = details == null ? "" : ProtoMessage.Parse(details.BytesValue).Fields
                    .Where(f => f.FieldNumber == 2 && f.WireType == 2)
                    .Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue)).FirstOrDefault() ?? "";
                list.Add((id, name));
            }
            return list;
        }

        private static long Var(byte[] payload, int field)
        {
            if (payload.Length == 0) return 0;
            try
            {
                return ProtoMessage.Parse(payload).Fields
                    .FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;
            }
            catch { return 0; }
        }

        private async Task<T?> WithinAsync<T>(Task<T> task)
            => await Task.WhenAny(task, Task.Delay(Patience, _stop.Token)) == task ? await task : default;
    }
}
